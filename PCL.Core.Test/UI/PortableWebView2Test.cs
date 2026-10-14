using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.WebHost;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PortableWebView2Test
{
    [TestMethod]
    public void BundledRuntimeIsResolvedRelativeToExecutableNotWorkingDirectory()
    {
        var root = @"D:\Portable PCL";
        var expected = Path.Combine(root, "WebView2Runtime");
        Assert.AreEqual(expected, WebView2RuntimePlan.FindBundledFolder(root,
            path => path == Path.Combine(expected, "msedgewebview2.exe")));
    }

    [TestMethod]
    public void ChromeIsNotAWebView2Runtime()
    {
        Assert.IsNull(WebView2RuntimePlan.FindBundledFolder(@"D:\PCL", path => path.EndsWith("chrome.exe", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NativePresentationIsDefaultAndBrowserIsAnExplicitSupplement()
    {
        Assert.IsFalse(WebView2RuntimePlan.UseWebUi([]));
        Assert.IsTrue(WebView2RuntimePlan.UseWebUi(["--webview"]));
        Assert.IsTrue(WebView2RuntimePlan.UseWebUi(["--browse"]));
        Assert.IsFalse(WebView2RuntimePlan.UseWebUi(["--webview", "--NATIVE-UI"]));
    }

    [TestMethod]
    [DataRow("https://pcl.webui/index.html", true)]
    [DataRow("https://PCL.WEBUI/index.html", true)]
    [DataRow("https://pcl.webui.evil.test/index.html", false)]
    [DataRow("https://pcl.webui@evil.test/", false)]
    [DataRow("https://evil.test@pcl.webui/", false)]
    [DataRow("https://pcl.webui:8080/", false)]
    [DataRow("http://pcl.webui/index.html", false)]
    [DataRow("file:///D:/index.html", false)]
    [DataRow("javascript:alert(1)", false)]
    [DataRow("", false)]
    public void BridgeAcceptsOnlyItsOwnHttpsOrigin(string source, bool trusted)
    {
        Assert.AreEqual(trusted, WebView2RuntimePlan.IsTrustedSource(source, "pcl.webui"));
    }
}
