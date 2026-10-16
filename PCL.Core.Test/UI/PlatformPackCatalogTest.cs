using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PlatformPackCatalogTest
{
    private const string CfPack = """{"id":123,"classId":4471,"name":"CF pack","summary":"Summary","downloadCount":18694984,"authors":[{"name":"Writer"}],"links":{"websiteUrl":"https://www.curseforge.com/minecraft/modpacks/pack"},"logo":{"url":"https://example.com/a.png"},"latestFiles":[{"id":1,"fileDate":"2024-01-01T00:00:00Z","displayName":"Old","gameVersions":["1.20.1","Forge"]},{"id":2,"fileDate":"2025-01-01T00:00:00Z","displayName":"Actual 2.0","gameVersions":["1.21.1","NeoForge"]}],"categories":[{"name":"Adventure"}]}""";
    private const string MrPack = """{"project_id":"abcd1234","project_type":"modpack","title":"MR pack","slug":"sample","author":"Creator","description":"Desc","downloads":42,"versions":["1.20.1","1.21.1"],"latest_version":"vers1234","categories":["fabric","adventure"]}""";
    private const string MrDetail = """{"id":"abcd1234","project_type":"modpack","title":"MR pack","slug":"sample","body":"<p>Hello</p><script>alert(1)</script>","team":"team1234","game_versions":["1.20.1"],"loaders":["fabric"],"categories":["adventure"],"versions":["oldr1234","vers1234"]}""";
    private static string CfList(string packs = CfPack, int count = 49) => "{\"data\":[" + packs + "],\"pagination\":{\"totalCount\":" + count + "}}";
    private static string MrList(string packs = MrPack, int count = 25) => "{\"hits\":[" + packs + "],\"total_hits\":" + count + "}";

    [TestMethod]
    public void CurseForgeSearchIsModpacksOnlyAndEncodesFilters()
    {
        var uri = PlatformPackCatalog.SearchAddress("curseforge", 2, "hello & 中文", "1.21.1", "NeoForge", "downloads");
        Assert.AreEqual("api.curseforge.com", uri.Host);
        StringAssert.Contains(uri.Query, "classId=4471"); StringAssert.Contains(uri.Query, "pageSize=24");
        StringAssert.Contains(uri.Query, "index=24"); StringAssert.Contains(uri.Query, "modLoaderType=6");
        StringAssert.Contains(uri.Query, "sortField=6"); StringAssert.Contains(Uri.UnescapeDataString(uri.Query), "searchFilter=hello & 中文");
    }
    [TestMethod]
    public void ModrinthSearchUsesAndFacetsAndRemoteQuery()
    {
        var uri = PlatformPackCatalog.SearchAddress("modrinth", 3, "adventure", "1.20.1", "Fabric", "updated");
        var decoded = Uri.UnescapeDataString(uri.Query);
        Assert.AreEqual("api.modrinth.com", uri.Host); StringAssert.Contains(decoded, "offset=48");
        StringAssert.Contains(decoded, "project_type:modpack"); StringAssert.Contains(decoded, "versions:1.20.1");
        StringAssert.Contains(decoded, "categories:fabric"); StringAssert.Contains(decoded, "index=updated");
    }
    [TestMethod]
    public void InputsAreBoundedAndCannotBecomeArbitraryEndpoints()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("https://evil.test", 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlatformPackCatalog.SearchAddress("modrinth", 401));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlatformPackCatalog.SearchAddress("modrinth", 0));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("modrinth", 1, new string('a', 201)));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("modrinth", 1, "a\nb"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("modrinth", 1, "", "1.20.1&admin=1"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("modrinth", 1, "", "", "Rift"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.SearchAddress("curseforge", 1, sort: "bad"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.DetailAddress("modrinth", "../../secret"));
        Assert.ThrowsExactly<ArgumentException>(() => PlatformPackCatalog.DetailAddress("curseforge", "-1"));
    }
    [TestMethod]
    public void CurseForgeFieldsAndNewestReleaseAreReal()
    {
        var page = PlatformPackCatalog.ParsePage("curseforge", CfList(), 1);
        Assert.AreEqual(3, page.TotalPages); Assert.AreEqual("curseforge-123", page.Items[0].Id);
        var pack = page.Items[0]; Assert.AreEqual("Actual 2.0", pack.Version);
        Assert.AreEqual(18694984, pack.DownloadCount);
        CollectionAssert.AreEqual(new[] { "Writer" }, pack.Authors);
        CollectionAssert.AreEquivalent(new[] { "1.20.1", "1.21.1" }, pack.MinecraftVersions);
        CollectionAssert.AreEquivalent(new[] { "Forge", "NeoForge" }, pack.Loaders);
        Assert.AreEqual("Adventure", pack.Categories.Single());
    }
    [TestMethod]
    public void ModrinthListDoesNotMislabelVersionIdAsRelease()
    {
        var page = PlatformPackCatalog.ParsePage("modrinth", MrList(), 1);
        Assert.AreEqual(2, page.TotalPages); var pack = page.Items[0];
        Assert.AreEqual("modrinth-abcd1234", pack.Id); Assert.AreEqual("", pack.Version);
        Assert.AreEqual("sample", pack.Slug);
        CollectionAssert.AreEqual(new[] { "Creator" }, pack.Authors);
        CollectionAssert.AreEqual(new[] { "Fabric" }, pack.Loaders);
        Assert.AreEqual(42, pack.DownloadCount);
        CollectionAssert.AreEqual(new[] { "adventure" }, pack.Categories);
    }
    [TestMethod]
    public void DetailValidatesSourceIdentityAndSafeText()
    {
        var pack = PlatformPackCatalog.ParseDetail("modrinth", MrDetail, "abcd1234");
        Assert.AreEqual("Hello", pack.Description); Assert.AreEqual("Fabric", pack.Loaders.Single());
        Assert.AreEqual("Hello **bold**", PlatformPackCatalog.ParseDetail("modrinth", MrDetail.Replace("<p>Hello</p><script>alert(1)</script>", "Hello **bold**"), "abcd1234").Description);
        var markdown = "# Title\n![banner](https://cdn.modrinth.com/a.png)";
        Assert.AreEqual(markdown, PlatformPackCatalog.ParseDetail("modrinth", MrDetail.Replace("<p>Hello</p><script>alert(1)</script>", markdown.Replace("\n", "\\n")), "abcd1234").Description);
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParseDetail("modrinth", MrDetail, "zzzz1234"));
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParsePage("curseforge", CfList(CfPack.Replace("4471", "6")), 1));
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParsePage("modrinth", MrList(MrPack.Replace("\"modpack\"", "\"mod\"")), 1));
    }
    [TestMethod]
    public void EmptyListsAndMalformedListsAreDifferent()
    {
        Assert.AreEqual(0, PlatformPackCatalog.ParsePage("curseforge", CfList("", 0), 1).Items.Count);
        Assert.AreEqual(1, PlatformPackCatalog.ParsePage("modrinth", MrList("", 0), 1).TotalPages);
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParsePage("modrinth", "{}", 1));
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParsePage("curseforge", "{\"data\":[]}", 1));
        Assert.ThrowsExactly<InvalidDataException>(() => PlatformPackCatalog.ParsePage("modrinth", new string('a', PlatformPackCatalog.MaxJsonChars + 1), 1));
        Assert.AreEqual(400, PlatformPackCatalog.ParsePage("modrinth", MrList(count: int.MaxValue), 1).TotalPages);
    }
    [TestMethod]
    public async Task ClientCachesByPlatformAndQuery()
    {
        var calls = 0;
        var client = new PlatformPackClient((uri, _) => { calls++; return Task.FromResult(uri.Host == "api.curseforge.com" ? CfList() : MrList()); });
        await Task.WhenAll(client.GetPageAsync("modrinth", 1), client.GetPageAsync("modrinth", 1));
        Assert.AreEqual(1, calls);
        await client.GetPageAsync("curseforge", 1); await client.GetPageAsync("modrinth", 1, "other");
        Assert.AreEqual(3, calls);
    }
    [TestMethod]
    public async Task OptionalDescriptionFailureKeepsCurseForgeSummary()
    {
        var client = new PlatformPackClient((uri, _) => uri.AbsolutePath.EndsWith("description") ? Task.FromException<string>(new IOException("optional unavailable")) : Task.FromResult("{\"data\":" + CfPack + "}"));
        Assert.AreEqual("Summary", (await client.GetDetailAsync("curseforge", "123")).Description);
    }
    [TestMethod]
    public async Task ModrinthDetailResolvesTeamAndKeepsUnknownReleaseUnknown()
    {
        var client = new PlatformPackClient((uri, _) => Task.FromResult(uri.AbsolutePath.EndsWith("members") ? """[{"user":{"username":"Team Writer"}}]""" : MrDetail));
        var pack = await client.GetDetailAsync("modrinth", "abcd1234");
        Assert.AreEqual("Team Writer", pack.Authors.Single()); Assert.AreEqual("", pack.Version);
    }
    [TestMethod]
    public async Task CancellationAndNetworkFailureDoNotCacheEmptySuccess()
    {
        using var token = new CancellationTokenSource(); token.Cancel();
        var client = new PlatformPackClient((_, _) => Task.FromException<string>(new IOException("unavailable")));
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.GetPageAsync("modrinth", 1, token: token.Token));
        await Assert.ThrowsExactlyAsync<IOException>(() => client.GetPageAsync("modrinth", 1));
    }

    [TestMethod]
    public async Task ModrinthLatestVersionReferenceResolvesToActualReleaseNumber()
    {
        var client = new PlatformPackClient((uri, _) => Task.FromResult(uri.AbsolutePath == "/v2/search" ? MrList() : uri.AbsolutePath == "/v2/version/vers1234" ? """{"project_id":"abcd1234","version_number":"3.2.1"}""" : MrDetail));
        await client.GetPageAsync("modrinth", 1);
        var pack = await client.GetDetailAsync("modrinth", "abcd1234");
        Assert.AreEqual("3.2.1", pack.Version); Assert.AreEqual("Creator", pack.Authors.Single());
    }
}
