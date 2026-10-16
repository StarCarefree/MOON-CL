using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>Tracks live instance processes and accumulated play time. Several processes of one instance count as one.</summary>
public static class InstanceActivity
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> Opened = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<WeakReference<RunningBadge>> Badges = [];

    public sealed class RunningBadge
    {
        public required TextBlock Text { get; init; }
        public required Func<string?> Path { get; init; }
        public required Func<bool> Expanded { get; init; }
    }

    public static void Track(RunningBadge badge) => Badges.Add(new WeakReference<RunningBadge>(badge));

    public static int Count(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        foreach (var watcher in Snapshot())
            if (IsLive(watcher) && string.Equals(watcher.version?.PathInstance, path, StringComparison.OrdinalIgnoreCase))
                return 1;
        return 0;
    }

    public static int DistinctCount() => LivePaths().Count;

    /// <summary>Live instance paths, most recently started first. Launching paths that are not yet watched come first.</summary>
    public static string[] PriorityPaths(IEnumerable<string>? launchingPaths = null)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (launchingPaths is not null)
        {
            foreach (var path in launchingPaths)
            {
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                paths.Add(path);
            }
        }
        foreach (var watcher in Snapshot().Reverse())
        {
            if (!IsLive(watcher) || watcher.version?.PathInstance is not { } livePath || !seen.Add(livePath)) continue;
            paths.Add(livePath);
        }
        return paths.ToArray();
    }

    public static void Tick()
    {
        var live = LivePaths();
        var now = DateTime.UtcNow;
        var finished = new List<(string path, int seconds)>();
        lock (Gate)
        {
            foreach (var path in live)
                if (!Opened.ContainsKey(path)) Opened[path] = now;
            foreach (var path in Opened.Keys.Where(path => !live.Contains(path)).ToArray())
            {
                var seconds = Elapsed(Opened[path], now);
                Opened.Remove(path);
                if (seconds > 0) finished.Add((path, seconds));
            }
        }
        Commit(finished);
        RefreshBadges();
    }

    public static void Flush()
    {
        var now = DateTime.UtcNow;
        var finished = new List<(string path, int seconds)>();
        lock (Gate)
        {
            foreach (var pair in Opened.ToArray())
            {
                var seconds = Elapsed(pair.Value, now);
                if (seconds > 0) finished.Add((pair.Key, seconds));
                Opened[pair.Key] = now;
            }
        }
        Commit(finished);
    }

    public static int PlayedSeconds(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        var stored = States.Instance.PlaySeconds[path];
        lock (Gate)
            if (Opened.TryGetValue(path, out var start))
                stored += Elapsed(start, DateTime.UtcNow);
        return stored;
    }

    public static string PlayText(string? path)
    {
        var seconds = PlayedSeconds(path);
        return seconds < 60 ? "" : Lang.Text("Hybrid.PlayTime", Format(seconds));
    }

    public static string PlayDetail(string? path)
    {
        var seconds = PlayedSeconds(path);
        if (seconds <= 0) return Lang.Text("Instance.Overall.Info.PlayTime.None");
        if (seconds < 60) return Lang.Text("Hybrid.PlayUnderMinute");
        return Format(seconds);
    }

    public static string RunningText(int count) => Lang.Text("Hybrid.Running", count);

    public static void RefreshBadges()
    {
        for (var i = Badges.Count - 1; i >= 0; i--)
        {
            if (!Badges[i].TryGetTarget(out var badge))
            {
                Badges.RemoveAt(i);
                continue;
            }
            var count = Count(badge.Path());
            badge.Text.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            badge.Text.Text = count == 0 ? "" : badge.Expanded() ? RunningText(count) : count.ToString();
        }
    }

    private static void Commit(List<(string path, int seconds)> finished)
    {
        foreach (var (path, seconds) in finished)
            States.Instance.PlaySeconds[path] += seconds;
    }

    private static HashSet<string> LivePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var watcher in Snapshot())
            if (IsLive(watcher) && watcher.version?.PathInstance is { } livePath) paths.Add(livePath);
        return paths;
    }

    private static bool IsLive(ModWatcher.Watcher watcher)
    {
        if (watcher.version is null || string.IsNullOrEmpty(watcher.version.PathInstance)) return false;
        if (watcher.State is not (ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running))
            return false;
        try { return watcher.gameProcess is null || !watcher.gameProcess.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static ModWatcher.Watcher[] Snapshot()
    {
        var list = ModWatcher.mcWatcherList;
        try { return list.ToArray(); }
        catch (InvalidOperationException) { return []; }
    }

    private static int Elapsed(DateTime start, DateTime now) => Math.Max(0, (int)(now - start).TotalSeconds);

    private static string Format(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1)
            return Lang.Text("Hybrid.PlayHours", (int)span.TotalHours, span.Minutes);
        return Lang.Text("Hybrid.PlayMinutes", Math.Max(1, span.Minutes));
    }
}
