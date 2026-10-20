using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Server;

namespace PCL.Core.Test.Minecraft.Server;

[TestClass]
public sealed class ServerManagementTest
{
    private string _root = null!;
    private ServerManager _manager = null!;
    private ServerProfile _profile = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(), "server-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _manager = new ServerManager(Path.Combine(_root, "servers"));
        _profile = new ServerProfile { Name = "测试服务端", Loader = ServerLoader.Fabric, MinecraftVersion = "1.21.1", LoaderVersion = "0.16.14" };
        _manager.Save(_profile);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        var session = _manager.GetSession(_profile);
        if (session.IsRunning) session.Kill();
        await WaitFor(() => !session.IsRunning && !session.IsInstalling);
        Directory.Delete(_root, true);
    }

    [TestMethod]
    public void ProfilesAreIsolatedAndSessionsAreShared()
    {
        var other = new ServerProfile { Name = "另一个服务端" }; _manager.Save(other);
         Assert.AreEqual("测试服务端", _profile.Id);
         Assert.AreEqual(Path.Combine(_manager.RootDirectory, "测试服务端"), _manager.GetDirectory(_profile));
         Assert.AreEqual(Path.Combine(_manager.RootDirectory, "另一个服务端"), _manager.GetDirectory(other));
         Assert.AreEqual(2, _manager.List().Count);
         Assert.AreSame(_manager.GetSession(_profile), new ServerManager(_manager.RootDirectory).GetSession(_profile));
         Assert.IsFalse(Directory.Exists(Path.Combine(_root, "versions")));
         Assert.Throws<ArgumentException>(() => _manager.GetDirectory(new ServerProfile { Id = "../escape" }));
        _profile.EntryPoint = "../outside.jar";
        Assert.Throws<ArgumentException>(() => _manager.Save(_profile));
        var guid = Guid.NewGuid().ToString("N");
        var renamed = new ServerProfile { Id = guid, Name = "测试服务端" };
        Directory.CreateDirectory(Path.Combine(_manager.RootDirectory, guid));
        File.WriteAllText(Path.Combine(_manager.RootDirectory, guid, ".pcl-moon-server.json"), "{\"Id\":\"" + guid + "\",\"EntryPoint\":\"\"}");
        _manager.Save(renamed);
        Assert.AreEqual("测试服务端-2", renamed.Id);
        Assert.IsFalse(Directory.Exists(Path.Combine(_manager.RootDirectory, guid)));
        Assert.IsTrue(Directory.Exists(Path.Combine(_manager.RootDirectory, "测试服务端-2")));
    }

    [TestMethod]
    public void PropertiesKeepCommentsUnknownKeysAndJavaEscapes()
    {
        const string original = "# comment\r\nmotd=\\u4f60\\u597d\r\ncustom\\:key : first\\\r\n  second\r\nserver-port=25565\r\nunknown=value\r\n";
        var parsed = ServerProperties.Parse(original);
        Assert.AreEqual("你好", parsed["motd"]); Assert.AreEqual("firstsecond", parsed["custom:key"]);
        var result = ServerProperties.Merge(original, new Dictionary<string, string> { ["server-port"] = "25566", ["motd"] = "Hello\nserver-port=1", ["space key"] = " value\\tail" });
        var values = ServerProperties.Parse(result);
        Assert.AreEqual("25566", values["server-port"]); Assert.AreEqual("value", values["unknown"]);
        Assert.AreEqual("Hello\nserver-port=1", values["motd"]); Assert.AreEqual(" value\\tail", values["space key"]);
        StringAssert.Contains(result, "# comment\r\n"); StringAssert.Contains(result, "unknown=value\r\n");
        Assert.Throws<ArgumentException>(() => ServerProperties.Merge("", new Dictionary<string, string> { ["server-port"] = "70000" }));
        Assert.Throws<ArgumentException>(() => ServerProperties.Merge("", new Dictionary<string, string> { ["bad\nkey"] = "x" }));
    }

    [TestMethod]
    public void CommandsUseSelectedLoaderJavaMemoryAndSafeArguments()
    {
        var directory = _manager.GetDirectory(_profile);
         var temp = Path.Combine(directory, "tmp");
         var fabric = ServerCommands.Installer(_profile, directory, "installer with spaces.jar", temp);
         CollectionAssert.AreEqual(new[] { "-Djava.io.tmpdir=" + Path.GetFullPath(temp), "-jar", "installer with spaces.jar", "server", "-mcversion", "1.21.1", "-loader", "0.16.14", "-downloadMinecraft", "-dir", directory }, fabric.ArgumentList.ToArray());
         _profile.Loader = ServerLoader.NeoForge; _profile.LoaderVersion = "21.1.172";
         CollectionAssert.AreEqual(new[] { "-Djava.io.tmpdir=" + Path.GetFullPath(temp), "-jar", "installer.jar", "--installServer" }, ServerCommands.Installer(_profile, directory, "installer.jar", temp).ArgumentList.ToArray());
        var entry = "libraries/net/neoforged/neoforge/21.1.172/win_args.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, entry))!); File.WriteAllText(Path.Combine(directory, entry), "test");
        _profile.EntryPoint = entry; _profile.JvmArguments = "-Dtitle=\"a b\" -XX:+UseG1GC";
         var start = ServerCommands.Start(_profile, directory, temp);
         Assert.AreEqual("java", start.FileName); Assert.AreEqual(directory, start.WorkingDirectory);
         CollectionAssert.AreEqual(new[] { "-Djava.io.tmpdir=" + Path.GetFullPath(temp), "-Xms1024M", "-Xmx4096M", "-Dtitle=a b", "-XX:+UseG1GC", "@" + entry, "nogui" }, start.ArgumentList.ToArray());
        Assert.Throws<ArgumentException>(() => ServerCommands.ParseJvmArguments("-Xmx8G"));
        Assert.Throws<ArgumentException>(() => ServerCommands.ParseJvmArguments("-jar other.jar"));
        _profile.JavaPath = "javaw.exe"; Assert.Throws<ArgumentException>(() => _manager.Save(_profile));
    }

    [TestMethod]
    public void DownloadOrderingMatchesLauncherSettings()
    {
        _profile.Loader = ServerLoader.NeoForge; _profile.LoaderVersion = "21.1.172";
        StringAssert.Contains(ServerManager.InstallerUrls(_profile, 0)[0], "bmclapi2");
        StringAssert.Contains(ServerManager.InstallerUrls(_profile, 1)[0], "maven.neoforged.net");
        Assert.AreEqual(1, ServerManager.InstallerUrls(_profile, 2).Length);
        _profile.Loader = ServerLoader.Fabric;
        StringAssert.Contains(ServerManager.InstallerUrls(_profile, 0, "1.0.3")[0], "maven.fabricmc.net");
    }

    [TestMethod]
    public async Task ServerZipFlattensOneWrapperAndNeverAcceptsEula()
    {
        var zip = Zip(("README.md", "outer notes"), ("server-pack/fabric-server-launch.jar", "fake jar"), ("server-pack/mods/test.txt", "mod"), ("server-pack/eula.txt", "eula=true"));
        await _manager.InstallAsync(_profile, zip, null, null, false);
        Assert.AreEqual("fabric-server-launch.jar", _profile.EntryPoint);
        Assert.IsTrue(File.Exists(Path.Combine(_manager.GetDirectory(_profile), "mods/test.txt")));
        Assert.IsFalse(_manager.IsEulaAccepted(_profile));
        Assert.AreEqual(_profile.EntryPoint, _manager.List().Single().EntryPoint);
        Assert.Throws<InvalidOperationException>(() => _manager.Save(new ServerProfile { Id = _profile.Id, EntryPoint = "" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.InstallAsync(_profile, zip, null, null, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.GetSession(_profile).StartAsync());
    }

    [TestMethod]
    public async Task RejectsTraversalClientPackAndMetadataOverwrite()
    {
        await Assert.ThrowsAsync<IOException>(() => _manager.InstallAsync(_profile, Zip(("../escape.txt", "x")), null, null, false));
        Assert.IsFalse(File.Exists(Path.Combine(_manager.RootDirectory, "escape.txt")));
        await Assert.ThrowsAsync<IOException>(() => _manager.InstallAsync(_profile, Zip(("pack/manifest.json", "{}")), null, null, false));
        await Assert.ThrowsAsync<IOException>(() => _manager.InstallAsync(_profile, Zip((".pcl-moon-server.json", "{}")), null, null, false));
        Assert.AreEqual("", _manager.List().Single().EntryPoint);
        Assert.IsFalse(_manager.GetSession(_profile).IsInstalling);
    }

    [TestMethod]
    public async Task UntrustedScriptsAreNeverExecutedAndTrustedInstallLogsOutput()
    {
        var script = Path.Combine(_root, "install.cmd"); File.WriteAllText(script, "@echo off\r\necho installed\r\necho fake>fabric-server-launch.jar\r\nexit /b 0\r\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.InstallAsync(_profile, null, null, script, false));
        Assert.IsFalse(File.Exists(Path.Combine(_manager.GetDirectory(_profile), "fabric-server-launch.jar")));
        await _manager.InstallAsync(_profile, null, null, script, true);
        Assert.AreEqual("fabric-server-launch.jar", _profile.EntryPoint);
        Assert.IsTrue(_manager.GetSession(_profile).GetLog().Any(line => line.Contains("installed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FailedInstallerDoesNotPublishEntryAndCanRetry()
    {
        var script = Path.Combine(_root, "install.cmd"); File.WriteAllText(script, "@echo off\r\necho fake>fabric-server-launch.jar\r\nexit /b 7\r\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.InstallAsync(_profile, null, null, script, true));
        Assert.AreEqual("", _manager.List().Single().EntryPoint);
        File.WriteAllText(script, "@echo off\r\necho fake>fabric-server-launch.jar\r\nexit /b 0\r\n");
        await _manager.InstallAsync(_profile, null, null, script, true);
        Assert.AreEqual("fabric-server-launch.jar", _profile.EntryPoint);
    }

    [TestMethod]
    public async Task CancellationReleasesInstallGateAndPreventsConcurrentWrites()
    {
        var script = Path.Combine(_root, "install.ps1"); File.WriteAllText(script, "Write-Output 'waiting'; Start-Sleep -Seconds 60");
        using var cancellation = new CancellationTokenSource();
        var install = _manager.InstallAsync(_profile, null, null, script, true, cancellation.Token);
        var session = _manager.GetSession(_profile);
        Assert.IsTrue(session.IsInstalling);
        Assert.Throws<InvalidOperationException>(() => _manager.Save(_profile));
        Assert.Throws<InvalidOperationException>(() => _manager.WriteProperties(_profile, new Dictionary<string, string> { ["motd"] = "blocked" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        await WaitFor(() => session.GetLog().Any(line => line.EndsWith("waiting", StringComparison.Ordinal)));
         cancellation.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => install);
         Assert.IsFalse(session.IsInstalling);
         Assert.IsFalse(Directory.Exists(_manager.GetDirectory(_profile)));
         Assert.IsFalse(_manager.List().Any(item => item.Id == _profile.Id));
    }

    [TestMethod]
    public async Task SimulatedServerSupportsCommandsStopRestartAndLatestSettings()
    {
        var directory = _manager.GetDirectory(_profile);
        _profile.EntryPoint = "server.ps1";
        File.WriteAllText(Path.Combine(directory, _profile.EntryPoint), "Write-Output 'ready'; while (($line = [Console]::ReadLine()) -ne $null) { Write-Output ('received:' + $line); if ($line -eq 'stop') { exit 0 } }");
        _manager.Save(_profile);
        // This is a dummy PowerShell process, not Minecraft; no real Minecraft EULA is accepted by the test.
        _manager.AcceptEula(_profile);
        var session = _manager.GetSession(_profile);
        try
        {
            await session.StartAsync(); await WaitFor(() => session.GetLog().Any(line => line.EndsWith("ready", StringComparison.Ordinal)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
            session.SendCommand("say hello");
            await WaitFor(() => session.GetLog().Any(line => line.Contains("received:say hello", StringComparison.Ordinal)));
            Assert.Throws<ArgumentException>(() => session.SendCommand("one\ntwo"));
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); await WaitFor(() => !session.IsRunning);
            _profile.EntryPoint = "second.ps1"; File.WriteAllText(Path.Combine(directory, _profile.EntryPoint), "Write-Output 'second'; Start-Sleep -Seconds 60"); _manager.Save(_profile);
            await session.StartAsync(); await WaitFor(() => session.GetLog().Any(line => line.EndsWith("second", StringComparison.Ordinal)));
            session.Kill(); await WaitFor(() => !session.IsRunning);
        }
        finally { if (session.IsRunning) session.Kill(); }
    }

    [TestMethod]
    public async Task InstallationUsesSnapshotNotMutableWindowProfile()
    {
        var script = Path.Combine(_root, "install.ps1");
        File.WriteAllText(script, "Start-Sleep -Milliseconds 150; Set-Content fabric-server-launch.jar 'fake'");
        var install = _manager.InstallAsync(_profile, null, null, script, true);
        _profile.Name = "changed by another window";
        await install;
        Assert.AreEqual("测试服务端", _manager.List().Single().Name);
        Assert.AreEqual("fabric-server-launch.jar", _profile.EntryPoint);
    }

    [TestMethod]
    public void LoaderDetectionDoesNotMistakeVanillaJarForInstalledLoader()
    {
        var directory = _manager.GetDirectory(_profile);
        File.WriteAllText(Path.Combine(directory, "server.jar"), "vanilla");
        Assert.IsNull(ServerManager.DetectLoaderEntry(_profile, directory));
        File.WriteAllText(Path.Combine(directory, "fabric-server-launch.jar"), "fabric");
        Assert.AreEqual("fabric-server-launch.jar", ServerManager.DetectLoaderEntry(_profile, directory));
        _profile.Loader = ServerLoader.NeoForge; _profile.LoaderVersion = "21.1.172";
        Assert.IsNull(ServerManager.DetectLoaderEntry(_profile, directory));
        var entry = Path.Combine("libraries", "net", "neoforged", "neoforge", "21.1.172", "win_args.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, entry))!);
        File.WriteAllText(Path.Combine(directory, entry), "neoforge");
        Assert.AreEqual(entry, ServerManager.DetectLoaderEntry(_profile, directory));
    }

    [TestMethod]
    public async Task ModOnlyArchiveKeepsModsDirectory()
    {
        var script = Path.Combine(_root, "install.cmd");
        File.WriteAllText(script, "@echo off\r\necho fake>fabric-server-launch.jar\r\nexit /b 0\r\n");
        await _manager.InstallAsync(_profile, Zip(("mods/example.jar", "fake mod")), null, script, true);
        var directory = _manager.GetDirectory(_profile);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "mods", "example.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "example.jar")));
    }

    private string Zip(params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries) { using var writer = new StreamWriter(zip.CreateEntry(entry.Path).Open()); writer.Write(entry.Content); }
        return path;
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate()) { if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for server process state"); await Task.Delay(30); }
    }
}
