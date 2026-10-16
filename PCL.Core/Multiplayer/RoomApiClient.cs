using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Multiplayer;

public sealed class RoomApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Small authenticated HTTP messages only; never handles Minecraft traffic.</summary>
public sealed class RoomApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _root;
    private readonly bool _official;
    private bool? _legacyCoordinator;

    public RoomApiClient(string serviceUrl, HttpClient? http = null)
    {
        if (!Uri.TryCreate(serviceUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && (uri.IsLoopback || RoomServiceDirectory.IsOfficial(uri)))) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("房间服务须使用 HTTPS；仅本机调试允许 HTTP。请填写服务根地址。");
        _root = new Uri(uri, "api/v1/rooms");
        _official = RoomServiceDirectory.IsOfficial(uri);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    }

    public Task<RoomTicket> CreateAsync(CreateRoomRequest request, CancellationToken ct) =>
        SendProfileAsync<RoomTicket>(HttpMethod.Post, "", request, null, () => request with { Game = Legacy(request.Game) }, ct);
    public Task<RoomTicket> JoinAsync(string code, JoinRoomRequest request, CancellationToken ct) =>
        SendProfileAsync<RoomTicket>(HttpMethod.Post, "/" + Code(code) + "/join", request, null, () => request with { Game = Legacy(request.Game) }, ct);
    public Task<RoomSnapshot> HeartbeatAsync(RoomTicket ticket, CancellationToken ct) =>
        SendAsync<RoomSnapshot>(HttpMethod.Post, MemberPath(ticket) + "/heartbeat", null, ticket.Token, ct);
    public Task<RoomSnapshot> HeartbeatAsync(RoomTicket ticket, RoomPresence presence, CancellationToken ct)
    {
        presence.Validate();
        return SendProfileAsync<RoomSnapshot>(HttpMethod.Post, MemberPath(ticket) + "/heartbeat", presence, ticket.Token,
            () => presence with { Game = Legacy(presence.Game) }, ct);
    }
    private static RoomGameStatus? Legacy(RoomGameStatus? game) => game is null ? null : game with { Pack = null, Server = "" };
    private async Task<T> SendProfileAsync<T>(HttpMethod method, string suffix, object body, string? token, Func<object> legacy, CancellationToken ct)
    {
        if (await UsesLegacyAsync(ct).ConfigureAwait(false)) body = legacy();
        return await SendAsync<T>(method, suffix, body, token, ct).ConfigureAwait(false);
    }
    private readonly SemaphoreSlim _legacyGate = new(1, 1);
    private async Task<bool> UsesLegacyAsync(CancellationToken ct)
    {
        if (_legacyCoordinator is { } known) return known;
        await _legacyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_legacyCoordinator is { } raced) return raced;
            var probe = new CreateRoomRequest("probe", "probe", "", null, "", new RoomGameStatus(Server: "not a server"));
            try { await SendAsync<RoomTicket>(HttpMethod.Post, "", probe, null, ct).ConfigureAwait(false); }
            catch (RoomApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
            {
                if (ex.Message.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("参数格式错误"))
                    _legacyCoordinator = true;
            }
            _legacyCoordinator ??= false;
            return _legacyCoordinator.Value;
        }
        finally { _legacyGate.Release(); }
    }
    public Task<RoomSnapshot> PublishRoutesAsync(RoomTicket ticket, RoomRoute[] routes, CancellationToken ct)
    {
        RoomRoute.ValidateAll(routes);
        return SendAsync<RoomSnapshot>(HttpMethod.Put, "/" + Code(ticket.Room.Code) + "/routes", new { routes }, ticket.Token, ct);
    }
    public Task<RoomSnapshot> PublishAsync(RoomTicket ticket, RoomConnection connection, CancellationToken ct)
    {
        connection.Validate();
        return SendAsync<RoomSnapshot>(HttpMethod.Put, "/" + Code(ticket.Room.Code) + "/connection", connection, ticket.Token, ct);
    }
    public async Task LeaveAsync(RoomTicket ticket, CancellationToken ct) =>
        _ = await SendAsync<object>(HttpMethod.Delete, MemberPath(ticket), null, ticket.Token, ct).ConfigureAwait(false);

    private static string Code(string code)
    {
        code = code.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(code, "^[A-Z0-9]{8}$")) throw new ArgumentException("邀请码应为 8 位字母或数字。");
        return code;
    }
    private static string MemberPath(RoomTicket ticket) => "/" + Code(ticket.Room.Code) + "/members/" + Uri.EscapeDataString(ticket.MemberId);


    private async Task<T> SendAsync<T>(HttpMethod method, string suffix, object? body, string? token, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        ct = timeout.Token;
        using var request = new HttpRequestMessage(method, new Uri(_root.AbsoluteUri + suffix));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await SendRequestAsync(request, ct).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(256 * 1024, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string message = $"房间服务返回 {(int)response.StatusCode}。";
            try
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    message = error.GetString() is { Length: <= 512 } text ? text : message;
            }
            catch (JsonException) { }
            var failure = new RoomApiException(response.StatusCode, message);
            throw _official ? new RoomApiException(response.StatusCode, RoomServiceDirectory.FriendlyError(failure, true)) : failure;
        }
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new JsonException("房间服务返回了空数据。");
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (_official)
        { throw new HttpRequestException("官方中控暂未就绪或无法连接。", null, ex.StatusCode); }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
