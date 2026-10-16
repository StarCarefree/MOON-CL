using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

public sealed record BrowseCategory(string Source, string ResourceType, string Id, string Name);

/// <summary>Pure category address and response parser. Names come from the provider payload, never a local taxonomy.</summary>
public static class BrowseCategories
{
    private static readonly Regex CfId = new("^[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MrSlug = new("^[a-zA-Z0-9_+.-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void ValidateSelection(string source, string category)
    {
        if (category.Length == 0) return;
        PlatformPackCatalog.ValidateSource(source);
        if (source == "curseforge" ? !CfId.IsMatch(category) : !MrSlug.IsMatch(category))
            throw new ArgumentException(source == "curseforge" ? "无效的 CurseForge 分类。" : "无效的 Modrinth 分类。");
    }

    public static Uri Address(string source, string type)
    {
        PlatformPackCatalog.ValidateSource(source);
        BrowseResourceTypes.RequireSupported(source, type);
        return source == "curseforge"
            ? new Uri($"https://api.curseforge.com/v1/categories?gameId=432&classId={BrowseResourceTypes.CurseForgeClass(type)}")
            : new Uri("https://api.modrinth.com/v2/tag/category");
    }

    public static IReadOnlyList<BrowseCategory> Parse(string source, string type, string json)
    {
        PlatformPackCatalog.ValidateSource(source);
        BrowseResourceTypes.RequireSupported(source, type);
        if (json.Length is < 2 or > PlatformPackCatalog.MaxJsonChars) throw new InvalidDataException("分类响应超过大小上限。");
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException ex) { throw new InvalidDataException("分类响应不是有效的 JSON。", ex); }
        var expected = BrowseResourceTypes.CurseForgeClass(type);
        var items = source == "curseforge" ? ParseCurseForge(root, type, expected) : ParseModrinth(root, type);
        return items.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).Take(512).ToArray();
    }

    private static List<BrowseCategory> ParseCurseForge(JsonNode? root, string type, int expectedClass)
    {
        if (root is not JsonObject document || document["data"] is not JsonArray data)
            throw new InvalidDataException("CurseForge 分类响应缺少 data 数组。");
        var items = new List<BrowseCategory>();
        foreach (var node in data)
        {
            if (node is not JsonObject item) throw new InvalidDataException("CurseForge 分类条目无效。");
            if (!TryInt(item["classId"], out var classId) || classId != expectedClass) continue;
            if (item["isClass"] is JsonValue flag && flag.TryGetValue<bool>(out var isClass) && isClass) continue;
            if (!TryInt(item["id"], out var id) || !CfId.IsMatch(id.ToString())) throw new InvalidDataException("分类 ID 无效。");
            if (id == expectedClass || id == classId) continue;
            items.Add(new BrowseCategory("curseforge", type, id.ToString(), NameOf(item, "name")));
        }
        return items;
    }

    private static List<BrowseCategory> ParseModrinth(JsonNode? root, string type)
    {
        if (root is not JsonArray data) throw new InvalidDataException("Modrinth 分类响应不是数组。");
        var exact = CollectModrinth(data, type, type);
        if (type == "datapack" && exact.Count == 0) exact = CollectModrinth(data, type, "mod");
        return exact;
    }

    private static List<BrowseCategory> CollectModrinth(JsonArray data, string requestedType, string projectType)
    {
        var items = new List<BrowseCategory>();
        foreach (var node in data)
        {
            if (node is not JsonObject item) throw new InvalidDataException("Modrinth 分类条目无效。");
            if (!string.Equals(PlatformPackCatalog.Str(item, "project_type"), projectType, StringComparison.Ordinal)) continue;
            if (string.Equals(PlatformPackCatalog.Str(item, "header"), "loaders", StringComparison.OrdinalIgnoreCase)) continue;
            var slug = PlatformPackCatalog.Str(item, "name");
            if (!MrSlug.IsMatch(slug)) throw new InvalidDataException("分类 ID 无效。");
            items.Add(new BrowseCategory("modrinth", requestedType, slug, NameOf(item, "name")));
        }
        return items;
    }

    private static string NameOf(JsonObject item, string key)
    {
        var raw = PlatformPackCatalog.Str(item, key);
        if (raw.Length is < 1 or > 128 || raw.Any(char.IsControl)) throw new InvalidDataException("分类名称无效。");
        var text = PlatformPackCatalog.PlainText(raw, 128);
        if (text.Length is < 1 or > 128 || text.Any(char.IsControl)) throw new InvalidDataException("分类名称无效。");
        return text;
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<int>(out value)) return true;
        if (v.TryGetValue<long>(out var wide) && wide is >= int.MinValue and <= int.MaxValue) { value = (int)wide; return true; }
        return v.TryGetValue<string>(out var text) && int.TryParse(text, out value);
    }
}
