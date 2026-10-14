using System;
using System.IO;
using System.IO.Compression;
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
public sealed class ExternalPackUpdaterTest
{
    private sealed class Handler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
    private string _root = "";
    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "config", "mod-loading-screen"));
        File.WriteAllText(Path.Combine(_root, "config", "mod-loading-screen", "update.properties"), "autoUpdate=true\npackwizUrl=http://example.com/pack.toml\n");
        Directory.CreateDirectory(Path.Combine(_root, "mods")); File.WriteAllText(Path.Combine(_root, "mods", "old.jar"), "old");
        Directory.CreateDirectory(Path.Combine(_root, "saves")); File.WriteAllText(Path.Combine(_root, "saves", "world.dat"), "world");
        using var jar = ZipFile.Open(Path.Combine(_root, "mod-loading-screen-test.jar"), ZipArchiveMode.Create);
        jar.CreateEntry("io/github/gaming32/modloadingscreen/update/StandaloneUpdater.class");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task HashCheckUsesExactBytesAndSourceCache()
    {
        byte[] body = [0xef, 0xbb, 0xbf, 110, 97, 109, 101, 10];
        using var http = new HttpClient(new Handler(body));
        var updater = new ExternalPackUpdater(http);
        var plan = await updater.CheckAsync(_root, CancellationToken.None);
        Assert.IsNotNull(plan); Assert.AreEqual(Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), plan.Hash);
        var path = Path.Combine(_root, ".updatemod", "sources", "default", "packwiz.hash");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, plan.Hash);
        Assert.IsNull(await updater.CheckAsync(_root, CancellationToken.None));
    }

    [TestMethod]
    public async Task FailureRestoresManagedFilesAndLeavesSavesUntouched()
    {
        using var http = new HttpClient(new Handler(Encoding.UTF8.GetBytes("pack")));
        var updater = new ExternalPackUpdater(http, "java", (start, token) =>
        {
            Assert.IsTrue(start.ArgumentList.Contains("--gameDir=" + _root));
            File.WriteAllText(Path.Combine(_root, "mods", "old.jar"), "changed");
            File.WriteAllText(Path.Combine(_root, "mods", "new.jar"), "new");
            return Task.FromResult(1);
        });
        var plan = await updater.CheckAsync(_root, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(() => updater.ApplyAsync(_root, plan!, null, CancellationToken.None));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(_root, "mods", "old.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "mods", "new.jar")));
        Assert.AreEqual("world", File.ReadAllText(Path.Combine(_root, "saves", "world.dat")));
        Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(_root, ".launcher-backups")).Length);
    }

    [TestMethod]
    public async Task CancellationRestoresBeforeReturning()
    {
        using var http = new HttpClient(new Handler(Encoding.UTF8.GetBytes("pack")));
        using var cts = new CancellationTokenSource();
        var updater = new ExternalPackUpdater(http, "java", (_, token) =>
        {
            File.WriteAllText(Path.Combine(_root, "mods", "old.jar"), "changed");
            cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(0);
        });
        var plan = await updater.CheckAsync(_root, CancellationToken.None);
        await Assert.ThrowsAsync<OperationCanceledException>(() => updater.ApplyAsync(_root, plan!, null, cts.Token));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(_root, "mods", "old.jar")));
    }

    [TestMethod]
    public async Task SuccessRequiresUpdaterHashAcknowledgement()
    {
        using var http = new HttpClient(new Handler(Encoding.UTF8.GetBytes("pack")));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pack"))).ToLowerInvariant();
        var updater = new ExternalPackUpdater(http, "java", (_, _) =>
        {
            var path = Path.Combine(_root, ".updatemod", "sources", "default", "packwiz.hash");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, hash);
            File.WriteAllText(Path.Combine(_root, "mods", "new.jar"), "new"); return Task.FromResult(0);
        });
        var plan = await updater.CheckAsync(_root, CancellationToken.None);
        await updater.ApplyAsync(_root, plan!, null, CancellationToken.None);
        Assert.IsTrue(File.Exists(Path.Combine(_root, "mods", "new.jar")));
        Assert.IsNull(await updater.CheckAsync(_root, CancellationToken.None));
    }

    [TestMethod]
    public void ActiveSourceAndJavaEscapesAreRespected()
    {
        File.WriteAllText(Path.Combine(_root, "config", "mod-loading-screen", "update.properties"),
            "sources=alpha,beta\nactiveSource=beta\nautoUpdate=true\nsource.beta.packwizUrl=http\\://test/\\\n  pack.toml\n");
        var source = ExternalPackUpdater.ReadSource(_root);
        Assert.IsNotNull(source); Assert.AreEqual("beta", source.Id); Assert.AreEqual("http://test/pack.toml", source.Url); Assert.IsTrue(source.Enabled);
    }

    [TestMethod]
    public void DisabledUpdateSourceIsNotEnabledByLauncher()
    {
        File.WriteAllText(Path.Combine(_root, "config", "mod-loading-screen", "update.properties"), "autoUpdate=false\npackwizUrl=http://example.com/pack.toml");
        Assert.IsFalse(ExternalPackUpdater.ReadSource(_root)!.Enabled);
    }

    [TestMethod]
    public void AutomaticCheckIsOncePerDirectoryUntilReset()
    {
        var gate = new ExternalPackCheckOnce();
        Assert.IsFalse(gate.IsDone(_root));
        gate.MarkDone(_root + Path.DirectorySeparatorChar);
        Assert.IsTrue(gate.IsDone(_root));
        var other = Path.Combine(Path.GetTempPath(), "pcl-update-other");
        Assert.IsFalse(gate.IsDone(other));
        Assert.AreNotEqual(ExternalPackCheckOnce.ManagedPackId("Same Name", _root), ExternalPackCheckOnce.ManagedPackId("Same Name", other));
        Assert.AreEqual(ExternalPackCheckOnce.ManagedPackId("Same Name", _root), ExternalPackCheckOnce.ManagedPackId("Same Name", _root));
        gate.Clear();
        Assert.IsFalse(gate.IsDone(_root));
    }

    [TestMethod]
    public async Task MissingLocalConfigStillUsesPackConfig()
    {
        File.Delete(Path.Combine(_root, "config", "mod-loading-screen", "update.properties"));
        var fallback = new ExternalUpdateSource("default", "http://example.com/pack.toml", true);
        using var http = new HttpClient(new Handler(Encoding.UTF8.GetBytes("pack")));
        var updater = new ExternalPackUpdater(http, "java", (_, _) =>
        {
            var path = Path.Combine(_root, ".updatemod", "packwiz.hash");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pack"))).ToLowerInvariant());
            return Task.FromResult(0);
        });
        var plan = await updater.CheckAsync(_root, CancellationToken.None, fallback);
        Assert.IsNotNull(plan);
        Assert.IsFalse(plan.ConfigExistenceOnly);
        await updater.ApplyAsync(_root, plan, null, CancellationToken.None);
        Assert.IsNull(await updater.CheckAsync(_root, CancellationToken.None, fallback));
    }

}
