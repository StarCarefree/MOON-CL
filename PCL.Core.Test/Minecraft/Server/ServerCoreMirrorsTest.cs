using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Server;

namespace PCL.Core.Test.Minecraft.Server;

[TestClass]
public sealed class ServerCoreMirrorsTest
{
    private static ServerCoreArtifact Artifact(ServerLoader loader = ServerLoader.Paper) => new()
    {
        Loader = loader, MinecraftVersion = "1.21.10", Build = "130", JavaMajor = 21,
        ChecksumAlgorithm = "sha256", Checksum = new string('a', 64), Size = 123,
        Urls = ["https://fill-data.papermc.io/server.jar"], MetadataSource = "official"
    };

    [TestMethod]
    public async Task MirrorOrderingRetainsOfficialChecksumAndIdentity()
    {
        using var handler = new Handler(_ => Json("Paper", "build130"));
        using var http = new HttpClient(handler);
        foreach (var source in new[] { 0, 1 })
        {
            var original = Artifact();
            var result = await ServerCoreMirrors.AddAsync(http, original, source, true, default);
            Assert.AreEqual(2, result.Urls.Count);
            StringAssert.Contains(result.Urls[source == 0 ? 0 : 1], "download.fastmirror.net");
            Assert.AreEqual(original.Checksum, result.Checksum);
            Assert.AreEqual(original.ChecksumAlgorithm, result.ChecksumAlgorithm);
            Assert.AreEqual(original.Build, result.Build);
        }
    }

    [TestMethod]
    public async Task OfficialOnlyNeverQueriesMirrorAndFailureKeepsOfficial()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var original = Artifact();
        var only = await ServerCoreMirrors.AddAsync(http, original, 2, true, default);
        Assert.AreSame(original, only);
        Assert.AreEqual(0, handler.Requests.Count);
        var failed = await ServerCoreMirrors.AddAsync(http, original, 0, true, default);
        CollectionAssert.AreEqual(new[] { original.Urls[0] }, new List<string>(failed.Urls));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task FloatingPurpurMirrorRequiresLatestSelection()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("buildlatest") ? Json("Purpur", "buildlatest") : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var pinned = await ServerCoreMirrors.AddAsync(http, Artifact(ServerLoader.Purpur), 0, false, default);
        Assert.AreEqual(1, pinned.Urls.Count);
        var latest = await ServerCoreMirrors.AddAsync(http, Artifact(ServerLoader.Purpur), 0, true, default);
        Assert.AreEqual(2, latest.Urls.Count);
        Assert.AreEqual(new string('a', 64), latest.Checksum);
    }

    [TestMethod]
    public async Task WrongCoreMetadataCannotAddDownload()
    {
        using var handler = new Handler(_ => Json("Purpur", "build130"));
        using var http = new HttpClient(handler);
        var result = await ServerCoreMirrors.AddAsync(http, Artifact(), 0, true, default);
        Assert.AreEqual(1, result.Urls.Count);
    }

    private static HttpResponseMessage Json(string core, string build) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { success = true, data = new { name = core, mc_version = "1.21.10", core_version = build, download_url = $"https://download.fastmirror.net/download/{core}/1.21.10/{build}" } }))
    };
    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
