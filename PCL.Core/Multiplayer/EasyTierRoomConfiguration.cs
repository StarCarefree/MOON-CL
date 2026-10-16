using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace PCL.Core.Multiplayer;

public sealed record NetworkToolPlan(string[] Arguments, IReadOnlyDictionary<string, string> Environment)
{
    public override string ToString() => "NetworkToolPlan[redacted]";
}

/// <summary>EasyTier 2.6.4 native no-TUN + loopback forwarding, matching existing GameLink semantics.</summary>
public static class EasyTierRoomConfiguration
{
    public static NetworkToolPlan Build(RoomConnection connection, bool asHost, int gamePort, int localPort, int rpcPort)
    {
        connection.Validate(); RoomConnection.CheckPort(rpcPort);
        if (connection.Mode != "easytier") throw new ArgumentException("此线路不是 EasyTier。");
        if (asHost)
        {
            RoomConnection.CheckPort(gamePort);
            if (connection.Port != gamePort) throw new ArgumentException("EasyTier 房主发布端口必须与本地游戏端口一致。");
        }
        else RoomConnection.CheckPort(localPort);
        var args = new List<string>
        {
            "--no-tun", "true", "--use-smoltcp", "true", "--private-mode", "true", "--bind-device", "false",
            "--latency-first", "true", "--rpc-portal", "127.0.0.1:" + rpcPort,
            "--rpc-portal-whitelist", "127.0.0.1/32", "--hostname", "MOONCL-" + Guid.NewGuid().ToString("N")[..8],
            "--machine-id", Guid.NewGuid().ToString("D"),
            "--console-log-level", "off", "--file-log-level", "off",
            "--listeners", "tcp://0.0.0.0:0", "udp://0.0.0.0:0",
            "--tcp-whitelist", asHost ? gamePort.ToString() : "0", "--udp-whitelist", "0"
        };
        if (asHost)
        {
            args.Add(IPAddress.Parse(connection.Address).AddressFamily == AddressFamily.InterNetworkV6 ? "--ipv6" : "--ipv4");
            args.Add(connection.Address);
        }
        else
        {
            args.Add("--dhcp");
            args.Add("--port-forward");
            args.Add($"tcp://127.0.0.1:{localPort}/" + RoomConnection.FormatEndpoint(connection.Address, connection.Port));
        }
        foreach (var peer in connection.Peers!) { args.Add("--peers"); args.Add(peer); }
        // Native CLI supports these env vars. Avoid leaking network passwords into process command lines.
        var environment = new Dictionary<string, string>
        {
            ["ET_NETWORK_NAME"] = connection.NetworkName, ["ET_NETWORK_SECRET"] = connection.SecretKey
        };
        return new NetworkToolPlan(args.ToArray(), environment);
    }
}
