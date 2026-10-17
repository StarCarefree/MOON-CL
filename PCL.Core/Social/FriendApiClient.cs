using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Social;

public sealed class FriendApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed record FriendSession(string Uuid, string Username, string AccessToken);

public sealed record FriendProfile(string Uuid, string Username, string? Relation, string? Status, string? Activity, string? ActivityDescription, string? ServerAddress = null, string? ServerName = null, DateTimeOffset? Started = null);

public sealed record FriendSettings(bool ShowRegistered, bool RetainUsernames, bool ShowLastOnline, bool ShowActivity, bool AllowFriendsImageAccess);

public sealed record FriendRequests(IReadOnlyList<string> Incoming, IReadOnlyList<string> Outgoing);

public sealed record FriendChannel(string Id, string Name, string Owner, IReadOnlyList<string> Participants, bool Direct);

public sealed record FriendMessage(string Id, string ChannelId, string Sender, string SenderName, string Content, DateTimeOffset Timestamp);

public sealed record FriendChannelInvite(string ChannelId, string Name, string From);

public sealed record FriendServer(string Address, string Name);

/// <summary>AxolotlClient-compatible friend API. Authorization is the raw base64 token, not Bearer.</summary>
public sealed class FriendApiClient : IDisposable
{
    public const string OfficialUrl = "https://api.axolotlclient.com/v1/";
    public const string SessionJoinUrl = "https://sessionserver.mojang.com/session/minecraft/join";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _root;

    public Uri Root => _root;

    public FriendApiClient(string serviceUrl, HttpClient? http = null)
    {
        _root = Normalize(serviceUrl);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public static Uri Normalize(string serviceUrl)
    {
        if (!Uri.TryCreate((serviceUrl ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.IsFile ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("好友服务须是不含账号、参数的 http 或 https 根地址。");
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new ArgumentException("自定义好友服务的明文地址只允许本机调试；远程服务请使用 HTTPS。");
        return uri;
    }

    public static bool IsOfficial(string? serviceUrl)
    {
        if (!Uri.TryCreate((serviceUrl ?? "").Trim(), UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), OfficialUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    public static string MojangUuid(string? uuid)
    {
        var text = (uuid ?? "").Trim().Replace("-", "", StringComparison.Ordinal);
        if (text.Length != 32 || !Guid.TryParseExact(text, "N", out var parsed))
            throw new ArgumentException("UUID 须是 32 位十六进制，可带连字符。");
        return parsed.ToString("N");
    }
    public const string NameLookupUrl = "https://uapis.cn/api/v1/game/minecraft/userinfo";

    public static bool TryUuid(string? value, out string uuid)
    {
        try { uuid = MojangUuid(value); return true; }
        catch (ArgumentException) { uuid = ""; return false; }
    }

    public static string PlayerName(string? name)
    {
        var text = (name ?? "").Trim();
        if (text.Length is < 3 or > 16 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("正版用户名须为 3–16 位字母、数字或下划线。");
        return text;
    }

    /// <summary>Resolve a Java username through https://uapis.cn/docs/api-reference/get-game-minecraft-userinfo.</summary>
    public async Task<string> LookupUuidAsync(string username, CancellationToken ct)
    {
        var name = PlayerName(username);
        var uri = new Uri(NameLookupUrl + "?username=" + Uri.EscapeDataString(name));
        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        UuidBody? body = null;
        try { body = await response.Content.ReadFromJsonAsync<UuidBody>(Json, ct).ConfigureAwait(false); }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode || !TryUuid(body?.Uuid, out var uuid))
            throw new FriendApiException(response.StatusCode, "没有找到这个正版用户名。");
        return uuid;
    }

    public async Task<FriendSession> AuthenticateAsync(string username, string accessToken, string uuid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length is < 3 or > 16)
            throw new ArgumentException("正版用户名无效。");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("正版访问令牌为空。");
        var id = MojangUuid(uuid);
        var serverId = Guid.NewGuid().ToString("N");
        var join = new { accessToken, selectedProfile = id, serverId };
        using (var response = await _http.PostAsJsonAsync(SessionJoinUrl, join, Json, ct).ConfigureAwait(false))
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new FriendApiException(response.StatusCode, "正版登录已失效，请在账号页重新登录微软账号。");
            if (!response.IsSuccessStatusCode)
                throw new FriendApiException(response.StatusCode, "无法向 Mojang 登记会话，请稍后重试。");
        }
        var auth = new UriBuilder(_root) { Path = new Uri(_root, "authenticate").AbsolutePath, Query = "username=" + Uri.EscapeDataString(username.Trim()) + "&server_id=" + Uri.EscapeDataString(serverId) }.Uri;
        using var ticket = await SendAsync(HttpMethod.Get, auth, null, null, ct).ConfigureAwait(false);
        var body = await ticket.Content.ReadFromJsonAsync<AuthBody>(Json, ct).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.AccessToken))
            throw new FriendApiException(HttpStatusCode.BadGateway, "好友服务没有返回访问令牌。");
        return new FriendSession(MojangUuid(body.Uuid), body.Username ?? username.Trim(), body.AccessToken);
    }

    public Task<IReadOnlyList<string>> FriendsAsync(string token, CancellationToken ct) => UuidsAsync("account/relations/friends", token, ct);
    public Task<IReadOnlyList<string>> BlockedAsync(string token, CancellationToken ct) => UuidsAsync("account/relations/blocked", token, ct);

    public async Task<FriendRequests> RequestsAsync(string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(_root, "account/relations/requests"), token, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<RequestsBody>(Json, ct).ConfigureAwait(false) ?? new RequestsBody();
        return new FriendRequests(Clean(body.Incoming), Clean(body.Outgoing));
    }

    public async Task<FriendProfile> UserAsync(string token, string uuid, CancellationToken ct)
    {
        var id = MojangUuid(uuid);
        using var response = await SendAsync(HttpMethod.Get, new Uri(_root, "user/" + id), token, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<UserBody>(Json, ct).ConfigureAwait(false)
                   ?? throw new FriendApiException(HttpStatusCode.BadGateway, "好友资料为空。");
        return new FriendProfile(MojangUuid(body.Uuid ?? id), body.Username ?? id, body.Relation, body.Status?.Type, body.Status?.Activity?.Title, body.Status?.Activity?.Description, ServerAddress(body.Status?.Activity), body.Status?.Activity?.Metadata?.Attributes?.ServerName, body.Status?.Activity?.Started);
    }

    public static FriendServer? ServerOf(FriendProfile? profile)
    {
        if (profile?.Status != "online" || !IsJoinAddress(profile.ServerAddress)) return null;
        var name = string.IsNullOrWhiteSpace(profile.ServerName) ? profile.ServerAddress! : profile.ServerName.Trim();
        return new FriendServer(profile.ServerAddress!, name);
    }

    public static bool IsJoinAddress(string? address)
    {
        var text = (address ?? "").Trim();
        if (text.Length is < 3 or > 253 || text.Contains(' ') || text.Contains('/') || text.Contains('\\') || text.Contains('@')) return false;
        var host = text;
        var portText = "";
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            host = text[..colon];
            portText = text[(colon + 1)..];
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535) return false;
        }
        if (host.Length is < 1 or > 253 || host.StartsWith('.') || host.EndsWith('.') || host.Contains("..")) return false;
        return host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    public static string ChannelId(string? id)
    {
        var text = (id ?? "").Trim();
        if (text.Length is < 1 or > 20 || !ulong.TryParse(text, out var value) || value == 0)
            throw new ArgumentException("频道编号无效。");
        return value.ToString();
    }

    public static string DmName(string selfUuid, string otherUuid)
    {
        var self = Guid.ParseExact(MojangUuid(selfUuid), "N");
        var other = Guid.ParseExact(MojangUuid(otherUuid), "N");
        unchecked
        {
            var most = (long)(((ulong)Most(self) ^ (ulong)Most(other)) * 53UL);
            var least = (long)(((ulong)Least(self) ^ (ulong)Least(other)) * 53UL);
            var bytes = new byte[16];
            WriteLong(bytes, 0, most);
            WriteLong(bytes, 8, least);
            return new Guid(bytes, true).ToString("N");
        }
    }

    private static long Most(Guid id)
    {
        var bytes = id.ToByteArray(true);
        return ReadLong(bytes, 0);
    }

    private static long Least(Guid id)
    {
        var bytes = id.ToByteArray(true);
        return ReadLong(bytes, 8);
    }

    private static long ReadLong(byte[] bytes, int offset)
    {
        long value = 0;
        for (var i = 0; i < 8; i++) value = (value << 8) | bytes[offset + i];
        return value;
    }

    private static void WriteLong(byte[] bytes, int offset, long value)
    {
        for (var i = 7; i >= 0; i--) { bytes[offset + i] = (byte)value; value >>= 8; }
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetUInt64(out var number) => number.ToString(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.ToString()
    };

    public async Task<IReadOnlyList<string>> ChannelIdsAsync(string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(_root, "channels"), token, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct).ConfigureAwait(false);
        var list = new List<string>();
        if (body.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in body.EnumerateArray())
        {
            try { list.Add(ChannelId(item.ValueKind == JsonValueKind.Number ? item.GetUInt64().ToString() : item.GetString())); }
            catch (ArgumentException) { }
            catch (FormatException) { }
        }
        return list;
    }

    public async Task<FriendChannel> ChannelAsync(string token, string id, string selfUuid, CancellationToken ct)
    {
        var channel = ChannelId(id);
        using var response = await SendAsync(HttpMethod.Get, new Uri(_root, "channel/" + channel), token, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<ChannelBody>(Json, ct).ConfigureAwait(false)
                   ?? throw new FriendApiException(HttpStatusCode.BadGateway, "频道资料为空。");
        var owner = MojangUuid(body.Owner);
        var people = Clean(body.Participants);
        var name = body.Name ?? channel;
        var direct = people.Count == 1 && string.Equals(name, DmName(selfUuid, owner.Equals(MojangUuid(selfUuid), StringComparison.OrdinalIgnoreCase) ? people[0] : owner), StringComparison.OrdinalIgnoreCase);
        return new FriendChannel(ChannelId(Text(body.Id)), name, owner, people, direct);
    }

    public async Task<string> CreateChannelAsync(string token, string name, IReadOnlyList<string> participants, CancellationToken ct)
    {
        var title = (name ?? "").Trim();
        if (title.Length is < 1 or > 32) throw new ArgumentException("频道名称须为 1–32 个字符。");
        var people = participants.Select(MojangUuid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var content = JsonContent.Create(new { name = title, persistence = new { type = "channel" }, participants = people }, options: Json);
        using var response = await SendAsync(HttpMethod.Post, new Uri(_root, "channel"), token, content, ct).ConfigureAwait(false);
        return ChannelId((await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim().Trim('"'));
    }

    public async Task<string> GetOrCreateDirectAsync(string token, string selfUuid, string otherUuid, CancellationToken ct)
    {
        var self = MojangUuid(selfUuid);
        var other = MojangUuid(otherUuid);
        if (string.Equals(self, other, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("不能和自己私聊。");
        var expected = DmName(self, other);
        foreach (var id in await ChannelIdsAsync(token, ct).ConfigureAwait(false))
        {
            var channel = await ChannelAsync(token, id, self, ct).ConfigureAwait(false);
            if (channel.Direct && string.Equals(channel.Name, expected, StringComparison.OrdinalIgnoreCase) &&
                channel.Participants.Contains(other, StringComparer.OrdinalIgnoreCase))
                return channel.Id;
        }
        return await CreateChannelAsync(token, expected, [other], ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FriendMessage>> MessagesAsync(string token, string id, DateTimeOffset? before, CancellationToken ct)
    {
        var channel = ChannelId(id);
        var uri = new Uri(_root, "channel/" + channel + "/messages" + (before is null ? "" : "?before=" + Uri.EscapeDataString(before.Value.UtcDateTime.ToString("O"))));
        using var response = await SendAsync(HttpMethod.Get, uri, token, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<MessageBody[]>(Json, ct).ConfigureAwait(false) ?? [];
        return body.Select(item => new FriendMessage(ChannelId(Text(item.Id)), ChannelId(Text(item.ChannelId) ?? channel), MojangUuid(item.Sender), (item.SenderName ?? "").Trim(), item.Content ?? "", item.Timestamp)).ToArray();
    }

    public async Task<string> SendMessageAsync(string token, string id, string content, string displayName, CancellationToken ct)
    {
        var text = (content ?? "").Trim();
        var name = (displayName ?? "").Trim();
        if (text.Length is < 1 or > 2000) throw new ArgumentException("消息须为 1–2000 个字符。");
        if (name.Length is < 1 or > 179) throw new ArgumentException("显示名无效。");
        using var payload = JsonContent.Create(new { content = text, display_name = name }, options: Json);
        using var response = await SendAsync(HttpMethod.Post, new Uri(_root, "channel/" + ChannelId(id)), token, payload, ct).ConfigureAwait(false);
        return ChannelId((await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim().Trim('"'));
    }

    public async Task LeaveChannelAsync(string token, string id, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, new Uri(_root, "channel/" + ChannelId(id)), token, null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FriendChannelInvite>> ChannelInvitesAsync(string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(_root, "channels/invites"), token, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<InviteBody[]>(Json, ct).ConfigureAwait(false) ?? [];
        var list = new List<FriendChannelInvite>();
        foreach (var item in body)
        {
            try { list.Add(new FriendChannelInvite(ChannelId(Text(item.Id)), (item.Name ?? "").Trim(), MojangUuid(item.From))); }
            catch (ArgumentException) { }
        }
        return list;
    }

    public async Task AnswerChannelInviteAsync(string token, string id, bool accept, CancellationToken ct)
    {
        var uri = new Uri(_root, "channels/invites?id=" + Uri.EscapeDataString(ChannelId(id)) + "&accept=" + (accept ? "true" : "false"));
        using var _ = await SendAsync(HttpMethod.Post, uri, token, null, ct).ConfigureAwait(false);
    }

    private static string? ServerAddress(ActivityBody? activity)
    {
        var metadata = activity?.Metadata;
        if (!string.Equals(metadata?.Type, "external_server", StringComparison.Ordinal)) return null;
        return IsJoinAddress(metadata?.Attributes?.Address) ? metadata!.Attributes!.Address!.Trim() : null;
    }

    public async Task<FriendSettings> SettingsAsync(string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(_root, "account/settings"), token, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<SettingsBody>(Json, ct).ConfigureAwait(false) ?? new SettingsBody();
        return new FriendSettings(body.ShowRegistered, body.RetainUsernames, body.ShowLastOnline, body.ShowActivity, body.AllowFriendsImageAccess);
    }

    public async Task UpdateSettingsAsync(string token, FriendSettings settings, CancellationToken ct)
    {
        using var content = JsonContent.Create(new SettingsBody
        {
            ShowRegistered = settings.ShowRegistered,
            RetainUsernames = settings.RetainUsernames,
            ShowLastOnline = settings.ShowLastOnline,
            ShowActivity = settings.ShowActivity,
            AllowFriendsImageAccess = settings.AllowFriendsImageAccess
        }, options: Json);
        using var _ = await SendAsync(HttpMethod.Patch, new Uri(_root, "account/settings"), token, content, ct).ConfigureAwait(false);
    }

    public async Task UpdateActivityAsync(string token, string title, string description, CancellationToken ct)
    {
        var text = (description ?? "").Trim();
        if (text.Length > 120) text = text[..120];
        using var content = JsonContent.Create(new { title, description = text, started = DateTimeOffset.UtcNow }, options: Json);
        using var _ = await SendAsync(HttpMethod.Post, new Uri(_root, "account/activity"), token, content, ct).ConfigureAwait(false);
    }
    public async Task SetRelationAsync(string token, string selfUuid, string otherUuid, string relation, CancellationToken ct)
    {
        if (relation is not ("request" or "friend" or "none" or "blocked"))
            throw new ArgumentException("未知的好友关系。");
        var self = MojangUuid(selfUuid);
        var other = MojangUuid(otherUuid);
        if (string.Equals(self, other, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能把自己加为好友。");
        using var _ = await SendAsync(HttpMethod.Post, new Uri(_root, "user/" + other + "?relation=" + relation), token, null, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> UuidsAsync(string path, string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(_root, path), token, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<string[]>(Json, ct).ConfigureAwait(false);
        return Clean(body);
    }

    private static IReadOnlyList<string> Clean(IEnumerable<string>? values)
    {
        var list = new List<string>();
        if (values is null) return list;
        foreach (var value in values)
        {
            try { list.Add(MojangUuid(value)); }
            catch (ArgumentException) { }
        }
        return list;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string? token, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (!string.IsNullOrEmpty(token))
        {
            if (token.Contains(' ') || token.Contains('\r') || token.Contains('\n'))
                throw new ArgumentException("好友令牌无效。");
            request.Headers.TryAddWithoutValidation("Authorization", token);
        }
        request.Headers.TryAddWithoutValidation("User-Agent", "MOONCL");
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new FriendApiException(0, "无法连接好友服务，请检查地址和网络。"); }
        if (response.IsSuccessStatusCode) return response;
        var failure = response;
        response = null!;
        using (failure)
            throw new FriendApiException(failure.StatusCode, Explain(failure.StatusCode, uri));
    }

    internal static string Explain(HttpStatusCode status, Uri uri)
    {
        var path = uri.AbsolutePath;
        return status switch
        {
            HttpStatusCode.Unauthorized => "好友登录已过期，请重新连接。",
            HttpStatusCode.Forbidden when path.Contains("/user/", StringComparison.Ordinal) => "对方已拉黑你，或你还不能接受这个申请。",
            HttpStatusCode.NotFound when path.Contains("/user/", StringComparison.Ordinal) => "对方还没有登录过这个好友服务。",
            HttpStatusCode.BadRequest => "不能对自己进行这个操作。",
            HttpStatusCode.Conflict => "好友在线连接已被占用，请稍后重试。",
            _ => "好友服务暂时不可用，请稍后重试。"
        };
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    private sealed class AuthBody
    {
        [JsonPropertyName("uuid")] public string? Uuid { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    }
    private sealed class UuidBody
    {
        [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    }
    private sealed class RequestsBody
    {
        [JsonPropertyName("in")] public string[]? Incoming { get; set; }
        [JsonPropertyName("out")] public string[]? Outgoing { get; set; }
    }
    private sealed class UserBody
    {
        [JsonPropertyName("uuid")] public string? Uuid { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("relation")] public string? Relation { get; set; }
        [JsonPropertyName("status")] public StatusBody? Status { get; set; }
    }
    private sealed class StatusBody
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("activity")] public ActivityBody? Activity { get; set; }
    }
    private sealed class ActivityBody
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("started")] public DateTimeOffset? Started { get; set; }
        [JsonPropertyName("metadata")] public MetadataBody? Metadata { get; set; }
    }
    private sealed class MetadataBody
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("attributes")] public ServerBody? Attributes { get; set; }
    }
    private sealed class ServerBody
    {
        [JsonPropertyName("serverName")] public string? ServerName { get; set; }
        [JsonPropertyName("address")] public string? Address { get; set; }
    }
    private sealed class ChannelBody
    {
        [JsonPropertyName("id")] public JsonElement Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("owner")] public string? Owner { get; set; }
        [JsonPropertyName("participants")] public string[]? Participants { get; set; }
    }
    private sealed class MessageBody
    {
        [JsonPropertyName("id")] public JsonElement Id { get; set; }
        [JsonPropertyName("channel_id")] public JsonElement ChannelId { get; set; }
        [JsonPropertyName("sender")] public string? Sender { get; set; }
        [JsonPropertyName("sender_name")] public string? SenderName { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; }
    }
    private sealed class InviteBody
    {
        [JsonPropertyName("id")] public JsonElement Id { get; set; }
        [JsonPropertyName("channel_name")] public string? Name { get; set; }
        [JsonPropertyName("from")] public string? From { get; set; }
    }
    private sealed class SettingsBody
    {
        [JsonPropertyName("show_registered")] public bool ShowRegistered { get; set; }
        [JsonPropertyName("retain_usernames")] public bool RetainUsernames { get; set; }
        [JsonPropertyName("show_last_online")] public bool ShowLastOnline { get; set; }
        [JsonPropertyName("show_activity")] public bool ShowActivity { get; set; }
        [JsonPropertyName("allow_friends_image_access")] public bool AllowFriendsImageAccess { get; set; } = true;
    }
}

