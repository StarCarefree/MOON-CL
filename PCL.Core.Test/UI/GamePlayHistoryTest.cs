using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class GamePlayHistoryTest
{
    private static readonly TimeZoneInfo Zone = Fixed(TimeSpan.FromHours(8));

    [TestMethod]
    public void FrequentTicksRetainFractionalSeconds()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var history = New(dir, clock);
        GamePlayInstance[] live = [new("D:/mc/A", "A")];
        history.Update(live, clock.Value);
        for (var i = 0; i < 100; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(120));
            history.Update(live, clock.Value);
        }
        Assert.AreEqual(12L, One(history.Snapshot(clock.Value)).Seconds);
        history.Flush(clock.Value);
        Assert.AreEqual(12L, One(New(dir, clock).Snapshot(clock.Value)).Seconds);
    }

    [TestMethod]
    public void ExpiredHistoryIsPrunedWithoutRestarting()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var history = New(dir, clock);
        history.Update([new("D:/mc/A", "A")], clock.Value);
        clock.Advance(TimeSpan.FromSeconds(20));
        history.Update([], clock.Value);
        clock.Advance(TimeSpan.FromDays(366 * 4));
        Assert.AreEqual(0, history.Snapshot(clock.Value).Count);
        history.Update([new("D:/mc/B", "B")], clock.Value);
        Assert.IsTrue(history.Snapshot(clock.Value).All(day => day.InstanceName == "B"));
        Assert.IsFalse(File.ReadAllText(dir.File).Contains("D:/mc/A"));
    }

    [TestMethod]
    public void FailedWritesAreThrottledBetweenFlushes()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var writes = 0;
        var history = new GamePlayHistory(dir.File, Zone, () => clock.Value, (_, _) => { writes++; throw new IOException("full"); });
        GamePlayInstance[] live = [new("D:/mc/A", "A")];
        history.Update(live, clock.Value);
        for (var i = 0; i < 10; i++) { clock.Advance(TimeSpan.FromSeconds(1)); history.Update(live, clock.Value); }
        Assert.AreEqual(1, writes);
        history.Flush(clock.Value);
        Assert.AreEqual(2, writes);
    }

    [TestMethod]
    public void SamePathProcessesCountAsOneSession()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var history = New(dir, clock);
        history.Update([new(@"D:\mc\A", "A"), new(@"d:\mc\a", "A")], clock.Value);
        var day = One(history.Snapshot(clock.Value));
        Assert.AreEqual(1, day.Sessions);
        Assert.AreEqual(0, day.Seconds);
        Assert.AreEqual(@"D:\mc\A", day.InstancePath);
        clock.Advance(TimeSpan.FromSeconds(30));
        history.Update([new(@"D:\MC\A", "A")], clock.Value);
        Assert.AreEqual(1, One(history.Snapshot(clock.Value)).Sessions);
        Assert.AreEqual(30, One(history.Snapshot(clock.Value)).Seconds);
    }

    [TestMethod]
    public void SnapshotIncludesLiveSecondsAndIsIdempotent()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var history = New(dir, clock);
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        clock.Advance(TimeSpan.FromSeconds(12));
        var first = history.Snapshot(clock.Value);
        var second = history.Snapshot(clock.Value);
        Assert.AreEqual(12, One(first).Seconds);
        Assert.AreEqual(12, One(second).Seconds);
        Assert.AreEqual(1, One(second).Sessions);
        var restored = New(dir, clock);
        Assert.AreEqual(0, One(restored.Snapshot(clock.Value)).Seconds);
        Assert.AreEqual(1, One(restored.Snapshot(clock.Value)).Sessions);
    }

    [TestMethod]
    public void StopAddsTailAndDoesNotCountLaterIdle()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var history = New(dir, clock);
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        clock.Advance(TimeSpan.FromMinutes(2));
        history.Update([], clock.Value);
        Assert.AreEqual(120, One(history.Snapshot(clock.Value)).Seconds);
        clock.Advance(TimeSpan.FromHours(5));
        history.Update([], clock.Value);
        Assert.AreEqual(120, One(history.Snapshot(clock.Value)).Seconds);
    }

    [TestMethod]
    public void FlushKeepsLiveSessionWithoutDoubleCounting()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var history = New(dir, clock);
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        clock.Advance(TimeSpan.FromSeconds(40));
        history.Flush(clock.Value);
        Assert.AreEqual(1, One(history.Snapshot(clock.Value)).Sessions);
        Assert.AreEqual(40, One(history.Snapshot(clock.Value)).Seconds);
        var restored = New(dir, clock);
        Assert.AreEqual(40, One(restored.Snapshot(clock.Value)).Seconds);
        clock.Advance(TimeSpan.FromSeconds(20));
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        Assert.AreEqual(1, One(history.Snapshot(clock.Value)).Sessions);
        Assert.AreEqual(60, One(history.Snapshot(clock.Value)).Seconds);
    }

    [TestMethod]
    public void MidnightSplitsSecondsAndKeepsSessionOnStartDay()
    {
        var clock = new Clock(At(2026, 7, 14, 23, 59, 50));
        using var dir = new Temp();
        var history = New(dir, clock);
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        clock.Advance(TimeSpan.FromSeconds(20));
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        var days = history.Snapshot(clock.Value);
        Assert.AreEqual(2, days.Count);
        Assert.AreEqual(new DateOnly(2026, 7, 14), days[0].Date);
        Assert.AreEqual(10, days[0].Seconds);
        Assert.AreEqual(1, days[0].Sessions);
        Assert.AreEqual(new DateOnly(2026, 7, 15), days[1].Date);
        Assert.AreEqual(10, days[1].Seconds);
        Assert.AreEqual(0, days[1].Sessions);
    }

    [TestMethod]
    public void DstSpringForwardUsesInjectedZone()
    {
        var zone = Dst();
        if (zone is null)
        {
            Assert.Inconclusive("No DST zone is available.");
            return;
        }
        var start = new DateTimeOffset(2026, 3, 8, 6, 30, 0, TimeSpan.Zero);
        var clock = new Clock(start);
        using var dir = new Temp();
        var history = new GamePlayHistory(dir.File, zone, () => clock.Value);
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        clock.Advance(TimeSpan.FromHours(1));
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        var days = history.Snapshot(clock.Value);
        Assert.AreEqual(3600, days.Sum(day => day.Seconds));
        Assert.AreEqual(1, days.Sum(day => day.Sessions));
        Assert.IsTrue(days.All(day => day.Seconds >= 0));
        Assert.IsTrue(days.Count is 1 or 2);
    }

    [TestMethod]
    public void ReverseClockDoesNotCreateNegativeSeconds()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var history = New(dir, clock);
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        clock.Advance(TimeSpan.FromSeconds(15));
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        clock.Advance(TimeSpan.FromSeconds(-40));
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        Assert.AreEqual(15, One(history.Snapshot(clock.Value)).Seconds);
        Assert.IsTrue(history.Snapshot(clock.Value).All(day => day.Seconds >= 0));
    }

    [TestMethod]
    public void RestartReadsCommittedTimeWithoutCountingOfflineGap()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var history = New(dir, clock);
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        clock.Advance(TimeSpan.FromSeconds(25));
        history.Update([], clock.Value);
        clock.Advance(TimeSpan.FromHours(3));
        var restored = New(dir, clock);
        restored.Update([], clock.Value);
        Assert.AreEqual(25, One(restored.Snapshot(clock.Value)).Seconds);
        Assert.AreEqual(1, One(restored.Snapshot(clock.Value)).Sessions);
        Assert.AreEqual(history.TrackingSince, restored.TrackingSince);
    }

    [TestMethod]
    public void CorruptAndOversizedFilesFallBackSafely()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var corrupt = new Temp();
        File.WriteAllText(corrupt.File, "{not json");
        var broken = New(corrupt, clock);
        Assert.AreEqual(0, broken.Snapshot(clock.Value).Count);
        Assert.IsNotNull(broken.LastError);
        broken.Update([new(@"D:\mc\A", "A")], clock.Value);
        Assert.AreEqual(1, One(broken.Snapshot(clock.Value)).Sessions);

        using var huge = new Temp();
        File.WriteAllText(huge.File, new string('x', (8 * 1024 * 1024) + 1));
        var oversized = New(huge, clock);
        Assert.AreEqual(0, oversized.Snapshot(clock.Value).Count);
        Assert.IsNull(oversized.LastError);
    }

    [TestMethod]
    public void NoRunningGameDoesNotCreateFileAndSnapshotDoesNotWrite()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var before = clock.Value;
        var history = New(dir, clock);
        Assert.IsFalse(File.Exists(dir.File));
        Assert.IsTrue(history.TrackingSince >= before);
        history.Update([], clock.Value);
        history.Flush(clock.Value);
        history.Snapshot(clock.Value);
        Assert.IsFalse(File.Exists(dir.File));
    }

    [TestMethod]
    public void FailedSaveKeepsMemoryAndRetries()
    {
        var clock = new Clock(At(2026, 7, 14, 10));
        using var dir = new Temp();
        var fail = true;
        var history = new GamePlayHistory(dir.File, Zone, () => clock.Value, (_, json) =>
        {
            if (fail) throw new IOException("disk full");
            File.WriteAllText(dir.File, json);
        });
        history.Update([new(@"D:\mc\A", "A")], clock.Value);
        Assert.IsNotNull(history.LastError);
        Assert.AreEqual(1, One(history.Snapshot(clock.Value)).Sessions);
        Assert.IsFalse(File.Exists(dir.File));
        fail = false;
        clock.Advance(TimeSpan.FromSeconds(5));
        history.Flush(clock.Value);
        Assert.IsNull(history.LastError);
        Assert.IsTrue(File.Exists(dir.File));
        var restored = New(dir, clock);
        Assert.AreEqual(5, One(restored.Snapshot(clock.Value)).Seconds);
    }

    [TestMethod]
    public void CaseDifferenceDoesNotSplitPath()
    {
        using var dir = new Temp();
        File.WriteAllText(dir.File, """
        {"schema":1,"trackingSince":"2026-01-01T00:00:00+08:00","days":[
          {"date":"2026-07-14","path":"D:\\MC\\a","name":"Old","seconds":-9,"sessions":-2},
          {"date":"2026-07-14","path":"d:\\mc\\A","name":"New","seconds":7,"sessions":1},
          {"date":"not-a-date","path":"D:\\mc\\A","name":"Bad","seconds":100,"sessions":4}
        ]}
        """);
        var clock = new Clock(At(2026, 7, 14, 12));
        var history = New(dir, clock);
        var day = One(history.Snapshot(clock.Value));
        Assert.AreEqual(7, day.Seconds);
        Assert.AreEqual(1, day.Sessions);
        Assert.AreEqual("New", day.InstanceName);
    }

    [TestMethod]
    public void LoadedGarbageAndAncientRowsAreDropped()
    {
        using var dir = new Temp();
        var ancient = DateOnly.FromDateTime(DateTime.Now).AddYears(-4).ToString("yyyy-MM-dd");
        File.WriteAllText(dir.File, "{\"schema\":1,\"trackingSince\":\"2024-02-03T04:05:06+08:00\",\"days\":[" +
            "{\"date\":\"" + ancient + "\",\"path\":\"D:\\\\old\",\"name\":\"Old\",\"seconds\":999999999,\"sessions\":99}," +
            "{\"date\":\"2026-07-14\",\"path\":\"D:\\\\mc\\\\A\",\"name\":\"A\",\"seconds\":999999999,\"sessions\":999999}" +
            "]}", Encoding.UTF8);
        var history = new GamePlayHistory(dir.File, Zone);
        Assert.AreEqual(new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.FromHours(8)), history.TrackingSince);
        if (history.Snapshot(DateTimeOffset.Now).Count == 0) return;
        var day = history.Snapshot(DateTimeOffset.Now).Single(item => item.InstancePath == @"D:\mc\A");
        Assert.AreEqual(48L * 60 * 60, day.Seconds);
        Assert.AreEqual(10_000, day.Sessions);
        Assert.IsFalse(history.Snapshot(DateTimeOffset.Now).Any(item => item.InstancePath == @"D:\old"));
    }

    private static GamePlayHistory New(Temp dir, Clock clock) => new(dir.File, Zone, () => clock.Value);

    private static GamePlayDay One(IReadOnlyList<GamePlayDay> days) => days.Single();

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.FromHours(8));

    private static TimeZoneInfo Fixed(TimeSpan offset) =>
        TimeZoneInfo.CreateCustomTimeZone("game-play-fixed", offset, "fixed", "fixed");

    private static TimeZoneInfo? Dst()
    {
        foreach (var id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return null;
    }

    private sealed class Clock(DateTimeOffset value)
    {
        public DateTimeOffset Value { get; private set; } = value;
        public void Advance(TimeSpan by) => Value += by;
    }

    private sealed class Temp : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcl-play-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(_dir, "game-play-history.json");
        public Temp() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
    }
}
