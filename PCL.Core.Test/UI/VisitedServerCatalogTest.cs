using System;
using System.IO;
using System.Linq;
using fNbt;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class VisitedServerCatalogTest
{
    [TestMethod]
    public void ReadsServersDatAndDedupesDefaultPort()
    {
        var root = Path.Combine(Path.GetTempPath(), "mooncl-visited-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "a");
        var second = Path.Combine(root, "b");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            Write(Path.Combine(first, "servers.dat"), ("Hypixel", "mc.hypixel.net"), ("局域网", "127.0.0.1:25565"));
            Write(Path.Combine(second, "servers.dat"), ("重复", "mc.hypixel.net:25565"), ("坏地址", "not a host"));
            var servers = VisitedServerCatalog.Load(
            [
                new("生存", first, "1.21.1"),
                new("创造", second, "1.20.1")
            ]);
            Assert.AreEqual(2, servers.Count);
            Assert.AreEqual("Hypixel", servers[0].Name);
            Assert.AreEqual("生存", servers[0].InstanceName);
            Assert.AreEqual("1.21.1", servers[0].Minecraft);
            Assert.AreEqual("局域网", servers[1].Name);
            Assert.AreEqual("127.0.0.1:25565", servers[1].Address);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Write(string path, params (string Name, string Address)[] servers)
    {
        var list = new NbtList("servers", NbtTagType.Compound);
        foreach (var server in servers)
            list.Add(new NbtCompound { new NbtString("name", server.Name), new NbtString("ip", server.Address) });
        new NbtFile(new NbtCompound("") { list }).SaveToFile(path, NbtCompression.GZip);
    }
}
