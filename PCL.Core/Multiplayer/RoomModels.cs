using System;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PCL.Core.Multiplayer;

/// <summary>Only room members receive this descriptor. Never include frps login credentials.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(RoomConnectionJsonConverter))]
public sealed record RoomConnection(string Mode, string Address = "", int Port = 0,
    string ServerHost = "", int ServerPort = 0, string ProxyName = "", string SecretKey = "")
{
    public override string ToString() => "RoomConnection[redacted]";
    public void Validate()
    {
        if (Mode == "ipv6")
        {
            if (!IsGlobalIpv6(Address))
                throw new ArgumentException("请输入公网 IPv6 地址（不能使用回环、局域网或带作用域地址）。");
            CheckPort(Port);
        }
        else if (Mode == "frp") { CheckHost(Address); CheckPort(Port); }
        else if (Mode == "p2p")
        {
            CheckHost(ServerHost); CheckPort(ServerPort);
            if (!Regex.IsMatch(ProxyName ?? "", "^[A-Za-z0-9_-]{1,64}$") ||
                !Regex.IsMatch(SecretKey ?? "", "^[A-Za-z0-9_-]{16,128}$"))
                throw new ArgumentException("P2P 连接标识或密钥无效。");
        }
        else throw new ArgumentException("不支持的联机方式。");
    }

    public string Endpoint => Mode == "p2p" ? "" : FormatEndpoint(Address, Port);
    public static string FormatEndpoint(string host, int port) => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    public static void CheckPort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("端口必须在 1–65535 之间。");
    }
    public static bool IsGlobalIpv6(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.ScopeId != 0) return false;
        var bytes = ip.GetAddressBytes();
        var first = (bytes[0] << 8) | bytes[1];
        var second = (bytes[2] << 8) | bytes[3];
        return (bytes[0] & 0xe0) == 0x20 && !(first == 0x2001 && (second < 0x200 || second == 0xdb8)) &&
            first != 0x2002 && !(first == 0x3fff && second < 0x1000);
    }
    public static void CheckHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Contains('%') ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("请输入有效的 IP 地址或主机名，不包含协议、端口或路径。");
    }
}

public sealed record RoomMember(string Id, string Name, bool IsHost);
public sealed record RoomSnapshot(string Code, string Name, string HostId, RoomMember[] Members,
    RoomConnection? Connection, DateTimeOffset ExpiresAt);
public sealed record RoomTicket(RoomSnapshot Room, string MemberId, string Token)
{
    public override string ToString() => "RoomTicket[redacted]";
}
public sealed record CreateRoomRequest(string Name, string PlayerName, string Password, RoomConnection? Connection);
public sealed record JoinRoomRequest(string PlayerName, string Password);

/// <summary>Future game launch integration consumes this; this layer never starts Minecraft.</summary>
public sealed record RoomLaunchTarget(string Host, int Port, string RoomCode);
