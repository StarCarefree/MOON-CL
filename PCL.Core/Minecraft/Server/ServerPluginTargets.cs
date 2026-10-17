using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCL.Core.Minecraft.Server;

/// <summary>Plugin targets are launcher-managed server identities, never paths supplied by a web page.</summary>
public static class ServerPluginTargets
{
     public static bool SupportsPlugins(ServerProfile profile) => profile.Loader.ToString() is "Paper" or "Purpur" or "Spigot" or "Folia";
    public static bool MatchesVersion(ServerProfile profile, IEnumerable<string> versions)
    {
        var exact = versions.Where(v => !string.IsNullOrWhiteSpace(v) && v.Any(char.IsDigit) && v.Contains('.')).ToArray();
        return exact.Length == 0 || exact.Contains(profile.MinecraftVersion, StringComparer.OrdinalIgnoreCase);
    }
    public static bool SupportsEnvironments(ServerProfile profile, IEnumerable<string> environments)
    {
        var loaders = environments.ToArray();
        // CurseForge Bukkit class files have no per-file loader tags. Folia-only jars are not Paper plugins.
         return SupportsPlugins(profile) && (loaders.Length == 0 || loaders.Any(v => v is "bukkit" or "spigot" or "paper" || v == "purpur" && profile.Loader == ServerLoader.Purpur || v == "folia" && profile.Loader == ServerLoader.Folia));
    }
    public static string PluginsDirectory(ServerManager manager, ServerProfile profile)
    {
         if (!SupportsPlugins(profile) || string.IsNullOrWhiteSpace(profile.EntryPoint)) throw new InvalidOperationException("请选择已完成安装的 Paper / Purpur / Spigot / Folia 插件服务器，原版与模组服不能加载 Bukkit 插件。");
        var installed = manager.List().FirstOrDefault(p => p.Id == profile.Id) ?? throw new InvalidOperationException("服务端不在当前管理目录中。");
        if (installed.Loader != profile.Loader || installed.MinecraftVersion != profile.MinecraftVersion || installed.EntryPoint != profile.EntryPoint)
            throw new InvalidOperationException("服务器配置已变化，请重新选择目标。");
        if (!File.Exists(ServerPaths.Inside(manager.GetDirectory(installed), installed.EntryPoint)))
            throw new InvalidOperationException("服务器启动文件不存在，请先完成安装。");
        var session = manager.GetSession(installed);
        if (session.IsRunning || session.IsInstalling) throw new InvalidOperationException("请先停止服务器，避免下载期间读取插件文件。");
        return ServerPaths.Inside(manager.GetDirectory(installed), "plugins");
    }
    public static string FilePath(ServerManager manager, ServerProfile profile, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName || fileName.Contains(':') || fileName.Contains('/') || fileName.Contains('\\') || !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("服务器插件必须是有效的 JAR 文件名。");
        return ServerPaths.Inside(PluginsDirectory(manager, profile), fileName);
    }

    public static IDisposable ReserveDownload(ServerManager manager, ServerProfile profile)
    {
        var session = manager.GetSession(profile);
        lock (session.SyncRoot)
        {
            _ = PluginsDirectory(manager, profile);
            session.BeginInstall();
            session.Log("下载插件期间暂停启动与配置修改。");
            return new DownloadReservation(session);
        }
    }

    private sealed class DownloadReservation(ServerSession session) : IDisposable
    {
        private ServerSession? _session = session;
        public void Dispose() => Interlocked.Exchange(ref _session, null)?.EndInstall("插件下载任务已结束，等待启动");
    }
}
