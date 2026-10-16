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

    private const string BatchPath = "/open/v1/file/multi_get_download_url";
    private const long LargeSize = 235_008_058;

    private static Dictionary<string, object?> BatchItem(string fid = "owned-fid") => new()
    {
        ["fid"] = fid, ["file_name"] = "pack.mrpack", ["size"] = LargeSize, ["download_url"] = Signed
    };

    private static Handler BatchHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> download,
        string fid = "owned-fid", bool owner = false) => new(async (request, ct) =>
    {
        if (request.RequestUri!.Host == "drive-pc.quark.cn")
            return request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(new { code = 0, data = new { stoken = Stoken } })
                : Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = owner ? fid : "share-fid", dir = false, size = LargeSize, share_fid_token = "file-token" } } } });
        switch (request.RequestUri.AbsolutePath)
        {
            case "/open/v1/dir":
                return Json(new { status = 0, data = new { fid = "folder-fid" } });
            case "/open/v1/share/saveas":
                if (owner)
                {
                    var response = Json(new { status = -1, errno = 41017 });
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }
                return Json(new { status = 0, data = new { status = 2, fid } });
            default:
                return await download(request, ct);
        }
    });

    private static async Task AssertBatchRequest(HttpRequestMessage request, CancellationToken ct, string fid, string access = Access)
    {
        Assert.AreEqual(BatchPath, request.RequestUri!.AbsolutePath);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
        Assert.AreEqual(access, request.Headers.Authorization?.Parameter);
        Assert.IsFalse(request.Headers.Contains("Cookie"));
        var query = request.RequestUri.Query.TrimStart('?').Split('&');
        Assert.AreEqual(2, query.Length);
        Assert.IsTrue(query.Any(part => part.StartsWith("req_id=", StringComparison.Ordinal)));
        Assert.IsTrue(query.Contains("access_token=" + access));
        var tm = request.Headers.GetValues("x-pan-tm").Single();
        Assert.AreEqual(QuarkOpenClient.Sign("POST", BatchPath, tm), request.Headers.GetValues("x-pan-token").Single());
        Assert.AreEqual(QuarkOpenClient.ClientId, request.Headers.GetValues("x-pan-client-id").Single());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Assert.AreEqual(1, body.RootElement.EnumerateObject().Count());
        Assert.AreEqual(1, body.RootElement.GetProperty("fids").GetArrayLength());
        Assert.AreEqual(fid, body.RootElement.GetProperty("fids")[0].GetString());
    }

    [TestMethod]
    [DataRow("owned-fid", "owned-fid", false)]
    [DataRow("~request|tag", "~response|tag", false)]
    [DataRow("~request|tag", "raw-fid", false)]
    [DataRow("raw-fid", "~response|tag", false)]
    [DataRow("~share|tag", "~owner-response|tag", true)]
    public async Task BatchDownloadsLargeFileWithSessionAndOpaqueFids(string fid, string returnedFid, bool owner)
    {
        using var handler = BatchHandler(async (request, ct) =>
        {
            await AssertBatchRequest(request, ct, fid);
            return Json(new { status = 0, data = new[] { BatchItem(returnedFid) } });
        }, fid, owner);
        using var http = new HttpClient(handler);
        var owned = await new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None);
        Assert.AreEqual(Signed, owned.Download.Url);
        Assert.AreEqual(LargeSize, owned.ExpectedSize);
        Assert.AreEqual(0L, new QuarkOwnedDownload(owned.Download, owned.Session).ExpectedSize);
        Assert.AreEqual(!owner, owned.SavedToDrive);
        Assert.AreEqual("x_pan_client_id=third_party_agent; x_pan_access_token=" + Access, owned.Download.Headers["Cookie"]);
        Assert.IsFalse(owned.Download.Headers.ContainsKey("Authorization"));
        Assert.AreEqual(1, handler.Paths.Count(path => path == BatchPath));
        Assert.IsFalse(handler.Paths.Contains("/open/v1/file/get_download_url"));
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("multiple")]
    [DataRow("object")]
    [DataRow("null-item")]
    [DataRow("scalar-item")]
    [DataRow("fid")]
    [DataRow("missing-fid")]
    [DataRow("empty-fid")]
    [DataRow("numeric-fid")]
    [DataRow("name")]
    [DataRow("missing-name")]
    [DataRow("numeric-name")]
    [DataRow("size")]
    [DataRow("missing-size")]
    [DataRow("string-size")]
    [DataRow("zero-size")]
    [DataRow("http")]
    [DataRow("fragment")]
    [DataRow("userinfo")]
    [DataRow("missing-url")]
    [DataRow("directory")]
    public async Task BatchRejectsAmbiguousOrDifferentFileWithoutSingleFallback(string invalid)
    {
        var item = BatchItem();
        object? data = new[] { item };
        switch (invalid)
        {
            case "empty": data = Array.Empty<object>(); break;
            case "multiple": data = new[] { item, BatchItem("other-fid") }; break;
            case "object": data = item; break;
            case "null-item": data = new object?[] { null }; break;
            case "scalar-item": data = new[] { 123 }; break;
            case "fid": item["fid"] = "other-fid"; break;
            case "missing-fid": item.Remove("fid"); break;
            case "empty-fid": item["fid"] = ""; break;
            case "numeric-fid": item["fid"] = 123; break;
            case "name": item["file_name"] = "other.mrpack"; break;
            case "missing-name": item.Remove("file_name"); break;
            case "numeric-name": item["file_name"] = 123; break;
            case "size": item["size"] = LargeSize - 1; break;
            case "missing-size": item.Remove("size"); break;
            case "string-size": item["size"] = LargeSize.ToString(); break;
            case "zero-size": item["size"] = 0; break;
            case "http": item["download_url"] = "http://cdn.example/pack"; break;
            case "fragment": item["download_url"] = Signed + "#secret-download-query"; break;
            case "userinfo": item["download_url"] = "https://user:secret-download-query@cdn.example/pack"; break;
            case "missing-url": item.Remove("download_url"); break;
            case "directory": item["dir"] = true; break;
        }
        using var handler = BatchHandler((_, _) => Task.FromResult(Json(new { status = 0, data })));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() => new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None));
        Assert.IsTrue(ex.SavedToDrive);
        Redacted(ex);
        Assert.AreEqual(1, handler.Paths.Count(path => path == BatchPath));
        Assert.IsFalse(handler.Paths.Contains("/open/v1/file/get_download_url"));
    }

    [TestMethod]
    public async Task BatchRefreshRetriesWithRotatedBearerAndCookieAndPersistsImmediately()
    {
        var attempts = 0;
        QuarkOpenSession? persisted = null;
        using var handler = BatchHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/agent/v1/oauth/access_token/rotate")
            {
                Assert.IsNull(request.Headers.Authorization);
                Assert.IsFalse(request.RequestUri.Query.Contains("access_token=", StringComparison.Ordinal));
                return Json(new { status = 0, data = new { access_token = "rotated-access", refresh_token = "rotated-refresh", device_id = "device-2" } });
            }
            attempts++;
            await AssertBatchRequest(request, ct, "owned-fid", attempts == 1 ? Access : "rotated-access");
            if (attempts == 1)
            {
                var expired = Json(new { status = -1, errno = 11001 });
                expired.StatusCode = HttpStatusCode.BadRequest;
                return expired;
            }
            Assert.IsTrue(persisted?.Matches("rotated-access", "rotated-refresh") == true);
            return Json(new { status = 0, data = new[] { BatchItem() } });
        });
        using var http = new HttpClient(handler);
        var client = new QuarkOpenClient(http);
        client.SessionRefreshed += session => persisted = session;
        var owned = await client.ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None);
        Assert.AreEqual(2, attempts);
        Assert.AreSame(persisted, owned.Session);
        Assert.AreEqual("x_pan_client_id=third_party_agent; x_pan_access_token=rotated-access", owned.Download.Headers["Cookie"]);
    }

    [TestMethod]
    public async Task BatchCancellationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = BatchHandler(async (request, ct) =>
        {
            Assert.AreEqual(BatchPath, request.RequestUri!.AbsolutePath);
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(new { });
        });
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", cancellation.Token));
        Assert.AreEqual(cancellation.Token, ex.CancellationToken);
        Assert.AreEqual(1, handler.Paths.Count(path => path == BatchPath));
        Redacted(ex);
    }

    [TestMethod]
    public async Task AnonymousAuthorizationDoesNotReuseOwnedBearer()
    {
        using var handler = BatchHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == BatchPath)
                return Task.FromResult(Json(new { status = 0, data = new[] { BatchItem() } }));
            Assert.IsNull(request.Headers.Authorization);
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.IsFalse(request.RequestUri.Query.Contains("access_token=", StringComparison.Ordinal));
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/agent/v1/get_authorize_page_url" => Json(new { status = 0, data = new { authorize_page_url = "https://pan.quark.cn/open/authorize", page_code = "secret-page", device_id = "device-1" } }),
                "/agent/v1/oauth/get_aac_by_pagecode" => Json(new { status = 0, data = new { agent_auth_code = "auth-code" } }),
                "/agent/v1/oauth/agent_auth_code" => Json(new { status = 0, data = new { access_token = Access, refresh_token = Refresh, device_id = "device-1" } }),
                _ => throw new AssertFailedException(request.RequestUri.AbsolutePath)
            });
        });
        using var http = new HttpClient(handler);
        var client = new QuarkOpenClient(http);
        await client.ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None);
        var start = await client.BeginAuthorizeAsync(CancellationToken.None);
        var session = await client.PollAsync(start, CancellationToken.None);
        Assert.IsTrue(session?.Matches(Access, Refresh) == true);
        Assert.IsNull(http.DefaultRequestHeaders.Authorization);
    }

    private static Handler OwnShareHandler(HttpStatusCode saveStatus, bool wrongSize = false, bool owner = true, bool limited = false) => new(async (request, ct) =>
    {
        if (request.RequestUri!.Host == "drive-pc.quark.cn")
            return request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(new { code = 0, data = new { stoken = Stoken } })
                : Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 100, share_fid_token = "file-token" } } } });
        switch (request.RequestUri.AbsolutePath)
        {
            case "/open/v1/dir":
                return Json(new { status = 0, data = new { fid = "folder-fid" } });
            case "/open/v1/share/saveas":
                if (!owner) return SaveAs(await request.Content!.ReadAsStringAsync(ct));
                var rejected = Json(new { status = -1, errno = 41017, error_info = "用户禁止转存自己的分享 " + Stoken });
                rejected.StatusCode = saveStatus;
                return rejected;
            case "/open/v1/file/multi_get_download_url":
                using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                {
                    Assert.AreEqual(1, body.RootElement.GetProperty("fids").GetArrayLength());
                    Assert.AreEqual(owner ? "share-fid" : "owned-fid", body.RootElement.GetProperty("fids")[0].GetString());
                }
                if (limited)
                {
                    var limit = Json(new { status = -1, errno = 23018, error_info = "download file size limit[52428800]" });
                    limit.StatusCode = HttpStatusCode.BadRequest;
                    return limit;
                }
                return Json(new { status = 0, data = new[] { new { fid = owner ? "share-fid" : "owned-fid", file_name = "pack.mrpack", size = wrongSize ? 99 : 100, download_url = Signed } } });
            default:
                throw new AssertFailedException("Owner share must not poll a save task or search for a new copy.");
        }
    });

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.OK)]
    public async Task OwnShareDownloadsOriginalFileInsteadOfSavingAnotherCopy(HttpStatusCode saveStatus)
    {
        using var handler = OwnShareHandler(saveStatus);
        using var http = new HttpClient(handler);
        var owned = await new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None);
        Assert.AreEqual("pack.mrpack", owned.Download.FileName);
        Assert.AreEqual(Signed, owned.Download.Url);
        Assert.AreEqual(100L, owned.ExpectedSize);
        Assert.IsFalse(owned.SavedToDrive);
        Assert.AreEqual(1, handler.Paths.Count(path => path == "/open/v1/share/saveas"));
        Assert.AreEqual(1, handler.Paths.Count(path => path == "/open/v1/file/multi_get_download_url"));
        Assert.IsFalse(handler.Paths.Contains("/open/v1/file/get_download_url"));
        Assert.IsFalse(handler.Paths.Any(path => path is "/open/v1/task/query" or "/open/v1/file/list"));
    }

    [TestMethod]
    public async Task OwnShareStillRejectsWrongFileSizeWithoutClaimingItWasSaved()
    {
        using var handler = OwnShareHandler(HttpStatusCode.BadRequest, wrongSize: true);
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() => new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None));
        Assert.AreEqual("网盘文件大小与分享文件不一致，已停止下载。", ex.Message);
        Assert.IsFalse(ex.SavedToDrive);
        Redacted(ex);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DownloadLimitReportsServerSizeAndActualSaveState(bool owner)
    {
        using var handler = OwnShareHandler(HttpStatusCode.BadRequest, owner: owner, limited: true);
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() => new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None));
        Assert.AreEqual(23018, ex.ErrorCode);
        Assert.AreEqual(!owner, ex.SavedToDrive);
        StringAssert.Contains(ex.Message, "50 MiB");
        Redacted(ex);
    }

    [TestMethod]
    [DataRow(32003)]
    [DataRow(41008)]
    [DataRow(98765)]
    [DataRow(23018)]
    public async Task Http400RetainsBusinessCodeWithoutEchoingServerSecrets(int code)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Json(new { status = -1, errno = code, error_info = Access + " " + Stoken + " " + Signed });
            response.StatusCode = HttpStatusCode.BadRequest;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() => new QuarkOpenClient(http).BeginAuthorizeAsync(CancellationToken.None));
        Assert.AreEqual(code, ex.ErrorCode);
        Assert.IsFalse(ex.Message.Contains("HTTP 请求失败", StringComparison.Ordinal));
        Assert.AreEqual(1, handler.Calls);
        Redacted(ex);
    }

    [TestMethod]
    public async Task NonJsonHttp400KeepsSafeHttpFailure()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("<html>" + Access + " " + Signed + "</html>") }));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<QuarkOpenException>(() => new QuarkOpenClient(http).BeginAuthorizeAsync(CancellationToken.None));
        Assert.AreEqual(0, ex.ErrorCode);
        Assert.AreEqual("夸克开放平台 HTTP 请求失败（400）。", ex.Message);
        Redacted(ex);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RefreshedCredentialsPersistEvenWhenLaterRequestFails(bool serverExpired, bool httpError)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(), "quark-refresh-" + Guid.NewGuid().ToString("N"));
        var store = new QuarkOpenSessionStore(root, root);
        var rotated = false;
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/agent/v1/oauth/access_token/rotate")
            {
                rotated = true;
                return Task.FromResult(Json(new { status = 0, data = new { access_token = "rotated-access", refresh_token = "rotated-refresh", device_id = "device-1" } }));
            }
            if (request.RequestUri.Host == "drive-pc.quark.cn")
                return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                    ? Json(new { code = 0, data = new { stoken = Stoken } })
                    : Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 100, share_fid_token = "file-token" } } } }));
            if (!rotated)
            {
                var expired = Json(new { status = 1, errno = 11001, data = new { } });
                if (httpError) expired.StatusCode = HttpStatusCode.BadRequest;
                return Task.FromResult(expired);
            }
            Assert.IsTrue(new QuarkOpenSessionStore(root, root).Load()!.Matches("rotated-access", "rotated-refresh"));
            return Task.FromResult(Json(new { status = 1, errno = 32003, data = new { } }));
        });
        using var http = new HttpClient(handler);
        var client = new QuarkOpenClient(http);
        client.SessionRefreshed += store.Save;
        try
        {
            var initial = new QuarkOpenSession(Access, Refresh, "device-1", serverExpired ? 0 : 1, 0);
            store.Save(initial);
            await Assert.ThrowsExactlyAsync<QuarkOpenException>(() => client.ResolveOwnedAsync(initial, Share, "pack.mrpack", CancellationToken.None));
            Assert.IsTrue(rotated);
            Assert.IsTrue(new QuarkOpenSessionStore(root, root).Load()!.Matches("rotated-access", "rotated-refresh"));
            Assert.AreEqual(1, handler.Paths.Count(path => path == "/agent/v1/oauth/access_token/rotate"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SignUsesMethodPathTimeAndDocumentedKey()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "POST&/open/v1/file/multi_get_download_url&1700000000000&cf134812e2de4032bd1cb7c3727e84b3"))).ToLowerInvariant();
        Assert.AreEqual(expected, QuarkOpenClient.Sign("POST", "/open/v1/file/multi_get_download_url", "1700000000000"));
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
                Assert.IsNull(request.Headers.Authorization);
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                    return Json(new { code = 0, data = new { stoken = Stoken } });
                return Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 93_571_003, share_fid_token = "file-token" } } } });
            }
            Assert.AreEqual("open-api-drive.quark.cn", request.RequestUri.Host);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual(Access, request.Headers.Authorization?.Parameter);
            var tm = request.Headers.GetValues("x-pan-tm").Single();
            Assert.AreEqual(QuarkOpenClient.Sign(request.Method.Method, request.RequestUri.AbsolutePath, tm), request.Headers.GetValues("x-pan-token").Single());
            Assert.AreEqual(QuarkOpenClient.ClientId, request.Headers.GetValues("x-pan-client-id").Single());
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Assert.IsFalse(body.Contains("save_all", StringComparison.Ordinal));
            return request.RequestUri.AbsolutePath switch
            {
                "/open/v1/dir" => Json(new { status = 0, errno = 0, data = new { fid = "folder-fid" } }),
                "/open/v1/share/saveas" => SaveAs(body),
                "/open/v1/file/multi_get_download_url" => DownloadFor(body),
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task OwnedSaveWithoutFidListsFolderByExactName(bool opaqueParent)
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
                "/open/v1/file/list" => ListPage(body, listBodies, opaqueParent),
                "/open/v1/file/multi_get_download_url" => DownloadFor(body),
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

    private static HttpResponseMessage ListPage(string body, List<string> bodies, bool opaqueParent)
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
                    new { fid = "owned-fid", filename = "pack.mrpack", size = 93_571_003, parent_fid = opaqueParent ? "~encrypted-parent|tag" : "folder-fid", file_type = "1" }
                }
            }
        });
    }

    private static HttpResponseMessage DownloadFor(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.AreEqual(1, json.RootElement.GetProperty("fids").GetArrayLength());
        Assert.AreEqual("owned-fid", json.RootElement.GetProperty("fids")[0].GetString());
        Assert.AreEqual(1, json.RootElement.EnumerateObject().Count());
        return Json(new { status = 0, errno = 0, data = new[] { new { fid = "owned-fid", file_name = "pack.mrpack", size = 93_571_003, download_url = Signed } } });
    }

    [TestMethod]
    [DataRow(false, "0", false, false)]
    [DataRow(true, "actual-folder", false, false)]
    [DataRow(true, "actual-folder", true, false)]
    [DataRow(true, "actual-folder", true, true)]
    public async Task OwnedSaveUsesReturnedDestinationEvenWhenTaskOmitsIt(bool pending, string destination, bool destinationInTask, bool taskOnly)
    {
        var queried = false;
        var queryCount = 0;
        var listed = new List<string>();
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "drive-pc.quark.cn")
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                    return Json(new { code = 0, data = new { stoken = Stoken } });
                return Json(new { data = new { list = new[] { new { file_name = "pack.mrpack", fid = "share-fid", dir = false, size = 93_571_003, share_fid_token = "file-token" } } } });
            }
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/open/v1/dir":
                    return Json(new { status = 0, data = new { fid = "folder-fid" } });
                case "/open/v1/share/saveas":
                    if (taskOnly) return Json(new { status = 0, data = new { task_id = "task-1", task_sync = false } });
                    return Json(new { status = 0, data = new { status = pending ? 1 : 2, task_id = "task-1", save_as = new { to_pdir_fid = destinationInTask ? "folder-fid" : destination } } });
                case "/open/v1/task/query":
                    queried = true;
                    queryCount++;
                    if (taskOnly && queryCount == 1) return Json(new { status = 0, data = new { status = 1, task_id = "task-1" } });
                    return destinationInTask
                        ? Json(new { status = 0, data = new { status = 2, task_id = "task-1", save_as = new { to_pdir_fid = destination } } })
                        : Json(new { status = 0, data = new { status = 2, task_id = "task-1" } });
                case "/open/v1/file/list":
                    using (var json = JsonDocument.Parse(body))
                        listed.Add(json.RootElement.GetProperty("parent_fid").GetString()!);
                    return Json(new { status = 0, data = new { last_page = true, file_list = listed.Last() == destination
                        ? new[] { new { fid = "owned-fid", filename = "pack.mrpack", size = 93_571_003, parent_fid = destination, file_type = 1 } }
                        : Array.Empty<object>() } });
                case "/open/v1/file/multi_get_download_url":
                    return DownloadFor(body);
                default:
                    throw new AssertFailedException(request.RequestUri.AbsolutePath);
            }
        });
        using var http = new HttpClient(handler);
        var owned = await new QuarkOpenClient(http).ResolveOwnedAsync(Session(), Share, "pack.mrpack", CancellationToken.None);
        Assert.AreEqual("pack.mrpack", owned.Download.FileName);
        CollectionAssert.AreEqual(new[] { destination }, listed);
        Assert.AreEqual(pending, queried);
        Assert.AreEqual(pending ? taskOnly ? 2 : 1 : 0, queryCount);
        Assert.AreEqual(1, handler.Paths.Count(path => path == "/open/v1/share/saveas"));
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
        Assert.IsFalse(handler.Paths.Any(path => path.Contains("get_download_url", StringComparison.Ordinal)));
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
