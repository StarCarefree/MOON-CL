using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class McmodCatalogTest
{
    private const string ListHtml = """
        <div class="modlist-block"><a class="intro-content"><span>冒险 &amp; 科技</span></a>
        <img src="//i.mcmod.cn/test.jpg"><p class="name"><a href="/modpack/1426.html">千界※万锻</a></p>
        <p class="ename"><a>Myriad Dimensional Forge</a></p></div>
        <div class="modlist-block"><p class="name"><a href="/modpack/1426.html">重复</a></p></div>
        <div class="modlist-block"><p class="name"><a href="/class/42.html">不是整合包</a></p></div>
        <ul class="pagination common-pages"><li><a data-page="47">尾页</a></li></ul>
        """;
    private static string Detail(string links = "") => """
        <a href="/modpack.html?mcver=1.12.2">导航中的版本</a><div class="class-title"><span class="short-name">[SDBF]</span><h3>千界※万锻</h3><h4>Myriad</h4></div>
        <div class="class-cover-image"><img src="//i.mcmod.cn/cover.jpg"></div><div class="class-category"><a>科技</a><a>冒险</a></div>
        <div class="class-info-left"><li>运作方式: <a href="/modpack.html?api=1">Forge</a></li>
        <li class="mcver"><div class="platform-block"><div class="mcver-title">Forge</div><a href="/modpack.html?api=1&amp;mcver=1.20.1">1.20.1</a></div></li>
        <li class="author"><span class="avatar"><a><img></a></span><span class="member"><span class="name"><a>Tonywww</a></span></span></li>
        <div class="common-link-frame"><ul>
        """ + links + """
        </ul></div></div><li class="text-area common-text" data-id="1"><p>正文 &amp; 介绍</p><script>alert('bad')</script><p>第二段</p></li>
        """;
    private static string Link(string url, string name) => $"<li><a href='//link.mcmod.cn/target/{Convert.ToBase64String(Encoding.UTF8.GetBytes(url))}'></a><span class='name'>{name}</span></li>";

    [TestMethod]
    public void ListIsScopedDeduplicatedAndHasPagination()
    {
        var page = McmodCatalog.ParsePage(ListHtml);
        Assert.AreEqual(47, page.TotalPages); Assert.AreEqual(1, page.Items.Count);
        var pack = page.Items[0]; Assert.AreEqual("mcmod-1426", pack.Id);
        Assert.AreEqual("冒险 & 科技", pack.Description); Assert.AreEqual("https://i.mcmod.cn/test.jpg", pack.Icon);
        Assert.AreEqual(0, pack.MinecraftVersions.Length);
    }

    [TestMethod]
    public void DetailExtractsRealFieldsAndDecodesAuthorLinks()
    {
        var cf = "https://www.curseforge.com/minecraft/modpacks/sendims-banforges";
        var pack = McmodCatalog.ParseDetail(Detail(Link(cf, "CurseForge") + Link("https://pan.quark.cn/s/ff96d10800cd", "夸克网盘")), 1426);
        Assert.AreEqual("[SDBF] 千界※万锻", pack.Name); Assert.AreEqual(cf, pack.CurseForgeUrl);
        CollectionAssert.AreEqual(new[] { "1.20.1" }, pack.MinecraftVersions);
        CollectionAssert.AreEqual(new[] { "Forge" }, pack.Loaders);
        CollectionAssert.AreEqual(new[] { "Tonywww" }, pack.Authors);
        CollectionAssert.AreEqual(new[] { "科技", "冒险" }, pack.Categories);
        Assert.AreEqual("", pack.Version); Assert.IsFalse(pack.Description.Contains("alert"));
        Assert.AreEqual(2, pack.Links.Length);
    }

    [TestMethod]
    public void NoCurseForgeMeansManualSourcesNotInventedDownload()
    {
        var pack = McmodCatalog.ParseDetail(Detail(Link("https://example.com/download", "作者下载")), 7);
        Assert.AreEqual("", pack.CurseForgeUrl); Assert.AreEqual(1, pack.Links.Length);
    }

    [TestMethod]
    [DataRow("https://curseforge.com.evil.test/minecraft/modpacks/pack")]
    [DataRow("https://evilcurseforge.com/minecraft/modpacks/pack")]
    [DataRow("https://user:pass@www.curseforge.com/minecraft/modpacks/pack")]
    [DataRow("https://www.curseforge.com/minecraft/mc-mods/mod")]
    [DataRow("file:///C:/pack.zip")]
    public void RejectsFalseCurseForgeProjects(string url) => Assert.IsFalse(McmodCatalog.IsCurseForgeModpack(url));

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("file:///C:/secret")]
    [DataRow("https://user:pass@example.com/file")]
    public void RejectsUnsafeDecodedLinks(string url) => Assert.AreEqual("", McmodCatalog.DecodeLink("//link.mcmod.cn/target/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url))));

    [TestMethod]
    public void MalformedOrChangedPagesFailClearly()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => McmodCatalog.ParsePage("<html>challenge</html>"));
        Assert.ThrowsExactly<InvalidDataException>(() => McmodCatalog.ParseDetail("<h1>404</h1>", 1));
        Assert.ThrowsExactly<InvalidDataException>(() => McmodCatalog.ParsePage(new string('a', McmodCatalog.MaxHtmlBytes + 1)));
        Assert.AreEqual("", McmodCatalog.DecodeLink("//link.mcmod.cn/target/not_base64!"));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
    [TestMethod]
    public async Task RequestsAreFixedOriginCachedAndBounded()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(r => {
            count++; Assert.AreEqual("https://www.mcmod.cn/modpack.html?page=2", r.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ListHtml) };
        }));
        var client = new McmodCatalog(http);
        await Task.WhenAll(client.GetPageAsync(2), client.GetPageAsync(2));
        Assert.AreEqual(1, count);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => client.GetPageAsync(0));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => client.GetDetailAsync(-1));
    }
    [TestMethod]
    public async Task NetworkFailureDoesNotBecomeEmptySuccess()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new McmodCatalog(http).GetPageAsync(1));
    }
    [TestMethod]
    public async Task OversizedResponsesAreRejected()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('a', McmodCatalog.MaxHtmlBytes + 1)) }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new McmodCatalog(http).GetPageAsync(1));
    }

    [TestMethod]
    public void FiltersAreEncodedAndUseRealMcmodLoaderIds()
    {
        Assert.AreEqual("modpack.html?page=2&mcver=1.21.1&api=7&category=3", McmodCatalog.ListPath(2, "1.21.1", "NeoForge", 3));
        Assert.AreEqual("modpack.html?page=1&sort=createtime", McmodCatalog.ListPath(1, sort: "createtime"));
        Assert.AreEqual("modlist.html?api=5&page=3&sort=lastedittime", McmodCatalog.ListPath(3, sort: "updated", resourceType: "datapack"));
        Assert.ThrowsExactly<ArgumentException>(() => McmodCatalog.ListPath(1, sort: "views"));
        Assert.ThrowsExactly<ArgumentException>(() => McmodCatalog.ListPath(1, "1.20.1&other=evil"));
        Assert.ThrowsExactly<ArgumentException>(() => McmodCatalog.ListPath(1, "", "unexpected"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => McmodCatalog.ListPath(1, "", "", 16));
        Assert.AreEqual(0, McmodCatalog.ParsePage("<div class='modlist-list-frame'>没有结果</div>").Items.Count);
    }

    [TestMethod]
    public async Task RateLimitEntersCooldownWithoutRepeatedNetworkRequests()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => { count++; return new HttpResponseMessage(HttpStatusCode.TooManyRequests); }));
        var client = new McmodCatalog(http);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetPageAsync(1));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetPageAsync(2));
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public void DatapackUsesClassNamespaceAndFixedApiFive()
    {
        Assert.AreEqual("modlist.html?api=5&page=2&mcver=1.20.1", McmodCatalog.ListPath(2, "1.20.1", resourceType: "datapack"));
        var html = """<div class="modlist-block"><p class="name"><a href="/class/1426.html">Data</a></p></div><ul class="common-pages"><a data-page="2">2</a></ul>""";
        var pack = McmodCatalog.ParsePage(html, resourceType: "datapack").Items[0];
        Assert.AreEqual("mcmod-datapack-1426", pack.Id); Assert.AreEqual("datapack", pack.ResourceType);
        Assert.AreEqual("https://www.mcmod.cn/class/1426.html", pack.Website);
        Assert.ThrowsExactly<ArgumentException>(() => McmodCatalog.ListPath(1, loader: "Forge", resourceType: "datapack"));
        Assert.ThrowsExactly<NotSupportedException>(() => McmodCatalog.ListPath(1, resourceType: "shader"));
    }

    [TestMethod]
    public void DatapackDetailRequiresMarkerAndUsesOnlyDataVersions()
    {
        var url = "https://www.curseforge.com/minecraft/data-packs/example";
        var html = Detail(Link(url, "CurseForge")).Replace("/modpack.html?api=1", "/modlist.html?api=5")
            .Replace("<li class=\"mcver\">", "<li class=\"mcver\"><a href=\"/modlist.html?api=1&mcver=1.21.1\">1.21.1</a>");
        var pack = McmodCatalog.ParseDetail(html, 1426, "datapack");
        CollectionAssert.AreEqual(new[] { "1.20.1" }, pack.MinecraftVersions);
        CollectionAssert.AreEqual(new[] { "数据包" }, pack.Loaders);
        Assert.AreEqual("Tonywww", pack.Authors[0]); Assert.AreEqual(url, pack.CurseForgeUrl);
        Assert.ThrowsExactly<InvalidDataException>(() => McmodCatalog.ParseDetail(Detail(), 1426, "datapack"));
        Assert.IsFalse(McmodCatalog.IsCurseForgeResource("https://www.curseforge.com/minecraft/modpacks/example", "datapack"));
        Assert.IsFalse(McmodCatalog.IsCurseForgeResource("https://www.curseforge.com.evil.test/minecraft/data-packs/example", "datapack"));
    }

    [TestMethod]
    public async Task SameWikiNumberDoesNotShareDetailCacheAcrossTypes()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(r => { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(r.RequestUri!.AbsolutePath.StartsWith("/class/") ? Detail().Replace("/modpack.html?api=1", "/modlist.html?api=5") : Detail()) }; }));
        var client = new McmodCatalog(http);
        var pack = await client.GetDetailAsync(1426);
        var data = await client.GetDetailAsync(1426, resourceType: "datapack");
        Assert.AreNotEqual(pack.Id, data.Id); Assert.AreEqual(2, calls);
        await client.GetDetailAsync(1426, resourceType: "datapack"); Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void ModrinthOnlyAssociationKeepsTypedDownloadReference()
    {
        var url = "https://modrinth.com/modpack/example/versions";
        var pack = McmodCatalog.ParseDetail(Detail(Link(url, "Modrinth")), 1426);
        Assert.AreEqual(url, pack.ModrinthUrl); Assert.AreEqual("", pack.CurseForgeUrl);
        Assert.IsTrue(McmodCatalog.IsModrinthResource("https://modrinth.com/mod/example", "datapack"));
        Assert.IsFalse(McmodCatalog.IsModrinthResource("https://modrinth.com/mod/example", "modpack"));
        Assert.IsFalse(McmodCatalog.IsModrinthResource("https://modrinth.com.evil.test/modpack/example"));
        Assert.IsFalse(McmodCatalog.IsModrinthResource("https://user:pass@modrinth.com/modpack/example"));
        Assert.IsTrue(McmodCatalog.IsCurseForgeResource("https://www.curseforge.com/minecraft/modpacks/example/files/123"));
    }

    [TestMethod]
    public void DatapackCategoriesComeFromItsOwnFilterBlock()
    {
        var html = """<div class="modlist-filter-block category"><a onclick="window.location='/modlist.html?api=5&amp;category=35'">结构</a><a href="/modlist.html?api=5&amp;category=9">世界</a><a href="/modlist.html?api=5&amp;category=35">重复</a><a href="/modpack.html?category=1">错误命名空间</a></div><div class="modlist-list-frame"></div>""";
        var page = McmodCatalog.ParsePage(html, resourceType: "datapack");
        CollectionAssert.AreEqual(new[] { "35", "9" }, page.Categories.Select(c => c.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "结构", "世界" }, page.Categories.Select(c => c.Name).ToArray());
        Assert.AreEqual("modlist.html?api=5&page=1&category=35", McmodCatalog.ListPath(1, category: 35, resourceType: "datapack"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => McmodCatalog.ListPath(1, category: 1001, resourceType: "datapack"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => McmodCatalog.ListPath(1, category: 35));
    }

    [TestMethod]
    public void IconOnlyCategoriesKeepTheProviderTooltipLabel()
    {
        var html = """<div class="modlist-filter-block category"><li title="农业与饮食"><a href="/modlist.html?api=5&amp;category=4"><svg></svg></a></li></div><div class="modlist-list-frame"></div>""";
        var item = McmodCatalog.ParsePage(html, resourceType: "datapack").Categories.Single();
        Assert.AreEqual("4", item.Id); Assert.AreEqual("农业与饮食", item.Name);
    }
}
