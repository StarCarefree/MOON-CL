using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackTransferTest
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Response { get; set; } = (_, _) => new(HttpStatusCode.OK) { Content = new StringContent("pack-content") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Response(request, cancellationToken));
    }

    [TestMethod]
    public async Task VerifiedDownloadBecomesFinalFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new Handler());
        try
        {
            var path = Path.Combine(root, "pack.zip");
            await new PackTransfer(http).DownloadAsync("http://test/pack.zip", path,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pack-content"))), HashAlgorithmName.SHA256, null, CancellationToken.None);
            Assert.AreEqual("pack-content", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".partial"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task HashFailurePreservesExistingTargetAndRemovesPartial()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var http = new HttpClient(new Handler());
        try
        {
            var path = Path.Combine(root, "pack.zip"); File.WriteAllText(path, "old");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new PackTransfer(http).DownloadAsync("http://test/pack.zip", path,
                new string('0', 64), HashAlgorithmName.SHA256, null, CancellationToken.None));
            Assert.AreEqual("old", File.ReadAllText(path)); Assert.IsFalse(File.Exists(path + ".partial"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationNeverCommitsTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new Handler { Response = (_, _) => { cts.Cancel(); return new(HttpStatusCode.OK) { Content = new StringContent("pack-content") }; } });
        try
        {
            var path = Path.Combine(root, "pack.zip");
            await Assert.ThrowsAsync<OperationCanceledException>(() => new PackTransfer(http).DownloadAsync("http://test/pack.zip", path, "", HashAlgorithmName.SHA256, null, cts.Token));
            Assert.IsFalse(File.Exists(path)); Assert.IsFalse(File.Exists(path + ".partial"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MetadataIsSizeBounded()
    {
        using var http = new HttpClient(new Handler());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new PackTransfer(http).ReadTextAsync(new Uri("http://test/catalog"), CancellationToken.None, 4));
    }
}
