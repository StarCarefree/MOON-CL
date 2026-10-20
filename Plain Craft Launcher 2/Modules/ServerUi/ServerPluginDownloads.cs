using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.Minecraft.Server;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>Native server picker plus the original PCL-MOON DownloadFile/LoaderDownload/Taskbar pipeline.</summary>
internal static class ServerPluginDownloads
{
    internal static string? PreferredServerId { get; set; }

    internal static ServerProfile? Pick(IEnumerable<string> versions, string? preferredDirectory = null)
    {
        var items = ServerUi.Manager.List().Where(p => ServerPluginTargets.SupportsPlugins(p) && !string.IsNullOrWhiteSpace(p.EntryPoint))
            .OrderByDescending(p => p.Id == PreferredServerId || preferredDirectory is not null && Path.GetFullPath(preferredDirectory).TrimEnd('\\', '/').Equals(Path.Combine(ServerUi.Manager.GetDirectory(p), "plugins"), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => ServerPluginTargets.MatchesVersion(p, versions)).ThenBy(p => p.Name).ToArray();
        if (items.Length == 0) throw new InvalidOperationException("尚无可用的插件服务器。请先在服务端管理中安装 Paper 或 Purpur；不能将 Bukkit 插件放进客户端 mods 目录。");
        var dialog = new Window { Title = "选择插件下载目标", Width = 640, Height = 370, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = ModMain.frmMain, ResizeMode = ResizeMode.CanResize };
        ServerUi.ApplyTheme(dialog);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(ServerUi.Text("下载到已有插件服务器", 21));
        root.Children.Add(ServerUi.Text("将使用原有资源版本列表与下载任务；插件属于可执行代码，请确认来源。运行中的服务器请先停止。", 12, muted: true));
        var list = new ComboBox { ItemsSource = items.Select(p => new Target(p, $"{p.Name} · MC {p.MinecraftVersion} · {p.Loader}" + (ServerPluginTargets.MatchesVersion(p, versions) ? "" : "（版本可能不匹配）"))).ToArray(), DisplayMemberPath = nameof(Target.Label), SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 12) };
        root.Children.Add(list);
        var location = ServerUi.Text("", 12, muted: true); root.Children.Add(location);
        void Update() { if (list.SelectedItem is Target target) location.Text = Path.Combine(ServerUi.Manager.GetDirectory(target.Profile), "plugins"); }
        list.SelectionChanged += (_, _) => Update(); Update();
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ServerUi.Button("取消", (_, _) => dialog.DialogResult = false));
        actions.Children.Add(ServerUi.Button("确认目标", (_, _) => {
            try {
                if (list.SelectedItem is not Target target) return;
                _ = ServerPluginTargets.PluginsDirectory(ServerUi.Manager, target.Profile);
                if (!ServerPluginTargets.MatchesVersion(target.Profile, versions) && MessageBox.Show(dialog, "此插件标记的 MC 版本与服务器不同。仍然选择该服务器？", "版本不匹配", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                dialog.DialogResult = true;
            } catch (Exception ex) { ServerUi.Error(ex); }
        }, primary: true));
        root.Children.Add(actions); dialog.Content = ServerUi.Scroll(root);
        return dialog.ShowDialog() == true && list.SelectedItem is Target selected ? selected.Profile : null;
    }
    private sealed record Target(ServerProfile Profile, string Label);

    internal static void Download(ModComp.CompFile file, ModComp.CompProject project, string? preferredDirectory)
    {
        IDisposable? reservation = null;
        try
        {
            if (file.Type != ModComp.CompType.Plugin && file.PluginLoaders.Count == 0) throw new InvalidOperationException("此文件不是服务器插件，请在插件版本列表中重新选择。");
            if (!file.FromCurseForge && file.PluginLoaders.Count == 0) throw new InvalidOperationException("此 Modrinth 文件缺少插件环境信息，请刷新版本列表后重试。");
            var profile = Pick(file.GameVersions, preferredDirectory); if (profile is null) return;
            if (!ServerPluginTargets.SupportsEnvironments(profile, file.PluginLoaders)) throw new InvalidOperationException("此文件仅支持其他服务器环境（例如 Folia 或 Purpur），不能安装到所选核心。");
            var target = ServerPluginTargets.FilePath(ServerUi.Manager, profile, file.FileName);
            if (File.Exists(target) && MessageBox.Show("目标插件已存在。确认覆盖同名文件？\n" + target, "插件下载", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var loaders = new List<ModLoader.LoaderBase>
            {
                new LoaderDownload("下载服务器插件", new List<DownloadFile> { file.ToNetFile(target, ModComp.DownloadReason.Standalone, profile.MinecraftVersion) }) { block = true, ProgressWeight = 6 }
            };
            reservation = ServerPluginTargets.ReserveDownload(ServerUi.Manager, profile);
            var downloadReservation = reservation;
            var task = new ModLoader.LoaderCombo<int>($"下载插件：{project.RawName} → {profile.Name}", loaders)
            {
                OnStateChanged = loader =>
                {
                    if (loader.State is ModBase.LoadState.Finished or ModBase.LoadState.Failed or ModBase.LoadState.Aborted) downloadReservation.Dispose();
                    ModDownloadLib.LoaderStateChangedHintOnly(loader);
                }
            };
            task.Start(1); ModLoader.LoaderTaskbarAdd(task);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh(); ModMain.frmMain.BtnExtraDownload.Ribble();
        }
        catch (Exception ex) { reservation?.Dispose(); ServerUi.Error(ex); }
    }
}
