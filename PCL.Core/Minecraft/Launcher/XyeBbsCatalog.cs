using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>
/// Reads an XyeBBS project download page and keeps only Quark shares the official resource API returns.
/// The browser never supplies the share URL.
/// </summary>
public sealed class XyeBbsCatalog(HttpClient http)
{
    public const int MaxJsonChars = 1024 * 1024;
    public const int MaxPages = 8;
    public const int MaxVersions = 64;
    private static readonly Regex IdentifyPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PagePattern = new("^/res-id/([A-Za-z0-9_-]{1,64})(?:/(?:downloads|releases))?/?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, XyeBbsListing Value)> _cache = new(StringComparer.Ordinal);
    public static bool TryParse(string? url, out string identify)
    {
        identify = "";
        if (string.IsNullOrWhiteSpace(url) || url.Length > 512 || url.Any(char.IsControl)) return false;
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var pathAt = scheme < 0 ? -1 : url.IndexOf('/', scheme + 3);
        if (pathAt < 0) return false;
        var rawPath = url[pathAt..];
        var cut = rawPath.IndexOfAny(['?', '#']);
        if (cut >= 0) rawPath = rawPath[..cut];
        if (rawPath.Split('/').Any(part => part is "." or "..")) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0 || uri.Query.Length > 0) return false;
        if (!uri.Host.Equals("www.xyebbs.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("xyebbs.com", StringComparison.OrdinalIgnoreCase)) return false;
        var match = PagePattern.Match(uri.AbsolutePath);
        if (!match.Success || !IdentifyPattern.IsMatch(match.Groups[1].Value)) return false;
        identify = match.Groups[1].Value;
        return true;
    }

    public static string Canonical(string identify)
    {
        if (!IdentifyPattern.IsMatch(identify ?? "")) throw new ArgumentException("无效的 XyeBBS 资源标识。");
        return "https://www.xyebbs.com/res-id/" + identify + "/downloads#resource-download-section";
    }

    /// <summary>Returns the canonical download page only when the requested project is already on the encyclopedia detail.</summary>
    public static string? MatchPage(IEnumerable<string>? links, string? requested)
    {
        if (!TryParse(requested, out var identify) || links is null) return null;
        foreach (var link in links)
            if (TryParse(link, out var other) && other == identify) return Canonical(identify);
        return null;
    }

    public static Uri IdentityAddress(string identify)
    {
        if (!IdentifyPattern.IsMatch(identify ?? "")) throw new ArgumentException("无效的 XyeBBS 资源标识。");
        return new Uri("https://resource-api.xyeidc.com/client/resources/identify/" + identify);
    }

    public static Uri ReleasesAddress(int resourceId, int page)
    {
        if (resourceId is < 1 or > 10_000_000 || page is < 1 or > MaxPages) throw new ArgumentOutOfRangeException(nameof(page));
        return new Uri($"https://resource-api.xyeidc.com/client/resources/{resourceId}/releases?page={page}&includes=versions%3Bcores%3Blinks");
    }

    public static int ParseIdentity(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var status))
            throw new InvalidDataException("XyeBBS 下载信息无法识别，请改为跳转。");
        if (status == 404) throw new InvalidDataException("XyeBBS 上没有这个资源，请改为跳转。");
        if (status != 200 || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("XyeBBS 下载信息无法识别，请改为跳转。");
        if (!data.TryGetProperty("id", out var idNode) || !idNode.TryGetInt32(out var id) || id is < 1 or > 10_000_000)
            throw new InvalidDataException("XyeBBS 下载信息无法识别，请改为跳转。");
        if (!data.TryGetProperty("resourceTypeId", out var typeNode) || !typeNode.TryGetInt32(out var typeId) || typeId != 2)
            throw new InvalidDataException("这个 XyeBBS 页面不是整合包，请改为跳转。");
        return id;
    }

    public static IReadOnlyList<XyeBbsVersion> ParseReleases(string json, out int totalPages)
    {
        totalPages = 1;
        using var document = Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var status) || status != 200 ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("XyeBBS 下载信息无法识别，请改为跳转。");
        if (data.TryGetProperty("totalPages", out var pages) && pages.TryGetInt32(out var parsed) && parsed > 0)
            totalPages = Math.Min(parsed, 500);
        var versions = new List<XyeBbsVersion>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array) continue;
            foreach (var link in links.EnumerateArray())
            {
                if (link.ValueKind != JsonValueKind.Object) continue;
                var kind = Text(link, "linkType");
                var share = Text(link, "url").Trim();
                if (!kind.Equals("QUARK", StringComparison.OrdinalIgnoreCase) || !QuarkClientLaunch.IsShareUrl(share)) continue;
                var label = Clean(Text(item, "label"), 80);
                versions.Add(new XyeBbsVersion(label.Length == 0 ? "未命名版本" : label, FirstName(item, "versions"), FirstName(item, "cores"), share));
                break;
            }
        }
        return versions;
    }

    public async Task<XyeBbsListing> ListAsync(string pageUrl, CancellationToken token = default)
    {
        if (!TryParse(pageUrl, out var identify)) throw new InvalidDataException("不是有效的 XyeBBS 下载页。");
        var canonical = Canonical(identify);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(canonical, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(15))
                return cached.Value;
            var resourceId = ParseIdentity(await GetAsync(IdentityAddress(identify), token).ConfigureAwait(false));
            var versions = new List<XyeBbsVersion>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var truncated = false;
            var page = 1;
            var totalPages = 1;
            do
            {
                var batch = ParseReleases(await GetAsync(ReleasesAddress(resourceId, page), token).ConfigureAwait(false), out totalPages);
                foreach (var version in batch)
                {
                    var shareId = version.ShareId;
                    if (string.IsNullOrEmpty(shareId) || !seen.Add(shareId)) continue;
                    if (versions.Count >= MaxVersions) { truncated = true; break; }
                    versions.Add(version);
                }
                page++;
            }
            while (!truncated && page <= totalPages && page <= MaxPages);
            if (totalPages > MaxPages) truncated = true;
            var listing = new XyeBbsListing(canonical, truncated, versions);
            if (_cache.Count >= 32) _cache.Remove(_cache.MinBy(pair => pair.Value.At).Key);
            _cache[canonical] = (DateTimeOffset.UtcNow, listing);
            return listing;
        }
        finally { _gate.Release(); }
    }

    private static JsonDocument Parse(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxJsonChars) throw new InvalidDataException("XyeBBS 响应超过大小上限。");
        try { return JsonDocument.Parse(json, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("XyeBBS 下载信息无法识别，请改为跳转。", ex); }
    }

    private static string FirstName(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) return "";
        foreach (var entry in list.EnumerateArray())
        {
            var name = Clean(Text(entry, "name"), 32);
            if (name.Length > 0) return name;
        }
        return "";
    }

    private static string Text(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Clean(string value, int limit)
    {
        if (value.Length == 0) return "";
        var text = Regex.Replace(new string(value.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray()), @"\s+", " ").Trim();
        return text.Length > limit ? text[..limit] : text;
    }

    private async Task<string> GetAsync(Uri address, CancellationToken token)
    {
        if (!address.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || address.UserInfo.Length > 0 ||
            !address.Host.Equals("resource-api.xyeidc.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("XyeBBS 接口地址无效。");
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", "PCL-MOON/1.0 (+https://www.xyebbs.com/)");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.Referrer = new Uri("https://www.xyebbs.com/");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("XyeBBS 返回了意外跳转，没有读取下载页。", null, response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("XyeBBS 下载页暂时无法读取，请改为跳转。", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxJsonChars) throw new InvalidDataException("XyeBBS 响应超过大小上限。");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxJsonChars) throw new InvalidDataException("XyeBBS 响应超过大小上限。");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

public sealed class XyeBbsVersion
{
    public string Label { get; }
    public string Minecraft { get; }
    public string Loader { get; }
    public string ShareUrl { get; }
    public string? ShareId => QuarkClientLaunch.ShareId(ShareUrl);

    public XyeBbsVersion(string label, string minecraft, string loader, string shareUrl)
    {
        Label = label;
        Minecraft = minecraft;
        Loader = loader;
        ShareUrl = shareUrl;
    }

    public override string ToString() => Label;
}

public sealed class XyeBbsListing
{
    public string PageUrl { get; }
    public bool Truncated { get; }
    public IReadOnlyList<XyeBbsVersion> Versions { get; }

    public XyeBbsListing(string pageUrl, bool truncated, IReadOnlyList<XyeBbsVersion> versions)
    {
        PageUrl = pageUrl;
        Truncated = truncated;
        Versions = versions;
    }

    public override string ToString() => PageUrl;
}
