using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class ServerPluginBrowseTest
{
    [TestMethod]
    public void ModpacksAreFirstAndPluginHasRealProviderClass()
    {
        Assert.AreEqual("modpack", BrowseResourceTypes.All[0]);
        Assert.AreEqual(5, BrowseResourceTypes.CurseForgeClass("plugin"));
        Assert.IsTrue(BrowseResourceTypes.Supports("curseforge", "plugin"));
        Assert.IsTrue(BrowseResourceTypes.Supports("modrinth", "plugin"));
        Assert.IsFalse(BrowseResourceTypes.Supports("mcmod", "plugin"));
        StringAssert.Contains(PlatformPackCatalog.SearchAddress("curseforge", 1, resourceType: "plugin").Query, "classId=5");
        StringAssert.Contains(Uri.UnescapeDataString(PlatformPackCatalog.SearchAddress("modrinth", 1, resourceType: "plugin").Query), "project_type:plugin");
    }
    [TestMethod]
    public void BukkitProjectsAndLegacyPaperRecordsAreNotClientMods()
    {
        var cf = PlatformPackCatalog.ParseDetail("curseforge", """{"data":{"id":123,"classId":5,"name":"Plugin","links":{"websiteUrl":"https://www.curseforge.com/minecraft/bukkit-plugins/example"}}}""", "123", "plugin");
        Assert.AreEqual("plugin", cf.ResourceType);
        var mr = PlatformPackCatalog.ParseDetail("modrinth", """{"id":"abcd1234","project_type":"mod","title":"Plugin","slug":"example","loaders":["paper","purpur"],"categories":["utility"]}""", "abcd1234", "plugin");
        CollectionAssert.AreEqual(new[] { "Paper", "Purpur" }, mr.Loaders);
        Assert.AreEqual("https://modrinth.com/plugin/example", mr.Website);
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParseDetail("modrinth", """{"id":"abcd1234","project_type":"mod","loaders":["fabric"]}""", "abcd1234", "plugin"));
    }
}
