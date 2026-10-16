using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class QuarkPackClientTest
{
    private const string Share = "https://pan.quark.cn/s/e3f623dd58d6";
    private const string Secret = "secret-share-token+/=";
    private const string Signed = "https://cdn.example/pack?signature=secret-signed-query";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static object Item(string name, string fid, bool directory = false) => new { file_name = name, fid, dir = directory, size = 123, share_fid_token = "file-token" };
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    private static void Redacted(Exception exception)
    {
        Assert.IsFalse(exception.ToString().Contains(Secret, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("secret-signed-query", StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("file-token", StringComparison.Ordinal));
        Assert.IsNull(exception.InnerException);
    }

    [TestMethod]
    public async Task OfficialWalksExactPathAndPagesWithWinGuestProtocol()
    {
        var step = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.AreEqual("drive-pc.quark.cn", request.RequestUri!.Host);
            var query = Query(request.RequestUri);
            Assert.AreEqual("ucpro", query["pr"]);
            Assert.AreEqual("win", query["fr"]);
            Assert.AreEqual("https://pan.quark.cn/", request.Headers.Referrer!.AbsoluteUri);
            Assert.AreEqual("https://pan.quark.cn", request.Headers.GetValues("Origin").Single());
            StringAssert.Contains(request.Headers.UserAgent.ToString(), "Mozilla/5.0");
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.IsFalse(request.Headers.Contains("Authorization"));
            switch (step++)
            {
                case 0:
                    Assert.AreEqual(HttpMethod.Post, request.Method);
                    Assert.AreEqual("/1/clouddrive/share/sharepage/token", request.RequestUri.AbsolutePath);
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                    {
                        Assert.AreEqual("e3f623dd58d6", body.RootElement.GetProperty("pwd_id").GetString());
                        Assert.AreEqual("abcd", body.RootElement.GetProperty("passcode").GetString());
                        Assert.IsTrue(body.RootElement.GetProperty("support_visit_limit_private_share").GetBoolean());
                    }
                    return Json(new { code = 0, data = new { stoken = Secret } });
                case 1:
                    AssertDetail("0", "1");
                    return Json(new { data = new { list = Enumerable.Range(0, 100).Select(i => Item("other" + i, "other" + i, true)).ToArray() } });
                case 2:
                    AssertDetail("0", "2");
                    return Json(new { data = new { list = new[] { Item("SRE哈比列车", "folder", true) } } });
                case 3:
                    AssertDetail("folder", "1");
                    return Json(new { data = new { list = new[] { Item("4.4.0", "version", true) } } });
                case 4:
                    AssertDetail("version", "1");
                    return Json(new { data = new { list = new[] { Item(PackCatalog.TestSharePath.Split('/')[^1], "target"), Item("other.zip", "other") } } });
                case 5:
                    Assert.AreEqual(HttpMethod.Post, request.Method);
                    Assert.AreEqual("/1/clouddrive/file/download", request.RequestUri.AbsolutePath);
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                    {
                        var root = body.RootElement;
                        Assert.AreEqual("target", root.GetProperty("fids")[0].GetString());
                        Assert.AreEqual("file-token", root.GetProperty("fids_token")[0].GetString());
                        Assert.AreEqual(Secret, root.GetProperty("stoken").GetString());
                        Assert.AreEqual("e3f623dd58d6", root.GetProperty("pwd_id").GetString());
                        Assert.AreEqual("41060", root.GetProperty("support_error_code")[0].GetString());
                    }
                    return Json(new { status = 200, code = 0, data = new[] { new { download_url = Signed } } });
                default: Assert.Fail("Resolver must not fetch CDN content."); return Json(new { });
            }

            void AssertDetail(string parent, string page)
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                Assert.AreEqual("/1/clouddrive/share/sharepage/detail", request.RequestUri.AbsolutePath);
                Assert.AreEqual("e3f623dd58d6", query["pwd_id"]);
                Assert.AreEqual(Secret, query["stoken"]);
                Assert.AreEqual(parent, query["pdir_fid"]);
                Assert.AreEqual(page, query["_page"]);
                Assert.AreEqual("100", query["_size"]);
                Assert.AreEqual("file_type:asc,file_name:asc", query["_sort"]);
            }
        });
        using var http = new HttpClient(handler);
        var result = await new QuarkPackClient(http).ResolveAsync("", Share + "?pwd=abcd", PackCatalog.TestSharePath, CancellationToken.None);
        Assert.AreEqual(6, handler.Calls);
        Assert.AreEqual(Signed, result.Url);
        Assert.AreEqual(PackCatalog.TestSharePath.Split('/')[^1], result.FileName);
        Assert.AreEqual("https://pan.quark.cn/", result.Headers["Referer"]);
        Assert.IsFalse(result.ToString()!.Contains("secret-signed-query", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ServicePostsContractAndOnlyAllowsDownloadHeaders()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.AreEqual("https://resolver.example/api/resolve", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Assert.AreEqual(Share, body.RootElement.GetProperty("shareUrl").GetString());
            Assert.AreEqual("folder/pack.zip", body.RootElement.GetProperty("path").GetString());
            return Json(new { url = Signed, fileName = "pack.zip", headers = new Dictionary<string, string>
            {
                ["cookie"] = "server-cookie", ["REFERER"] = "https://pan.quark.cn/", ["User-Agent"] = "browser",
                ["Authorization"] = "never-forward", ["X-U-Drive-Secret-Auth"] = "never-forward", ["Host"] = "evil.example"
            } });
        });
        using var http = new HttpClient(handler);
        var result = await new QuarkPackClient(http).ResolveAsync("https://resolver.example/api/", Share, "folder/pack.zip", CancellationToken.None);
        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(3, result.Headers.Count);
        Assert.AreEqual("server-cookie", result.Headers["Cookie"]);
        Assert.AreEqual("browser", result.Headers["User-Agent"]);
    }

    [TestMethod]
    [DataRow("http://pan.quark.cn/s/abc", "pack.zip")]
    [DataRow("https://pan.quark.cn.evil/s/abc", "pack.zip")]
    [DataRow("https://user@pan.quark.cn/s/abc", "pack.zip")]
    [DataRow("https://pan.quark.cn/s/abc/../def", "pack.zip")]
    [DataRow("https://pan.quark.cn/s/abc?token=secret", "pack.zip")]
    [DataRow("https://pan.quark.cn/s/abc#fragment", "pack.zip")]
    [DataRow(Share, "../pack.zip")]
    [DataRow(Share, "/pack.zip")]
    [DataRow(Share, "folder//pack.zip")]
    [DataRow(Share, "folder/../pack.zip")]
    [DataRow(Share, "C:\\pack.zip")]
    [DataRow(Share, "pack.zip:ads")]
    [DataRow(Share, "pack.exe")]
    [DataRow(Share, "pack.zip ")]
    public async Task RejectsUnsafeInputBeforeNetwork(string share, string path)
    {
        using var handler = new Handler((_, _) => { Assert.Fail("Invalid input must not reach network."); return Task.FromResult(Json(new { })); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new QuarkPackClient(http).ResolveAsync("", share, path, CancellationToken.None));
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    [DataRow("../pack.zip", Signed, "ok")]
    [DataRow("pack.exe", Signed, "ok")]
    [DataRow("pack.zip", "file:///C:/secret-signed-query", "ok")]
    [DataRow("pack.zip", "https://user:secret-signed-query@cdn.example/p", "ok")]
    [DataRow("pack.zip", Signed, "bad\r\nInjected: yes")]
    public async Task RejectsUnsafeResolverResults(string name, string url, string header)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { url, fileName = name, headers = new { Cookie = header } }))));
        var ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new QuarkPackClient(http).ResolveAsync("https://resolver.example", Share, "pack.zip", CancellationToken.None));
        Redacted(ex);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("case")]
    [DataRow("duplicate")]
    [DataRow("directory")]
    [DataRow("no-file-token")]
    [DataRow("cross-page-duplicate")]
    [DataRow("page-limit")]
    public async Task RefusesMissingAmbiguousOrIncompleteListings(string scenario)
    {
        var pages = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.AreEqual("/1/clouddrive/share/sharepage/token", request.RequestUri!.AbsolutePath);
                return Task.FromResult(Json(new { data = new { stoken = Secret } }));
            }
            pages++;
            object[] list = scenario switch
            {
                "missing" => [],
                "case" => [Item("PACK.ZIP", "x")],
                "duplicate" => [Item("pack.zip", "a"), Item("pack.zip", "b")],
                "directory" => [Item("pack.zip", "a", true)],
                "no-file-token" => [new { file_name = "pack.zip", fid = "a", dir = false }],
                "cross-page-duplicate" when pages == 1 => new[] { Item("pack.zip", "a") }.Concat(Enumerable.Range(1, 99).Select(i => Item("other" + i, "x"))).ToArray(),
                "cross-page-duplicate" => [Item("pack.zip", "b")],
                _ => Enumerable.Range(0, 100).Select(i => Item("other" + i, "x")).ToArray()
            };
            return Task.FromResult(Json(new { data = new { list } }));
        });
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new QuarkPackClient(http).ResolveAsync("", Share, "pack.zip", CancellationToken.None));
        Redacted(ex);
        Assert.AreEqual(scenario == "page-limit" ? 100 : scenario == "cross-page-duplicate" ? 2 : 1, pages);
    }

    [TestMethod]
    [DataRow("http")]
    [DataRow("api")]
    [DataRow("json")]
    [DataRow("transport")]
    public async Task ErrorsNeverExposeBodiesTokensOrSignedQueries(string failure)
    {
        using var handler = new Handler((_, _) => failure switch
        {
            "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { ReasonPhrase = Secret, Content = new StringContent(Secret + Signed + "file-token") }),
            "api" => Task.FromResult(Json(new { code = 23018, message = Secret + Signed + "file-token" })),
            "json" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"" + Secret + Signed + "file-token") }),
            _ => throw new HttpRequestException(Secret + Signed, new IOException("file-token"))
        });
        using var http = new HttpClient(handler);
        if (failure is "http" or "transport")
        {
            var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new QuarkPackClient(http).ResolveAsync("", Share, "pack.zip", CancellationToken.None));
            Redacted(ex);
            if (failure == "http") Assert.AreEqual(HttpStatusCode.BadRequest, ex.StatusCode);
        }
        else Redacted(await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new QuarkPackClient(http).ResolveAsync("", Share, "pack.zip", CancellationToken.None)));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task JsonLimitAppliesToContentLengthAndStreaming(bool knownLength)
    {
        using var handler = new Handler((_, _) =>
        {
            var bytes = new byte[4 * 1024 * 1024 + 1];
            HttpContent content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new NonSeekStream(bytes));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new QuarkPackClient(http).ResolveAsync("", Share, "pack.zip", CancellationToken.None));
    }

    private sealed class NonSeekStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [TestMethod]
    public async Task CallerCancellationIsPreservedAndRedacted()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new Handler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(new { });
        });
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => new QuarkPackClient(http).ResolveAsync("", Share, "pack.zip", cts.Token));
        Assert.AreEqual(cts.Token, ex.CancellationToken);
        Redacted(ex);
        await Assert.ThrowsAsync<OperationCanceledException>(() => new QuarkPackClient(http).ResolveAsync("", Share, "pack.zip", cts.Token));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task ResolveHasThirtySecondTimeoutWithoutChangingSharedHttpClient()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(new { }); });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsExactlyAsync<TimeoutException>(() => new QuarkPackClient(http).ResolveAsync("", Share, "pack.zip", CancellationToken.None));
        Redacted(ex);
        Assert.IsTrue(watch.Elapsed >= TimeSpan.FromSeconds(29));
        Assert.AreEqual(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [TestMethod]
    public async Task ResolvedUrlStillReportsRealTransferHttp412WithoutCreatingFile()
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Json(new { url = Signed, fileName = "pack.zip", headers = new { } })
            : new HttpResponseMessage(HttpStatusCode.PreconditionFailed))));
        var result = await new QuarkPackClient(http).ResolveAsync("https://resolver.example", Share, "pack.zip", CancellationToken.None);
        var root = Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? throw new InvalidOperationException("PI_SCRATCH_DIR required for test artifacts.");
        var target = Path.Combine(root, "quark-test-" + Guid.NewGuid().ToString("N") + ".zip");
        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new PackTransfer(http).DownloadAsync(result.Url, target, "", HashAlgorithmName.SHA256, null, CancellationToken.None, result.Headers));
         Assert.AreEqual(HttpStatusCode.PreconditionFailed, ex.StatusCode);
         Assert.IsFalse(ex.Message.Contains(Signed, StringComparison.Ordinal));
         Assert.IsTrue(ex.Message.Contains("412", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(target));
        Assert.IsFalse(File.Exists(target + ".partial"));
    }

    [TestMethod]
    public async Task ListReturnsFoldersAndPacksWithoutTokensOrDownload()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.AreEqual("drive-pc.quark.cn", request.RequestUri!.Host);
            Assert.AreEqual("win", Query(request.RequestUri)["fr"]);
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.IsFalse(request.RequestUri.AbsolutePath.Contains("file/download", StringComparison.Ordinal));
            Assert.IsFalse(request.RequestUri.AbsolutePath.Contains("save", StringComparison.OrdinalIgnoreCase));
            if (request.Method == HttpMethod.Post) return Task.FromResult(Json(new { code = 0, data = new { stoken = Secret } }));
            return Task.FromResult(Json(new { data = new { list = new object[] { Item("1.20.1", "folder", true), Item("pack.mrpack", "file"), Item("readme.txt", "note"), Item("..", "bad") } } }));
        });
        using var http = new HttpClient(handler);
        var listing = await new QuarkPackClient(http).ListAsync(Share, " ", CancellationToken.None);
        var json = JsonSerializer.Serialize(listing);
        Assert.IsFalse(json.Contains(Secret, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("file-token", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("stoken", StringComparison.Ordinal));
        Assert.AreEqual("", listing.ToString());
        Assert.AreEqual(3, listing.Entries.Count);
        Assert.IsTrue(listing.Entries[0].Directory);
        Assert.AreEqual("pack.mrpack", listing.Entries[1].ToString());
        Assert.AreEqual(123, listing.Entries[1].Size);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task ListRefusesTraversalWithoutRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not request"));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new QuarkPackClient(http).ListAsync(Share, "folder/../pack.mrpack", CancellationToken.None));
        Assert.IsFalse(ex.ToString().Contains(Secret, StringComparison.Ordinal));
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task ListStopsAtMaxEntries()
    {
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(Json(new { code = 0, data = new { stoken = Secret } }));
            var page = int.Parse(Query(request.RequestUri!)["_page"]);
            return Task.FromResult(Json(new { data = new { list = Enumerable.Range(0, 100).Select(i => Item($"pack-{page}-{i}.mrpack", "f")).ToArray() } }));
        });
        using var http = new HttpClient(handler);
        var listing = await new QuarkPackClient(http).ListAsync(Share, null, CancellationToken.None);
        Assert.IsTrue(listing.Truncated);
        Assert.AreEqual(QuarkShareListing.MaxEntries, listing.Entries.Count);
        Assert.AreEqual(3, handler.Calls);
        Assert.IsFalse(JsonSerializer.Serialize(listing).Contains(Secret, StringComparison.Ordinal));
    }
}
