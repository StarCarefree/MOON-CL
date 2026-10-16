using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

public sealed record PlatformFile(string Source, string ProjectId, string Id, string Name, string Version, DateTimeOffset Published);

/// <summary>Compares an installed CurseForge or Modrinth pack with that platform's newest release. MC百科 is not queried.</summary>
public static class PackReleaseCheck
{
    public const int MaxJsonChars = 2 * 1024 * 1024;

    public static Uri FilesAddress(string source, string projectId)
    {
        if (source == "curseforge")
        {
            if (!int.TryParse(projectId, out var id) || id <= 0) throw new ArgumentException("无效的 CurseForge 项目 ID。");
            return new Uri($"https://api.curseforge.com/v1/mods/{id}/files?pageSize=50&index=0&sortField=3&sortOrder=desc");
        }
        if (source == "modrinth" && Regex.IsMatch(projectId, "^[A-Za-z0-9]{8}$"))
            return new Uri("https://api.modrinth.com/v2/project/" + projectId + "/version?limit=20");
        throw new ArgumentException("无效的 Modrinth 项目 ID。");
    }

    public static PlatformFile? LatestRelease(string source, string json, string projectId)
    {
        if (source is not ("curseforge" or "modrinth")) throw new ArgumentException("只检测 CurseForge 和 Modrinth。");
        if (json.Length > MaxJsonChars) throw new InvalidDataException("版本列表过大。");
        var root = JsonNode.Parse(json);
        var items = source == "curseforge" ? (root as JsonObject)?["data"] as JsonArray : root as JsonArray;
        if (items is null) throw new InvalidDataException("版本列表格式不对。");
        PlatformFile? best = null;
        foreach (var node in items.OfType<JsonObject>())
        {
            var file = Read(source, node, projectId);
            if (file is null) continue;
            if (best is null || file.Published > best.Published || (file.Published == best.Published && string.Compare(file.Id, best.Id, StringComparison.Ordinal) > 0))
                best = file;
        }
        return best;
    }

    public static bool IsNewer(string installedVersion, string installedFileId, PlatformFile latest)
    {
        if (!string.IsNullOrWhiteSpace(installedFileId) && string.Equals(installedFileId, latest.Id, StringComparison.OrdinalIgnoreCase))
            return false;
        var installed = Primary(installedVersion);
        if (installed is null) return false;
        var tokens = Tokens(latest.Version + " " + latest.Name);
        if (tokens.Any(token => string.Equals(token, installed, StringComparison.OrdinalIgnoreCase))) return false;
        var candidate = tokens.OrderByDescending(token => SharedPrefix(installed, token)).ThenByDescending(token => Parts(token).Length).FirstOrDefault();
        if (candidate is null || SharedPrefix(installed, candidate) == 0) return false;
        if (LooksLikeMinecraft(candidate) && !LooksLikeMinecraft(installed) && SharedPrefix(installed, candidate) < 2) return false;
        var left = Parts(installed);
        var right = Parts(candidate);
        if (LooksLikeMinecraft(installed) && LooksLikeMinecraft(candidate) && SharedPrefix(installed, candidate) < 2 &&
             left.Length >= 2 && right.Length >= 2 && Math.Abs(left[1] - right[1]) > 1)
            return false;
        return Compare(candidate, installed) > 0;
    }

    public static string Notice(string source, string installed, PlatformFile latest)
    {
        var label = source == "modrinth" ? "Modrinth" : "CurseForge";
        var remote = latest.Version.Length > 0 ? latest.Version : latest.Name;
        if (remote.Length > 80) remote = remote[..80].Trim();
        var current = installed.Trim();
        if (current.Length > 80) current = current[..80].Trim();
        return $"{label} 有新的正式版 {remote}（当前 {current}）";
    }
 
     private static PlatformFile? Read(string source, JsonObject node, string projectId)
     {
        if (source == "curseforge")
        {
            if (Text(node, "modId") != projectId || Text(node, "releaseType") != "1") return null;
            var name = Text(node, "displayName", Text(node, "fileName"));
            if (name.Length == 0 || !DateTimeOffset.TryParse(Text(node, "fileDate"), out var published)) return null;
            return new PlatformFile(source, projectId, Text(node, "id"), name, Primary(name) ?? name, published);
        }
        if (!string.Equals(Text(node, "project_id"), projectId, StringComparison.OrdinalIgnoreCase) || Text(node, "version_type") != "release")
            return null;
        var version = Text(node, "version_number");
        var title = Text(node, "name", version);
        if ((version.Length == 0 && title.Length == 0) || !DateTimeOffset.TryParse(Text(node, "date_published"), out var released)) return null;
        return new PlatformFile(source, projectId, Text(node, "id"), title, version.Length > 0 ? version : title, released);
    }

    private static string? Primary(string text)
    {
        var tokens = Tokens(text);
        if (tokens.Length == 0) return null;
        var specific = tokens.Where(token => !LooksLikeMinecraft(token)).ToArray();
        var pool = specific.Length > 0 ? specific : tokens;
        return pool.OrderByDescending(token => Parts(token).Length).ThenByDescending(token => token.Length).First();
    }

    private static string[] Tokens(string text) =>
        Regex.Matches(text ?? "", @"(?<![0-9])\d+(?:\.\d+){1,4}(?![0-9])")
            .Select(match => match.Value)
            .Where(token => token.Length <= 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool LooksLikeMinecraft(string token)
    {
        var parts = Parts(token);
        if (parts.Length is < 2 or > 3) return false;
        if (parts[0] == 1 && parts[1] is >= 0 and <= 26) return true;
        return parts[0] is >= 20 and <= 40 && parts[1] <= 20;
    }

    private static int[] Parts(string token) =>
        Regex.Matches(token, @"\d+").Select(match => int.TryParse(match.Value, out var number) ? number : 0).ToArray();

    private static int SharedPrefix(string left, string right)
    {
        var a = Parts(left);
        var b = Parts(right);
        var count = 0;
        while (count < a.Length && count < b.Length && a[count] == b[count]) count++;
        return count;
    }

    private static int Compare(string left, string right)
    {
        var a = Parts(left);
        var b = Parts(right);
        var count = Math.Max(a.Length, b.Length);
        for (var i = 0; i < count; i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    private static string Text(JsonObject node, string key, string fallback = "")
    {
        if (node[key] is not JsonValue value) return fallback;
        if (value.TryGetValue<string>(out var text) && text.Trim().Length > 0) return text.Trim();
        if (value.TryGetValue<int>(out var number)) return number.ToString();
        if (value.TryGetValue<long>(out var wide)) return wide.ToString();
        return fallback;
    }
}
