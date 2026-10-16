using System.IO;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class BrowseCategoriesTest
{
    private const string Cf = """
        {"data":[
          {"id":12,"name":"Resource Packs","isClass":true,"classId":null},
          {"id":17,"name":"Worlds","isClass":true,"classId":null},
          {"id":6552,"name":"Shaders","isClass":true,"classId":null},
          {"id":6945,"name":"Data Packs","isClass":true,"classId":null},
          {"id":4471,"name":"Modpacks","isClass":true},
          {"id":12,"name":"Root again","isClass":false,"classId":12},
          {"id":398,"name":"512x and Higher","isClass":null,"classId":12},
          {"id":398,"name":"Duplicate","isClass":false,"classId":12},
          {"id":253,"name":"Survival","classId":17},
          {"id":6553,"name":"<b>Realistic</b><script>alert(1)</script>","isClass":false,"classId":6552},
          {"id":6554,"name":"Fantasy","classId":6552},
          {"id":6948,"name":"Adventure","classId":6945},
          {"id":423,"name":"Map and Information","classId":6}
        ]}
        """;
    private const string Mr = """
        [
          {"name":"16x","project_type":"resourcepack","header":"resolutions"},
          {"name":"512x+","project_type":"resourcepack","header":"resolutions"},
          {"name":"fabric","project_type":"resourcepack","header":"loaders"},
          {"name":"audio","project_type":"resourcepack","header":"features"},
          {"name":"bloom","project_type":"shader","header":"features"},
          {"name":"realistic","project_type":"shader","header":"categories"},
          {"name":"iris","project_type":"shader","header":"Loaders"},
          {"name":"technology","project_type":"mod","header":"categories"},
          {"name":"magic","project_type":"mod","header":"categories"},
          {"name":"adventure","project_type":"modpack","header":"categories"}
        ]
        """;

    [TestMethod]
    [DataRow("datapack", 6945, "6948", "Adventure")]
    [DataRow("world", 17, "253", "Survival")]
    [DataRow("shader", 6552, "6553", "Realistic")]
    [DataRow("resourcepack", 12, "398", "512x and Higher")]
    public void CurseForgeKeepsOnlyTheRequestedClass(string type, int classId, string id, string name)
    {
        var address = BrowseCategories.Address("curseforge", type);
        StringAssert.Contains(address.Query, "gameId=432");
        StringAssert.Contains(address.Query, "classId=" + classId);
        var items = BrowseCategories.Parse("curseforge", type, Cf);
        Assert.IsTrue(items.All(item => item.Source == "curseforge" && item.ResourceType == type));
        Assert.AreEqual(1, items.Count(item => item.Id == id));
        Assert.AreEqual(name, items.Single(item => item.Id == id).Name);
        Assert.IsFalse(items.Any(item => item.Id == classId.ToString() || item.Id == "423" || item.Id == "4471"));
    }

    [TestMethod]
    public void ModrinthKeepsRealTagsAndFallsBackOnlyForDatapacks()
    {
        Assert.AreEqual("https://api.modrinth.com/v2/tag/category", BrowseCategories.Address("modrinth", "shader").AbsoluteUri);
        var packs = BrowseCategories.Parse("modrinth", "resourcepack", Mr);
        CollectionAssert.AreEquivalent(new[] { "16x", "512x+", "audio" }, packs.Select(item => item.Id).ToArray());
        var shaders = BrowseCategories.Parse("modrinth", "shader", Mr);
        CollectionAssert.AreEquivalent(new[] { "bloom", "realistic" }, shaders.Select(item => item.Id).ToArray());
        var datapacks = BrowseCategories.Parse("modrinth", "datapack", Mr);
        Assert.IsTrue(datapacks.All(item => item.ResourceType == "datapack"));
        CollectionAssert.AreEquivalent(new[] { "technology", "magic" }, datapacks.Select(item => item.Id).ToArray());
        var exact = """[{"name":"utility","project_type":"datapack","header":"categories"},{"name":"technology","project_type":"mod","header":"categories"}]""";
        CollectionAssert.AreEqual(new[] { "utility" }, BrowseCategories.Parse("modrinth", "datapack", exact).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void MalformedCategoryPayloadsThrowInsteadOfReturningEmpty()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => BrowseCategories.Parse("curseforge", "shader", "{"));
        Assert.ThrowsExactly<InvalidDataException>(() => BrowseCategories.Parse("curseforge", "shader", """{"data":{}}"""));
        Assert.ThrowsExactly<InvalidDataException>(() => BrowseCategories.Parse("modrinth", "shader", """{"data":[]}"""));
        Assert.ThrowsExactly<InvalidDataException>(() => BrowseCategories.Parse("modrinth", "shader", new string(' ', PlatformPackCatalog.MaxJsonChars + 1)));
        Assert.ThrowsExactly<InvalidDataException>(() => BrowseCategories.Parse("curseforge", "shader", """{"data":[{"id":0,"name":"Bad","classId":6552}]}"""));
        Assert.ThrowsExactly<InvalidDataException>(() => BrowseCategories.Parse("modrinth", "shader", """[{"name":"bad slug","project_type":"shader","header":"features"}]"""));
        Assert.AreEqual(0, BrowseCategories.Parse("curseforge", "shader", """{"data":[]}""").Count);
    }

    [TestMethod]
    public void CategoryFiltersAreEncodedBoundedAndIsolated()
    {
        var cf = PlatformPackCatalog.SearchAddress("curseforge", 1, resourceType: "resourcepack", category: "398");
        StringAssert.Contains(cf.Query, "classId=12");
        StringAssert.Contains(cf.Query, "categoryId=398");
        var mr = Uri.UnescapeDataString(PlatformPackCatalog.SearchAddress("modrinth", 1, loader: "Fabric", resourceType: "modpack", category: "512x+").Query);
        StringAssert.Contains(mr, """["project_type:modpack"]""");
        StringAssert.Contains(mr, """["categories:fabric"]""");
        StringAssert.Contains(mr, """["categories:512x\u002B"]""");
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("curseforge", 1, category: "0"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("curseforge", 1, category: "12a"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("curseforge", 1, category: "1&classId=6"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("modrinth", 1, category: new string('a', 65)));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("modrinth", 1, category: "../secret"));
        Assert.IsFalse(PlatformPackCatalog.SearchAddress("modrinth", 1).Query.Contains("categories%3A", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WrongSourceOrTypeSelectionIsRejectedWithoutNestedCacheDeadlock()
    {
        var calls = 0;
        var client = new PlatformPackClient((uri, _) =>
        {
            calls++;
            if (uri.AbsolutePath.Contains("categories", StringComparison.Ordinal)) return Task.FromResult(Cf);
            if (uri.AbsolutePath.Contains("tag/category", StringComparison.Ordinal)) return Task.FromResult(Mr);
            return Task.FromResult("""{"data":[],"pagination":{"totalCount":0}}""");
        });
        var page = client.GetPageAsync("curseforge", 1, resourceType: "shader", category: "6553");
        Assert.AreSame(page, await Task.WhenAny(page, Task.Delay(3000)));
        Assert.AreEqual("6553", (await page).Source == "curseforge" ? "6553" : "");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.GetPageAsync("curseforge", 1, resourceType: "resourcepack", category: "6553"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.GetPageAsync("curseforge", 1, resourceType: "shader", category: "6552"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.GetPageAsync("modrinth", 1, resourceType: "shader", category: "6553"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => client.GetCategoriesAsync("modrinth", "world"));
        var again = await client.GetCategoriesAsync("curseforge", "shader");
        Assert.AreEqual("Realistic", again.Single(item => item.Id == "6553").Name);
        Assert.IsTrue(calls >= 2);
        var before = calls;
        await client.GetCategoriesAsync("curseforge", "shader");
        Assert.AreEqual(before, calls);
    }
}
