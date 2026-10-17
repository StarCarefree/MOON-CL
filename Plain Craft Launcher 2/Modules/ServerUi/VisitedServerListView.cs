using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PCL.Core.Link.McPing;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal sealed class VisitedServerListView : UserControl
{
    private const int PageSize = 10;
    private readonly StackPanel _list = new();
    private readonly TextBlock _summary = ServerUi.Text("读取各游戏实例里保存的多人服务器", 11, muted: true);
    private readonly TextBox _search = new() { Padding = new Thickness(8, 4, 8, 4), Height = 32, MinHeight = 30, ToolTip = "搜索服务器名称、地址或来源实例" };
    private readonly TextBlock _pageLabel = ServerUi.Text("", 11, muted: true);
    private readonly Button _previous;
    private readonly Button _next;
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<string, ServerStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _querying = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _pingGate = new(4);
    private Func<VisitedServer, Task<ServerStatus>> _ping = PingAsync;
    private List<VisitedServer> _servers = [];
    private int _load, _page;

    internal VisitedServerListView()
    {
        ServerUi.ApplyTheme(this);
        var root = new DockPanel { Margin = new Thickness(16, 12, 16, 10) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var heading = new DockPanel();
        var refresh = CompactButton("刷新状态", async (_, _) => await LoadAsync(true), primary: true);
        refresh.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(refresh, Dock.Right); heading.Children.Add(refresh);
        var title = ServerUi.Text("服务器", 20); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0);
        heading.Children.Add(title); header.Children.Add(heading);
        _summary.Margin = new Thickness(0, 4, 0, 6); header.Children.Add(_summary);
        AutomationProperties.SetName(_search, "搜索服务器名称、地址或来源实例");
        _search.TextChanged += (_, _) => { _page = 0; Render(false); _scroll.ScrollToHome(); };
        var searchRow = new DockPanel();
        var searchLabel = ServerUi.Text("搜索", 11, muted: true);
        searchLabel.Margin = new Thickness(0, 0, 8, 0); searchLabel.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(searchLabel, Dock.Left); searchRow.Children.Add(searchLabel);
        searchRow.Children.Add(_search); header.Children.Add(searchRow);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var pager = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _previous = CompactButton("上一页", (_, _) => ChangePage(-1));
        _next = CompactButton("下一页", (_, _) => ChangePage(1));
        _previous.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(_previous, Dock.Left); pager.Children.Add(_previous);
        DockPanel.SetDock(_next, Dock.Right); pager.Children.Add(_next);
        _pageLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _pageLabel.VerticalAlignment = VerticalAlignment.Center;
        _pageLabel.Margin = new Thickness(0); pager.Children.Add(_pageLabel);
        DockPanel.SetDock(pager, Dock.Bottom); root.Children.Add(pager);
        _scroll = ServerUi.Scroll(_list); root.Children.Add(_scroll); Content = root;
        IsVisibleChanged += async (_, _) => { if (IsVisible) await LoadAsync(false); };
    }

    private async Task LoadAsync(bool ping)
    {
        var token = Interlocked.Increment(ref _load);
        _summary.Text = "正在读取服务器…";
        try
        {
            var instances = Instances();
            var servers = await Task.Run(() => VisitedServerCatalog.Load(instances).ToList());
            if (token != _load) return;
            _servers = servers;
            Render(false);
            if (ping) await Task.WhenAll(servers.Select(QueryServerAsync));
        }
        catch (Exception ex)
        {
            if (token == _load) _summary.Text = "读取服务器失败：" + ex.Message;
        }
    }

    private static VisitedServerCatalog.InstanceRoot[] Instances()
    {
        if (ModInstanceList.mcInstanceList is null) return [];
        return ModInstanceList.mcInstanceList.ToArray()
            .Where(card => card.Key != McInstanceCardType.Hidden)
            .SelectMany(card => card.Value.ToArray())
            .Where(instance => instance is not null && !string.IsNullOrWhiteSpace(instance.PathIndie))
            .Select(instance => new VisitedServerCatalog.InstanceRoot(instance.Name, instance.PathIndie, instance.Info?.VanillaName ?? ""))
            .ToArray();
    }

    private List<VisitedServer> FilteredServers()
    {
        var query = _search.Text.Trim();
        return string.IsNullOrEmpty(query) ? _servers
            : _servers.Where(server => Contains(server.Name, query) || Contains(server.Address, query) || Contains(server.InstanceName, query)).ToList();
    }

    private void ChangePage(int delta)
    {
        var pages = Math.Max(1, (FilteredServers().Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page + delta, 0, pages - 1);
        Render(false);
        _scroll.ScrollToHome();
    }

    private void UpdateSummary()
    {
        var shown = FilteredServers().Count;
        var online = _servers.Count(server => _statuses.TryGetValue(VisitedServerCatalog.Key(server.Address), out var status) && status.Online);
        _summary.Text = _servers.Count == 0 ? "还没有保存的服务器，在游戏里添加后可刷新。"
            : $"{shown} / {_servers.Count} 个服务器" + (_statuses.Count > 0 ? $" · {online} 个在线" : "");
    }

    private void Render(bool keepSummary)
    {
        var shown = FilteredServers();
        var pages = Math.Max(1, (shown.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        _previous.IsEnabled = _page > 0;
        _next.IsEnabled = _page + 1 < pages;
        _pageLabel.Text = $"第 {_page + 1} / {pages} 页 · 每页 {PageSize} 条";
        _list.Children.Clear();
        if (!keepSummary) UpdateSummary();
        if (shown.Count == 0)
        {
            var empty = ServerUi.Section(_servers.Count == 0 ? "还没有保存的服务器" : "没有匹配的服务器",
                _servers.Count == 0 ? "这里汇总当前文件夹下各实例 servers.dat 里的多人服务器，不会改动游戏文件。" : "换个名称、地址或实例名再搜。");
            _list.Children.Add(ServerUi.Card(empty));
            return;
        }
        foreach (var server in shown.Skip(_page * PageSize).Take(PageSize)) _list.Children.Add(Card(server));
    }

    private static bool Contains(string value, string query) =>
        (value ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);

    private static TextBlock Line(string text, double size, bool muted = false)
    {
        var block = ServerUi.Text(text, size, muted);
        block.Margin = new Thickness(0, 0, 0, 2);
        block.TextWrapping = TextWrapping.NoWrap;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.ToolTip = text;
        return block;
    }

    private static Button CompactButton(string text, RoutedEventHandler click, bool primary = false)
    {
        var button = ServerUi.Button(text, click, primary);
        button.FontSize = 11;
        button.Padding = new Thickness(10, 4, 10, 4);
        button.MinHeight = button.Height = 28;
        button.Margin = new Thickness(0);
        return button;
    }

    private FrameworkElement Card(VisitedServer server)
    {
        var key = VisitedServerCatalog.Key(server.Address);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.Source = Icon(server.Icon) ?? new BitmapImage(new Uri("pack://application:,,,/Images/Icons/DefaultServer.png"));
        body.Children.Add(icon);
        var details = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        var name = Line(server.Name, 14); name.FontWeight = FontWeights.SemiBold; details.Children.Add(name);
        details.Children.Add(Line(server.Address, 11));
        var meta = string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(server.Minecraft) ? "" : "MC " + server.Minecraft,
            string.IsNullOrWhiteSpace(server.InstanceName) ? "" : "来自 " + server.InstanceName
        }.Where(text => text.Length > 0));
        if (meta.Length > 0) details.Children.Add(Line(meta, 10, muted: true));
        var motd = Line("", 10, muted: true); motd.Tag = "motd"; motd.Visibility = Visibility.Collapsed;
        details.Children.Add(motd);
        Grid.SetColumn(details, 1); body.Children.Add(details);

        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var query = CompactButton("Ping", async (_, _) => await QueryServerAsync(server));
        query.Tag = "ping"; query.Width = 56; query.Margin = new Thickness(0, 0, 6, 0);
        query.ToolTip = "查询此服务器的在线人数、延迟与简介";
        buttons.Children.Add(query);
        var open = CompactButton("一键打开", (_, _) => Open(server), primary: true);
        open.MinWidth = 76; buttons.Children.Add(open); actions.Children.Add(buttons);
        var status = Line("尚未查询", 10, muted: true); status.Tag = "status";
        status.TextAlignment = TextAlignment.Right; status.Margin = new Thickness(0, 4, 0, 0);
        actions.Children.Add(status);
        Grid.SetColumn(actions, 2); body.Children.Add(actions);
        var card = ServerUi.Card(body); card.Tag = server;
        card.Padding = new Thickness(10, 8, 10, 8); card.Margin = new Thickness(0, 0, 0, 6);
        if (_statuses.TryGetValue(key, out var cached)) Apply(card, cached);
        SetQuerying(card, _querying.Contains(key));
        return card;
    }

    private async Task QueryServerAsync(VisitedServer server)
    {
        var key = VisitedServerCatalog.Key(server.Address);
        if (!_querying.Add(key)) return;
        UpdateVisible(server, querying: true);
        try
        {
            await _pingGate.WaitAsync();
            try { _statuses[key] = await _ping(server); }
            finally { _pingGate.Release(); }
        }
        catch (Exception) { _statuses[key] = new ServerStatus(false, "", 0, ""); }
        finally
        {
            _querying.Remove(key);
            UpdateVisible(server, querying: false);
            UpdateSummary();
        }
    }

    private void UpdateVisible(VisitedServer server, bool querying)
    {
        var key = VisitedServerCatalog.Key(server.Address);
        foreach (var card in _list.Children.OfType<Border>())
        {
            if (card.Tag is not VisitedServer item || !string.Equals(VisitedServerCatalog.Key(item.Address), key, StringComparison.OrdinalIgnoreCase)) continue;
            if (_statuses.TryGetValue(key, out var status)) Apply(card, status);
            SetQuerying(card, querying);
        }
    }

    private static void SetQuerying(Border card, bool querying)
    {
        if (card.Child is not Grid grid) return;
        var button = grid.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<StackPanel>())
            .SelectMany(panel => panel.Children.OfType<Button>()).FirstOrDefault(button => Equals(button.Tag, "ping"));
        if (button is null) return;
        button.IsEnabled = !querying;
        button.Content = querying ? "查询中" : "Ping";
    }

    private static void Apply(Border card, ServerStatus status)
    {
        var blocks = card.Child is Grid grid ? grid.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<TextBlock>()) : [];
        foreach (var block in blocks)
        {
            if (Equals(block.Tag, "status"))
            {
                block.Text = status.Online ? status.Players + " · " + status.Ping + " ms" : "无法连接";
                block.ToolTip = status.Online ? block.Text : "服务器可能离线、超时或无法解析，可再次查询。";
                block.SetResourceReference(TextBlock.ForegroundProperty, status.Online ? "Hybrid.Accent" : "Hybrid.Muted");
            }
            else if (Equals(block.Tag, "motd"))
            {
                block.Text = status.Motd; block.ToolTip = status.Motd;
                block.Visibility = string.IsNullOrWhiteSpace(status.Motd) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private static BitmapImage? Icon(string data)
    {
        var split = data.IndexOf(',');
        if (split < 0) return null;
        try
        {
            var bytes = Convert.FromBase64String(data[(split + 1)..]);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.StreamSource = stream; image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    private static async Task<ServerStatus> PingAsync(VisitedServer server)
    {
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var resolved = await ServerAddressResolver.GetResolvedServerAddressAsync(server.Address, stop.Token);
            using var query = McPingServiceFactory.CreateService(resolved.Host, resolved.Ip, resolved.Port);
            var result = await query.PingAsync(stop.Token);
            if (result is null) return new ServerStatus(false, "", 0, "");
            var motd = System.Text.RegularExpressions.Regex.Replace(result.Description ?? "", @"§.", "").Replace('\n', ' ').Trim();
            if (motd.Length > 80) motd = motd[..80];
            return new ServerStatus(true, result.Players.Online + "/" + result.Players.Max, (int)result.Latency, motd);
        }
        catch (Exception) { return new ServerStatus(false, "", 0, ""); }
    }

    private static void Open(VisitedServer server)
    {
        try
        {
            var instance = InstanceFor(server);
            if (instance is null || !instance.Check())
            {
                HintService.Hint("找不到「" + (server.InstanceName.Length > 0 ? server.InstanceName : server.Name) + "」对应的可启动实例", HintType.Error);
                return;
            }
            GameLinkSession.ValidateAddress(server.Address);
            if (!ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { ServerIp = server.Address, instance = instance }))
            {
                HintService.Hint("没有开始进入 " + server.Name, HintType.Error);
                return;
            }
            ModMain.frmMain?.PageChange(new FormMain.PageStackData { page = FormMain.PageType.Launch });
            HintService.Hint("正在用 " + instance.Name + " 打开 " + server.Name);
        }
        catch (Exception ex) { HintService.Hint("打开失败：" + ex.Message, HintType.Error); }
    }

    private static McInstance InstanceFor(VisitedServer server)
    {
        if (ModInstanceList.mcInstanceList is null || string.IsNullOrWhiteSpace(server.InstancePath)) return null;
        var path = Path.TrimEndingDirectorySeparator(server.InstancePath);
        return ModInstanceList.mcInstanceList.ToArray()
            .Where(card => card.Key != McInstanceCardType.Hidden)
            .SelectMany(card => card.Value.ToArray())
            .FirstOrDefault(instance => instance is not null &&
                string.Equals(Path.TrimEndingDirectorySeparator(instance.PathIndie ?? ""), path, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ServerStatus(bool Online, string Players, int Ping, string Motd);
}
