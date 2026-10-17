using System;
using System.Collections.Generic;
using System.Linq;
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
public sealed class RoomV2Test
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request);
    }
    private static RoomSnapshot Room(params RoomRoute[] routes) => new("ABCDEFGH", "test", "host",
        [new("host", "owner", true, "", new("实例", "1.21.1", "launching"))], routes.FirstOrDefault()?.Connection, DateTimeOffset.UtcNow.AddSeconds(75), routes);
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static RoomConnection EasyTier() => new("easytier", "10.144.14.1", 25565, SecretKey: "test-network-secret", NetworkName: "test-network", Peers: ["tcp://127.0.0.1:11010"]);

    [TestMethod]
    public void OverlayDescriptorsRoundTripAndCompareSemantically()
    {
        foreach (var connection in new[] { EasyTier(), new RoomConnection("zerotier", "10.23.0.1", 25565, NetworkId: "0123456789abcdef") })
        {
            connection.Validate(); var json = JsonSerializer.Serialize(connection);
            var copy = JsonSerializer.Deserialize<RoomConnection>(json)!;
            Assert.AreEqual(connection, copy); Assert.AreEqual(connection.GetHashCode(), copy.GetHashCode());
            Assert.IsFalse(json.Contains("Token", StringComparison.OrdinalIgnoreCase));
        }
        var et = EasyTier();
        Assert.AreEqual(et, et with { Peers = et.Peers!.ToArray() });
        Assert.ThrowsExactly<ArgumentException>(() => (et with { Peers = ["tcp://user:password@example.com:11010"] }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (et with { Peers = ["file:///C:/test"] }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new RoomConnection("zerotier", "10.0.0.1", 25565, NetworkId: "invalid").Validate());
    }
    [TestMethod]
    public void RouteBatchRejectsDuplicatesAndBoundsWhileAllowingEmpty()
    {
        var route = new RoomRoute("one", "线路一", new("frp", "example.com", 25565));
        RoomRoute.ValidateAll([]); RoomRoute.ValidateAll([route]);
        Assert.ThrowsExactly<ArgumentException>(() => RoomRoute.ValidateAll([route, route]));
        Assert.ThrowsExactly<ArgumentException>(() => RoomRoute.ValidateAll(Enumerable.Range(0, 13).Select(i => route with { Id = "r" + i }).ToArray()));
        Assert.ThrowsExactly<ArgumentException>(() => (route with { Id = "../route" }).Validate());
    }
    [TestMethod]
    public void AvatarAndGameLimitsRejectUntrustedMetadata()
    {
        new RoomPresence().Validate(); new RoomGameStatus("test", "1.21.1", "running").Validate();
        Assert.ThrowsExactly<ArgumentException>(() => new RoomPresence("https://tracker.example.com/avatar.png").Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new RoomPresence(new string('A', 8193)).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new RoomGameStatus("", "", "invented").Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new RoomGameStatus(new string('x', 81)).Validate());
        var tiny = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/k9sAAAAASUVORK5CYII=");
        new RoomPresence(Convert.ToBase64String(tiny)).Validate();
        tiny[16] = 1;
        Assert.ThrowsExactly<ArgumentException>(() => new RoomPresence(Convert.ToBase64String(tiny)).Validate());
    }
    [TestMethod]
    public void PackShareAcceptsPublicDownloadsAndRejectsLocalPaths()
    {
        var pack = new RoomPackShare("starrail-harpy-express", "SRE", "4.4.0", "1.21.1", "pack.mrpack", "https://example.com/pack.mrpack", new string('a', 64), "", "folder/pack.mrpack");
        pack.Validate();
        new RoomPackShare("modrinth-abcd1234", "Simply Optimized", "1.21.11", "1.21.11", "Simply-Optimized.mrpack", "https://cdn.modrinth.com/data/AbCd1234/versions/x/Simply-Optimized.mrpack", new string('a', 128)).Validate();
        Assert.ThrowsExactly<ArgumentException>(() => (pack with { Sha256 = new string('a', 32) }).Validate());
        new RoomGameStatus("实例", "1.21.1", "running", pack).Validate();
        Assert.ThrowsExactly<ArgumentException>(() => (pack with { DownloadUrl = "file:///C:/packs/a.mrpack", ShareUrl = "" }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (pack with { DownloadUrl = "https://user:secret@example.com/a.mrpack" }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (pack with { SharePath = "../secret.mrpack" }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (pack with { FileName = "pack.exe" }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new RoomGameStatus("实例", "1.21.1", "running", pack with { Id = "../pack" }).Validate());
        new RoomGameStatus("实例", "1.21.1", "running", null, "play.example.com:25566").Validate();
        Assert.ThrowsExactly<ArgumentException>(() => new RoomGameStatus("实例", "1.21.1", "running", null, "bad server").Validate());
    }
    [TestMethod]
    public async Task OfficialNodeCanUseTemporaryHttpButNeverLeaksItsAddressInErrors()
    {
        var address = new Uri(RoomServiceDirectory.OfficialUrl);
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("failed to connect " + address.Authority)));
        using var api = new RoomApiClient(RoomServiceDirectory.OfficialUrl, http);
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => api.CreateAsync(new("test", "owner", ""), CancellationToken.None));
        Assert.IsFalse(error.ToString().Contains(address.Host));
        var display = RoomServiceDirectory.FriendlyError(new Exception(address.AbsoluteUri), true);
        Assert.IsFalse(display.Contains(address.Host));
        Assert.IsTrue(display.Contains(RoomServiceDirectory.OfficialLabel));
    }
    [TestMethod]
    public async Task RoutesAndPresenceUseAuthenticatedProtocolAndPreparingRoomNeedsNoTransport()
    {
        var routes = new[] { new RoomRoute("direct", "直连", new("ipv6", "2606:4700::1", 25565)), new RoomRoute("overlay", "组网", EasyTier()) };
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (request.RequestUri.AbsolutePath.EndsWith("/rooms"))
            {
                Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("connection").ValueKind);
                if (body.RootElement.GetProperty("name").GetString() == "probe")
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "Invalid game server" }) };
                return Ok(new RoomTicket(Room(), "host", "local-token"));
            }
            Assert.AreEqual("local-token", request.Headers.Authorization!.Parameter);
            if (request.RequestUri.AbsolutePath.EndsWith("/routes"))
                Assert.AreEqual(2, body.RootElement.GetProperty("routes").GetArrayLength());
            else Assert.AreEqual("running", body.RootElement.GetProperty("game").GetProperty("state").GetString());
            return Ok(Room(routes));
        }));
        using var session = new RoomSession(new RoomApiClient("https://example.com", http));
        await session.CreateAsync(new("test", "owner", ""), CancellationToken.None);
        Assert.AreEqual(0, session.Room!.EffectiveRoutes.Length);
        await session.PublishRoutesAsync(routes, CancellationToken.None);
        await session.RefreshAsync(new RoomPresence("", new("游戏", "1.21.1", "running")), CancellationToken.None);
        Assert.AreEqual(2, session.Room!.EffectiveRoutes.Length);
        Assert.AreEqual("launching", session.Room.Members[0].Game!.State);
        Assert.AreEqual(4, requests.Count);
    }
    [TestMethod]
    public void AutoSelectionUsesGameReachabilityNotIcmpOrLoopbackSpeed()
    {
        RoomRouteProbeResult[] results = [new("failed", false, null, 0, "", "no game"), new("slow", true, 90, 1, "1.21.1", ""), new("fast", true, 20, null, "1.21.1", "")];
        Assert.AreEqual("fast", RoomRouteSelector.Choose(results)!.RouteId);
        Assert.AreEqual("slow", RoomRouteSelector.Choose(results, "slow")!.RouteId);
        Assert.IsNull(RoomRouteSelector.Choose(results, "failed"));
        Assert.IsNull(RoomRouteSelector.Choose([]));
    }
    [TestMethod]
    public void EasyTierPlanIsNoTunPrivateAndSecretIsNotInCommandLine()
    {
        var connection = EasyTier(); var plan = EasyTierRoomConfiguration.Build(connection, false, 25565, 25566, 29001);
        var args = string.Join(' ', plan.Arguments);
        StringAssert.Contains(args, "--no-tun true"); StringAssert.Contains(args, "--bind-device false");
        StringAssert.Contains(args, "--rpc-portal 127.0.0.1:29001");
        StringAssert.Contains(args, "tcp://127.0.0.1:25566/10.144.14.1:25565");
        Assert.IsFalse(args.Contains(connection.SecretKey));
        Assert.AreEqual(connection.SecretKey, plan.Environment["ET_NETWORK_SECRET"]);
        Assert.IsFalse(plan.ToString().Contains(connection.SecretKey));
        Assert.ThrowsExactly<ArgumentException>(() => EasyTierRoomConfiguration.Build(connection, true, 25564, 0, 29001));
    }
    [TestMethod]
    public async Task IndependentDirectLeasesAreOwnedAndDisposedSeparately()
    {
        using var factory = new RoomTransportFactory();
        var route = new RoomRoute("first", "第一条", new("frp", "127.0.0.1", 25565));
        using var first = await factory.CreateAsync(route, "ABCDEFGH", false, 0, new(), CancellationToken.None);
        using var second = await factory.CreateAsync(route with { Id = "second" }, "ABCDEFGH", false, 0, new(), CancellationToken.None);
        first.Dispose(); Assert.IsFalse(first.IsAlive); Assert.IsTrue(second.IsAlive);
        factory.Dispose(); Assert.IsFalse(second.IsAlive); Assert.IsNull(second.Target);
        await Assert.ThrowsAsync<OperationCanceledException>(() => factory.CreateAsync(route, "ABCDEFGH", false, 0, new(), CancellationToken.None));
    }
}
