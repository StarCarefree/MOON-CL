using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PCL.Core.Multiplayer;

/// <summary>Room-specific transport data, never node administration credentials or executable paths.</summary>
[JsonConverter(typeof(RoomConnectionJsonConverter))]
public sealed record RoomConnection(string Mode, string Address = "", int Port = 0,
    string ServerHost = "", int ServerPort = 0, string ProxyName = "", string SecretKey = "",
    string NetworkId = "", string NetworkName = "", string[]? Peers = null)
{
    public override string ToString() => "RoomConnection[redacted]";
    public bool Equals(RoomConnection? other) => other is not null && Mode == other.Mode && Address == other.Address && Port == other.Port &&
        ServerHost == other.ServerHost && ServerPort == other.ServerPort && ProxyName == other.ProxyName && SecretKey == other.SecretKey &&
        NetworkId == other.NetworkId && NetworkName == other.NetworkName && (Peers ?? []).SequenceEqual(other.Peers ?? [], StringComparer.Ordinal);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Mode); hash.Add(Address); hash.Add(Port); hash.Add(ServerHost); hash.Add(ServerPort);
        hash.Add(ProxyName); hash.Add(SecretKey); hash.Add(NetworkId); hash.Add(NetworkName);
        foreach (var peer in Peers ?? []) hash.Add(peer); return hash.ToHashCode();
    }
    public void Validate()
    {
        switch (Mode)
        {
            case "ipv6":
                if (!IsGlobalIpv6(Address)) throw new ArgumentException("请输入有效的公网 IPv6 地址。");
                CheckPort(Port); break;
            case "frp": CheckHost(Address); CheckPort(Port); break;
            case "p2p":
                CheckHost(ServerHost); CheckPort(ServerPort);
                if (!Regex.IsMatch(ProxyName ?? "", "^[A-Za-z0-9_-]{1,64}$") ||
                    !Regex.IsMatch(SecretKey ?? "", "^[A-Za-z0-9_-]{16,128}$"))
                    throw new ArgumentException("P2P 连接标识或密钥无效。");
                break;
            case "zerotier":
                CheckVirtualAddress();
                if (!Regex.IsMatch(NetworkId ?? "", "^[a-fA-F0-9]{16}$")) throw new ArgumentException("ZeroTier Network ID 必须为 16 位十六进制。");
                break;
            case "easytier":
                CheckVirtualAddress();
                CheckText(NetworkName, 64, "EasyTier 网络名称"); CheckText(SecretKey, 128, "EasyTier 网络密码");
                if (Peers is not { Length: >= 1 and <= 8 }) throw new ArgumentException("EasyTier 需要 1–8 个引导节点。");
                foreach (var peer in Peers) CheckPeer(peer);
                break;
            default: throw new ArgumentException("不支持的联机方式。");
        }
    }
    private void CheckVirtualAddress()
    {
        if (!IPAddress.TryParse(Address, out var ip) || Address.Contains('%') || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("请填写房主在虚拟网络内的 IP 地址，而非公网节点地址。");
        CheckPort(Port);
    }
    [JsonIgnore] public string Endpoint => Mode == "p2p" ? "" : FormatEndpoint(Address, Port);
    public static string FormatEndpoint(string host, int port) => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    public static void CheckPort(int port) { if (port is < 1 or > 65535) throw new ArgumentException("端口必须在 1–65535 之间。"); }
    public static bool IsGlobalIpv6(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.ScopeId != 0) return false;
        var bytes = ip.GetAddressBytes(); var first = (bytes[0] << 8) | bytes[1]; var second = (bytes[2] << 8) | bytes[3];
        return (bytes[0] & 0xe0) == 0x20 && !(first == 0x2001 && (second < 0x200 || second == 0xdb8)) &&
            first != 0x2002 && !(first == 0x3fff && second < 0x1000);
    }
    public static void CheckHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Contains('%') || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("请输入有效 IP 地址或主机名，不包含协议、端口或路径。");
    }
    public static void CheckText(string text, int max, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl)) throw new ArgumentException(label + "无效或过长。");
    }
    public static void CheckPeer(string peer)
    {
        if (peer is null || peer.Length > 256 || !Uri.TryCreate(peer, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("tcp" or "udp" or "ws" or "wss" or "quic" or "wg") ||
            string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("引导节点须使用 tcp/udp/ws/wss/quic/wg://主机:端口 格式。");
    }
}

public sealed record RoomGameStatus(string Name = "", string Version = "", string State = "stopped")
{
    public void Validate()
    {
        if (Name.Length > 80 || Version.Length > 80 || Name.Any(char.IsControl) || Version.Any(char.IsControl) ||
            State is not ("stopped" or "launching" or "running" or "failed")) throw new ArgumentException("游戏状态信息无效。");
    }
    [JsonIgnore] public string StateText => State switch { "launching" => "正在启动", "running" => "游戏运行中", "failed" => "启动失败", _ => "未启动" };
    [JsonIgnore] public string Display => (string.IsNullOrWhiteSpace(Name) ? "Minecraft" : Name) + " · " + StateText;
}
public sealed record RoomPresence(string Avatar = "", RoomGameStatus? Game = null)
{
    public void Validate()
    {
        Game?.Validate();
        if (Avatar.Length == 0) return;
        if (Avatar.Length > 8192) throw new ArgumentException("头像过大。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(Avatar); } catch (FormatException) { throw new ArgumentException("头像格式无效。"); }
        if (bytes.Length is < 24 or > 6144 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) is < 1 or > 64 ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) is < 1 or > 64) throw new ArgumentException("头像须为不超过 64×64 的小 PNG。");
    }
}
public sealed record RoomMember(string Id, string Name, bool IsHost, string Avatar = "", RoomGameStatus? Game = null);
public sealed record RoomRoute(string Id, string Name, RoomConnection Connection, bool Enabled = true)
{
    public void Validate()
    {
        if (!Regex.IsMatch(Id ?? "", "^[A-Za-z0-9_-]{1,64}$")) throw new ArgumentException("线路标识无效。");
        RoomConnection.CheckText(Name, 48, "线路名称"); Connection.Validate();
    }
    public static void ValidateAll(RoomRoute[] routes)
    {
        if (routes is null || routes.Length > 12 || routes.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != routes.Length)
            throw new ArgumentException("最多发布 12 条线路，且线路标识不能重复。");
        foreach (var route in routes) route.Validate();
    }
}
public sealed record RoomSnapshot(string Code, string Name, string HostId, RoomMember[] Members,
    RoomConnection? Connection, DateTimeOffset ExpiresAt, RoomRoute[]? Routes = null)
{
    [JsonIgnore] public RoomRoute[] EffectiveRoutes => Routes ?? (Connection is null ? [] : [new("legacy", "默认线路", Connection)]);
}
public sealed record RoomTicket(RoomSnapshot Room, string MemberId, string Token)
{
    public override string ToString() => "RoomTicket[redacted]";
}
public sealed record CreateRoomRequest(string Name, string PlayerName, string Password, RoomConnection? Connection = null, string Avatar = "", RoomGameStatus? Game = null);
public sealed record JoinRoomRequest(string PlayerName, string Password, string Avatar = "", RoomGameStatus? Game = null);
public sealed record RoomLaunchTarget(string Host, int Port, string RoomCode);
