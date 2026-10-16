using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Multiplayer;

public sealed record RoomRouteProbeResult(string RouteId, bool Reachable, long? GameLatencyMs, long? PingMs, string Version, string Error);

public static class RoomRouteProbe
{
    public static async Task<RoomRouteProbeResult> ProbeAsync(RoomRoute route, RoomTransportLease lease, CancellationToken ct)
    {
        if (lease.Target is not { } target) return new(route.Id, false, null, null, "", "房主 XTCP 线路需由访客检测");
        // Never advertise loopback Ping as remote latency for an XTCP/EasyTier proxy.
        var proxied = route.Connection.Mode is "p2p" or "easytier";
        var pingTask = proxied ? Task.FromResult<long?>(null) : PingAsync(target.Host, ct);
        try
        {
            var measured = await MeasureGameAsync(target, proxied, ct).ConfigureAwait(false);
            return new(route.Id, true, measured.Latency, await pingTask.ConfigureAwait(false), measured.Version, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return new(route.Id, false, null, await pingTask.ConfigureAwait(false), "", "未收到远端 Minecraft 状态响应，请检查游戏、授权或网络线路");
        }
    }
    private static async Task<(string Version, long Latency)> MeasureGameAsync(RoomLaunchTarget target, bool waitForOverlay, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (waitForOverlay) window.CancelAfter(TimeSpan.FromSeconds(16));
        while (true)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var version = await RoomConnectivity.ProbeAsync(target, window.Token).ConfigureAwait(false);
                return (version, Math.Max(1, clock.ElapsedMilliseconds));
            }
            catch (Exception ex) when (waitForOverlay && !window.IsCancellationRequested &&
                ex is System.IO.IOException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                await Task.Delay(400, window.Token).ConfigureAwait(false);
            }
        }
    }
    private static async Task<long?> PingAsync(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(800), cancellationToken: ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is PingException or System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}

public static class RoomRouteSelector
{
    public static RoomRouteProbeResult? Choose(IEnumerable<RoomRouteProbeResult> results, string? manuallySelectedId = null)
    {
        var available = results.Where(r => r.Reachable && r.GameLatencyMs is >= 0);
        if (manuallySelectedId is not null) return available.FirstOrDefault(r => r.RouteId == manuallySelectedId);
        return available.OrderBy(r => r.GameLatencyMs).ThenBy(r => r.RouteId, StringComparer.Ordinal).FirstOrDefault();
    }
}
