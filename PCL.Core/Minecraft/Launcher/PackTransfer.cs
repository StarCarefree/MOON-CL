using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Launcher;

public sealed class PackTransfer
{
    private readonly HttpClient _http;
    public PackTransfer(HttpClient http) => _http = http;

    public async Task<string> ReadTextAsync(Uri uri, CancellationToken token, int maxBytes = 4 * 1024 * 1024) =>
        Encoding.UTF8.GetString(await ReadBytesAsync(uri, token, maxBytes).ConfigureAwait(false));

    public async Task<byte[]> ReadBytesAsync(Uri uri, CancellationToken token, int maxBytes = 4 * 1024 * 1024)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("服务端元数据过大。");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32768];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maxBytes) throw new InvalidDataException("服务端元数据过大。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public async Task DownloadAsync(string url, string target, string expectedHash, HashAlgorithmName algorithm,
        IProgress<double>? progress, CancellationToken token, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PackCatalog.HttpUri(url));
        if (headers is not null)
            foreach (var (key, value) in headers)
                if (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(key, value);
         using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
         if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
             throw new HttpRequestException("夸克 CDN 拒绝了匿名直链（HTTP 412）。该文件超过 PC 访客通道大小限制，不能直接下载。请登录夸克客户端后手动导入，或配置受信解析服务。", null, response.StatusCode);
         response.EnsureSuccessStatusCode();
        var partial = target + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            using (var hash = IncrementalHash.CreateHash(algorithm))
            {
                var total = response.Content.Headers.ContentLength;
                var buffer = new byte[131072];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    progress?.Report(total > 0 ? Math.Min(.999, (double)received / total.Value) : 0);
                }
                if (total.HasValue && total.Value != received) throw new IOException("下载未完成。");
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (expectedHash.Length > 0 && !string.Equals(expectedHash, actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("文件哈希校验失败，已丢弃下载内容。");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, target, true);
            progress?.Report(1);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
