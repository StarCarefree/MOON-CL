using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.Minecraft.Server;

namespace PCL;

internal sealed class ServerManagementView : UserControl
{
    private readonly StackPanel _list = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _signature = "";

    internal ServerManagementView()
    {
        var root = new DockPanel { Margin = new Thickness(25, 16, 25, 15) };
        var header = new StackPanel();
        header.Children.Add(ServerUi.Text("服务端管理", 23));
        header.Children.Add(ServerUi.Text("与客户端完全分开 · 独立 Java、内存、控制台和配置 · 关闭控制面板不会停服"));
        var actions = new WrapPanel();
        actions.Children.Add(ServerUi.Button("＋ 新建 / 一键安装", (_, _) => new ServerInstallWindow(Refresh).Show()));
        actions.Children.Add(ServerUi.Button("打开服务端目录", (_, _) => { try { ServerUi.Folder(ServerUi.Manager.RootDirectory); } catch (Exception ex) { ServerUi.Error(ex); } }));
        actions.Children.Add(ServerUi.Button("刷新", (_, _) => Refresh()));
        header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(ServerUi.Scroll(_list));
        Content = root;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        try
        {
            var profiles = ServerUi.Manager.List();
            var signature = string.Join("|", profiles.Select(p => $"{p.Id}:{p.Name}:{p.Loader}:{p.MinecraftVersion}:{p.LoaderVersion}:{ServerUi.Manager.GetSession(p).Status}"));
            if (_signature == signature && _list.Children.Count > 0) return;
            _signature = signature;
            _list.Children.Clear();
            if (profiles.Count == 0)
            {
                _list.Children.Add(ServerUi.Text("还没有服务端。点击“一键安装”，选择 NeoForge / Fabric，导入服务端整合包 ZIP 或下载官方安装器。\n客户端整合包不能直接作为服务端包运行。", 15));
                return;
            }
            foreach (var profile in profiles)
            {
                var session = ServerUi.Manager.GetSession(profile);
                var body = new DockPanel { Margin = new Thickness(14) };
                var icon = ServerUi.Icon(profile.Loader);
                DockPanel.SetDock(icon, Dock.Left);
                body.Children.Add(icon);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                actions.Children.Add(ServerUi.Button("控制面板 / 设置", (_, _) => ServerUi.Open(profile)));
                actions.Children.Add(ServerUi.Button(session.IsRunning ? "查看运行中服务端" : "启动", async (_, _) =>
                {
                    try
                    {
                        ServerUi.Open(profile);
                        if (!session.IsRunning && ServerUi.ConfirmEula(profile)) await session.StartAsync();
                    }
                    catch (Exception ex) { ServerUi.Error(ex); }
                    Refresh();
                }));
                DockPanel.SetDock(actions, Dock.Right);
                body.Children.Add(actions);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(ServerUi.Text(profile.Name, 17));
                text.Children.Add(ServerUi.Text($"{profile.Loader} {profile.LoaderVersion} · Minecraft {profile.MinecraftVersion} · {session.Status}"));
                body.Children.Add(text);
                var card = new Border { Child = body, CornerRadius = new CornerRadius(8), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 5, 0, 8) };
                _list.Children.Add(card);
            }
        }
        catch (Exception ex)
        {
            _list.Children.Clear();
            _list.Children.Add(ServerUi.Text("读取服务端失败：" + ex.Message));
        }
    }
}
