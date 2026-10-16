using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCL.Core.IO;

/// <summary>Chooses the local modpack download cache. A saved folder wins; otherwise the system temp directory is used when it has room.</summary>
public static class PackDownloadCache
{
    public const string FolderName = "PCL-PackDownloads";
    public const long MinimumFreeBytes = 256L * 1024 * 1024;

    public static readonly string[] FallbackParents = [@"I:\tmp-pcl", @"D:\pcl-cy\tmp", @"G:\download"];

    public static string Resolve(string? configured, string systemTemp, Func<string, long, bool>? hasRoom = null, Func<string, bool>? parentExists = null)
    {
        hasRoom ??= DefaultHasRoom;
        parentExists ??= Directory.Exists;
        if (!string.IsNullOrWhiteSpace(configured))
            return NormalizeChosenFolder(configured);

        if (hasRoom(systemTemp, MinimumFreeBytes)) return Path.Combine(systemTemp, FolderName);
        foreach (var candidate in FallbackParents)
        {
            var root = Path.GetPathRoot(candidate);
            if (string.IsNullOrEmpty(root) || !parentExists(root)) continue;
            if (hasRoom(candidate, MinimumFreeBytes)) return Path.Combine(candidate, FolderName);
        }

        throw new IOException("临时盘空间不足，已停止下载，没有写入整合包。");
    }

    public static string NormalizeChosenFolder(string selected)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected.Trim()));
        return full.EndsWith(FolderName, StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.Combine(full, FolderName);
    }

    public static string[] ClearTargets(string? configured, string systemTemp)
    {
        var targets = new List<string> { Path.Combine(systemTemp, FolderName) };
        foreach (var parent in FallbackParents) targets.Add(Path.Combine(parent, FolderName));
        if (!string.IsNullOrWhiteSpace(configured))
            targets.Add(NormalizeChosenFolder(configured));
        return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool DefaultHasRoom(string path, long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace >= bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
