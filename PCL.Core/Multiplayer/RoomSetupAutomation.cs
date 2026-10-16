using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PCL.Core.Multiplayer;

/// <summary>Bounded, read-only setup suggestions. Never installs tools, joins a network, or changes firewall rules.</summary>
public static class RoomSetupAutomation
{
    private const int MaximumLogBytes = 256 * 1024;
    private static readonly Regex LanPort = new(@"(?:\[Server thread/[^\]]+\][^\r\n]*?:\s*Started serving on|\[(?:Render|Client) thread/[^\]]+\][^\r\n]*?:\s*(?:\[System\]\s*)?\[CHAT\]\s*Local game hosted on port)\s+(?<port>\d{1,5})(?!\d)", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex JoinedServer = new(@"\[(?:Render|Client|Server) thread/[^\]]+\][^\r\n]*?:\s*(?:Connecting to|Connected to a modded server\.)\s+(?<address>[A-Za-z0-9_.:-]{1,253})(?![\w.:-])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string GenerateNetworkName(string roomCode)
    {
        var code = new string((roomCode ?? "").Where(char.IsAsciiLetterOrDigit).Take(16).ToArray());
        return "mooncl-" + (code.Length == 0 ? Guid.NewGuid().ToString("N")[..12] : code.ToLowerInvariant());
    }
    public static string GenerateNetworkSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public static string SuggestRouteName(string mode, int number) => (mode switch
    {
        "ipv6" => "IPv6 直连", "frp" => "FRP 中继", "p2p" => "P2P 直连", "zerotier" => "ZeroTier 组网", "easytier" => "EasyTier 组网", _ => "游戏线路"
    }) + " · " + Math.Clamp(number, 1, 12);

    public static string? FindExecutable(string fileName, IEnumerable<string> directories)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName ||
            fileName.IndexOfAny(['/', '\\', ':']) >= 0 || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var directory in directories.Take(24).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, fileName));
                if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.Directory) == 0) return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    /// <summary>Reads the last 256 KiB of latest.log with sharing enabled. Caller selects the live game's log.</summary>
    public static int? FindMinecraftLanPort(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return null;
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, stream.Length - MaximumLogBytes);
            stream.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[(int)Math.Min(MaximumLogBytes, stream.Length - offset)];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0) break;
                read += count;
            }
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            if (offset > 0)
            {
                var newline = text.IndexOf('\n');
                text = newline >= 0 ? text[(newline + 1)..] : ""; // Do not match a partially read log line.
            }
            int? port = null;
            foreach (var line in text.Split('\n'))
            {
                if (line.Length > 4096) continue;
                // A stopped integrated server invalidates an earlier LAN announcement in the same process.
                if (line.Contains("[Server thread/", StringComparison.Ordinal) && line.Contains("Stopping server", StringComparison.OrdinalIgnoreCase))
                { port = null; continue; }
                if (line.Contains("[CHAT] <", StringComparison.Ordinal) || line.Contains("[CHAT] [", StringComparison.Ordinal)) continue;
                var match = LanPort.Match(line);
                if (match.Success && int.TryParse(match.Groups["port"].Value, out var value) && value is >= 1 and <= 65535) port = value;
            }
            return port;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or RegexMatchTimeoutException) { return null; }
    }
    /// <summary>Latest remote server the running client joined. Disconnect clears it; local LAN announcements do not count.</summary>
    public static string? FindJoinedServer(string logPath)
    {
        var text = ReadLogTail(logPath);
        if (text is null) return null;
        string? address = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 4096) continue;
            if (line.Contains("Disconnected", StringComparison.OrdinalIgnoreCase) || line.Contains("Lost connection", StringComparison.OrdinalIgnoreCase))
            { address = null; continue; }
            var match = JoinedServer.Match(line);
            if (!match.Success) continue;
            var value = match.Groups["address"].Value.Trim('.');
            if (value.Length is > 0 and <= 253 && !value.Contains("..", StringComparison.Ordinal)) address = value;
        }
        return address;
    }
    private static string? ReadLogTail(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return null;
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, stream.Length - MaximumLogBytes);
            stream.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[(int)Math.Min(MaximumLogBytes, stream.Length - offset)];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0) break;
                read += count;
            }
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            if (offset > 0)
            {
                var newline = text.IndexOf('\n');
                text = newline >= 0 ? text[(newline + 1)..] : "";
            }
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
