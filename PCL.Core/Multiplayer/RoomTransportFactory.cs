using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Multiplayer;

public sealed record RoomTransportSettings(string FrpcPath = "", FrpNode? Frp = null, string EasyTierPath = "", string ZeroTierToken = "")
{
    public override string ToString() => "RoomTransportSettings[redacted]";
}

public sealed class RoomTransportLease : IDisposable
{
    private readonly FrpProcess? _process;
    private readonly RoomLaunchTarget? _target;
    private readonly Action? _release;
    private readonly Action<RoomTransportLease> _removed;
    private readonly string _status;
    private int _disposed;
    internal RoomTransportLease(RoomRoute route, RoomLaunchTarget? target, FrpProcess? process, string status,
        string[] addresses, Action? release, Action<RoomTransportLease> removed)
    { Route = route; _target = target; _process = process; _status = status; LocalAddresses = addresses; _release = release; _removed = removed; }
    public RoomRoute Route { get; }
    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && (_process?.IsRunning ?? true);
    public RoomLaunchTarget? Target => IsAlive ? _target : null;
    public string Status => IsAlive ? _status : "线路资源已停止";
    public string[] LocalAddresses { get; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _process?.Dispose(); _release?.Invoke(); _removed(this);
    }
}

/// <summary>Per-view resource owner. Several routes on one ZeroTier network share one join lease.</summary>
public sealed class RoomTransportFactory : IDisposable
{
    private readonly HashSet<RoomTransportLease> _owned = [];
    private readonly Dictionary<string, SharedNetwork> _networks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private sealed class SharedNetwork(ZeroTierClient client, ZeroTierNetworkLease lease)
    {
        public ZeroTierClient Client { get; } = client;
        public ZeroTierNetworkLease Lease { get; } = lease;
        public int References { get; set; } = 1;
    }

    public async Task<ZeroTierNetworkInfo?> GetZeroTierInfoAsync(string networkId, RoomTransportSettings settings, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var client = new ZeroTierClient(settings.ZeroTierToken);
        return await client.GetNetworkAsync(networkId, linked.Token).ConfigureAwait(false);
    }

    public async Task<RoomTransportLease> CreateAsync(RoomRoute route, string roomCode, bool asHost, int localGamePort,
        RoomTransportSettings settings, CancellationToken ct)
    {
        route.Validate();
        if (!route.Enabled) throw new InvalidOperationException("此线路已停用。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token; ct.ThrowIfCancellationRequested();
        var c = route.Connection;
        FrpProcess? process = null; Action? release = null; string[] addresses = [];
        RoomLaunchTarget? target = c.Mode == "p2p" ? null : new RoomLaunchTarget(c.Address, c.Port, roomCode);
        var status = "线路已准备，仍需检测游戏端点";
        RoomTransportLease? result = null;
        try
        {
            switch (c.Mode)
            {
                case "frp" when asHost:
                    process = await FrpProcess.StartAsync(settings.FrpcPath, FrpConfiguration.Host(
                        settings.Frp ?? throw new ArgumentException("请配置此 FRP 节点的本机凭据。"), c, localGamePort), ct).ConfigureAwait(false);
                    break;
                case "p2p":
                    if (asHost)
                    {
                        process = await FrpProcess.StartAsync(settings.FrpcPath, FrpConfiguration.Host(
                            settings.Frp ?? throw new ArgumentException("请配置 P2P 节点的本机凭据。"), c, localGamePort), ct).ConfigureAwait(false);
                        status = "XTCP 房主进程运行中；端到端连通性需由访客检测";
                    }
                    else
                    {
                        var port = FreeTcpPort();
                        var node = new FrpNode(c.ServerHost, c.ServerPort, settings.Frp?.Token ?? "", settings.Frp?.TrustedCaFile ?? "");
                        process = await FrpProcess.StartAsync(settings.FrpcPath, FrpConfiguration.Visitor(node, c, port), ct).ConfigureAwait(false);
                        target = new RoomLaunchTarget("127.0.0.1", port, roomCode);
                    }
                    break;
                case "easytier":
                    var rpcPort = FreeTcpPort(); var local = FreeTcpPort();
                    while (local == rpcPort) local = FreeTcpPort();
                    var plan = EasyTierRoomConfiguration.Build(c, asHost, localGamePort, local, rpcPort);
                    process = await FrpProcess.StartToolAsync(settings.EasyTierPath, plan, ct).ConfigureAwait(false);
                    target = new RoomLaunchTarget("127.0.0.1", asHost ? localGamePort : local, roomCode);
                    status = "EasyTier 免虚拟网卡进程运行中；仍需检测远端游戏响应";
                    break;
                case "zerotier":
                    var network = await AcquireNetworkAsync(c.NetworkId, settings.ZeroTierToken, ct).ConfigureAwait(false);
                    release = () => _ = ReleaseNetworkAsync(c.NetworkId);
                    addresses = network.Lease.Info.Addresses;
                    if (asHost && !addresses.Contains(c.Address, StringComparer.OrdinalIgnoreCase))
                        throw new IOException("发布的 ZeroTier 地址不是本机在此网络中的地址，请先读取本机地址。");
                    status = network.Lease.CreatedByLauncher ? "ZeroTier 网络已就绪（本轮加入）" : "ZeroTier 网络已就绪（保留原有网络）";
                    break;
            }
            ct.ThrowIfCancellationRequested();
            result = new RoomTransportLease(route, target, process, status, addresses, release, Removed);
            lock (_owned)
            {
                if (_disposed) throw new OperationCanceledException();
                _owned.Add(result);
            }
            return result;
        }
        catch
        {
            if (result is not null) result.Dispose();
            else { process?.Dispose(); release?.Invoke(); }
            throw;
        }
    }
    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private async Task<SharedNetwork> AcquireNetworkAsync(string id, string token, CancellationToken ct)
    {
        await _networkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_networks.TryGetValue(id, out var existing)) { existing.References++; return existing; }
            var client = new ZeroTierClient(token);
            try
            {
                var joined = await client.JoinAsync(id, ct).ConfigureAwait(false);
                var shared = new SharedNetwork(client, joined); _networks.Add(id, shared); return shared;
            }
            catch { client.Dispose(); throw; }
        }
        finally { _networkGate.Release(); }
    }
    private async Task ReleaseNetworkAsync(string id)
    {
        await _networkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_networks.TryGetValue(id, out var shared) || --shared.References > 0) return;
            _networks.Remove(id);
            try { await shared.Lease.DisposeAsync().ConfigureAwait(false); }
            finally { shared.Client.Dispose(); }
        }
        finally { _networkGate.Release(); }
    }
    private void Removed(RoomTransportLease lease) { lock (_owned) _owned.Remove(lease); }
    public void Dispose()
    {
        RoomTransportLease[] leases;
        lock (_owned) { if (_disposed) return; _disposed = true; leases = _owned.ToArray(); }
        _lifetime.Cancel();
        foreach (var lease in leases) lease.Dispose();
    }
}
