using System;
using System.Runtime.InteropServices;

namespace PCL.Core.UI.WebHost;

/// <summary>
/// 页面宿主要区分的操作系统。WebView2 只存在于 Windows。
/// </summary>
public enum WebHostOs
{
    Windows,
    MacOS,
    Linux,
    Unknown
}

public static class WebHostPlatform
{
    public static WebHostOs Detect()
    {
        if (OperatingSystem.IsWindows()) return WebHostOs.Windows;
        if (OperatingSystem.IsMacOS()) return WebHostOs.MacOS;
        if (OperatingSystem.IsLinux()) return WebHostOs.Linux;
        return Detect(RuntimeInformation.OSDescription);
    }

    public static WebHostOs Detect(string osDescription)
    {
        if (osDescription.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return WebHostOs.Windows;
        if (osDescription.Contains("Mac", StringComparison.OrdinalIgnoreCase) ||
            osDescription.Contains("Darwin", StringComparison.OrdinalIgnoreCase))
            return WebHostOs.MacOS;
        if (osDescription.Contains("Linux", StringComparison.OrdinalIgnoreCase))
            return WebHostOs.Linux;
        return WebHostOs.Unknown;
    }
}
