using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using fNbt;
using PCL.Core.Minecraft.Saves;

namespace PCL.Core.UI;

public sealed record GameDashboardWorld(string Name, string Folder, DateTime LastPlayedUtc, string Version);
public sealed record GameDashboardServer(string Name, string Address);
public sealed record GameDashboardFileSnapshot(IReadOnlyList<GameDashboardWorld> Worlds, IReadOnlyList<GameDashboardServer> Servers, string? Error);

/// <summary>Read-only, bounded dashboard preview of a single instance's game directory.</summary>
public static class GameDashboardFiles
{
    public static Task<GameDashboardFileSnapshot> LoadAsync(string gameRoot, CancellationToken token = default) => Task.Run(async () =>
    {
        var worlds = new List<GameDashboardWorld>();
        var servers = new List<GameDashboardServer>();
        var errors = new List<string>();
        try
        {
            var saves = Path.Combine(gameRoot, "saves");
            if (Directory.Exists(saves))
            {
                var manager = new SaveManager();
                // Bound the preview. Full archive management remains in the existing saves page.
                var folders = Directory.EnumerateDirectories(saves).Select(path => new DirectoryInfo(path))
                    .OrderByDescending(folder => folder.LastWriteTimeUtc).Take(100).ToArray();
                foreach (var folder in folders)
                {
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(Path.Combine(folder.FullName, "level.dat"))) continue;
                    try
                    {
                        var save = await manager.LoadSaveAsync(folder.FullName, token).ConfigureAwait(false);
                        worlds.Add(new(save.LevelName, save.FolderPath, save.LastPlayedUtc, save.VersionName ?? "未知版本"));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { errors.Add("部分世界无法读取，可在存档管理中检查。"); }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { errors.Add("世界目录读取失败：" + ex.Message); }
        token.ThrowIfCancellationRequested();
        try
        {
            var file = Path.Combine(gameRoot, "servers.dat");
            if (File.Exists(file))
            {
                if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("服务器列表文件过大");
                var nbt = new NbtFile();
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                nbt.LoadFromStream(stream, NbtCompression.AutoDetect);
                if (nbt.RootTag.Get<NbtList>("servers") is { } list)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tag in list)
                    {
                        token.ThrowIfCancellationRequested();
                        if (servers.Count >= 100) break;
                        if (tag is not NbtCompound server || server.Get<NbtByte>("hidden")?.Value == 1) continue;
                        var address = server.Get<NbtString>("ip")?.Value?.Trim() ?? "";
                        var name = server.Get<NbtString>("name")?.Value?.Trim() ?? "";
                        if (address.Length is 0 or > 512 || address.Any(char.IsControl) || !seen.Add(address)) continue;
                        servers.Add(new(name.Length is > 0 and <= 120 ? name : address, address));
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add("服务器列表读取失败：" + ex.Message); }
        return new GameDashboardFileSnapshot(worlds.OrderByDescending(world => world.LastPlayedUtc).Take(6).ToArray(), servers.Take(6).ToArray(), errors.Count == 0 ? null : string.Join("\n", errors.Distinct()));
    }, token);
}
