using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

/// <summary>Per-session independent window. Closing it does not own or abort the game process.</summary>
internal sealed class LaunchProgressPresentation : IDisposable
{
    internal LaunchProgressState State { get; } = new();
    private readonly MlsProgressChannel _channel;
    private readonly HybridLaunchWindow _window;
    private readonly DispatcherTimer _timer;
    private readonly Action _cancel;
    private LaunchProgressSnapshot? _displayed;
    private bool _closed;
    private int _disposed;
    private DateTime? _gameStartedUtc;
    private bool _bridgePrepared;

    internal LaunchProgressPresentation(string name, Action cancel)
    {
        _cancel = cancel;
        _channel = new MlsProgressChannel(Paths.Temp);
        _window = new HybridLaunchWindow(); // Intentionally not owned by the launcher window.
        _timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += Tick;
        _window.CancelRequested += CancelRequested;
        _window.Closed += (_, _) => { _closed = true; _timer.Stop(); };
        _window.BeginLaunch(name);
        _timer.Start();
    }

    private void CancelRequested(object? sender, EventArgs e) => _cancel();
    private void Tick(object? sender, EventArgs e) => Paint();

    // Only the session's launch worker reads the file; the dispatcher only paints immutable snapshots.
    internal void Poll()
    {
        if (Volatile.Read(ref _disposed) != 0 || _gameStartedUtc is null) return;
        _channel.Poll(State);
        if (State.TelemetryConnected && !State.ModLoadingComplete && !State.IsTerminal &&
            DateTime.UtcNow - _channel.LastSignalUtc > TimeSpan.FromSeconds(20)) State.SignalLost();
    }
    private void Paint()
    {
        if (_closed) return;
        var snapshot = State.Snapshot;
        if (_displayed == snapshot) return;
        _displayed = snapshot;
        _window.ApplyProgress(snapshot);
    }

    internal void PrepareProcess(ProcessStartInfo info, bool supportsMls)
    {
        if (!supportsMls)
        {
            State.GameStarted("mod-loading-screen 仅支持 Fabric/Quilt，此实例将按游戏窗口状态交接");
            return;
        }
        try
        {
            var uri = new Uri("pack://application:,,,/Plain Craft Launcher 2;component/Resources/mooncl-mls-bridge.jar");
            using var resource = Application.GetResourceStream(uri)?.Stream ?? throw new IOException("Loading progress bridge is missing");
            using var data = new MemoryStream(); resource.CopyTo(data);
            var bytes = data.ToArray();
            var digest = SHA256.HashData(bytes);
            var hash = Convert.ToHexString(digest);
            var cache = Path.Combine(Paths.SharedLocalData, "LoadingBridge"); Directory.CreateDirectory(cache);
            var jar = Path.Combine(cache, hash + ".jar");
            if (!File.Exists(jar) || !SHA256.HashData(File.ReadAllBytes(jar)).AsSpan().SequenceEqual(digest))
            {
                var pending = jar + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(pending, bytes); File.Move(pending, jar, true);
            }
            _channel.Configure(info, jar);
            _bridgePrepared = true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "无法启用模组加载进度，保留原游戏启动流程", ModBase.LogLevel.Debug);
            State.GameStarted("无法启用进度桥接，将等待游戏窗口");
        }
    }

    internal void GameStarted()
    {
        _gameStartedUtc = DateTime.UtcNow;
        State.GameStarted();
    }
    internal bool WaitForTelemetryHandshake => _bridgePrepared && _gameStartedUtc is { } start && !State.TelemetryConnected && DateTime.UtcNow - start < TimeSpan.FromSeconds(4);
    internal void WindowAppeared() { Poll(); State.WindowAppeared(); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Dispose();
        if (_window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            _timer.Stop(); _timer.Tick -= Tick;
            _window.CancelRequested -= CancelRequested;
            if (_closed) return;
            Paint();
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            closeTimer.Tick += (_, _) => { closeTimer.Stop(); if (!_closed) _window.Close(); };
            closeTimer.Start();
        });
    }
}
