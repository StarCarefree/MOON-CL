using System;

namespace PCL.Core.UI.WebHost;

public enum ExternalBrowser
{
    SystemChrome,
    ShellDefault,
    Rejected
}

/// <summary>
/// 外链走系统 Chrome；协议链接和无效地址不交给 Chrome。
/// </summary>
public static class ChromeLinkPlan
{
    public static bool IsHttp(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    public static ExternalBrowser Choose(string? url, bool chromeInstalled)
    {
        if (!IsHttp(url)) return ExternalBrowser.Rejected;
        return chromeInstalled ? ExternalBrowser.SystemChrome : ExternalBrowser.ShellDefault;
    }
}
