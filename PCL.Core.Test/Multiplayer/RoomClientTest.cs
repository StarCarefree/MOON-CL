using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Multiplayer;

namespace PCL.Core.Test.Multiplayer;

[TestClass]
public sealed class RoomClientTest
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private static RoomTicket Ticket(RoomConnection? connection = null) => new(
        new RoomSnapshot("ABCD2345", "test", "host", [new RoomMember("host", "owner", true)], connection, DateTimeOffset.UtcNow.AddSeconds(90)), "host", "private-token");
    private static HttpResponseMessage Ok<T>(T data) => new(HttpStatusCode.OK) { Content = JsonContent.Create(data) };

    [TestMethod]
    public void ServiceUrlRequiresTlsAndDoesNotAcceptCredentialsOrQueries()
    {
        foreach (var url in new[] { "http://rooms.example.com", "https://user:pass@rooms.example.com", "https://rooms.example.com/?token=secret", "file:///C:/rooms", "https://rooms.example.com/#x" })
            Assert.ThrowsExactly<ArgumentException>(() => new RoomApiClient(url));
        using var local = new RoomApiClient("http://localhost:8080");
        using var secure = new RoomApiClient("https://rooms.example.com/prefix");
    }

    [TestMethod]
    public async Task AuthenticatedHeartbeatUsesMemberTokenAndBasePath()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("https://rooms.example.com/prefix/api/v1/rooms/ABCD2345/members/host/heartbeat", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            Assert.AreEqual("private-token", request.Headers.Authorization.Parameter);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            return Ok(Ticket().Room);
        }));
        using var api = new RoomApiClient("https://rooms.example.com/prefix/", http);
        var room = await api.HeartbeatAsync(Ticket(), CancellationToken.None);
        Assert.AreEqual("ABCD2345", room.Code);
    }

    [TestMethod]
    public async Task InvalidCodeIsRejectedWithoutNetwork()
    {
        using var http = new HttpClient(new Handler(_ => throw new AssertFailedException("No request expected")));
        using var api = new RoomApiClient("https://example.com", http);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => api.JoinAsync("../../api", new JoinRoomRequest("guest", ""), CancellationToken.None));
    }

    [TestMethod]
    public async Task RoomExpiryClearsSessionAndOfflineLeaveAlwaysClearsLocalState()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            count++;
            var text = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            if (text.Contains("\"name\":\"probe\"")) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "JSON 或参数格式错误，请检查字段与类型" }) };
            return text.Contains("\"name\":\"test\"") ? Ok(Ticket()) : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { error = "room expired" }) };
        }));
        using var session = new RoomSession(new RoomApiClient("https://example.com", http));
        await session.CreateAsync(new CreateRoomRequest("test", "owner", "", null), CancellationToken.None);
        Assert.IsTrue(session.IsHost); Assert.IsTrue(session.IsFresh);
        var ex = await Assert.ThrowsExactlyAsync<RoomApiException>(() => session.RefreshAsync(CancellationToken.None));
        Assert.AreEqual("room expired", ex.Message); Assert.IsNull(session.Room);
        await session.LeaveAsync(CancellationToken.None);
        Assert.AreEqual(3, count);
    }

    [TestMethod]
    public async Task LeaveFailureStillDropsTicket()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            count++;
            var text = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            if (text.Contains("\"name\":\"probe\"")) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "JSON 或参数格式错误，请检查字段与类型" }) };
            return text.Contains("\"name\":\"test\"") ? Ok(Ticket()) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));
        using var session = new RoomSession(new RoomApiClient("https://example.com", http));
        await session.CreateAsync(new CreateRoomRequest("test", "owner", "", null), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<RoomApiException>(() => session.LeaveAsync(CancellationToken.None));
        Assert.IsNull(session.Ticket);
    }

    [TestMethod]
    public async Task RepeatedCreateAndGuestPublishAreRejectedLocally()
    {
        using var http = new HttpClient(new Handler(_ => Ok(Ticket() with { MemberId = "guest" })));
        using var session = new RoomSession(new RoomApiClient("https://example.com", http));
        await session.JoinAsync("ABCD2345", new JoinRoomRequest("guest", ""), CancellationToken.None);
        Assert.IsFalse(session.IsHost);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.CreateAsync(new CreateRoomRequest("test", "x", "", null), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.PublishConnectionAsync(new RoomConnection("frp", "example.com", 25565), CancellationToken.None));
    }

    [TestMethod]
    public void Ipv6RequiresGlobalAddressAndFormatsBrackets()
    {
        foreach (var address in new[] { "::1", "::", "fe80::1", "fc00::1", "2001:db8::1%12", "127.0.0.1", "ff02::1" })
            Assert.ThrowsExactly<ArgumentException>(() => new RoomConnection("ipv6", address, 25565).Validate());
        var c = new RoomConnection("ipv6", "2606:4700::1", 25565); c.Validate();
        Assert.AreEqual("[2606:4700::1]:25565", c.Endpoint);
        Assert.ThrowsExactly<ArgumentException>(() => new RoomConnection("ipv6", "2001:db8::1", 25565).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (c with { Port = 65536 }).Validate());
    }

    [TestMethod]
    public void FrpConfigurationUsesNativeTcpAndLoopbackXtcpWithoutSharingNodeToken()
    {
        var node = new FrpNode("frps.example.com", 7000, "a\"\n[[visitors]]\nname = \"injected");
        var p2p = FrpConfiguration.CreateP2p(node); p2p.Validate();
        var host = FrpConfiguration.Host(node, p2p, 25565);
        StringAssert.Contains(host, "type = \"xtcp\"");
        StringAssert.Contains(host, "localIP = \"127.0.0.1\"");
        Assert.AreEqual(1, host.Split("[[proxies]]").Length - 1);
        Assert.IsFalse(host.Contains("\n[[visitors]]"));
        var visitor = FrpConfiguration.Visitor(node, p2p, 25566);
        StringAssert.Contains(visitor, "bindAddr = \"127.0.0.1\"");
        StringAssert.Contains(visitor, "bindPort = 25566");
        StringAssert.Contains(visitor, "serverName = \"" + p2p.ProxyName + "\"");
        Assert.IsFalse(JsonSerializer.Serialize(p2p).Contains("injected"));
        var tcp = FrpConfiguration.Host(node, new RoomConnection("frp", "public.example.com", 30000), 25565);
        StringAssert.Contains(tcp, "type = \"tcp\""); StringAssert.Contains(tcp, "remotePort = 30000");
        Assert.ThrowsExactly<ArgumentException>(() => FrpConfiguration.Visitor(node, p2p, 0));
    }

    [TestMethod]
    public void ConnectionWireFormatMatchesJavaNullAndAbsentFields()
    {
        var ipv6 = new RoomConnection("ipv6", "2606:4700::1", 25565);
        var frp = new RoomConnection("frp", "public.example.com", 25565);
        var p2p = FrpConfiguration.CreateP2p(new FrpNode("frps.example.com", 7000, "local-secret"));
        foreach (var connection in new[] { ipv6, frp, p2p })
        {
            var json = JsonSerializer.Serialize(connection);
            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual(connection.Mode == "p2p" ? 5 : 3, System.Linq.Enumerable.Count(doc.RootElement.EnumerateObject()));
            Assert.AreEqual(connection, JsonSerializer.Deserialize<RoomConnection>(json));
        }
        var response = "{\"mode\":\"frp\",\"address\":\"public.example.com\",\"port\":25565,\"serverHost\":null,\"serverPort\":null,\"proxyName\":null,\"secretKey\":null}";
        Assert.AreEqual(frp, JsonSerializer.Deserialize<RoomConnection>(response));
        Assert.IsFalse(p2p.ToString().Contains(p2p.SecretKey));
        Assert.IsFalse(Ticket().ToString().Contains("private-token"));
    }

    [TestMethod]
    public async Task NativeJobAndPrivateConfigAreCleanedOnVerificationFailure()
    {
        var executable = System.IO.Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe");
        Assert.IsTrue(System.IO.File.Exists(executable), "Tests require DOTNET_ROOT pointing to the SDK used to run them.");
        var before = new HashSet<string>(System.IO.Directory.GetDirectories(System.IO.Path.GetTempPath(), "mooncl-frp-*"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<System.IO.IOException>(() => FrpProcess.StartAsync(executable, "", timeout.Token));
        foreach (var directory in System.IO.Directory.GetDirectories(System.IO.Path.GetTempPath(), "mooncl-frp-*"))
            Assert.IsTrue(before.Contains(directory), "Private temporary config leaked: " + directory);
    }

    [TestMethod]
    public async Task FrpcMustBeExplicitExistingExecutable()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => FrpProcess.StartAsync("frpc.exe", "", CancellationToken.None));
    }
}
