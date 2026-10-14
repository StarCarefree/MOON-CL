using System;
using System.Collections.Generic;
using System.IO;

namespace PCL.Core.UI.WebHost;

/// <summary>
/// 查找系统安装的 Google Chrome。不把 Edge / WebView2 运行时算进去。
/// </summary>
public static class SystemChromeLocator
{
    public static string? Find(
        WebHostOs os,
        IReadOnlyDictionary<string, string?> environment,
        Func<string, bool> fileExists,
        string? home = null,
        string? registryPath = null)
    {
        foreach (var candidate in Candidates(os, environment, home, registryPath))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!IsChromeBinary(candidate)) continue;
            if (fileExists(candidate)) return candidate;
        }

        return null;
    }

    public static IEnumerable<string> Candidates(
        WebHostOs os,
        IReadOnlyDictionary<string, string?> environment,
        string? home = null,
        string? registryPath = null)
    {
        if (!string.IsNullOrWhiteSpace(registryPath))
            yield return registryPath;

        if (os == WebHostOs.Windows)
        {
            yield return Combine(environment, "ProgramFiles", @"Google\Chrome\Application\chrome.exe");
            yield return Combine(environment, "ProgramFiles(x86)", @"Google\Chrome\Application\chrome.exe");
            yield return Combine(environment, "ProgramW6432", @"Google\Chrome\Application\chrome.exe");
            yield return Combine(environment, "LocalAppData", @"Google\Chrome\Application\chrome.exe");
        }
        else if (os == WebHostOs.MacOS)
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            if (!string.IsNullOrWhiteSpace(home))
                yield return Path.Combine(home, "Applications", "Google Chrome.app", "Contents", "MacOS", "Google Chrome");
        }
    }

    public static bool IsChromeBinary(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Google Chrome", StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Combine(IReadOnlyDictionary<string, string?> environment, string variable, string relative)
    {
        environment.TryGetValue(variable, out var root);
        if (string.IsNullOrWhiteSpace(root)) return "";
        return Path.Combine(root, relative);
    }
}
