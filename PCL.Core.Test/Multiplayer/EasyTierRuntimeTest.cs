using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Multiplayer;

namespace PCL.Core.Test.Multiplayer;

[TestClass]
public sealed class EasyTierRuntimeTest
{
    private static string Core()
    {
        var path = Environment.GetEnvironmentVariable("MOONCL_TEST_EASYTIER_CORE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            Assert.Inconclusive("Set MOONCL_TEST_EASYTIER_CORE to a trusted EasyTier 2.6.4 executable for native tests.");
        return path!;
    }
    private static int Port()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
    private static ProcessStartInfo StartInfo(string executable, NetworkToolPlan plan)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in plan.Arguments) start.ArgumentList.Add(arg);
        foreach (var pair in plan.Environment) start.Environment[pair.Key] = pair.Value;
        return start;
    }
    [TestMethod]
    public async Task Native264StartsGeneratedHostAndGuestPlans()
    {
        var executable = Core();
        var connection = new RoomConnection("easytier", "10.144.14.1", 25565, SecretKey: "synthetic-test-only", NetworkName: "mooncl-check", Peers: ["tcp://127.0.0.1:11010"]);
        foreach (var host in new[] { true, false })
        {
            var start = StartInfo(executable, EasyTierRoomConfiguration.Build(connection, host, 25565, Port(), Port()));
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null); var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try { await Task.Delay(800); Assert.IsFalse(process.HasExited, "Native EasyTier rejected the generated startup plan."); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            await Task.WhenAll(output, error);
        }
    }
    [TestMethod]
    public async Task NativeNoTunGuestReachesMinecraftThroughLoopbackForward()
    {
        var executable = Core();
        using var game = new TcpListener(IPAddress.Loopback, 0); game.Start();
        var gamePort = ((IPEndPoint)game.LocalEndpoint).Port; var nodePort = Port();
        var connection = new RoomConnection("easytier", "10.144.14.1", gamePort, SecretKey: Guid.NewGuid().ToString("N"),
            NetworkName: "mooncl-test-" + Guid.NewGuid().ToString("N"), Peers: ["tcp://127.0.0.1:" + nodePort]);
        var hostPlan = EasyTierRoomConfiguration.Build(connection, true, gamePort, 0, Port());
        hostPlan = hostPlan with { Arguments = hostPlan.Arguments.Select(a => a == "tcp://0.0.0.0:0" ? "tcp://127.0.0.1:" + nodePort : a).ToArray() };
        using var host = Process.Start(StartInfo(executable, hostPlan))!;
        var hostOutput = host.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var hostError = host.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var factory = new RoomTransportFactory();
        try
        {
            await Task.Delay(1200, timeout.Token); Assert.IsFalse(host.HasExited, "Native host failed to start.");
            var route = new RoomRoute("native-et", "本机 EasyTier", connection);
            using var guest = await factory.CreateAsync(route, "ABCDEFGH", false, 0, new(EasyTierPath: executable), timeout.Token);
            Assert.AreEqual("127.0.0.1", guest.Target!.Host);
            var serving = RoomConnectivityTest.ReplyStatusAsync(game, timeout.Token);
            var result = await RoomRouteProbe.ProbeAsync(route, guest, timeout.Token);
            Assert.IsTrue(result.Reachable, "The no-TUN forward did not return a remote Minecraft response.");
            Assert.AreEqual("1.21.1", result.Version); Assert.IsNull(result.PingMs, "Do not measure loopback as peer latency.");
            await serving; guest.Dispose(); Assert.IsFalse(guest.IsAlive);
        }
        finally
        {
            timeout.Cancel();
            if (!host.HasExited) { host.Kill(true); await host.WaitForExitAsync(); }
            await Task.WhenAll(hostOutput, hostError);
        }
    }
}
