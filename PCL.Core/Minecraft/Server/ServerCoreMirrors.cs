using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Server;

/// <summary>FastMirror adds a binary alternative, never replaces the official checksum/build authority.</summary>
public static class ServerCoreMirrors
{
    public const string Api = "https://download.fastmirror.net/api/v3";

    public static async Task<ServerCoreArtifact> AddAsync(HttpClient http, ServerCoreArtifact artifact, int source, bool latestRequested, CancellationToken token)
    {
         if (artifact.Loader is not (ServerLoader.Paper or ServerLoader.Purpur or ServerLoader.Folia) || source == 2) return artifact;
        var core = artifact.Loader.ToString();
        var mirror = await ReadAsync("build" + artifact.Build).ConfigureAwait(false);
        if (mirror is null && artifact.Loader == ServerLoader.Purpur && latestRequested)
            mirror = await ReadAsync("buildlatest").ConfigureAwait(false);
        return new ServerCoreArtifact
        {
            Loader = artifact.Loader, MinecraftVersion = artifact.MinecraftVersion, Build = artifact.Build,
            JavaMajor = artifact.JavaMajor, ChecksumAlgorithm = artifact.ChecksumAlgorithm,
            Checksum = artifact.Checksum, Size = artifact.Size, MetadataSource = artifact.MetadataSource,
            Urls = ServerCoreCatalog.OrderUrls(source, artifact.Urls, [mirror]),
            MirrorNotice = mirror is null ? "FastMirror 未提供此固定构建，使用官方源。" : "FastMirror 为第三方镜像；仍按官方元数据校验文件，不匹配时回退。"
        };

        async Task<string?> ReadAsync(string requestedBuild)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var url = $"{Api}/{core}/{Uri.EscapeDataString(artifact.MinecraftVersion)}/{requestedBuild}";
            try
            {
                var text = await ServerCoreTransfer.GetTextAsync(http, url, deadline.Token, userAgent: ServerCoreCatalog.UserAgent).ConfigureAwait(false);
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True || !root.TryGetProperty("data", out var data)) return null;
                if (data.GetProperty("name").GetString() != core || data.GetProperty("mc_version").GetString() != artifact.MinecraftVersion || data.GetProperty("core_version").GetString() != requestedBuild) return null;
                var download = data.GetProperty("download_url").GetString();
                if (!Uri.TryCreate(download, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.IdnHost != "download.fastmirror.net" || !string.IsNullOrEmpty(uri.UserInfo)) return null;
                return uri.AbsoluteUri;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException) { return null; }
        }
    }
}
