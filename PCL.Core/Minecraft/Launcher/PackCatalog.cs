using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

public sealed record PackDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public string Minecraft { get; init; } = "";
    public string Author { get; init; } = "";
    public string Loader { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Background { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string FileName { get; init; } = "pack.zip";
    public string LocalArchive { get; init; } = "";
    public string ShareUrl { get; init; } = "";
    public string SharePath { get; init; } = "";
}

/// <summary>Server metadata is data, never a local path or an executable command.</summary>
public static class PackCatalog
{
    public const string StarRailId = "starrail-harpy-express";
    public const string TestArchive = @"G:\download\(PCL推荐使用)StarRailHarpyExpress V4.3.3-Release (1).zip";
    public const string TestDownloadUrl = "https://gitee.com/starrailexpress/starrailexpress/releases/download/modpack/(PCL%E6%8E%A8%E8%8D%90%E4%BD%BF%E7%94%A8)StarRailHarpyExpress%20V4.3.3-Release.zip";
    public const string TestDownloadFile = "(PCL推荐使用)StarRailHarpyExpress V4.3.3-Release.zip";
    public const string TestShare = "https://pan.quark.cn/s/e3f623dd58d6";
    public const string TestSharePath = "SRE哈比列车/4.4.0/公开版（不含地图）StarRailHarpyExpress V4.4.0-beta1.mrpack";
    public static PackDefinition BuiltIn => new()
    {
        Id = StarRailId, Name = "StarRailHarpyExpress", Version = "V4.3.0", Minecraft = "1.21.1",
        Description = "SRE 哈比列车 · 本地测试包（外层标记 4.3.3，包内版本 V4.3.0）", LocalArchive = TestArchive,
        DownloadUrl = TestDownloadUrl, FileName = TestDownloadFile,
        ShareUrl = TestShare, SharePath = TestSharePath
    };

    public static string ResolveSelection(string selected, IEnumerable<string> presets, IEnumerable<string> instances)
    {
        var ids = presets.ToArray();
        return ids.Contains(selected, StringComparer.Ordinal) || instances.Contains(selected, StringComparer.Ordinal)
            ? selected : ids.FirstOrDefault() ?? StarRailId;
    }

    public static Uri HttpUri(string value, Uri? relativeTo = null)
    {
        var uri = relativeTo is null ? new Uri(value, UriKind.Absolute) : new Uri(relativeTo, value);
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("只允许不含凭据的 HTTP(S) 地址。");
        return uri;
    }

    public static IReadOnlyList<PackDefinition> Parse(string json, Uri source)
    {
        using var document = JsonDocument.Parse(json);
        var array = document.RootElement.GetProperty("packs");
        if (array.GetArrayLength() > 100) throw new InvalidDataException("预设数量超过 100。");
        var result = new List<PackDefinition>();
        foreach (var item in array.EnumerateArray())
        {
            string Get(string key) => item.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
            string Url(string key) => Get(key) is { Length: > 0 } url ? HttpUri(url, source).AbsoluteUri : "";
            var id = Get("id");
            if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{0,63}$") || result.Any(p => p.Id == id))
                throw new InvalidDataException("预设 ID 无效或重复。");
            var fileName = Get("fileName");
            if (fileName.Length == 0) fileName = "pack.zip";
            if (Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !(fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("下载文件名必须为 ZIP 或 MRPACK 文件名。");
            var sha = Get("sha256");
            if (sha.Length > 0 && !Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("SHA256 格式无效。");
            var pack = new PackDefinition
            {
                Id = id, Name = Get("name"), Description = Get("description"), Version = Get("version"),
                Minecraft = Get("minecraft"), Icon = Url("icon"), Background = Url("background"),
                Author = Get("author"), Loader = Get("loader"),
                DownloadUrl = Url("downloadUrl"), FileName = fileName, Sha256 = sha,
                ShareUrl = Url("shareUrl"), SharePath = Get("sharePath")
            };
            if (string.IsNullOrWhiteSpace(pack.Name) || string.IsNullOrWhiteSpace(pack.Version)) throw new InvalidDataException("缺少名称或版本。");
            result.Add(pack);
        }
        return result;
    }

    /// <summary>Canonical containment check for all update paths, including Windows drive/ADS syntax.</summary>
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\'))
            throw new InvalidDataException("非法相对路径：" + relative);
        var parts = relative.Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("非法相对路径：" + relative);
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(canonicalRoot, relative));
        if (!target.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越界。");
        // Reject junctions/symlinks in existing ancestors as well as the file itself.
        for (var path = target; path.Length >= canonicalRoot.TrimEnd(Path.DirectorySeparatorChar).Length; path = Path.GetDirectoryName(path)!)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新路径不允许符号链接。");
            if (string.Equals(path, canonicalRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
        return target;
    }
}
