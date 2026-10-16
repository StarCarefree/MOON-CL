using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class GameLinkSessionTest
{
    [TestMethod]
    public void AddressValidationAcceptsDomainIpv4Ipv6AndRejectsUnsafeArguments()
    {
        foreach (var address in new[] { "localhost", "play.example.com:25565", "127.0.0.1:12345", "[2001:db8::1]:25565", "[::1]" })
            GameLinkSession.ValidateAddress(address);
        foreach (var address in new[] { "", "host --demo", "host\n--server", "host:0", "host:65536", "host/path", "user@host", "host\"", "host?x", "host#x", "http://example.com" })
            Assert.ThrowsExactly<ArgumentException>(() => GameLinkSession.ValidateAddress(address), address);
    }

    [TestMethod]
    public async Task AuthenticatedChannelIsolatesSessionsReportsActualStateAndSendsCommands()
    {
        var jar = Path.Combine(Path.GetTempPath(), "mooncl-link-" + Guid.NewGuid().ToString("N") + ".jar");
        File.WriteAllText(jar, "fixture");
        try
        {
            using var first = new GameLinkSession("first");
            using var second = new GameLinkSession("second");
            var info = new ProcessStartInfo("java") { UseShellExecute = false, Arguments = "-javaagent:authlib.jar Main" };
            var other = new ProcessStartInfo("java") { UseShellExecute = false };
            first.Configure(info, jar); second.Configure(other, jar);
            Assert.AreNotEqual(info.Environment["MOONCL_LINK_TOKEN"], other.Environment["MOONCL_LINK_TOKEN"]);
            Assert.IsFalse(info.Arguments.Contains(info.Environment["MOONCL_LINK_TOKEN"]!));
            Assert.IsTrue(info.Arguments.EndsWith("-javaagent:authlib.jar Main"));
            var port = int.Parse(info.Environment["MOONCL_LINK_PORT"]!);
            using (var intruder = new TcpClient())
            {
                await intruder.ConnectAsync("127.0.0.1", port);
                using var badWriter = new StreamWriter(intruder.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await badWriter.WriteLineAsync("HELLO\t1\t" + other.Environment["MOONCL_LINK_TOKEN"]);
                var one = new byte[1];
                Assert.AreEqual(0, await intruder.GetStream().ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            }
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("HELLO\t1\t" + info.Environment["MOONCL_LINK_TOKEN"]);
            Assert.AreEqual("OK", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            await writer.WriteLineAsync("STATE\t\tready\t\t");
            await UntilAsync(() => first.State.State == "ready");
            Assert.AreEqual("waiting", second.State.State);
            var request = await GameLinkSession.SendAsync(first.Id, "[::1]:25565");
            Assert.AreEqual("CONNECT\t" + request + "\t" + B64("[::1]:25565"), await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.AreEqual("ready", first.State.State, "Command delivery must not fabricate connected state");
            await writer.WriteLineAsync("STATE\t" + request + "\tconnected\t" + B64("[::1]:25565") + "\t" + B64("joined"));
            await UntilAsync(() => first.State.State == "connected");
            Assert.AreEqual("[::1]:25565", first.State.Address);
            var disconnect = await GameLinkSession.SendAsync(first.Id, null);
            Assert.AreEqual("DISCONNECT\t" + disconnect, await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            await writer.WriteLineAsync("STATE\t" + disconnect + "\tdisconnected\t\t");
            await UntilAsync(() => first.State.State == "disconnected");
            Assert.AreEqual("", first.State.Address);
            client.Close();
            await UntilAsync(() => first.State.State == "error");
            first.Dispose();
            Assert.IsFalse(GameLinkSession.Snapshot().Any(s => s.SessionId == first.Id));
        }
        finally { File.Delete(jar); }
    }

    [TestMethod]
    public async Task OversizedAuthenticatedFrameClosesControlChannel()
    {
        var jar = Path.GetTempFileName();
        try
        {
            using var session = new GameLinkSession("oversized");
            var info = new ProcessStartInfo("java") { UseShellExecute = false };
            session.Configure(info, jar);
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", int.Parse(info.Environment["MOONCL_LINK_PORT"]!));
            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync("HELLO\t1\t" + info.Environment["MOONCL_LINK_TOKEN"]);
            Assert.AreEqual("OK", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            await writer.WriteLineAsync(new string('x', 16400));
            await UntilAsync(() => session.State.State == "error");
        }
        finally { File.Delete(jar); }
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static async Task UntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.IsTrue(condition(), "Timed out waiting for game channel state");
    }
}
