using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackCatalogClientTest
{
    private const string Primary = "https://primary.example/index.json";
    private const string Backup = "https://backup.example/index.json";
    private const string Catalog = """{"packs":[{"id":"sample","name":"测试","version":"1.0","downloadUrl":"files/pack.zip"}]}""";
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    [TestMethod]
    public void LegacyUrlMigratesAndExplicitEmptyClears()
    {
        CollectionAssert.AreEqual(new[] { Primary }, PackCatalogSources.Normalize(null, Primary));
        Assert.AreEqual(0, PackCatalogSources.Normalize([], Primary).Length);
        CollectionAssert.AreEqual(new[] { Primary, Backup }, PackCatalogSources.Normalize([" " + Primary + " ", "", Primary, Backup], "https://old.example/index.json"));
        var old = JsonSerializer.Deserialize<Dictionary<string, string>>("""{"CatalogUrl":"https://primary.example/index.json"}""")!;
        CollectionAssert.AreEqual(new[] { Primary }, PackCatalogSources.Normalize(null, old["CatalogUrl"]));
    }

    [TestMethod]
    [DataRow("file:///C:/index.json")]
    [DataRow("https://user:pass@example.com/index.json")]
    [DataRow("https://example.com/index.json#fragment")]
    [DataRow("relative/index.json")]
    public void RejectsUnsafeSource(string url) => Assert.ThrowsExactly<InvalidDataException>(() => PackCatalogSources.Normalize([url]));

    [TestMethod]
    public void LimitsSourcesAndAddressLength()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => PackCatalogSources.Normalize(System.Linq.Enumerable.Range(0, 9).Select(i => $"https://source{i}.example/index.json")));
        Assert.ThrowsExactly<InvalidDataException>(() => PackCatalogSources.Normalize([Primary + new string('a', 2048)]));
    }

    [TestMethod]
    public async Task HttpFailureFallsBackAndResolvesBackupRelativeLinks()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler((request, token) => {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(calls.Count == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(Catalog));
        }));
        var result = await new PackCatalogClient(http).FetchAsync([Primary, Backup], CancellationToken.None);
        CollectionAssert.AreEqual(new[] { Primary, Backup }, calls);
        Assert.AreEqual(Backup, result.SourceUrl);
        Assert.AreEqual("https://backup.example/files/pack.zip", result.Packs[0].DownloadUrl);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{\"packs\":{}}")]
    [DataRow("{\"wrong\":[]}")]
    [DataRow("{\"packs\":[{\"id\":\"../bad\"}]}")]
    public async Task InvalidDirectoryFallsBack(string invalid)
    {
        var count = 0;
        using var http = new HttpClient(new Handler((request, token) => Task.FromResult(Json(++count == 1 ? invalid : Catalog))));
        Assert.AreEqual(Backup, (await new PackCatalogClient(http).FetchAsync([Primary, Backup], CancellationToken.None)).SourceUrl);
    }

    [TestMethod]
    public async Task ValidEmptyDirectoryDoesNotContactBackup()
    {
        var count = 0;
        using var http = new HttpClient(new Handler((request, token) => { count++; return Task.FromResult(Json("{\"packs\":[]}")); }));
        var result = await new PackCatalogClient(http).FetchAsync([Primary, Backup], CancellationToken.None);
        Assert.AreEqual(0, result.Packs.Count);
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task UsesEtagAnd304WithoutReparsingOrFetchingBackup()
    {
        var count = 0;
        using var http = new HttpClient(new Handler((request, token) => {
            if (++count == 1) { var response = Json(Catalog); response.Headers.ETag = new("\"catalog-1\""); return Task.FromResult(response); }
            Assert.AreEqual("\"catalog-1\"", string.Join("", request.Headers.GetValues("If-None-Match")));
            Assert.IsTrue(request.Headers.CacheControl!.NoCache);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }));
        var client = new PackCatalogClient(http);
        var first = await client.FetchAsync([Primary, Backup], CancellationToken.None);
        var second = await client.FetchAsync([Primary, Backup], CancellationToken.None);
        Assert.IsTrue(second.NotModified);
        Assert.AreSame(first.Packs, second.Packs);
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public async Task RedirectedDirectoryIsTheRelativeLinkBase()
    {
        using var http = new HttpClient(new Handler((request, token) => {
            var response = Json(Catalog);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example/dir/index.json");
            return Task.FromResult(response);
        }));
        var result = await new PackCatalogClient(http).FetchAsync([Primary], CancellationToken.None);
        Assert.AreEqual("https://cdn.example/dir/files/pack.zip", result.Packs[0].DownloadUrl);
    }

    [TestMethod]
    public async Task TimeoutFallsBackButUserCancellationStopsImmediately()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) => {
            calls++;
            if (request.RequestUri!.Host == "primary.example") await Task.Delay(Timeout.Infinite, token);
            return Json(Catalog);
        }));
        var client = new PackCatalogClient(http, TimeSpan.FromMilliseconds(30));
        Assert.AreEqual(Backup, (await client.FetchAsync([Primary, Backup], CancellationToken.None)).SourceUrl);
        calls = 0;
        using var cancel = new CancellationTokenSource(); cancel.CancelAfter(10);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.FetchAsync([Primary, Backup], cancel.Token));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task OversizedBodyAndUnexpected304FallBack()
    {
        foreach (var mode in new[] { 0, 1 }) {
            using var http = new HttpClient(new Handler((request, token) => {
                if (request.RequestUri!.Host == "backup.example") return Task.FromResult(Json(Catalog));
                return Task.FromResult(mode == 0 ? Json(new string('x', 4 * 1024 * 1024 + 1)) : new HttpResponseMessage(HttpStatusCode.NotModified));
            }));
            Assert.AreEqual(Backup, (await new PackCatalogClient(http).FetchAsync([Primary, Backup], CancellationToken.None)).SourceUrl);
        }
    }

    [TestMethod]
    public async Task AllFailuresExposeErrorWithoutDiscardingValidatedCache()
    {
        var fail = false;
        using var http = new HttpClient(new Handler((request, token) => Task.FromResult(fail ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Json(Catalog))));
        var client = new PackCatalogClient(http);
        var old = await client.FetchAsync([Primary], CancellationToken.None);
        fail = true;
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.FetchAsync([Primary, Backup], CancellationToken.None));
        StringAssert.Contains(exception.Message, "全部 2 个目录源");
        Assert.AreEqual(1, old.Packs.Count);
    }

    [TestMethod]
    public void EmptyConfigurationFallsBackToTheShippedDefaultSources()
    {
        CollectionAssert.AreEqual(PackCatalogSources.DefaultUrls, PackCatalogSources.Effective([]));
        CollectionAssert.AreEqual(PackCatalogSources.DefaultUrls, PackCatalogSources.Effective(null));
        CollectionAssert.AreEqual(new[] { Primary }, PackCatalogSources.Effective([Primary]));
        CollectionAssert.AreEqual(PackCatalogSources.DefaultUrls, PackCatalogSources.Normalize(PackCatalogSources.DefaultUrls));
    }

    // Verifies the shipped index.json through the real parser: the default source must
    // resolve to a committed file that carries the SRE pack for a fresh install.
    [TestMethod]
    public void ShippedDefaultSourceCarriesTheStarRailPack()
    {
        Assert.IsTrue(File.Exists(ShippedCatalog()), ShippedCatalog());
        Assert.AreEqual(2, PackCatalogSources.DefaultUrls.Length);
        foreach (var url in PackCatalogSources.DefaultUrls) StringAssert.Contains(url, "pack-catalog/index.json");
        var packs = PackCatalog.Parse(File.ReadAllText(ShippedCatalog()), new Uri(PackCatalogSources.DefaultUrls[0]));
        var sre = packs.Single(p => p.Id == PackCatalog.StarRailId);
        Assert.AreEqual("StarRailHarpyExpress", sre.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sre.Version));
        Assert.AreEqual("1.21.1", sre.Minecraft);
        Assert.IsTrue(sre.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        StringAssert.StartsWith(sre.DownloadUrl, "https://gitee.com/starrailexpress/starrailexpress/releases/download/modpack/");
        StringAssert.StartsWith(sre.ShareUrl, "https://pan.quark.cn/s/");
    }

    private static string ShippedCatalog(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "docs", "pack-catalog", "index.json"));
}
