using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class BrowseResourceTypesTest
{
    [TestMethod]
    [DataRow("datapack", 6945)]
    [DataRow("world", 17)]
    [DataRow("modpack", 4471)]
    [DataRow("shader", 6552)]
    [DataRow("resourcepack", 12)]
    public void CurseForgeUsesTheRealResourceClass(string kind, int classId)
    {
        var address = PlatformPackCatalog.SearchAddress("curseforge", 1, resourceType: kind);
        StringAssert.Contains(address.Query, "classId=" + classId + "&");
        var raw = "{\"id\":123,\"classId\":" + classId + ",\"name\":\"Resource\"}";
        var pack = PlatformPackCatalog.ParseDetail("curseforge", "{\"data\":" + raw + "}", "123", kind);
        Assert.AreEqual(kind, pack.ResourceType);
        var page = PlatformPackCatalog.ParsePage("curseforge", "{\"data\":[" + raw + "],\"pagination\":{\"totalCount\":1}}", 1, kind);
        Assert.AreEqual(kind, page.Items[0].ResourceType);
    }
    [TestMethod]
    [DataRow("datapack")]
    [DataRow("modpack")]
    [DataRow("shader")]
    [DataRow("resourcepack")]
    public void ModrinthQueriesAndParsesTheSelectedProjectType(string kind)
    {
        var address = Uri.UnescapeDataString(PlatformPackCatalog.SearchAddress("modrinth", 1, resourceType: kind).Query);
        StringAssert.Contains(address, "project_type:" + kind);
        var raw = "{\"id\":\"abcd1234\",\"project_type\":\"" + kind + "\",\"title\":\"Resource\",\"slug\":\"sample\"}";
        var pack = PlatformPackCatalog.ParseDetail("modrinth", raw, "abcd1234", kind);
        Assert.AreEqual(kind, pack.ResourceType); StringAssert.Contains(pack.Website, "/" + kind + "/sample");
    }
    [TestMethod]
    public void LegacyModrinthDatapacksAreAcceptedButOrdinaryModsAreNot()
    {
        var json = """{"id":"abcd1234","project_type":"mod","title":"DP","categories":["datapack"]}""";
        Assert.AreEqual("datapack", PlatformPackCatalog.ParseDetail("modrinth", json, "abcd1234", "datapack").ResourceType);
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParseDetail("modrinth", json.Replace("datapack", "technology"), "abcd1234", "datapack"));
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParseDetail("modrinth", json, "abcd1234", "modpack"));
    }
    [TestMethod]
    public void WrongClassAndUnknownTypeCannotCrossLists()
    {
        const string cf = """{"data":{"id":123,"classId":4471,"name":"Pack"}}""";
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParseDetail("curseforge", cf, "123", "world"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("curseforge", 1, resourceType: "../../mods"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("curseforge", 1, loader: "Fabric", resourceType: "shader"));
    }
    [TestMethod]
    public async Task UnavailableSourceTypesNeverFetchOrInventEntries()
    {
        var calls = 0;
        var client = new PlatformPackClient((_, _) => { calls++; return Task.FromResult("{}"); });
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => client.GetPageAsync("modrinth", 1, resourceType: "world"));
        Assert.ThrowsExactly<NotSupportedException>(() => BrowseResourceTypes.RequireSupported("mcmod", "shader"));
        Assert.ThrowsExactly<NotSupportedException>(() => BrowseResourceTypes.RequireSupported("mcmod", "world"));
        Assert.ThrowsExactly<NotSupportedException>(() => BrowseResourceTypes.RequireSupported("mcmod", "resourcepack"));
        Assert.IsTrue(BrowseResourceTypes.Supports("mcmod", "datapack")); Assert.AreEqual(0, calls);
    }
    [TestMethod]
    public async Task DetailCacheIncludesResourceType()
    {
        var client = new PlatformPackClient((uri, _) => Task.FromResult(uri.AbsolutePath.EndsWith("description") ? """{"data":"Summary"}""" : """{"data":{"id":123,"classId":4471,"name":"Pack"}}"""));
        await client.GetDetailAsync("curseforge", "123");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.GetDetailAsync("curseforge", "123", resourceType: "world"));
    }
    [TestMethod]
    public void ShaderEnvironmentsAreMetadataNotForgeFilters()
    {
        var pack = PlatformPackCatalog.ParseDetail("modrinth", """{"id":"abcd1234","project_type":"shader","title":"Shader","loaders":["iris","optifine"],"categories":["fantasy","iris"]}""", "abcd1234", "shader");
        CollectionAssert.AreEqual(new[] { "Iris", "OptiFine" }, pack.Loaders);
        CollectionAssert.AreEqual(new[] { "fantasy" }, pack.Categories);
    }
}
