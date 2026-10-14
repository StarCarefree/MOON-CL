namespace PCL.Core.UI.WebHost;

/// <summary>
/// Windows 页面层嵌入系统已安装的 WebView2 Runtime。exe 只带加载器，不打包浏览器。
/// </summary>
public sealed record WebHostCapability(
    string Os,
    string EmbeddedEngine,
    bool CanEmbedWebView2,
    bool CanEmbedSystemChrome,
    bool CanOpenSystemChrome,
    bool UsesSystemBrowser,
    bool ShipsBrowserRuntime,
    bool PageLayerPortable,
    bool NativeWpfShellPortable,
    string ChromePath,
    string EdgePath,
    string Summary)
{
    public static WebHostCapability Evaluate(WebHostOs os, string? chromePath, bool systemWebView2Installed = false)
    {
        var chrome = chromePath ?? "";
        var canOpenChrome = chrome.Length > 0;
        var canEmbed = SystemWebView2Plan.PlatformCanHost(os) && systemWebView2Installed;
        return os switch
        {
            WebHostOs.Windows => new WebHostCapability(
                "windows",
                canEmbed ? "SystemWebView2" : "None",
                canEmbed,
                CanEmbedSystemChrome: false,
                canOpenChrome,
                UsesSystemBrowser: canEmbed,
                ShipsBrowserRuntime: false,
                PageLayerPortable: true,
                NativeWpfShellPortable: true,
                chrome,
                "",
                canEmbed
                    ? "Windows 页面层嵌入系统已安装的 WebView2 Runtime，不把运行时打进 exe。系统 Chrome 只打开外链。"
                    : "Windows 可以嵌入系统 WebView2，但这台机器还没安装 WebView2 Runtime。不会改用 Chrome，也不会打包浏览器。"),
            WebHostOs.MacOS => new WebHostCapability(
                "macos",
                "None",
                CanEmbedWebView2: false,
                CanEmbedSystemChrome: false,
                canOpenChrome,
                UsesSystemBrowser: false,
                ShipsBrowserRuntime: false,
                PageLayerPortable: true,
                NativeWpfShellPortable: false,
                chrome,
                "",
                "macOS 没有系统 WebView2。同一套 HTML 和 JSON bridge 可以以后放进 WKWebView；系统 Chrome 仍然只能开外链。当前 WPF 壳是 net10.0-windows，不能在 Mac 上运行。"),
            WebHostOs.Linux => new WebHostCapability(
                "linux",
                "None",
                CanEmbedWebView2: false,
                CanEmbedSystemChrome: false,
                canOpenChrome,
                UsesSystemBrowser: false,
                ShipsBrowserRuntime: false,
                PageLayerPortable: true,
                NativeWpfShellPortable: false,
                chrome,
                "",
                "Linux 没有系统 WebView2。页面文件可移植，WPF 壳不能运行。"),
            _ => new WebHostCapability(
                "unknown",
                "None",
                CanEmbedWebView2: false,
                CanEmbedSystemChrome: false,
                canOpenChrome,
                UsesSystemBrowser: false,
                ShipsBrowserRuntime: false,
                PageLayerPortable: true,
                NativeWpfShellPortable: false,
                chrome,
                "",
                "未知系统。没有系统 WebView2 可用。")
        };
    }
}
