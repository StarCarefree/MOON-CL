using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Launcher;

// Deliberately not a record: ToString() must not print signed URLs or cookies.
public sealed class QuarkDownload
{
    public string Url { get; }
    public string FileName { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }

    public QuarkDownload(string url, string fileName, IReadOnlyDictionary<string, string> headers)
    {
        Url = url;
        FileName = fileName;
        Headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class QuarkShareFile
{
    public string PwdId { get; }
    public string Stoken { get; }
    public string Fid { get; }
    public string ShareFidToken { get; }
    public string FileName { get; }
    public long Size { get; }

    public QuarkShareFile(string pwdId, string stoken, string fid, string shareFidToken, string fileName, long size)
    {
        PwdId = pwdId;
        Stoken = stoken;
        Fid = fid;
        ShareFidToken = shareFidToken;
        FileName = fileName;
        Size = size;
    }

    public override string ToString() => FileName;
}

public sealed class QuarkShareEntry
{
    public string Name { get; }
    public string Path { get; }
    public bool Directory { get; }
    public long Size { get; }

    public QuarkShareEntry(string name, string path, bool directory, long size)
    {
        Name = name;
        Path = path;
        Directory = directory;
        Size = size;
    }

    public override string ToString() => Name;
}

public sealed class QuarkShareListing
{
    public const int MaxEntries = 200;
    public string Path { get; }
    public bool Truncated { get; }
    public IReadOnlyList<QuarkShareEntry> Entries { get; }

    public QuarkShareListing(string path, bool truncated, IReadOnlyList<QuarkShareEntry> entries)
    {
        Path = path;
        Truncated = truncated;
        Entries = entries;
    }

    public override string ToString() => Path;
}

/// <summary>Resolves metadata only. The caller must use PackTransfer to download and report CDN errors.</summary>
public class QuarkPackClient(HttpClient http)
{
    private const int MaxJsonBytes = 4 * 1024 * 1024;
    private const string Api = "https://drive-pc.quark.cn/1/clouddrive/";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public async Task<QuarkDownload> ResolveAsync(string serviceUrl, string shareUrl, string sharePath, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            // Match the original spelling, not a URI-normalized path (which could hide traversal).
            var share = Regex.Match(shareUrl ?? "", @"\Ahttps://pan\.quark\.cn/s/([A-Za-z0-9]+)(?:\?pwd=([A-Za-z0-9]+))?\z", RegexOptions.CultureInvariant);
            if (!share.Success) throw new InvalidDataException();
            ValidatePath(sharePath);
            if (!string.IsNullOrEmpty(serviceUrl))
            {
                var baseUri = PackCatalog.HttpUri(serviceUrl);
                if (baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0) throw new InvalidDataException();
                var endpoint = PackCatalog.HttpUri(baseUri.AbsoluteUri.TrimEnd('/') + "/resolve");
                using var result = await RequestAsync(endpoint, new { shareUrl, path = sharePath }, false, timeout.Token).ConfigureAwait(false);
                var root = result.RootElement;
                var headers = DownloadHeaders();
                if (root.TryGetProperty("headers", out var supplied))
                {
                    if (supplied.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                    foreach (var header in supplied.EnumerateObject())
                    {
                        if (!AllowedHeader(header.Name)) continue;
                        var value = header.Value.GetString() ?? throw new InvalidDataException();
                        if (value.Any(char.IsControl)) throw new InvalidDataException();
                        headers[header.Name] = value;
                    }
                }
                return Download(Text(root, "url"), Text(root, "fileName"), headers);
            }

            var located = await LocateCore(shareUrl ?? "", sharePath, timeout.Token).ConfigureAwait(false);
            using var download = await RequestAsync(new Uri(Api + "file/download?pr=ucpro&fr=win"),
                new { fids = new[] { located.Fid }, pwd_id = located.PwdId, stoken = located.Stoken, fids_token = new[] { located.ShareFidToken }, support_error_code = new[] { "41060" } },
                true, timeout.Token).ConfigureAwait(false);
            var data = Data(download);
            if (data.GetArrayLength() != 1) throw new InvalidDataException();
            return Download(Text(data[0], "download_url"), located.FileName, DownloadHeaders());
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException("夸克分享解析已取消。", token);
            throw new TimeoutException("夸克分享解析超时（30 秒）。");
        }
        catch (HttpRequestException ex)
        {
            // No response body, reason phrase, request URI or inner exception may escape.
            throw new HttpRequestException("夸克分享解析 HTTP 请求失败。", null, ex.StatusCode);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("夸克分享解析失败：地址、路径或响应无效。");
        }
    }

    public async Task<QuarkShareFile> LocateAsync(string shareUrl, string sharePath, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            return await LocateCore(shareUrl, sharePath, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException("夸克分享解析已取消。", token);
            throw new TimeoutException("夸克分享解析超时（30 秒）。");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException("夸克分享解析 HTTP 请求失败。", null, ex.StatusCode);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("夸克分享解析失败：地址、路径或响应无效。");
        }
    }

    public async Task<QuarkShareListing> ListAsync(string shareUrl, string? directoryPath, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            return await ListCore(shareUrl, directoryPath, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException("夸克分享解析已取消。", token);
            throw new TimeoutException("夸克分享解析超时（30 秒）。");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException("夸克分享解析 HTTP 请求失败。", null, ex.StatusCode);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("夸克分享解析失败：地址、路径或响应无效。");
        }
    }

    private async Task<QuarkShareListing> ListCore(string shareUrl, string? directoryPath, CancellationToken token)
    {
        var share = Regex.Match(shareUrl ?? "", @"\Ahttps://pan\.quark\.cn/s/([A-Za-z0-9]+)(?:\?pwd=([A-Za-z0-9]+))?\z", RegexOptions.CultureInvariant);
        if (!share.Success) throw new InvalidDataException();
        var parts = ValidateDirectory(directoryPath);
        var pwdId = share.Groups[1].Value;
        using var tokenJson = await RequestAsync(new Uri(Api + "share/sharepage/token?pr=ucpro&fr=win"),
            new { pwd_id = pwdId, passcode = share.Groups[2].Value, support_visit_limit_private_share = true }, true, token).ConfigureAwait(false);
        var stoken = Text(Data(tokenJson), "stoken");
        var parent = "0";
        for (var depth = 0; depth < parts.Length; depth++)
        {
            var match = await FindNamedAsync(pwdId, stoken, parent, parts[depth], token).ConfigureAwait(false);
            if (!match.TryGetProperty("dir", out var dirNode) || dirNode.ValueKind != JsonValueKind.True) throw new InvalidDataException();
            parent = Text(match, "fid");
        }
        var entries = new List<QuarkShareEntry>();
        var truncated = false;
        var scanned = 0;
        for (var page = 1; ; page++)
        {
            var query = "share/sharepage/detail?pr=ucpro&fr=win&pwd_id=" + Uri.EscapeDataString(pwdId) +
                "&stoken=" + Uri.EscapeDataString(stoken) + "&pdir_fid=" + Uri.EscapeDataString(parent) +
                "&_page=" + page + "&_size=100&_sort=file_type:asc,file_name:asc";
            using var listing = await RequestAsync(new Uri(Api + query), null, true, token).ConfigureAwait(false);
            var list = Data(listing).GetProperty("list");
            var count = list.GetArrayLength();
            scanned += count;
            if (count > 100 || scanned > 10000) throw new InvalidDataException();
            foreach (var item in list.EnumerateArray())
            {
                if (entries.Count >= QuarkShareListing.MaxEntries) { truncated = true; break; }
                if (!TryText(item, "file_name", out var name) || !SafeName(name)) continue;
                if (!item.TryGetProperty("dir", out var childDir) || childDir.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                var directory = childDir.GetBoolean();
                long size = 0;
                if (!directory && item.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed) && parsed > 0) size = parsed;
                var relative = parts.Length == 0 ? name : string.Join("/", parts) + "/" + name;
                entries.Add(new QuarkShareEntry(name, relative, directory, size));
            }
            if (entries.Count >= QuarkShareListing.MaxEntries)
            {
                truncated = truncated || count == 100;
                break;
            }
            if (count < 100) break;
            if (page >= 100) { truncated = true; break; }
        }
        return new QuarkShareListing(parts.Length == 0 ? "" : string.Join("/", parts), truncated, entries);
    }

    private async Task<JsonElement> FindNamedAsync(string pwdId, string stoken, string parent, string name, CancellationToken token)
    {
        JsonElement? match = null;
        var entries = 0;
        for (var page = 1; ; page++)
        {
            var query = "share/sharepage/detail?pr=ucpro&fr=win&pwd_id=" + Uri.EscapeDataString(pwdId) +
                "&stoken=" + Uri.EscapeDataString(stoken) + "&pdir_fid=" + Uri.EscapeDataString(parent) +
                "&_page=" + page + "&_size=100&_sort=file_type:asc,file_name:asc";
            using var listing = await RequestAsync(new Uri(Api + query), null, true, token).ConfigureAwait(false);
            var list = Data(listing).GetProperty("list");
            var count = list.GetArrayLength();
            entries += count;
            if (count > 100 || entries > 10000) throw new InvalidDataException();
            foreach (var item in list.EnumerateArray())
            {
                if (!string.Equals(Text(item, "file_name"), name, StringComparison.Ordinal)) continue;
                if (match.HasValue) throw new InvalidDataException();
                match = item.Clone();
            }
            if (count < 100) break;
            if (page >= 100 || entries >= 10000) throw new InvalidDataException();
        }
        return match ?? throw new InvalidDataException();
    }

    private static string[] ValidateDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        if (path.Length > 4096) throw new InvalidDataException();
        var parts = path.Split('/');
        if (parts.Length > 99 || parts.Any(part => !SafeName(part))) throw new InvalidDataException();
        return parts;
    }

    private static bool TryText(JsonElement element, string key, out string value)
    {
        value = "";
        if (!element.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.String) return false;
        value = node.GetString() ?? "";
        return value.Length > 0;
    }

    private async Task<QuarkShareFile> LocateCore(string shareUrl, string sharePath, CancellationToken token)
    {
        var share = Regex.Match(shareUrl ?? "", @"\Ahttps://pan\.quark\.cn/s/([A-Za-z0-9]+)(?:\?pwd=([A-Za-z0-9]+))?\z", RegexOptions.CultureInvariant);
        if (!share.Success) throw new InvalidDataException();
        var parts = ValidatePath(sharePath);
        var pwdId = share.Groups[1].Value;
        using var tokenJson = await RequestAsync(new Uri(Api + "share/sharepage/token?pr=ucpro&fr=win"),
            new { pwd_id = pwdId, passcode = share.Groups[2].Value, support_visit_limit_private_share = true }, true, token).ConfigureAwait(false);
        var stoken = Text(Data(tokenJson), "stoken");
        var parent = "0";
        JsonElement selected = default;
        var entries = 0;
        for (var depth = 0; depth < parts.Length; depth++)
        {
            JsonElement? match = null;
            for (var page = 1; ; page++)
            {
                var query = "share/sharepage/detail?pr=ucpro&fr=win&pwd_id=" + Uri.EscapeDataString(pwdId) +
                    "&stoken=" + Uri.EscapeDataString(stoken) + "&pdir_fid=" + Uri.EscapeDataString(parent) +
                    "&_page=" + page + "&_size=100&_sort=file_type:asc,file_name:asc";
                using var listing = await RequestAsync(new Uri(Api + query), null, true, token).ConfigureAwait(false);
                var list = Data(listing).GetProperty("list");
                var count = list.GetArrayLength();
                entries += count;
                if (count > 100 || entries > 10000) throw new InvalidDataException();
                foreach (var item in list.EnumerateArray())
                {
                    if (!string.Equals(Text(item, "file_name"), parts[depth], StringComparison.Ordinal)) continue;
                    if (match.HasValue) throw new InvalidDataException();
                    match = item.Clone();
                }
                if (count < 100) break;
                if (page >= 100 || entries >= 10000) throw new InvalidDataException();
            }
            selected = match ?? throw new InvalidDataException();
            var directory = selected.GetProperty("dir").GetBoolean();
            if (directory != (depth < parts.Length - 1)) throw new InvalidDataException();
            parent = Text(selected, "fid");
        }
        long size = 0;
        if (selected.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed) && parsed > 0) size = parsed;
        return new QuarkShareFile(pwdId, stoken, parent, Text(selected, "share_fid_token"), parts[^1], size);
    }

    private static string[] ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096) throw new InvalidDataException();
        var parts = path.Split('/');
        if (parts.Length > 100 || parts.Any(p => !SafeName(p))) throw new InvalidDataException();
        ValidateFileName(parts[^1]);
        return parts;
    }

    private static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") &&
        !name.EndsWith('.') && !name.EndsWith(' ') && !name.Any(char.IsControl) && name.IndexOfAny("/\\:<>\"|?*".ToCharArray()) < 0;

    private static void ValidateFileName(string name)
    {
        if (!SafeName(name) || !(name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException();
    }

    private static QuarkDownload Download(string url, string name, Dictionary<string, string> headers)
    {
        ValidateFileName(name);
        var uri = PackCatalog.HttpUri(url);
        if (uri.Fragment.Length != 0) throw new InvalidDataException();
        return new QuarkDownload(uri.AbsoluteUri, name, headers);
    }

    private static bool AllowedHeader(string name) => name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Referer", StringComparison.OrdinalIgnoreCase) || name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> DownloadHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = UserAgent,
        ["Referer"] = "https://pan.quark.cn/"
    };

    private static string Text(JsonElement element, string key)
    {
        var value = element.GetProperty(key).GetString();
        return string.IsNullOrEmpty(value) ? throw new InvalidDataException() : value;
    }

    private static JsonElement Data(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0) throw new InvalidDataException();
        if (root.TryGetProperty("status", out var status) && status.GetInt32() != 200) throw new InvalidDataException();
        return root.GetProperty("data");
    }

    private async Task<JsonDocument> RequestAsync(Uri uri, object? body, bool official, CancellationToken token)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Referrer = new Uri("https://pan.quark.cn/");
        if (official) request.Headers.TryAddWithoutValidation("Origin", "https://pan.quark.cn");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("夸克解析 HTTP 请求失败。", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxJsonBytes) throw new InvalidDataException();
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32768];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxJsonBytes) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        return JsonDocument.Parse(output.ToArray());
    }
}
