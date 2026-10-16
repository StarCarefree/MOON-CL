using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public class RunningInstanceCardTest
{
    [TestMethod]
    public void Cpu_IsShareOfElapsedCoreTime()
    {
        var previous = new ProcessUsage.Counters(TimeSpan.FromSeconds(1), 1000, 0, 0);
        var current = new ProcessUsage.Counters(TimeSpan.FromSeconds(2), 2L * 1024 * 1024 * 1024, 5000, 1000);
        var sample = ProcessUsage.Measure(previous, current, TimeSpan.FromSeconds(8), 4);
        Assert.AreEqual(3, Math.Round(sample.CpuPercent));
        Assert.AreEqual("2.00 GB", sample.MemoryText);
        Assert.AreEqual("625 B/s", sample.ReceiveText);
        Assert.AreEqual("125 B/s", sample.SendText);
    }

    [TestMethod]
    public void Filter_KeepsRequestedLevels_AndColorsPrefixes()
    {
        var lines = new[]
        {
            "[12:00:00] [main/INFO]: ready",
            "[12:00:01] [main/WARN]: slow",
            "[12:00:02] [main/ERROR]: failed",
            "\tat com.example.Game.run",
            "[12:00:03] [main/DEBUG]: noise"
        };
        var shown = GameLogFilter.Select(lines, debug: false, info: true, warn: true, error: true).ToArray();
        Assert.AreEqual(4, shown.Length);
        Assert.AreEqual(GameLogKind.Error, shown[3].Kind);
        Assert.AreEqual("#C9CED6", GameLogFilter.ColorHex(GameLogKind.Info));
        Assert.AreEqual("#FFB942", GameLogFilter.ColorHex(GameLogKind.Warn));
        Assert.AreEqual("#FF6B6B", GameLogFilter.ColorHex(GameLogKind.Error));
    }
}
