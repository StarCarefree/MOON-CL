using System;
using System.Globalization;

namespace PCL.Core.UI;

/// <summary>CPU, memory and network deltas for one game process. Callers supply the counters so tests need no live process.</summary>
public static class ProcessUsage
{
    public readonly record struct Counters(TimeSpan ProcessorTime, long WorkingSet, long BytesReceived, long BytesSent);

    public readonly record struct Sample(double CpuPercent, long WorkingSet, double ReceiveBytesPerSecond, double SendBytesPerSecond)
    {
        public string CpuText => CpuPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
        public string MemoryText => FormatBytes(WorkingSet);
        public string ReceiveText => FormatRate(ReceiveBytesPerSecond);
        public string SendText => FormatRate(SendBytesPerSecond);
    }

    public static Sample Measure(Counters previous, Counters current, TimeSpan elapsed, int processorCount)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001d);
        var cores = Math.Max(processorCount, 1);
        var cpu = (current.ProcessorTime - previous.ProcessorTime).TotalSeconds / seconds / cores * 100d;
        if (double.IsNaN(cpu) || double.IsInfinity(cpu) || cpu < 0) cpu = 0;
        cpu = Math.Min(cpu, 100d);
        return new Sample(
            cpu,
            Math.Max(current.WorkingSet, 0),
            Rate(current.BytesReceived, previous.BytesReceived, seconds),
            Rate(current.BytesSent, previous.BytesSent, seconds));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return bytes + " B";
        var value = bytes / 1024d;
        if (value < 1024) return value.ToString("0", CultureInfo.InvariantCulture) + " KB";
        value /= 1024d;
        if (value < 1024) return value.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (value / 1024d).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    public static string FormatRate(double bytesPerSecond) => FormatBytes((long)Math.Max(bytesPerSecond, 0)) + "/s";

    private static double Rate(long current, long previous, double seconds)
    {
        if (current < previous) return 0;
        return (current - previous) / seconds;
    }
}
