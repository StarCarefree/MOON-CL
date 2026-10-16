using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Server;

/// <summary>
/// Bounded metadata and JAR transfer. No TLS bypass, no secrets, and no process launch.
/// </summary>
public static class ServerCoreTransfer
{
    public const int MaxJsonBytes = 6 * 1024 * 1024;
    public const long MaxJarBytes = 256L * 1024 * 1024;

    public static async Task<string> GetTextAsync(HttpClient http, string url, CancellationToken token, int maxBytes = MaxJsonBytes, string? userAgent = null)
    {
        ServerCoreCatalog.RequireHttpsAllowlisted(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(userAgent)) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("元数据超过大小上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count; long total = 0;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            token.ThrowIfCancellationRequested();
            total += count;
            if (total > maxBytes) throw new IOException("元数据超过大小上限。");
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static async Task DownloadVerifiedAsync(HttpClient http, IReadOnlyList<string> urls, string destination, string algorithm, string expectedHash, long? expectedSize, Action<string>? log, CancellationToken token)
    {
        if (urls.Count == 0) throw new IOException("没有可用的下载地址。");
        if (expectedSize > MaxJarBytes) throw new IOException("服务端 JAR 超过大小上限。");
        Exception? last = null;
        foreach (var url in urls)
        {
            token.ThrowIfCancellationRequested();
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ServerCoreCatalog.RequireHttpsAllowlisted(url);
                log?.Invoke("下载核心：" + new Uri(url).Host);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", ServerCoreCatalog.UserAgent);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxJarBytes) throw new IOException("服务端 JAR 超过大小上限。");
                if (expectedSize is > 0 && response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength != expectedSize)
                    throw new IOException("下载大小与元数据不一致。");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
                await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var output = File.Create(temporary))
                {
                    var buffer = new byte[81920];
                    long total = 0; int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        total += count;
                        if (total > MaxJarBytes || (expectedSize is > 0 && total > expectedSize)) throw new IOException("服务端 JAR 超过大小上限或元数据大小。");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                    if (expectedSize is > 0 && total != expectedSize) throw new IOException("下载大小与元数据不一致。");
                }
                var actual = ComputeHash(temporary, algorithm);
                if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"校验失败，已丢弃下载（{algorithm}）。");
                ValidateJar(temporary);
                File.Move(temporary, destination, true);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
            {
                last = ex;
                log?.Invoke("此下载源失败，尝试下一个来源：" + ex.Message);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        throw new IOException("无法下载并校验服务端 JAR。", last);
    }

    public static void CopyValidatedJar(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(source)) throw new FileNotFoundException("未找到本地服务端 JAR。", source);
        var info = new FileInfo(source);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不能从符号链接导入服务端 JAR。");
        if (info.Length <= 0 || info.Length > MaxJarBytes) throw new IOException("本地 JAR 大小无效或超过上限。");
        var fullSource = Path.GetFullPath(source);
        var fullDestination = Path.GetFullPath(destination);
        if (fullSource.Equals(fullDestination, StringComparison.OrdinalIgnoreCase)) { ValidateJar(fullDestination); return; }
        var temporary = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
            using (var input = File.OpenRead(fullSource))
            using (var output = File.Create(temporary))
            {
                var buffer = new byte[81920];
                int count; long total = 0;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    total += count;
                    if (total > MaxJarBytes) throw new IOException("本地 JAR 超过大小上限。");
                    output.Write(buffer, 0, count);
                }
            }
            ValidateJar(temporary);
            File.Move(temporary, fullDestination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void ValidateJar(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 4 || info.Length > MaxJarBytes) throw new IOException("服务端文件不是有效 JAR。");
        using (var stream = File.OpenRead(path))
        {
            Span<byte> header = stackalloc byte[4];
            if (stream.Read(header) < 4 || header[0] != 0x50 || header[1] != 0x4B) throw new IOException("服务端文件缺少 ZIP/JAR 文件头。");
        }
        using var jar = ZipFile.OpenRead(path);
        if (jar.Entries.Count == 0) throw new IOException("服务端 JAR 为空。");
    }

    public static string ComputeHash(string path, string algorithm)
    {
        using var stream = File.OpenRead(path);
        using HashAlgorithm hash = algorithm.ToLowerInvariant() switch
        {
            "sha1" => SHA1.Create(),
            "sha256" => SHA256.Create(),
            "md5" => MD5.Create(),
            _ => throw new ArgumentException("不支持的校验算法。")
        };
        return Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
    }
}
