using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Multiplayer;

namespace PCL.Core.Test.Multiplayer;

[TestClass]
public sealed class EasyTierToolSupportTest
{
    [TestMethod]
    public void VersionParserRecognizesCoreAndRejectsOtherPrograms()
    {
        var info = EasyTierToolSupport.ParseVersion("easytier-core 2.6.4-8428a89d\r\n", true);
        Assert.AreEqual("2.6.4", info.Version); Assert.IsTrue(info.Supported); Assert.IsTrue(info.HasCli);
        Assert.IsFalse(EasyTierToolSupport.ParseVersion("easytier-core 2.5.0", false).Supported);
        Assert.IsTrue(EasyTierToolSupport.ParseVersion("easytier-core 2.7.0", false).Supported);
        Assert.ThrowsExactly<IOException>(() => EasyTierToolSupport.ParseVersion("easytier-cli 2.6.4", true));
        Assert.ThrowsExactly<IOException>(() => EasyTierToolSupport.ParseVersion(new string('x', 4097), true));
    }
    [TestMethod]
    public async Task MissingToolExplainsHowToGetItWithoutExecutingAnything()
    {
        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() => EasyTierToolSupport.CheckAsync("easytier-core.exe", CancellationToken.None));
        StringAssert.Contains(failure.Message, "工具面板");
        Assert.ThrowsExactly<ArgumentException>(() => EasyTierToolSupport.ValidateExecutable(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
    }
    [TestMethod]
    public async Task NativeCoreVersionCheckDoesNotStartAnOverlay()
    {
        var executable = Environment.GetEnvironmentVariable("PCL_MOON_TEST_EASYTIER_CORE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) Assert.Inconclusive("Native EasyTier path not provided.");
        var result = await EasyTierToolSupport.CheckAsync(executable!, CancellationToken.None);
        Assert.IsTrue(result.Supported); Assert.AreEqual("2.6.4", result.Version);
    }
}
