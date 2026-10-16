using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackReleaseCheckTest
{
    private const string Curse = """
        {"data":[
          {"id":9,"modId":715572,"displayName":"ATM9-0.2.46","fileName":"old.zip","fileDate":"2024-01-01T00:00:00Z","releaseType":1},
          {"id":10,"modId":715572,"displayName":"ATM9-0.2.48-beta","fileName":"beta.zip","fileDate":"2025-06-01T00:00:00Z","releaseType":2},
          {"id":11,"modId":715572,"displayName":"ATM9-0.2.47","fileName":"current.zip","fileDate":"2025-01-01T00:00:00Z","releaseType":1},
          {"id":12,"modId":1,"displayName":"Other-9.9.9","fileName":"other.zip","fileDate":"2026-01-01T00:00:00Z","releaseType":1}
        ]}
        """;

    [TestMethod]
    public void LatestReleaseIgnoresBetaAndOtherProjects()
    {
        var latest = PackReleaseCheck.LatestRelease("curseforge", Curse, "715572");
        Assert.IsNotNull(latest);
        Assert.AreEqual("11", latest.Id);
        Assert.AreEqual("0.2.47", latest.Version);
    }

    [TestMethod]
    public void SameTokenIsNotNewerAndHigherTokenIs()
    {
        var latest = PackReleaseCheck.LatestRelease("curseforge", Curse, "715572")!;
        Assert.IsFalse(PackReleaseCheck.IsNewer("0.2.47", "", latest));
        Assert.IsFalse(PackReleaseCheck.IsNewer("ATM9-0.2.47.zip", "", latest));
        Assert.IsTrue(PackReleaseCheck.IsNewer("0.2.46", "", latest));
        Assert.IsFalse(PackReleaseCheck.IsNewer("", "", latest));
        Assert.IsFalse(PackReleaseCheck.IsNewer("0.2.46", "11", latest));
    }

    [TestMethod]
    public void MinecraftTokenDoesNotLookLikeAPackUpdate()
    {
        var latest = new PlatformFile("modrinth", "abcd1234", "ver12345", "Create 1.20.1", "1.20.1", DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        Assert.IsFalse(PackReleaseCheck.IsNewer("0.2.47", "", latest));
        Assert.IsFalse(PackReleaseCheck.IsNewer("1.2.3", "", latest));
    }

    [TestMethod]
    public void ModrinthReleaseNumberBeatsAnOlderManifest()
    {
        var json = """[{"id":"aaaaaaaa","project_id":"abcd1234","name":"Release","version_number":"1.4.0","version_type":"release","date_published":"2025-02-01T00:00:00Z"},{"id":"bbbbbbbb","project_id":"abcd1234","name":"Old","version_number":"1.3.0","version_type":"release","date_published":"2024-02-01T00:00:00Z"}]""";
        var latest = PackReleaseCheck.LatestRelease("modrinth", json, "abcd1234");
        Assert.IsNotNull(latest);
        Assert.IsTrue(PackReleaseCheck.IsNewer("1.3.2", "", latest));
        Assert.IsFalse(PackReleaseCheck.IsNewer("1.4.0", "", latest));
        StringAssert.Contains(PackReleaseCheck.Notice("modrinth", "1.3.2", latest), "Modrinth 有新的正式版 1.4.0");
    }

    [TestMethod]
    public void AddressesStayOnThePlatformAndRejectBadIds()
    {
        Assert.AreEqual("https://api.curseforge.com/v1/mods/715572/files?pageSize=50&index=0&sortField=3&sortOrder=desc", PackReleaseCheck.FilesAddress("curseforge", "715572").AbsoluteUri);
        Assert.AreEqual("https://api.modrinth.com/v2/project/abcd1234/version?limit=20", PackReleaseCheck.FilesAddress("modrinth", "abcd1234").AbsoluteUri);
         Assert.ThrowsExactly<ArgumentException>(() => PackReleaseCheck.FilesAddress("curseforge", "abcd1234"));
         Assert.ThrowsExactly<ArgumentException>(() => PackReleaseCheck.FilesAddress("mcmod", "88"));
    }
}
