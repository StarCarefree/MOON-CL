using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackCatalogTest
{
    [TestMethod]
    public void BuiltInTestPackUsesGiteeRelease()
    {
        var pack = PackCatalog.BuiltIn;
        Assert.AreEqual(PackCatalog.TestDownloadUrl, pack.DownloadUrl);
        Assert.AreEqual(PackCatalog.TestDownloadFile, pack.FileName);
        Assert.AreEqual("https", PackCatalog.HttpUri(pack.DownloadUrl).Scheme);
        Assert.AreEqual(Path.Combine(Path.GetTempPath(), pack.FileName), PackCatalog.SafePath(Path.GetTempPath(), pack.FileName));
    }

    [TestMethod]
    public void MetadataResolvesRelativeUrlsButCannotInjectLocalArchive()
    {
        var packs = PackCatalog.Parse("""{"packs":[{"id":"sre","name":"列车","version":"4.4.0","downloadUrl":"packs/a.mrpack","background":"bg.png","localArchive":"C:/secret.zip"}]}""", new Uri("http://127.0.0.1:8080/catalog.json"));
        Assert.AreEqual("http://127.0.0.1:8080/packs/a.mrpack", packs[0].DownloadUrl);
        Assert.AreEqual("http://127.0.0.1:8080/bg.png", packs[0].Background);
        Assert.AreEqual("", packs[0].LocalArchive);
    }

    [TestMethod]
    [DataRow("../escape.jar")]
    [DataRow("mods/../../escape.jar")]
    [DataRow("C:/escape.jar")]
    [DataRow("mods/x.jar:evil")]
    [DataRow("mods\\x.jar")]
    [DataRow("mods/../x.jar")]
    [DataRow("mods/thing./x.jar")]
    public void UnsafeUpdatePathsAreRejected(string path) =>
        Assert.ThrowsExactly<InvalidDataException>(() => PackCatalog.SafePath(Path.GetTempPath(), path));

    [TestMethod]
    public void NormalUpdatePathRemainsInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-update-test");
        Assert.AreEqual(Path.Combine(root, "mods", "example.jar"), PackCatalog.SafePath(root, "mods/example.jar"));
    }

    [TestMethod]
    [DataRow("file:///C:/payload.zip")]
    [DataRow("javascript:alert(1)")]
    [DataRow("https://user:password@host/pack.zip")]
    public void NonHttpAndEmbeddedCredentialsAreRejected(string url) =>
        Assert.ThrowsExactly<InvalidDataException>(() => PackCatalog.HttpUri(url));

    [TestMethod]
    public void DuplicateIdsAreRejected() => Assert.ThrowsExactly<InvalidDataException>(() =>
        PackCatalog.Parse("""{"packs":[{"id":"sre","name":"a","version":"1"},{"id":"sre","name":"b","version":"2"}]}""", new Uri("https://example.com/catalog.json")));

    [TestMethod]
    [DataRow("instance:removed", "starrail-harpy-express")]
    [DataRow("instance:present", "instance:present")]
    [DataRow("removed-preset", "starrail-harpy-express")]
    [DataRow("starrail-harpy-express", "starrail-harpy-express")]
    public void MissingInstanceSelectionFallsBackAfterDirectoryChange(string selected, string expected) =>
        Assert.AreEqual(expected, PackCatalog.ResolveSelection(selected, [PackCatalog.StarRailId], ["instance:present"]));
}
