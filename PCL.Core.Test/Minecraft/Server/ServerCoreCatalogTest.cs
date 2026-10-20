using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Server;

namespace PCL.Core.Test.Minecraft.Server;

[TestClass]
public sealed class ServerCoreCatalogTest
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(), "server-core-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void PersistedLoaderNumbersStayCompatibleAndPluginsArePaperPurpurOnly()
    {
        Assert.AreEqual(0, (int)ServerLoader.NeoForge);
        Assert.AreEqual(1, (int)ServerLoader.Fabric);
        Assert.AreEqual(2, (int)ServerLoader.Vanilla);
        Assert.AreEqual(3, (int)ServerLoader.Paper);
         Assert.AreEqual(4, (int)ServerLoader.Purpur);
         Assert.AreEqual(5, (int)ServerLoader.Spigot);
         Assert.AreEqual(6, (int)ServerLoader.Folia);
         Assert.IsFalse(ServerCommands.SupportsPlugins(ServerLoader.Vanilla));
         Assert.IsFalse(ServerCommands.SupportsPlugins(ServerLoader.Fabric));
         Assert.IsFalse(ServerCommands.SupportsPlugins(ServerLoader.NeoForge));
         Assert.IsTrue(ServerCommands.SupportsPlugins(ServerLoader.Paper));
         Assert.IsTrue(ServerCommands.SupportsPlugins(ServerLoader.Spigot));
         Assert.IsTrue(new ServerProfile { Loader = ServerLoader.Folia }.SupportsPlugins);
        var old = JsonSerializer.Deserialize<ServerProfile>("""{"Id":"0123456789abcdef0123456789abcdef","Name":"旧","Loader":1,"MinecraftVersion":"1.21.1","LoaderVersion":"0.16.14","JavaPath":"java"}""")!;
        Assert.AreEqual(ServerLoader.Fabric, old.Loader);
        Assert.IsNull(old.RequiredJavaMajor);
        ServerCommands.Validate(new ServerProfile { Loader = ServerLoader.Vanilla, MinecraftVersion = "1.21.1", LoaderVersion = "" });
        var manager = new ServerManager(Path.Combine(_root, "servers"));
        var profile = new ServerProfile { Name = "兼容", Loader = ServerLoader.Fabric, MinecraftVersion = "1.21.1", LoaderVersion = "0.16.14" };
         manager.Save(profile);
         StringAssert.Contains(File.ReadAllText(Path.Combine(manager.GetDirectory(profile), ".pcl-moon-server.json")), "\"Loader\": 1");
         var plugin = new ServerProfile { Loader = ServerLoader.Purpur, MinecraftVersion = "1.21.1" };
         Assert.IsTrue(ServerPluginTargets.SupportsEnvironments(plugin, ["purpur"]));
         plugin.Loader = ServerLoader.Folia;
         Assert.IsTrue(ServerPluginTargets.SupportsEnvironments(plugin, ["folia"]));
         Assert.IsFalse(ServerPluginTargets.SupportsEnvironments(plugin, ["purpur"]));
        profile.Loader = ServerLoader.Paper;
        manager.Save(profile);
        StringAssert.Contains(File.ReadAllText(Path.Combine(manager.GetDirectory(profile), ".pcl-moon-server.json")), "\"Loader\": 3");
        Assert.AreEqual(ServerLoader.Paper, manager.List().Single().Loader);
    }

    [TestMethod]
    public void DirectCoresDoNotUseFabricInstallerArguments()
    {
        var profile = new ServerProfile { Loader = ServerLoader.Vanilla, MinecraftVersion = "1.21.1" };
        Assert.Throws<ArgumentException>(() => ServerCommands.Installer(profile, _root, "installer.jar"));
        Assert.Throws<ArgumentException>(() => ServerManager.InstallerUrls(profile, 0));
        var directory = Path.Combine(_root, "run");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "server.jar"), "not-used");
        profile.EntryPoint = "server.jar";
         CollectionAssert.AreEqual(new[] { "-Xms1024M", "-Xmx4096M", "-jar", "server.jar", "nogui" }, ServerCommands.Start(profile, directory).ArgumentList.ToArray());
         var temp = Path.Combine(directory, "tmp");
         CollectionAssert.AreEqual(new[] { "-Djava.io.tmpdir=" + Path.GetFullPath(temp), "-Xms1024M", "-Xmx4096M", "-jar", "server.jar", "nogui" }, ServerCommands.Start(profile, directory, temp).ArgumentList.ToArray());
        Assert.AreEqual("server.jar", ServerManager.DetectLoaderEntry(profile, directory));
        profile.Loader = ServerLoader.Fabric;
        Assert.IsNull(ServerManager.DetectLoaderEntry(profile, directory));
    }

    [TestMethod]
    public void JavaMajorParserAndMissingRuntimeAreActionable()
    {
        Assert.AreEqual(8, ServerCommands.ParseJavaMajor("java version \"1.8.0_333\""));
        Assert.AreEqual(21, ServerCommands.ParseJavaMajor("openjdk version \"21.0.2\" 2024-01-16"));
        var profile = new ServerProfile { JavaPath = Path.Combine(_root, "missing-java.exe"), RequiredJavaMajor = 21, Loader = ServerLoader.Vanilla, MinecraftVersion = "1.21.1" };
        var error = Assert.Throws<InvalidOperationException>(() => ServerCommands.EnsureJava(profile));
        StringAssert.Contains(error.Message, "Java 21");
    }

    [TestMethod]
    public void VanillaMirrorOrderFollowsSourcePolicy()
    {
        Assert.IsTrue(ServerCoreCatalog.RewriteVanillaHost("https://piston-data.mojang.com/v1/objects/abc/server.jar")!.Contains("bmclapi2.bangbang93.com", StringComparison.Ordinal));
        var seen = new List<string>();
        using var http = Client((request, _) => { seen.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(Json(request.RequestUri.AbsolutePath.Contains("version_manifest", StringComparison.Ordinal) ? Manifest() : VersionJson(TinyJar()))); });
        var mirrorFirst = ServerCoreCatalog.ResolveAsync(http, ServerLoader.Vanilla, "1.21.1", "", 0, CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsTrue(seen[0].Contains("bmclapi2.bangbang93.com", StringComparison.Ordinal));
        Assert.IsTrue(mirrorFirst.Urls[0].Contains("bmclapi2.bangbang93.com", StringComparison.Ordinal));
        Assert.AreEqual(21, mirrorFirst.JavaMajor);
        seen.Clear();
        var officialOnly = ServerCoreCatalog.ResolveAsync(http, ServerLoader.Vanilla, "1.21.1", "", 2, CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsTrue(seen.All(url => url.Contains("mojang.com", StringComparison.Ordinal)));
        Assert.IsTrue(officialOnly.Urls.All(url => url.Contains("piston-data.mojang.com", StringComparison.Ordinal)));
        Assert.AreEqual("", ServerCoreCatalog.MirrorNotice(ServerLoader.Vanilla, 0));
    }

    [TestMethod]
    public async Task PaperSelectsStableUnlessBuildIsExplicitAndMissingMirrorKeepsOfficial()
    {
        var seen = new List<string>();
        using var http = Client((request, _) =>
        {
            seen.Add(request.RequestUri!.AbsoluteUri);
            Assert.IsTrue(request.Headers.UserAgent.ToString().Contains("PCL-MOON", StringComparison.Ordinal));
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/builds/10", StringComparison.Ordinal)) return Task.FromResult(Json(PaperBuild(10, "ALPHA")));
            if (path.EndsWith("/builds", StringComparison.Ordinal)) return Task.FromResult(Json("[" + PaperBuild(10, "ALPHA") + "," + PaperBuild(9, "STABLE") + "]"));
            if (path.EndsWith("/1.21.1", StringComparison.Ordinal)) return Task.FromResult(Json("{\"version\":{\"java\":{\"version\":{\"minimum\":21}}}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var stable = await ServerCoreCatalog.ResolveAsync(http, ServerLoader.Paper, "1.21.1", "", 0, CancellationToken.None);
        Assert.AreEqual("9", stable.Build);
        Assert.AreEqual("sha256", stable.ChecksumAlgorithm);
        Assert.AreEqual(21, stable.JavaMajor);
        Assert.AreEqual("fill.papermc.io/v3", stable.MetadataSource);
        StringAssert.Contains(stable.MirrorNotice, "使用官方源");
        Assert.IsTrue(seen.Any(url => url.Contains("download.fastmirror.net", StringComparison.OrdinalIgnoreCase)));
        var explicitBuild = await ServerCoreCatalog.ResolveAsync(http, ServerLoader.Paper, "1.21.1", "10", 2, CancellationToken.None);
        Assert.AreEqual("10", explicitBuild.Build);
        Assert.IsTrue(explicitBuild.Urls[0].Contains("fill-data.papermc.io", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PaperDoesNotCallSunsetV2WhenV3IsUnavailable()
    {
        var seen = new List<string>();
        using var http = Client((request, _) =>
        {
            seen.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => ServerCoreCatalog.ResolveAsync(http, ServerLoader.Paper, "1.21.1", "", 1, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "fill.papermc.io" }, seen);
    }

    [TestMethod]
    public async Task PurpurUsesLatestOrExplicitBuildMd5()
    {
        using var http = Client((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/latest", StringComparison.Ordinal)) return Task.FromResult(Json("{\"build\":\"2329\",\"md5\":\"" + new string('c', 32) + "\"}"));
            if (path.EndsWith("/5", StringComparison.Ordinal)) return Task.FromResult(Json("{\"build\":\"5\",\"md5\":\"" + new string('d', 32) + "\"}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var latest = await ServerCoreCatalog.ResolveAsync(http, ServerLoader.Purpur, "1.21.1", "latest", 0, CancellationToken.None);
        Assert.AreEqual("2329", latest.Build);
        Assert.AreEqual("md5", latest.ChecksumAlgorithm);
        Assert.IsTrue(latest.Urls[0].EndsWith("/2329/download", StringComparison.Ordinal));
        var pinned = await ServerCoreCatalog.ResolveAsync(http, ServerLoader.Purpur, "1.21.1", "5", 2, CancellationToken.None);
        Assert.AreEqual("5", pinned.Build);
        Assert.IsFalse(pinned.Urls[0].Contains("fastmirror", StringComparison.OrdinalIgnoreCase));
    }
 
     [TestMethod]
     public async Task LoaderChoicesFollowSelectedMinecraftVersion()
     {
         using var http = Client((request, _) =>
         {
             var path = request.RequestUri!.AbsolutePath;
             if (path.EndsWith("/neoforge", StringComparison.Ordinal)) return Task.FromResult(Json("{\"versions\":[\"21.1.1\",\"21.1.2\",\"20.1-47.1.0\",\"1.20.1-47.1.106\"]}"));
             if (path.Contains("/loader/1.21.1", StringComparison.Ordinal)) return Task.FromResult(Json("[{\"loader\":{\"version\":\"0.16.14\",\"stable\":true}},{\"loader\":{\"version\":\"0.16.10\",\"stable\":true}}]"));
             if (path.Contains("/paper/versions/1.21.1/builds", StringComparison.Ordinal)) return Task.FromResult(Json("[" + PaperBuild(10, "ALPHA") + "," + PaperBuild(9, "STABLE") + "]"));
             if (path.Contains("/folia/versions/1.21.8/builds", StringComparison.Ordinal)) return Task.FromResult(Json("[" + PaperBuild(4, "STABLE") + "]"));
             if (path.EndsWith("/purpur/1.21.1", StringComparison.Ordinal)) return Task.FromResult(Json("{\"builds\":{\"latest\":\"3\",\"all\":[\"1\",\"2\",\"3\"]}}"));
             if (path.EndsWith("/api/jars", StringComparison.Ordinal)) return Task.FromResult(Json("{\"spigot\":[{\"type\":\"spigot\",\"version\":\"1.21.1\",\"downloadUrl\":\"/spigot/spigot-1.21.1.jar\",\"sha256\":\"" + new string('a', 64) + "\",\"size\":12},{\"type\":\"craftbukkit\",\"version\":\"1.21.1\",\"downloadUrl\":\"/craftbukkit/x.jar\",\"sha256\":\"" + new string('b', 64) + "\",\"size\":12}]}"));
             return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
         });
         CollectionAssert.AreEqual(new[] { "21.1.2", "21.1.1" }, (await ServerCoreCatalog.ListLoaderVersionsAsync(http, ServerLoader.NeoForge, "1.21.1", 1, CancellationToken.None)).ToArray());
         CollectionAssert.AreEqual(new[] { "1.20.1-47.1.106" }, (await ServerCoreCatalog.ListLoaderVersionsAsync(http, ServerLoader.NeoForge, "1.20.1", 1, CancellationToken.None)).ToArray());
         CollectionAssert.AreEqual(new[] { "0.16.14", "0.16.10" }, (await ServerCoreCatalog.ListLoaderVersionsAsync(http, ServerLoader.Fabric, "1.21.1", 2, CancellationToken.None)).ToArray());
         CollectionAssert.AreEqual(new[] { "10", "9" }, (await ServerCoreCatalog.ListLoaderVersionsAsync(http, ServerLoader.Paper, "1.21.1", 2, CancellationToken.None)).ToArray());
         CollectionAssert.AreEqual(new[] { "4" }, (await ServerCoreCatalog.ListLoaderVersionsAsync(http, ServerLoader.Folia, "1.21.8", 2, CancellationToken.None)).ToArray());
         CollectionAssert.AreEqual(new[] { "3", "2", "1" }, (await ServerCoreCatalog.ListLoaderVersionsAsync(http, ServerLoader.Purpur, "1.21.1", 2, CancellationToken.None)).ToArray());
         var spigot = await ServerCoreCatalog.ResolveAsync(http, ServerLoader.Spigot, "1.21.1", "1.21.1", 2, CancellationToken.None);
         Assert.AreEqual("https://download.getbukkit.org/spigot/spigot-1.21.1.jar", spigot.Urls.Single());
         Assert.AreEqual("sha256", spigot.ChecksumAlgorithm);
     }

    [TestMethod]
    public async Task VanillaInstallVerifiesChecksumWritesEulaAndDoesNotExecute()
    {
        var jar = TinyJar();
        var urls = new List<string>();
        using var http = Client((request, token) =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.Host == "piston-data.mojang.com") return Task.FromResult(Bytes(jar));
            return Task.FromResult(Json(request.RequestUri.AbsolutePath.Contains("version_manifest", StringComparison.Ordinal) ? Manifest() : VersionJson(jar)));
        });
        var manager = new ServerManager(Path.Combine(_root, "servers"), http);
        var profile = new ServerProfile { Name = "原版", Loader = ServerLoader.Vanilla, MinecraftVersion = "1.21.1", LoaderVersion = "" };
        manager.Save(profile);
        await manager.InstallAsync(profile, null, null, null, false);
        Assert.AreEqual("server.jar", profile.EntryPoint);
        Assert.AreEqual(21, profile.RequiredJavaMajor);
        Assert.IsFalse(manager.IsEulaAccepted(profile));
        Assert.AreEqual("eula=false\n", File.ReadAllText(Path.Combine(manager.GetDirectory(profile), "eula.txt")));
        CollectionAssert.AreEqual(jar, File.ReadAllBytes(Path.Combine(manager.GetDirectory(profile), "server.jar")));
        Assert.IsFalse(urls.Any(url => url.Contains("fabric", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(manager.GetSession(profile).GetLog().Any(line => line.Contains("已启动", StringComparison.Ordinal)));
        Assert.IsTrue(manager.GetSession(profile).GetLog().Any(line => line.Contains("不执行", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BadChecksumCancelAndUntrustedLocalJarDoNotPublishEntry()
    {
        var jar = TinyJar();
        using var badHttp = Client((request, _) => Task.FromResult(request.RequestUri!.Host == "piston-data.mojang.com"
            ? Bytes(jar)
            : Json(request.RequestUri.AbsolutePath.Contains("version_manifest", StringComparison.Ordinal) ? Manifest() : VersionJson(jar, new string('a', 40)))));
        var bad = new ServerManager(Path.Combine(_root, "bad"), badHttp);
        var profile = new ServerProfile { Name = "校验", Loader = ServerLoader.Vanilla, MinecraftVersion = "1.21.1" };
        bad.Save(profile);
        await Assert.ThrowsAsync<IOException>(() => bad.InstallAsync(profile, null, null, null, false));
        Assert.AreEqual("", bad.List().Single().EntryPoint);
        Assert.IsFalse(File.Exists(Path.Combine(bad.GetDirectory(profile), "server.jar")));
        Assert.IsFalse(Directory.EnumerateFiles(bad.GetDirectory(profile), "*.tmp").Any());
        Assert.IsFalse(bad.GetSession(profile).IsInstalling);

        using var hang = new CancellationTokenSource();
        using var slowHttp = Client(async (request, token) =>
        {
            if (request.RequestUri!.Host == "piston-data.mojang.com") await Task.Delay(Timeout.Infinite, token);
            return Json(request.RequestUri.AbsolutePath.Contains("version_manifest", StringComparison.Ordinal) ? Manifest() : VersionJson(jar));
        });
        var slow = new ServerManager(Path.Combine(_root, "slow"), slowHttp);
        var waiting = new ServerProfile { Name = "取消", Loader = ServerLoader.Vanilla, MinecraftVersion = "1.21.1" };
        slow.Save(waiting);
        var install = slow.InstallAsync(waiting, null, null, null, false, hang.Token);
        await WaitFor(() => slow.GetSession(waiting).IsInstalling);
        hang.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => install);
        Assert.IsFalse(slow.GetSession(waiting).IsInstalling);
        Assert.IsFalse(Directory.Exists(Path.Combine(slow.RootDirectory, "取消")));

        var local = new ServerManager(Path.Combine(_root, "local"), Client((_, _) => throw new InvalidOperationException("本地导入不应访问网络。")));
        var imported = new ServerProfile { Name = "本地", Loader = ServerLoader.Paper, MinecraftVersion = "1.21.1" };
        local.Save(imported);
        var source = Path.Combine(_root, "local.jar");
        File.WriteAllBytes(source, jar);
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.InstallAsync(imported, null, source, null, false));
        Assert.IsFalse(File.Exists(Path.Combine(local.GetDirectory(imported), "server.jar")));
        await local.InstallAsync(imported, null, source, null, true);
        Assert.AreEqual("server.jar", imported.EntryPoint);
        Assert.IsFalse(local.IsEulaAccepted(imported));
        Assert.IsFalse(local.GetSession(imported).GetLog().Any(line => line.Contains("已启动", StringComparison.Ordinal)));
        Assert.IsFalse(File.Exists(Path.Combine(local.GetDirectory(imported), "pcl-moon-loader-installer.jar")));
    }

    [TestMethod]
    public async Task MetadataIsBoundedAndOversizedJarIsRejectedBeforeDownload()
    {
        using var huge = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', ServerCoreTransfer.MaxJsonBytes + 1)) }));
        await Assert.ThrowsAsync<IOException>(() => ServerCoreCatalog.ListVersionsAsync(huge, ServerLoader.Vanilla, 2, CancellationToken.None));
        var oversized = "{\"downloads\":{\"server\":{\"url\":\"https://piston-data.mojang.com/v1/objects/abc/server.jar\",\"sha1\":\"" + new string('a', 40) + "\",\"size\":" + (ServerCoreTransfer.MaxJarBytes + 1) + "}},\"javaVersion\":{\"majorVersion\":21}}";
        using var http = Client((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.Contains("version_manifest", StringComparison.Ordinal) ? Manifest() : oversized)));
        await Assert.ThrowsAsync<IOException>(() => ServerCoreCatalog.ResolveAsync(http, ServerLoader.Vanilla, "1.21.1", "", 2, CancellationToken.None));
    }

    private static string Manifest() => """{"versions":[{"id":"1.21.1","type":"release","url":"https://piston-meta.mojang.com/v1/packages/abc/1.21.1.json"}]}""";

    private static string VersionJson(byte[] jar, string? sha1 = null)
    {
        sha1 ??= Convert.ToHexString(SHA1.HashData(jar)).ToLowerInvariant();
        return "{\"downloads\":{\"server\":{\"url\":\"https://piston-data.mojang.com/v1/objects/abc/server.jar\",\"sha1\":\"" + sha1 + "\",\"size\":" + jar.Length + "}},\"javaVersion\":{\"majorVersion\":21}}";
    }

    private static string PaperBuild(int id, string channel) => "{\"id\":" + id + ",\"channel\":\"" + channel + "\",\"downloads\":{\"server:default\":{\"name\":\"paper.jar\",\"url\":\"https://fill-data.papermc.io/v1/objects/" + new string('e', 64) + "/paper.jar\",\"checksums\":{\"sha256\":\"" + new string('e', 64) + "\"},\"size\":12}}}";

    private static byte[] TinyJar()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open())) writer.Write("Manifest-Version: 1.0\n");
        return stream.ToArray();
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(8) };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate()) { if (DateTime.UtcNow > deadline) Assert.Fail("Timed out"); await Task.Delay(20); }
    }
}
