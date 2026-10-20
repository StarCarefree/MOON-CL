using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft;

/// <summary>用户可见的地区下载限制错误。</summary>
public sealed class DownloadRegionBlockedException : Exception
{
    public DownloadRegionBlockedException(string message) : base(message) { }
}

/// <summary>
/// 按出口 IP 所在地区限制 Minecraft 本体下载：中国大陆（CN）拦截，
/// 无法确认地区时失败关闭同样拦截；已安装的实例不受影响。
/// </summary>
public static class DownloadRegionPolicy
{
    private const int BodyLimit = 16 * 1024;
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    internal static readonly (string Url, Func<string, string?> Parse)[] Providers =
    {
        ("https://www.cloudflare.com/cdn-cgi/trace", ParseCloudflareTrace),
        ("https://api.ip.sb/geoip", ParseIpSb),
        ("https://ipinfo.io/country", ParseIpinfo),
    };

    internal static string? ParseCloudflareTrace(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("loc=", StringComparison.Ordinal)) continue;
            var code = trimmed[4..].Trim();
            return code.Length == 2 ? code.ToUpperInvariant() : null;
        }
        return null;
    }

    internal static string? ParseIpSb(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.TryGetProperty("country_code", out var value) &&
                       value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return code is { Length: 2 } ? code.ToUpperInvariant() : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string? ParseIpinfo(string body)
    {
        var code = body.Trim();
        return Regex.IsMatch(code, @"\A[A-Za-z]{2}\z") ? code.ToUpperInvariant() : null;
    }

    public static async Task<string?> DetectCountryAsync(HttpClient http, CancellationToken token)
    {
        foreach (var (url, parse) in Providers)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ProviderTimeout);
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var body = await ReadCappedAsync(response, token).ConfigureAwait(false);
                var country = parse(body);
                if (country is null) continue;
                LogWrapper.Info("Region", $"出口地区 {country}（{new Uri(url).Host}）");
                return country;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                LogWrapper.Debug("Region", $"地区探测失败（{new Uri(url).Host}）：{ex.Message}");
            }
        }
        LogWrapper.Info("Region", "所有出口地区探测源均失败");
        return null;
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[BodyLimit];
        var total = 0;
        int read;
        while (total < BodyLimit && (read = await stream.ReadAsync(buffer.AsMemory(total, BodyLimit - total), token).ConfigureAwait(false)) > 0)
            total += read;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    public static bool IsBlocked(string? country) =>
        country is null || country.Equals("CN", StringComparison.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim LookupLock = new(1, 1);
    private static (string Country, DateTimeOffset Expires)? _cache;

    internal static void ResetCache()
    {
        LookupLock.Wait();
        try { _cache = null; }
        finally { LookupLock.Release(); }
    }

    /// <summary>拦截地区或无法确认地区时抛出 DownloadRegionBlockedException。</summary>
    public static async Task EnsureAllowedAsync(HttpClient http, CancellationToken token)
    {
        string? country;
        await LookupLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cache is { } cached && cached.Expires > DateTimeOffset.UtcNow)
                country = cached.Country;
            else
            {
                country = await DetectCountryAsync(http, token).ConfigureAwait(false);
                if (country is not null)
                    _cache = (country, DateTimeOffset.UtcNow + CacheLifetime);
            }
        }
        finally { LookupLock.Release(); }

        if (!IsBlocked(country)) return;
        LogWrapper.Warn("Region", $"阻止 Minecraft 本体下载（出口地区 {country ?? "未知"}）");
        throw new DownloadRegionBlockedException(country is null
            ? "无法确认当前网络所在地区，暂时不能下载 Minecraft 本体。请检查网络连接后重试。"
            : "根据中国大陆相关规定，当前网络出口位于中国大陆，PCL-MOON 不提供 Minecraft 本体下载。已安装的版本仍可正常启动。");
    }

    /// <summary>同步包装，供加载器任务等阻塞调用点使用。</summary>
    public static void EnsureAllowed(HttpClient http) =>
        Task.Run(() => EnsureAllowedAsync(http, CancellationToken.None)).GetAwaiter().GetResult();

    /// <summary>异常链中是否包含地区拦截错误。</summary>
    public static bool IsRegionBlock(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
            if (ex is DownloadRegionBlockedException) return true;
        return false;
    }

    /// <summary>返回异常链中地区拦截错误的用户可读信息；不存在则返回 null。</summary>
    public static string? RegionBlockMessage(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
            if (ex is DownloadRegionBlockedException) return ex.Message;
        return null;
    }
}
