using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.Minecraft.Server;

namespace PCL;

internal sealed class ServerInstallWindow : Window
{
    private readonly ServerProfile _profile;
    private readonly Action _refresh;
    private readonly ServerProfileEditor _editor;
    private readonly TextBox _pack;
    private readonly TextBox _installer;
    private readonly TextBox _script;
    private readonly CheckBox _trust = new() { Content = "我信任所选安装器及脚本的来源，允许执行（脚本拥有当前用户权限）", Margin = new Thickness(0, 8, 0, 14) };
    private readonly Button _install;
    private readonly Button _cancel;
    private readonly TextBlock _status;
    private CancellationTokenSource? _work;
    private bool _saved;

    internal ServerInstallWindow(Action refresh, ServerProfile? profile = null)
    {
        _profile = profile ?? new ServerProfile { Name = "新服务端" };
        _refresh = refresh;
        _saved = profile is not null;
        Title = profile is null ? "一键安装服务端" : "重试服务端安装";
        Width = 660; Height = 800; MinWidth = 530; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(ServerUi.Text("NeoForge / Fabric 服务端", 23));
        panel.Children.Add(ServerUi.Text("填写精确的 Minecraft / 加载器版本，未选择本地 JAR 时自动从官方下载对应安装器。Java 建议使用完整 java.exe 路径，1.20.5+ 通常需要 Java 21。"));
        _editor = new ServerProfileEditor(_profile, installing: true);
        panel.Children.Add(_editor);
        _pack = ServerUi.FileField(panel, "服务端整合包 ZIP（可选；不是客户端包）", "服务端整合包 (*.zip)|*.zip");
        _installer = ServerUi.FileField(panel, "本地加载器安装器 JAR（可选；优先于官方下载）", "Java 安装器 (*.jar)|*.jar");
        _script = ServerUi.FileField(panel, "安装脚本（可选；包内脚本也需下面的信任确认）", "安装脚本 (*.bat;*.cmd;*.ps1)|*.bat;*.cmd;*.ps1");
        panel.Children.Add(ServerUi.Text("NeoForge 调用 --installServer；Fabric 调用 server -downloadMinecraft。选择安装脚本时，由脚本安装。脚本不得需要交互式输入。ZIP 只导入服务端文件，不会将客户端模组自动转换成服务端模组。"));
        panel.Children.Add(_trust);
        var actions = new WrapPanel();
        _install = ServerUi.Button("一键安装并打开控制台", Install_Click);
        _cancel = ServerUi.Button("取消安装", (_, _) => _work?.Cancel());
        _cancel.IsEnabled = false;
        actions.Children.Add(_install); actions.Children.Add(_cancel);
        panel.Children.Add(actions);
        _status = ServerUi.Text("安装和客户端完全隔离，目录：" + ServerUi.Manager.GetDirectory(_profile));
        panel.Children.Add(_status);
        Content = ServerUi.Scroll(panel);
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
            ServerUi.Manager.DownloadSource = ServerUi.DownloadSource();
            ServerUi.Manager.Save(_profile);
            _saved = true;
            _work = new CancellationTokenSource();
            _editor.IsEnabled = _pack.IsEnabled = _installer.IsEnabled = _script.IsEnabled = _trust.IsEnabled = _install.IsEnabled = false;
            _cancel.IsEnabled = true;
            _status.Text = "正在安装，可在控制面板查看实时日志；此窗口可取消。";
            ServerUi.Open(_profile);
            _refresh();
            await ServerUi.Manager.InstallAsync(_profile, Optional(_pack.Text), Optional(_installer.Text), Optional(_script.Text), _trust.IsChecked == true, _work.Token);
            _status.Text = "安装成功。请在控制面板确认 EULA 后启动服务端。";
            _install.Content = "已安装";
        }
        catch (OperationCanceledException) { _status.Text = "安装已取消，可检查日志后重试。"; }
        catch (Exception ex) { _status.Text = "安装失败：" + ex.Message; ServerUi.Error(ex); }
        finally
        {
            _work?.Dispose(); _work = null;
            _cancel.IsEnabled = false;
            _editor.IsEnabled = _pack.IsEnabled = _installer.IsEnabled = _script.IsEnabled = _trust.IsEnabled = true;
            _install.IsEnabled = string.IsNullOrWhiteSpace(_profile.EntryPoint);
            ServerUi.Refresh(_profile);
            _editor.Reload(_profile);
            _refresh();
        }
    }

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_work is not null)
        {
            e.Cancel = true;
            if (MessageBox.Show("安装仍在进行。取消安装？", "服务端安装", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _work.Cancel();
        }
        else if (_saved) _refresh();
    }
}

internal sealed class ServerProfileEditor : StackPanel
{
    private readonly TextBox _name, _minecraft, _version, _java, _min, _max, _jvm, _entry;
    private readonly ComboBox _loader;

    internal ServerProfileEditor(ServerProfile profile, bool installing = false)
    {
        _name = ServerUi.Field(this, "服务端名称", profile.Name);
        Children.Add(ServerUi.Text("加载器"));
        _loader = new ComboBox { ItemsSource = Enum.GetValues<ServerLoader>(), SelectedItem = profile.Loader, Margin = new Thickness(0, 0, 0, 10), MinHeight = 32, IsEnabled = installing };
        Children.Add(_loader);
        _minecraft = ServerUi.Field(this, "Minecraft 版本（例如 1.21.1）", profile.MinecraftVersion);
        _version = ServerUi.Field(this, "加载器版本（例如 NeoForge 21.1.172 / Fabric 0.16.14）", profile.LoaderVersion);
        _minecraft.IsReadOnly = _version.IsReadOnly = !installing;
        _java = ServerUi.Field(this, "Java 可执行文件（java 或完整 java.exe 路径）", profile.JavaPath);
        Children.Add(ServerUi.Button("选择 Java…", (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Java (java.exe)|java.exe|可执行文件 (*.exe)|*.exe" };
            if (picker.ShowDialog() == true) _java.Text = picker.FileName;
        }));
        _min = ServerUi.Field(this, "最小内存（MiB）", profile.MinMemoryMb.ToString());
        _max = ServerUi.Field(this, "最大内存（MiB）", profile.MaxMemoryMb.ToString());
        _jvm = ServerUi.Field(this, "额外 JVM 参数（高级；不要填写 -jar、入口或内存参数）", profile.JvmArguments);
        _entry = ServerUi.Field(this, "启动入口（安装后自动识别；相对服务端目录的 JAR / win_args.txt / 启动脚本）", profile.EntryPoint);
        _entry.IsReadOnly = installing;
        if (!installing) Children.Add(ServerUi.Text("加载器和游戏版本由已安装文件决定；更换版本请新建服务端，勿直接修改。使用自定义启动脚本时，内存与 JVM 参数由脚本自行管理。"));
    }

    internal void Reload(ServerProfile profile)
    {
        _name.Text = profile.Name; _loader.SelectedItem = profile.Loader;
        _minecraft.Text = profile.MinecraftVersion; _version.Text = profile.LoaderVersion;
        _java.Text = profile.JavaPath; _min.Text = profile.MinMemoryMb.ToString(); _max.Text = profile.MaxMemoryMb.ToString();
        _jvm.Text = profile.JvmArguments; _entry.Text = profile.EntryPoint;
    }

    internal void Apply(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_java.Text)) throw new InvalidOperationException("名称和 Java 不能为空。");
        if (!int.TryParse(_min.Text, out var min) || !int.TryParse(_max.Text, out var max) || min < 256 || max < min || max > 1048576)
            throw new InvalidOperationException("内存需要是有效整数：256 ≤ 最小内存 ≤ 最大内存 ≤ 1048576 MiB。");
        profile.Name = _name.Text.Trim(); profile.Loader = (ServerLoader)_loader.SelectedItem;
        profile.MinecraftVersion = _minecraft.Text.Trim(); profile.LoaderVersion = _version.Text.Trim();
        profile.JavaPath = _java.Text.Trim(); profile.MinMemoryMb = min; profile.MaxMemoryMb = max;
        profile.JvmArguments = _jvm.Text.Trim(); profile.EntryPoint = _entry.Text.Trim();
    }
}
