using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class QuarkOpenSessionStoreTest
{
    private string _root = null!;
    [TestInitialize]
    public void Setup() => _root = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(), "quark-session-" + Guid.NewGuid().ToString("N"));
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static QuarkOpenSession Session(string value = "latest", long expires = 0) => new(value, value + "-refresh", "device", 0, expires);
    private string Legacy(char hash) => Path.Combine(_root, "single", new string(hash, 64), "PCL", "quark-open-session.bin");

    [TestMethod]
    public void MigratesLatestExtractionSessionAndRestoresAfterPayloadChanges()
    {
        var old = Legacy('a');
        var recent = Legacy('b');
        QuarkOpenSessionFile.Save(old, Session("old"));
        QuarkOpenSessionFile.Save(recent, Session());
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        var store = new QuarkOpenSessionStore(Path.Combine(_root, "single", new string('c', 64)), _root);
        Assert.IsTrue(store.Load()!.Matches("latest", "latest-refresh"));
        Assert.IsTrue(File.Exists(store.SessionPath));
        Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(store.SessionPath)).Contains("latest", StringComparison.Ordinal));
        File.Delete(old);
        File.Delete(recent);
        var restarted = new QuarkOpenSessionStore(Path.Combine(_root, "new-payload"), _root);
        Assert.AreEqual(store.SessionPath, restarted.SessionPath);
        Assert.IsTrue(restarted.Load()!.Matches("latest", "latest-refresh"));
    }

    [TestMethod]
    public void MigratesCurrentPortableSessionWithoutDeletingIt()
    {
        var launcher = Path.Combine(_root, "portable");
        var legacy = Path.Combine(launcher, "PCL", "quark-open-session.bin");
        QuarkOpenSessionFile.Save(legacy, Session());
        Assert.IsTrue(new QuarkOpenSessionStore(launcher, _root).Load()!.IsReusable);
        Assert.IsTrue(File.Exists(legacy));
    }

    [TestMethod]
    public void ExistingStableSessionWinsOverNewerLegacyCache()
    {
        var store = new QuarkOpenSessionStore(_root, _root);
        store.Save(Session());
        QuarkOpenSessionFile.Save(Legacy('a'), Session("stale"));
        Assert.IsTrue(new QuarkOpenSessionStore(_root, _root).Load()!.Matches("latest", "latest-refresh"));
    }

    [TestMethod]
    public void ExpiredRefreshTokenIsNotShownAsAuthorizedOrReplacedWithStaleCache()
    {
        var store = new QuarkOpenSessionStore(_root, _root);
        store.Save(Session(expires: 1));
        QuarkOpenSessionFile.Save(Legacy('a'), Session());
        Assert.IsNull(new QuarkOpenSessionStore(_root, _root).Load());
    }
}
