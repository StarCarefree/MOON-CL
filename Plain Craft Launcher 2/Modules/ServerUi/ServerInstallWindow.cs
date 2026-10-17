using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.Minecraft.Server;

namespace PCL;

internal sealed class ServerInstallWindow : Window
{
    private readonly ServerProfile _profile;
    private readonly Action _refresh;
    private readonly ServerProfileEditor _editor;
    private readonly TextBox _pack, _installer, _script;
    private readonly TextBlock _installerLabel;
    private readonly CheckBox _trust;
    private readonly FrameworkElement _sources;
    private readonly Button _install, _cancel;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private CancellationTokenSource? _work;
    private bool _saved;

    internal ServerInstallWindow(Action refresh, ServerProfile? profile = null)
    {
        ServerUi.ApplyTheme(this);
        _profile = profile ?? new ServerProfile { Name = "新服务端" };
        _refresh = refresh; _saved = profile is not null;
        Title = profile is null ? "MOONCL · 创建服务端" : "MOONCL · 继续安装服务端";
        Width = 800; Height = Math.Min(860, Math.Max(620, SystemParameters.WorkArea.Height - 70));
        MinWidth = 660; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(26, 20, 26, 10) };
        var title = ServerUi.Text(profile is null ? "创建服务端" : "继续安装服务端", 25); title.FontWeight = FontWeights.SemiBold;
        header.Children.Add(title);
         header.Children.Add(ServerUi.Text("Vanilla / Paper / Purpur / Spigot / Folia / NeoForge / Fabric   ·   独立安装，不影响客户端", 12, muted: true));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(26, 12, 26, 14) };
        _progress = new ProgressBar { Height = 3, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };
        _progress.SetResourceReference(Control.ForegroundProperty, "Hybrid.Accent");
        _progress.SetResourceReference(Control.BackgroundProperty, "Hybrid.Line");
        footer.Children.Add(_progress);
         _status = ServerUi.Text("服务端将保存在当前版本列表所选的 Minecraft 目录中。", 12, muted: true);
         try { ServerUi.UseSelectedGameDirectory(); _status.ToolTip = ServerUi.Manager.GetDirectory(_profile); } catch (Exception ex) { _status.ToolTip = ex.Message; }
         footer.Children.Add(_status);
         _cancel = ServerUi.Button("取消并删除", (_, _) => _work?.Cancel()); _cancel.IsEnabled = false;
        _install = ServerUi.Button("安装并打开控制台", Install_Click, primary: true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(_cancel); actions.Children.Add(_install); footer.Children.Add(actions);
        var footerBorder = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0) };
        footerBorder.SetResourceReference(Border.BorderBrushProperty, "Hybrid.Line");
        footerBorder.SetResourceReference(Border.BackgroundProperty, "Hybrid.Background");
        DockPanel.SetDock(footerBorder, Dock.Bottom); root.Children.Add(footerBorder);

        var panel = new StackPanel { Margin = new Thickness(26, 0, 26, 4) };
        _editor = new ServerProfileEditor(_profile, installing: true); panel.Children.Add(_editor);
        var sources = ServerUi.Section("安装来源", "可以全部留空。NeoForge / Fabric 会下载安装器；原版 / Paper / Purpur 会下载 server.jar，安装时不执行。导入整合包时请选择服务端包。");
        _pack = ServerUi.FileField(sources, "服务端整合包 ZIP · 可选", "服务端整合包 (*.zip)|*.zip");
        _installer = ServerUi.FileField(sources, "本地 JAR · 可选", "Java 归档 (*.jar)|*.jar");
        _installerLabel = (TextBlock)sources.Children[sources.Children.Count - 2];
        _editor.LoaderChanged += UpdateSourceLabels;
        UpdateSourceLabels();
        var scripts = new StackPanel();
        _script = ServerUi.FileField(scripts, "安装脚本 BAT / CMD / PS1", "安装脚本 (*.bat;*.cmd;*.ps1)|*.bat;*.cmd;*.ps1");
        scripts.Children.Add(ServerUi.Text("选择后由脚本负责安装。请使用无需交互输入的脚本；包内 install 脚本也需要下方的信任确认。", 12, muted: true));
        sources.Children.Add(new Expander { Header = ServerUi.Text("使用整合包安装脚本", 13), Content = scripts, Margin = new Thickness(0, 0, 0, 6) });
        _trust = new CheckBox { Content = ServerUi.Text("我信任所选安装器、本地 JAR 及脚本的来源。允许执行安装器/脚本，或把本地 JAR 复制为服务端核心（该 JAR 不会被自动执行）。", 12), Margin = new Thickness(0, 8, 0, 0) };
        sources.Children.Add(_trust);
        _sources = ServerUi.Card(sources); panel.Children.Add(_sources);
        root.Children.Add(ServerUi.Scroll(panel)); Content = root;
        Closing += OnClosing;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        try
        {
            _editor.Apply(_profile);
            if ((!string.IsNullOrWhiteSpace(_script.Text) || !string.IsNullOrWhiteSpace(_installer.Text)) && _trust.IsChecked != true)
                throw new InvalidOperationException("请先确认信任所选本地安装器或脚本。外部程序可执行任意代码。");
             ServerUi.UseSelectedGameDirectory();
             ServerUi.Manager.DownloadSource = ServerUi.DownloadSource();
            ServerUi.Manager.Save(_profile); _saved = true;
            Title = "正在安装 · " + _profile.Name;
            _work = new CancellationTokenSource();
            _editor.IsEnabled = _sources.IsEnabled = _install.IsEnabled = false;
            _cancel.IsEnabled = true; _progress.Visibility = Visibility.Visible; _install.Content = "正在安装…";
            _status.Text = "正在安装到 " + ServerUi.Manager.GetDirectory(_profile) + "。取消会停止安装并删除这次未完成的文件。";
            ServerUi.Open(_profile); _refresh();
            await ServerUi.Manager.InstallAsync(_profile, Optional(_pack.Text), Optional(_installer.Text), Optional(_script.Text), _trust.IsChecked == true, _work.Token);
            var build = string.IsNullOrWhiteSpace(_profile.LoaderVersion) ? "" : "实际构建 " + _profile.LoaderVersion + "。";
            _status.Text = "安装成功。" + build + "请在控制面板确认 EULA 后启动服务端。";
        }
         catch (OperationCanceledException) { _status.Text = "安装已取消，未完成的服务端文件已删除。"; }
        catch (Exception ex) { _status.Text = "安装失败：" + ex.Message; ServerUi.Error(ex); }
        finally
        {
            _work?.Dispose(); _work = null;
            _cancel.IsEnabled = false; _progress.Visibility = Visibility.Collapsed;
            _editor.IsEnabled = _sources.IsEnabled = true;
            _install.IsEnabled = string.IsNullOrWhiteSpace(_profile.EntryPoint);
            _install.Content = _install.IsEnabled ? "安装并打开控制台" : "安装完成";
            ServerUi.Refresh(_profile); _editor.Reload(_profile); _refresh();
        }
    }

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void UpdateSourceLabels() => _installerLabel.Text = ServerCommands.IsDirectCore(_editor.SelectedLoader)
        ? "本地服务端 JAR · 可选，确认信任后复制为 server.jar，不会执行"
        : "本地 installer JAR · 可选，优先使用";

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_work is not null)
        {
            e.Cancel = true;
             if (MessageBox.Show("取消安装并删除这次未完成的文件？", "服务端安装", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _work.Cancel();
        }
        else if (_saved) _refresh();
    }
}

internal sealed class ServerProfileEditor : StackPanel
{
     private readonly TextBox _name, _java, _min, _max, _jvm, _entry;
     private readonly ComboBox _loader, _minecraft, _version;
     private readonly TextBlock _versionLabel, _help, _mirror, _runtimeSummary;
     private TextBlock? _versions;
     private int _versionQuery;
     internal event Action? LoaderChanged;
     internal ServerLoader SelectedLoader => _loader.SelectedItem is LoaderItem item ? item.Loader : ServerLoader.Vanilla;
 
     internal ServerProfileEditor(ServerProfile profile, bool installing = false)
     {
         var identity = ServerUi.Section("版本信息");
         _name = ServerUi.Field(identity, "服务端名称", profile.Name);
         var loader = new StackPanel(); loader.Children.Add(ServerUi.Text("加载器", 12, muted: true));
         _loader = new ComboBox { Margin = new Thickness(0, 0, 0, 10), IsEnabled = installing };
         foreach (ServerLoader item in Enum.GetValues<ServerLoader>()) _loader.Items.Add(new LoaderItem(item));
         _loader.SelectedItem = FindLoader(profile.Loader);
         loader.Children.Add(_loader);
         var minecraft = new StackPanel(); minecraft.Children.Add(ServerUi.Text("Minecraft 版本", 12, muted: true));
         _minecraft = new ComboBox { Margin = new Thickness(0, 0, 0, 10), IsEditable = true, IsEnabled = installing, Text = profile.MinecraftVersion };
         minecraft.Children.Add(_minecraft);
         var version = new StackPanel(); version.Children.Add(ServerUi.Text("加载器版本", 12, muted: true));
         _versionLabel = (TextBlock)version.Children[0];
         _version = new ComboBox { Margin = new Thickness(0, 0, 0, 10), IsEditable = true, IsEnabled = installing, Text = profile.LoaderVersion };
         version.Children.Add(_version);
         identity.Children.Add(Columns(loader, minecraft, version));
         _help = ServerUi.Text("", 11, muted: true); _mirror = ServerUi.Text("", 11, muted: true);
         identity.Children.Add(_help); identity.Children.Add(_mirror);
         if (installing)
         {
             _versions = ServerUi.Text("", 11, muted: true); identity.Children.Add(_versions);
             _loader.SelectionChanged += async (_, _) => { UpdateLoaderHelp(); await LoadGameVersionsAsync(); };
             _minecraft.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(async (_, _) => await LoadLoaderVersionsAsync()));
             _minecraft.SelectionChanged += async (_, _) => await LoadLoaderVersionsAsync();
             Loaded += async (_, _) => await LoadGameVersionsAsync();
         }
         else _loader.SelectionChanged += (_, _) => UpdateLoaderHelp();
         _version.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateLoaderHelp()));
         UpdateLoaderHelp();
         Children.Add(ServerUi.Card(identity));
        var runtime = new StackPanel();
        _java = ServerUi.Field(runtime, "Java 可执行文件 · java 或完整 java.exe 路径", profile.JavaPath);
        runtime.Children.Add(ServerUi.Button("选择 Java…", (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Java (java.exe)|java.exe|可执行文件 (*.exe)|*.exe" };
            if (picker.ShowDialog() == true) _java.Text = picker.FileName;
        }));
        runtime.Children.Add(ServerUi.Text("Java 要求以核心元数据为准：1.20.5–1.21.x 通常为 Java 21，26.x 可能需要 Java 25。使用 java.exe 而非 javaw.exe；记录最低版本后会在启动前检查。", 11, muted: true));
        var min = new StackPanel(); _min = ServerUi.Field(min, "最小内存 MiB", profile.MinMemoryMb.ToString());
        var max = new StackPanel(); _max = ServerUi.Field(max, "最大内存 MiB", profile.MaxMemoryMb.ToString());
        runtime.Children.Add(Columns(min, max));
        _jvm = ServerUi.Field(runtime, "额外 JVM 参数 · 不含内存和入口参数", profile.JvmArguments);
        _entry = installing ? new TextBox { Text = profile.EntryPoint } : ServerUi.Field(runtime, "启动入口 · 相对服务端目录的 JAR / win_args.txt / 脚本", profile.EntryPoint);
        _entry.IsReadOnly = installing;
        var heading = new StackPanel(); heading.Children.Add(ServerUi.Text("运行环境", 16));
        _runtimeSummary = ServerUi.Text("", 12, muted: true); heading.Children.Add(_runtimeSummary);
        Children.Add(ServerUi.Card(new Expander { Header = heading, Content = runtime, IsExpanded = !installing }));
        _java.TextChanged += (_, _) => UpdateRuntimeSummary(); _min.TextChanged += (_, _) => UpdateRuntimeSummary(); _max.TextChanged += (_, _) => UpdateRuntimeSummary();
        UpdateRuntimeSummary();
        if (!installing) Children.Add(ServerUi.Text("游戏和加载器版本由安装文件决定。更换版本请新建服务端；自定义脚本的 JVM 参数由脚本管理。", 12, muted: true));
    }

    private void UpdateRuntimeSummary() => _runtimeSummary.Text = $"{Path.GetFileName(_java.Text)}   ·   {_min.Text} – {_max.Text} MiB   ·   展开调整";

    private static Grid Columns(params FrameworkElement[] fields)
    {
        var grid = new Grid();
        for (var i = 0; i < fields.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            fields[i].Margin = new Thickness(0, 0, i == fields.Length - 1 ? 0 : 14, 0);
            Grid.SetColumn(fields[i], i); grid.Children.Add(fields[i]);
        }
        return grid;
    }

    internal void Reload(ServerProfile profile)
    {
         _name.Text = profile.Name; _loader.SelectedItem = FindLoader(profile.Loader);
         _minecraft.Text = profile.MinecraftVersion; _version.Text = profile.LoaderVersion;
        _java.Text = profile.JavaPath; _min.Text = profile.MinMemoryMb.ToString(); _max.Text = profile.MaxMemoryMb.ToString();
        _jvm.Text = profile.JvmArguments; _entry.Text = profile.EntryPoint;
        UpdateLoaderHelp();
    }

    internal void Apply(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_java.Text)) throw new InvalidOperationException("名称和 Java 不能为空。");
        if (!int.TryParse(_min.Text, out var min) || !int.TryParse(_max.Text, out var max) || min < 256 || max < min || max > 1048576)
            throw new InvalidOperationException("内存需要是有效整数：256 ≤ 最小内存 ≤ 最大内存 ≤ 1048576 MiB。");
        if (_loader.SelectedItem is not LoaderItem loader) throw new InvalidOperationException("请选择加载器。");
        profile.Name = _name.Text.Trim(); profile.Loader = loader.Loader;
         profile.MinecraftVersion = _minecraft.Text.Trim(); profile.LoaderVersion = _version.Text.Trim();
        profile.JavaPath = _java.Text.Trim(); profile.MinMemoryMb = min; profile.MaxMemoryMb = max;
        profile.JvmArguments = _jvm.Text.Trim(); profile.EntryPoint = _entry.Text.Trim();
    }
    private LoaderItem FindLoader(ServerLoader loader)
    {
        foreach (LoaderItem item in _loader.Items) if (item.Loader == loader) return item;
        return (LoaderItem)_loader.Items[0]!;
    }

     private void UpdateLoaderHelp()
     {
         var loader = SelectedLoader;
         var blank = string.IsNullOrWhiteSpace(_version.Text);
         _versionLabel.Text = loader switch
         {
             ServerLoader.Vanilla => "构建 · 原版可留空",
             ServerLoader.Paper => "Paper 构建",
             ServerLoader.Folia => "Folia 构建",
             ServerLoader.Purpur => "Purpur 构建",
             ServerLoader.Spigot => "Spigot 版本",
             ServerLoader.NeoForge => "NeoForge 版本",
             _ => "Fabric 版本"
         };
         _help.Text = loader switch
         {
             ServerLoader.Vanilla => "先选择 Minecraft 版本。构建可留空。安装只下载 server.jar，不启动服务端，也不自动同意 EULA。",
             ServerLoader.Paper or ServerLoader.Folia or ServerLoader.Purpur => (blank ? "构建留空将选择稳定版或最新构建，安装后写入实际构建号。" : "将安装所选构建。") + "安装只下载 server.jar，不执行。",
             ServerLoader.Spigot => "选择 Minecraft 版本后，下拉框会列出 GetBukkit 已提供的 Spigot JAR。安装按目录 SHA-256 校验，不运行 BuildTools。",
             _ => "选择 Minecraft 版本后，下拉框会列出对应的 NeoForge 或 Fabric 版本。"
         };
         _mirror.Text = loader == ServerLoader.Vanilla
             ? "原版可使用 BMCLAPI 镜像，顺序跟随下载源：镜像优先 / 官方优先 / 仅官方。"
             : ServerCoreCatalog.MirrorNotice(loader, 1);
         LoaderChanged?.Invoke();
     }
 
     private async Task LoadGameVersionsAsync()
     {
         if (_versions is null) return;
         var query = ++_versionQuery;
         var loader = SelectedLoader;
         var current = _minecraft.Text;
         try
         {
             ServerUi.Manager.DownloadSource = ServerUi.DownloadSource();
             _versions.Text = "正在查询 Minecraft 版本…";
             var versions = await ServerUi.Manager.ListCoreVersionsAsync(loader);
             if (query != _versionQuery || loader != SelectedLoader) return;
             Fill(_minecraft, versions, current);
             _versions.Text = versions.Count == 0 ? "没有查询到 Minecraft 版本。" : "";
             await LoadLoaderVersionsAsync();
         }
         catch (Exception ex) { if (query == _versionQuery) _versions.Text = "版本查询失败：" + ex.Message; }
     }
 
     private async Task LoadLoaderVersionsAsync()
     {
         if (_versions is null || SelectedLoader == ServerLoader.Vanilla) { if (SelectedLoader == ServerLoader.Vanilla) _version.Items.Clear(); return; }
         var query = _versionQuery;
         var loader = SelectedLoader;
         var minecraft = _minecraft.Text.Trim();
         var current = _version.Text;
         if (minecraft.Length == 0) { _version.Items.Clear(); return; }
         try
         {
             _versions.Text = "正在查询 " + loader + " 版本…";
             var versions = await ServerUi.Manager.ListLoaderVersionsAsync(loader, minecraft);
             if (query != _versionQuery || loader != SelectedLoader || minecraft != _minecraft.Text.Trim()) return;
             Fill(_version, versions, current);
             _versions.Text = versions.Count == 0 ? "这个 Minecraft 版本没有可用的 " + loader + " 版本。" : "";
         }
         catch (Exception ex) { if (query == _versionQuery) _versions.Text = "版本查询失败：" + ex.Message; }
     }
 
     private static void Fill(ComboBox box, IReadOnlyList<string> values, string current)
     {
         box.Items.Clear();
         foreach (var value in values) box.Items.Add(value);
         box.Text = string.IsNullOrWhiteSpace(current) && values.Count > 0 ? values[0] : current;
     }

    private sealed class LoaderItem(ServerLoader loader)
    {
         public ServerLoader Loader { get; } = loader;
         public override string ToString() => loader switch
         {
             ServerLoader.Vanilla => "Vanilla 原版",
             ServerLoader.Paper => "Paper",
             ServerLoader.Purpur => "Purpur",
             ServerLoader.Spigot => "Spigot",
             ServerLoader.Folia => "Folia",
             _ => loader.ToString()
         };
    }
}
