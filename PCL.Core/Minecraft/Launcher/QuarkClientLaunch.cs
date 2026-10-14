using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>Opens a Quark share. The browser and cloud-drive protocol only show the share; neither downloads the file.</summary>
public static class QuarkClientLaunch
{
    public static bool TryOpenShare(string shareUrl) =>
        TryOpenShare(shareUrl, FindBrowser(), Start);

    public static bool TryOpenShare(string shareUrl, string? exe, Func<string, string, bool> start)
    {
        if (!IsShareUrl(shareUrl) || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return false;
        return start(exe, shareUrl);
    }

    /// <summary>Opens the official cloud-drive share window. It does not pass a login token and does not download.</summary>
    public static bool TryOpenDriveShare(string shareUrl)
    {
        try { return TryOpenDriveShare(shareUrl, StartProtocol); }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    public static bool TryOpenDriveShare(string shareUrl, Func<string, bool> start)
    {
        var uri = BuildDriveShareUri(shareUrl);
        return uri is not null && start(uri);
    }

    public static string? BuildDriveShareUri(string? shareUrl)
    {
        var match = Regex.Match(shareUrl ?? "", @"\Ahttps://pan\.quark\.cn/s/([A-Za-z0-9]+)(?:\?pwd=([A-Za-z0-9]+))?\z", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var id = Uri.EscapeDataString(match.Groups[1].Value);
        var uri = "qkclouddrive://openShareModal?pwdid=" + id + "&source=13&pwd_id=" + id + "&share_dn=" + id;
        if (match.Groups[2].Success) uri += "&passcode=" + Uri.EscapeDataString(match.Groups[2].Value);
        return uri;
    }

    public static bool IsShareUrl(string? shareUrl) =>
        Regex.IsMatch(shareUrl ?? "", @"\Ahttps://pan\.quark\.cn/s/[A-Za-z0-9]+(?:\?pwd=[A-Za-z0-9]+)?\z", RegexOptions.CultureInvariant);

    public static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end - 1) : null;
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command.Substring(0, space);
    }

    public static string? FindBrowser()
    {
        foreach (var candidate in new[] { ExeFromCommand(RegistryCommand()), @"G:\Quark\quark.exe" })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool Start(string exe, string shareUrl)
    {
        Process.Start(new ProcessStartInfo(exe, "\"" + shareUrl + "\"") { UseShellExecute = true });
        return true;
    }

    private static bool StartProtocol(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        return true;
    }

    private static string? RegistryCommand()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\quark\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch (System.Security.SecurityException) { return null; }
        catch (IOException) { return null; }
    }
}
