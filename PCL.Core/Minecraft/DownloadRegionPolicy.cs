using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.IO.Net.Dns;
using PCL.Core.IO.Net.Http;
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
/// 例外：全局出口为 CN 或未知、但检测到 Minecraft 下载流量被代理/加速器
/// 单独分流（fake-ip、分流路由、系统代理覆盖该域名等）时放行并记日志。
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

    internal const string MinecraftHost = "piston-data.mojang.com";
    private static readonly IPAddress RouteAnchorV4 = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress RouteAnchorV6 = IPAddress.Parse("2606:4700:4700::1111");

    /// <summary>返回 Minecraft 下载流量被代理/加速器分流的原因；未分流返回 null。测试可替换。</summary>
    internal static Func<CancellationToken, Task<string?>> DetectMinecraftDivertAsync = DetectMinecraftDivertCoreAsync;

    private static async Task<string?> DetectMinecraftDivertCoreAsync(CancellationToken token)
    {
        // 系统代理对该域名生效
        var uri = new Uri($"https://{MinecraftHost}/");
        if (!HttpProxyManager.Instance.IsBypassed(uri) &&
            HttpProxyManager.Instance.GetProxy(uri) is { } proxy &&
            !proxy.Equals(uri))
        {
            return $"经代理 {proxy.Authority}";
        }

        // 与真实下载用同一种解析方式（DoH 开关与 NetworkService 一致）
        IPAddress[]? addresses;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(ProviderTimeout);
            try
            {
                addresses = Config.Network.EnableDoH
                    ? await DnsQuery.Instance.QueryForIpAsync(MinecraftHost, timeout.Token).ConfigureAwait(false)
                    : await System.Net.Dns.GetHostAddressesAsync(MinecraftHost, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                LogWrapper.Debug("Region", $"{MinecraftHost} 解析失败：{ex.Message}");
                return null;
            }
        }
        if (addresses is null) return null;

        foreach (var ip in addresses)
        {
            if (IsFakeIp(ip))
                return $"{MinecraftHost} 解析到 fake-ip {ip}";
            var anchor = ip.AddressFamily == AddressFamily.InterNetworkV6 ? RouteAnchorV6 : RouteAnchorV4;
            var source = GetRouteSource(ip);
            var anchorSource = GetRouteSource(anchor);
            // 与常规流量的出接口不同，说明该域名被单独分流
            if (source is not null && anchorSource is not null && !source.Equals(anchorSource))
                return $"{MinecraftHost}（{ip}）经 {source} 出站，常规流量经 {anchorSource}";
        }
        return null;
    }

    /// <summary>是否落在 fake-ip 段 198.18.0.0/15（Clash TUN 等）。</summary>
    internal static bool IsFakeIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 198 && (bytes[1] & 0xFE) == 18;
    }

    /// <summary>UDP Connect 触发路由选择，取本地出接口地址；无路由返回 null。</summary>
    internal static IPAddress? GetRouteSource(IPAddress destination)
    {
        try
        {
            using var socket = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(destination, 443);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch (Exception)
        {
            return null;
        }
    }

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
        // 分流结果不缓存：加速器可能随时开关
        var divert = await DetectMinecraftDivertAsync(token).ConfigureAwait(false);
        if (divert is not null)
        {
            LogWrapper.Info("Region", $"常规出口地区 {country ?? "未知"}，但 Minecraft 流量已分流（{divert}），按加速器放行");
            return;
        }
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
