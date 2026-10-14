using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.WebHost;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class WebHostCompatibilityTest
{
    [TestMethod]
    public void WindowsEmbedsInstalledWebView2AndDoesNotShipIt()
    {
        var capability = WebHostCapability.Evaluate(
            WebHostOs.Windows,
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            systemWebView2Installed: true);

        Assert.AreEqual("SystemWebView2", capability.EmbeddedEngine);
        Assert.IsTrue(capability.CanEmbedWebView2);
        Assert.IsFalse(capability.CanEmbedSystemChrome);
        Assert.IsTrue(capability.CanOpenSystemChrome);
        Assert.IsTrue(capability.UsesSystemBrowser);
        Assert.IsFalse(capability.ShipsBrowserRuntime);
        Assert.IsTrue(capability.NativeWpfShellPortable);
    }

    [TestMethod]
    public void MissingRuntimeDoesNotFallBackToChrome()
    {
        var capability = WebHostCapability.Evaluate(
            WebHostOs.Windows,
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            systemWebView2Installed: false);

        Assert.AreEqual("None", capability.EmbeddedEngine);
        Assert.IsFalse(capability.CanEmbedWebView2);
        Assert.IsFalse(capability.ShipsBrowserRuntime);
    }

    [TestMethod]
    public void MacHasNoSystemWebView2()
    {
        var capability = WebHostCapability.Evaluate(
            WebHostOs.MacOS,
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            systemWebView2Installed: true);

        Assert.AreEqual("None", capability.EmbeddedEngine);
        Assert.IsFalse(capability.CanEmbedWebView2);
        Assert.IsFalse(capability.ShipsBrowserRuntime);
        Assert.IsTrue(capability.PageLayerPortable);
        Assert.IsFalse(capability.NativeWpfShellPortable);
    }

    [TestMethod]
    public void CustomBrowserFolderIsNeverPassedToTheLoader()
    {
        Assert.IsNull(SystemWebView2Plan.BrowserExecutableFolder(null));
        Assert.IsNull(SystemWebView2Plan.BrowserExecutableFolder(@"C:\Program Files\Google\Chrome\Application"));
        Assert.IsNull(SystemWebView2Plan.BrowserExecutableFolder(@"C:\FixedRuntime"));
        Assert.IsTrue(SystemWebView2Plan.IgnoresCustomFolder(@"C:\FixedRuntime"));
        Assert.IsFalse(SystemWebView2Plan.PlatformCanHost(WebHostOs.MacOS));
        Assert.IsTrue(SystemWebView2Plan.PlatformCanHost(WebHostOs.Windows));
    }

    [TestMethod]
    public void LocatorFindsWindowsChromeAndIgnoresEdge()
    {
        var root = @"D:\Apps";
        var chrome = Path.Combine(root, @"Google\Chrome\Application\chrome.exe");
        var edge = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
        var found = SystemChromeLocator.Find(
            WebHostOs.Windows,
            new Dictionary<string, string?>
            {
                ["LocalAppData"] = root
            },
            path => path == chrome,
            registryPath: edge);

        Assert.AreEqual(chrome, found);
    }

    [TestMethod]
    public void LocatorFindsMacChrome()
    {
        var home = "/Users/me";
        var chrome = Path.Combine(home, "Applications", "Google Chrome.app", "Contents", "MacOS", "Google Chrome");
        var found = SystemChromeLocator.Find(
            WebHostOs.MacOS,
            new Dictionary<string, string?>(),
            path => path == chrome,
            home: home);

        Assert.AreEqual(chrome, found);
    }

    [TestMethod]
    public void HttpUsesChromeWhenInstalledAndProtocolsDoNot()
    {
        Assert.AreEqual(ExternalBrowser.SystemChrome, ChromeLinkPlan.Choose("https://github.com/PCL-Community/PCL-CE", true));
        Assert.AreEqual(ExternalBrowser.ShellDefault, ChromeLinkPlan.Choose("https://example.test", false));
        Assert.AreEqual(ExternalBrowser.Rejected, ChromeLinkPlan.Choose("minecraft://launch", true));
    }

    [TestMethod]
    public void PageCatalogKeepsNativeIdsAndDoesNotPretendEveryPageIsPorted()
    {
        Assert.IsTrue(LauncherPageCatalog.Routes.Count > 20);
        Assert.IsTrue(LauncherPageCatalog.Find("setup.about")!.WebImplemented);
        Assert.IsFalse(LauncherPageCatalog.Find("download.forge")!.WebImplemented);
        Assert.AreEqual(LauncherPageCatalog.Download, LauncherPageCatalog.Find("download.forge")!.NativePage);
        Assert.AreEqual(11, LauncherPageCatalog.Find("download.forge")!.NativeSubType);
        Assert.AreEqual(1, LauncherPageCatalog.Find("tools.lobby")!.NativeSubType);
        Assert.AreEqual(3, LauncherPageCatalog.Find("tools.test")!.NativeSubType);
        Assert.IsNull(LauncherPageCatalog.Find("missing"));
    }

    [TestMethod]
    public void PlatformNamesMapToHostOs()
    {
        Assert.AreEqual(WebHostOs.MacOS, WebHostPlatform.Detect("Darwin 24.0.0"));
        Assert.AreEqual(WebHostOs.Windows, WebHostPlatform.Detect("Microsoft Windows 10.0.26100"));
        Assert.AreEqual(WebHostOs.Linux, WebHostPlatform.Detect("Linux 6.8"));
    }
}
