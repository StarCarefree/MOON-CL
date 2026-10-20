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
    private readonly TreeView _versions;
    private readonly TextBox _search;
    private readonly ComboBox _sort;
    private readonly CheckBox _onlyCurrent;
    private readonly TextBlock _summary;
    private readonly List<PackDownloadVersion> _allVersions = [];
    private readonly Dictionary<string, bool> _expandedGroups = new(StringComparer.Ordinal);
    private PackDownloadVersion? SelectedVersion => (_versions.SelectedItem as TreeViewItem)?.Tag as PackDownloadVersion;
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
        Title = "PCL-MOON · 下载整合包更新";
        Width = 820; Height = Math.Min(750, SystemParameters.WorkArea.Height - 50);
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
        header.Children.Add(ServerUi.Text("搜索版本 / 文件名 / MC / 加载器，例如：1.21.11 Fabric", 11, muted: true));
        var filters = new Grid();
        filters.ColumnDefinitions.Add(new ColumnDefinition());
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        _search = new TextBox { MinHeight = 35, MaxLength = 160, Margin = new Thickness(0, 0, 10, 8), ToolTip = "空格分隔多个关键词；MC 版本精确匹配" };
        _sort = new ComboBox { MinHeight = 35, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var label in new[] { "最新发布", "最早发布", "名称 A–Z", "MC 版本（高→低）" }) _sort.Items.Add(label);
        _sort.SelectedIndex = 0; Grid.SetColumn(_sort, 1);
        filters.Children.Add(_search); filters.Children.Add(_sort); header.Children.Add(filters);
        var grouping = new WrapPanel();
        _onlyCurrent = new CheckBox { Content = "仅显示当前 MC", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 8) };
        grouping.Children.Add(_onlyCurrent);
        grouping.Children.Add(ServerUi.Button("全部展开", (_, _) => SetGroupsExpanded(true)));
        grouping.Children.Add(ServerUi.Button("全部折叠", (_, _) => SetGroupsExpanded(false)));
        header.Children.Add(grouping);
        _summary = ServerUi.Text("版本将按 Minecraft 版本分组。", 11, muted: true); header.Children.Add(_summary);
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
        _versions = new TreeView { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _versions.SetResourceReference(Control.BackgroundProperty, "Hybrid.Surface");
        _versions.SetResourceReference(Control.ForegroundProperty, "Hybrid.Text");
        _versions.SetResourceReference(Control.BorderBrushProperty, "Hybrid.Line");
        root.Children.Add(_versions); Content = root;
        _versions.SelectedItemChanged += (_, _) =>
        {
            _details.Text = SelectedVersion is { } version ? "文件：" + version.FileName + "\n" + version : "请选择客户端版本（展开 MC 分组）。";
            UpdateControls();
        };
        _sources.SelectionChanged += async (_, _) => { if (!_initializing && _work is null) await LoadAsync(false); };
        _search.TextChanged += (_, _) => ApplyFilters();
        _sort.SelectionChanged += (_, _) => ApplyFilters();
        _onlyCurrent.Checked += (_, _) => ApplyFilters();
        _onlyCurrent.Unchecked += (_, _) => ApplyFilters();
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
                _allVersions.Clear(); _versions.Items.Clear(); _expandedGroups.Clear(); _hasMore = false;
                SelectedArchive = null; DownloadedIdentity = null; _downloadedVersion = null;
                if (_identity is null || _sources.Items.Count == 0)
                {
                    _identity = await Task.Run(() => PackIdentityLookup.ResolveInstalled(_instance), token).WaitAsync(token);
                    PackIdentityStore.Save(_instance.PathInstance, _identity);
                    _project.Text = _identity.Name + "  ·  MC " + (_identity.MinecraftVersion.Length > 0 ? _identity.MinecraftVersion : "未知") +
                        "  ·  整合包版本 " + (_identity.Version.Length > 0 ? _identity.Version : "未知（不使用远端版本替代）");
                    _onlyCurrent.Content = "仅显示当前 MC " + _identity.MinecraftVersion;
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
                if (!_allVersions.Any(v => v.Id == version.Id)) _allVersions.Add(version);
            _page = page; _hasMore = PackUpdateDownloads.HasMore(target, json, page);
            ApplyFilters();
            _status.Text = _allVersions.Count == 0
                ? "当前页没有可直接下载且带校验值的客户端包，可加载更多版本或切换来源。"
                : $"已加载 {_allVersions.Count} 个客户端版本。搜索和排序作用于已加载版本；测试版 / 预览版请谨慎使用。";
        }
        catch (OperationCanceledException) { _status.Text = "查找已取消或超时，可以重试。"; }
        catch (Exception ex) { Failed("获取版本列表失败", ex); }
        finally { EndWork(); }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null || SelectedVersion is not { } version) return;
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
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-MOON/2.15 (Minecraft launcher)");
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
        if (_work is not null || SelectedArchive is null || _downloadedVersion is null || SelectedVersion != _downloadedVersion) return;
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

    private void ApplyFilters()
    {
        var selected = SelectedVersion;
        var current = _identity?.MinecraftVersion ?? "";
        var groups = PackDownloadBrowser.Browse(_allVersions, _search.Text, _onlyCurrent.IsChecked == true ? current : "",
            (PackDownloadSort)Math.Max(0, _sort.SelectedIndex));
        _versions.Items.Clear();
        TreeViewItem? selection = null;
        foreach (var group in groups)
        {
            var expanded = _search.Text.Trim().Length > 0 || (_expandedGroups.TryGetValue(group.Minecraft, out var saved) ? saved
                : group.Minecraft == current || current.Length == 0 && _versions.Items.Count == 0);
            var node = new TreeViewItem
            {
                Header = $"Minecraft {group.Minecraft}" + (group.Minecraft == current ? " · 当前版本" : "") + $"（{group.Versions.Count}）",
                Tag = group.Minecraft, IsExpanded = expanded, Padding = new Thickness(5, 8, 5, 8)
            };
            node.Expanded += (_, e) => { if (e.OriginalSource == node) _expandedGroups[group.Minecraft] = true; };
            node.Collapsed += (_, e) => { if (e.OriginalSource == node) _expandedGroups[group.Minecraft] = false; };
            foreach (var version in group.Versions)
            {
                var row = new TreeViewItem { Header = new TextBlock { Text = version.ToString(), TextWrapping = TextWrapping.Wrap, MaxWidth = 660 },
                    Tag = version, ToolTip = version.FileName, Padding = new Thickness(6, 8, 6, 8) };
                node.Items.Add(row);
                if (Equals(version, selected)) selection = row;
                else if (selection is null && node.IsExpanded) selection = row;
            }
            _versions.Items.Add(node);
        }
        if (selection is not null) selection.IsSelected = true;
        var count = groups.SelectMany(g => g.Versions).Select(v => v.Id).Distinct().Count();
        _summary.Text = count == 0 ? $"没有匹配版本（已加载 {_allVersions.Count} 个）。可清空搜索、取消“仅当前 MC”或加载更多。"
            : $"显示 {count} / {_allVersions.Count} 个版本，{groups.Count} 个 MC 分组。点击分组箭头展开或折叠。";
        UpdateControls();
    }

    private void SetGroupsExpanded(bool expanded)
    {
        foreach (TreeViewItem node in _versions.Items)
        {
            _expandedGroups[(string)node.Tag] = expanded;
            node.IsExpanded = expanded;
        }
    }

    private void UpdateControls()
    {
        var idle = _work is null;
        _sources.IsEnabled = _versions.IsEnabled = _refresh.IsEnabled = _search.IsEnabled = _sort.IsEnabled = idle;
        _onlyCurrent.IsEnabled = idle && !string.IsNullOrEmpty(_identity?.MinecraftVersion);
        _more.IsEnabled = idle && _hasMore;
        _download.IsEnabled = idle && SelectedVersion is not null;
        _update.IsEnabled = idle && SelectedArchive is not null && Equals(SelectedVersion, _downloadedVersion);
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
