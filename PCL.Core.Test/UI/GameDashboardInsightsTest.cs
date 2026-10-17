using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using fNbt;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class GameDashboardInsightsTest
{
    private static readonly DateOnly First = new(2026, 1, 30);
    private static GamePlayDay[] History =>
    [
        new(First, "D:/games/A", "生存", 600, 1),
        new(First.AddDays(1), "d:/GAMES/a", "生存", 1800, 0),
        new(First.AddDays(1), "D:/games/B", "创造", 900, 1),
        new(First.AddDays(3), "D:/games/B", "创造", 0, 1)
    ];

    [TestMethod]
    public void ReportFillsEmptyDaysAndComputesStreakAcrossMonths()
    {
        var report = GamePlayReport.Create(History, First, First.AddDays(4));
        Assert.AreEqual(5, report.Daily.Count);
        Assert.AreEqual(3300L, report.Seconds);
        Assert.AreEqual(3, report.Sessions);
        Assert.AreEqual(3, report.ActiveDays);
        Assert.AreEqual(1100L, report.AverageActiveDaySeconds);
        Assert.AreEqual(2, report.LongestStreak);
        Assert.AreEqual(0L, report.Daily[2].Seconds);
        Assert.AreEqual(2, report.Versions.Count);
        Assert.AreEqual("生存", report.Versions[0].Name);
    }

    [TestMethod]
    public void ReportFiltersInclusiveDatesAndCaseInsensitivePaths()
    {
        var report = GamePlayReport.Create(History, First.AddDays(1), First.AddDays(1), "d:/games/A");
        Assert.AreEqual(1800L, report.Seconds);
        Assert.AreEqual(0, report.Sessions);
        Assert.AreEqual(1, report.ActiveDays);
        Assert.AreEqual(1, report.Records.Count);
    }

    [TestMethod]
    public void EmptyReportDoesNotInventActivity()
    {
        var report = GamePlayReport.Create([], First, First.AddDays(6));
        Assert.AreEqual(7, report.Daily.Count);
        Assert.AreEqual(0L, report.Seconds);
        Assert.AreEqual(0L, report.AverageActiveDaySeconds);
        Assert.AreEqual(0, report.LongestStreak);
        Assert.AreEqual(0, report.Versions.Count);
    }

    [TestMethod]
    public void CsvEscapesNamesAndSpreadsheetFormulas()
    {
        var report = GamePlayReport.Create([new(First, "@path", "=SUM(1,2)\"\n世界", 90, 1)], First, First);
        var csv = report.ToCsv();
        StringAssert.Contains(csv, "\"'=SUM(1,2)\"\"\n世界\"");
        StringAssert.Contains(csv, "\"'@path\",90,1.5,1\r\n");
        Assert.AreEqual(1, csv.Split("2026-01-30").Length - 1);
    }

    [TestMethod]
    public void InvalidDateRangesAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GamePlayReport.Create([], First, First.AddDays(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GamePlayReport.Create([], First, First.AddDays(3661)));
    }

    [TestMethod]
    public void DateNotesPersistAndCanBeDeletedIndependentlyOfLayout()
    {
        WithRoot(root =>
        {
            var file = Path.Combine(root, "notes.json");
            var notes = new GameCalendarNotes(file);
            Assert.IsFalse(File.Exists(file));
            notes.Set(First, "坐标 10 64 20\n明晚联机");
            notes.Set(First.AddDays(1), "另一条");
            Assert.AreEqual("坐标 10 64 20\n明晚联机", new GameCalendarNotes(file).Get(First));
            notes.Set(First, " ");
            Assert.AreEqual("", new GameCalendarNotes(file).Get(First));
            Assert.AreEqual("另一条", new GameCalendarNotes(file).Get(First.AddDays(1)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => notes.Set(First, new string('x', 2001)));
        });
    }

    [TestMethod]
    public void CorruptDateNotesAreNotOverwritten()
    {
        WithRoot(root =>
        {
            var file = Path.Combine(root, "notes.json");
            File.WriteAllText(file, "{bad");
            var notes = new GameCalendarNotes(file);
            Assert.IsNotNull(notes.LastError);
            Assert.ThrowsExactly<IOException>(() => notes.Set(First, "new"));
            Assert.AreEqual("{bad", File.ReadAllText(file));
        });
    }

    [TestMethod]
    public void FailedDateNoteSaveRetainsMemoryAndFile()
    {
        WithRoot(root =>
        {
            var file = Path.Combine(root, "notes.json");
            var notes = new GameCalendarNotes(file);
            notes.Set(First, "old");
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsExactly<IOException>(() => notes.Set(First, "new"));
            Assert.AreEqual("old", notes.Get(First));
            Assert.AreEqual("old", new GameCalendarNotes(file).Get(First));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
        });
    }

    [TestMethod]
    public async Task ReadsWorldAndServerPreviewsWithoutWritingGameFiles()
    {
        var root = NewRoot();
        try
        {
            var save = Path.Combine(root, "saves", "world");
            Directory.CreateDirectory(save);
            var data = new NbtCompound("Data")
            {
                new NbtString("LevelName", "测试世界"), new NbtInt("version", 19133),
                new NbtLong("LastPlayed", 1760000000000), new NbtInt("GameType", 1),
                new NbtCompound("Version") { new NbtInt("Id", 3955), new NbtString("Name", "1.21.1") }
            };
            new NbtFile(new NbtCompound("") { data }).SaveToFile(Path.Combine(save, "level.dat"), NbtCompression.GZip);
            var list = new NbtList("servers", NbtTagType.Compound)
            {
                new NbtCompound { new NbtString("name", "测试服"), new NbtString("ip", "play.example.com") },
                new NbtCompound { new NbtString("name", "重复"), new NbtString("ip", "PLAY.EXAMPLE.COM") },
                new NbtCompound { new NbtString("name", "隐藏"), new NbtString("ip", "hidden.example.com"), new NbtByte("hidden", 1) }
            };
            var file = Path.Combine(root, "servers.dat");
            new NbtFile(new NbtCompound("") { list }).SaveToFile(file, NbtCompression.GZip);
            var original = File.ReadAllBytes(file);
            var result = await GameDashboardFiles.LoadAsync(root);
            Assert.IsNull(result.Error);
            Assert.AreEqual(1, result.Worlds.Count);
            Assert.AreEqual("测试世界", result.Worlds[0].Name);
            Assert.AreEqual(1, result.Servers.Count);
            Assert.AreEqual("play.example.com", result.Servers[0].Address);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MissingAndCorruptGameFilesHaveSafeStates()
    {
        var root = NewRoot();
        try
        {
            var empty = await GameDashboardFiles.LoadAsync(root);
            Assert.IsNull(empty.Error);
            Assert.AreEqual(0, empty.Worlds.Count);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
            File.WriteAllText(Path.Combine(root, "servers.dat"), "not nbt");
            var corrupt = await GameDashboardFiles.LoadAsync(root);
            Assert.IsNotNull(corrupt.Error);
            Assert.AreEqual(0, corrupt.Servers.Count);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => GameDashboardFiles.LoadAsync(root, cancellation.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NewWidgetKindsSurviveLayoutRoundTrip()
    {
        WithRoot(root =>
        {
            var layout = new GameDashboardLayout { Widgets = [new() { Kind = "favorites" }, new() { Kind = "worlds" }, new() { Kind = "servers" }, new() { Kind = "actions" }, new() { Kind = "folders" }] };
            var file = Path.Combine(root, "layout.json");
            layout.Save(file);
            Assert.AreEqual(5, GameDashboardLayout.Load(file).Widgets.Count);
        });
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mooncl-insights-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { Directory.Delete(root, true); }
    }
}
