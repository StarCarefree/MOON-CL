using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL.Core.Multiplayer;

namespace PCL;

internal static class RoomPresentation
{
    internal static string Bounded(string? text, int max = 80) => new((text ?? "").Where(c => !char.IsControl(c) && c != '/' && c != '\\').Take(max).ToArray());
    internal static string GameText(RoomGameStatus? game) => game is null ? "Minecraft · 未启动" :
        game.Display + (string.IsNullOrWhiteSpace(game.Version) ? "" : " · " + game.Version);

    internal static RoomGameStatus LocalGame(McInstance? selected)
    {
        try
        {
            var watchers = ModWatcher.mcWatcherList.ToArray();
            var live = watchers.Where(w => w.version is not null &&
                    w.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running)
                .Where(w => Alive(w)).OrderByDescending(StartTime).FirstOrDefault();
            var launching = ModLaunch.LaunchingPaths().ToArray();
            var launchPath = launching.FirstOrDefault();
            McInstance? instance = live?.version ?? selected;
            var state = live is null ? "stopped" : live.State == ModWatcher.Watcher.MinecraftState.Running ? "running" : "launching";
            if (launchPath is not null && (live is null || !string.Equals(live.version.PathInstance, launchPath, StringComparison.OrdinalIgnoreCase)))
            {
                instance = watchers.LastOrDefault(w => string.Equals(w.version?.PathInstance, launchPath, StringComparison.OrdinalIgnoreCase))?.version;
                if (instance is null && string.Equals(selected?.PathInstance, launchPath, StringComparison.OrdinalIgnoreCase)) instance = selected;
                instance ??= ModInstanceList.mcInstanceList.ToArray().SelectMany(pair => pair.Value.ToArray())
                    .FirstOrDefault(candidate => string.Equals(candidate.PathInstance, launchPath, StringComparison.OrdinalIgnoreCase));
                // LaunchingPaths contains only real in-flight launches; never infer running from selection.
                state = "launching";
            }
            else if (live is null && watchers.LastOrDefault() is { State: ModWatcher.Watcher.MinecraftState.Crashed } crashed)
            { instance = crashed.version; state = "failed"; }
            return new RoomGameStatus(Bounded(instance?.Name), Bounded(instance?.Info?.VanillaName), state);
        }
        catch (Exception) { return new RoomGameStatus(); }
    }
    private static bool Alive(ModWatcher.Watcher watcher)
    {
        try { return watcher.gameProcess is not null && !watcher.gameProcess.HasExited; }
        catch (Exception) { return false; }
    }
    private static DateTime StartTime(ModWatcher.Watcher watcher)
    {
        try { return watcher.gameProcess.StartTime; } catch (Exception) { return DateTime.MinValue; }
    }

    internal static string EncodeAvatar(ImageSource? face, ImageSource? hat)
    {
        if (face is null) return "";
        try
        {
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawImage(face, new Rect(0, 0, 32, 32));
                if (hat is not null) drawing.DrawImage(hat, new Rect(0, 0, 32, 32));
            }
            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            if (stream.Length > 6144) return "";
            return Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception) { return ""; }
    }
}

/// <summary>UI-thread-only, bounded PNG cache. Never resolves a URI or keeps an open stream.</summary>
internal sealed class RoomAvatarCache
{
    private readonly Dictionary<string, ImageSource?> _images = new(StringComparer.Ordinal);
    internal int Count => _images.Count;
    internal ImageSource? Decode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (_images.TryGetValue(raw, out var cached)) return cached;
        ImageSource? result = null;
        try
        {
            new RoomPresence(raw).Validate();
            using var stream = new MemoryStream(Convert.FromBase64String(raw), false);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream; image.EndInit();
            if (image.PixelWidth is >= 1 and <= 64 && image.PixelHeight is >= 1 and <= 64)
            { image.Freeze(); result = image; }
        }
        catch (Exception) { /* Invalid remote avatars get initials, not a network fallback. */ }
        if (_images.Count >= 64) _images.Remove(_images.Keys.First());
        if (raw.Length <= 8192) _images[raw] = result;
        return result;
    }
}

internal abstract class RoomObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class RoomMemberPresentation(string id) : RoomObservable
{
    internal string Id { get; } = id;
    private RoomMember? _member;
    private bool _self;
    public string Name => (_member?.Name ?? "") + (_member?.IsHost == true ? " · 房主" : "") + (_self ? " · 你" : "");
    public string Initials => string.IsNullOrWhiteSpace(_member?.Name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(_member.Name).ToUpperInvariant();
    public string Game => RoomPresentation.GameText(_member?.Game);
    public ImageSource? Avatar { get; private set; }
    internal void Update(RoomMember member, bool self, RoomAvatarCache cache)
    {
        if (_member == member && _self == self) return;
        if (_member?.Avatar != member.Avatar) { Avatar = cache.Decode(member.Avatar); Changed(nameof(Avatar)); }
        _member = member; _self = self;
        Changed(nameof(Name)); Changed(nameof(Initials)); Changed(nameof(Game));
    }
}

internal sealed class RoomRoutePresentation(RoomRoute route) : RoomObservable
{
    internal RoomRoute Route { get; private set; } = route;
    public string Title => Route.Name + " · " + Route.Connection.Mode.ToUpperInvariant() + (Route.Enabled ? "" : " · 已禁用");
    public string Endpoint => Route.Connection.Mode == "p2p" ? "游戏端点：建立 XTCP 后分配本机接入地址 · 节点 " + RoomConnection.FormatEndpoint(Route.Connection.ServerHost, Route.Connection.ServerPort) : "游戏端点：" + Route.Connection.Endpoint;
    private string _result = "尚未检测游戏握手";
    public string Result => _result;
    internal void Update(RoomRoute value)
    {
        if (Route == value) return;
        Route = value; Changed(nameof(Title)); Changed(nameof(Endpoint));
    }
    internal void SetResult(string value) { if (_result == value) return; _result = value; Changed(nameof(Result)); }
}
