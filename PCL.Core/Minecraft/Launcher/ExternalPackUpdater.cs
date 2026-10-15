using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Launcher;

public sealed record ExternalUpdateSource(string Id, string Url, bool Enabled);
public sealed record ExternalUpdatePlan(string Root, string Url, string Hash, string Version, bool ConfigExistenceOnly = false);
public sealed record PackwizIndexEntry(string Path, string Hash, bool Metafile);

/// <summary>One automatic Packwiz manifest check per game directory. Manual checks do not use this gate.</summary>
public sealed class ExternalPackCheckOnce
{
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public bool IsDone(string root) => _done.Contains(Normalize(root));

    public void MarkDone(string root) => _done.Add(Normalize(root));

    public void Clear() => _done.Clear();

    public static string ManagedPackId(string instanceName, string root)
    {
        var slug = Regex.Replace(instanceName.ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        if (slug.Length == 0 || !char.IsAsciiLetterOrDigit(slug[0])) slug = slug.Length == 0 ? "pack" : "pack-" + slug;
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(root).ToLowerInvariant())))[..8].ToLowerInvariant();
        return "mls-" + slug + "-" + hash;
    }
}
/// <summary>Runs the pack's own MLS/Packwiz logic, with a recoverable managed-directory snapshot.</summary>
public sealed class ExternalPackUpdater
{
    public const string PresetUpdateUrl = "http://yd.aurorian.cn:25588/pack/pack.toml";
    public static readonly TimeSpan DefaultManifestTimeout = TimeSpan.FromSeconds(20);
    private static readonly object ArchiveConfigGate = new();
    private static string _cachedArchive = "";
    private static long _cachedArchiveLength = -1;
    private static DateTime _cachedArchiveWrite;
    private static ExternalUpdateSource? _cachedArchiveSource;
    private static readonly string[] Managed = ["mods", "config", "resourcepacks", ".updatemod", "packwiz.json", "pack.toml"];
    private readonly PackTransfer _transfer;
    private readonly string _java;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _run;

    public ExternalPackUpdater(HttpClient http, string javaExecutable = "java", Func<ProcessStartInfo, CancellationToken, Task<int>>? runner = null)
    {
        _transfer = new PackTransfer(http); _java = javaExecutable; _run = runner ?? RunProcessAsync;
    }

    public static ExternalUpdateSource? ReadSource(string root)
    {
        var path = PackCatalog.SafePath(root, "config/mod-loading-screen/update.properties");
        var values = File.Exists(path) ? ReadProperties(File.ReadAllText(path, Encoding.Latin1)) : new Dictionary<string, string>();
        string Get(string key, string fallback = "") => values.TryGetValue(key, out var value) ? value : fallback;
        var ids = Get("sources").Split(',').Select(v => Regex.Replace(v.Trim(), "[^a-zA-Z0-9_-]", "")).Where(v => v.Length > 0).ToArray();
        var id = Get("activeSource", ids.FirstOrDefault() ?? "default");
        if (ids.Length > 0 && !ids.Contains(id)) id = ids[0];
        var prefix = ids.Length > 0 ? "source." + id + "." : "";
        var url = Get(prefix + "packwizUrl", id == "default" ? Get("packwizUrl") : "");
        var enabled = string.Equals(Get(prefix + "autoUpdate", Get("autoUpdate", "false")), "true", StringComparison.OrdinalIgnoreCase);
        if (url.Length == 0)
        {
            var legacy = PackCatalog.SafePath(root, ".updatemod/config.json");
            if (File.Exists(legacy))
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(legacy));
                if (json.RootElement.TryGetProperty("packwizUrl", out var v)) url = v.GetString() ?? "";
                if (!File.Exists(path) && json.RootElement.TryGetProperty("autoUpdate", out var a)) enabled = a.GetBoolean();
            }
        }
        if (url.Length == 0) return null;
        PackCatalog.HttpUri(url);
        return new ExternalUpdateSource(id, url, enabled);
    }

    public static ExternalUpdateSource? ReadArchiveConfig(string? archivePath)
    {
        if (!string.IsNullOrEmpty(archivePath) && File.Exists(archivePath))
        {
            var info = new FileInfo(archivePath);
            var full = info.FullName;
            lock (ArchiveConfigGate)
            {
                if (_cachedArchiveSource is not null && string.Equals(_cachedArchive, full, StringComparison.OrdinalIgnoreCase)
                    && _cachedArchiveLength == info.Length && _cachedArchiveWrite == info.LastWriteTimeUtc)
                    return _cachedArchiveSource;
            }
            ExternalUpdateSource? parsed = null;
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var entry = zip.Entries.FirstOrDefault(item => item.FullName.Replace('\\', '/').EndsWith("config/mod-loading-screen/update.properties", StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
                    parsed = ReadSourceText(reader.ReadToEnd());
                }
            }
            var source = parsed ?? PresetSource();
            lock (ArchiveConfigGate)
            {
                _cachedArchive = full;
                _cachedArchiveLength = info.Length;
                _cachedArchiveWrite = info.LastWriteTimeUtc;
                _cachedArchiveSource = source;
            }
            return source;
        }
        return PresetSource();
    }

    private static ExternalUpdateSource PresetSource()
    {
        PackCatalog.HttpUri(PresetUpdateUrl);
        return new ExternalUpdateSource("default", PresetUpdateUrl, true);
    }

    private static ExternalUpdateSource? ReadSourceText(string text)
    {
        var values = ReadProperties(text);
        var url = values.TryGetValue("packwizUrl", out var value) ? value : "";
        if (url.Length == 0) return null;
        var enabled = values.TryGetValue("autoUpdate", out var auto) && string.Equals(auto, "true", StringComparison.OrdinalIgnoreCase);
        PackCatalog.HttpUri(url);
        return new ExternalUpdateSource("default", url, enabled);
    }

    public static Dictionary<string, string> ReadProperties(string text)
    {
        var result = new Dictionary<string, string>();
        var logical = "";
        foreach (var physical in text.Replace("\r", "").Split('\n'))
        {
            logical += physical.TrimStart();
            var trailing = logical.Reverse().TakeWhile(c => c == '\\').Count();
            if (trailing % 2 != 0) { logical = logical[..^1]; continue; }
            var line = logical; logical = "";
            if (line.Length == 0 || line[0] is '#' or '!') continue;
            var separator = -1;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] is '=' or ':' || char.IsWhiteSpace(line[i])) { separator = i; break; }
            }
            if (separator < 0) result[Unescape(line)] = "";
            else
            {
                var value = line[(separator + 1)..].TrimStart();
                if (char.IsWhiteSpace(line[separator]) && value.Length > 0 && value[0] is '=' or ':') value = value[1..].TrimStart();
                result[Unescape(line[..separator])] = Unescape(value);
            }
        }
        return result;
    }

    private static string Unescape(string value) => Regex.Replace(value, @"\\(u[0-9a-fA-F]{4}|.)", m => m.Groups[1].Value switch
    {
        "t" => "\t", "n" => "\n", "r" => "\r", "f" => "\f",
        var s when s.StartsWith('u') && s.Length == 5 => ((char)Convert.ToInt32(s[1..], 16)).ToString(),
        var s => s
    });

    public static string ReadLocalVersion(string root)
    {
        var path = Path.Combine(root, "modrinth.index.json");
        if (!File.Exists(path)) return "";
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("versionId", out var v) ? v.GetString() ?? "" : "";
    }
    public static bool HasModLoadingScreen(string root)
    {
        foreach (var dir in new[] { root, Path.Combine(root, "mods") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var jar in Directory.EnumerateFiles(dir, "*mod-loading-screen*.jar"))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(jar);
                    if (archive.Entries.Count >= 0) return true;
                }
                catch (InvalidDataException) { }
            }
        }
        return false;
    }

    public static bool IsModLoadingScreenConfig(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        return path.Equals("config/mod-loading-screen/update.properties", StringComparison.OrdinalIgnoreCase)
            || path.Equals("mod-loading-screen/update.properties", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Without mod-loading-screen, config files are written only when the target file is missing.</summary>
    public static bool ShouldUpdateConfigFile(string relativePath, bool targetExists, bool hasModLoadingScreen)
    {
        if (IsModLoadingScreenConfig(relativePath)) return false;
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        if (!path.StartsWith("config/", StringComparison.OrdinalIgnoreCase)) return false;
        return hasModLoadingScreen || !targetExists;
    }

    public static IReadOnlyList<PackwizIndexEntry> ParseIndex(string toml)
    {
        var result = new List<PackwizIndexEntry>();
        string? path = null;
        var hash = "";
        var meta = false;
        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(path)) result.Add(new PackwizIndexEntry(path, hash, meta));
            path = null; hash = ""; meta = false;
        }
        foreach (var raw in toml.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("[[")) { Flush(); continue; }
            if (line.StartsWith("file ") || line.StartsWith("file=")) path = Quoted(line);
            else if (line.StartsWith("hash ") || line.StartsWith("hash=")) hash = Quoted(line);
            else if (line.StartsWith("metafile ") || line.StartsWith("metafile=")) meta = line.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
        Flush();
        return result;
    }

    private static string Quoted(string line)
    {
        var quote = line.IndexOf('"');
        if (quote < 0) return line[(line.IndexOf('=') + 1)..].Trim();
        var end = line.IndexOf('"', quote + 1);
        return end < 0 ? line[(quote + 1)..] : line[(quote + 1)..end];
    }

    public async Task<ExternalUpdatePlan?> CheckAsync(string root, CancellationToken token, ExternalUpdateSource? fallback = null, TimeSpan? manifestTimeout = null)
    {
        var source = ReadSource(root) ?? fallback;
        if (source is null || !source.Enabled) return null;
        var limit = manifestTimeout ?? DefaultManifestTimeout;
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(manifestTimeout));
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        var read = _transfer.ReadBytesAsync(PackCatalog.HttpUri(source.Url), timeout.Token);
        _ = await Task.WhenAny(read, Task.Delay(limit, token)).ConfigureAwait(false);
        if (!read.IsCompleted)
        {
            timeout.Cancel();
            Observe(read);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("读取 Packwiz 清单超时。");
        }
        byte[] bytes;
        try { bytes = await read.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("读取 Packwiz 清单超时。");
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var cached = PackCatalog.SafePath(root, ".updatemod/sources/" + source.Id + "/packwiz.hash");
        if (!File.Exists(cached) && source.Id == "default") cached = PackCatalog.SafePath(root, ".updatemod/packwiz.hash");
        if (File.Exists(cached) && File.ReadAllText(cached).Trim().Equals(hash, StringComparison.OrdinalIgnoreCase)) return null;
        return new ExternalUpdatePlan(Path.GetFullPath(root), source.Url, hash, "Packwiz · " + hash[..8]);
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(static finished => { _ = finished.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async Task ApplyAsync(string root, ExternalUpdatePlan plan, IProgress<double>? progress, CancellationToken token, string? updaterArchive = null)
    {
        root = Path.GetFullPath(root);
        var local = ReadSource(root);
        if (!string.Equals(root, plan.Root, StringComparison.OrdinalIgnoreCase) || (local is not null && local.Url != plan.Url))
            throw new InvalidOperationException("更新目录或来源已改变，请重新检查更新。");
        var jar = FindUpdaterJar(root) ?? MaterializeUpdaterJar(root, updaterArchive);
        var backup = PackCatalog.SafePath(root, ".launcher-backups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(backup);
        await Task.Run(() =>
        {
            foreach (var entry in Managed) CopyTree(PackCatalog.SafePath(root, entry), Path.Combine(backup, entry), token);
            File.WriteAllText(Path.Combine(backup, "snapshot.complete"), "ready");
        }, token);
        var start = new ProcessStartInfo(_java) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-jar"); start.ArgumentList.Add(jar);
        start.ArgumentList.Add("--gameDir=" + root.TrimEnd(Path.DirectorySeparatorChar));
        start.ArgumentList.Add("--packwizUrl=" + plan.Url); start.ArgumentList.Add("--direct");
        progress?.Report(0);
        try
        {
            token.ThrowIfCancellationRequested();
            var exit = await _run(start, token).ConfigureAwait(false);
            if (exit != 0) throw new IOException("整合包更新器退出码：" + exit + "。受管文件将恢复；日志在 .updatemod/updater.log。");
            token.ThrowIfCancellationRequested();
            var source = ReadSource(root);
            var sourceId = source?.Id ?? "default";
            var hashFile = PackCatalog.SafePath(root, ".updatemod/sources/" + sourceId + "/packwiz.hash");
            if (!File.Exists(hashFile) && sourceId == "default") hashFile = PackCatalog.SafePath(root, ".updatemod/packwiz.hash");
            if (!File.Exists(hashFile) || !File.ReadAllText(hashFile).Trim().Equals(plan.Hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("更新器未确认预期清单哈希，请重新检查更新。");
            File.WriteAllText(Path.Combine(backup, "update.complete"), plan.Hash);
            progress?.Report(1);
        }
        catch (Exception updateError)
        {
            try
            {
                // Keep the failed run log separately, then restore all snapshotted managed state.
                var log = PackCatalog.SafePath(root, ".updatemod/updater.log");
                if (File.Exists(log)) File.Copy(log, Path.Combine(backup, "failed-updater.log"), true);
                foreach (var entry in Managed)
                {
                    var destination = PackCatalog.SafePath(root, entry);
                    if (Directory.Exists(destination)) Directory.Delete(destination, true);
                    else if (File.Exists(destination)) File.Delete(destination);
                    CopyTree(Path.Combine(backup, entry), destination, CancellationToken.None);
                }
            }
            catch (Exception recoveryError) { throw new AggregateException("更新失败，自动恢复也未完成。请保留备份：" + backup, updateError, recoveryError); }
            throw;
        }
    }

    public static string? FindUpdaterJar(string root)
    {
        foreach (var dir in new[] { root, Path.Combine(root, "mods") })
        {
            if (!Directory.Exists(dir)) continue;
            var jar = Directory.EnumerateFiles(dir, "*mod-loading-screen*.jar").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(HasStandaloneUpdater);
            if (jar is not null) return jar;
        }
        return null;
    }

    private static string MaterializeUpdaterJar(string root, string? archivePath)
    {
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
            throw new FileNotFoundException("目标没有 mod-loading-screen，整合包里也没有可调用的更新器。");
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.Entries.FirstOrDefault(item => item.Name.Contains("mod-loading-screen", StringComparison.OrdinalIgnoreCase) && item.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        if (entry is null) throw new FileNotFoundException("整合包内没有 mod-loading-screen 更新器。");
        var destination = PackCatalog.SafePath(root, ".updatemod/mod-loading-screen-updater.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".partial";
        entry.ExtractToFile(partial, true);
        if (!HasStandaloneUpdater(partial)) { File.Delete(partial); throw new InvalidDataException("整合包的更新器不支持独立更新入口。"); }
        File.Move(partial, destination, true);
        return destination;
    }

    private static bool HasStandaloneUpdater(string jar)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jar);
            return archive.GetEntry("io/github/gaming32/modloadingscreen/update/StandaloneUpdater.class") is not null;
        }
        catch (InvalidDataException) { return false; }
    }

    private async Task<List<PackwizIndexEntry>> MissingConfigFilesAsync(string root, Uri packUri, byte[] packTomlBytes, CancellationToken token)
    {
        var missing = new List<PackwizIndexEntry>();
        foreach (var entry in await ReadConfigEntriesAsync(packUri, packTomlBytes, token).ConfigureAwait(false))
        {
            var relative = entry.Path.Replace('\\', '/');
            if (ShouldUpdateConfigFile(relative, File.Exists(PackCatalog.SafePath(root, relative)), false)) missing.Add(entry);
        }
        return missing;
    }

    private async Task<IReadOnlyList<PackwizIndexEntry>> ReadConfigEntriesAsync(Uri packUri, byte[] packTomlBytes, CancellationToken token)
    {
        var indexRef = ReadIndexRef(Encoding.UTF8.GetString(packTomlBytes));
        var indexBytes = await _transfer.ReadBytesAsync(new Uri(packUri, indexRef.File), token).ConfigureAwait(false);
        var indexHash = Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant();
        if (indexRef.Hash.Length > 0 && !indexHash.Equals(indexRef.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("index.toml 哈希与 pack.toml 不一致。");
        return ParseIndex(Encoding.UTF8.GetString(indexBytes))
            .Where(entry => entry.Path.Replace('\\', '/').StartsWith("config/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static (string File, string Hash) ReadIndexRef(string packToml)
    {
        var inIndex = false;
        var file = "";
        var hash = "";
        foreach (var raw in packToml.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { if (inIndex) break; inIndex = line == "[index]"; continue; }
            if (!inIndex) continue;
            if (line.StartsWith("file ") || line.StartsWith("file=")) file = Quoted(line);
            else if (line.StartsWith("hash ") || line.StartsWith("hash=")) hash = Quoted(line);
        }
        if (file.Length == 0) throw new InvalidDataException("pack.toml 缺少 index 文件。");
        return (file, hash);
    }

    private async Task ApplyMissingConfigsAsync(string root, ExternalUpdatePlan plan, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var packUri = PackCatalog.HttpUri(plan.Url);
        var packBytes = await _transfer.ReadBytesAsync(packUri, timeout.Token).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(packBytes)).ToLowerInvariant();
        if (!hash.Equals(plan.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("远程清单在检查后已变化，请重新检查更新。");
        foreach (var entry in await ReadConfigEntriesAsync(packUri, packBytes, timeout.Token).ConfigureAwait(false))
        {
            var relative = entry.Path.Replace('\\', '/');
            var destination = PackCatalog.SafePath(root, relative);
            if (!ShouldUpdateConfigFile(relative, File.Exists(destination), false)) continue;
            if (entry.Metafile) throw new InvalidDataException("缺少 mod-loading-screen，不能按提供方规则更新配置：" + relative);
            if (entry.Hash.Length != 64) throw new InvalidDataException("配置文件缺少 SHA256：" + relative);
            var bytes = await _transfer.ReadBytesAsync(new Uri(packUri, relative), timeout.Token, 32 * 1024 * 1024).ConfigureAwait(false);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!actual.Equals(entry.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("配置文件哈希校验失败：" + relative);
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var partial = destination + ".partial";
            await File.WriteAllBytesAsync(partial, bytes, token).ConfigureAwait(false);
            if (File.Exists(destination)) { File.Delete(partial); continue; }
            File.Move(partial, destination);
        }
    }

    private static void RestoreManaged(string root, string backup, Exception updateError)
    {
        try
        {
            var log = PackCatalog.SafePath(root, ".updatemod/updater.log");
            if (File.Exists(log)) File.Copy(log, Path.Combine(backup, "failed-updater.log"), true);
            foreach (var entry in Managed)
            {
                var destination = PackCatalog.SafePath(root, entry);
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                else if (File.Exists(destination)) File.Delete(destination);
                CopyTree(Path.Combine(backup, entry), destination, CancellationToken.None);
            }
        }
        catch (Exception recoveryError)
        {
            throw new AggregateException("更新失败，自动恢复也未完成。请保留备份：" + backup, updateError, recoveryError);
        }
    }

    private static void CopyTree(string source, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(source) && !Directory.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("备份/恢复不允许符号链接：" + source);
        if (File.Exists(source)) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true); return; }
        Directory.CreateDirectory(target);
        foreach (var child in Directory.EnumerateFileSystemEntries(source)) CopyTree(child, Path.Combine(target, Path.GetFileName(child)), token);
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动 Java 更新进程。");
        // Drain without retaining arbitrary output in memory. MLS writes its own local log.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
        token.ThrowIfCancellationRequested();
        return process.ExitCode;
    }
}
