using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using PCL.Core.Multiplayer;

namespace PCL;

public partial class HybridRooms
{
    private bool _awaitingEasyTierDownload;
    private DateTime _easyTierDownloadStarted;
    private ModLoader.LoaderCombo<System.Text.Json.Nodes.JsonObject>? _easyTierPreviousDownload;
    private string _easyTierPathBeforeDownload = "";

    private async void CheckEasyTier_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        DiscoverTools();
        EasyTierToolStatus.Text = "正在检查 EasyTier Core 版本……";
        try
        {
            var info = await EasyTierToolSupport.CheckAsync(EasyTierPath.Text.Trim(), ct);
            Guard(ct);
            EasyTierToolStatus.Text = info.Summary;
            Status.Text = info.Summary + "。此检查不加入网络；连接线路时才启动组网进程。";
            SaveSettings();
        }
        catch (Exception ex) { if (!_closed) EasyTierToolStatus.Text = Error(ex); throw; }
    });

    private void GetEasyTier_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _closed || _busy || _awaitingEasyTierDownload) return;
        try
        {
            if (ModMain.frmMain is null) throw new InvalidOperationException("请在启动器主窗口中获取 EasyTier，或手动选择已安装的程序。");
            var active = ModLink.dlEasyTierLoader;
            if (active?.State == ModBase.LoadState.Loading)
            {
                _easyTierPreviousDownload = null;
                BeginEasyTierDownloadWatch();
                EasyTierToolStatus.Text = "正在使用已有的 EasyTier 下载任务，可在启动器下载任务中查看进度。";
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                "将通过启动器已有的下载源获取 EasyTier 2.6.4 并解压到工具目录。\n不会自动启动工具、加入网络或修改当前连接。\n\n是否继续？",
                "获取 EasyTier 工具", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            _easyTierPreviousDownload = active;
            ModLink.DownloadEasyTier();
            BeginEasyTierDownloadWatch();
            EasyTierToolStatus.Text = "EasyTier 已加入后台下载任务；可在启动器任务区取消。下载完成后自动填入路径。";
            Status.Text = "EasyTier 工具正在后台获取；房间心跳和已有连接继续运行。";
        }
        catch (Exception ex) { EasyTierToolStatus.Text = Error(ex); Status.Text = Error(ex); }
    }
    private void BeginEasyTierDownloadWatch()
    {
        _awaitingEasyTierDownload = true;
        _easyTierDownloadStarted = DateTime.UtcNow;
        _easyTierPathBeforeDownload = EasyTierPath.Text;
    }
    private void RefreshEasyTierDownload()
    {
        if (!_awaitingEasyTierDownload || _closed) return;
        var loader = ModLink.dlEasyTierLoader;
        if (loader is null || ReferenceEquals(loader, _easyTierPreviousDownload))
        {
            if (DateTime.UtcNow - _easyTierDownloadStarted > TimeSpan.FromSeconds(20))
            {
                _awaitingEasyTierDownload = false;
                EasyTierToolStatus.Text = "未能建立 EasyTier 下载任务。请检查启动器任务列表，或手动选择已安装的工具。";
            }
            return;
        }
        switch (loader.State)
        {
            case ModBase.LoadState.Loading:
                EasyTierToolStatus.Text = "EasyTier 后台下载中，可在启动器任务区查看或取消。";
                break;
            case ModBase.LoadState.Finished:
                _awaitingEasyTierDownload = false;
                try
                {
                    var executable = Path.Combine(PCL.Core.Link.Scaffolding.EasyTier.EasyTierMetadata.EasyTierFilePath, "easytier-core.exe");
                    EasyTierToolSupport.ValidateExecutable(executable);
                    if (EasyTierPath.Text == _easyTierPathBeforeDownload) EasyTierPath.Text = executable;
                    SaveSettings();
                    EasyTierToolStatus.Text = "EasyTier 获取完成。点击检测工具确认版本后，即可发布或连接 EasyTier 线路。";
                }
                catch (Exception ex) { EasyTierToolStatus.Text = Error(ex); }
                break;
            case ModBase.LoadState.Failed:
            case ModBase.LoadState.Aborted:
                _awaitingEasyTierDownload = false;
                EasyTierToolStatus.Text = "EasyTier 下载失败或已取消；已有房间连接不受影响，可重试或手动选择工具。";
                break;
        }
    }
}
