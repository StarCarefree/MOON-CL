using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PCL.Core.Minecraft.Launcher;

public sealed record MigrationReceipt(bool Verified, string Source, string Destination, string[] Copied);

public static class InstanceNames
{
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string((name ?? "").Select(ch => invalid.Contains(ch) || ch is '!' or ';' ? ' ' : ch).ToArray()).Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        if (text.Length == 0) text = "整合包";
        return text.Length > 80 ? text[..80].Trim() : text;
    }

    public static string Next(string oldName, string packName, string version, Func<string, bool> exists)
    {
        var stem = Sanitize(string.IsNullOrWhiteSpace(packName) ? oldName : packName);
        if (!string.IsNullOrWhiteSpace(version)) stem = Sanitize(stem + " " + version);
        if (stem.Equals(Sanitize(oldName), StringComparison.OrdinalIgnoreCase) || exists(stem)) stem = Sanitize(oldName + " 更新");
        var name = stem;
        var index = 2;
        while (exists(name) || name.Equals(oldName, StringComparison.OrdinalIgnoreCase))
        {
            name = Sanitize(stem + " " + index);
            if (++index > 1000) throw new IOException("没有可用的新版本名称。");
        }
        return name;
    }
}

/// <summary>Copies player progress only. Quest books and mod files stay with the newly installed pack.</summary>
public static class InstancePlayerData
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string ReceiptPath(string versionFolder) => Path.Combine(versionFolder.TrimEnd('\\', '/'), "PCL", "player-data-migrated.json");

    public static bool IsPlayerData(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || path.Split('/').Any(part => part is "." or ".." || part.Length == 0)) return false;
        if (EqualsAny(path, "options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat")) return true;
        if (Starts(path, "saves/") || Starts(path, "xaero/") || Starts(path, "XaeroWorldMap/") || Starts(path, "XaeroMinimap/")) return true;
        if (Starts(path, "local/ftbquests/") || Starts(path, "local/ftbteams/")) return true;
        if (Starts(path, "config/ftbquests/quests/") || EqualsAny(path, "config/ftbquests/quests.snbt")) return false;
        if (Contains(path, "/ftbquests/") || Contains(path, "/ftbteams/") || Starts(path, "ftbquests/") || Starts(path, "ftbteams/")) return true;
        if (!Starts(path, "config/")) return false;
        if (Starts(path, "config/xaero/") || Starts(path, "config/xaerominimap") || Starts(path, "config/xaeroworldmap")) return true;
        var file = path[(path.LastIndexOf('/') + 1)..];
        return file.StartsWith("xaero", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReady(string versionFolder, string instanceName)
    {
        var root = versionFolder.TrimEnd('\\', '/');
        var json = Path.Combine(root, instanceName + ".json");
        return Directory.Exists(root) && File.Exists(json) && new FileInfo(json).Length > 0 && !File.Exists(Path.Combine(root, ".pclignore"));
    }

    public static MigrationReceipt Copy(string oldIndie, string newIndie)
    {
        var source = Path.GetFullPath(oldIndie).TrimEnd('\\', '/');
        var destination = Path.GetFullPath(newIndie).TrimEnd('\\', '/');
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能把玩家数据复制到同一个目录。");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("找不到旧版本的游戏目录。");
        Directory.CreateDirectory(destination);
        var copied = new List<string>();
        foreach (var file in EnumerateFiles(source))
        {
            var relative = Path.GetRelativePath(source, file);
            if (!IsPlayerData(relative)) continue;
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!IsInside(destination, target)) throw new IOException("玩家数据路径越界：" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            if (new FileInfo(target).Length != new FileInfo(file).Length) throw new IOException("复制后大小不一致：" + relative);
            copied.Add(relative.Replace('\\', '/'));
        }
        return new MigrationReceipt(true, source, destination, copied.ToArray());
    }

    public static void WriteReceipt(string versionFolder, MigrationReceipt receipt)
    {
        var path = ReceiptPath(versionFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(receipt, Options));
    }

    public static MigrationReceipt? TryReadReceipt(string versionFolder)
    {
        var path = ReceiptPath(versionFolder);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MigrationReceipt>(File.ReadAllText(path), Options); }
        catch (JsonException) { return null; }
    }

    public static bool MayDelete(bool requested, string oldVersionFolder, string newVersionFolder, string newInstanceName, string? sharedGameFolder = null, Func<string, string, bool>? ready = null)
    {
        ready ??= IsReady;
        if (!requested || string.IsNullOrWhiteSpace(newInstanceName)) return false;
        var oldPath = Full(oldVersionFolder);
        var newPath = Full(newVersionFolder);
        var shared = string.IsNullOrWhiteSpace(sharedGameFolder) ? "" : Full(sharedGameFolder);
        if (oldPath.Length == 0 || newPath.Length == 0 || Same(oldPath, newPath) || shared.Length > 0 && Same(oldPath, shared)) return false;
        if (newPath.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            oldPath.StartsWith(newPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        if (!ready(newVersionFolder, newInstanceName)) return false;
        var receipt = TryReadReceipt(newVersionFolder);
        return receipt is { Verified: true };
    }

    public static void DeleteOldIfSafe(string oldVersionFolder, string newVersionFolder, string newInstanceName, bool requested, string? sharedGameFolder = null, Func<string, string, bool>? ready = null)
    {
        ready ??= IsReady;
        if (!MayDelete(requested, oldVersionFolder, newVersionFolder, newInstanceName, sharedGameFolder, ready))
            throw new InvalidOperationException("新版本尚未核对完成，已保留旧版本。");
        var oldPath = Full(oldVersionFolder);
        var versionsDir = Directory.GetParent(oldPath)?.FullName ?? throw new IOException("找不到旧版本的上级目录。");
        var mcDir = Directory.GetParent(versionsDir)?.FullName ?? versionsDir;
        var quarantine = Path.Combine(mcDir, "PCL", "instance-quarantine", Path.GetFileName(oldPath) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
        Directory.Move(oldPath, quarantine);
        try
        {
            if (!ready(newVersionFolder, newInstanceName)) throw new IOException("移走旧版本后新版本不可用。");
            Directory.Delete(quarantine, true);
        }
        catch
        {
            if (!Directory.Exists(oldPath) && Directory.Exists(quarantine)) Directory.Move(quarantine, oldPath);
            throw;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(dir); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) continue;
                if (Directory.Exists(child)) pending.Push(child);
                else yield return child;
            }
        }
    }

    private static bool IsInside(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Full(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path).TrimEnd('\\', '/');
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Starts(string path, string prefix) => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    private static bool Contains(string path, string value) => path.Contains(value, StringComparison.OrdinalIgnoreCase);
    private static bool EqualsAny(string path, params string[] values) => values.Any(value => path.Equals(value, StringComparison.OrdinalIgnoreCase));
}
