using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackIdentityVersionRegressionTest
{
    [TestMethod]
    public void SimplyOptimizedProjectDetailMustNotSupplyInstalledRelease()
    {
        var identity = PackIdentityResolver.Choose(new PackClues
        {
            Name = "Simply Optimized", FileName = "Simply Optimized 1.21.11fabric", MinecraftVersion = "1.21.11", ModrinthId = "AbCd1234"
        }, modrinth: [new PackCandidate
        {
            Source = "modrinth", Name = "Simply Optimized", ModrinthId = "AbCd1234", Version = "1.20.1fabric", MinecraftVersion = "1.20.1"
        }]);
        Assert.AreEqual("1.21.11", identity.MinecraftVersion);
        Assert.AreEqual("", identity.Version, "A project's other/latest release is not the installed pack version.");
    }

    [TestMethod]
    public void ProjectSupportedMinecraftMustNotBecomeInstalledMinecraft()
    {
        var identity = PackIdentityResolver.Choose(new PackClues { Name = "Simply Optimized", ModrinthId = "AbCd1234" },
            modrinth: [new PackCandidate { Source = "modrinth", Name = "Simply Optimized", ModrinthId = "AbCd1234", MinecraftVersion = "1.20.1" }]);
        Assert.AreEqual("", identity.MinecraftVersion);
    }

    [TestMethod]
    public void InstalledGameVersionRepairsOldCacheWithoutInventingPackRelease()
    {
        var old = new PackIdentity { Name = "Simply Optimized", MinecraftVersion = "1.20.1", Version = "1.20.1fabric", ModrinthId = "AbCd1234" };
        var clues = PackIdentityResolver.InstalledClues(old, "Simply Optimized 1.21.11fabric", "1.21.11");
        Assert.AreEqual("1.21.11", clues.MinecraftVersion);
        Assert.AreEqual("", clues.Version);
        Assert.AreEqual("AbCd1234", clues.ModrinthId);
        Assert.AreEqual("", PackIdentityResolver.InstalledClues(old with { MinecraftVersion = "1.21.11" }, "Simply Optimized 1.21.11fabric", "1.21.11").Version);
        var manifest = new PackClues { Name = "Simply Optimized", Version = "12.0.0", MinecraftVersion = "1.21.11" };
        Assert.AreEqual("12.0.0", PackIdentityResolver.InstalledClues(old, "SO", "1.21.11", manifest).Version);
    }

    [TestMethod]
    public void LoaderSuffixFallbackAndPatchVersionsAreExact()
    {
        Assert.AreEqual("1.21.11", PackIdentityResolver.InstalledClues(null, "Simply Optimized 1.21.11fabric", "Unknown").MinecraftVersion);
        Assert.IsFalse(PackIdentityResolver.MinecraftMatches("1.21.11", "1.21.1"));
        Assert.IsTrue(PackIdentityResolver.MinecraftMatches("1.20.1, 1.21.11", "1.21.11"));
        Assert.AreEqual(100, PackIdentityResolver.Score(new() { Name = "Simply Optimized", MinecraftVersion = "1.21.11" },
            new() { Name = "Simply Optimized", MinecraftVersion = "1.20.1, 1.21.11" }));
    }

    [TestMethod]
    public void LocalManifestSuppliesRealInstalledRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-installed-clues-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "modrinth.index.json"), """{"name":"Simply Optimized","versionId":"1.21.11fabric","dependencies":{"minecraft":"1.21.11"}}""");
            var manifest = PackCluesReader.ReadInstalled(root);
            Assert.IsNotNull(manifest);
            Assert.AreEqual("1.21.11fabric", manifest.Version);
            Assert.AreEqual("1.21.11", manifest.MinecraftVersion);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MissingCatalogMatchIsNotADescription()
    {
        var identity = PackIdentityResolver.Choose(new PackClues { Name = "SRE", FileName = "SRE4.3", Version = "4.3" });
        Assert.AreEqual("", identity.Description);
        Assert.AreEqual("", identity.SidebarLine);
        Assert.AreEqual("", identity.HeroText);
        Assert.AreEqual("本地整合包", identity.SourceLabel);
    }

    [TestMethod]
    public void RealSummaryStillBecomesIntro()
    {
        var identity = PackIdentityResolver.Choose(new PackClues { Name = "SRE", Summary = "哈比列车" });
        Assert.AreEqual("哈比列车", identity.Description);
        Assert.AreEqual("本地整合包 · 哈比列车", identity.SidebarLine);
    }
}
