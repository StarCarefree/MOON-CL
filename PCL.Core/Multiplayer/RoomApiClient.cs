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

    public RoomApiClient(string serviceUrl, HttpClient? http = null)
    {
        if (!Uri.TryCreate(serviceUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("房间服务须使用 HTTPS；仅本机调试允许 HTTP。请填写服务根地址。");
        _root = new Uri(uri, "api/v1/rooms");
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    }

    public Task<RoomTicket> CreateAsync(CreateRoomRequest request, CancellationToken ct) =>
        SendAsync<RoomTicket>(HttpMethod.Post, "", request, null, ct);
    public Task<RoomTicket> JoinAsync(string code, JoinRoomRequest request, CancellationToken ct) =>
        SendAsync<RoomTicket>(HttpMethod.Post, "/" + Code(code) + "/join", request, null, ct);
    public Task<RoomSnapshot> HeartbeatAsync(RoomTicket ticket, CancellationToken ct) =>
        SendAsync<RoomSnapshot>(HttpMethod.Post, MemberPath(ticket) + "/heartbeat", null, ticket.Token, ct);
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
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(128 * 1024, ct).ConfigureAwait(false);
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
            throw new RoomApiException(response.StatusCode, message);
        }
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new JsonException("房间服务返回了空数据。");
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
