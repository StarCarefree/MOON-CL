using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal static class GameLinkLaunch
{
    internal static GameLinkSession? Prepare(ProcessStartInfo info, McInstance instance)
    {
        GameLinkSession? session = null;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCL.GameLink.agent.jar")
                ?? throw new FileNotFoundException("联机 Agent 尚未打包，请运行 tools/multiplayer-agent/build.ps1 后重新构建启动器");
            using var output = new MemoryStream();
            stream.CopyTo(output);
            var bytes = output.ToArray();
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MOONCL", "game-link");
            Directory.CreateDirectory(root);
            var jar = Path.Combine(root, Convert.ToHexString(SHA256.HashData(bytes)) + ".jar");
            if (!File.Exists(jar) || !SHA256.HashData(File.ReadAllBytes(jar)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            {
                var temporary = jar + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, jar, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            var mappings = PrepareMappingsAsync(instance, root, false);
            var runtime = PrepareMappingsAsync(instance, root, true);
            System.Threading.Tasks.Task.WhenAll(mappings, runtime).GetAwaiter().GetResult();
            session = new GameLinkSession(instance.Name);
            session.Configure(info, jar, mappings.Result, runtime.Result);
            return session;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            ModBase.Log("[GameLink] 运行时联机控制不可用，保留原有启动功能：" + ex.Message);
            return null;
        }
    }
    private static async System.Threading.Tasks.Task<string?> PrepareMappingsAsync(McInstance instance, string root, bool runtime)
    {
        try
        {
            var cache = Path.Combine(root, "mappings");
            if (!runtime) return await GameLinkMappings.PrepareAsync(instance.Info.VanillaName, cache).ConfigureAwait(false);
            var info = instance.Info;
            var kind = info.HasFabric || info.HasQuilt ? "fabric" : info.HasForge || (info.HasNeoForge && info.VanillaName == "1.20.1") ? "forge" : null;
            return kind == null ? null : await GameLinkMappings.PrepareRuntimeAsync(info.VanillaName, kind, cache).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ModBase.Log("[GameLink] " + (runtime ? "加载器" : "官方") + "映射不可用，将尝试已知客户端命名空间：" + ex.Message);
            return null;
        }
    }
}
