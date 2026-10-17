using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Social;

namespace PCL.Core.Test.Social;

[TestClass]
public sealed class FriendApiClientTest
{
    private const string Self = "11111111111111111111111111111111";
    private const string Other = "22222222222222222222222222222222";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    [TestMethod]
    public void ServiceUrlRejectsCredentialsQueriesAndRemoteHttp()
    {
        foreach (var url in new[] { "http://friends.example.com", "https://user:pass@friends.example.com/v1", "https://friends.example.com/v1?token=1", "file:///C:/friends" })
            Assert.ThrowsExactly<ArgumentException>(() => FriendApiClient.Normalize(url));
        Assert.AreEqual("https://api.axolotlclient.com/v1/", FriendApiClient.Normalize(FriendApiClient.OfficialUrl).AbsoluteUri);
        Assert.IsTrue(FriendApiClient.IsOfficial("https://api.axolotlclient.com/v1"));
        Assert.AreEqual("http://127.0.0.1:8080/v1/", FriendApiClient.Normalize("http://127.0.0.1:8080/v1").AbsoluteUri);
    }

    [TestMethod]
    public async Task AuthenticatePostsMojangJoinThenUsesRawAuthorization()
    {
        string? join = null;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "sessionserver.mojang.com")
            {
                Assert.AreEqual(FriendApiClient.SessionJoinUrl, request.RequestUri.AbsoluteUri);
                join = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            Assert.AreEqual("https://api.axolotlclient.com/v1/authenticate", request.RequestUri.GetLeftPart(UriPartial.Path));
            var query = request.RequestUri.Query;
            StringAssert.Contains(query, "username=Steve");
            StringAssert.Contains(query, "server_id=");
            Assert.IsNull(request.Headers.Authorization);
            return Json(new { uuid = Self, username = "Steve", access_token = "raw-token" });
        }));
        using var api = new FriendApiClient(FriendApiClient.OfficialUrl, http);
        var session = await api.AuthenticateAsync("Steve", "minecraft-token", Self, CancellationToken.None);
        using var document = JsonDocument.Parse(join!);
        Assert.AreEqual("minecraft-token", document.RootElement.GetProperty("accessToken").GetString());
        Assert.AreEqual(Self, document.RootElement.GetProperty("selectedProfile").GetString());
        Assert.AreEqual(32, document.RootElement.GetProperty("serverId").GetString()!.Length);
        Assert.AreEqual("raw-token", session.AccessToken);
    }

    [TestMethod]
    public async Task RelationRequestsUseRawTokenAndOfficialRelationQuery()
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new Handler(request =>
        {
            seen = request;
            Assert.AreEqual("raw-token", request.Headers.Authorization?.Parameter ?? request.Headers.GetValues("Authorization").Single());
            Assert.AreNotEqual("Bearer", request.Headers.Authorization?.Scheme);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        using var api = new FriendApiClient("https://friends.example.com/v1/", http);
        await api.SetRelationAsync("raw-token", Self, "22222222-2222-2222-2222-222222222222", "request", CancellationToken.None);
        Assert.AreEqual("https://friends.example.com/v1/user/" + Other + "?relation=request", seen!.RequestUri!.AbsoluteUri);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => api.SetRelationAsync("raw-token", Self, Self, "friend", CancellationToken.None));
        Assert.AreEqual("对方还没有登录过这个好友服务。", FriendApiClient.Explain(HttpStatusCode.NotFound, new Uri("https://friends.example.com/v1/user/" + Other)));
        Assert.AreEqual("对方已拉黑你，或你还不能接受这个申请。", FriendApiClient.Explain(HttpStatusCode.Forbidden, new Uri("https://friends.example.com/v1/user/" + Other)));
        Assert.AreEqual("好友登录已过期，请重新连接。", FriendApiClient.Explain(HttpStatusCode.Unauthorized, new Uri("https://friends.example.com/v1/account/relations/friends")));
    }

    [TestMethod]
    public async Task ListsAndProfileDecodeRelationStatus()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/account/relations/friends" => Json(new[] { Other }),
            "/v1/account/relations/requests" => Json(new { @in = new[] { Other }, @out = Array.Empty<string>() }),
            "/v1/account/relations/blocked" => Json(Array.Empty<string>()),
            _ => Json(new { uuid = Other, username = "Alex", relation = "request", status = new { type = "online", activity = new { title = "Building", description = "Castle" } } })
        }));
        using var api = new FriendApiClient("https://friends.example.com/v1", http);
        CollectionAssert.AreEqual(new[] { Other }, (System.Collections.ICollection)await api.FriendsAsync("raw-token", CancellationToken.None));
        var requests = await api.RequestsAsync("raw-token", CancellationToken.None);
        CollectionAssert.AreEqual(new[] { Other }, (System.Collections.ICollection)requests.Incoming);
        Assert.AreEqual(0, requests.Outgoing.Count);
        var profile = await api.UserAsync("raw-token", Other, CancellationToken.None);
        Assert.AreEqual("Alex", profile.Username);
        Assert.AreEqual("request", profile.Relation);
        Assert.AreEqual("online", profile.Status);
        Assert.AreEqual("Building", profile.Activity);
        Assert.AreEqual("Castle", profile.ActivityDescription);
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    [TestMethod]
    public async Task NameLookupUsesDocumentedQueryAndRejectsMissingPlayers()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("https://api.nxvav.cn/api/mcuuid?id=Notch", request.RequestUri!.AbsoluteUri);
            return request.RequestUri.Query.Contains("Notch")
                ? Json(new { code = 200, name = "Notch", uuid = "069a79f444e94726a5befca90e38aaf5" })
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        }));
        using var api = new FriendApiClient(FriendApiClient.OfficialUrl, http);
        Assert.AreEqual("069a79f444e94726a5befca90e38aaf5", await api.LookupUuidAsync("Notch", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => api.LookupUuidAsync("bad name", CancellationToken.None));
    }

    [TestMethod]
    public async Task DirectChannelMessageAndExternalServerFollowModRoutes()
    {
        var self = "069a79f444e94726a5befca90e38aaf5";
        var other = "853c80ef3c3749fdaa49938b674adae6";
        var seen = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            seen.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath switch
            {
                "/v1/channels" => Json(new ulong[] { 42 }),
                "/v1/channel/42" when request.Method == HttpMethod.Get => Json(new { id = 42, name = FriendApiClient.DmName(self, other), owner = self, participants = new[] { other }, persistence = new { type = "channel" } }),
                "/v1/channel/42/messages" => Json(new[] { new { id = 7, channel_id = 42, sender = other, sender_name = "jeb_", content = "hi", timestamp = "2026-08-14T00:00:00Z" } }),
                "/v1/channel/42" when request.Method == HttpMethod.Post => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("99", Encoding.UTF8) },
                "/v1/channels/invites" => Json(new[] { new { id = 8, channel_name = "builders", from = other } }),
                _ => Json(new { uuid = other, username = "jeb_", relation = "friend", status = new { type = "online", activity = new { title = "In game", description = "Hypixel", metadata = new { type = "external_server", attributes = new { serverName = "Hypixel", address = "mc.hypixel.net" } } } } })
            };
        }));
        using var api = new FriendApiClient("https://friends.example.com/v1", http);
        Assert.AreEqual("42", await api.GetOrCreateDirectAsync("raw-token", self, other, CancellationToken.None));
        var message = (await api.MessagesAsync("raw-token", "42", null, CancellationToken.None)).Single();
        Assert.AreEqual("hi", message.Content);
        Assert.AreEqual("99", await api.SendMessageAsync("raw-token", "42", "hello", "Notch", CancellationToken.None));
        var invite = (await api.ChannelInvitesAsync("raw-token", CancellationToken.None)).Single();
        Assert.AreEqual("builders", invite.Name);
        var profile = await api.UserAsync("raw-token", other, CancellationToken.None);
        Assert.AreEqual("mc.hypixel.net", FriendApiClient.ServerOf(profile)!.Address);
        Assert.IsFalse(FriendApiClient.IsJoinAddress("http://evil.example/join"));
        CollectionAssert.Contains(seen, "GET /v1/channels");
        CollectionAssert.Contains(seen, "POST /v1/channel/42");
    }
}
