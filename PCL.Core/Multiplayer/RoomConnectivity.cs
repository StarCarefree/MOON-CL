using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Multiplayer;

/// <summary>Minecraft Java server-list status, not merely a local XTCP listener accepting TCP.</summary>
public static class RoomConnectivity
{
    public static async Task<string> ProbeAsync(RoomLaunchTarget target, CancellationToken ct)
    {
        RoomConnection.CheckHost(target.Host); RoomConnection.CheckPort(target.Port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        ct = timeout.Token;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(target.Host, target.Port, ct).ConfigureAwait(false);
        using var stream = tcp.GetStream();
        using var handshake = new MemoryStream();
        WriteVarInt(handshake, 0); // handshake packet
        WriteVarInt(handshake, -1); // status query supports version discovery
        var address = Encoding.UTF8.GetBytes(target.Host);
        WriteVarInt(handshake, address.Length); handshake.Write(address);
        handshake.WriteByte((byte)(target.Port >> 8)); handshake.WriteByte((byte)target.Port);
        WriteVarInt(handshake, 1); // next state: status
        using var packets = new MemoryStream();
        WriteVarInt(packets, (int)handshake.Length); handshake.Position = 0; handshake.CopyTo(packets);
        packets.WriteByte(1); packets.WriteByte(0); // empty status request, id 0
        await stream.WriteAsync(packets.ToArray(), ct).ConfigureAwait(false);
        var length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length is < 3 or > 65536) throw new IOException("游戏状态响应大小无效。");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        using var body = new MemoryStream(payload, false);
        if (await ReadVarIntAsync(body, ct).ConfigureAwait(false) != 0) throw new IOException("目标未返回 Minecraft 状态响应。");
        var jsonLength = await ReadVarIntAsync(body, ct).ConfigureAwait(false);
        if (jsonLength < 2 || jsonLength != body.Length - body.Position) throw new IOException("游戏状态响应格式无效。");
        using var status = JsonDocument.Parse(payload.AsMemory((int)body.Position, jsonLength));
        if (!status.RootElement.TryGetProperty("version", out var version) ||
            !version.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            throw new IOException("目标未返回有效的 Minecraft 版本信息。");
        var result = name.GetString() ?? "Minecraft";
        return result.Length > 128 ? result[..128] : result;
    }
    private static void WriteVarInt(Stream stream, int value)
    {
        var remaining = (uint)value;
        do { var next = (byte)(remaining & 0x7f); remaining >>= 7; stream.WriteByte((byte)(next | (remaining == 0 ? 0 : 0x80))); } while (remaining != 0);
    }
    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var value = 0;
        var buffer = new byte[1];
        for (var index = 0; index < 5; index++)
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            if (index == 4 && (buffer[0] & 0xf0) != 0) throw new IOException("游戏状态 VarInt 溢出。");
            value |= (buffer[0] & 0x7f) << (7 * index);
            if ((buffer[0] & 0x80) == 0) return value;
        }
        throw new IOException("游戏状态 VarInt 无效。");
    }
}
