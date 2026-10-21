using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;

namespace PCL.Core.Test.App;

[TestClass]
public sealed class BasicsHostExecutableTest
{
    private string _root = null!;

    private static string TestRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "PCL.Core")))
                return Path.Combine(directory.FullName, "artifacts", "test", "host-exe");
            directory = directory.Parent;
        }
        Assert.Fail("无法定位仓库根目录");
        return string.Empty;
    }

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(TestRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string CreateHost()
    {
        var host = Path.Combine(_root, "PCL-MOON.exe");
        File.WriteAllText(host, "stub");
        return host;
    }

    private string CreatePayload(out string processPath)
    {
        var payload = Path.Combine(_root, "payload");
        Directory.CreateDirectory(payload);
        processPath = Path.Combine(payload, "Plain Craft Launcher 2.exe");
        return payload;
    }

    [TestMethod]
    public void ValidHostAndPayloadResolves()
    {
        var host = CreateHost();
        var payload = CreatePayload(out var processPath);
        Assert.AreEqual(Path.GetFullPath(host),
            Basics.ResolveHostExecutable(host, payload, processPath));
    }

    [TestMethod]
    public void PayloadTrailingSeparatorStillResolves()
    {
        var host = CreateHost();
        var payload = CreatePayload(out var processPath);
        Assert.AreEqual(Path.GetFullPath(host),
            Basics.ResolveHostExecutable(host, payload + Path.DirectorySeparatorChar, processPath));
    }

    [TestMethod]
    public void MismatchedPayloadReturnsNull()
    {
        var host = CreateHost();
        var payload = CreatePayload(out _);
        var other = Path.Combine(_root, "other", "inner.exe");
        Assert.IsNull(Basics.ResolveHostExecutable(host, payload, other));
    }

    [TestMethod]
    public void MissingHostFileReturnsNull()
    {
        var payload = CreatePayload(out var processPath);
        Assert.IsNull(Basics.ResolveHostExecutable(Path.Combine(_root, "gone.exe"), payload, processPath));
    }

    [TestMethod]
    public void EmptyHostOrPayloadReturnsNull()
    {
        var host = CreateHost();
        var payload = CreatePayload(out var processPath);
        Assert.IsNull(Basics.ResolveHostExecutable(null, payload, processPath));
        Assert.IsNull(Basics.ResolveHostExecutable(host, null, processPath));
        Assert.IsNull(Basics.ResolveHostExecutable(host, "", processPath));
    }
}
