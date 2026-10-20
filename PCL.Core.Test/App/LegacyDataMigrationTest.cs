using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;

namespace PCL.Core.Test.App;

[TestClass]
public sealed class LegacyDataMigrationTest
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl-moon-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string OldDir => Path.Combine(_root, "MOONCL");
    private string NewDir => Path.Combine(_root, "PCL-MOON");

    [TestMethod]
    public void MovesFilesAndSubdirectories()
    {
        Directory.CreateDirectory(OldDir);
        File.WriteAllText(Path.Combine(OldDir, "config.json"), "{}");
        Directory.CreateDirectory(Path.Combine(OldDir, "saves"));
        File.WriteAllText(Path.Combine(OldDir, "saves", "a.txt"), "x");
        LegacyDataMigration.Migrate(OldDir, NewDir);
        Assert.IsTrue(File.Exists(Path.Combine(NewDir, "config.json")));
        Assert.IsTrue(File.Exists(Path.Combine(NewDir, "saves", "a.txt")));
        Assert.IsFalse(Directory.Exists(OldDir));
    }

    [TestMethod]
    public void SkipsBundleCacheAndKeepsOldRoot()
    {
        Directory.CreateDirectory(OldDir);
        Directory.CreateDirectory(Path.Combine(OldDir, "bundle"));
        File.WriteAllText(Path.Combine(OldDir, "bundle", "app.exe"), "x");
        File.WriteAllText(Path.Combine(OldDir, "config.json"), "{}");
        LegacyDataMigration.Migrate(OldDir, NewDir);
        Assert.IsTrue(File.Exists(Path.Combine(NewDir, "config.json")));
        Assert.IsTrue(Directory.Exists(Path.Combine(OldDir, "bundle")));
        Assert.IsFalse(Directory.Exists(Path.Combine(NewDir, "bundle")));
    }

    [TestMethod]
    public void DoesNotOverwriteExistingTargetEntries()
    {
        Directory.CreateDirectory(OldDir);
        Directory.CreateDirectory(NewDir);
        File.WriteAllText(Path.Combine(OldDir, "config.json"), "old");
        File.WriteAllText(Path.Combine(NewDir, "config.json"), "new");
        File.WriteAllText(Path.Combine(OldDir, "other.json"), "y");
        LegacyDataMigration.Migrate(OldDir, NewDir);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(NewDir, "config.json")));
        Assert.AreEqual("y", File.ReadAllText(Path.Combine(NewDir, "other.json")));
        Assert.IsTrue(File.Exists(Path.Combine(OldDir, "config.json")));
    }

    [TestMethod]
    public void RemovesEmptyOldRoot()
    {
        Directory.CreateDirectory(OldDir);
        File.WriteAllText(Path.Combine(OldDir, "a.txt"), "x");
        LegacyDataMigration.Migrate(OldDir, NewDir);
        Assert.IsFalse(Directory.Exists(OldDir));
    }

    [TestMethod]
    public void NoopWhenOldRootMissing()
    {
        LegacyDataMigration.Migrate(OldDir, NewDir);
        Assert.IsFalse(Directory.Exists(NewDir));
    }
}
