using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackIdentityTest
{
    [TestMethod]
    public void SearchKeepsModpackHitsAndDropsOtherHosts()
    {
        var html = """
            <div class="search-result-list">
              <div class="result-item"><div class="head"><a href="https://www.mcmod.cn/modpack/549.html">机械动力：星海</a></div><div class="body">星海介绍</div></div>
              <div class="result-item"><div class="head"><a href="https://www.mcmod.cn/class/2021.html">机械动力</a></div><div class="body">这是模组</div></div>
              <div class="result-item"><div class="head"><a href="https://evil.example/modpack/1.html">坏链接</a></div></div>
              <div class="result-item"><div class="head"><a href="/modpack/549.html">重复</a></div></div>
            </div>
            """;
        var hits = McmodCatalog.ParseSearch(html);
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(549, hits[0].WikiId);
        Assert.AreEqual("星海介绍", hits[0].Description);
    }

    [TestMethod]
    public void EmptySearchPageIsNotAnError()
    {
        var hits = McmodCatalog.ParseSearch("<div class=\"search-result-list\">没有找到与关键词有关的内容。</div>");
        Assert.AreEqual(0, hits.Count);
    }

    [TestMethod]
    public void McmodIntroductionWinsOverPlatformIds()
    {
        var clues = new PackClues { Name = "All the Mods 9", FileName = "All the Mods 9-1.2.3.zip", CurseForgeId = "715572", MinecraftVersion = "1.20.1" };
        var identity = PackIdentityResolver.Choose(clues,
            [new PackCandidate { Source = "mcmod", Name = "All the Mods 9", Description = "百科介绍", McmodId = "88", Website = "https://www.mcmod.cn/modpack/88.html" }],
            [new PackCandidate { Source = "curseforge", Name = "All the Mods 9", Description = "Curse 介绍", CurseForgeId = "715572", MinecraftVersion = "1.20.1" }],
            [new PackCandidate { Source = "modrinth", Name = "Unrelated Pack", Description = "不该用", ModrinthId = "abcdefgh" }]);
        Assert.AreEqual("mcmod", identity.Source);
        Assert.AreEqual("百科介绍", identity.Description);
        Assert.AreEqual("715572", identity.CurseForgeId);
        Assert.AreEqual("88", identity.McmodId);
        Assert.AreEqual("", identity.ModrinthId);
    }

    [TestMethod]
    public void WeakNameDoesNotAttachAForeignIntroduction()
    {
        var clues = new PackClues { Name = "Create", FileName = "Create.zip" };
        var identity = PackIdentityResolver.Choose(clues,
            [new PackCandidate { Source = "mcmod", Name = "Create Astral", Description = "另一包", McmodId = "9" }]);
        Assert.AreEqual("local", identity.Source);
        Assert.IsFalse(identity.Description.Contains("另一包"));
    }

    [TestMethod]
    public void ArchiveCluesPreferManifestAndKnownCurseForgeId()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ATM9-0.2.47.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("manifest.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("""{"name":"All the Mods 9","version":"0.2.47","minecraft":{"version":"1.20.1"}}""");
        }
        var clues = PackCluesReader.Read(path, "715572", "curseforge");
        Directory.Delete(root, true);
        Assert.AreEqual("All the Mods 9", clues.Name);
        Assert.AreEqual("0.2.47", clues.Version);
        Assert.AreEqual("1.20.1", clues.MinecraftVersion);
        Assert.AreEqual("715572", clues.CurseForgeId);
        Assert.AreEqual("All the Mods 9", clues.Query);
    }

    [TestMethod]
    public void NestedModrinthIndexIsRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "pack.mrpack");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("pack/modrinth.index.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""{"name":"Star Rail","versionId":"4.4.0","summary":"列车介绍","dependencies":{"minecraft":"1.20.1"}}""");
        }
        var clues = PackCluesReader.Read(path);
        Directory.Delete(root, true);
        Assert.AreEqual("Star Rail", clues.Name);
        Assert.AreEqual("4.4.0", clues.Version);
        Assert.AreEqual("列车介绍", clues.Summary);
    }
}

[TestClass]
public sealed class InstancePlayerDataTest
{
    private string _root = "";
    [TestInitialize] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "pcl-migrate-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void CopyMovesProgressAndLeavesPackFiles()
    {
        var oldIndie = Path.Combine(_root, "old");
        var newIndie = Path.Combine(_root, "new");
        Write(oldIndie, "options.txt", "fov:90");
        Write(oldIndie, "saves/world/level.dat", "world");
        Write(oldIndie, "xaero/world-map/dim.dat", "map");
        Write(oldIndie, "config/xaeroworldmap.txt", "xaero");
        Write(oldIndie, "local/ftbteams/party.snbt", "team");
        Write(oldIndie, "saves/world/ftbquests/progress.snbt", "progress");
        Write(oldIndie, "config/ftbquests/quests/chapter.snbt", "book");
        Write(oldIndie, "mods/old.jar", "mod");
        Write(newIndie, "config/ftbquests/quests/chapter.snbt", "new-book");
        Write(newIndie, "mods/new.jar", "new-mod");
        var receipt = InstancePlayerData.Copy(oldIndie, newIndie);
        Assert.IsTrue(receipt.Verified);
        Assert.AreEqual("fov:90", File.ReadAllText(Path.Combine(newIndie, "options.txt")));
        Assert.AreEqual("map", File.ReadAllText(Path.Combine(newIndie, "xaero", "world-map", "dim.dat")));
        Assert.AreEqual("progress", File.ReadAllText(Path.Combine(newIndie, "saves", "world", "ftbquests", "progress.snbt")));
        Assert.AreEqual("team", File.ReadAllText(Path.Combine(newIndie, "local", "ftbteams", "party.snbt")));
        Assert.AreEqual("new-book", File.ReadAllText(Path.Combine(newIndie, "config", "ftbquests", "quests", "chapter.snbt")));
        Assert.IsFalse(File.Exists(Path.Combine(newIndie, "mods", "old.jar")));
        Assert.AreEqual("new-mod", File.ReadAllText(Path.Combine(newIndie, "mods", "new.jar")));
        Assert.AreEqual("mod", File.ReadAllText(Path.Combine(oldIndie, "mods", "old.jar")));
    }

    [TestMethod]
    public void DeleteRefusesUntilReceiptAndReadyInstanceExist()
    {
        var oldVersion = Path.Combine(_root, "versions", "Old");
        var newVersion = Path.Combine(_root, "versions", "New");
        Write(oldVersion, "Old.json", "{}");
        Write(newVersion, "New.json", "{}");
        Assert.IsFalse(InstancePlayerData.MayDelete(true, oldVersion, newVersion, "New", Path.Combine(_root, "minecraft")));
        InstancePlayerData.WriteReceipt(newVersion, new MigrationReceipt(true, oldVersion, newVersion, ["options.txt"]));
        Assert.IsTrue(InstancePlayerData.MayDelete(true, oldVersion, newVersion, "New", Path.Combine(_root, "minecraft")));
        Assert.IsFalse(InstancePlayerData.MayDelete(false, oldVersion, newVersion, "New"));
        Assert.IsFalse(InstancePlayerData.MayDelete(true, Path.Combine(_root, "minecraft"), newVersion, "New", Path.Combine(_root, "minecraft")));
        Assert.ThrowsExactly<InvalidOperationException>(() => InstancePlayerData.DeleteOldIfSafe(oldVersion, newVersion, "New", false));
        Assert.IsTrue(Directory.Exists(oldVersion));
    }

    [TestMethod]
    public void DeleteRestoresOldWhenNewStopsBeingReady()
    {
        var oldVersion = Path.Combine(_root, "versions", "Old");
        var newVersion = Path.Combine(_root, "versions", "New");
        Write(oldVersion, "keep.txt", "keep");
        Write(newVersion, "New.json", "{}");
        InstancePlayerData.WriteReceipt(newVersion, new MigrationReceipt(true, oldVersion, newVersion, []));
        var calls = 0;
        bool Ready(string folder, string name)
        {
            calls++;
            return calls == 1 && InstancePlayerData.IsReady(folder, name);
        }
        Assert.ThrowsExactly<IOException>(() => InstancePlayerData.DeleteOldIfSafe(oldVersion, newVersion, "New", true, ready: Ready));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(oldVersion, "keep.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "PCL", "instance-quarantine")) && Directory.EnumerateFileSystemEntries(Path.Combine(_root, "PCL", "instance-quarantine")).Any());
    }

    [TestMethod]
    public void SuccessfulDeleteRemovesOnlyTheOldVersion()
    {
        var oldVersion = Path.Combine(_root, "versions", "Old");
        var newVersion = Path.Combine(_root, "versions", "New");
        Write(oldVersion, "Old.json", "{}");
        Write(newVersion, "New.json", "{}");
        InstancePlayerData.WriteReceipt(newVersion, new MigrationReceipt(true, oldVersion, newVersion, []));
        InstancePlayerData.DeleteOldIfSafe(oldVersion, newVersion, "New", true, Path.Combine(_root, "minecraft"));
        Assert.IsFalse(Directory.Exists(oldVersion));
        Assert.IsTrue(File.Exists(Path.Combine(newVersion, "New.json")));
    }

    [TestMethod]
    public void NextNameNeverReusesTheOldFolder()
    {
        var name = InstanceNames.Next("ATM9", "ATM9", "1.2", exists: candidate => candidate == "ATM9 1.2");
        Assert.AreEqual("ATM9 更新", name);
        Assert.AreNotEqual("ATM9", InstanceNames.Next("ATM9", "", "", _ => false));
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
