using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
 using PCL.Core.App;
 using PCL.Core.Minecraft.Launcher;
 using PCL.Core.UI;

namespace PCL;

internal static class InstanceClientUpdate
{
    public static async Task StartAsync(McInstance old, bool deleteOld, bool keepOldSelected)
    {
        if (ModLaunch.IsInstanceLaunching(old))
        {
            HintService.Hint("这个版本正在启动，请稍后再更新。", HintType.Error);
            return;
        }
        var shared = ModFolder.mcFolderSelected;
        var file = SystemDialogs.SelectFile("整合包 (*.zip;*.mrpack)|*.zip;*.mrpack", "选择新的客户端整合包");
        if (string.IsNullOrEmpty(file)) return;
        if (!(file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)))
        {
            HintService.Hint("请选择 .zip 或 .mrpack。", HintType.Error);
            return;
        }
        string newName;
        PackIdentity installed;
        PackIdentity downloaded;
        try
        {
            HintService.Hint("正在读取整合包并核对对应项目……");
            var result = await Task.Run(() =>
            {
                var clues = PackCluesReader.Read(file);
                return (Clues: clues, Installed: PackIdentityLookup.ResolveInstalled(old), Downloaded: PackIdentityLookup.Resolve(clues));
            });
            installed = result.Installed;
            downloaded = result.Downloaded;
            newName = InstanceNames.Next(old.Name, result.Clues.Name, result.Clues.Version, name =>
                Directory.Exists(Path.Combine(shared, "versions", name)));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取更新包失败", ModBase.LogLevel.Debug);
            HintService.Hint("这个压缩包无法读取，旧版本没有改动。", HintType.Error);
            return;
        }
        if (!string.Equals(shared, ModFolder.mcFolderSelected, StringComparison.OrdinalIgnoreCase))
        {
            HintService.Hint("游戏目录已切换，请回到原目录重新选择更新包。", HintType.Error);
            return;
        }
        if (ModLaunch.IsInstanceLaunching(old))
        {
            HintService.Hint("这个版本正在启动，请稍后再更新。", HintType.Error);
            return;
        }
        var match = PackUpdateProject.Compare(installed, downloaded);
        if (match == PackProjectMatch.DifferentProject)
        {
            ModMain.MyMsgBox($"所选文件对应的项目与当前整合包不一致，已停止更新。\n当前项目：{installed.Name}\n文件项目：{downloaded.Name}\n文件：{Path.GetFileName(file)}\n旧版本没有改动。", "整合包项目不匹配");
            return;
        }
        var keepOld = keepOldSelected && !deleteOld;
         var message = $"将安装新版本“{newName}”，成功后移植旧版本的设置、存档、Xaero 地图和 FTB 任务进度。\n新版本会使用自己的游戏目录，这样移植的内容才会被读到。\n新版本没有安装并核对完成前，不会删除旧版本。";
         var projectCheck = match switch
         {
             PackProjectMatch.SameProject => "项目 ID 核对一致。",
             PackProjectMatch.NameMatch => "仅名称匹配，未能通过共同项目 ID 核实。请确认文件来自同一项目。",
             _ => "无法确认文件属于当前项目（可能离线或缺少元数据）。继续前请自行核实，选错包可能导致存档无法使用。"
         };
         message = $"当前项目：{installed.Name} {installed.Version}\n文件项目：{downloaded.Name} {downloaded.Version}\n文件：{Path.GetFileName(file)}\n{projectCheck}\n\n" + message;
         message += deleteOld ? "\n你选择了完成后删除旧版本。只有移植核对成功，且旧版本没有在运行，才会删除。" : "\n旧版本会保留。";
         if (keepOld) message += "\n完成后仍选中旧版本。";
        if (ModMain.MyMsgBox(message, "客户端更新", "开始更新", "取消") != 1) return;
        var oldFolder = old.PathInstance;
        var oldIndie = old.PathIndie;
        var oldName = old.Name;
        ModBase.RunInThread(() =>
        {
            try
            {
                if (!string.Equals(shared, ModFolder.mcFolderSelected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("游戏目录已切换。");
                var loader = ModModpack.ModpackInstall(file, newName, resolvedIdentity: downloaded);
                if (loader is null) throw new InvalidOperationException("没有开始安装。");
                var started = 0;
                loader.OnStateChanged = _ =>
                {
                    if (loader.State is ModBase.LoadState.Failed or ModBase.LoadState.Aborted)
                    {
                        Restore(oldName);
                        Hint("新版本没有安装完成，旧版本已保留。");
                        return;
                    }
                    if (loader.State != ModBase.LoadState.Finished || Interlocked.Exchange(ref started, 1) == 1) return;
                    ModBase.RunInThread(() => Finish(oldFolder, oldIndie, oldName, newName, shared, deleteOld, keepOld));
                };
            }
            catch (ModBase.CancelledException) { Hint("已取消，旧版本没有改动。"); }
            catch (Exception ex)
            {
                ModBase.Log(ex, "客户端更新没有开始", ModBase.LogLevel.Debug);
                Hint("更新没有开始，旧版本没有改动。");
            }
        });
    }

    private static void Finish(string oldFolder, string oldIndie, string oldName, string newName, string shared, bool deleteOld, bool keepOld)
    {
        var newFolder = Path.Combine(shared, "versions", newName);
        try
        {
            if (!InstancePlayerData.IsReady(newFolder, newName))
            {
                Restore(oldName);
                Hint("新版本还没准备好，旧版本已保留。");
                return;
            }
             var receipt = InstancePlayerData.Copy(oldIndie, newFolder);
             InstancePlayerData.WriteReceipt(newFolder, receipt);
             if (!receipt.Verified) throw new IOException("玩家数据核对未通过。");
             Config.Instance.IndieV2[newFolder.TrimEnd('\\', '/') + "\\"] = true;
             var removed = false;
             if (deleteOld && !OldStillRunning(oldFolder))
             {
                 InstancePlayerData.DeleteOldIfSafe(oldFolder, newFolder, newName, true, shared);
                 removed = true;
             }
             var keepBecauseRunning = deleteOld && !removed;
             Restore(removed || !keepOld ? newName : oldName);
             Hint(removed ? $"已更新到 {newName}，并在核对后删除旧版本。" : keepBecauseRunning ? $"已更新到 {newName}，旧版本正在运行，所以没有删除。" : keepOld ? $"已更新到 {newName}，当前仍选中旧版本。" : $"已更新到 {newName}，旧版本已保留。");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "客户端更新移植失败", ModBase.LogLevel.Debug);
            Restore(oldName);
            Hint("新版本没有核对完成，旧版本已保留。");
        }
    }

    private static void Restore(string instanceName)
    {
        try
        {
            ModBase.WriteIni(Path.Combine(ModFolder.mcFolderSelected, "PCL.ini"), "Version", instanceName);
            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected, ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
        }
        catch (Exception ex) { ModBase.Log(ex, "刷新版本选择失败", ModBase.LogLevel.Debug); }
    }

     private static bool OldStillRunning(string oldFolder)
     {
         var folder = oldFolder.TrimEnd('\\', '/');
         return InstanceActivity.Count(oldFolder) > 0 || InstanceActivity.Count(folder) > 0 || InstanceActivity.Count(folder + "\\") > 0 ||
                ModLaunch.LaunchingPaths().Any(path => string.Equals(path.TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase));
     }
 
     private static void Hint(string text) => ModBase.RunInUi(() => HintService.Hint(text, text.Contains("保留") && text.Contains("没有") ? HintType.Error : HintType.Success));
}
