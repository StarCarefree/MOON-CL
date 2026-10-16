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
        game.Display + (string.IsNullOrWhiteSpace(game.Version) ? "" : " · " + game.Version) +
        (string.IsNullOrWhiteSpace(game.Server) ? "" : " · " + game.Server);
    internal static string LoaderText(McInstance? instance)
    {
        if (instance?.Info is not { } info) return "原版";
        if (info.HasNeoForge) return "NeoForge";
        if (info.HasForge) return "Forge";
        if (info.HasFabric) return "Fabric";
        if (info.HasQuilt) return "Quilt";
        if (info.HasLegacyFabric) return "Legacy Fabric";
        if (info.HasCleanroom) return "Cleanroom";
        if (info.HasLiteLoader) return "LiteLoader";
        return "原版";
    }
    internal static IReadOnlyList<McInstance> SameVersion(string? version)
    {
        var target = (version ?? "").Trim();
        if (target.Length == 0) return [];
        try
        {
            return ModInstanceList.mcInstanceList.ToArray().SelectMany(pair => pair.Value.ToArray())
                .Where(instance => string.Equals(instance.Info?.VanillaName, target, StringComparison.OrdinalIgnoreCase))
                .GroupBy(instance => instance.PathInstance, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        }
        catch (Exception) { return []; }
    }

    internal static RoomGameStatus LocalGame(McInstance? selected, IEnumerable<PCL.Core.Minecraft.Launcher.PackDefinition>? catalog = null, RoomPackShare? chosen = null, bool manual = false)
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
            var pack = manual ? chosen : RoomPackShareResolver.Resolve(instance, catalog) ?? chosen;
            return new RoomGameStatus(Bounded(instance?.Name), Bounded(instance?.Info?.VanillaName), state, pack, HostServer(live));
        }
        catch (Exception) { return new RoomGameStatus(); }
    }
    internal static string HostServer(ModWatcher.Watcher? watcher)
    {
        if (watcher is null) return "";
        try
        {
            var link = PCL.Core.Minecraft.Launcher.GameLinkSession.Snapshot()
                .FirstOrDefault(s => s.ProcessId == watcher.gameProcess?.Id);
            if (link is not null)
                return link.State == "connected" ? link.Address : "";
            var path = Path.Combine(watcher.version.PathIndie, "logs", "latest.log");
            return RoomSetupAutomation.FindJoinedServer(path) ?? "";
        }
        catch (Exception) { return ""; }
    }
    internal static McInstance? LatestRunningInstance()
    {
        try
        {
            return ModWatcher.mcWatcherList.ToArray()
                .Where(w => w.version is not null && Alive(w) &&
                    w.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running)
                .OrderByDescending(StartTime).FirstOrDefault()?.version;
        }
        catch (Exception) { return null; }
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
            // Render directly at display resolution, preserving transparent pixels and pixel edges.
            // The coordinator still caps decoded PNG bytes at 6144; retry at 32px if necessary.
            foreach (var size in new[] { 64, 32 })
            {
                var pixels = AvatarPixels(face, size);
                if (hat is not null)
                {
                    var overlay = AvatarPixels(hat, size);
                    for (var i = 0; i < pixels.Length; i += 4)
                    {
                        var inverseAlpha = 255 - overlay[i + 3];
                        for (var channel = 0; channel < 4; channel++)
                            pixels[i + channel] = (byte)Math.Min(255, overlay[i + channel] + (pixels[i + channel] * inverseAlpha + 127) / 255);
                    }
                }
                var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
                bitmap.Freeze();
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream(); encoder.Save(stream);
                if (stream.Length <= 6144) return Convert.ToBase64String(stream.ToArray());
            }
            return "";
        }
        catch (Exception) { return ""; }
    }

    private static byte[] AvatarPixels(ImageSource source, int size)
    {
        BitmapSource bitmap;
        if (source is BitmapSource native) bitmap = native;
        else
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawImage(source, new Rect(0, 0, size, size));
            var rendered = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual); bitmap = rendered;
        }
        if (bitmap.PixelWidth is < 1 or > 1024 || bitmap.PixelHeight is < 1 or > 1024) throw new InvalidDataException("Avatar dimensions exceed local limits.");
        if (bitmap.Format != PixelFormats.Pbgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var sourcePixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(sourcePixels, bitmap.PixelWidth * 4, 0);
        var output = new byte[size * size * 4];
        var scale = Math.Min((double)size / bitmap.PixelWidth, (double)size / bitmap.PixelHeight);
        var width = Math.Max(1, (int)Math.Round(bitmap.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.PixelHeight * scale));
        var left = (size - width) / 2; var top = (size - height) / 2;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var sx = Math.Min(bitmap.PixelWidth - 1, x * bitmap.PixelWidth / width);
            var sy = Math.Min(bitmap.PixelHeight - 1, y * bitmap.PixelHeight / height);
            Buffer.BlockCopy(sourcePixels, (sy * bitmap.PixelWidth + sx) * 4, output, ((y + top) * size + x + left) * 4, 4);
        }
        return output;
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
    public string Name => _member?.Name ?? "";
    public string Role => (_member?.IsHost == true ? "房主" : "成员") + (_self ? " · 你" : "");
    public string Initials => string.IsNullOrWhiteSpace(_member?.Name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(_member.Name).ToUpperInvariant();
    public string Game => RoomPresentation.GameText(_member?.Game);
    public bool CanJoinServer => !_self && !string.IsNullOrWhiteSpace(_member?.Game?.Server) && !string.IsNullOrWhiteSpace(_member?.Game?.Version);
    public string JoinServerLabel => CanJoinServer ? "进入 " + _member!.Game!.Server : "";
    internal RoomGameStatus? JoinableGame => CanJoinServer ? _member?.Game : null;
    public ImageSource? Avatar { get; private set; }
    internal void Update(RoomMember member, bool self, RoomAvatarCache cache)
    {
        if (_member == member && _self == self) return;
        if (_member?.Avatar != member.Avatar) { Avatar = cache.Decode(member.Avatar); Changed(nameof(Avatar)); }
        _member = member; _self = self;
        Changed(nameof(Name)); Changed(nameof(Role)); Changed(nameof(Initials)); Changed(nameof(Game));
        Changed(nameof(CanJoinServer)); Changed(nameof(JoinServerLabel));
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
