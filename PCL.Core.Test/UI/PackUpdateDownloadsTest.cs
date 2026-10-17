using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackUpdateDownloadsTest
{
    private static JsonObject Mr(string name = "client.mrpack", string project = "AbCd1234") => new()
    {
        ["id"] = "version1", ["project_id"] = project, ["name"] = "Pack 2.0", ["version_number"] = "2.0",
        ["version_type"] = "beta", ["date_published"] = "2026-01-02T00:00:00Z",
        ["game_versions"] = new JsonArray("1.21.1"),
        ["files"] = new JsonArray(new JsonObject { ["primary"] = true, ["filename"] = name,
            ["url"] = "https://cdn.modrinth.com/pack.mrpack", ["hashes"] = new JsonObject { ["sha512"] = new string('a', 128) } })
    };
    private static JsonObject Cf(string name = "client.zip") => new()
    {
        ["id"] = 12, ["modId"] = 123, ["displayName"] = "Pack 2.0", ["fileName"] = name,
        ["fileDate"] = "2026-01-02T00:00:00Z", ["releaseType"] = 1, ["isAvailable"] = true,
        ["downloadUrl"] = "https://edge.forgecdn.net/client.zip", ["gameVersions"] = new JsonArray("1.21.1", "NeoForge"),
        ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = new string('b', 40) })
    };
    private static readonly PackDownloadProject Modrinth = new("modrinth", "AbCd1234");
    private static readonly PackDownloadProject CurseForge = new("curseforge", "123");
    private static string CfResponse(params JsonObject[] items) => new JsonObject { ["data"] = new JsonArray(items) }.ToJsonString();

    [TestMethod]
    public void ModrinthIncludesBetaAndUsesSha512()
    {
        var result = PackUpdateDownloads.Parse(Modrinth, new JsonArray(Mr()).ToJsonString());
        Assert.AreEqual(1, result.Count); Assert.AreEqual("测试版", result[0].ReleaseType);
        Assert.AreEqual(HashAlgorithmName.SHA512, result[0].Algorithm); Assert.AreEqual("2.0", result[0].Version);
        Assert.AreEqual("1.21.1", result[0].Minecraft);
    }

    [TestMethod]
    public void ModrinthRejectsOtherProjectsAndNonClientFiles()
    {
        foreach (var item in new[] { Mr(project: "abcd1234"), Mr("server-pack.mrpack"), Mr("../client.mrpack"), Mr("readme.txt") })
            Assert.AreEqual(0, PackUpdateDownloads.Parse(Modrinth, new JsonArray(item).ToJsonString()).Count);
        var draft = Mr(); draft["status"] = "draft";
        Assert.AreEqual(0, PackUpdateDownloads.Parse(Modrinth, new JsonArray(draft).ToJsonString()).Count);
    }

    [TestMethod]
    public void PrimaryNonArchiveDoesNotHideRealClientPack()
    {
        var item = Mr(); var files = (JsonArray)item["files"]!;
        var pack = files[0]!.AsObject(); pack["primary"] = false;
        files.Insert(0, new JsonObject { ["primary"] = true, ["filename"] = "readme.txt" });
        Assert.AreEqual(1, PackUpdateDownloads.Parse(Modrinth, new JsonArray(item).ToJsonString()).Count);
    }

    [TestMethod]
    public void CurseForgeExcludesUnavailableServerAndForeignFiles()
    {
        var server = Cf(); server["isServerPack"] = true;
        var unavailable = Cf(); unavailable["isAvailable"] = false;
        var foreign = Cf(); foreign["modId"] = 456;
        var result = PackUpdateDownloads.Parse(CurseForge, CfResponse(server, unavailable, foreign, Cf()));
        Assert.AreEqual(1, result.Count); Assert.AreEqual(HashAlgorithmName.SHA1, result[0].Algorithm);
        Assert.AreEqual("1.21.1", result[0].Minecraft);
    }

    [TestMethod]
    public void MissingOrInvalidHashAndUnsafeUrlAreNotDownloadable()
    {
        var noHash = Cf(); noHash["hashes"] = new JsonArray();
        var badHash = Cf(); badHash["hashes"]![0]!["value"] = "wrong";
        var noUrl = Cf(); noUrl["downloadUrl"] = null;
        var badUrl = Cf(); badUrl["downloadUrl"] = "file:///C:/test.zip";
        Assert.AreEqual(0, PackUpdateDownloads.Parse(CurseForge, CfResponse(noHash, badHash, noUrl, badUrl)).Count);
    }
    [TestMethod]
    public void CurseForgeKeepsClientZipWhenOnlyMd5IsPresent()
    {
        var md5 = Cf();
        md5["hashes"] = new JsonArray(new JsonObject { ["algo"] = 2, ["value"] = new string('c', 32) });
        var result = PackUpdateDownloads.Parse(CurseForge, CfResponse(md5));
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(HashAlgorithmName.MD5, result[0].Algorithm);
        Assert.AreEqual(32, result[0].Hash.Length);
    }

    [TestMethod]
    public void PaginationAndProjectIdentityAreBounded()
    {
        Assert.AreEqual(2, PackUpdateDownloads.Projects(new() { CurseForgeId = "123", ModrinthId = "12345678" }).Count);
        Assert.IsTrue(PackUpdateDownloads.Address(CurseForge, 2).Query.Contains("index=100"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PackUpdateDownloads.Address(CurseForge, -1));
        Assert.IsTrue(PackUpdateDownloads.HasMore(CurseForge, "{\"data\":[],\"pagination\":{\"totalCount\":51}}", 0));
        Assert.IsFalse(PackUpdateDownloads.HasMore(CurseForge, "{\"data\":[],\"pagination\":{\"totalCount\":51}}", 1));
        Assert.IsFalse(PackUpdateDownloads.HasMore(Modrinth, "[]", 0));
        Assert.ThrowsExactly<InvalidDataException>(() => PackUpdateDownloads.Parse(Modrinth, new string(' ', PlatformPackCatalog.MaxJsonChars + 1)));
    }
}
