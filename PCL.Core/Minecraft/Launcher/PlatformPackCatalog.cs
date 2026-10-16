using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace PCL.Core.Minecraft.Launcher;

public sealed record PlatformPack
{
    public string Id => Source + "-" + ProjectId;
    public string Source { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Slug { get; init; } = "";
    public string ResourceType { get; init; } = "modpack";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Website { get; init; } = "";
    public string[] MinecraftVersions { get; init; } = [];
    public string[] Loaders { get; init; } = [];
    public string[] Authors { get; init; } = [];
    public string[] Categories { get; init; } = [];
    public string Version { get; init; } = "";
}
public sealed record PlatformPackPage(string Source, int Page, int TotalPages, int TotalItems, IReadOnlyList<PlatformPack> Items);

/// <summary>Pure platform request/response contract. Fetching uses the launcher's established auth/mirror policy.</summary>
public static class PlatformPackCatalog
{
    public const int PageSize = 24;
    public const int MaxPage = 400;
    public const int MaxJsonChars = 4 * 1024 * 1024;
    private static readonly Dictionary<string, (string Name, int Cf)> Loaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forge"] = ("Forge", 1), ["fabric"] = ("Fabric", 4), ["quilt"] = ("Quilt", 5), ["neoforge"] = ("NeoForge", 6)
    };

    public static void ValidateSource(string source)
    {
        if (source is not ("curseforge" or "modrinth")) throw new ArgumentException("未知的整合包来源。");
    }
    public static void ValidateId(string source, string id)
    {
        ValidateSource(source);
        if (source == "curseforge" ? !int.TryParse(id, out var number) || number <= 0 : !Regex.IsMatch(id, "^[a-zA-Z0-9]{8}$"))
            throw new ArgumentException("无效的整合包项目 ID。");
    }
    public static Uri SearchAddress(string source, int page, string query = "", string minecraft = "", string loader = "", string sort = "relevance", string resourceType = "modpack", string category = "")
    {
        ValidateSource(source);
        BrowseResourceTypes.RequireSupported(source, resourceType);
        if (page is < 1 or > MaxPage) throw new ArgumentOutOfRangeException(nameof(page));
        if (query.Length > 200 || query.Any(char.IsControl)) throw new ArgumentException("搜索内容过长或包含控制字符。");
        if (minecraft.Length > 32 || minecraft.Length > 0 && !Regex.IsMatch(minecraft, @"^\d{1,2}\.\d{1,2}(?:\.\d{1,2})?(?:[-a-zA-Z0-9.]+)?$"))
            throw new ArgumentException("无效的 Minecraft 版本筛选。");
        if (loader.Length > 0 && !Loaders.ContainsKey(loader)) throw new ArgumentException("此平台不支持该加载器筛选。");
        if (!BrowseResourceTypes.UsesModLoader(resourceType) && loader.Length > 0) throw new ArgumentException("当前资源类型不使用模组加载器筛选。");
        if (sort is not ("relevance" or "downloads" or "updated" or "newest")) throw new ArgumentException("未知的排序方式。");
        BrowseCategories.ValidateSelection(source, category);
        var offset = (page - 1) * PageSize;
        if (source == "curseforge")
        {
            var sortId = sort switch { "downloads" => 6, "updated" => 3, "newest" => 11, _ => 4 };
            return new Uri($"https://api.curseforge.com/v1/mods/search?gameId=432&classId={BrowseResourceTypes.CurseForgeClass(resourceType)}&pageSize={PageSize}&index={offset}&sortField={sortId}&sortOrder=desc" +
                (query.Length > 0 ? "&searchFilter=" + Uri.EscapeDataString(query) : "") +
                (minecraft.Length > 0 ? "&gameVersion=" + Uri.EscapeDataString(minecraft) : "") +
                (loader.Length > 0 ? "&modLoaderType=" + Loaders[loader].Cf : "") +
                (category.Length > 0 ? "&categoryId=" + category : ""));
        }
        var facets = new List<string[]> { new[] { "project_type:" + resourceType } };
        if (minecraft.Length > 0) facets.Add(["versions:" + minecraft]);
        if (loader.Length > 0) facets.Add(["categories:" + loader.ToLowerInvariant()]);
        if (category.Length > 0) facets.Add(["categories:" + category]);
        return new Uri($"https://api.modrinth.com/v2/search?limit={PageSize}&offset={offset}&index={sort}&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}");
    }
    public static Uri DetailAddress(string source, string id)
    {
        ValidateId(source, id);
        return new Uri(source == "curseforge" ? $"https://api.curseforge.com/v1/mods/{id}" : $"https://api.modrinth.com/v2/project/{id}");
    }
    public static JsonObject ReadJson(string json)
    {
        if (json.Length > MaxJsonChars) throw new InvalidDataException("平台响应超过大小上限。");
        return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject ?? throw new InvalidDataException("平台响应不是 JSON 对象。");
    }
    public static PlatformPackPage ParsePage(string source, string json, int page, string resourceType = "modpack")
    {
        ValidateSource(source);
        BrowseResourceTypes.RequireSupported(source, resourceType);
        if (page is < 1 or > MaxPage) throw new ArgumentOutOfRangeException(nameof(page));
        var root = ReadJson(json);
        var array = root[source == "curseforge" ? "data" : "hits"] as JsonArray ?? throw new InvalidDataException("平台列表缺少条目数组。");
        var totalNode = source == "curseforge" ? root["pagination"]?["totalCount"] : root["total_hits"];
        if (totalNode is not JsonValue totalValue || !totalValue.TryGetValue<int>(out var total) || total < 0)
            throw new InvalidDataException("平台列表缺少分页信息。");
        var items = array.OfType<JsonObject>().Select(item => ParsePack(source, item, resourceType)).GroupBy(p => p.Id).Select(g => g.First()).Take(PageSize).ToArray();
        return new PlatformPackPage(source, page, Math.Clamp((int)Math.Ceiling(total / (double)PageSize), 1, MaxPage), total, items);
    }
    public static PlatformPack ParseDetail(string source, string json, string expectedId, string resourceType = "modpack")
    {
        ValidateId(source, expectedId);
        BrowseResourceTypes.RequireSupported(source, resourceType);
        var root = ReadJson(json);
        var pack = ParsePack(source, source == "curseforge" ? root["data"] as JsonObject ?? throw new InvalidDataException("CurseForge 未返回项目信息。") : root, resourceType);
        if (pack.ProjectId != expectedId) throw new InvalidDataException("平台返回的项目与请求不符。");
        return pack;
    }
    private static PlatformPack ParsePack(string source, JsonObject item, string resourceType)
    {
        var cf = source == "curseforge";
        var actualType = Str(item, "project_type");
        var legacyDatapack = resourceType == "datapack" && actualType == "mod" && Strings(item["categories"]).Concat(Strings(item["loaders"])).Contains("datapack");
        var legacyPlugin = resourceType == "plugin" && actualType == "mod" && Strings(item["categories"]).Concat(Strings(item["loaders"])).Any(s => new[] { "paper", "purpur", "bukkit", "spigot", "folia" }.Contains(s));
        if (cf ? item["classId"]?.ToString() != BrowseResourceTypes.CurseForgeClass(resourceType).ToString() : actualType != resourceType && !legacyDatapack && !legacyPlugin)
            throw new InvalidDataException("平台返回的资源类型与当前列表不符。");
        var id = cf ? Str(item, "id") : Str(item, "project_id", Str(item, "id"));
        ValidateId(source, id);
        var files = Array(item["latestFiles"]).OfType<JsonObject>().ToArray();
        var gameValues = cf ? files.SelectMany(f => Strings(f["gameVersions"])).Concat(Array(item["latestFilesIndexes"]).Select(f => f?["gameVersion"]?.ToString() ?? "")) : Strings(item["game_versions"] ?? item["versions"]);
        var loaderValues = cf ? files.SelectMany(f => Strings(f["gameVersions"])).Concat(Array(item["latestFilesIndexes"]).Select(f => int.TryParse(f?["modLoader"]?.ToString(), out var n) ? Loaders.Values.FirstOrDefault(v => v.Cf == n).Name ?? "" : "")) : Strings(item["loaders"]).Concat(Strings(item["categories"]));
        var authors = cf ? Array(item["authors"]).Select(a => a?["name"]?.ToString() ?? "") : new[] { Str(item, "author") };
        var slug = Str(item, "slug");
        var website = cf ? item["links"]?["websiteUrl"]?.ToString() ?? "" : "https://modrinth.com/" + resourceType + "/" + (slug.Length > 0 ? Uri.EscapeDataString(slug) : id);
        var newest = files.OrderByDescending(f => DateTimeOffset.TryParse(Str(f, "fileDate"), out var date) ? date : DateTimeOffset.MinValue)
            .ThenByDescending(f => long.TryParse(Str(f, "id"), out var value) ? value : 0).FirstOrDefault();
        return new PlatformPack
        {
            Source = source, ProjectId = id, Slug = slug, ResourceType = resourceType, Name = PlainText(Str(item, cf ? "name" : "title"), 512),
            Description = PlainText(Str(item, cf ? "summary" : "body", Str(item, "description")), 18000),
            Icon = SafeUrl(cf ? item["logo"]?["thumbnailUrl"]?.ToString() ?? item["logo"]?["url"]?.ToString() ?? "" : Str(item, "icon_url")),
            Website = SafeUrl(website),
            MinecraftVersions = Unique(gameValues.Where(s => Regex.IsMatch(s, @"^\d{1,2}\.\d{1,2}(?:\.\d{1,2})?(?:[-a-zA-Z0-9.]+)?$"))),
            Loaders = Unique(loaderValues.Select(LoaderName)), Authors = Unique(authors),
            Categories = Unique(cf ? Array(item["categories"]).Select(c => c?["name"]?.ToString() ?? "") : Strings(item["categories"]).Where(s => LoaderName(s).Length == 0)),
            Version = cf && newest is not null ? PlainText(Str(newest, "displayName"), 512) : ""
        };
    }
    internal static IEnumerable<JsonNode?> Array(JsonNode? node) => node is JsonArray array ? array : [];
    internal static IEnumerable<string> Strings(JsonNode? node) => Array(node).OfType<JsonValue>().Select(n => n.TryGetValue<string>(out var s) ? s : "");
    internal static string Str(JsonObject node, string key, string fallback = "") => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : node[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n.ToString() : fallback;
    internal static string[] Unique(IEnumerable<string> values) => values.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(256).ToArray();
    internal static string LoaderName(string value) => Loaders.TryGetValue(value, out var known) ? known.Name : value.ToLowerInvariant() switch
    {
        "iris" => "Iris", "optifine" => "OptiFine", "canvas" => "Canvas", "minecraft" => "Minecraft", "datapack" => "数据包", "paper" => "Paper", "purpur" => "Purpur", "bukkit" => "Bukkit", "spigot" => "Spigot", "folia" => "Folia", _ => ""
    };
    internal static string SafeUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : "";
    internal static string PlainText(string value, int limit)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(value);
        foreach (var script in doc.DocumentNode.Descendants().Where(n => n.Name is "script" or "style" or "noscript").ToArray()) script.Remove();
        var text = WebUtility.HtmlDecode(doc.DocumentNode.InnerText).Trim();
        return text.Length > limit ? text[..limit] : text;
    }
}

/// <summary>Small TTL cache and cancellable queue; no endpoint may be supplied by the browser.</summary>
public sealed class PlatformPackClient(Func<Uri, CancellationToken, Task<string>> fetch)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, object Value)> _cache = new();
    private readonly Dictionary<string, string[]> _authors = new();
    private readonly Dictionary<string, string> _latestVersions = new();

    public async Task<PlatformPackPage> GetPageAsync(string source, int page, string query = "", string minecraft = "", string loader = "", string sort = "relevance", CancellationToken token = default, string resourceType = "modpack", string category = "")
    {
        var address = PlatformPackCatalog.SearchAddress(source, page, query, minecraft, loader, sort, resourceType, category);
        if (category.Length > 0)
        {
            var known = await GetCategoriesAsync(source, resourceType, token).ConfigureAwait(false);
            if (!known.Any(item => item.Id == category)) throw new ArgumentException("所选分类不属于当前资源类型。");
        }
        return (PlatformPackPage)await CachedAsync(address, async ct =>
        {
            var json = await fetch(address, ct).ConfigureAwait(false);
            var result = PlatformPackCatalog.ParsePage(source, json, page, resourceType);
            foreach (var pack in result.Items) if (pack.Authors.Length > 0) _authors[resourceType + ":" + pack.Id] = pack.Authors;
            if (source == "modrinth")
                foreach (var hit in PlatformPackCatalog.Array(PlatformPackCatalog.ReadJson(json)["hits"]).OfType<JsonObject>())
                {
                    var projectId = PlatformPackCatalog.Str(hit, "project_id");
                    var releaseId = PlatformPackCatalog.Str(hit, "latest_version");
                    if (Regex.IsMatch(releaseId, "^[a-zA-Z0-9]{8}$")) _latestVersions[resourceType + ":modrinth-" + projectId] = releaseId;
                }
            if (_authors.Count > 2048) _authors.Clear();
            if (_latestVersions.Count > 2048) _latestVersions.Clear();
            return result;
        }, token, resourceType).ConfigureAwait(false);
    }
    public async Task<PlatformPack> GetDetailAsync(string source, string id, CancellationToken token = default, string resourceType = "modpack")
    {
        BrowseResourceTypes.RequireSupported(source, resourceType);
        var address = PlatformPackCatalog.DetailAddress(source, id);
        return (PlatformPack)await CachedAsync(address, async ct =>
        {
            var json = await fetch(address, ct).ConfigureAwait(false);
            var pack = PlatformPackCatalog.ParseDetail(source, json, id, resourceType);
            if (source == "curseforge")
            {
                try
                {
                    var description = PlatformPackCatalog.ReadJson(await fetch(new Uri(address.AbsoluteUri + "/description"), ct).ConfigureAwait(false));
                    var text = PlatformPackCatalog.PlainText(PlatformPackCatalog.Str(description, "data"), 18000);
                    if (text.Length > 0) pack = pack with { Description = text };
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* Summary remains usable when the optional description is unavailable. */ }
            }
            else
            {
                var root = PlatformPackCatalog.ReadJson(json);
                var version = PlatformPackCatalog.Str(root, "latest_version", _latestVersions.GetValueOrDefault(resourceType + ":" + pack.Id, ""));
                // Project responses don't always contain latest_version; version IDs are never shown as release numbers.
                if (Regex.IsMatch(version, "^[a-zA-Z0-9]{8}$"))
                {
                    try
                    {
                        var release = PlatformPackCatalog.ReadJson(await fetch(new Uri("https://api.modrinth.com/v2/version/" + version), ct).ConfigureAwait(false));
                        if (PlatformPackCatalog.Str(release, "project_id") == id) pack = pack with { Version = PlatformPackCatalog.Str(release, "version_number") };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
                if (pack.Authors.Length == 0 && _authors.TryGetValue(resourceType + ":" + pack.Id, out var known)) pack = pack with { Authors = known };
                if (pack.Authors.Length == 0 && Regex.IsMatch(PlatformPackCatalog.Str(root, "team"), "^[a-zA-Z0-9]{8}$"))
                {
                    try
                    {
                        var raw = await fetch(new Uri("https://api.modrinth.com/v2/team/" + PlatformPackCatalog.Str(root, "team") + "/members"), ct).ConfigureAwait(false);
                        if (raw.Length > PlatformPackCatalog.MaxJsonChars) throw new InvalidDataException("团队响应过大。");
                        var members = JsonNode.Parse(raw) as JsonArray;
                        pack = pack with { Authors = PlatformPackCatalog.Unique(PlatformPackCatalog.Array(members).Select(m => m?["user"]?["username"]?.ToString() ?? "")) };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
            }
            return pack;
        }, token, resourceType).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<BrowseCategory>> GetCategoriesAsync(string source, string resourceType, CancellationToken token = default)
    {
        var address = BrowseCategories.Address(source, resourceType);
        return (IReadOnlyList<BrowseCategory>)await CachedAsync(address, async ct =>
        {
            var json = await fetch(address, ct).ConfigureAwait(false);
            return BrowseCategories.Parse(source, resourceType, json);
        }, token, resourceType).ConfigureAwait(false);
    }
    private async Task<object> CachedAsync(Uri address, Func<CancellationToken, Task<object>> build, CancellationToken token, string resourceType)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            var key = resourceType + ":" + address.AbsoluteUri;
            if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(2)) return cached.Value;
            var value = await build(deadline.Token).ConfigureAwait(false);
            if (_cache.Count >= 64) _cache.Remove(_cache.MinBy(p => p.Value.At).Key);
            _cache[key] = (DateTimeOffset.UtcNow, value);
            return value;
        }
        finally { _gate.Release(); }
    }
}
