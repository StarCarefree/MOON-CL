using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace PCL.Core.Minecraft.Launcher;

public sealed record McmodLink(string Name, string Url);
public sealed record McmodPack
{
    public string Id => ResourceType == "datapack" ? "mcmod-datapack-" + WikiId : "mcmod-" + WikiId;
    public int WikiId { get; init; }
    public string ResourceType { get; init; } = "modpack";
    public string Source => "mcmod";
    public string Name { get; init; } = "";
    public string EnglishName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Website => $"https://www.mcmod.cn/{(ResourceType == "datapack" ? "class" : "modpack")}/{WikiId}.html";
    public string[] MinecraftVersions { get; init; } = [];
    public string[] Loaders { get; init; } = [];
    public string[] Authors { get; init; } = [];
    public string[] Categories { get; init; } = [];
    // MC support versions are not pack release versions. The encyclopedia does not always publish the latter.
    public string Version { get; init; } = "";
    public string CurseForgeUrl { get; init; } = "";
    public string ModrinthUrl { get; init; } = "";
    public McmodLink[] Links { get; init; } = [];
    public int Views { get; init; }
}
public sealed record McmodCategory(string Id, string Name);
public sealed record McmodPage(int Page, int TotalPages, IReadOnlyList<McmodPack> Items)
{
    public IReadOnlyList<McmodCategory> Categories { get; init; } = [];
}

/// <summary>Read-only adapter. Fetches only the public list/detail, never scripts or remote download redirects.</summary>
public sealed class McmodCatalog(HttpClient http)
{
    public const int MaxHtmlBytes = 4 * 1024 * 1024;
    private static readonly Uri Origin = new("https://www.mcmod.cn/");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, object Value)> _cache = new();
    private DateTimeOffset _nextRequestAt;
    private DateTimeOffset _cooldownUntil;

    public async Task<McmodPage> GetPageAsync(int page, CancellationToken token = default, string minecraft = "", string loader = "", int category = 0, string resourceType = "modpack", string sort = "")
    {
        if (page is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(page));
        var path = ListPath(page, minecraft, loader, category, resourceType, sort);
        return (McmodPage)await CachedAsync("page:" + resourceType + ":" + path, path,
            html => ParsePage(html, page, resourceType), token).ConfigureAwait(false);
    }

    public static string ListPath(int page, string minecraft = "", string loader = "", int category = 0, string resourceType = "modpack", string sort = "")
    {
        if (page is < 1 or > 500 || category < 0 || category > (resourceType == "datapack" ? 1000 : 15)) throw new ArgumentOutOfRangeException(nameof(page));
        BrowseResourceTypes.RequireSupported("mcmod", resourceType);
        if (minecraft.Length > 0 && !Regex.IsMatch(minecraft, @"^\d{1,2}\.\d{1,2}(?:\.\d{1,2})?$"))
            throw new ArgumentException("无效的 Minecraft 版本筛选。");
        // Official list only offers default, 收录时间 and 最后编辑. Views exist on detail pages, not as a list sort.
        var order = sort switch { "" or "default" => "", "createtime" or "newest" => "createtime", "lastedittime" or "updated" => "lastedittime", _ => null };
        if (order is null) throw new ArgumentException("百科不支持该排序。");
        var sortQuery = order.Length > 0 ? "&sort=" + order : "";
        if (resourceType == "datapack")
        {
            if (loader.Length > 0 && loader != "数据包") throw new ArgumentException("数据包不使用模组加载器筛选。");
            return $"modlist.html?api=5&page={page}" + (minecraft.Length > 0 ? "&mcver=" + Uri.EscapeDataString(minecraft) : "") + (category > 0 ? "&category=" + category : "") + sortQuery;
        }
        var api = loader.ToLowerInvariant() switch { "" => 0, "forge" => 1, "fabric" => 2, "quilt" => 3, "neoforge" => 7, "liteloader" => 5, "rift" => 6, "数据包" => 4, "行为包" => 8, _ => -1 };
        if (api < 0) throw new ArgumentException("无效的加载器筛选。");
        return $"modpack.html?page={page}" + (minecraft.Length > 0 ? "&mcver=" + Uri.EscapeDataString(minecraft) : "") +
            (api > 0 ? "&api=" + api : "") + (category > 0 ? "&category=" + category : "") + sortQuery;
    }

    public async Task<IReadOnlyList<McmodCategory>> GetCategoriesAsync(string resourceType = "modpack", CancellationToken token = default)
        => (await GetPageAsync(1, token, resourceType: resourceType).ConfigureAwait(false)).Categories;

    public async Task<McmodPack> GetDetailAsync(int id, CancellationToken token = default, string resourceType = "modpack")
    {
        if (id is < 1 or > 10000000) throw new ArgumentOutOfRangeException(nameof(id));
        BrowseResourceTypes.RequireSupported("mcmod", resourceType);
        return (McmodPack)await CachedAsync("detail:" + resourceType + ":" + id, $"{(resourceType == "datapack" ? "class" : "modpack")}/{id}.html",
            html => ParseDetail(html, id, resourceType), token).ConfigureAwait(false);
    }

    public static Uri SearchAddress(string query)
    {
        var text = query.Trim();
        if (text.Length is < 1 or > 80 || text.Any(char.IsControl)) throw new ArgumentException("无效的百科搜索词。");
        return new Uri("https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(text));
    }

    public async Task<IReadOnlyList<McmodPack>> SearchModpacksAsync(string query, CancellationToken token = default)
    {
        var address = SearchAddress(query);
        return (IReadOnlyList<McmodPack>)await CachedAsync("search:" + address.AbsoluteUri, address.AbsoluteUri, ParseSearch, token).ConfigureAwait(false);
    }

    public static IReadOnlyList<McmodPack> ParseSearch(string html)
    {
        var root = Document(html);
        var nodes = ByClass(root, "result-item").ToArray();
        if (nodes.Length == 0)
            nodes = root.Descendants("a").Where(a => ModpackId(a) > 0).Select(a => a.ParentNode).Where(n => n is not null).Distinct().Cast<HtmlNode>().ToArray();
        var items = new List<McmodPack>();
        foreach (var node in nodes)
        {
            var anchor = node.Descendants("a").FirstOrDefault(a => ModpackId(a) > 0);
            if (anchor is null) continue;
            var id = ModpackId(anchor);
            var name = Text(anchor);
            if (name.Length == 0 || items.Any(p => p.WikiId == id)) continue;
            var body = ByClass(node, "body").FirstOrDefault();
            var desc = Text(body);
            if (desc == name) desc = "";
            items.Add(new McmodPack { WikiId = id, ResourceType = "modpack", Name = name, Description = desc });
            if (items.Count >= 12) break;
        }
        if (items.Count == 0 && !html.Contains("没有找到", StringComparison.Ordinal) && !html.Contains("search-result", StringComparison.Ordinal) && !html.Contains("result-item", StringComparison.Ordinal))
            throw new InvalidDataException("未识别到百科搜索结果，页面可能暂不可用。");
        return items;
    }

    private static int ModpackId(HtmlNode anchor)
    {
        var url = HttpUrl(anchor.GetAttributeValue("href", ""));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !(uri.Host.Equals("www.mcmod.cn", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("mcmod.cn", StringComparison.OrdinalIgnoreCase))) return 0;
        var match = Regex.Match(uri.AbsolutePath, @"^/modpack/(\d+)\.html$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) && id > 0 ? id : 0;
    }

    private async Task<object> CachedAsync(string key, string path, Func<string, object> parse, CancellationToken token)
    {
        // Serialize/coalesce identical requests and keep site load bounded even on rapid clicks.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(15))
                return cached.Value;
            if (_cooldownUntil > DateTimeOffset.UtcNow)
                throw new HttpRequestException("MC百科暂时限流，请稍后再试；已有缓存仍可浏览。", null, HttpStatusCode.TooManyRequests);
            var wait = _nextRequestAt - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, timeout.Token).ConfigureAwait(false);
            _nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(1.5);
            var address = key.StartsWith("search:", StringComparison.Ordinal) ? new Uri(key[7..]) : new Uri(Origin, path);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("MOONCL/1.0 (+https://www.mcmod.cn/modpack.html)");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(1);
                _cooldownUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(retry.TotalSeconds, 60, 300));
                throw new HttpRequestException("MC百科暂时限流，请稍后再试；已有缓存仍可浏览。", null, HttpStatusCode.TooManyRequests);
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxHtmlBytes) throw new InvalidDataException("百科页面超过大小上限。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + count > MaxHtmlBytes) throw new InvalidDataException("百科页面超过大小上限。");
                memory.Write(buffer, 0, count);
            }
            var value = parse(Encoding.UTF8.GetString(memory.ToArray()));
            if (_cache.Count >= 128) _cache.Remove(_cache.MinBy(p => p.Value.At).Key);
            _cache[key] = (DateTimeOffset.UtcNow, value);
            return value;
        }
        finally { _gate.Release(); }
    }

    public static McmodPage ParsePage(string html, int page = 1, string resourceType = "modpack")
    {
        BrowseResourceTypes.RequireSupported("mcmod", resourceType);
        var root = Document(html);
        var packs = new List<McmodPack>();
        foreach (var card in ByClass(root, "modlist-block"))
        {
            var nameNode = ByClass(card, "name").FirstOrDefault();
            var anchor = nameNode?.Descendants("a").FirstOrDefault();
            var match = Regex.Match(anchor?.GetAttributeValue("href", "") ?? "", resourceType == "datapack" ? @"^/class/(\d+)\.html$" : @"^/modpack/(\d+)\.html$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var id)) continue;
            var name = Text(anchor);
            if (name.Length == 0 || packs.Any(p => p.WikiId == id)) continue;
            packs.Add(new McmodPack
            {
                WikiId = id, ResourceType = resourceType, Name = name, EnglishName = Text(ByClass(card, "ename").FirstOrDefault()),
                Description = Text(ByClass(card, "intro-content").FirstOrDefault()),
                Icon = HttpUrl(card.Descendants("img").FirstOrDefault()?.GetAttributeValue("src", "") ?? "")
            });
        }
        if (packs.Count == 0 && !ByClass(root, "modlist-list-frame").Any()) throw new InvalidDataException("未识别到百科整合包列表，页面可能暂不可用或结构已变化。请在浏览器中查看百科。");
        var pages = ByClass(root, "common-pages").SelectMany(n => n.Descendants("a"))
            .Select(n => int.TryParse(n.GetAttributeValue("data-page", ""), out var p) ? p : 1).DefaultIfEmpty(1).Max();
        return new McmodPage(page, Math.Clamp(pages, page, 500), packs) { Categories = ParseCategories(root, resourceType) };
    }

    private static IReadOnlyList<McmodCategory> ParseCategories(HtmlNode root, string resourceType)
    {
        var path = resourceType == "datapack" ? "modlist" : "modpack";
        return ByClass(root, "modlist-filter-block").Where(n => HasClass(n, "category")).SelectMany(n => n.Descendants("a"))
            .Select(a => new { Node = a, Match = Regex.Match(WebUtility.HtmlDecode(a.GetAttributeValue("onclick", "") + " " + a.GetAttributeValue("href", "")), @"/" + path + @"\.html\?[^'\""\s]*\bcategory=(\d+)(?:[&'\""\s]|$)") })
            .Where(x => x.Match.Success && CategoryName(x.Node).Length > 0)
            .Select(x => new McmodCategory(x.Match.Groups[1].Value, CategoryName(x.Node)))
            .GroupBy(x => x.Id).Select(g => g.First()).Take(512).ToArray();
    }

    private static string CategoryName(HtmlNode node)
    {
        var name = Text(node);
        if (name.Length == 0) name = WebUtility.HtmlDecode(node.GetAttributeValue("title", node.ParentNode?.GetAttributeValue("title", "") ?? "")).Trim();
        return name.Length > 120 ? name[..120] : name;
    }

    public static McmodPack ParseDetail(string html, int id, string resourceType = "modpack")
    {
        BrowseResourceTypes.RequireSupported("mcmod", resourceType);
        var root = Document(html);
        var title = ByClass(root, "class-title").FirstOrDefault();
        var name = Text(title?.Descendants("h3").FirstOrDefault());
        if (name.Length == 0) throw new InvalidDataException("未识别到百科整合包详情，请稍后重试或查看原页面。");
        var info = ByClass(root, "class-info-left").FirstOrDefault();
        if (resourceType == "datapack" && !(info?.Descendants("a").Any(n => Regex.IsMatch(n.GetAttributeValue("href", ""), @"^/modlist\.html\?[^#]*\bapi=5(?:&|$)")) ?? false))
            throw new InvalidDataException("百科页面未标记为数据包，不能显示在数据包列表中。");
        var links = new List<McmodLink>();
        foreach (var frame in ByClass(info, "common-link-frame"))
        foreach (var a in frame.Descendants("a"))
        {
            var url = DecodeLink(a.GetAttributeValue("href", ""));
            if (url.Length == 0 || links.Any(l => l.Url == url)) continue;
            var label = Text(a.ParentNode?.Descendants("span").FirstOrDefault(n => HasClass(n, "name")));
            if (label.Length == 0) label = a.GetAttributeValue("data-original-title", new Uri(url).Host);
            links.Add(new McmodLink(label, url));
        }
        var versions = ByClass(info, "mcver").SelectMany(n => n.Descendants("a"))
            .Where(n => resourceType != "datapack" || Regex.IsMatch(n.GetAttributeValue("href", ""), @"[?&]api=5(?:&|$)"))
            .Select(Text).Where(s => Regex.IsMatch(s, @"^\d+\.\d+(?:\.\d+)?(?:[-\w.]+)?$"));
        var loaders = info?.Descendants("a").Where(n => Regex.IsMatch(n.GetAttributeValue("href", ""), @"[?&]api=\d+"))
            .Select(Text).Where(s => !Regex.IsMatch(s, @"^\d")) ?? [];
        var authors = ByClass(info, "author").SelectMany(n => ByClass(n, "member"))
            .SelectMany(n => ByClass(n, "name")).Select(Text);
        var desc = ByClass(root, "text-area").FirstOrDefault(n => n.GetAttributeValue("data-id", "") == "1");
        var views = 0;
        foreach (var span in ByClass(root, "infos").SelectMany(n => ByClass(n, "span")))
        {
            if (Text(ByClass(span, "t").FirstOrDefault()) != "总浏览") continue;
            var raw = span.GetAttributeValue("title", "");
            if (int.TryParse(raw, out var exact) && exact > 0) views = exact;
            break;
        }
        return new McmodPack
        {
            WikiId = id, ResourceType = resourceType, Name = string.Join(" ", new[] { Text(ByClass(title, "short-name").FirstOrDefault()), name }.Where(s => s.Length > 0)),
            EnglishName = Text(title?.Descendants("h4").FirstOrDefault()),
            Description = PlainText(desc, 18000),
            Icon = HttpUrl(ByClass(root, "class-cover-image").FirstOrDefault()?.Descendants("img").FirstOrDefault()?.GetAttributeValue("src", "") ?? ""),
            MinecraftVersions = Unique(versions), Loaders = resourceType == "datapack" ? ["数据包"] : Unique(loaders), Authors = Unique(authors),
            Categories = Unique(ByClass(root, "class-category").SelectMany(n => n.Descendants("a")).Select(Text)),
            CurseForgeUrl = links.Select(l => l.Url).FirstOrDefault(url => IsCurseForgeResource(url, resourceType)) ?? "",
            ModrinthUrl = links.Select(l => l.Url).FirstOrDefault(url => IsModrinthResource(url, resourceType)) ?? "", Links = links.Take(32).ToArray(),
            Views = views
        };
    }

    public static bool IsCurseForgeModpack(string url) => IsCurseForgeResource(url, "modpack");
    public static bool IsCurseForgeResource(string url, string resourceType = "modpack")
    {
        if (resourceType is not ("modpack" or "datapack")) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Host.Equals("www.curseforge.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("curseforge.com", StringComparison.OrdinalIgnoreCase))) return false;
        var path = resourceType == "datapack" ? @"^/minecraft/(?:data-packs|datapacks)/[a-zA-Z0-9_-]+(?:/.*)?$" : @"^/minecraft/modpacks/[a-zA-Z0-9_-]+(?:/.*)?$";
        return Regex.IsMatch(uri.AbsolutePath, path);
    }

    public static bool IsModrinthResource(string url, string resourceType = "modpack")
    {
        if (resourceType is not ("modpack" or "datapack")) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Host.Equals("modrinth.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.modrinth.com", StringComparison.OrdinalIgnoreCase))) return false;
        var prefix = resourceType == "datapack" ? "(?:datapack|mod)" : "modpack";
        return Regex.IsMatch(uri.AbsolutePath, "^/(?:" + prefix + @"/[a-zA-Z0-9_-]+|project/[A-Za-z0-9]{8})(?:/.*)?$");
    }

    public static string DecodeLink(string value)
    {
        var url = HttpUrl(value);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        if (!uri.Host.Equals("link.mcmod.cn", StringComparison.OrdinalIgnoreCase)) return url;
        if (!uri.AbsolutePath.StartsWith("/target/", StringComparison.Ordinal)) return "";
        try
        {
            var encoded = Uri.UnescapeDataString(uri.AbsolutePath[8..]).Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var target = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return Uri.TryCreate(target, UriKind.Absolute, out _) ? HttpUrl(target) : "";
        }
        catch (FormatException) { return ""; }
    }

    private static HtmlNode Document(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes) throw new InvalidDataException("百科页面超过大小上限。");
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        foreach (var node in doc.DocumentNode.Descendants().Where(n => n.Name is "script" or "style" or "noscript").ToArray()) node.Remove();
        return doc.DocumentNode;
    }
    private static string HttpUrl(string value)
    {
        value = WebUtility.HtmlDecode(value).Trim();
        // Never turn a dangerous scheme into a relative URL.
        if (value.Length == 0 || value.Length > 8192 || value.Any(char.IsControl)) return "";
        if (!Uri.TryCreate(Origin, value, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return "";
        return uri.AbsoluteUri;
    }
    private static bool HasClass(HtmlNode node, string name) => node.GetAttributeValue("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(name);
    private static IEnumerable<HtmlNode> ByClass(HtmlNode? node, string name) => node?.Descendants().Where(n => HasClass(n, name)) ?? [];
    private static string[] Unique(IEnumerable<string> values) => values.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
    private static string Text(HtmlNode? node) => PlainText(node, 2048);
    private static string PlainText(HtmlNode? node, int limit)
    {
        if (node is null) return "";
        var parts = new StringBuilder();
        foreach (var child in node.DescendantsAndSelf())
        {
            if (child.NodeType == HtmlNodeType.Text) parts.Append(WebUtility.HtmlDecode(child.InnerText));
            else if (child.Name is "p" or "br" or "li" or "h3" or "h4" or "div") parts.Append(limit > 2048 ? '\n' : ' ');
        }
        var result = (limit > 2048 ? Regex.Replace(Regex.Replace(parts.ToString(), @"[^\S\n]+", " "), @" *\n[ \n]*", "\n\n") : Regex.Replace(parts.ToString(), @"\s+", " ")).Trim();
        return result.Length > limit ? result[..limit] : result;
    }
}
