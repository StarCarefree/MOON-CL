using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

public sealed record PackDownloadProject(string Source, string Id)
{
    public override string ToString() => (Source == "modrinth" ? "Modrinth" : "CurseForge") + " · " + Id;
}

public sealed record PackDownloadVersion(string Source, string ProjectId, string Id, string Name, string Version,
    string FileName, string Url, string Hash, HashAlgorithmName Algorithm, string ReleaseType, string Minecraft,
    DateTimeOffset Published)
{
    public string Loader { get; init; } = "";
    public override string ToString() => $"{Name}  ·  {ReleaseType}  ·  MC {Minecraft}  ·  {Loader}  ·  {Published:yyyy-MM-dd}";
}

/// <summary>Only downloadable client archives belonging to the requested project are offered for updates.</summary>
public static class PackUpdateDownloads
{
    public const int PageSize = 50;
    public static IReadOnlyList<PackDownloadProject> Projects(PackIdentity identity)
    {
        var result = new List<PackDownloadProject>();
        if (Regex.IsMatch(identity.ModrinthId, "^[A-Za-z0-9]{8}$")) result.Add(new("modrinth", identity.ModrinthId));
        if (int.TryParse(identity.CurseForgeId, out var id) && id > 0) result.Add(new("curseforge", identity.CurseForgeId));
        return result;
    }

    public static Uri Address(PackDownloadProject project, int page = 0)
    {
        PlatformPackCatalog.ValidateId(project.Source, project.Id);
        if (page is < 0 or > 399) throw new ArgumentOutOfRangeException(nameof(page));
        return new Uri(project.Source == "modrinth"
            ? $"https://api.modrinth.com/v2/project/{project.Id}/version"
            : $"https://api.curseforge.com/v1/mods/{project.Id}/files?pageSize={PageSize}&index={page * PageSize}&sortField=3&sortOrder=desc");
    }

    public static IReadOnlyList<PackDownloadVersion> Parse(PackDownloadProject project, string json)
    {
        PlatformPackCatalog.ValidateId(project.Source, project.Id);
        if (json.Length > PlatformPackCatalog.MaxJsonChars) throw new InvalidDataException("版本列表过大。");
        var root = JsonNode.Parse(json);
        var items = (project.Source == "modrinth" ? root : root?["data"]) as JsonArray
            ?? throw new InvalidDataException("版本列表格式不正确。");
        var result = new List<PackDownloadVersion>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var mr = project.Source == "modrinth";
            if (Text(item, mr ? "project_id" : "modId") != project.Id) continue;
            if (mr ? Text(item, "status") is "draft" or "archived" or "scheduled" || Text(item, "client_side") == "unsupported"
                : item["isServerPack"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
                  item["isAvailable"]?.ToString().Equals("false", StringComparison.OrdinalIgnoreCase) == true) continue;
            IEnumerable<JsonObject> files = mr ? (item["files"] as JsonArray)?.OfType<JsonObject>()
                .OrderByDescending(f => Text(f, "primary").Equals("true", StringComparison.OrdinalIgnoreCase)).AsEnumerable() ?? [] : new[] { item };
            foreach (var file in files)
            {
                var name = Text(file, mr ? "filename" : "fileName");
                if (!IsClientArchive(name, mr)) continue;
                var url = Text(file, mr ? "url" : "downloadUrl");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) continue;
                var hash = "";
                var algorithm = HashAlgorithmName.SHA256;
                if (mr)
                {
                    hash = Text(file["hashes"] as JsonObject, "sha512"); algorithm = HashAlgorithmName.SHA512;
                    if (hash.Length == 0) { hash = Text(file["hashes"] as JsonObject, "sha1"); algorithm = HashAlgorithmName.SHA1; }
                }
                else if (file["hashes"] is JsonArray hashes)
                {
                    hash = hashes.OfType<JsonObject>().Where(h => Text(h, "algo") == "1").Select(h => Text(h, "value")).FirstOrDefault() ?? "";
                    algorithm = HashAlgorithmName.SHA1;
                    if (hash.Length == 0)
                    {
                        hash = hashes.OfType<JsonObject>().Where(h => Text(h, "algo") == "2").Select(h => Text(h, "value")).FirstOrDefault() ?? "";
                        algorithm = HashAlgorithmName.MD5;
                    }
                }
                var length = algorithm == HashAlgorithmName.SHA512 ? 128 : algorithm == HashAlgorithmName.SHA1 ? 40 : 32;
                if (hash.Length != length || !Regex.IsMatch(hash, "^[a-fA-F0-9]+$")) continue;
                var type = Text(item, mr ? "version_type" : "releaseType") switch
                {
                    "release" or "1" => "正式版", "beta" or "2" => "测试版", "alpha" or "3" => "预览版", _ => "未知类型"
                };
                var games = (item[mr ? "game_versions" : "gameVersions"] as JsonArray)?.Select(n => n?.ToString() ?? "")
                    .Where(s => Regex.IsMatch(s, @"^\d+\.\d+(?:\.\d+)?$")) ?? [];
                DateTimeOffset.TryParse(Text(item, mr ? "date_published" : "fileDate"), out var published);
                var title = Text(item, mr ? "name" : "displayName");
                var version = mr ? Text(item, "version_number") : title;
                result.Add(new(project.Source, project.Id, Text(item, "id"), title.Length > 0 ? title : name,
                    version, name, uri.AbsoluteUri, hash, algorithm, type, string.Join(", ", games), published)
                {
                    Loader = string.Join(", ", (item[mr ? "loaders" : "gameVersions"] as JsonArray)?.Select(n => n?.ToString() ?? "")
                        .Where(s => s.Equals("fabric", StringComparison.OrdinalIgnoreCase) || s.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
                                    s.Equals("neoforge", StringComparison.OrdinalIgnoreCase) || s.Equals("quilt", StringComparison.OrdinalIgnoreCase)) ?? [])
                });
                break;
            }
        }
        return result.OrderByDescending(v => v.Published).ToArray();
    }

    public static bool HasMore(PackDownloadProject project, string json, int page)
    {
        if (project.Source != "curseforge" || page >= 399) return false;
        var root = PlatformPackCatalog.ReadJson(json);
        if (int.TryParse(root["pagination"]?["totalCount"]?.ToString(), out var total)) return (page + 1) * PageSize < total;
        return (root["data"] as JsonArray)?.Count == PageSize;
    }

    private static bool IsClientArchive(string name, bool modrinth) => name.Length is > 0 and <= 230 &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\') &&
        (modrinth ? name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) : name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) &&
        !Regex.IsMatch(name, @"server[\s_-]*pack|(?:^|[\s_.-])server(?:[\s_.-]|$)", RegexOptions.IgnoreCase);

    private static string Text(JsonObject? item, string key) => item?[key]?.ToString()?.Trim() ?? "";
}
