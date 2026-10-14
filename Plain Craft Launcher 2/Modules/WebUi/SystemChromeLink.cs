using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using PCL.Core.UI.WebHost;

namespace PCL;

/// <summary>
/// 把 http(s) 外链交给系统安装的 Google Chrome。找不到或启动失败时返回 false，调用方再走默认浏览器。
/// </summary>
internal static class SystemChromeLink
{
    public static string? FindInstalled()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProgramFiles"] = Environment.GetEnvironmentVariable("ProgramFiles"),
            ["ProgramFiles(x86)"] = Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            ["ProgramW6432"] = Environment.GetEnvironmentVariable("ProgramW6432"),
            ["LocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        return SystemChromeLocator.Find(
            WebHostPlatform.Detect(),
            environment,
            File.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ReadAppPath());
    }

    public static bool TryOpenHttp(string url)
    {
        var chrome = FindInstalled();
        if (ChromeLinkPlan.Choose(url, chrome is not null) != ExternalBrowser.SystemChrome || chrome is null)
            return false;

        try
        {
            var start = new ProcessStartInfo(chrome)
            {
                UseShellExecute = false
            };
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add(url);
            Process.Start(start);
            ModBase.Log("[System] 正在用系统 Chrome 打开网页：" + url);
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "系统 Chrome 打开失败，改走默认浏览器");
            return false;
        }
    }

    private static string? ReadAppPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
                if (key?.GetValue(null) is string path && path.Length > 0) return path;
            }

            using var user = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (user?.GetValue(null) is string userPath && userPath.Length > 0) return userPath;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取 Chrome App Paths 失败", ModBase.LogLevel.Debug);
        }

        return null;
    }
}
