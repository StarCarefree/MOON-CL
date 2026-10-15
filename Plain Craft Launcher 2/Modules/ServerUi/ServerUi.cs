using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL.Core.Minecraft.Server;

namespace PCL;

internal static class ServerUi
{
    internal static readonly ServerManager Manager = new(Path.Combine(AppContext.BaseDirectory, "servers"));
    internal static Func<int> DownloadSource = () => 1;
    private static readonly Dictionary<string, ServerControlWindow> Windows = new();

    private static readonly HashSet<ServerSession> Sessions = [];

    internal static bool CanExit()
    {
        if (!Sessions.Any(s => s.IsRunning || s.IsInstalling)) return true;
        MessageBox.Show("仍有服务端正在运行或安装。请先在服务端控制面板安全停止，或在安装窗口取消任务，再退出启动器。", "服务端仍在运行", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }
    internal static void Refresh(ServerProfile profile)
    {
        if (Windows.TryGetValue(profile.Id, out var window)) window.ReloadProfile();
    }

    internal static ServerControlWindow Open(ServerProfile profile)
    {
        if (Windows.TryGetValue(profile.Id, out var existing)) { existing.Show(); existing.Activate(); return existing; }
        Sessions.Add(Manager.GetSession(profile));
        var window = new ServerControlWindow(profile);
        Windows[profile.Id] = window;
        window.Closed += (_, _) => Windows.Remove(profile.Id);
        window.Show();
        return window;
    }

    internal static void Error(Exception ex) => MessageBox.Show(ex.Message, "服务端管理", MessageBoxButton.OK, MessageBoxImage.Warning);
    internal static void Folder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true });
    }

    internal static TextBlock Text(string text, double size = 13) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8)
    };

    internal static Button Button(string title, RoutedEventHandler click)
    {
        var button = new Button { Content = title, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 8), MinHeight = 32 };
        button.Click += click;
        return button;
    }

    internal static TextBox Field(Panel panel, string label, string value, bool multiline = false)
    {
        panel.Children.Add(Text(label));
        var field = new TextBox
        {
            Text = value, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 10),
            MinHeight = multiline ? 180 : 32, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden
        };
        panel.Children.Add(field);
        return field;
    }

    internal static TextBox FileField(Panel panel, string label, string filter)
    {
        var field = Field(panel, label, "");
        panel.Children.Add(Button("浏览…", (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (picker.ShowDialog() == true) field.Text = picker.FileName;
        }));
        return field;
    }

    internal static FrameworkElement Icon(ServerLoader loader, double size = 44)
    {
        var grid = new Grid { Width = size, Height = size, Margin = new Thickness(0, 0, 14, 0) };
        grid.Children.Add(new Image { Source = new BitmapImage(new Uri($"pack://application:,,,/Images/Blocks/{loader}.png")), Stretch = Stretch.Uniform });
        var badge = new Border
        {
            Width = size * .47, Height = size * .47, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.White, CornerRadius = new CornerRadius(4), Padding = new Thickness(2),
            Child = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Images/Icons/DefaultServer.png")), Stretch = Stretch.Uniform }
        };
        grid.Children.Add(badge);
        return grid;
    }

    internal static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    internal static bool ConfirmEula(ServerProfile profile)
    {
        if (Manager.IsEulaAccepted(profile)) return true;
        var result = MessageBox.Show("启动 Minecraft 服务端需要你阅读并同意 Minecraft EULA：\nhttps://aka.ms/MinecraftEULA\n\n是否已阅读并同意？选择“是”将为此服务端写入 eula=true。", "Minecraft EULA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return false;
        Manager.AcceptEula(profile);
        return true;
    }
}
