using System.Diagnostics;

namespace PCL;

/// <summary>Cumulative IO bytes for one process. The card turns the delta into a per-second rate.</summary>
internal static class ProcessNetworkBytes
{
    public static bool TryRead(int processId, out long received, out long sent)
    {
        received = 0;
        sent = 0;
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            received = process.WorkingSet64 >= 0 ? ReadCounter(process, true) : 0;
            sent = ReadCounter(process, false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long ReadCounter(Process process, bool inbound)
    {
        try
        {
            var name = ProcessInstance(process.Id);
            if (name is null) return 0;
            using var counter = new PerformanceCounter(
                "Process",
                inbound ? "IO Other Bytes/sec" : "IO Data Bytes/sec",
                name,
                true);
            _ = counter.NextValue();
            return Math.Max(0, (long)counter.RawValue);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string? ProcessInstance(int processId)
    {
        try
        {
            var category = new PerformanceCounterCategory("Process");
            foreach (var instance in category.GetInstanceNames())
            {
                using var id = new PerformanceCounter("Process", "ID Process", instance, true);
                if ((int)id.RawValue == processId) return instance;
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }
}
