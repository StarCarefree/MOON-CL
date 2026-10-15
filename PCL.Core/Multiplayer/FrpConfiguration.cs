using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCL.Core.Multiplayer;

public sealed record FrpNode(string Host, int Port, string Token, string TrustedCaFile = "");

/// <summary>frp's native TOML format (frp >= 0.61); XTCP delegates NAT traversal to frp.</summary>
public static class FrpConfiguration
{
    private static string Q(string value) => JsonSerializer.Serialize(value);
    public static RoomConnection CreateP2p(FrpNode node)
    {
        Validate(node);
        return new RoomConnection("p2p", ServerHost: node.Host, ServerPort: node.Port,
            ProxyName: "mooncl_" + Guid.NewGuid().ToString("N"), SecretKey: Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }
    public static string Host(FrpNode node, RoomConnection connection, int localPort)
    {
        connection.Validate(); RoomConnection.CheckPort(localPort);
        if (connection.Mode is not ("frp" or "p2p")) throw new ArgumentException("此模式无需 frpc。");
        var text = Header(node);
        text.AppendLine("[[proxies]]");
        text.AppendLine("name = " + Q(connection.Mode == "p2p" ? connection.ProxyName : "mooncl_" + Guid.NewGuid().ToString("N")));
        text.AppendLine("type = " + Q(connection.Mode == "p2p" ? "xtcp" : "tcp"));
        text.AppendLine("localIP = \"127.0.0.1\"");
        text.AppendLine("localPort = " + localPort);
        if (connection.Mode == "p2p") text.AppendLine("secretKey = " + Q(connection.SecretKey));
        else text.AppendLine("remotePort = " + connection.Port);
        return text.ToString();
    }
    public static string Visitor(FrpNode localCredentials, RoomConnection connection, int bindPort)
    {
        connection.Validate(); RoomConnection.CheckPort(bindPort);
        if (connection.Mode != "p2p") throw new ArgumentException("只有 P2P 模式需要访客进程。");
        // Room advertises the node, but node login token/CA are supplied on THIS machine only.
        var text = Header(localCredentials with { Host = connection.ServerHost, Port = connection.ServerPort });
        text.AppendLine("[[visitors]]");
        text.AppendLine("name = " + Q("visitor_" + Guid.NewGuid().ToString("N")));
        text.AppendLine("type = \"xtcp\"");
        text.AppendLine("serverName = " + Q(connection.ProxyName));
        text.AppendLine("secretKey = " + Q(connection.SecretKey));
        text.AppendLine("bindAddr = \"127.0.0.1\"");
        text.AppendLine("bindPort = " + bindPort);
        return text.ToString();
    }
    private static void Validate(FrpNode node)
    {
        RoomConnection.CheckHost(node.Host); RoomConnection.CheckPort(node.Port);
        if (node.Token.Length > 512 || node.TrustedCaFile.Length > 1024) throw new ArgumentException("节点参数过长。");
    }
    private static StringBuilder Header(FrpNode node)
    {
        Validate(node);
        var text = new StringBuilder();
        text.AppendLine("serverAddr = " + Q(node.Host));
        text.AppendLine("serverPort = " + node.Port);
        text.AppendLine("loginFailExit = true");
        text.AppendLine("auth.method = \"token\"");
        text.AppendLine("auth.token = " + Q(node.Token));
        text.AppendLine("transport.tls.enable = true");
        if (!string.IsNullOrWhiteSpace(node.TrustedCaFile)) text.AppendLine("transport.tls.trustedCaFile = " + Q(node.TrustedCaFile));
        // Never create a dashboard or persist frp logs containing connection metadata.
        text.AppendLine("log.to = \"console\"");
        text.AppendLine("log.level = \"warn\"");
        return text;
    }
}
