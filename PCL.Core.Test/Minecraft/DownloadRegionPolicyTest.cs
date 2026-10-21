using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class DownloadRegionPolicyTest
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Text(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8) };

    private static HttpClient Client(Handler handler) => new(handler);

    private Func<CancellationToken, Task<string?>>? _originalDivert;

    [TestInitialize]
    public void Setup()
    {
        DownloadRegionPolicy.ResetCache();
        _originalDivert = DownloadRegionPolicy.DetectMinecraftDivertAsync;
        DownloadRegionPolicy.DetectMinecraftDivertAsync = _ => Task.FromResult<string?>(null);
    }

    [TestCleanup]
    public void Cleanup() => DownloadRegionPolicy.DetectMinecraftDivertAsync = _originalDivert!;

    [TestMethod]
    public void CloudflareTraceParsesLoc()
    {
        var body = "fl=1f2\nh=www.cloudflare.com\nip=203.0.113.9\nts=1\nloc=CN\ncolo=HKG\n";
        Assert.AreEqual("CN", DownloadRegionPolicy.ParseCloudflareTrace(body));
        Assert.AreEqual("HK", DownloadRegionPolicy.ParseCloudflareTrace(body.Replace("loc=CN", "loc=hk")));
        Assert.IsNull(DownloadRegionPolicy.ParseCloudflareTrace("fl=1f2\ncolo=HKG\n"));
        Assert.IsNull(DownloadRegionPolicy.ParseCloudflareTrace("loc=CHN\n"));
    }

    [TestMethod]
    public void IpSbParsesCountryCode()
    {
        Assert.AreEqual("JP", DownloadRegionPolicy.ParseIpSb("{\"country_code\":\"jp\",\"ip\":\"1.2.3.4\"}"));
        Assert.IsNull(DownloadRegionPolicy.ParseIpSb("{\"other\":1}"));
        Assert.IsNull(DownloadRegionPolicy.ParseIpSb("{\"country_code\":5}"));
        Assert.IsNull(DownloadRegionPolicy.ParseIpSb("not json"));
        Assert.IsNull(DownloadRegionPolicy.ParseIpSb("{\"country_code\":\"JPN\"}"));
    }

    [TestMethod]
    public void IpinfoParsesPlainCode()
    {
        Assert.AreEqual("CN", DownloadRegionPolicy.ParseIpinfo("cn\n"));
        Assert.IsNull(DownloadRegionPolicy.ParseIpinfo("<html>CN</html>"));
        Assert.IsNull(DownloadRegionPolicy.ParseIpinfo("USA"));
        Assert.IsNull(DownloadRegionPolicy.ParseIpinfo(""));
    }

    [TestMethod]
    public void IsBlockedMatchesSpec()
    {
        Assert.IsTrue(DownloadRegionPolicy.IsBlocked("CN"));
        Assert.IsTrue(DownloadRegionPolicy.IsBlocked("cn"));
        Assert.IsTrue(DownloadRegionPolicy.IsBlocked(null));
        Assert.IsFalse(DownloadRegionPolicy.IsBlocked("HK"));
        Assert.IsFalse(DownloadRegionPolicy.IsBlocked("MO"));
        Assert.IsFalse(DownloadRegionPolicy.IsBlocked("TW"));
        Assert.IsFalse(DownloadRegionPolicy.IsBlocked("US"));
    }

    [TestMethod]
    public async Task FallsBackToNextProvider()
    {
        var handler = new Handler(req => req.RequestUri!.Host switch
        {
            "www.cloudflare.com" => Text("boom", HttpStatusCode.InternalServerError),
            "api.ip.sb" => Text("{\"country_code\":\"US\"}"),
            _ => Text("CN"),
        });
        await DownloadRegionPolicy.EnsureAllowedAsync(Client(handler), CancellationToken.None);
        Assert.AreEqual(2, handler.Calls);
        Assert.AreEqual("api.ip.sb", handler.Last!.RequestUri!.Host);
    }

    [TestMethod]
    public async Task ThrowsWhenCn()
    {
        var divertCalls = 0;
        DownloadRegionPolicy.DetectMinecraftDivertAsync = _ =>
        {
            divertCalls++;
            return Task.FromResult<string?>(null);
        };
        var handler = new Handler(_ => Text("fl=1\nloc=CN\n"));
        var ex = await Assert.ThrowsExactlyAsync<DownloadRegionBlockedException>(
            () => DownloadRegionPolicy.EnsureAllowedAsync(Client(handler), CancellationToken.None));
        StringAssert.Contains(ex.Message, "中国大陆");
        Assert.AreEqual(1, divertCalls);
    }

    [TestMethod]
    public async Task ThrowsWhenAllProvidersFail()
    {
        var handler = new Handler(req => req.RequestUri!.Host switch
        {
            "www.cloudflare.com" => throw new HttpRequestException("down"),
            "api.ip.sb" => Text("not json"),
            _ => Text("<html>"),
        });
        var ex = await Assert.ThrowsExactlyAsync<DownloadRegionBlockedException>(
            () => DownloadRegionPolicy.EnsureAllowedAsync(Client(handler), CancellationToken.None));
        StringAssert.Contains(ex.Message, "无法确认");
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task CachesSuccessfulDetection()
    {
        var handler = new Handler(_ => Text("fl=1\nloc=HK\n"));
        var http = Client(handler);
        await DownloadRegionPolicy.EnsureAllowedAsync(http, CancellationToken.None);
        await DownloadRegionPolicy.EnsureAllowedAsync(http, CancellationToken.None);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task FailureIsNotCached()
    {
        var calls = 0;
        var handler = new Handler(_ => { calls++; return calls <= 3 ? Text("x", HttpStatusCode.InternalServerError) : Text("fl=1\nloc=JP\n"); });
        var http = Client(handler);
        await Assert.ThrowsExactlyAsync<DownloadRegionBlockedException>(
            () => DownloadRegionPolicy.EnsureAllowedAsync(http, CancellationToken.None));
        await DownloadRegionPolicy.EnsureAllowedAsync(http, CancellationToken.None);
        Assert.AreEqual(4, calls); // 首次三家全失败，第二次首源即返回 JP
    }

    [TestMethod]
    public async Task CnButDivertedIsAllowed()
    {
        DownloadRegionPolicy.DetectMinecraftDivertAsync = _ => Task.FromResult<string?>("fake-ip");
        var handler = new Handler(_ => Text("fl=1\nloc=CN\n"));
        await DownloadRegionPolicy.EnsureAllowedAsync(Client(handler), CancellationToken.None);
    }

    [TestMethod]
    public async Task UnknownButDivertedIsAllowed()
    {
        DownloadRegionPolicy.DetectMinecraftDivertAsync = _ => Task.FromResult<string?>("经代理 127.0.0.1:7890");
        var handler = new Handler(req => req.RequestUri!.Host switch
        {
            "www.cloudflare.com" => throw new HttpRequestException("down"),
            "api.ip.sb" => Text("not json"),
            _ => Text("<html>"),
        });
        await DownloadRegionPolicy.EnsureAllowedAsync(Client(handler), CancellationToken.None);
    }

    [TestMethod]
    public async Task NonCnSkipsDivertCheck()
    {
        var divertCalls = 0;
        DownloadRegionPolicy.DetectMinecraftDivertAsync = _ =>
        {
            divertCalls++;
            return Task.FromResult<string?>("fake-ip");
        };
        var handler = new Handler(_ => Text("fl=1\nloc=HK\n"));
        await DownloadRegionPolicy.EnsureAllowedAsync(Client(handler), CancellationToken.None);
        Assert.AreEqual(0, divertCalls);
    }

    [TestMethod]
    public void IsFakeIpMatchesRange()
    {
        Assert.IsTrue(DownloadRegionPolicy.IsFakeIp(IPAddress.Parse("198.18.0.1")));
        Assert.IsTrue(DownloadRegionPolicy.IsFakeIp(IPAddress.Parse("198.19.255.255")));
        Assert.IsTrue(DownloadRegionPolicy.IsFakeIp(IPAddress.Parse("::ffff:198.18.0.65")));
        Assert.IsFalse(DownloadRegionPolicy.IsFakeIp(IPAddress.Parse("198.17.255.255")));
        Assert.IsFalse(DownloadRegionPolicy.IsFakeIp(IPAddress.Parse("198.20.0.1")));
        Assert.IsFalse(DownloadRegionPolicy.IsFakeIp(IPAddress.Parse("150.171.110.105")));
        Assert.IsFalse(DownloadRegionPolicy.IsFakeIp(IPAddress.Parse("2001:db8::1")));
    }

    [TestMethod]
    public void GetRouteSourceForLoopback()
    {
        Assert.AreEqual(IPAddress.Loopback, DownloadRegionPolicy.GetRouteSource(IPAddress.Loopback));
    }
}
