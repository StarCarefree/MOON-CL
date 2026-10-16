using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

public sealed record CatalogPackOffer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Minecraft { get; init; } = "";
    public string Loader { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string ShareUrl { get; init; } = "";
    public string SharePath { get; init; } = "";
    public string Modrinth { get; init; } = "";
}

public sealed record ServerDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Address { get; init; } = "";
    public string Minecraft { get; init; } = "";
    public bool Vanilla { get; init; }
    public string Icon { get; init; } = "";
    public string Website { get; init; } = "";
    public string Source { get; init; } = "";
    public IReadOnlyList<CatalogPackOffer> Packs { get; init; } = [];
}

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

    public const string FabulouslyOptimizedId = "fabulously-optimized";
    public const string FabulouslyOptimizedUrl = "https://modrinth.com/modpack/fabulously-optimized";

    public sealed record Document(IReadOnlyList<PackDefinition> Packs, IReadOnlyList<ServerDefinition> Servers);

    public static IReadOnlyList<PackDefinition> Parse(string json, Uri source) => ParseDocument(json, source).Packs;

    public static Document ParseDocument(string json, Uri source)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("目录必须是 JSON 对象。");
        var packs = document.RootElement.TryGetProperty("packs", out var packArray) ? ParsePacks(packArray, source) : [];
        var servers = document.RootElement.TryGetProperty("servers", out var serverArray) ? ParseServers(serverArray, source) : [];
        return new Document(packs, servers);
    }

    public static string Serialize(IReadOnlyList<PackDefinition> packs, IReadOnlyList<ServerDefinition> servers)
    {
        var root = new Dictionary<string, object?>
        {
            ["packs"] = packs.Select(pack => new Dictionary<string, string>
            {
                ["id"] = pack.Id, ["name"] = pack.Name, ["description"] = pack.Description, ["version"] = pack.Version,
                ["minecraft"] = pack.Minecraft, ["author"] = pack.Author, ["loader"] = pack.Loader,
                ["icon"] = pack.Icon, ["background"] = pack.Background, ["downloadUrl"] = pack.DownloadUrl,
                ["fileName"] = pack.FileName, ["sha256"] = pack.Sha256, ["shareUrl"] = pack.ShareUrl, ["sharePath"] = pack.SharePath
            }),
            ["servers"] = servers.Select(server => new Dictionary<string, object?>
            {
                ["id"] = server.Id, ["name"] = server.Name, ["description"] = server.Description, ["address"] = server.Address,
                ["minecraft"] = server.Minecraft, ["vanilla"] = server.Vanilla, ["icon"] = server.Icon,
                ["website"] = server.Website, ["source"] = server.Source,
                ["packs"] = server.Packs.Select(offer => new Dictionary<string, string>
                {
                    ["id"] = offer.Id, ["name"] = offer.Name, ["minecraft"] = offer.Minecraft, ["loader"] = offer.Loader,
                    ["downloadUrl"] = offer.DownloadUrl, ["fileName"] = offer.FileName, ["sha256"] = offer.Sha256,
                    ["shareUrl"] = offer.ShareUrl, ["sharePath"] = offer.SharePath, ["modrinth"] = offer.Modrinth
                })
            })
        };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyList<PackDefinition> ParsePacks(JsonElement array, Uri source)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("packs 必须是数组。");
        if (array.GetArrayLength() > 100) throw new InvalidDataException("预设数量超过 100。");
        var result = new List<PackDefinition>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("整合包条目必须是对象。");
            string Get(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string Url(string key) => Get(key) is { Length: > 0 } url ? HttpUri(url, source).AbsoluteUri : "";
            var id = RequireId(Get("id"), "预设");
            if (result.Any(p => p.Id == id)) throw new InvalidDataException("预设 ID 无效或重复。");
            var fileName = Get("fileName").Length == 0 ? "" : ArchiveName(Get("fileName"));
            var sha = Sha(Get("sha256"));
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

    private static IReadOnlyList<ServerDefinition> ParseServers(JsonElement array, Uri source)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("servers 必须是数组。");
        if (array.GetArrayLength() > 500) throw new InvalidDataException("服务器数量超过 500。");
        var result = new List<ServerDefinition>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("服务器条目必须是对象。");
            string Get(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string Url(string key) => Get(key) is { Length: > 0 } url ? HttpUri(url, source).AbsoluteUri : "";
            var id = RequireId(Get("id"), "服务器");
            if (result.Any(server => server.Id == id)) throw new InvalidDataException("服务器 ID 重复。");
            var address = Get("address").Trim();
            if (!IsAddress(address)) throw new InvalidDataException("服务器地址无效。");
            var minecraft = Get("minecraft").Trim();
            if (minecraft.Length > 32 || minecraft.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')))
                throw new InvalidDataException("服务器 MC 版本无效。");
            var vanilla = item.TryGetProperty("vanilla", out var flag) && flag.ValueKind == JsonValueKind.True;
            var offers = item.TryGetProperty("packs", out var offersNode) ? ParseOffers(offersNode, source) : [];
            result.Add(new ServerDefinition
            {
                Id = id, Name = RequireText(Get("name"), "服务器名称"), Description = Get("description"),
                Address = address, Minecraft = minecraft, Vanilla = vanilla, Icon = Url("icon"),
                Website = Url("website"), Source = Get("source"), Packs = offers
            });
        }
        return result;
    }

    private static IReadOnlyList<CatalogPackOffer> ParseOffers(JsonElement array, Uri source)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 20) throw new InvalidDataException("服务器推荐整合包无效。");
        var result = new List<CatalogPackOffer>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("推荐整合包必须是对象。");
            string Get(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string Url(string key) => Get(key) is { Length: > 0 } url ? HttpUri(url, source).AbsoluteUri : "";
            var id = RequireId(Get("id"), "推荐整合包");
            if (result.Any(offer => offer.Id == id)) throw new InvalidDataException("推荐整合包 ID 重复。");
            var modrinth = Url("modrinth");
            if (modrinth.Length > 0 && !modrinth.StartsWith("https://modrinth.com/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("推荐整合包的 Modrinth 地址无效。");
            result.Add(new CatalogPackOffer
            {
                Id = id, Name = RequireText(Get("name"), "推荐整合包名称"), Minecraft = Get("minecraft"), Loader = Get("loader"),
                DownloadUrl = Url("downloadUrl"), FileName = Get("fileName").Length == 0 ? "" : ArchiveName(Get("fileName")),
                Sha256 = Sha(Get("sha256")), ShareUrl = Url("shareUrl"), SharePath = Get("sharePath"), Modrinth = modrinth
            });
        }
        return result;
    }

    public static bool IsAddress(string address)
    {
        if (address.Length is < 1 or > 253 || address.Contains(' ') || address.Contains("://", StringComparison.Ordinal)) return false;
        var port = 25565;
        var host = address;
        var split = address.LastIndexOf(':');
        if (split > 0 && address.IndexOf(':') == split)
        {
            host = address[..split];
            if (!int.TryParse(address[(split + 1)..], out port) || port is < 1 or > 65535) return false;
        }
        return host.Length > 0 && host.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' || ch == '_') && port > 0;
    }

    public static CatalogPackOffer RecommendedFabric(string minecraft) => new()
    {
        Id = FabulouslyOptimizedId, Name = "Fabulously Optimized", Minecraft = minecraft, Loader = "Fabric",
        Modrinth = FabulouslyOptimizedUrl
    };

    private static string RequireId(string id, string kind)
    {
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{0,63}$")) throw new InvalidDataException(kind + " ID 无效。");
        return id;
    }

    private static string RequireText(string value, string kind)
    {
        var text = value.Trim();
        if (text.Length is 0 or > 120) throw new InvalidDataException(kind + "无效。");
        return text;
    }

    private static string ArchiveName(string fileName)
    {
        if (fileName.Length == 0) return "";
        if (Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !(fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("下载文件名必须为 ZIP 或 MRPACK 文件名。");
        return fileName;
    }

    private static string Sha(string sha)
    {
        if (sha.Length > 0 && !Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("SHA256 格式无效。");
        return sha;
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

    /// <summary>Default instance folder from a downloaded archive name. Illegal characters become fullwidth; collisions get " (2)".</summary>
    public static string InstanceFolderName(string fileName, string? parentFolder = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
        var builder = new System.Text.StringBuilder(stem.Length);
        foreach (var ch in stem)
        {
            if (ch < ' ' || Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0) builder.Append(Fullwidth(ch));
            else builder.Append(ch);
        }
        var name = builder.ToString().Trim().TrimEnd('.');
        if (name.Length == 0) name = "整合包";
        if (name.Length > 80) name = name[..80].TrimEnd().TrimEnd('.');
        if (name.Length == 0) name = "整合包";
        if (string.IsNullOrEmpty(parentFolder) || !Directory.Exists(parentFolder)) return name;
        var taken = new HashSet<string>(Directory.EnumerateDirectories(parentFolder).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var n = 2; n < 1000; n++)
        {
            var suffix = " (" + n + ")";
            var candidate = (name.Length + suffix.Length <= 80 ? name : name[..(80 - suffix.Length)].TrimEnd().TrimEnd('.')) + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("已有过多同名实例，请手动填写名称。");
    }

    private static char Fullwidth(char ch) => ch switch
    {
        '\\' => '＼', '/' => '／', '|' => '｜', ':' => '：', '<' => '＜', '>' => '＞', '*' => '＊', '?' => '？', '"' => '＂',
        _ => '＿'
    };
}
