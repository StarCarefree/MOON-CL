using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>Official per-version mappings, content-addressed and hash checked. No authentication data.</summary>
public static class GameLinkMappings
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public static async Task<string?> PrepareAsync(string version, string cache, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(cache);
        var index = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version))) + ".ref");
        if (File.Exists(index))
        {
            var cachedHash = (await File.ReadAllTextAsync(index, cancellationToken).ConfigureAwait(false)).Trim();
            if (cachedHash.Length == 40 && cachedHash.All(Uri.IsHexDigit))
            {
                var cachedPath = Path.Combine(cache, cachedHash + ".txt");
                if (File.Exists(cachedPath) && Convert.ToHexString(SHA1.HashData(await File.ReadAllBytesAsync(cachedPath, cancellationToken).ConfigureAwait(false))).Equals(cachedHash, StringComparison.OrdinalIgnoreCase))
                    return cachedPath;
            }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var manifest = JsonNode.Parse(await GetAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", 8 * 1024 * 1024, token).ConfigureAwait(false));
        var entry = manifest?["versions"]?.AsArray().FirstOrDefault(v => (string?)v?["id"] == version);
        if (entry == null) return null;
        var metadataBytes = await GetAsync((string)entry["url"]!, 8 * 1024 * 1024, token).ConfigureAwait(false);
        if ((string?)entry["sha1"] is { } metadataHash) Verify(metadataBytes, metadataHash);
        var metadata = JsonNode.Parse(metadataBytes);
        var mappings = metadata?["downloads"]?["client_mappings"];
        if (mappings == null) return null;
        var hash = (string)mappings["sha1"]!;
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new IOException("Invalid mappings hash");
        Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, hash + ".txt");
        if (File.Exists(path))
        {
            var cached = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (Convert.ToHexString(SHA1.HashData(cached)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(index, hash, token).ConfigureAwait(false);
                return path;
            }
        }
        var bytes = await GetAsync((string)mappings["url"]!, 32 * 1024 * 1024, token).ConfigureAwait(false);
        Verify(bytes, hash);
        var temporary = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        await File.WriteAllTextAsync(index, hash, token).ConfigureAwait(false);
        return path;
    }

    public static async Task<string> PrepareRuntimeAsync(string version, string kind, string cache, CancellationToken cancellationToken = default)
    {
        if (kind is not ("fabric" or "forge")) throw new ArgumentException("Unknown loader mappings", nameof(kind));
        Directory.CreateDirectory(cache);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + version)));
        var index = Path.Combine(cache, key + ".runtime.ref");
        if (File.Exists(index))
        {
            var hash = (await File.ReadAllTextAsync(index, cancellationToken).ConfigureAwait(false)).Trim();
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit))
            {
                var candidate = Path.Combine(cache, hash + ".runtime");
                if (File.Exists(candidate) && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(candidate, cancellationToken).ConfigureAwait(false))).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var id = Uri.EscapeDataString(version);
        var url = kind == "fabric"
            ? $"https://maven.fabricmc.net/net/fabricmc/intermediary/{id}/intermediary-{id}-v2.jar"
            : $"https://maven.minecraftforge.net/de/oceanlabs/mcp/mcp_config/{id}/mcp_config-{id}.zip";
        var checksum = Encoding.UTF8.GetString(await GetAsync(url + ".sha1", 1024, token).ConfigureAwait(false)).Trim().Split(' ', '\t', '\r', '\n')[0];
        if (checksum.Length != 40 || !checksum.All(Uri.IsHexDigit)) throw new IOException("Invalid loader mappings checksum");
        var archive = await GetAsync(url, 32 * 1024 * 1024, token).ConfigureAwait(false);
        Verify(archive, checksum);
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        var entry = zip.GetEntry(kind == "fabric" ? "mappings/mappings.tiny" : "config/joined.tsrg")
            ?? throw new IOException("Loader mapping entry is missing");
        if (entry.Length > 32 * 1024 * 1024) throw new IOException("Loader mappings too large");
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > 32 * 1024 * 1024) throw new IOException("Loader mappings too large");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = Path.Combine(cache, contentHash + ".runtime");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
            await File.WriteAllTextAsync(index, contentHash, token).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    private static void Verify(byte[] bytes, string hash)
    {
        if (!Convert.ToHexString(SHA1.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Mojang mappings checksum mismatch");
    }

    private static async Task<byte[]> GetAsync(string url, int limit, CancellationToken token)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !new[] { "piston-meta.mojang.com", "piston-data.mojang.com", "launchermeta.mojang.com", "launcher.mojang.com", "maven.fabricmc.net", "maven.minecraftforge.net" }.Contains(uri.Host))
            throw new IOException("Untrusted mappings URL");
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Mappings response too large");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > limit) throw new IOException("Mappings response too large");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
