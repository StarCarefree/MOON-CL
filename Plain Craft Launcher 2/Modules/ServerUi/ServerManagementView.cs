using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCL.Core.Minecraft.Server;

namespace PCL;

internal sealed class ServerManagementView : UserControl
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _summary = ServerUi.Text("独立目录 · 独立运行环境 · 不影响客户端", 12, muted: true);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _signature = "";

    internal ServerManagementView()
    {
        ServerUi.ApplyTheme(this);
        var root = new DockPanel { Margin = new Thickness(28, 22, 28, 22) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = ServerUi.Text("服务端管理", 26); title.FontWeight = FontWeights.SemiBold;
        header.Children.Add(title); header.Children.Add(_summary);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(ServerUi.Button("＋ 创建服务端", (_, _) => new ServerInstallWindow(Refresh).Show(), primary: true));
        actions.Children.Add(ServerUi.Button("打开服务端目录", (_, _) =>
        {
            try { ServerUi.Folder(ServerUi.Manager.RootDirectory); } catch (Exception ex) { ServerUi.Error(ex); }
        }));
        actions.Children.Add(ServerUi.Button("刷新列表", (_, _) => Refresh()));
        header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(ServerUi.Scroll(_list)); Content = root;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        try
        {
            var profiles = ServerUi.Manager.List();
            var signature = string.Join("|", profiles.Select(p => $"{p.Id}:{p.Name}:{p.Loader}:{p.MinecraftVersion}:{p.LoaderVersion}:{p.EntryPoint}:{ServerUi.Manager.GetSession(p).Status}"));
            if (_signature == signature && _list.Children.Count > 0) return;
            _signature = signature; _list.Children.Clear();
            var running = profiles.Count(p => ServerUi.Manager.GetSession(p).IsRunning);
            _summary.Text = profiles.Count == 0 ? "服务端和客户端分开管理，安装与设置互不影响。" : $"{profiles.Count} 个服务端 · {running} 个运行中 · 独立于客户端";
            if (profiles.Count == 0)
            {
                var empty = ServerUi.Section("创建你的第一个服务端", "选择 NeoForge 或 Fabric，自动下载加载器安装器；也可以导入作者提供的服务端整合包。");
                empty.Margin = new Thickness(12, 18, 12, 18);
                empty.Children.Add(ServerUi.Text("控制台、Java、内存和 server.properties 都会独立保存。关闭控制面板不会停服。", 12, muted: true));
                _list.Children.Add(ServerUi.Card(empty));
                return;
            }
            foreach (var profile in profiles) _list.Children.Add(InstanceCard(profile));
            _list.Children.Add(ServerUi.Text("关闭控制面板不会停止服务端。退出启动器前，请在控制台安全停服。", 12, muted: true));
        }
        catch (Exception ex)
        {
            _list.Children.Clear();
            _list.Children.Add(ServerUi.Card(ServerUi.Text("读取服务端失败：" + ex.Message)));
        }
    }

    private FrameworkElement InstanceCard(ServerProfile profile)
    {
        var session = ServerUi.Manager.GetSession(profile);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = ServerUi.Icon(profile.Loader, 48); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 6, 18, 0);
        body.Children.Add(icon);
        var details = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        var name = ServerUi.Text(profile.Name, 17); name.FontWeight = FontWeights.SemiBold;
        details.Children.Add(name);
        details.Children.Add(ServerUi.Text($"Minecraft {profile.MinecraftVersion}   /   {profile.Loader} {profile.LoaderVersion}", 12, muted: true));
        var status = ServerUi.Text(session.IsInstalling || session.IsRunning ? session.Status : string.IsNullOrEmpty(profile.EntryPoint) ? "待安装 · 可在控制面板继续" : session.Status, 11, muted: !session.IsRunning);
        status.Margin = new Thickness(0, 4, 0, 0);
        if (session.IsRunning) status.SetResourceReference(TextBlock.ForegroundProperty, "Hybrid.Accent");
        details.Children.Add(status);
        Grid.SetColumn(details, 1); body.Children.Add(details);
        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 210 };
        var launch = ServerUi.Button(session.IsRunning ? "运行中" : "启动", async (_, _) =>
        {
            try
            {
                ServerUi.Open(profile);
                if (!session.IsRunning && ServerUi.ConfirmEula(profile)) await session.StartAsync();
            }
            catch (Exception ex) { ServerUi.Error(ex); }
            Refresh();
        }, primary: !session.IsRunning);
        launch.IsEnabled = !session.IsInstalling && !session.IsRunning && !string.IsNullOrWhiteSpace(profile.EntryPoint);
        launch.ToolTip = string.IsNullOrWhiteSpace(profile.EntryPoint) ? "请先在控制面板完成安装" : "打开独立控制台并启动服务端";
        actions.Children.Add(launch);
        actions.Children.Add(ServerUi.Button("控制面板", (_, _) => ServerUi.Open(profile)));
        Grid.SetColumn(actions, 2); body.Children.Add(actions);
        return ServerUi.Card(body);
    }
}
