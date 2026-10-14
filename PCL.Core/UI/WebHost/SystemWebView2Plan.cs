using System;

namespace PCL.Core.UI.WebHost;

/// <summary>
/// 页面层只使用系统已安装的 WebView2 Runtime。不接受固定版本目录，也不接受 Chrome。
/// </summary>
public static class SystemWebView2Plan
{
    public const string LoaderOverride = "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER";

    public static bool PlatformCanHost(WebHostOs os) => os == WebHostOs.Windows;

    public static string? BrowserExecutableFolder(string? requestedFolder) => null;

    public static bool IgnoresCustomFolder(string? requestedFolder) =>
        !string.IsNullOrWhiteSpace(requestedFolder);
}
