using System;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackDownloadBrowserTest
{
    private static PackDownloadVersion File(string id, string mc, string name, int days) =>
        new("modrinth", "AbCd1234", id, name, id, name + ".mrpack", "https://example.com/pack.mrpack", "", HashAlgorithmName.SHA512,
            "正式版", mc, DateTimeOffset.UnixEpoch.AddDays(days)) { Loader = "Fabric" };
    private static PackDownloadVersion[] Files => [File("old", "1.20.1", "Simply Optimized old", 1),
        File("new", "1.21.11", "Simply Optimized new", 3), File("patch", "1.21.1", "Simply Optimized patch", 2)];

    [TestMethod]
    public void CurrentMinecraftNeverShowsOtherPatchVersions()
    {
        var result = PackDownloadBrowser.Browse(Files, "", "1.21.11", PackDownloadSort.Newest);
        Assert.AreEqual(1, result.Count); Assert.AreEqual("1.21.11", result[0].Minecraft);
        Assert.AreEqual("new", result[0].Versions.Single().Id);
    }
    [TestMethod]
    public void SearchUsesAllTokensAndExactNumericBoundaries()
    {
        Assert.AreEqual("new", PackDownloadBrowser.Browse(Files, "optimized FABRIC 1.21.11", "", PackDownloadSort.Newest).Single().Versions.Single().Id);
        Assert.AreEqual("patch", PackDownloadBrowser.Browse(Files, "1.21.1", "", PackDownloadSort.Newest).Single().Versions.Single().Id);
        Assert.AreEqual(0, PackDownloadBrowser.Browse(Files, "nonexistent", "", PackDownloadSort.Newest).Count);
    }
    [TestMethod]
    public void SortsByDateNameAndNumericMinecraft()
    {
        Assert.AreEqual("new", PackDownloadBrowser.Browse(Files, "", "", PackDownloadSort.Newest)[0].Versions[0].Id);
        Assert.AreEqual("old", PackDownloadBrowser.Browse(Files, "", "", PackDownloadSort.Oldest)[0].Versions[0].Id);
        Assert.AreEqual("new", PackDownloadBrowser.Browse(Files, "", "", PackDownloadSort.Name)[0].Versions[0].Id);
        var mixed = new[] { File("a", "1.9", "A", 3), File("b", "1.21.11", "B", 1), File("c", "1.21.2", "C", 2) };
        CollectionAssert.AreEqual(new[] { "1.21.11", "1.21.2", "1.9" }, PackDownloadBrowser.Browse(mixed, "", "", PackDownloadSort.Minecraft).Select(g => g.Minecraft).ToArray());
    }
    [TestMethod]
    public void MultiGameFileAppearsOnceInEachApplicableGroup()
    {
        var files = new[] { File("multi", "1.21.10, 1.21.11, 1.21.11", "Shared", 1) };
        var groups = PackDownloadBrowser.Browse(files, "", "", PackDownloadSort.Minecraft);
        Assert.AreEqual(2, groups.Count); Assert.IsTrue(groups.All(g => g.Versions.Count == 1));
        Assert.AreEqual(1, PackDownloadBrowser.Browse(files, "", "1.21.11", PackDownloadSort.Newest).Count);
    }
}
