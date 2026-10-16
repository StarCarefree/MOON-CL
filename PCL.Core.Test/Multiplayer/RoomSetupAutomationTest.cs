using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Multiplayer;

namespace PCL.Core.Test.Multiplayer;

[TestClass]
public sealed class RoomSetupAutomationTest
{
    private string _root = "";
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "mooncl-setup-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private int? Parse(string contents)
    {
        var log = Path.Combine(_root, "latest.log"); File.WriteAllText(log, contents, new UTF8Encoding(false));
        return RoomSetupAutomation.FindMinecraftLanPort(log);
    }
    [TestMethod]
    public void FindsLastLanAnnouncementAndRejectsInvalidPorts()
    {
        Assert.AreEqual(52123, Parse("[12:00:00] [Server thread/INFO]: Started serving on 50123\n[12:00:02] [Render thread/INFO]: [System] [CHAT] Local game hosted on port 52123\n"));
        Assert.IsNull(Parse("[Server thread/INFO]: Started serving on 0\n[Server thread/INFO]: Started serving on 65536\n[Server thread/INFO]: Started serving on 123456\n"));
        Assert.AreEqual(65535, Parse("[Server thread/INFO]: Started serving on 65535\n"));
        Assert.IsNull(Parse("Connecting to multiplayer.example.com, 25565\n"));
    }
    [TestMethod]
    public void StopsAndPlayerChatDoNotProduceFalseLanSuggestion()
    {
        Assert.IsNull(Parse("[Server thread/INFO]: Started serving on 50123\n[Server thread/INFO]: Stopping server\n"));
        Assert.IsNull(Parse("[Render thread/INFO]: [CHAT] <Friend> Local game hosted on port 52123\n"));
        Assert.IsNull(Parse("[Render thread/INFO]: [CHAT] [Friend] Started serving on 52123\n"));
        Assert.IsNull(Parse("[Render thread/INFO]: [CHAT] Friend: Local game hosted on port 52123\n"));
        Assert.IsNull(Parse("[Other/INFO]: Started serving on 52123\n"));
        Assert.AreEqual(51000, Parse("[Server thread/INFO]: Started serving on 50123\n[Server thread/INFO]: Stopping server\n[Server thread/INFO]: Started serving on 51000\n"));
    }
    [TestMethod]
    public void ReadsOnlyBoundedTailAndToleratesActiveLogsAndMissingFiles()
    {
        var text = "[Server thread/INFO]: Started serving on 50123\n" + new string('x', 270000) + "\n";
        Assert.IsNull(Parse(text));
        Assert.AreEqual(51000, Parse(text + "[Server thread/INFO]: Started serving on 51000\n"));
        var path = Path.Combine(_root, "latest.log");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.AreEqual(51000, RoomSetupAutomation.FindMinecraftLanPort(path));
        Assert.IsNull(RoomSetupAutomation.FindMinecraftLanPort(Path.Combine(_root, "missing.log")));
        Assert.IsNull(RoomSetupAutomation.FindMinecraftLanPort(""));
    }
    [TestMethod]
    public void ToolDiscoveryIsBoundedAndNeverSearchesRecursivelyOrAcceptsPathTraversal()
    {
        var tools = Path.Combine(_root, "tools"); Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "frpc.exe"), "fixture-not-executable");
        Assert.IsNull(RoomSetupAutomation.FindExecutable("frpc.exe", [_root]));
        Assert.AreEqual(Path.Combine(tools, "frpc.exe"), RoomSetupAutomation.FindExecutable("frpc.exe", ["", _root, tools]));
        Assert.IsNull(RoomSetupAutomation.FindExecutable("../frpc.exe", [tools]));
        Assert.IsNull(RoomSetupAutomation.FindExecutable("frpc.bat", [tools]));
        Assert.IsNull(RoomSetupAutomation.FindExecutable("frpc.exe", Enumerable.Repeat(_root, 24).Append(tools)));
    }
    [TestMethod]
    public void GeneratedDefaultsAreValidUniqueAndDoNotIncludeUnsafeInput()
    {
        var name = RoomSetupAutomation.GenerateNetworkName("ABCD2345");
        Assert.AreEqual("mooncl-abcd2345", name);
        Assert.IsFalse(RoomSetupAutomation.GenerateNetworkName("../BAD CODE\n").Contains('/'));
        var secrets = Enumerable.Range(0, 20).Select(_ => RoomSetupAutomation.GenerateNetworkSecret()).ToArray();
        Assert.AreEqual(20, secrets.Distinct().Count()); Assert.IsTrue(secrets.All(s => s.Length == 48 && s.All(char.IsAsciiHexDigit)));
        new RoomConnection("easytier", "10.144.14.1", 25565, SecretKey: secrets[0], NetworkName: name, Peers: ["tcp://localhost:11010"]).Validate();
        Assert.AreEqual("IPv6 直连 · 1", RoomSetupAutomation.SuggestRouteName("ipv6", 1));
        Assert.AreEqual("EasyTier 组网 · 12", RoomSetupAutomation.SuggestRouteName("easytier", 99));
    }
    [TestMethod]
    public void JoinedServerUsesLatestConnectionAndClearsAfterDisconnect()
    {
        var path = Path.Combine(Path.GetTempPath(), "mooncl-room-server-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, "[12:00:00] [Render thread/INFO]: Connecting to play.example.com\n[12:01:00] [Render thread/INFO]: Connecting to 192.0.2.10:25566\n[12:02:00] [Render thread/INFO]: Lost connection: timed out\n");
        try { Assert.IsNull(RoomSetupAutomation.FindJoinedServer(path)); }
        finally { File.Delete(path); }
        path = Path.Combine(Path.GetTempPath(), "mooncl-room-server-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, "[12:00:00] [Render thread/INFO]: Connecting to old.example.com\n[12:01:00] [Render thread/INFO]: Connected to a modded server. play.example.org:25566\n");
        try { Assert.AreEqual("play.example.org:25566", RoomSetupAutomation.FindJoinedServer(path)); }
        finally { File.Delete(path); }
    }
}
