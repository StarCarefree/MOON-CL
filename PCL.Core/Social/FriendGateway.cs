using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Social;

public sealed record FriendEvent(string Target, string From, string Channel = "", string ChannelName = "", string Content = "", string MessageId = "", string SenderName = "");

/// <summary>Server-to-client gateway. Business messages are never written to the socket.</summary>
public sealed class FriendGateway : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ClientWebSocket _socket;
    private readonly bool _ownsSocket;
    private readonly Uri _gateway;

    public FriendGateway(string serviceUrl, string accessToken, ClientWebSocket? socket = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Contains(' '))
            throw new ArgumentException("好友令牌无效。");
        var root = FriendApiClient.Normalize(serviceUrl);
        _gateway = new UriBuilder(root) { Scheme = root.Scheme == "https" ? "wss" : "ws", Path = new Uri(root, "gateway").AbsolutePath }.Uri;
        _ownsSocket = socket is null;
        _socket = socket ?? new ClientWebSocket();
        _socket.Options.SetRequestHeader("Authorization", accessToken);
        _socket.Options.SetRequestHeader("User-Agent", "MOONCL");
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
    }

    public Uri GatewayUri => _gateway;

    public async Task ListenAsync(Func<FriendEvent, CancellationToken, Task> onEvent, CancellationToken ct)
    {
        if (onEvent is null) throw new ArgumentNullException(nameof(onEvent));
        await _socket.ConnectAsync(_gateway, ct).ConfigureAwait(false);
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var text = await ReadTextAsync(buffer, ct).ConfigureAwait(false);
            if (text is null) return;
            FriendEvent? parsed;
            try { parsed = JsonSerializer.Deserialize<FriendWire>(text, Json)?.ToEvent(); }
            catch (JsonException) { continue; }
            if (parsed is { Target: "friend_request" or "friend_request_accept" or "friend_request_deny" or "activity_update" or "chat_message" or "channel_invite" or "channel_invite_reaction" })
                await onEvent(parsed, ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> ReadTextAsync(byte[] buffer, CancellationToken ct)
    {
        var builder = new StringBuilder();
        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false); }
            catch (InvalidOperationException) { return null; }

            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return builder.ToString();
            if (builder.Length > 65536) return "";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None).ConfigureAwait(false); }
            catch (WebSocketException) { }
        }
        if (_ownsSocket) _socket.Dispose();
    }

    private sealed class FriendWire
    {
        public string? Target { get; set; }
        public string? From { get; set; }
        public string? User { get; set; }
        public string? Player { get; set; }
        public JsonElement? Channel { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("channel_name")] public string? ChannelName { get; set; }
        public string? Content { get; set; }
        public JsonElement? Id { get; set; }
        public string? Sender { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("sender_name")] public string? SenderName { get; set; }
        public FriendEvent? ToEvent()
        {
            if (string.IsNullOrWhiteSpace(Target)) return null;
            var who = First(From, User, Player, Sender);
            return new FriendEvent(Target, who, Number(Channel), ChannelName ?? "", Content ?? "", Number(Id), SenderName ?? "");
        }
        private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        private static string Number(JsonElement? value)
        {
            if (value is not { } item) return "";
            return item.ValueKind switch
            {
                JsonValueKind.Number when item.TryGetUInt64(out var number) => number.ToString(),
                JsonValueKind.String => item.GetString() ?? "",
                _ => ""
            };
        }
    }
}
