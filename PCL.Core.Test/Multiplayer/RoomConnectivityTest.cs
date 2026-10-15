using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Multiplayer;

namespace PCL.Core.Test.Multiplayer;

[TestClass]
public sealed class RoomConnectivityTest
{
    internal static async Task ReplyStatusAsync(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        var request = new byte[1024];
        _ = await stream.ReadAsync(request, ct);
        var json = Encoding.UTF8.GetBytes("{\"version\":{\"name\":\"1.21.1\",\"protocol\":767},\"players\":{\"online\":0,\"max\":8}}");
        // This fixture fits a single-byte packet/string length; production parser supports full VarInt.
        var payload = new byte[json.Length + 3];
        payload[0] = (byte)(json.Length + 2); payload[1] = 0; payload[2] = (byte)json.Length;
        json.CopyTo(payload, 3);
        await stream.WriteAsync(payload, ct);
    }
    [TestMethod]
    public async Task ProbeRequiresActualMinecraftResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serving = ReplyStatusAsync(listener, timeout.Token);
        var result = await RoomConnectivity.ProbeAsync(new RoomLaunchTarget("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "ABCD2345"), timeout.Token);
        Assert.AreEqual("1.21.1", result); await serving;
    }
    [TestMethod]
    public async Task ALocalListenerWithoutRemoteResponseIsNotConnected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var accepting = listener.AcceptTcpClientAsync();
        var probe = RoomConnectivity.ProbeAsync(new RoomLaunchTarget("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "ABCD2345"), timeout.Token);
        using var accepted = await accepting;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await probe);
    }
    [TestMethod]
    public async Task OversizedReplyIsRejectedBeforeAllocation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = listener.AcceptTcpClientAsync();
        var probe = RoomConnectivity.ProbeAsync(new RoomLaunchTarget("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "ABCD2345"), timeout.Token);
        using var accepted = await accepting;
        await accepted.GetStream().WriteAsync(new byte[] { 0x81, 0x80, 0x04 }, timeout.Token); // 65537
        // Keep the socket open: the length itself must fail without waiting for a body.
        await Assert.ThrowsAsync<IOException>(async () => await probe);
    }
}
