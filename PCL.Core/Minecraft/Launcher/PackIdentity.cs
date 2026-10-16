using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

public sealed record PackClues
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Summary { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string FileName { get; init; } = "";
    public string CurseForgeId { get; init; } = "";
    public string ModrinthId { get; init; } = "";
    public string McmodId { get; init; } = "";
    public string Query => FirstUseful(Name, Stem(FileName));
    public static string Stem(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Trim();
        name = Regex.Replace(name, @"[-_ ]v?\d+(?:\.\d+){1,3}.*$", "", RegexOptions.IgnoreCase).Trim(' ', '-', '_');
        return name.Length >= 2 ? name : Path.GetFileNameWithoutExtension(fileName).Trim();
    }
    private static string FirstUseful(string preferred, string fallback) =>
        preferred.Trim().Length >= 2 ? preferred.Trim() : fallback.Trim();
}

public sealed record PackCandidate
{
    public string Source { get; init; } = "";
    public string Name { get; init; } = "";
    public string EnglishName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Website { get; init; } = "";
    public string Version { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Slug { get; init; } = "";
    public string CurseForgeId { get; init; } = "";
    public string ModrinthId { get; init; } = "";
    public string McmodId { get; init; } = "";
}

public sealed record PackIdentity
{
    public string Source { get; init; } = "local";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Website { get; init; } = "";
    public string Version { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string CurseForgeId { get; init; } = "";
    public string ModrinthId { get; init; } = "";
    public string McmodId { get; init; } = "";
    public string SourceLabel => Source switch
    {
        "mcmod" => "MC百科",
        "curseforge" => "CurseForge",
        "modrinth" => "Modrinth",
        _ => "本地整合包"
    };
    public string SidebarLine
    {
        get
        {
            var text = FirstLine(Description);
            if (text.Length == 0) text = Name;
            if (text.Length > 72) text = text[..72].Trim() + "…";
            return text.Length == 0 ? SourceLabel : SourceLabel + " · " + text;
        }
    }
    public string HeroText
    {
        get
        {
            var text = Regex.Replace(Description.Trim(), @"[ \t]+", " ");
            if (text.Length > 420) text = text[..420].Trim() + "…";
            return text.Length == 0 ? SourceLabel : SourceLabel + "\n" + text;
        }
    }
    private static string FirstLine(string value)
    {
        var line = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return Regex.Replace(line, @"\s+", " ").Trim();
    }
}

public static class PackCluesReader
{
    public static PackClues? ReadInstalled(string folder)
    {
        foreach (var name in new[] { "modrinth.index.json", "manifest.json", "mcbbs.packmeta", "modpack.json" })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path) || new FileInfo(path).Length > 1_048_576) continue;
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (json is null) continue;
                return new PackClues
                {
                    Name = Text(json, "name"), Version = Text(json, "version", Text(json, "versionId")),
                    MinecraftVersion = Minecraft(Text(json["dependencies"] as JsonObject, "minecraft", Text(json["minecraft"] as JsonObject, "version")))
                };
            }
            catch (JsonException) { }
        }
        return null;
    }

    public static PackClues Read(string archivePath, string? projectId = null, string? projectSource = null)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Read);
        return Read(archive, Path.GetFileName(archivePath), projectId, projectSource);
    }

    public static PackClues Read(ZipArchive archive, string fileName, string? projectId = null, string? projectSource = null)
    {
        var clues = new PackClues { FileName = fileName ?? "" };
        var manifest = Find(archive, "manifest.json");
        var modrinth = Find(archive, "modrinth.index.json");
        var mcbbs = Find(archive, "mcbbs.packmeta");
        var hmcl = Find(archive, "modpack.json");
        JsonObject? json = null;
        var kind = "";
        if (modrinth is not null) { json = Parse(modrinth); kind = "modrinth"; }
        else if (manifest is not null) { json = Parse(manifest); kind = json?["addons"] is null ? "curseforge" : "mcbbs"; }
        else if (mcbbs is not null) { json = Parse(mcbbs); kind = "mcbbs"; }
        else if (hmcl is not null) { json = Parse(hmcl); kind = "hmcl"; }
        if (json is null) return WithIds(clues, projectId, projectSource, kind);
        var name = Text(json, "name");
        var version = Text(json, "version", Text(json, "versionId"));
        var summary = Text(json, "summary", Text(json, "description"));
        var minecraft = Text(json["minecraft"] as JsonObject, "version");
        if (minecraft.Length == 0) minecraft = Text(json["dependencies"] as JsonObject, "minecraft");
        if (minecraft.Length == 0 && json["addons"] is JsonArray addons)
            minecraft = addons.OfType<JsonObject>().Where(a => Text(a, "id") == "game").Select(a => Text(a, "version")).FirstOrDefault() ?? "";
        return WithIds(clues with { Name = Clean(name), Version = Clean(version), Summary = Clean(summary), MinecraftVersion = Minecraft(minecraft) }, projectId, projectSource, kind);
    }

    private static PackClues WithIds(PackClues clues, string? projectId, string? projectSource, string kind)
    {
        var id = (projectId ?? "").Trim();
        var source = (projectSource ?? "").Trim().ToLowerInvariant();
        if (source.Length == 0) source = kind;
        if (id.Length == 0) return clues;
        if (source is "curseforge" or "curse") return clues with { CurseForgeId = id };
        if (source is "modrinth") return clues with { ModrinthId = id };
        if (source is "mcmod") return clues with { McmodId = id };
        return int.TryParse(id, out _) ? clues with { CurseForgeId = id } : clues with { ModrinthId = id };
    }

    private static ZipArchiveEntry? Find(ZipArchive archive, string name) =>
        archive.GetEntry(name) ?? archive.Entries.FirstOrDefault(entry =>
        {
            var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && parts[1].Equals(name, StringComparison.OrdinalIgnoreCase);
        });

    private static JsonObject? Parse(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var buffer = new char[1024];
        var text = new StringBuilder();
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > 1_048_576) throw new InvalidDataException("整合包索引过大。");
        }
        return JsonNode.Parse(text.ToString()) as JsonObject;
    }

    private static string Text(JsonObject? node, string key, string fallback = "") =>
        node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : fallback;
    private static string Clean(string value) => value.Length > 18000 ? value[..18000] : value.Trim();
    private static string Minecraft(string value) => Regex.IsMatch(value, @"^\d{1,2}\.\d{1,2}(?:\.\d{1,2})?$") ? value : "";
}

public static class PackIdentityResolver
{
    public const int AcceptScore = 72;
    public static PackIdentity Choose(PackClues clues, IEnumerable<PackCandidate>? mcmod = null, IEnumerable<PackCandidate>? curseforge = null, IEnumerable<PackCandidate>? modrinth = null)
    {
        var mc = Best(clues, mcmod);
        var cf = Best(clues, curseforge);
        var mr = Best(clues, modrinth);
        var winner = mc.Score >= AcceptScore ? mc : cf.Score >= AcceptScore ? cf : mr.Score >= AcceptScore ? mr : default;
        var source = winner.Item is null ? "local" : winner.Item.Source is "mcmod" or "curseforge" or "modrinth" ? winner.Item.Source : "local";
        var description = winner.Item?.Description ?? "";
        if (description.Length == 0) description = FirstText(cf.Item?.Description, mr.Item?.Description, clues.Summary);
        if (description.Length == 0) description = "未在 MC百科、CurseForge 或 Modrinth 找到匹配介绍。";
        var ids = new[] { mc.Item, cf.Item, mr.Item }.Where(item => item is not null && Score(clues, item) >= AcceptScore).Cast<PackCandidate>().ToArray();
        return new PackIdentity
        {
            Source = source,
            Name = FirstText(winner.Item?.Name, clues.Name, PackClues.Stem(clues.FileName)),
            Description = description.Length > 18000 ? description[..18000] : description,
            Website = SafeUrl(winner.Item?.Website ?? ""),
            Version = clues.Version,
            MinecraftVersion = clues.MinecraftVersion,
            CurseForgeId = FirstText(clues.CurseForgeId, ids.Select(i => i.CurseForgeId).FirstOrDefault(s => s.Length > 0)),
            ModrinthId = FirstText(clues.ModrinthId, ids.Select(i => i.ModrinthId).FirstOrDefault(s => s.Length > 0)),
            McmodId = FirstText(clues.McmodId, ids.Select(i => i.McmodId).FirstOrDefault(s => s.Length > 0))
        };
    }

    // Project metadata describes available releases, never the release installed on disk.
    public static PackClues InstalledClues(PackIdentity? existing, string instanceName, string actualMinecraft, PackClues? manifest = null)
    {
        const string loaderMinecraft = @"(?<![\d.])(?<mc>1\.\d+(?:\.\d+)?)(?=\s*[-_ ]*(?:neoforge|fabric|forge|quilt))";
        var namedMinecraft = Regex.Match(instanceName, loaderMinecraft, RegexOptions.IgnoreCase);
        var minecraft = Regex.IsMatch(actualMinecraft, @"^\d+\.\d+(?:\.\d+)?$") ? actualMinecraft
            : FirstText(manifest?.MinecraftVersion, namedMinecraft.Groups["mc"].Value, existing?.MinecraftVersion);
        var cachedMinecraft = Regex.Match(existing?.Version ?? "", loaderMinecraft, RegexOptions.IgnoreCase).Groups["mc"].Value;
        var stale = minecraft.Length > 0 &&
            (existing?.MinecraftVersion.Length > 0 && !MinecraftMatches(existing.MinecraftVersion, minecraft) || cachedMinecraft.Length > 0 && cachedMinecraft != minecraft);
        return new PackClues
        {
            Name = FirstText(manifest?.Name, existing?.Name, PackClues.Stem(instanceName)), FileName = instanceName,
            Version = FirstText(manifest?.Version, stale ? "" : existing?.Version), MinecraftVersion = minecraft,
            CurseForgeId = existing?.CurseForgeId ?? "", ModrinthId = existing?.ModrinthId ?? "", McmodId = existing?.McmodId ?? ""
        };
    }

    public static bool MinecraftMatches(string supported, string minecraft) =>
        Regex.Matches(supported, @"(?<![\d.])\d+\.\d+(?:\.\d+)?(?![\d.])").Any(m => m.Value == minecraft);

    public static PackIdentity Local(PackClues clues) => Choose(clues);
    public static int Score(PackClues clues, PackCandidate candidate)
    {
        if (Id(clues.CurseForgeId, candidate.CurseForgeId) || Id(clues.ModrinthId, candidate.ModrinthId) || Id(clues.McmodId, candidate.McmodId)) return 200;
        var names = new[] { candidate.Name, candidate.EnglishName, candidate.Slug.Replace('-', ' ') };
        var score = names.Select(name => NameScore(clues.Name, name)).Concat(names.Select(name => NameScore(PackClues.Stem(clues.FileName), name))).DefaultIfEmpty(0).Max();
        if (clues.MinecraftVersion.Length > 0 && candidate.MinecraftVersion.Length > 0 &&
            !MinecraftMatches(candidate.MinecraftVersion, clues.MinecraftVersion)) score -= 40;
        return Math.Max(0, score);
    }

    public static int NameScore(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length < 2 || b.Length < 2) return 0;
        if (a == b) return 100;
        var shorter = a.Length <= b.Length ? a : b;
        var longer = a.Length <= b.Length ? b : a;
        if (longer.Contains(shorter, StringComparison.Ordinal) && shorter.Length >= 4 && shorter.Length / (double)longer.Length >= 0.72) return 82;
        var leftTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0) return 0;
        var shared = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (int)Math.Round(70.0 * shared / union);
    }

    public static string Normalize(string value)
    {
        var text = value.ToLowerInvariant();
        text = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)", " ");
        text = Regex.Replace(text, @"\d+\.\d+(?:\.\d+)*", " ");
        text = Regex.Replace(text, @"[^a-z0-9\u4e00-\u9fff]+", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static (PackCandidate? Item, int Score) Best(PackClues clues, IEnumerable<PackCandidate>? items)
    {
        PackCandidate? best = null;
        var score = 0;
        foreach (var item in items ?? [])
        {
            var next = Score(clues, item);
            if (next > score) { score = next; best = item; }
        }
        return (best, score);
    }
    private static bool Id(string left, string right) => left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string FirstText(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    private static string SafeUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : "";
}

public static class PackIdentityStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly Dictionary<string, (DateTime Write, PackIdentity? Value)> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static string IdentityPath(string versionFolder) => Path.Combine(versionFolder.TrimEnd('\\', '/'), "PCL", "pack-identity.json");
    public static void Save(string versionFolder, PackIdentity identity)
    {
        var path = IdentityPath(versionFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(identity, Options));
        File.Move(temp, path, true);
        lock (Cache) Cache.Remove(path);
    }
    public static PackIdentity? TryRead(string versionFolder)
    {
        var path = IdentityPath(versionFolder);
        if (!File.Exists(path)) return null;
        var write = File.GetLastWriteTimeUtc(path);
        lock (Cache)
            if (Cache.TryGetValue(path, out var cached) && cached.Write == write) return cached.Value;
        PackIdentity? value = null;
        try { value = JsonSerializer.Deserialize<PackIdentity>(File.ReadAllText(path), Options); }
        catch (JsonException) { value = null; }
        if (value is not null && value.Source is not ("mcmod" or "curseforge" or "modrinth" or "local")) value = value with { Source = "local" };
        lock (Cache)
        {
            Cache[path] = (write, value);
            if (Cache.Count > 256) Cache.Clear();
        }
        return value;
    }
}
