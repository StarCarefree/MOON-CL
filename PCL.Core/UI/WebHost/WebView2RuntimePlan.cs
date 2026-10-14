using System;
using System.IO;

namespace PCL.Core.UI.WebHost;

/// <summary>Portable distributions use their own fixed runtime; developer builds may use Evergreen.</summary>
public static class WebView2RuntimePlan
{
    public const string BundledDirectoryName = "WebView2Runtime";

    public static string? FindBundledFolder(string baseDirectory, Func<string, bool> fileExists)
    {
        var folder = Path.Combine(baseDirectory, BundledDirectoryName);
        return fileExists(Path.Combine(folder, "msedgewebview2.exe")) ? folder : null;
    }

    public static bool IsTrustedSource(string? source, string host)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
               uri.IsDefaultPort && uri.UserInfo.Length == 0;
    }

    public static bool UseWebUi(string[] args) =>
        Array.Exists(args, arg => arg.Equals("--webview", StringComparison.OrdinalIgnoreCase) || arg.Equals("--browse", StringComparison.OrdinalIgnoreCase)) &&
        !Array.Exists(args, arg => arg.Equals("--native-ui", StringComparison.OrdinalIgnoreCase));
}
