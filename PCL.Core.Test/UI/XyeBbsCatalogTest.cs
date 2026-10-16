using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class XyeBbsCatalogTest
{
    private const string Page = "https://www.xyebbs.com/res-id/BloodStain/downloads";
    private const string Share = "https://pan.quark.cn/s/6cd997045e93?pwd=z3fw";
    private const string Identity = """{"data":{"id":1482,"identify":"BloodStain","resourceTypeId":2},"code":200}""";
    private const string Releases = """
        {"data":{"data":[
          {"label":"0.7Zbeta","versions":[{"name":"1.20.1"}],"cores":[{"name":"Forge"}],"links":[{"linkType":"QUARK","displayType":"URL","url":"https://pan.quark.cn/s/6cd997045e93?pwd=z3fw"}]},
          {"label":"empty","links":[]},
          {"label":"other","links":[{"linkType":"URL","url":"https://pan.quark.cn/s/aaaaaaaaaa"}]},
          {"label":"evil","links":[{"linkType":"QUARK","url":"https://pan.quark.cn.evil/s/bbbbbbbbbb"}]},
          {"label":"secret","links":[{"linkType":"QUARK","url":"https://pan.quark.cn/s/cccccccccc?token=secret"}]},
          {"label":"0.6.9\nnote","versions":[{"name":"1.20.1"}],"cores":[{"name":"Forge"}],"links":[{"linkType":"quark","url":"https://pan.quark.cn/s/c24968a3fc94?pwd=K3GF"}]}
        ],"totalPages":2},"code":200}
        """;

    [TestMethod]
    public void DownloadPageAcceptsHashAndRequiresTheEncyclopediaLink()
    {
        Assert.IsTrue(XyeBbsCatalog.TryParse(Page + "#resource-download-section", out var identify));
        Assert.AreEqual("BloodStain", identify);
        Assert.AreEqual(Page + "#resource-download-section", XyeBbsCatalog.MatchPage([Page], "https://xyebbs.com/res-id/BloodStain#resource-download-section"));
        Assert.IsNull(XyeBbsCatalog.MatchPage([Page], "https://www.xyebbs.com/res-id/OtherPack/downloads"));
        Assert.IsNull(XyeBbsCatalog.MatchPage(null, Page));
    }

    [TestMethod]
    [DataRow("http://www.xyebbs.com/res-id/BloodStain/downloads")]
    [DataRow("https://user:secret@www.xyebbs.com/res-id/BloodStain/downloads")]
    [DataRow("https://www.xyebbs.com.evil/res-id/BloodStain/downloads")]
    [DataRow("https://xyebbs.com.evil/res-id/BloodStain/downloads")]
    [DataRow("https://www.xyebbs.com/res-id/BloodStain/downloads?token=secret")]
    [DataRow("https://www.xyebbs.com/res-id/BloodStain/downloads/../admin")]
    [DataRow("https://www.xyebbs.com/resources/1482/releases")]
    [DataRow("https://www.xyebbs.com/res-id/BloodStain/downloads/files")]
    public void RejectsUnsafeProjectPages(string url)
    {
        Assert.IsFalse(XyeBbsCatalog.TryParse(url, out _));
        Assert.IsNull(XyeBbsCatalog.MatchPage([Page, url], url));
    }

    [TestMethod]
    public void IdentityRequiresAModpackAndReleasesKeepOnlyQuarkShares()
    {
        Assert.AreEqual(1482, XyeBbsCatalog.ParseIdentity(Identity));
        Assert.ThrowsExactly<InvalidDataException>(() => XyeBbsCatalog.ParseIdentity("""{"data":null,"code":404}"""));
        Assert.ThrowsExactly<InvalidDataException>(() => XyeBbsCatalog.ParseIdentity("""{"data":{"id":9,"resourceTypeId":1},"code":200}"""));
        var versions = XyeBbsCatalog.ParseReleases(Releases, out var pages);
        Assert.AreEqual(2, pages);
        Assert.AreEqual(2, versions.Count);
        Assert.AreEqual("0.7Zbeta", versions[0].Label);
        Assert.AreEqual("1.20.1", versions[0].Minecraft);
        Assert.AreEqual("Forge", versions[0].Loader);
        Assert.AreEqual(Share, versions[0].ShareUrl);
        Assert.AreEqual("0.6.9 note", versions[1].Label);
        Assert.IsFalse(versions[0].ToString().Contains("pwd", StringComparison.Ordinal));
        Assert.AreEqual("https://resource-api.xyeidc.com/client/resources/identify/BloodStain", XyeBbsCatalog.IdentityAddress("BloodStain").AbsoluteUri);
        Assert.AreEqual("https://resource-api.xyeidc.com/client/resources/1482/releases?page=1&includes=versions%3Bcores%3Blinks", XyeBbsCatalog.ReleasesAddress(1482, 1).AbsoluteUri);
    }

    [TestMethod]
    public async Task ListUsesOfficialApiDoesNotFollowRedirectsAndCaches()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.AreEqual("resource-api.xyeidc.com", request.RequestUri!.Host);
            Assert.AreEqual("https://www.xyebbs.com/", request.Headers.Referrer!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath.EndsWith("/BloodStain", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Identity) };
            Assert.AreEqual(XyeBbsCatalog.ReleasesAddress(1482, 1).AbsoluteUri, request.RequestUri.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Releases.Replace("\"totalPages\":2", "\"totalPages\":1")) };
        }));
        var catalog = new XyeBbsCatalog(http);
        var first = await catalog.ListAsync(Page + "#resource-download-section");
        var second = await catalog.ListAsync(XyeBbsCatalog.Canonical("BloodStain"));
        Assert.AreEqual(Share, first.Versions[0].ShareUrl);
        Assert.AreSame(first, second);
        Assert.AreEqual(2, calls);

        using var redirected = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/share") } }));
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new XyeBbsCatalog(redirected).ListAsync(Page));
        Assert.AreEqual(HttpStatusCode.Redirect, error.StatusCode);
        Assert.IsFalse(error.ToString().Contains("evil.example", StringComparison.Ordinal));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
}
