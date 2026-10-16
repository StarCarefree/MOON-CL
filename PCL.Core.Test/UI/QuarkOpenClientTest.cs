using System;
using System.Collections.Generic;
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
public sealed class QuarkOpenClientTest
{
    private const string Share = "https://pan.quark.cn/s/e3f623dd58d6";
    private const string Access = "owned-access-token";
    private const string Refresh = "owned-refresh-token";
    private const string Stoken = "secret-share-token";
    private const string Signed = "https://cdn.example/owned?sig=secret-download-query";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Paths.Add(request.RequestUri!.AbsolutePath);
            return respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private static QuarkOpenSession Session() => new(Access, Refresh, "device-1", 0, 0);

    private static void Redacted(Exception exception)
    {
        var text = exception.ToString();
        Assert.IsFalse(text.Contains(Access, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains(Refresh, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains(Stoken, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("secret-download-query", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("secret-page", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SignUsesMethodPathTimeAndDocumentedKey()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "POST&/open/v1/file/get_download_url&1700000000000&cf134812e2de4032bd1cb7c3727e84b3"))).ToLowerInvariant();
        Assert.AreEqual(expected, QuarkOpenClient.Sign("POST", "/open/v1/file/get_download_url", "1700000000000"));
    }

    [TestMethod]
    public async Task OwnedSaveSendsOneFileAndSessionCookie()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.IsFalse(request.RequestUri!.Query.Contains("fr=pc", StringComparison.Ordinal));
            if (request.RequestUri.Host == "drive-pc.quark.cn")
            {
                Assert.IsTrue(request.RequestUri.Query.Contains("fr=win", StringComparison.Ordinal));
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                    return Json(new { code = 0, data = new { stoken = Stoken } });
                return Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 93_571_003, share_fid_token = "file-token" } } } });
            }
            Assert.AreEqual("open-api-drive.quark.cn", request.RequestUri.Host);
            var tm = request.Headers.GetValues("x-pan-tm").Single();
            Assert.AreEqual(QuarkOpenClient.Sign(request.Method.Method, request.RequestUri.AbsolutePath, tm), request.Headers.GetValues("x-pan-token").Single());
            Assert.AreEqual(QuarkOpenClient.ClientId, request.Headers.GetValues("x-pan-client-id").Single());
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Assert.IsFalse(body.Contains("save_all", StringComparison.Ordinal));
            return request.RequestUri.AbsolutePath switch
            {
                "/open/v1/dir" => Json(new { status = 0, errno = 0, data = new { fid = "folder-fid" } }),
                "/open/v1/share/saveas" => SaveAs(body),
                "/open/v1/file/get_download_url" => Json(new { status = 0, errno = 0, data = new { fid = "owned-fid", file_name = "pack.mrpack", size = 93_571_003, download_url = Signed } }),
                _ => throw new AssertFailedException(request.RequestUri.AbsolutePath)
            };
        });
        using var http = new HttpClient(handler);
        var owned = await new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None);
        Assert.AreEqual("pack.mrpack", owned.ToString());
        Assert.AreEqual("x_pan_client_id=third_party_agent; x_pan_access_token=" + Access, owned.Download.Headers["Cookie"]);
        Assert.IsFalse(handler.Paths.Any(path => path.Contains("save_all", StringComparison.Ordinal)));
        Assert.IsTrue(owned.Session.Matches(Access, Refresh));
    }

    private static HttpResponseMessage SaveAs(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.IsFalse(root.TryGetProperty("save_all", out _));
        Assert.AreEqual(1, root.GetProperty("fid_list").GetArrayLength());
        Assert.AreEqual("share-fid", root.GetProperty("fid_list")[0].GetString());
        Assert.AreEqual("file-token", root.GetProperty("fid_token_list")[0].GetString());
        Assert.AreEqual("folder-fid", root.GetProperty("to_pdir_fid").GetString());
        return Json(new { status = 0, errno = 0, data = new { status = 2, task_id = "task-1", save_as = new { save_as_top_fids = new[] { "owned-fid" } } } });

    }
    [TestMethod]
    public async Task OwnedSaveWithoutFidListsFolderByExactName()
    {
        var listBodies = new List<string>();
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "drive-pc.quark.cn")
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                    return Json(new { code = 0, data = new { stoken = Stoken } });
                return Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 93_571_003, share_fid_token = "file-token" } } } });
            }
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Assert.IsFalse(body.Contains("save_all", StringComparison.Ordinal));
            return request.RequestUri.AbsolutePath switch
            {
                "/open/v1/dir" => Json(new { status = 0, errno = 0, data = new { fid = "folder-fid" } }),
                "/open/v1/share/saveas" => SaveAsWithoutFid(body),
                "/open/v1/file/list" => ListPage(body, listBodies),
                "/open/v1/file/get_download_url" => DownloadFor(body),
                _ => throw new AssertFailedException(request.RequestUri.AbsolutePath)
            };
        });
        using var http = new HttpClient(handler);
        var owned = await new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None);
        Assert.AreEqual("pack.mrpack", owned.ToString());
        Assert.AreEqual(2, listBodies.Count);
        using var first = JsonDocument.Parse(listBodies[0]);
        Assert.AreEqual("folder-fid", first.RootElement.GetProperty("parent_fid").GetString());
        Assert.AreEqual("updated_at:desc", first.RootElement.GetProperty("sort").GetString());
        Assert.IsFalse(first.RootElement.TryGetProperty("category", out _));
        Assert.IsFalse(first.RootElement.TryGetProperty("query_cursor", out _));
        using var second = JsonDocument.Parse(listBodies[1]);
        Assert.AreEqual("page-2", second.RootElement.GetProperty("query_cursor").GetProperty("token").GetString());
        Assert.IsFalse(handler.Paths.Any(path => path.Contains("file/search", StringComparison.Ordinal)));
        Assert.IsFalse(handler.Paths.Any(path => path.Contains("save_all", StringComparison.Ordinal)));
    }

    private static HttpResponseMessage SaveAsWithoutFid(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.AreEqual(1, root.GetProperty("fid_list").GetArrayLength());
        Assert.AreEqual("share-fid", root.GetProperty("fid_list")[0].GetString());
        Assert.AreEqual("folder-fid", root.GetProperty("to_pdir_fid").GetString());
        return Json(new { status = 0, errno = 0, data = new { status = 2, task_id = "task-1" } });
    }

    private static HttpResponseMessage ListPage(string body, List<string> bodies)
    {
        bodies.Add(body);
        if (bodies.Count == 1)
            return Json(new
            {
                status = 0,
                errno = 0,
                data = new
                {
                    last_page = false,
                    next_query_cursor = new { version = 1, token = "page-2" },
                    file_list = new object[]
                    {
                        new { fid = "other", filename = "notes.zip", size = 93_571_003, parent_fid = "folder-fid", file_type = 1 },
                        new { fid = "elsewhere", filename = "pack.mrpack", size = 93_571_003, parent_fid = "other-folder", file_type = 1 }
                    }
                }
            });
        return Json(new
        {
            status = 0,
            errno = 0,
            data = new
            {
                last_page = true,
                file_list = new object[]
                {
                    new { fid = "dir-fid", filename = "pack.mrpack", size = 93_571_003, parent_fid = "folder-fid", file_type = 0 },
                    new { fid = "wrong-size", file_name = "pack.mrpack", size = 12, parent_fid = "folder-fid", file_type = 1 },
                    new { fid = "owned-fid", filename = "pack.mrpack", size = 93_571_003, pdir_fid = "folder-fid", file_type = 1 }
                }
            }
        });
    }

    private static HttpResponseMessage DownloadFor(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.AreEqual("owned-fid", json.RootElement.GetProperty("fid").GetString());
        return Json(new { status = 0, errno = 0, data = new { fid = "owned-fid", file_name = "pack.mrpack", size = 93_571_003, download_url = Signed } });
    }

    [TestMethod]
    public async Task OwnedSaveWithoutFidStopsWhenFolderMissesFile()
    {
        var lists = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "drive-pc.quark.cn")
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                    return Json(new { code = 0, data = new { stoken = Stoken } });
                return Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 100, share_fid_token = "file-token" } } } });
            }
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (request.RequestUri.AbsolutePath == "/open/v1/dir")
                return Json(new { status = 0, errno = 0, data = new { fid = "folder-fid" } });
            if (request.RequestUri.AbsolutePath == "/open/v1/share/saveas")
                return SaveAsWithoutFid(body);
            if (request.RequestUri.AbsolutePath == "/open/v1/file/list")
            {
                lists++;
                return Json(new { status = 0, errno = 0, data = new { last_page = true, file_list = Array.Empty<object>() } });
            }
            throw new AssertFailedException(request.RequestUri.AbsolutePath);
        });
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() =>
            new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None));
        Redacted(ex);
        Assert.IsTrue(ex.SavedToDrive);
        Assert.AreEqual("已转存，但没有找到目标文件，已停止下载。", ex.Message);
        Assert.AreEqual(3, lists);
        Assert.IsFalse(handler.Paths.Any(path => path.Contains("get_download_url", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OwnedSaveWithoutFidStopsWhenFolderMatchIsNotUnique()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "drive-pc.quark.cn")
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                    return Json(new { code = 0, data = new { stoken = Stoken } });
                return Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 100, share_fid_token = "file-token" } } } });
            }
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (request.RequestUri.AbsolutePath == "/open/v1/dir")
                return Json(new { status = 0, errno = 0, data = new { fid = "folder-fid" } });
            if (request.RequestUri.AbsolutePath == "/open/v1/share/saveas")
                return SaveAsWithoutFid(body);
            if (request.RequestUri.AbsolutePath == "/open/v1/file/list")
                return Json(new
                {
                    status = 0,
                    errno = 0,
                    data = new
                    {
                        last_page = true,
                        file_list = new[]
                        {
                            new { fid = "owned-a", filename = "pack.mrpack", size = 100, parent_fid = "folder-fid", file_type = 1 },
                            new { fid = "owned-b", filename = "pack.mrpack", size = 100, parent_fid = "folder-fid", file_type = 1 }
                        }
                    }
                });
            throw new AssertFailedException(request.RequestUri.AbsolutePath);
        });
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() =>
            new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None));
        Redacted(ex);
        Assert.IsTrue(ex.SavedToDrive);
        Assert.AreEqual("转存结果不唯一，已停止下载。", ex.Message);
        Assert.AreEqual(1, handler.Paths.Count(path => path == "/open/v1/file/list"));
    }

    [TestMethod]
    public async Task RefusesOversizedShareWithoutSave()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new
        {
            code = 0,
            data = new { stoken = Stoken, list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 2_200_000_000L, share_fid_token = "file-token" } } }
        })));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() =>
            new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None));
        Redacted(ex);
        Assert.IsFalse(handler.Paths.Any(path => path.Contains("saveas", StringComparison.Ordinal)));
        Assert.IsFalse(ex.SavedToDrive);
    }

    [TestMethod]
    public async Task BeginAuthorizeRejectsNonPanPageWithoutEchoingIt()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new
        {
            status = 0,
            errno = 0,
            data = new { authorize_page_url = "https://evil.example/auth?page_code=secret-page", page_code = "secret-page", device_id = "device-1" }
        })));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() => new QuarkOpenClient(http).BeginAuthorizeAsync(CancellationToken.None));
        Redacted(ex);
        Assert.IsFalse(ex.ToString().Contains("evil.example", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PollWithoutCodeDoesNotExchange()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new { status = 0, errno = 0, data = new { status = 1, agent_auth_code = "" } })));
        using var http = new HttpClient(handler);
        var start = new QuarkAuthorizeStart("https://pan.quark.cn/open/authorize", "secret-page", "device-1");
        Assert.IsNull(await new QuarkOpenClient(http).PollAsync(start, CancellationToken.None));
        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual("QuarkAuthorizeStart", start.ToString());
    }

    [TestMethod]
    public async Task OpenFailureDoesNotEchoToken()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "drive-pc.quark.cn")
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                    return Json(new { code = 0, data = new { stoken = Stoken } });
                return Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 100, share_fid_token = "file-token" } } } });
            }
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (request.RequestUri.AbsolutePath == "/open/v1/dir")
                return Json(new { status = 0, errno = 0, data = new { fid = "folder-fid" } });
            Assert.IsFalse(body.Contains("save_all", StringComparison.Ordinal));
            return Json(new { status = 1, errno = 41008, error_info = "stoken " + Stoken });
        });
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() =>
            new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None));
        Redacted(ex);
        Assert.IsFalse(ex.SavedToDrive);
    }

    [TestMethod]
    public void SessionFileDoesNotStorePlainToken()
    {
        var root = Directory.Exists(@"I:\tmp-pcl") ? @"I:\tmp-pcl" : Path.GetTempPath();
        var dir = Path.Combine(root, "mooncl-quark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.bin");
        try
        {
            QuarkOpenSessionFile.Save(path, Session());
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.IsFalse(text.Contains(Access, StringComparison.Ordinal));
            Assert.IsFalse(text.Contains(Refresh, StringComparison.Ordinal));
            var loaded = QuarkOpenSessionFile.TryLoad(path);
            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.Matches(Access, Refresh));
            Assert.AreEqual("QuarkOpenSession", loaded.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }
}
