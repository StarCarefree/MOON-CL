using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Server;

namespace PCL.Core.Test.Minecraft.Server;

[TestClass]
public sealed class ServerPluginTargetsTest
{
    private static ServerProfile PluginProfile() => new() { Loader = Enum.Parse<ServerLoader>("Paper"), MinecraftVersion = "1.21.1", LoaderVersion = "123", EntryPoint = "server.jar" };
    [TestMethod]
    public void PluginFolderIsManagedServerNotClientDirectory()
    {
        var root=Path.Combine(Path.GetTempPath(),"pcl-moon-plugin-test-"+Guid.NewGuid().ToString("N"));
        try {
            var manager=new ServerManager(root);var profile=PluginProfile();manager.Save(profile);
            Assert.ThrowsExactly<InvalidOperationException>(() => ServerPluginTargets.PluginsDirectory(manager, profile));
            File.WriteAllText(Path.Combine(manager.GetDirectory(profile), "server.jar"), "test fixture only");
            var target=ServerPluginTargets.FilePath(manager,profile,"example.jar");
            Assert.AreEqual(Path.Combine(manager.GetDirectory(profile),"plugins","example.jar"),target);
            var lease = ServerPluginTargets.ReserveDownload(manager, profile);
            try
            {
                Assert.IsTrue(manager.GetSession(profile).IsInstalling);
                Assert.ThrowsExactly<InvalidOperationException>(() => manager.Save(profile));
                Assert.ThrowsExactly<InvalidOperationException>(() => manager.GetSession(profile).StartAsync());
            }
            finally { lease.Dispose(); lease.Dispose(); }
            Assert.IsFalse(manager.GetSession(profile).IsInstalling);
            Assert.IsTrue(ServerPluginTargets.MatchesVersion(profile,["1.21.1"]));
            Assert.IsFalse(ServerPluginTargets.MatchesVersion(profile,["1.20.1"]));
            Assert.ThrowsExactly<ArgumentException>(()=>ServerPluginTargets.FilePath(manager,profile,"../escape.jar"));
            Assert.ThrowsExactly<ArgumentException>(()=>ServerPluginTargets.FilePath(manager,profile,"file.zip"));
            Assert.ThrowsExactly<ArgumentException>(()=>ServerPluginTargets.FilePath(manager,profile,"a.jar:stream"));
            profile.MinecraftVersion="1.20.1";
            Assert.ThrowsExactly<InvalidOperationException>(()=>ServerPluginTargets.PluginsDirectory(manager,profile));
        } finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    [TestMethod]
    public void RejectFoliaOnlyAndPurpurOnlyOnPaper()
    {
        var profile = PluginProfile();
        Assert.IsFalse(ServerPluginTargets.SupportsEnvironments(profile, ["folia"]));
        Assert.IsFalse(ServerPluginTargets.SupportsEnvironments(profile, ["purpur"]));
        Assert.IsTrue(ServerPluginTargets.SupportsEnvironments(profile, ["paper", "folia"]));
        Assert.IsTrue(ServerPluginTargets.SupportsEnvironments(profile, []));
        profile.Loader = Enum.Parse<ServerLoader>("Purpur");
        Assert.IsTrue(ServerPluginTargets.SupportsEnvironments(profile, ["purpur"]));
    }
    [TestMethod]
    public void VanillaAndModServersCannotBecomePluginTargets()
    {
        foreach(var loader in new[]{ServerLoader.NeoForge,ServerLoader.Fabric,Enum.Parse<ServerLoader>("Vanilla")}) Assert.IsFalse(ServerPluginTargets.SupportsPlugins(new ServerProfile{Loader=loader}));
        Assert.IsTrue(ServerPluginTargets.SupportsPlugins(PluginProfile()));
        var root=Path.Combine(Path.GetTempPath(),"pcl-moon-plugin-test-"+Guid.NewGuid().ToString("N"));
        try {var manager=new ServerManager(root);var profile=PluginProfile();profile.EntryPoint="";manager.Save(profile);Assert.ThrowsExactly<InvalidOperationException>(()=>ServerPluginTargets.PluginsDirectory(manager,profile));}
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
