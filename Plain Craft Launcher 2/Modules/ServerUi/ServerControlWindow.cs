using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.Minecraft.Server;

namespace PCL;

internal sealed class ServerControlWindow : Window
{
    private ServerProfile _profile;
    private readonly ServerSession _session;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBlock _status = ServerUi.Text("");
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 13,
        Background = new SolidColorBrush(Color.FromRgb(22, 26, 35)), Foreground = Brushes.WhiteSmoke,
        Padding = new Thickness(12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBox _command = new() { Padding = new Thickness(8), MinHeight = 34, Margin = new Thickness(0, 0, 8, 8) };
    private readonly Button _start, _stop, _kill, _send, _retry;
    private readonly ServerProfileEditor _editor;
    private readonly ServerPropertiesEditor _properties;
    private readonly StackPanel _settings;
    private string _lastLog = "";
    private bool _wasBusy;

    internal ServerControlWindow(ServerProfile profile)
    {
        _profile = profile;
        _session = ServerUi.Manager.GetSession(profile);
        Title = profile.Name + " · 服务端控制面板";
        Width = 1000; Height = 740; MinWidth = 680; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new DockPanel();
        var icon = ServerUi.Icon(profile.Loader, 42); DockPanel.SetDock(icon, Dock.Left); header.Children.Add(icon);
        var info = new StackPanel();
        info.Children.Add(ServerUi.Text(profile.Name + " · " + profile.Loader, 22));
        info.Children.Add(_status); header.Children.Add(info);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var actions = new WrapPanel();
        _start = ServerUi.Button("启动服务端", async (_, _) =>
        {
            try { if (ServerUi.ConfirmEula(_profile)) await _session.StartAsync(); } catch (Exception ex) { ServerUi.Error(ex); }
            Update();
        });
        _stop = ServerUi.Button("安全停止（stop）", async (_, _) =>
        {
            try { await _session.StopAsync(); } catch (Exception ex) { ServerUi.Error(ex); }
            Update();
        });
        _kill = ServerUi.Button("强制结束", (_, _) =>
        {
            if (MessageBox.Show("强制结束可能导致世界存档损坏。确定继续？", "强制结束服务端", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { _session.Kill(); } catch (Exception ex) { ServerUi.Error(ex); }
        });
        _retry = ServerUi.Button("重试安装", (_, _) => new ServerInstallWindow(Update, _profile).Show());
        actions.Children.Add(_start); actions.Children.Add(_stop); actions.Children.Add(_kill); actions.Children.Add(_retry);
        actions.Children.Add(ServerUi.Button("打开目录", (_, _) => { try { ServerUi.Folder(ServerUi.Manager.GetDirectory(_profile)); } catch (Exception ex) { ServerUi.Error(ex); } }));
        DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        var tabs = new TabControl();
        var console = new DockPanel { Margin = new Thickness(10) };
        var commandRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _send = ServerUi.Button("发送命令", (_, _) => Send()); DockPanel.SetDock(_send, Dock.Right); commandRow.Children.Add(_send);
        _command.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Send(); e.Handled = true; } };
        commandRow.Children.Add(_command); DockPanel.SetDock(commandRow, Dock.Bottom); console.Children.Add(commandRow); console.Children.Add(_log);
        tabs.Items.Add(new TabItem { Header = "实时控制台", Content = console });
        _properties = new ServerPropertiesEditor(_profile);
        tabs.Items.Add(new TabItem { Header = "server.properties 配置", Content = _properties });
        _settings = new StackPanel { Margin = new Thickness(18) };
        _editor = new ServerProfileEditor(_profile);
        _settings.Children.Add(_editor);
        _settings.Children.Add(ServerUi.Button("保存版本设置", (_, _) =>
        {
            try
            {
                var next = JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(_profile))!;
                _editor.Apply(next); ServerUi.Manager.Save(next); _profile = next;
                Title = next.Name + " · 服务端控制面板";
                MessageBox.Show("版本设置已保存，下次启动生效。", "服务端管理");
            }
            catch (Exception ex) { ServerUi.Error(ex); }
        }));
        tabs.Items.Add(new TabItem { Header = "版本设置", Content = ServerUi.Scroll(_settings) });
        root.Children.Add(tabs); Content = root;
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => { Update(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void Send()
    {
        try { _session.SendCommand(_command.Text); _command.Clear(); } catch (Exception ex) { ServerUi.Error(ex); }
    }

    internal void ReloadProfile()
    {
        try
        {
            _profile = ServerUi.Manager.List().FirstOrDefault(p => p.Id == _profile.Id) ?? _profile;
            _editor.Reload(_profile);
            _properties.Reload();
            Title = _profile.Name + " · 服务端控制面板";
        }
        catch (Exception ex) { _status.Text = "刷新设置失败：" + ex.Message; }
    }

    private void Update()
    {
        _status.Text = $"{_session.Status} · Minecraft {_profile.MinecraftVersion} · {_profile.LoaderVersion}\n{ServerUi.Manager.GetDirectory(_profile)}";
        var busy = _session.IsRunning || _session.IsInstalling;
        if (_wasBusy && !busy) ReloadProfile();
        _wasBusy = busy;
        _start.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_profile.EntryPoint);
        _stop.IsEnabled = _send.IsEnabled = _command.IsEnabled = _session.IsRunning && !_session.IsInstalling;
        _kill.IsEnabled = _session.IsRunning && !_session.IsInstalling;
        _retry.IsEnabled = !busy && string.IsNullOrWhiteSpace(_profile.EntryPoint);
        _settings.IsEnabled = _properties.IsEnabled = !busy;
        var log = string.Join(Environment.NewLine, _session.GetLog());
        if (log == _lastLog) return;
        var follow = _log.VerticalOffset >= _log.ExtentHeight - _log.ViewportHeight - 24;
        _lastLog = log; _log.Text = log;
        if (follow) _log.ScrollToEnd();
    }
}
