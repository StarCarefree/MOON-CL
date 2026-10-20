using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class GameLinkMappingsTest
{
    [TestMethod]
    public async Task VerifiedVersionCacheCanBeUsedWithoutNetwork()
    {
        var cache = Path.Combine(Path.GetTempPath(), "pcl-moon-mappings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            const string version = "test-version-without-network";
            var bytes = Encoding.UTF8.GetBytes("net.minecraft.client.Minecraft -> abc:\n");
            var hash = Convert.ToHexString(SHA1.HashData(bytes));
            var path = Path.Combine(cache, hash + ".txt");
            File.WriteAllBytes(path, bytes);
            File.WriteAllText(Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version))) + ".ref"), hash);
            Assert.AreEqual(path, await GameLinkMappings.PrepareAsync(version, cache));
        }
        finally { Directory.Delete(cache, true); }
    }
    [TestMethod]
    public async Task OfficialAndLoaderMappingDownloadsAreVerifiedWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("PCL_MOON_TEST_LINK_MAPPINGS") != "1")
            Assert.Inconclusive("Set PCL_MOON_TEST_LINK_MAPPINGS=1 to enable official mapping downloads");
        var cache = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(), "pcl-moon-mapping-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var version in new[] { "1.16.5", "1.21.1" })
            {
                var path = await GameLinkMappings.PrepareAsync(version, cache);
                Assert.IsNotNull(path); Assert.IsTrue(new FileInfo(path).Length > 1000);
                Assert.AreEqual(path, await GameLinkMappings.PrepareAsync(version, cache), "Cache must return the same verified mapping");
            }
            foreach (var kind in new[] { "fabric", "forge" })
            {
                var path = await GameLinkMappings.PrepareRuntimeAsync("1.20.1", kind, cache);
                Assert.IsTrue(new FileInfo(path).Length > 1000);
                Assert.AreEqual(path, await GameLinkMappings.PrepareRuntimeAsync("1.20.1", kind, cache));
            }
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }


    [TestMethod]
    public async Task CacheReadHonorsCancellation()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var cache = Path.Combine(Path.GetTempPath(), "pcl-moon-mappings-" + Guid.NewGuid().ToString("N"));
        try { await Assert.ThrowsAsync<OperationCanceledException>(() => GameLinkMappings.PrepareAsync("1.16.5", cache, stop.Token)); }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
}
