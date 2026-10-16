using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>Ordered directory mirrors, never merged catalogs or archive download mirrors.</summary>
public static class PackCatalogSources
{
    public const int MaxSources = 8;

    /// <summary>Shipped fallback used when the user configured no source: the repository-hosted catalog.
    /// The jsDelivr mirror is listed second so a blocked GitHub raw host still resolves.</summary>
    public static readonly string[] DefaultUrls =
    [
        "https://raw.githubusercontent.com/catmoon233/MOON-CL/dev/docs/pack-catalog/index.json",
        "https://cdn.jsdelivr.net/gh/catmoon233/MOON-CL@dev/docs/pack-catalog/index.json"
    ];

    // null means legacy settings; an explicit empty list clears the configuration.
    public static string[] Normalize(IEnumerable<string>? sources, string legacyUrl = "")
    {
        sources ??= string.IsNullOrWhiteSpace(legacyUrl) ? [] : [legacyUrl];
        var result = new List<string>();
        foreach (var value in sources)
        {
            var url = value?.Trim() ?? "";
            if (url.Length == 0) continue;
            if (url.Length > 2048) throw new InvalidDataException("目录源地址不能超过 2048 字符。");
            Uri uri;
            try { uri = PackCatalog.HttpUri(url); }
            catch (UriFormatException ex) { throw new InvalidDataException("目录源必须是完整 HTTP(S) 地址。", ex); }
            if (uri.Host.Length == 0 || uri.Fragment.Length > 0) throw new InvalidDataException("目录源不能包含片段或缺少主机。");
            if (uri.AbsoluteUri.Length > 2048) throw new InvalidDataException("编码后的目录源地址不能超过 2048 字符。");
            if (result.Contains(uri.AbsoluteUri, StringComparer.Ordinal)) continue;
            result.Add(uri.AbsoluteUri);
            if (result.Count > MaxSources) throw new InvalidDataException("最多配置 8 个整合包目录源。");
        }
        return result.ToArray();
    }

    /// <summary>Configured sources, or the shipped default when the user cleared them.</summary>
    public static string[] Effective(string[]? configured) => configured is { Length: > 0 } ? configured : DefaultUrls;
}

public sealed record PackCatalogResult(string SourceUrl, IReadOnlyList<PackDefinition> Packs, IReadOnlyList<ServerDefinition> Servers, bool NotModified);

/// <summary>Single-operation owner. Bounded metadata and cancellation; caches only validated snapshots.</summary>
public sealed class PackCatalogClient
{
    private sealed record Cached(string? ETag, IReadOnlyList<PackDefinition> Packs, IReadOnlyList<ServerDefinition> Servers);
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _sourceTimeout;
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.Ordinal);

    public PackCatalogClient(HttpClient http, TimeSpan? sourceTimeout = null)
    {
        _http = http;
        _sourceTimeout = sourceTimeout ?? TimeSpan.FromSeconds(8);
        if (_sourceTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sourceTimeout));
    }

    public async Task<PackCatalogResult> FetchAsync(IEnumerable<string> sources, CancellationToken token,
        Action<int, int, string>? attempting = null)
    {
        var urls = PackCatalogSources.Normalize(sources);
        if (urls.Length == 0) throw new InvalidDataException("请先配置整合包目录 index.json 地址。");
        foreach (var removed in _cache.Keys.Where(url => !urls.Contains(url, StringComparer.Ordinal)).ToArray()) _cache.Remove(removed);
        var errors = new List<Exception>();
        for (var index = 0; index < urls.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var url = urls[index];
            attempting?.Invoke(index + 1, urls.Length, url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_sourceTimeout);
            try
            {
                _cache.TryGetValue(url, out var cached);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                if (cached?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (cached is null) throw new InvalidDataException("服务返回 304，但本机没有可复用目录。");
                    return new PackCatalogResult(url, cached.Packs, cached.Servers, true);
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("目录元数据超过 4 MiB。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[32768];
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > MaxBytes) throw new InvalidDataException("目录元数据超过 4 MiB。");
                    output.Write(buffer, 0, read);
                }
                timeout.Token.ThrowIfCancellationRequested();
                // Redirected directory URLs are the base for relative pack/image links.
                var source = response.RequestMessage?.RequestUri ?? new Uri(url);
                var document = PackCatalog.ParseDocument(Encoding.UTF8.GetString(output.ToArray()), source);
                var packs = document.Packs.ToArray();
                var servers = document.Servers.ToArray();
                _cache[url] = new Cached(response.Headers.ETag?.ToString(), Array.AsReadOnly(packs), Array.AsReadOnly(servers));
                return new PackCatalogResult(url, _cache[url].Packs, _cache[url].Servers, false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                errors.Add(new TimeoutException($"目录源 {index + 1} 超时。"));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                errors.Add(ex);
            }
        }
        token.ThrowIfCancellationRequested();
        throw new InvalidDataException($"全部 {urls.Length} 个目录源均不可用；已保留上次成功目录。请检查 index.json 地址、网络及 packs 数组格式。", new AggregateException(errors));
    }
}
