using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL;

internal static partial class Program
{
    private static int _checks;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static T Control<T>(HybridLaunchScreen view, string name) => (T)view.FindName(name);
    private static DispatcherTimer Clock(HybridLaunchScreen view) => (DispatcherTimer)typeof(HybridLaunchScreen).GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(HybridLaunchScreen view, string file)
    {
        view.UpdateLayout();
        var image = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(file); png.Save(stream);
    }
    [STAThread]
    public static void Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new System.Windows.Application();
        var view = new HybridLaunchScreen();
        var window = new Window { Content = view, Width = 730, Height = 720, Left = -10000, Top = -10000, ShowInTaskbar = false };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await CheckPreferences(args[1]);
                Check(view.Visibility == Visibility.Collapsed, "Must start hidden");
                Check(!Clock(view).IsEnabled, "No timer before launch");
                view.BeginLaunch("MOONCL · 星月之旅 1.21.1 / NeoForge");
                view.UpdateLaunch("正在启动游戏", "补全游戏文件", .42, "12 MiB/s");
                await Task.Delay(350); view.UpdateLayout();
                Check(view.IsVisible && Clock(view).IsEnabled, "Launch must enable the visible clock");
                Check(Control<TextBlock>(view, "Percentage").Text.Contains("42"), "Must show real progress");
                Check(Control<TextBlock>(view, "DownloadSpeed").Text.Contains("12 MiB/s"), "Download speed missing");
                Capture(view, Path.Combine(output, "launch-screen.png"));
                var home = 0; var cancel = 0;
                view.HomeRequested += (_, _) => home++;
                view.CancelRequested += (_, _) => cancel++;
                foreach (var button in Tree(view).OfType<Button>())
                {
                    var id = AutomationProperties.GetAutomationId(button);
                    if (!id.StartsWith("LaunchScreen.")) continue;
                    Check(button.IsVisible && button.IsEnabled, id + " inaccessible");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Check(home == 1 && cancel == 1, "Actions must dispatch exactly once");
                view.Visibility = Visibility.Hidden;
                Check(!Clock(view).IsEnabled, "Hidden page must stop timer");
                view.Visibility = Visibility.Visible;
                Check(Clock(view).IsEnabled, "Visible active page must resume timer");
                view.EndLaunch();
                Check(view.Visibility == Visibility.Collapsed && !Clock(view).IsEnabled, "Ending must clear visibility/timer");
                view.UpdateLaunch("stale", "stale", .9, null);
                Check(Control<TextBlock>(view, "StageName").Text != "stale", "Late update revived ended page");
                view.BeginLaunch(new string('长', 120));
                Check(Control<TextBlock>(view, "Percentage").Text == "0%", "New launch must reset progress");
                view.UpdateLaunch("等待游戏窗口", "等待游戏窗口", double.NaN, null);
                Check(Control<TextBlock>(view, "Percentage").Text.Contains('0'), "NaN must be safe");
                view.UpdateLaunch("等待游戏窗口", "等待游戏窗口", 2, null);
                Check(Control<TextBlock>(view, "Percentage").Text.Contains("100"), "Progress must be bounded");
                window.Width = 430; window.Height = 660;
                await Task.Delay(250); view.UpdateLayout();
                Capture(view, Path.Combine(output, "launch-screen-compact.png"));
                var scroll = Tree(view).OfType<ScrollViewer>().First();
                Check(scroll.ScrollableWidth == 0, "Compact page must not overflow horizontally");
                scroll.ScrollToBottom(); view.UpdateLayout();
                foreach (var button in Tree(view).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("LaunchScreen.")))
                {
                    var bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                    Check(bounds.Right <= view.ActualWidth + 1 && bounds.Bottom <= view.ActualHeight + 1, "Compact action clipped");
                }
                window.Content = null;
                await Task.Delay(30);
                Check(!Clock(view).IsEnabled, "Unload must stop timer");
                Console.WriteLine($"PASS: {_checks} launch UI checks; screenshots: {output}");
                app.Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        app.Run(window);
    }
}
