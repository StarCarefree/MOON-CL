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

namespace PCL.Core.Minecraft.Server;

public sealed class ServerCoreArtifact
{
    public required ServerLoader Loader { get; init; }
    public required string MinecraftVersion { get; init; }
    public string Build { get; init; } = "";
    public int? JavaMajor { get; init; }
    public required string ChecksumAlgorithm { get; init; }
    public required string Checksum { get; init; }
    public long? Size { get; init; }
    public required IReadOnlyList<string> Urls { get; init; }
    public string MetadataSource { get; init; } = "";
    public string MirrorNotice { get; init; } = "";
}

/// <summary>
/// Resolves Vanilla / Paper / Purpur download metadata. Does not download the JAR and does not launch anything.
/// Vanilla mirrors: BMCLAPI host rewrite of piston-meta/piston-data plus documented /version/{id}/json|server.
/// Paper/Purpur: official metadata, optional validated FastMirror binary URLs with official checksums.
/// </summary>
public static class ServerCoreCatalog
{
    public const string UserAgent = "MOONCL/ServerCore (+https://github.com/catmoon233/MOON-CL)";
    public const string MojangManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string BmclapiManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
     public const string PaperV3 = "https://fill.papermc.io/v3/projects/paper";
     public const string FoliaV3 = "https://fill.papermc.io/v3/projects/folia";
     public const string PurpurV2 = "https://api.purpurmc.org/v2/purpur";
     public const string FabricMeta = "https://meta.fabricmc.net/v2/versions";
     public const string NeoForgeVersions = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
     public const string SpigotJars = "https://getbukkit.org/api/jars";
     public const string SpigotDownload = "https://download.getbukkit.org";

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
         "piston-meta.mojang.com", "piston-data.mojang.com", "launchermeta.mojang.com", "launcher.mojang.com",
         "bmclapi2.bangbang93.com", "fill.papermc.io", "fill-data.papermc.io", "api.purpurmc.org", "download.fastmirror.net",
         "meta.fabricmc.net", "maven.neoforged.net", "getbukkit.org", "download.getbukkit.org"
    };

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip };

    public static string MirrorNotice(ServerLoader loader, int source)
    {
         return loader is ServerLoader.Paper or ServerLoader.Purpur or ServerLoader.Folia
             ? source == 2 ? "仅使用官方源。" : "支持 FastMirror 第三方镜像；源顺序跟随下载设置，文件仍按官方摘要校验；构建缺失时使用官方源。"
             : loader == ServerLoader.Spigot ? "Spigot 使用 GetBukkit 预构建 JAR，并按目录提供的 SHA-256 校验。这不是 BuildTools 源码编译。"
             : "";
    }

    public static bool IsStableDefault(string? build)
    {
        var value = build?.Trim() ?? "";
        return value.Length == 0 || value.Equals("latest", StringComparison.OrdinalIgnoreCase) || value.Equals("stable", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> OrderUrls(int source, IEnumerable<string?> official, IEnumerable<string?> mirror)
    {
        var officialUrls = Clean(official);
        var mirrorUrls = Clean(mirror);
        return source switch
        {
            0 => Distinct(mirrorUrls.Concat(officialUrls)),
            2 => Distinct(officialUrls),
            _ => Distinct(officialUrls.Concat(mirrorUrls))
        };
    }

    public static string? RewriteVanillaHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.Host is not ("piston-meta.mojang.com" or "piston-data.mojang.com" or "launchermeta.mojang.com" or "launcher.mojang.com")) return null;
        return new UriBuilder(uri) { Scheme = "https", Host = "bmclapi2.bangbang93.com", Port = -1 }.Uri.AbsoluteUri;
    }

    public static void RequireHttpsAllowlisted(string url)
    {
         if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !AllowedHosts.Contains(uri.IdnHost))
             throw new IOException("下载地址不受信任。");
     }
 
     public static async Task<IReadOnlyList<string>> ListVersionsAsync(HttpClient http, ServerLoader loader, int source, CancellationToken token)
     {
         return loader switch
         {
             ServerLoader.Paper => await ListPaperFamilyVersionsAsync(http, PaperV3, token).ConfigureAwait(false),
             ServerLoader.Folia => await ListPaperFamilyVersionsAsync(http, FoliaV3, token).ConfigureAwait(false),
             ServerLoader.Purpur => await ListPurpurVersionsAsync(http, token).ConfigureAwait(false),
             ServerLoader.Spigot => await ListSpigotVersionsAsync(http, token).ConfigureAwait(false),
             _ => await ListVanillaVersionsAsync(http, source, token).ConfigureAwait(false)
         };
     }
 
     public static Task<ServerCoreArtifact> ResolveAsync(HttpClient http, ServerProfile profile, int source, CancellationToken token)
         => ResolveAsync(http, profile.Loader, profile.MinecraftVersion, profile.LoaderVersion, source, token);
     public static async Task<IReadOnlyList<string>> ListLoaderVersionsAsync(HttpClient http, ServerLoader loader, string minecraftVersion, int source, CancellationToken token)
     {
         var version = RequireVersion(minecraftVersion);
         return loader switch
         {
             ServerLoader.NeoForge => await ListNeoForgeAsync(http, version, token).ConfigureAwait(false),
             ServerLoader.Fabric => await ListFabricLoadersAsync(http, version, source, token).ConfigureAwait(false),
             ServerLoader.Paper => await ListPaperFamilyBuildsAsync(http, PaperV3, version, token).ConfigureAwait(false),
             ServerLoader.Folia => await ListPaperFamilyBuildsAsync(http, FoliaV3, version, token).ConfigureAwait(false),
             ServerLoader.Purpur => await ListPurpurBuildsAsync(http, version, token).ConfigureAwait(false),
             ServerLoader.Spigot => await ListSpigotBuildsAsync(http, version, token).ConfigureAwait(false),
             _ => []
         };
     }
 
     public static async Task<ServerCoreArtifact> ResolveAsync(HttpClient http, ServerLoader loader, string minecraftVersion, string? build, int source, CancellationToken token)
     {
         if (!ServerCommands.IsDirectCore(loader)) throw new ArgumentException("只有原版、Paper、Purpur、Spigot、Folia 直接解析服务端 JAR。");
         var version = RequireVersion(minecraftVersion);
         var explicitBuild = loader == ServerLoader.Spigot ? RequireSpigotBuild(build) : NormalizeBuild(build);
         var artifact = loader switch
         {
             ServerLoader.Vanilla => await ResolveVanillaAsync(http, version, source, token).ConfigureAwait(false),
             ServerLoader.Paper => await ResolvePaperFamilyAsync(http, PaperV3, ServerLoader.Paper, version, explicitBuild, token).ConfigureAwait(false),
             ServerLoader.Folia => await ResolvePaperFamilyAsync(http, FoliaV3, ServerLoader.Folia, version, explicitBuild, token).ConfigureAwait(false),
             ServerLoader.Purpur => await ResolvePurpurAsync(http, version, explicitBuild, token).ConfigureAwait(false),
             ServerLoader.Spigot => await ResolveSpigotAsync(http, version, explicitBuild!, token).ConfigureAwait(false),
             _ => throw new ArgumentException("不支持的直接核心。")
         };
         if (artifact.Size > ServerCoreTransfer.MaxJarBytes) throw new IOException("元数据中的服务端 JAR 超过大小上限。");
         return loader == ServerLoader.Spigot ? artifact : await ServerCoreMirrors.AddAsync(http, artifact, source, explicitBuild is null, token).ConfigureAwait(false);
     }

    private static async Task<ServerCoreArtifact> ResolveVanillaAsync(HttpClient http, string version, int source, CancellationToken token)
    {
        using var manifest = await FirstJsonAsync(http, OrderUrls(source, [MojangManifest], [BmclapiManifest]), token).ConfigureAwait(false);
        string? versionUrl = null;
        if (!manifest.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            throw new IOException("版本清单缺少 versions。");
        foreach (var item in versions.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id) && id.GetString() == version && item.TryGetProperty("url", out var url))
            {
                versionUrl = url.GetString();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(versionUrl)) throw new IOException("版本清单中没有这个 Minecraft 版本。");
        var officialJson = RequireHttps(versionUrl, "版本 JSON");
        var mirrorJson = new[] { RewriteVanillaHost(officialJson), $"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(version)}/json" };
        using var json = await FirstJsonAsync(http, OrderUrls(source, [officialJson], mirrorJson), token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("server", out var server))
            throw new IOException("该 Minecraft 版本没有服务端下载。");
        var serverUrl = RequireHttps(RequiredString(server, "url"), "原版服务端");
        var sha1 = RequireHash(RequiredString(server, "sha1"), 40, "sha1");
        var size = server.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsed) ? parsed : (long?)null;
        int? java = null;
        if (json.RootElement.TryGetProperty("javaVersion", out var javaVersion) && javaVersion.TryGetProperty("majorVersion", out var major) && major.TryGetInt32(out var majorVersion))
            java = majorVersion;
        var urls = OrderUrls(source, [serverUrl], [RewriteVanillaHost(serverUrl), $"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(version)}/server"]);
        return new ServerCoreArtifact
        {
            Loader = ServerLoader.Vanilla, MinecraftVersion = version, Build = "", JavaMajor = java,
            ChecksumAlgorithm = "sha1", Checksum = sha1, Size = size, Urls = urls, MetadataSource = "mojang-manifest-v2"
        };
    }

     private static Task<ServerCoreArtifact> ResolvePaperV3Async(HttpClient http, string version, string? build, CancellationToken token)
         => ResolvePaperFamilyAsync(http, PaperV3, ServerLoader.Paper, version, build, token);
 
     private static async Task<ServerCoreArtifact> ResolvePaperFamilyAsync(HttpClient http, string project, ServerLoader loader, string version, string? build, CancellationToken token)
     {
         var encoded = Uri.EscapeDataString(version);
         JsonElement selected;
         string text;
         if (build is null)
         {
             text = await ServerCoreTransfer.GetTextAsync(http, $"{project}/versions/{encoded}/builds", token, userAgent: UserAgent).ConfigureAwait(false);
             using var document = Parse(text);
             RejectPaperError(document.RootElement);
             selected = FirstStable(document.RootElement) ?? throw new NoStableBuildException($"该 Minecraft 版本没有 {loader} 稳定构建。可填写明确构建号。");
         }
         else
         {
             text = await ServerCoreTransfer.GetTextAsync(http, $"{project}/versions/{encoded}/builds/{build}", token, userAgent: UserAgent).ConfigureAwait(false);
             using var document = Parse(text);
             RejectPaperError(document.RootElement);
             selected = document.RootElement.Clone();
         }
         var id = selected.TryGetProperty("id", out var idValue) ? JsonText(idValue) : build;
         if (string.IsNullOrWhiteSpace(id)) throw new IOException(loader + " 构建号缺失。");
         if (!selected.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("server:default", out var file))
             throw new IOException(loader + " 元数据缺少 server:default 下载。");
         var url = RequireHttps(RequiredString(file, "url"), loader.ToString());
         var sha = RequireHash(file.TryGetProperty("checksums", out var checksums) ? RequiredString(checksums, "sha256") : "", 64, "sha256");
         long? size = file.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsed) ? parsed : null;
         return new ServerCoreArtifact
         {
             Loader = loader, MinecraftVersion = version, Build = id, JavaMajor = await TryPaperJavaAsync(http, project, encoded, token).ConfigureAwait(false),
             ChecksumAlgorithm = "sha256", Checksum = sha, Size = size, Urls = [url], MetadataSource = "fill.papermc.io/v3",
             MirrorNotice = MirrorNotice(loader, 1)
         };
     }
 
     private static async Task<int?> TryPaperJavaAsync(HttpClient http, string project, string encodedVersion, CancellationToken token)
     {
         try
         {
             var text = await ServerCoreTransfer.GetTextAsync(http, $"{project}/versions/{encodedVersion}", token, userAgent: UserAgent).ConfigureAwait(false);
             using var document = Parse(text);
             if (document.RootElement.TryGetProperty("version", out var version)
                 && version.TryGetProperty("java", out var java)
                 && java.TryGetProperty("version", out var javaVersion)
                 && javaVersion.TryGetProperty("minimum", out var minimum)
                 && minimum.TryGetInt32(out var major)) return major;
         }
         catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
         catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { }
         return null;
     }

    private static async Task<ServerCoreArtifact> ResolvePurpurAsync(HttpClient http, string version, string? build, CancellationToken token)
    {
        var encoded = Uri.EscapeDataString(version);
        var metadataUrl = build is null ? $"{PurpurV2}/{encoded}/latest" : $"{PurpurV2}/{encoded}/{build}";
        var text = await ServerCoreTransfer.GetTextAsync(http, metadataUrl, token, userAgent: UserAgent).ConfigureAwait(false);
        using var document = Parse(text);
        var root = document.RootElement;
        var resolved = root.TryGetProperty("build", out var buildValue) ? JsonText(buildValue) : build;
        if (string.IsNullOrWhiteSpace(resolved) || !Regex.IsMatch(resolved, @"\A[0-9]{1,10}\z")) throw new IOException("Purpur 构建号无效。");
        if (build is not null && resolved != build) throw new IOException("Purpur 返回的构建号与请求不一致。");
        var md5 = root.TryGetProperty("md5", out var md5Value) ? md5Value.GetString() : null;
        if (string.IsNullOrWhiteSpace(md5) && root.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("md5", out var nested)) md5 = nested.GetString();
        var download = RequireHttps($"{PurpurV2}/{encoded}/{resolved}/download", "Purpur");
        return new ServerCoreArtifact
        {
            Loader = ServerLoader.Purpur, MinecraftVersion = version, Build = resolved, JavaMajor = null,
            ChecksumAlgorithm = "md5", Checksum = RequireHash(md5 ?? "", 32, "md5"), Size = null, Urls = [download],
            MetadataSource = "api.purpurmc.org/v2", MirrorNotice = MirrorNotice(ServerLoader.Purpur, 1)
        };
    }

    private static async Task<IReadOnlyList<string>> ListVanillaVersionsAsync(HttpClient http, int source, CancellationToken token)
    {
        using var manifest = await FirstJsonAsync(http, OrderUrls(source, [MojangManifest], [BmclapiManifest]), token).ConfigureAwait(false);
        if (!manifest.RootElement.TryGetProperty("versions", out var versions)) return [];
        var releases = new List<string>();
        var others = new List<string>();
        foreach (var item in versions.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (item.TryGetProperty("type", out var type) && type.GetString() == "release") releases.Add(id);
            else others.Add(id);
            if (releases.Count >= 24 && others.Count >= 8) break;
        }
        return releases.Take(24).Concat(others.Take(8)).ToArray();
    }

     private static Task<IReadOnlyList<string>> ListPaperFamilyVersionsAsync(HttpClient http, string project, CancellationToken token)
         => ListPaperProjectVersionsAsync(http, project, token);
 
     private static async Task<IReadOnlyList<string>> ListPaperProjectVersionsAsync(HttpClient http, string project, CancellationToken token)
     {
         var text = await ServerCoreTransfer.GetTextAsync(http, project, token, userAgent: UserAgent).ConfigureAwait(false);
         using var document = Parse(text);
         if (!document.RootElement.TryGetProperty("versions", out var versions)) return [];
         var result = new List<string>();
         if (versions.ValueKind == JsonValueKind.Array) result.AddRange(versions.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0));
         else if (versions.ValueKind == JsonValueKind.Object)
             foreach (var group in versions.EnumerateObject())
                 if (group.Value.ValueKind == JsonValueKind.Array)
                     result.AddRange(group.Value.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0));
         return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
     }

    private static async Task<IReadOnlyList<string>> ListPurpurVersionsAsync(HttpClient http, CancellationToken token)
    {
        var text = await ServerCoreTransfer.GetTextAsync(http, PurpurV2, token, userAgent: UserAgent).ConfigureAwait(false);
        using var document = Parse(text);
        var root = document.RootElement;
        var versions = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("versions", out var value) ? value : default;
        if (versions.ValueKind != JsonValueKind.Array) return [];
        return versions.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).Reverse().Take(32).ToArray();
    }
 
     private static async Task<IReadOnlyList<string>> ListPaperFamilyBuildsAsync(HttpClient http, string project, string version, CancellationToken token)
     {
         var text = await ServerCoreTransfer.GetTextAsync(http, $"{project}/versions/{Uri.EscapeDataString(version)}/builds", token, userAgent: UserAgent).ConfigureAwait(false);
         using var document = Parse(text);
         RejectPaperError(document.RootElement);
         return EnumerateObjects(document.RootElement).Select(item => item.TryGetProperty("id", out var id) ? JsonText(id) : "").Where(id => id.Length > 0).Take(48).ToArray();
     }
 
     private static async Task<IReadOnlyList<string>> ListPurpurBuildsAsync(HttpClient http, string version, CancellationToken token)
     {
         var text = await ServerCoreTransfer.GetTextAsync(http, $"{PurpurV2}/{Uri.EscapeDataString(version)}", token, userAgent: UserAgent).ConfigureAwait(false);
         using var document = Parse(text);
         if (!document.RootElement.TryGetProperty("builds", out var builds) || !builds.TryGetProperty("all", out var all) || all.ValueKind != JsonValueKind.Array) return [];
         return all.EnumerateArray().Select(item => JsonText(item)).Where(item => item.Length > 0).Reverse().Take(48).ToArray();
     }
 
     private static async Task<IReadOnlyList<string>> ListSpigotVersionsAsync(HttpClient http, CancellationToken token)
     {
         var jars = await SpigotJarsAsync(http, token).ConfigureAwait(false);
         return jars.Select(item => item.Version).Distinct(StringComparer.OrdinalIgnoreCase).Take(48).ToArray();
     }
 
     private static async Task<IReadOnlyList<string>> ListSpigotBuildsAsync(HttpClient http, string version, CancellationToken token)
     {
         var jars = await SpigotJarsAsync(http, token).ConfigureAwait(false);
         return jars.Where(item => item.Version.Equals(version, StringComparison.OrdinalIgnoreCase)).Select(item => item.Version).Take(1).ToArray();
     }
 
     private static async Task<ServerCoreArtifact> ResolveSpigotAsync(HttpClient http, string version, string build, CancellationToken token)
     {
         var jars = await SpigotJarsAsync(http, token).ConfigureAwait(false);
         var match = jars.FirstOrDefault(item => item.Version.Equals(build, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
         if (match is null) throw new IOException("GetBukkit 没有这个 Spigot 版本。");
         var relative = match.Download;
         if (!relative.StartsWith('/') || relative.Contains("..", StringComparison.Ordinal) || !relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
             throw new IOException("Spigot 下载路径无效。");
         return new ServerCoreArtifact
         {
             Loader = ServerLoader.Spigot, MinecraftVersion = version, Build = match.Version, JavaMajor = null,
             ChecksumAlgorithm = "sha256", Checksum = RequireHash(match.Sha256, 64, "sha256"), Size = match.Size,
             Urls = [RequireHttps(SpigotDownload + relative, "Spigot")], MetadataSource = "getbukkit.org/api/jars",
             MirrorNotice = MirrorNotice(ServerLoader.Spigot, 1)
         };
     }
 
     private static async Task<SpigotJar[]> SpigotJarsAsync(HttpClient http, CancellationToken token)
     {
         var text = await ServerCoreTransfer.GetTextAsync(http, SpigotJars, token, userAgent: UserAgent).ConfigureAwait(false);
         using var document = Parse(text);
         if (!document.RootElement.TryGetProperty("spigot", out var items) || items.ValueKind != JsonValueKind.Array) return [];
         var result = new List<SpigotJar>();
         foreach (var item in items.EnumerateArray())
         {
             if (item.TryGetProperty("type", out var type) && type.GetString() != "spigot") continue;
             var version = item.TryGetProperty("version", out var versionValue) ? versionValue.GetString() ?? "" : "";
             var download = item.TryGetProperty("downloadUrl", out var downloadValue) ? downloadValue.GetString() ?? "" : "";
             var sha = item.TryGetProperty("sha256", out var shaValue) ? shaValue.GetString() ?? "" : "";
             long? size = item.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsed) ? parsed : null;
             if (version.Length == 0 || !Regex.IsMatch(version, @"\A[0-9A-Za-z.+_-]+\z")) continue;
             result.Add(new SpigotJar(version, download, sha, size));
         }
         return result.ToArray();
     }
 
     private static string? RequireSpigotBuild(string? build)
     {
         var value = build?.Trim() ?? "";
         if (!Regex.IsMatch(value, @"\A[0-9A-Za-z.+_-]+\z")) throw new ArgumentException("请选择 Spigot 版本。");
         return value;
     }
 
     private static async Task<IReadOnlyList<string>> ListFabricLoadersAsync(HttpClient http, string version, int source, CancellationToken token)
     {
         var encoded = Uri.EscapeDataString(version);
         var official = $"{FabricMeta}/loader/{encoded}";
         var mirror = $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{encoded}";
         using var document = await FirstJsonAsync(http, OrderUrls(source, [official], [mirror]), token).ConfigureAwait(false);
         if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
         return document.RootElement.EnumerateArray()
             .Select(item => item.TryGetProperty("loader", out var loader) && loader.TryGetProperty("version", out var value) ? value.GetString() ?? "" : "")
             .Where(item => item.Length > 0).Take(48).ToArray();
     }
 
     private static async Task<IReadOnlyList<string>> ListNeoForgeAsync(HttpClient http, string minecraft, CancellationToken token)
     {
         var text = await ServerCoreTransfer.GetTextAsync(http, NeoForgeVersions, token, userAgent: UserAgent).ConfigureAwait(false);
         using var document = Parse(text);
         if (!document.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array) return [];
         var prefix = NeoForgePrefix(minecraft);
         return versions.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => NeoForgeMatches(minecraft, prefix, item)).Reverse().Take(48).ToArray();
     }
 
     private static string NeoForgePrefix(string minecraft)
     {
         var parts = minecraft.Split('.');
         if (parts.Length >= 2 && parts[0] == "1" && int.TryParse(parts[1], out var minor) && minor >= 21)
             return parts.Length >= 3 && parts[2] != "0" ? parts[1] + "." + parts[2] + "." : parts[1] + ".0.";
         return "";
     }
 
     private static bool NeoForgeMatches(string minecraft, string prefix, string version)
         => prefix.Length > 0 ? version.StartsWith(prefix, StringComparison.Ordinal) : version.StartsWith(minecraft + "-", StringComparison.OrdinalIgnoreCase);
 
     private sealed record SpigotJar(string Version, string Download, string Sha256, long? Size);

    private static async Task<JsonDocument> FirstJsonAsync(HttpClient http, IReadOnlyList<string> urls, CancellationToken token)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var text = await ServerCoreTransfer.GetTextAsync(http, url, token, userAgent: UserAgent).ConfigureAwait(false);
                return Parse(text);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException) { last = ex; }
        }
        throw new IOException("无法读取服务端元数据。", last);
    }

    private static JsonDocument Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > ServerCoreTransfer.MaxJsonBytes) throw new IOException("元数据超过大小上限。");
        return JsonDocument.Parse(text, JsonOptions);
    }

    private static void RejectPaperError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new IOException(root.TryGetProperty("message", out var message) ? message.GetString() ?? "Paper API 错误" : "Paper API 错误");
    }

    private static JsonElement? FirstStable(JsonElement root)
    {
        foreach (var build in EnumerateObjects(root))
            if (build.TryGetProperty("channel", out var channel) && channel.GetString()?.Equals("STABLE", StringComparison.OrdinalIgnoreCase) == true)
                return build.Clone();
        return null;
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object) yield return item;
            yield break;
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("builds", out var builds) && builds.ValueKind == JsonValueKind.Array)
            foreach (var item in builds.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object) yield return item;
    }

    private static string RequireVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || !Regex.IsMatch(version, @"\A[0-9A-Za-z.+_-]+\z"))
            throw new ArgumentException("请填写精确的 Minecraft 版本。");
        return version;
    }

    private static string? NormalizeBuild(string? build)
    {
        if (IsStableDefault(build)) return null;
        var value = build!.Trim();
        if (!Regex.IsMatch(value, @"\A[0-9]{1,10}\z")) throw new ArgumentException("构建号必须是数字，或留空以选择稳定版/最新构建。");
        return value;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) throw new IOException("元数据缺少字段：" + name);
        var text = JsonText(value);
        if (string.IsNullOrWhiteSpace(text)) throw new IOException("元数据字段为空：" + name);
        return text;
    }

    private static string JsonText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.ValueKind == JsonValueKind.Null ? "" : value.GetRawText()
    };

    private static string RequireHttps(string url, string context)
    {
        try { RequireHttpsAllowlisted(url); return new Uri(url).AbsoluteUri; }
        catch (IOException) { throw new IOException(context + " 的地址不受信任。"); }
    }

    private static string RequireHash(string hash, int length, string algorithm)
    {
        if (!Regex.IsMatch(hash, @"\A[0-9a-fA-F]{" + length + @"}\z")) throw new IOException($"元数据缺少有效 {algorithm}。");
        return hash.ToLowerInvariant();
    }

    private static IEnumerable<string> Clean(IEnumerable<string?> urls) => urls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url!);

    private static string[] Distinct(IEnumerable<string> urls)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            RequireHttpsAllowlisted(url);
            if (seen.Add(url)) result.Add(url);
        }
        return result.ToArray();
    }

    private sealed class NoStableBuildException(string message) : IOException(message);
}
