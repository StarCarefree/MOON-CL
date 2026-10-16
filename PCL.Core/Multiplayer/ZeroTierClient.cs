using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Multiplayer;

public sealed record ZeroTierNetworkInfo(string Id, string Name, string Status, string[] Addresses);

/// <summary>Only the official local ZeroTier One service. Auth never enters the room descriptor or a URL.</summary>
public sealed class ZeroTierClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string> _token;
    public ZeroTierClient(string token = "", HttpClient? http = null, Func<string>? tokenLoader = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4) };
        _token = tokenLoader ?? (() => string.IsNullOrWhiteSpace(token) ? LoadLocalToken() : token.Trim());
    }
    private static string NetworkId(string id)
    {
        if (!Regex.IsMatch(id ?? "", "^[0-9a-fA-F]{16}$")) throw new ArgumentException("ZeroTier Network ID 无效。");
        return id!.ToLowerInvariant();
    }
    public async Task<ZeroTierNetworkInfo?> GetNetworkAsync(string id, CancellationToken ct)
    {
        id = NetworkId(id);
        using var response = await SendAsync(HttpMethod.Get, id, null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "REQUESTING_CONFIGURATION" : "REQUESTING_CONFIGURATION";
        var addresses = root.TryGetProperty("assignedAddresses", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(a => (a.GetString() ?? "").Split('/')[0])
                .Where(a => IPAddress.TryParse(a, out _)).Take(16).ToArray() : [];
        return new ZeroTierNetworkInfo(id, name, status, addresses);
    }
    public async Task<ZeroTierNetworkLease> JoinAsync(string id, CancellationToken ct)
    {
        id = NetworkId(id);
        var existing = await GetNetworkAsync(id, ct).ConfigureAwait(false);
        var created = existing is null;
        try
        {
            if (created)
            {
                // Do not install a default route, global routes or DNS from a room invitation.
                using var response = await SendAsync(HttpMethod.Post, id,
                    new { allowManaged = true, allowGlobal = false, allowDefault = false, allowDNS = false }, ct).ConfigureAwait(false);
                EnsureSuccess(response);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                var info = await GetNetworkAsync(id, timeout.Token).ConfigureAwait(false);
                if (info is { Status: "OK", Addresses.Length: > 0 }) return new ZeroTierNetworkLease(this, info, created);
                if (info?.Status == "ACCESS_DENIED") throw new IOException("ZeroTier 已申请加入，但尚未获网络控制器授权。请授权此设备后重试。");
                if (info?.Status is "NOT_FOUND" or "PORT_ERROR") throw new IOException("ZeroTier 网络不存在或虚拟端口异常，请检查本机服务及 Network ID。");
                await Task.Delay(600, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            if (created) await LeaveBestEffortAsync(id).ConfigureAwait(false);
            throw;
        }
    }
    internal async Task LeaveBestEffortAsync(string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { using var response = await SendAsync(HttpMethod.Delete, id, null, timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException) { }
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string id, object? body, CancellationToken ct)
    {
        var token = _token();
        if (token.Length is < 1 or > 1024 || token.Any(char.IsControl)) throw new IOException("ZeroTier 本机令牌不可用，请提供本机 API Token 或检查文件权限。");
        using var request = new HttpRequestMessage(method, new Uri("http://127.0.0.1:9993/network/" + id));
        request.Headers.Add("X-ZT1-Auth", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            try { await response.Content.LoadIntoBufferAsync(64 * 1024, ct).ConfigureAwait(false); return response; }
            catch { response.Dispose(); throw; }
        }
        catch (HttpRequestException) { throw new IOException("无法连接 ZeroTier One 本机服务，请先安装并启动它。"); }
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new IOException("ZeroTier 本机 API 令牌错误或权限不足；这不是房间密码。");
        throw new IOException("ZeroTier 本机服务拒绝了操作，请检查服务状态。");
    }
    private static string LoadLocalToken()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZeroTier", "One", "authtoken.secret");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 2048) throw new IOException("ZeroTier One 未安装，或未找到本机令牌。");
            return File.ReadAllText(path).Trim();
        }
        catch (UnauthorizedAccessException) { throw new IOException("无法读取 ZeroTier 本机令牌。请在本机工具中提供 API Token，或以管理员权限配置 ZeroTier 后重试。"); }
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

public sealed class ZeroTierNetworkLease : IAsyncDisposable
{
    private readonly ZeroTierClient _client;
    private int _disposed;
    internal ZeroTierNetworkLease(ZeroTierClient client, ZeroTierNetworkInfo info, bool created) { _client = client; Info = info; CreatedByLauncher = created; }
    public ZeroTierNetworkInfo Info { get; }
    public bool CreatedByLauncher { get; }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && CreatedByLauncher)
            await _client.LeaveBestEffortAsync(Info.Id).ConfigureAwait(false);
    }
}
