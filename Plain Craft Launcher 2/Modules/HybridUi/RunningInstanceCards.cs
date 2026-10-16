using System.Windows.Controls;

namespace PCL;

/// <summary>Places one running-instance card under the matching game version.</summary>
public static class RunningInstanceCards
{
    public static void Refresh(Panel host, McInstance? selected)
    {
        var path = selected?.PathInstance;
        var live = Live(path);
        foreach (var child in host.Children.OfType<RunningInstanceCard>().ToArray())
            if (live.All(item => item.id != child.ProcessId)) host.Children.Remove(child);
        foreach (var (id, window, lines, name) in live)
        {
            if (host.Children.OfType<RunningInstanceCard>().Any(card => card.ProcessId == id)) continue;
            host.Children.Add(new RunningInstanceCard(name, id, window, lines));
        }
    }

    private static List<(int id, Func<nint> window, Func<IReadOnlyList<string?>> lines, string name)> Live(string? path)
    {
        var result = new List<(int, Func<nint>, Func<IReadOnlyList<string?>>, string)>();
        if (string.IsNullOrEmpty(path)) return result;
        ModWatcher.Watcher[] watchers;
        try { watchers = ModWatcher.mcWatcherList.ToArray(); }
        catch (InvalidOperationException) { return result; }
        foreach (var watcher in watchers)
        {
            if (!string.Equals(watcher.version?.PathInstance, path, StringComparison.OrdinalIgnoreCase)) continue;
            if (watcher.State is not (ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running))
                continue;
            int id;
            try
            {
                if (watcher.gameProcess is null || watcher.gameProcess.HasExited) continue;
                id = watcher.gameProcess.Id;
            }
            catch (Exception) { continue; }
            var current = watcher;
            result.Add((id, () => current.WindowHandle, () => current.fullLog, current.version?.Name ?? ""));
        }
        return result;
    }
}
