using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PCL.Core.Minecraft.Server;

namespace PCL;

internal sealed class ServerPropertiesEditor : UserControl
{
    public sealed class PropertyEntry : INotifyPropertyChanged
    {
        public string Key { get; init; } = "";
        private string _value = "";
        public string Value { get => _value; set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static readonly (string Key, string Label, string Default, string[]? Choices)[] Common =
    [
        ("motd", "服务器描述（MOTD）", "A Minecraft Server", null),
        ("server-ip", "绑定地址（通常留空）", "", null),
        ("server-port", "游戏端口（1–65535）", "25565", null),
        ("max-players", "最大玩家数", "20", null),
        ("online-mode", "正版验证（关闭有冒名风险）", "true", ["true", "false"]),
        ("white-list", "启用白名单", "false", ["true", "false"]),
        ("enforce-whitelist", "强制执行白名单", "false", ["true", "false"]),
        ("gamemode", "默认游戏模式", "survival", ["survival", "creative", "adventure", "spectator"]),
        ("difficulty", "难度", "easy", ["peaceful", "easy", "normal", "hard"]),
        ("pvp", "允许玩家互伤", "true", ["true", "false"]),
        ("allow-flight", "允许飞行", "false", ["true", "false"]),
        ("enable-command-block", "启用命令方块", "false", ["true", "false"]),
        ("view-distance", "视距（区块）", "10", null),
        ("simulation-distance", "模拟距离（区块）", "10", null),
        ("level-name", "世界目录名称", "world", null),
        ("level-seed", "世界种子（仅生成新世界时生效）", "", null),
        ("enable-rcon", "启用 RCON（需另外配置密码与防火墙）", "false", ["true", "false"])
    ];

    private readonly ServerProfile _profile;
    private readonly ObservableCollection<PropertyEntry> _entries = [];
    private readonly StackPanel _form = new() { Margin = new Thickness(16) };
    private readonly DataGrid _grid;
    private readonly TextBlock _notice = ServerUi.Text("配置保存后需重启服务端生效；运行和安装期间禁止编辑。");
    private string _loaded = "";

    internal ServerPropertiesEditor(ServerProfile profile)
    {
        _profile = profile;
        var root = new DockPanel { Margin = new Thickness(10) };
        var header = new StackPanel();
        header.Children.Add(_notice);
        var actions = new WrapPanel();
        actions.Children.Add(ServerUi.Button("保存配置", (_, _) => Save()));
        actions.Children.Add(ServerUi.Button("重新读取", (_, _) =>
        {
            if (MessageBox.Show("重新读取将丢弃尚未保存的编辑，继续？", "服务端配置", MessageBoxButton.YesNo) == MessageBoxResult.Yes) LoadProperties();
        }));
        header.Children.Add(actions); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "常用配置", Content = ServerUi.Scroll(_form) });
        _grid = new DataGrid { ItemsSource = _entries, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, Margin = new Thickness(8), MinHeight = 200 };
        _grid.Columns.Add(new DataGridTextColumn { Header = "属性名", Binding = new Binding(nameof(PropertyEntry.Key)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "值", Binding = ValueBinding(), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        var advanced = new DockPanel();
        var add = new WrapPanel { Margin = new Thickness(8) };
        var key = new TextBox { Width = 190, Padding = new Thickness(5), Margin = new Thickness(0, 0, 8, 8), ToolTip = "新属性名" };
        var value = new TextBox { Width = 220, Padding = new Thickness(5), Margin = new Thickness(0, 0, 8, 8), ToolTip = "属性值" };
        add.Children.Add(key); add.Children.Add(value);
        add.Children.Add(ServerUi.Button("添加属性", (_, _) =>
        {
            var name = key.Text.Trim();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace) || name.IndexOfAny(['=', ':', '\\']) >= 0) { ServerUi.Error(new InvalidOperationException("请输入有效的属性名。")); return; }
            if (_entries.Any(p => p.Key == name)) { ServerUi.Error(new InvalidOperationException("属性已存在，请在表格中编辑。")); return; }
            _entries.Add(new PropertyEntry { Key = name, Value = value.Text }); key.Clear(); value.Clear();
        }));
        DockPanel.SetDock(add, Dock.Bottom); advanced.Children.Add(add); advanced.Children.Add(_grid);
        tabs.Items.Add(new TabItem { Header = "全部属性 / 高级", Content = advanced });
        root.Children.Add(tabs); Content = root;
        LoadProperties();
    }

    private static Binding ValueBinding() => new(nameof(PropertyEntry.Value)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };

    private string DiskSnapshot() => JsonSerializer.Serialize(ServerUi.Manager.ReadProperties(_profile).OrderBy(p => p.Key));

    internal void Reload() => LoadProperties();

    private void LoadProperties()
    {
        try
        {
            var properties = ServerUi.Manager.ReadProperties(_profile);
            _loaded = JsonSerializer.Serialize(properties.OrderBy(p => p.Key));
            _entries.Clear(); _form.Children.Clear();
            foreach (var (key, _, fallback, _) in Common) properties.TryAdd(key, fallback);
            foreach (var item in properties.OrderBy(p => p.Key)) _entries.Add(new PropertyEntry { Key = item.Key, Value = item.Value });
            foreach (var (key, label, _, choices) in Common)
            {
                var entry = _entries.First(p => p.Key == key);
                _form.Children.Add(ServerUi.Text(label + " · " + key));
                if (choices is not null)
                {
                    var combo = new ComboBox { ItemsSource = choices, IsEditable = true, Margin = new Thickness(0, 0, 0, 12), MinHeight = 32, DataContext = entry };
                    combo.SetBinding(ComboBox.TextProperty, ValueBinding()); _form.Children.Add(combo);
                }
                else
                {
                    var text = new TextBox { Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 12), DataContext = entry };
                    text.SetBinding(TextBox.TextProperty, ValueBinding()); _form.Children.Add(text);
                }
            }
            _notice.Text = "配置保存后需重启服务端生效；运行和安装期间禁止编辑。";
        }
        catch (Exception ex) { _notice.Text = "读取配置失败：" + ex.Message; }
    }

    private void Save()
    {
        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (DiskSnapshot() != _loaded) throw new InvalidOperationException("配置文件已被服务端或其他程序修改，请重新读取后再保存，避免覆盖。");
            var values = _entries.ToDictionary(p => p.Key, p => p.Value);
            foreach (var key in new[] { "server-port", "query.port", "rcon.port" })
                if (values.TryGetValue(key, out var port) && (!int.TryParse(port, out var number) || number is < 1 or > 65535)) throw new InvalidOperationException(key + " 必须为 1–65535。");
            foreach (var key in new[] { "max-players", "view-distance", "simulation-distance" })
                if (values.TryGetValue(key, out var value) && (!int.TryParse(value, out var number) || number < 1)) throw new InvalidOperationException(key + " 必须为正整数。");
            ServerUi.Manager.WriteProperties(_profile, values); _loaded = DiskSnapshot();
            _notice.Text = "已保存。下次启动 / 重启服务端后生效。";
        }
        catch (Exception ex) { ServerUi.Error(ex); }
    }
}
