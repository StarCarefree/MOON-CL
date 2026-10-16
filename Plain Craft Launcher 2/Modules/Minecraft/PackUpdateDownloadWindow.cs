using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

/// <summary>Modal client-pack picker: downloading does not install or alter the old instance.</summary>
internal sealed class PackUpdateDownloadWindow : Window
{
    private readonly McInstance _instance;
    private readonly ComboBox _sources;
    private readonly ListBox _versions;
    private readonly TextBlock _project, _details, _status;
    private readonly ProgressBar _progress;
    private readonly Button _refresh, _more, _download, _update, _cancel, _website;
    private CancellationTokenSource? _work;
    private PackIdentity? _identity;
    private PackDownloadVersion? _downloadedVersion;
    private int _page;
    private bool _hasMore, _initializing, _closeRequested;
    internal string? SelectedArchive { get; private set; }
    internal PackIdentity? DownloadedIdentity { get; private set; }

    internal PackUpdateDownloadWindow(McInstance instance)
    {
        _instance = instance;
        ServerUi.ApplyTheme(this);
        Title = "MOONCL · 下载整合包更新";
        Width = 780; Height = Math.Min(650, SystemParameters.WorkArea.Height - 50);
        MinWidth = 620; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel();
        header.Children.Add(ServerUi.Text("下载新的客户端整合包", 24));
        _project = ServerUi.Text("正在查找 “" + instance.Name + "” 的对应项目……", 13, muted: true);
        header.Children.Add(_project);
        header.Children.Add(ServerUi.Text("在启动器内选择版本并下载；下载本身不会改动旧版本。网站入口可选。", 12, muted: true));
        var sources = new DockPanel { Margin = new Thickness(0, 8, 0, 12) };
        _refresh = ServerUi.Button("重新查找 / 刷新", async (_, _) => await LoadAsync(false));
        DockPanel.SetDock(_refresh, Dock.Right); sources.Children.Add(_refresh);
        _sources = new ComboBox { MinHeight = 35, Margin = new Thickness(0, 0, 12, 8) };
        sources.Children.Add(_sources); header.Children.Add(sources);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _details = ServerUi.Text("请选择客户端版本。", 12, muted: true); footer.Children.Add(_details);
        _progress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 8) };
        _progress.SetResourceReference(Control.ForegroundProperty, "Hybrid.Accent"); footer.Children.Add(_progress);
        _status = ServerUi.Text("正在准备……", 12); footer.Children.Add(_status);
        var actions = new WrapPanel();
        _website = ServerUi.Button("打开网站（可选）", OpenWebsite);
        _more = ServerUi.Button("加载更多版本", async (_, _) => await LoadAsync(true));
        _download = ServerUi.Button("下载所选版本", Download_Click, primary: true);
        _update = ServerUi.Button("使用此包更新", Update_Click, primary: true);
        _cancel = ServerUi.Button("取消", (_, _) => _work?.Cancel());
        foreach (var button in new[] { _website, _more, _download, _update, _cancel }) actions.Children.Add(button);
        footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _versions = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _versions.SetResourceReference(Control.BackgroundProperty, "Hybrid.Surface");
        _versions.SetResourceReference(Control.ForegroundProperty, "Hybrid.Text");
        _versions.SetResourceReference(Control.BorderBrushProperty, "Hybrid.Line");
        root.Children.Add(_versions); Content = root;
        _versions.SelectionChanged += (_, _) =>
        {
            _details.Text = _versions.SelectedItem is PackDownloadVersion version ? "文件：" + version.FileName + "\n" + version : "请选择客户端版本。";
            UpdateControls();
        };
        _sources.SelectionChanged += async (_, _) => { if (!_initializing && _work is null) await LoadAsync(false); };
        Loaded += async (_, _) => await LoadAsync(false);
        Closing += OnClosing;
        UpdateControls();
    }

    private async Task LoadAsync(bool more)
    {
        if (_work is not null) return;
        _work = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = _work.Token;
        UpdateControls();
        _status.Text = more ? "正在加载更多版本……" : "正在查找项目和客户端版本……";
        _progress.IsIndeterminate = true;
        try
        {
            if (!more)
            {
                _versions.Items.Clear(); _hasMore = false; SelectedArchive = null; DownloadedIdentity = null; _downloadedVersion = null;
                if (_identity is null || _sources.Items.Count == 0)
                {
                    _identity = await Task.Run(() => PackIdentityLookup.ResolveInstalled(_instance), token).WaitAsync(token);
                    PackIdentityStore.Save(_instance.PathInstance, _identity);
                    _project.Text = _identity.Name + "  ·  当前版本 " + _identity.Version;
                    _initializing = true;
                    try
                    {
                        _sources.Items.Clear();
                        foreach (var project in PackUpdateDownloads.Projects(_identity)) _sources.Items.Add(project);
                        if (_sources.Items.Count > 0) _sources.SelectedIndex = 0;
                    }
                    finally { _initializing = false; }
                }
            }
            UpdateControls();
            if (_sources.SelectedItem is not PackDownloadProject target)
            {
                _status.Text = "没有找到 CurseForge / Modrinth 可下载项目。可重新查找，或自愿打开项目网站下载后从设置页导入。";
                return;
            }
            var page = more ? _page + 1 : 0;
            var address = PackUpdateDownloads.Address(target, page);
            var json = await Task.Run(() => ModDownload.DlModRequest(address.AbsoluteUri), token).WaitAsync(token);
            var versions = PackUpdateDownloads.Parse(target, json);
            foreach (var version in versions)
                if (!_versions.Items.Cast<PackDownloadVersion>().Any(v => v.Id == version.Id)) _versions.Items.Add(version);
            _page = page; _hasMore = PackUpdateDownloads.HasMore(target, json, page);
            if (_versions.SelectedIndex < 0 && _versions.Items.Count > 0) _versions.SelectedIndex = 0;
            _status.Text = _versions.Items.Count == 0
                ? "当前列表没有可直接下载且带校验值的客户端包。可加载更多版本、切换来源或选择打开网站。"
                : $"已找到 {_versions.Items.Count} 个客户端版本。请选择需要的版本；测试版 / 预览版请谨慎使用。";
        }
        catch (OperationCanceledException) { _status.Text = "查找已取消或超时，可以重试。"; }
        catch (Exception ex) { Failed("获取版本列表失败", ex); }
        finally { EndWork(); }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || _versions.SelectedItem is not PackDownloadVersion version) return;
        var extension = Path.GetExtension(version.FileName);
        var save = new SaveFileDialog
        {
            Title = "保存客户端整合包", FileName = version.FileName, DefaultExt = extension,
            Filter = extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase) ? "Modrinth 整合包 (*.mrpack)|*.mrpack" : "客户端整合包 (*.zip)|*.zip",
            AddExtension = true, OverwritePrompt = true
        };
        if (save.ShowDialog(this) != true) return;
        if (!Path.GetExtension(save.FileName).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "请保持整合包文件扩展名 " + extension; return;
        }
        await DownloadAsync(version, save.FileName);
    }

    private async Task DownloadAsync(PackDownloadVersion version, string target)
    {
        if (_work is not null) return;
        _work = new CancellationTokenSource(TimeSpan.FromHours(2));
        var token = _work.Token;
        SelectedArchive = null; DownloadedIdentity = null; _downloadedVersion = null;
        _progress.IsIndeterminate = false; _progress.Value = 0;
        UpdateControls();
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MOONCL/2.15 (Minecraft launcher)");
            var transfer = new PackTransfer(http);
            var work = _work;
            var progress = new Progress<double>(value =>
            {
                if (!ReferenceEquals(_work, work)) return;
                _progress.Value = value;
                _status.Text = value >= 1 ? "下载完成，正在校验整合包格式……" : $"正在下载 {version.FileName} · {value:P0}";
            });
            Exception? last = null;
            var downloaded = false;
            foreach (var url in ModDownload.DlSourceModDownloadGet(version.Url).Distinct())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await transfer.DownloadAsync(url, target, version.Hash, version.Algorithm, progress, token);
                    downloaded = true; last = null; break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
            }
            if (last is not null) throw last;
            if (!downloaded || !File.Exists(target)) throw new IOException("下载源未返回文件。");
            var clues = await Task.Run(() => PackCluesReader.Read(target, version.ProjectId, version.Source), token).WaitAsync(token);
            SelectedArchive = target; _downloadedVersion = version;
            DownloadedIdentity = (_identity ?? new PackIdentity()) with
            {
                Source = version.Source,
                Version = clues.Version.Length > 0 ? clues.Version : version.Version,
                MinecraftVersion = clues.MinecraftVersion,
                CurseForgeId = version.Source == "curseforge" ? version.ProjectId : "",
                ModrinthId = version.Source == "modrinth" ? version.ProjectId : "",
                McmodId = ""
            };
            DownloadedIdentity = DownloadedIdentity with { Website = PackUpdateProject.DownloadPage(DownloadedIdentity) };
            _status.Text = "下载和哈希校验完成：" + SelectedArchive + "\n可使用此包更新，也可以关闭窗口稍后导入。旧版本尚未改动。";
        }
        catch (OperationCanceledException) { _status.Text = "下载已取消或超时，未完成的临时文件已清理，可重试。"; }
        catch (Exception ex) { Failed("下载失败，可重试或选择打开网站", ex); }
        finally { EndWork(); }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || SelectedArchive is null || _downloadedVersion is null) return;
        _work = new CancellationTokenSource();
        var token = _work.Token;
        UpdateControls();
        try
        {
            _status.Text = "正在再次核对下载文件……"; _progress.IsIndeterminate = true;
            var path = SelectedArchive; var version = _downloadedVersion;
            var hash = await Task.Run(async () =>
            {
                await using var input = File.OpenRead(path);
                using var algorithm = IncrementalHash.CreateHash(version.Algorithm);
                var buffer = new byte[131072]; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0) algorithm.AppendData(buffer, 0, read);
                return Convert.ToHexString(algorithm.GetHashAndReset());
            }, token);
            if (!hash.Equals(version.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件已发生变化，请重新下载。");
            DialogResult = true;
        }
        catch (OperationCanceledException) { _status.Text = "核对已取消。"; }
        catch (Exception ex) { SelectedArchive = null; DownloadedIdentity = null; Failed("无法使用此文件更新", ex); }
        finally { EndWork(); }
    }

    private void OpenWebsite(object sender, RoutedEventArgs e)
    {
        if (_identity is null) return;
        var url = PackUpdateProject.DownloadPage(_identity);
        if (_sources.SelectedItem is PackDownloadProject project)
            url = PackUpdateProject.DownloadPage(new PackIdentity { ModrinthId = project.Source == "modrinth" ? project.Id : "", CurseForgeId = project.Source == "curseforge" ? project.Id : "" });
        if (url.Length > 0) ModBase.OpenWebsite(url);
    }

    private void UpdateControls()
    {
        var idle = _work is null;
        _sources.IsEnabled = _versions.IsEnabled = _refresh.IsEnabled = idle;
        _more.IsEnabled = idle && _hasMore;
        _download.IsEnabled = idle && _versions.SelectedItem is PackDownloadVersion;
        _update.IsEnabled = idle && SelectedArchive is not null && Equals(_versions.SelectedItem, _downloadedVersion);
        _cancel.IsEnabled = !idle;
        _website.IsEnabled = _identity is not null && PackUpdateProject.DownloadPage(_identity).Length > 0;
    }

    private void EndWork()
    {
        _work?.Dispose(); _work = null; _progress.IsIndeterminate = false;
        UpdateControls();
        if (_closeRequested) Close();
    }

    private void Failed(string message, Exception ex)
    {
        ModBase.Log(ex, message, ModBase.LogLevel.Debug);
        _status.Text = message + "：" + ex.Message + "\n旧版本没有改动。";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_work is null || DialogResult == true) return;
        e.Cancel = true; _closeRequested = true; _work.Cancel();
        _status.Text = "正在取消并清理临时文件……";
    }
}
