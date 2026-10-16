using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>User-authorized Quark open-platform session. ToString never includes tokens.</summary>
public sealed class QuarkOpenSession
{
    internal string AccessToken { get; }
    internal string RefreshToken { get; }
    public string DeviceId { get; }
    public long AccessExpiresAt { get; }
    public long RefreshExpiresAt { get; }

    public QuarkOpenSession(string accessToken, string refreshToken, string deviceId, long accessExpiresAt, long refreshExpiresAt)
    {
        AccessToken = accessToken ?? "";
        RefreshToken = refreshToken ?? "";
        DeviceId = deviceId ?? "";
        AccessExpiresAt = accessExpiresAt;
        RefreshExpiresAt = refreshExpiresAt;
    }

    public bool HasRefreshableToken => AccessToken.Length > 0 && RefreshToken.Length > 0;
    public bool IsReusable => HasRefreshableToken &&
        (RefreshExpiresAt == 0 || RefreshExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public override string ToString() => "QuarkOpenSession";

    public bool Matches(string accessToken, string refreshToken)
    {
        var access = Encoding.UTF8.GetBytes(AccessToken);
        var refresh = Encoding.UTF8.GetBytes(RefreshToken);
        var expectedAccess = Encoding.UTF8.GetBytes(accessToken ?? "");
        var expectedRefresh = Encoding.UTF8.GetBytes(refreshToken ?? "");
        var sameAccess = access.Length == expectedAccess.Length && CryptographicOperations.FixedTimeEquals(access, expectedAccess);
        var sameRefresh = refresh.Length == expectedRefresh.Length && CryptographicOperations.FixedTimeEquals(refresh, expectedRefresh);
        return sameAccess && sameRefresh;
    }
}

/// <summary>Authorize-page handle. ToString never includes the page code.</summary>
public sealed class QuarkAuthorizeStart
{
    public string PageUrl { get; }
    internal string PageCode { get; }
    public string DeviceId { get; }

    public QuarkAuthorizeStart(string pageUrl, string pageCode, string deviceId)
    {
        PageUrl = pageUrl;
        PageCode = pageCode;
        DeviceId = deviceId ?? "";
    }

    public override string ToString() => "QuarkAuthorizeStart";
}

public sealed class QuarkOwnedDownload
{
    public QuarkDownload Download { get; }
    public QuarkOpenSession Session { get; }
    public bool SavedToDrive { get; }
    /// <summary>Exact archive size from the selected share file; zero when not supplied.</summary>
    public long ExpectedSize { get; }

    public QuarkOwnedDownload(QuarkDownload download, QuarkOpenSession session, bool savedToDrive = true, long expectedSize = 0)
    {
        Download = download;
        Session = session;
        SavedToDrive = savedToDrive;
        ExpectedSize = expectedSize;
    }

    public override string ToString() => Download.FileName;
}

public sealed class QuarkOpenException : InvalidOperationException
{
    public bool SavedToDrive { get; }
    public int ErrorCode { get; }

    public QuarkOpenException(string message, bool savedToDrive = false, int errorCode = 0) : base(message)
    {
        SavedToDrive = savedToDrive;
        ErrorCode = errorCode;
    }
}

/// <summary>
/// User OAuth against the published Quark open platform, then save one owned file and download it.
/// Does not read browser cookies, does not send save_all, and does not use fr=pc.
/// </summary>
public sealed class QuarkOpenClient
{
    public const string Gateway = "https://open-api-drive.quark.cn";
    public const string ClientId = "third_party_agent";
    public const long MaxSaveBytes = 512L * 1024 * 1024;
    private const string SignKey = "cf134812e2de4032bd1cb7c3727e84b3";
    private const string FolderName = "MOONCL";
    private const int MaxJsonBytes = 1024 * 1024;
    private const string BatchDownloadPath = "/open/v1/file/multi_get_download_url";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly QuarkPackClient _packs;
    private QuarkOpenSession? _session;

    /// <summary>Persist rotated credentials immediately, even if a later save/download fails.</summary>
    public event Action<QuarkOpenSession>? SessionRefreshed;

    public QuarkOpenClient(HttpClient http) : this(http, new QuarkPackClient(http)) { }

    public QuarkOpenClient(HttpClient http, QuarkPackClient packs)
    {
        _http = http;
        _packs = packs;
    }

    public static string Sign(string method, string path, string timestamp) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + "&" + path + "&" + timestamp + "&" + SignKey))).ToLowerInvariant();

    public async Task<QuarkAuthorizeStart> BeginAuthorizeAsync(CancellationToken token)
    {
        try
        {
            using var document = await CallAsync(HttpMethod.Post, "/agent/v1/get_authorize_page_url", null,
                new
                {
                    client_device_id = "mooncl-" + Guid.NewGuid().ToString("N"),
                    device_name = "MOONCL",
                    agent_id = "mooncl",
                    client_id = ClientId,
                    work_dir = FolderName
                }, token, null, false).ConfigureAwait(false);
            var data = Data(document);
            var page = Text(data, "authorize_page_url");
            if (!QuarkClientLaunch.IsAuthorizePage(page) || page.Length > 2048)
                throw new QuarkOpenException("夸克授权页地址无效。未保存令牌。");
            var code = Text(data, "page_code");
            var device = Text(data, "device_id");
            return new QuarkAuthorizeStart(page, code, device);
        }
        catch (Exception ex) when (ex is not QuarkOpenException and not OperationCanceledException and not TimeoutException)
        {
            throw new QuarkOpenException("无法请求夸克授权页。未保存令牌。");
        }
    }

    public async Task<QuarkOpenSession?> PollAsync(QuarkAuthorizeStart start, CancellationToken token)
    {
        if (start is null || string.IsNullOrEmpty(start.PageCode)) throw new QuarkOpenException("夸克授权尚未开始。");
        try
        {
            using var polled = await CallAsync(HttpMethod.Get, "/agent/v1/oauth/get_aac_by_pagecode", null, null, token,
                new Dictionary<string, string> { ["page_code"] = start.PageCode }, false).ConfigureAwait(false);
            var data = Data(polled);
            if (!data.TryGetProperty("agent_auth_code", out var codeNode)) return null;
            var code = codeNode.GetString();
            if (string.IsNullOrEmpty(code)) return null;
            using var exchanged = await CallAsync(HttpMethod.Get, "/agent/v1/oauth/agent_auth_code", null, null, token,
                new Dictionary<string, string> { ["agent_auth_code"] = code }, false).ConfigureAwait(false);
            return SessionFrom(Data(exchanged), start.DeviceId);
        }
        catch (QuarkOpenException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { throw; }
        catch { throw new QuarkOpenException("夸克授权结果无效。未保存令牌。"); }
    }

    public async Task<QuarkOwnedDownload> ResolveOwnedAsync(QuarkOpenSession session, string shareUrl, string sharePath, CancellationToken token)
    {
        if (session is null || !session.HasRefreshableToken) throw new QuarkOpenException("尚未授权夸克网盘，没有转存或下载。");
        var savedToDrive = false;
        try
        {
            _session = await EnsureFreshAsync(session, token).ConfigureAwait(false);
            var file = await _packs.LocateAsync(shareUrl, sharePath, token).ConfigureAwait(false);
            if (file.Size <= 0 || file.Size > MaxSaveBytes)
                throw new QuarkOpenException("无法确认这是单个可转存的整合包，已停止。没有转存整个分享。");
            var folder = await CreateFolderAsync(token).ConfigureAwait(false);
            JsonElement saved;
            try
            {
                saved = await SaveOneAsync(file, folder, token).ConfigureAwait(false);
            }
            catch (QuarkOpenException ex) when (ex.ErrorCode == 41017)
            {
                // Quark rejects copying your own share; its selected FID is already owned.
                var original = await DownloadForAsync(file.Fid, file, token, savedToDrive: false).ConfigureAwait(false);
                return new QuarkOwnedDownload(original, _session, savedToDrive: false, expectedSize: file.Size);
            }
            var ownedFid = await OwnedFidAsync(saved, file, folder, token).ConfigureAwait(false);
            savedToDrive = true;
            var download = await DownloadForAsync(ownedFid, file, token).ConfigureAwait(false);
            return new QuarkOwnedDownload(download, _session, expectedSize: file.Size);
        }
        catch (QuarkOpenException ex) when (savedToDrive && !ex.SavedToDrive)
        {
            throw new QuarkOpenException(ex.Message, savedToDrive: true, errorCode: ex.ErrorCode);
        }
        catch (QuarkOpenException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { throw; }
        catch { throw new QuarkOpenException("夸克网盘下载失败。没有转存整个分享。"); }
    }

    private async Task<QuarkOpenSession> EnsureFreshAsync(QuarkOpenSession session, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (session.RefreshExpiresAt > 0 && now >= session.RefreshExpiresAt)
            throw new QuarkOpenException("夸克授权已失效，请重新授权。");
        if (session.AccessExpiresAt == 0 || session.AccessExpiresAt - 60_000 > now) return session;
        return await RefreshAsync(session, token).ConfigureAwait(false);
    }

    private async Task<QuarkOpenSession> RefreshAsync(QuarkOpenSession session, CancellationToken token)
    {
        using var document = await CallAsync(HttpMethod.Post, "/agent/v1/oauth/access_token/rotate", null,
            new { refresh_token = session.RefreshToken, device_id = session.DeviceId }, token, null, false).ConfigureAwait(false);
        var refreshed = SessionFrom(Data(document), session.DeviceId);
        _session = refreshed;
        SessionRefreshed?.Invoke(refreshed);
        return refreshed;
    }

    private async Task<string> CreateFolderAsync(CancellationToken token)
    {
        using var document = await CallAsync(HttpMethod.Post, "/open/v1/dir", _session,
            new { dir_path = FolderName, pdir_fid = "0" }, token, null, true).ConfigureAwait(false);
        return Text(Data(document), "fid");
    }

    private async Task<JsonElement> SaveOneAsync(QuarkShareFile file, string folderFid, CancellationToken token)
    {
        if (string.IsNullOrEmpty(file.Fid) || string.IsNullOrEmpty(file.ShareFidToken))
            throw new QuarkOpenException("分享文件身份无效，已停止转存。");
        using var document = await CallAsync(HttpMethod.Post, "/open/v1/share/saveas", _session,
            new
            {
                pwd_id = file.PwdId,
                stoken = file.Stoken,
                to_pdir_fid = folderFid,
                fid_list = new[] { file.Fid },
                fid_token_list = new[] { file.ShareFidToken }
            }, token, null, true).ConfigureAwait(false);
        return Data(document).Clone();
    }

    private async Task<string> OwnedFidAsync(JsonElement saved, QuarkShareFile file, string folderFid, CancellationToken token)
    {
        var current = saved;
        // The service reports the actual destination; it need not be the requested folder.
        // Keep it across task responses that contain only status/task_id.
        folderFid = SavedFolder(current, folderFid);
        // saveas may return only { task_id, task_sync }, not a completed task result.
        var hasTask = current.TryGetProperty("task_id", out var taskNode)
            && taskNode.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(taskNode.GetString());
        var status = Int(current, "status", hasTask ? 0 : 2);
        if (status is 0 or 1)
        {
            var taskId = Text(current, "task_id");
            for (var attempt = 0; attempt < 24; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                using var document = await CallAsync(HttpMethod.Post, "/open/v1/task/query", _session,
                    new { task_id = taskId }, token, null, true).ConfigureAwait(false);
                current = Data(document).Clone();
                folderFid = SavedFolder(current, folderFid);
                status = Int(current, "status", -1);
                if (status is 0 or 1) continue;
                break;
            }
        }
        if (status != 2) throw new QuarkOpenException("夸克转存未完成，没有下载文件。", savedToDrive: false);
        var fid = FirstFid(current);
        if (!string.IsNullOrEmpty(fid)) return fid;
        return await FindSavedAsync(file, folderFid, token).ConfigureAwait(false);
    }

    private static string SavedFolder(JsonElement data, string fallback)
    {
        if (data.TryGetProperty("save_as", out var save) && save.ValueKind == JsonValueKind.Object
            && save.TryGetProperty("to_pdir_fid", out var folder) && folder.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(folder.GetString()))
            return folder.GetString()!; // "0" is a valid destination (root).
        return fallback;
    }

    private async Task<string> FindSavedAsync(QuarkShareFile file, string folderFid, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            var found = await MatchSavedFolderAsync(file, folderFid, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(found)) return found;
        }
        throw new QuarkOpenException("已转存，但没有找到目标文件，已停止下载。", savedToDrive: true);
    }

    private async Task<string?> MatchSavedFolderAsync(QuarkShareFile file, string folderFid, CancellationToken token)
    {
        JsonElement? cursor = null;
        string? match = null;
        for (var page = 0; page < 5; page++)
        {
            object body = cursor is null
                ? new { parent_fid = folderFid, sort = "updated_at:desc", size = 100 }
                : new { parent_fid = folderFid, sort = "updated_at:desc", size = 100, query_cursor = cursor.Value };
            using var document = await CallAsync(HttpMethod.Post, "/open/v1/file/list", _session, body, token, null, true).ConfigureAwait(false);
            var data = Data(document);
            if (data.TryGetProperty("file_list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (!SameSavedFile(item, file, folderFid)) continue;
                    var fid = item.TryGetProperty("fid", out var fidNode) && fidNode.ValueKind == JsonValueKind.String ? fidNode.GetString() : null;
                    if (string.IsNullOrEmpty(fid) || (match is not null && !string.Equals(match, fid, StringComparison.Ordinal)))
                        throw new QuarkOpenException("转存结果不唯一，已停止下载。", savedToDrive: true);
                    match = fid;
                }
            }
            if (!data.TryGetProperty("last_page", out var last) || last.ValueKind != JsonValueKind.False
                || !data.TryGetProperty("next_query_cursor", out var next) || next.ValueKind != JsonValueKind.Object)
                return match;
            cursor = next.Clone();
        }
        return match;
    }

    private static bool SameSavedFile(JsonElement item, QuarkShareFile file, string folderFid)
    {
        if (IsDirectory(item)) return false;
        var name = item.TryGetProperty("filename", out var filename) && filename.ValueKind == JsonValueKind.String
            ? filename.GetString()
            : item.TryGetProperty("file_name", out var fileName) && fileName.ValueKind == JsonValueKind.String
                ? fileName.GetString()
                : null;
        if (!string.Equals(name, file.FileName, StringComparison.Ordinal)) return false;
        if (item.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var size) && size != file.Size) return false;
        return !ParentMismatch(item, "parent_fid", folderFid) && !ParentMismatch(item, "pdir_fid", folderFid);
    }

    private static bool ParentMismatch(JsonElement item, string key, string folderFid)
    {
        if (!item.TryGetProperty(key, out var parent) || parent.ValueKind != JsonValueKind.String) return false;
        var value = parent.GetString();
        // List responses can contain randomized opaque IDs (~...|...), unlike /dir's raw FID.
        // The list request already scopes the results to folderFid; encoded strings are not comparable.
        return !string.IsNullOrEmpty(value) && !IsOpaqueFid(value) && !IsOpaqueFid(folderFid)
            && !string.Equals(value, folderFid, StringComparison.Ordinal);
    }

    private static bool IsOpaqueFid(string fid) => fid.StartsWith('~') && fid.IndexOf('|') > 1;

    private static bool IsDirectory(JsonElement item)
    {
        if (item.TryGetProperty("dir", out var dir) && dir.ValueKind == JsonValueKind.True) return true;
        if (!item.TryGetProperty("file_type", out var type)) return false;
        if (type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number)) return number == 0;
        return type.ValueKind == JsonValueKind.String && type.GetString() == "0";
    }

    private async Task<QuarkDownload> DownloadForAsync(string fid, QuarkShareFile file, CancellationToken token, bool savedToDrive = true)
    {
        if (string.IsNullOrWhiteSpace(fid))
            throw new QuarkOpenException("网盘文件身份无效，已停止下载。", savedToDrive);
        using var document = await CallAsync(HttpMethod.Post, BatchDownloadPath, _session,
            new { fids = new[] { fid } }, token, null, true).ConfigureAwait(false);
        var results = Data(document);
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != 1
            || results[0].ValueKind != JsonValueKind.Object)
            throw new QuarkOpenException("夸克下载结果不唯一或无效，已停止下载。", savedToDrive);
        var data = results[0];
        if (IsDirectory(data) || !data.TryGetProperty("fid", out var fidNode) || fidNode.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(fidNode.GetString()))
            throw new QuarkOpenException("网盘文件身份无效，已停止下载。", savedToDrive);
        var returnedFid = fidNode.GetString()!;
        // Opaque FIDs may be re-encoded on each response; the one-FID request and
        // exact name/size checks bind that result. Compare only ordinary FIDs.
        if (!IsOpaqueFid(fid) && !IsOpaqueFid(returnedFid) && !string.Equals(fid, returnedFid, StringComparison.Ordinal))
            throw new QuarkOpenException("网盘文件身份与请求不一致，已停止下载。", savedToDrive);
        if (!data.TryGetProperty("size", out var sizeNode) || sizeNode.ValueKind != JsonValueKind.Number
            || !sizeNode.TryGetInt64(out var size) || size != file.Size)
            throw new QuarkOpenException("网盘文件大小与分享文件不一致，已停止下载。", savedToDrive);
        if (!data.TryGetProperty("file_name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String
            || !string.Equals(nameNode.GetString(), file.FileName, StringComparison.Ordinal))
            throw new QuarkOpenException("网盘文件名与分享文件不一致，已停止下载。", savedToDrive);
        if (!data.TryGetProperty("download_url", out var urlNode) || urlNode.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(urlNode.GetString(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new QuarkOpenException("夸克下载地址无效。", savedToDrive);
        var session = _session ?? throw new QuarkOpenException("夸克授权已丢失。", savedToDrive);
        if (session.AccessToken.Length == 0 || session.AccessToken.IndexOfAny(";,\r\n ".ToCharArray()) >= 0)
            throw new QuarkOpenException("夸克授权无效，请重新授权。", savedToDrive);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = "https://pan.quark.cn/",
            ["Cookie"] = "x_pan_client_id=" + ClientId + "; x_pan_access_token=" + session.AccessToken
        };
        return new QuarkDownload(uri.AbsoluteUri, file.FileName, headers);
    }

    private async Task<JsonDocument> CallAsync(HttpMethod method, string path, QuarkOpenSession? session, object? body,
        CancellationToken token, IReadOnlyDictionary<string, string>? extraQuery, bool allowRefresh)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            var tm = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var query = new List<string> { "req_id=" + Uri.EscapeDataString(Guid.NewGuid().ToString()) };
            if (session is not null)
            {
                query.Add("access_token=" + Uri.EscapeDataString(session.AccessToken));
                if (session.DeviceId.Length > 0 && path != BatchDownloadPath)
                    query.Add("device_id=" + Uri.EscapeDataString(session.DeviceId));
            }
            if (extraQuery is not null)
                foreach (var pair in extraQuery) query.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
            using var request = new HttpRequestMessage(method, Gateway + path + "?" + string.Join("&", query));
            request.Headers.TryAddWithoutValidation("x-pan-client-id", ClientId);
            request.Headers.TryAddWithoutValidation("x-pan-tm", tm);
            request.Headers.TryAddWithoutValidation("x-pan-token", Sign(method.Method, path, tm));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxJsonBytes) throw new QuarkOpenException("夸克开放平台响应无效。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[32768];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaxJsonBytes) throw new QuarkOpenException("夸克开放平台响应无效。");
                output.Write(buffer, 0, read);
            }
            JsonDocument document;
            try { document = JsonDocument.Parse(output.ToArray()); }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                throw new QuarkOpenException("夸克开放平台 HTTP 请求失败（" + (int)response.StatusCode + "）。");
            }
            if (allowRefresh && session is not null && AuthExpired(document))
            {
                document.Dispose();
                _session = await RefreshAsync(session, token).ConfigureAwait(false);
                return await CallAsync(method, path, _session, body, token, extraQuery, false).ConfigureAwait(false);
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = BusinessCode(document.RootElement);
                var error = code == 0 ? null : BusinessError(document.RootElement, code);
                document.Dispose();
                if (error is not null) throw error;
                throw new QuarkOpenException("夸克开放平台 HTTP 请求失败（" + (int)response.StatusCode + "）。");
            }
            return document;
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException("夸克网盘操作已取消。", token);
            throw new TimeoutException("夸克开放平台请求超时。");
        }
        catch (QuarkOpenException) { throw; }
        catch (TimeoutException) { throw; }
        catch { throw new QuarkOpenException("夸克开放平台请求失败。"); }
    }

    private static bool AuthExpired(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("errno", out var errno) || !errno.TryGetInt32(out var code)) return false;
        return code is 11001 or 31001;
    }

    private static int BusinessCode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return 0;
        var errno = Int(root, "errno", 0);
        return errno != 0 ? errno : Int(root, "status", 0);
    }

    private static QuarkOpenException BusinessError(JsonElement root, int code)
    {
        var message = Map(code);
        // Extract only the documented numeric limit, never echo server error_info or tokens.
        if (code == 23018 && root.TryGetProperty("error_info", out var info) && info.ValueKind == JsonValueKind.String)
        {
            var match = Regex.Match(info.GetString() ?? "", @"\Adownload file size limit\[([0-9]{1,18})\]\z", RegexOptions.CultureInvariant);
            if (match.Success && long.TryParse(match.Groups[1].Value, out var limit) && limit > 0)
            {
                var size = limit % (1024 * 1024) == 0 ? (limit / (1024 * 1024)) + " MiB" : limit + " 字节";
                message = "夸克开放平台限制单文件下载大小为 " + size + "，此文件超出限制。请在夸克客户端手动下载后导入。";
            }
        }
        return new QuarkOpenException(message, errorCode: code);
    }

    private static JsonElement Data(JsonDocument document)
    {
        var root = document.RootElement;
        var code = BusinessCode(root);
        if (code != 0) throw BusinessError(root, code);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new QuarkOpenException("夸克开放平台响应无效。");
        return data;
    }

    private static string Map(int code) => code switch
    {
        10001 => "夸克开放平台拒绝了请求签名。",
        11001 or 31001 => "夸克授权已失效，请重新授权。",
        23018 => "夸克开放平台限制了此文件的下载大小。请在夸克客户端手动下载后导入。",
        32003 or 32004 => "夸克网盘空间不足，未下载文件。",
        41004 => "夸克分享文件列表为空。",
        41007 => "夸克分享不存在或已失效。",
        41008 => "夸克分享令牌无效，未转存整个分享。",
        41017 => "此分享属于当前账号，无需再次转存。",
        _ => "夸克开放平台拒绝了请求（" + code + "）。"
    };

    private static QuarkOpenSession SessionFrom(JsonElement data, string fallbackDevice)
    {
        var access = Text(data, "access_token");
        var refresh = Text(data, "refresh_token");
        var device = data.TryGetProperty("device_id", out var deviceNode) ? deviceNode.GetString() : fallbackDevice;
        if (string.IsNullOrEmpty(device)) device = fallbackDevice;
        if (string.IsNullOrEmpty(device)) throw new QuarkOpenException("夸克授权结果无效。未保存令牌。");
        return new QuarkOpenSession(access, refresh, device, Expiry(data, "access_token_expires_at", "access_token_expired_at"),
            Expiry(data, "refresh_token_expires_at", "refresh_token_expired_at"));
    }

    private static long Expiry(JsonElement data, string primary, string alternate)
    {
        if (data.TryGetProperty(primary, out var first) && first.TryGetInt64(out var value)) return value;
        if (data.TryGetProperty(alternate, out var second) && second.TryGetInt64(out value)) return value;
        return 0;
    }

    private static string? FirstFid(JsonElement data)
    {
        if (data.TryGetProperty("save_as", out var save))
        {
            var nested = FirstFid(save);
            if (!string.IsNullOrEmpty(nested)) return nested;
        }
        foreach (var name in new[] { "save_as_top_fids", "fid_list" })
        {
            if (!data.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != 1) continue;
            if (list[0].ValueKind == JsonValueKind.String) return list[0].GetString();
            if (list[0].ValueKind == JsonValueKind.Object && list[0].TryGetProperty("fid", out var fid)) return fid.GetString();
        }
        return data.TryGetProperty("fid", out var direct) && direct.ValueKind == JsonValueKind.String && direct.GetString() != "0"
            ? direct.GetString() : null;
    }

    private static int Int(JsonElement data, string name, int fallback) =>
        data.TryGetProperty(name, out var node) && node.TryGetInt32(out var value) ? value : fallback;

    private static string Text(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var node)) throw new QuarkOpenException("夸克开放平台响应无效。");
        var value = node.GetString();
        return string.IsNullOrEmpty(value) ? throw new QuarkOpenException("夸克开放平台响应无效。") : value;
    }
}

public static class QuarkOpenSessionFile
{
    private sealed class Stored
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public long AccessExpiresAt { get; set; }
        public long RefreshExpiresAt { get; set; }
    }

    public static QuarkOpenSession? TryLoad(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored is null || stored.AccessToken.Length == 0 || stored.RefreshToken.Length == 0) return null;
            return new QuarkOpenSession(stored.AccessToken, stored.RefreshToken, stored.DeviceId, stored.AccessExpiresAt, stored.RefreshExpiresAt);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static void Save(string path, QuarkOpenSession session)
    {
        if (session is null || !session.HasRefreshableToken) throw new QuarkOpenException("没有可保存的夸克授权。");
        var protectedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new Stored
        {
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            DeviceId = session.DeviceId,
            AccessExpiresAt = session.AccessExpiresAt,
            RefreshExpiresAt = session.RefreshExpiresAt
        }), null, DataProtectionScope.CurrentUser);
        var directory = Path.GetDirectoryName(path) ?? throw new QuarkOpenException("无法保存夸克授权。");
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
