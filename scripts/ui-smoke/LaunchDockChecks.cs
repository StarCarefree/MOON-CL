using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class LaunchDockChecks
{
    internal static void Run(Assembly assembly, string[] args)
    {
        foreach (var (key, text) in new[]
        {
            ("Launch.Status.CurrentStep", "当前步骤"), ("Launch.Status.LoginMethod", "验证方式"),
            ("Launch.Status.LaunchProgress", "启动进度"), ("Launch.Status.DownloadSpeed", "下载速度"),
            ("Common.Action.Cancel", "取消"), ("Launch.Home.Button.Loading", "启动游戏")
        }) Application.Current.Resources[key] = text;
        var type = assembly.GetType("PCL.PageLaunchLeft", true)!;
        var page = (FrameworkElement)Activator.CreateInstance(type)!;
        var input = (FrameworkElement)page.FindName("PanInput");
        var launching = (FrameworkElement)page.FindName("PanLaunching");
        var start = (FrameworkElement)page.FindName("BtnLaunch");
        var cancel = (FrameworkElement)page.FindName("BtnCancel");
        start.GetType().GetProperty("Text")!.SetValue(start, "启动游戏");
        start.IsEnabled = true;
        cancel.GetType().GetProperty("Text")!.SetValue(cancel, "取消");
        var startText = (TextBlock)start.GetType().GetField("LabText", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(start)!;
        startText.FontSize = 20;
        startText.FontWeight = FontWeights.SemiBold;
        startText.Foreground = new SolidColorBrush(Color.FromRgb(22, 24, 27));
        var title = (TextBlock)page.FindName("LabLaunchingTitle");
        var name = (TextBlock)page.FindName("LabLaunchingName");
        var stage = (TextBlock)page.FindName("LabLaunchingStage");
        var method = (TextBlock)page.FindName("LabLaunchingMethod");
        var percentage = (TextBlock)page.FindName("LabLaunchingProgress");
        title.Text = "正在启动游戏";
        name.Text = "StarRailHarpyExpress · 长版本名与实例名称边界验证";
        stage.Text = "启动游戏进程";
        method.Text = "第三方验证 / 测试皮肤站账号";
        percentage.Text = "90.39%";
        ((ColumnDefinition)page.FindName("ProgressLaunchingFinished")).Width = new GridLength(0.9039, GridUnitType.Star);
        ((ColumnDefinition)page.FindName("ProgressLaunchingUnfinished")).Width = new GridLength(0.0961, GridUnitType.Star);
        var speed = (TextBlock)page.FindName("LabLaunchingDownload");
        speed.Text = "12.5 MB/s";
        speed.Visibility = Visibility.Visible;
        speed.Opacity = 1;
        var speedLabel = (TextBlock)page.FindName("LabLaunchingDownloadLeft");
        speedLabel.Visibility = Visibility.Visible;
        speedLabel.Opacity = 1;
        ((TextBlock)page.FindName("LabVersion")).Text = name.Text;
        var imageDir = args.FirstOrDefault(arg => arg.StartsWith("--screenshots="))?[14..];
        foreach (var width in new[] { 640d, 900d, 1360d })
        {
            input.Visibility = Visibility.Collapsed;
            launching.Visibility = Visibility.Visible;
            launching.Opacity = 1;
            launching.RenderTransform = Transform.Identity;
            Layout(page, width);
            var cancelBounds = Bounds(cancel, page);
            var stageBounds = Bounds(stage, page);
            Check(!cancelBounds.IntersectsWith(stageBounds), "launch stage and cancel must never overlap");
            Check(!Bounds((FrameworkElement)page.FindName("LoadLaunching"), page).IntersectsWith(Bounds(title, page)), "loading icon and title do not overlap");
            Check(Inside(Bounds(speed, page), page.RenderSize), "download speed fits dock");
            foreach (var element in new FrameworkElement[] { title, name, stage, method, percentage, cancel })
                Check(Inside(Bounds(element, page), page.RenderSize), "launch content fits dock: " + element.Name);
            Check(page.ActualWidth >= width - 1, "dock stretches across page instead of floating over cards");
            Check(cancel.ActualHeight >= 40, "cancel remains a usable click target");
            Save(page, imageDir, $"launch-dock-{width}-launching.png");
            input.Visibility = Visibility.Visible;
            input.Opacity = 1;
            input.RenderTransform = Transform.Identity;
            launching.Visibility = Visibility.Collapsed;
            Layout(page, width);
            Check(Inside(Bounds(start, page), page.RenderSize), "start fits dock");
            Check(start.ActualWidth >= 240 && start.ActualHeight >= 64, "start remains prominent");
            Check(page.ActualHeight <= 180, "normal dock does not consume excessive card space");
            var version = (FrameworkElement)page.FindName("LabVersion");
            Check(!Bounds(version, page).IntersectsWith(Bounds(start, page)), "selected version and primary action occupy distinct areas");
            Check(((FrameworkElement)page.FindName("PanVersionActions")).Visibility == Visibility.Collapsed, "version actions stay on hero card");
            Save(page, imageDir, $"launch-dock-{width}-ready.png");
            Console.WriteLine($"PASS launch dock {width}px / ready and launching / no overlap or overflow");
        }
        CheckCardViewport(assembly, page, input, launching, imageDir);
        CheckStateTransitions(assembly, page, input, launching);
    }

    private static void CheckCardViewport(Assembly assembly, FrameworkElement launch, FrameworkElement input, FrameworkElement launching, string? imageDir)
    {
        // Real game cards and launch view in the same separate-row arrangement as FormMain.
        var home = (FrameworkElement)Activator.CreateInstance(assembly.GetType("PCL.PageLaunchRight", true)!)!;
        ((FrameworkElement)home.FindName("PanHint")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)home.FindName("PanLog")).Visibility = Visibility.Collapsed;
        ((TextBlock)home.FindName("LabNativeGameName")).Text = "StarRailHarpyExpress";
        ((TextBlock)home.FindName("LabNativeGameDetail")).Text = "1.21.1 + Fabric 0.19.3";
        var shell = new Grid { Background = new SolidColorBrush(Color.FromRgb(17, 19, 21)) };
        shell.RowDefinitions.Add(new RowDefinition());
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Children.Add(home);
        Grid.SetRow(launch, 1);
        shell.Children.Add(launch);
        foreach (var active in new[] { false, true })
        {
            input.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            launching.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            launching.Opacity = 1;
            shell.Measure(new Size(1100, 760)); shell.Arrange(new Rect(0, 0, 1100, 760)); shell.UpdateLayout();
            Check(Bounds(home, shell).Bottom <= Bounds(launch, shell).Top, "cards never extend beneath launch dock");
            var cardStack = (FrameworkElement)home.FindName("PanMain");
            var dock = (FrameworkElement)launch.FindName("LaunchDock");
            var cards = Bounds(cardStack, shell);
            var bar = Bounds(dock, shell);
            var leftOverhang = cards.Left - bar.Left;
            var rightOverhang = bar.Right - cards.Right;
            Check(leftOverhang >= 12 && Math.Abs(leftOverhang - rightOverhang) <= 1, "launch dock extends equally past both card edges");
            Save(shell, imageDir, active ? "games-launching.png" : "games-ready.png");
        }
        shell.Children.Clear();
        Console.WriteLine("PASS card viewport and bottom dock occupy separate non-overlapping rows");
    }

    private static void CheckStateTransitions(Assembly assembly, FrameworkElement page, FrameworkElement input, FrameworkElement launching)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var core = Assembly.Load("PCL.Core");
        var service = core.GetType("PCL.Core.App.Configuration.ConfigService", true)!;
        var items = (System.Collections.IDictionary)service.GetField("_Items", flags)!.GetValue(null)!;
        var item = items["UiShowLaunchingHint"]!;
        var cacheField = item.GetType().GetField("_valueCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = cacheField.GetValue(item)!;
        cache.GetType().GetMethod("Write")!.Invoke(cache, new object?[] { false, null });
        cacheField.SetValue(item, cache);
        var instanceType = assembly.GetType("PCL.McInstance", true)!;
        var instance = Activator.CreateInstance(instanceType, Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR")!, "dock-fixture", "versions", "SmokeWorld"));
        var list = assembly.GetType("PCL.ModInstanceList", true)!;
        // Set the fixture directly: the public setter schedules real pack-watcher I/O.
        var selected = list.GetFields(flags).Single(field => field.FieldType == instanceType && field.Name.Contains("McMcInstanceSelected"));
        selected.SetValue(null, instance);
        assembly.GetType("PCL.ModMain", true)!.GetField("frmLaunchLeft", flags)!.SetValue(null, page);
        var current = page.GetType().GetField("pageCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        current.SetValue(page, Enum.Parse(current.FieldType, "Profile"));
        page.GetType().GetMethod("PageChangeToLaunching")!.Invoke(page, null);
        Check(input.Visibility == Visibility.Collapsed && launching.Visibility == Visibility.Visible, "real start transition replaces ready view");
        Check(launching.IsHitTestVisible, "cancel is immediately reachable during launching");
        Layout(page, 900);
        var height = page.ActualHeight;
        page.GetType().GetMethod("PageChangeToLogin")!.Invoke(page, null);
        Check(input.Visibility == Visibility.Visible && launching.Visibility == Visibility.Collapsed && input.IsHitTestVisible, "real return restores ready view");
        Layout(page, 900);
        Check(page.ActualHeight == height, "launch state transitions preserve dock height");
        Console.WriteLine("PASS actual ready/launching/return state transitions without launching a game");
    }

    private static void Layout(FrameworkElement page, double width)
    {
        page.Measure(new Size(width, 160));
        page.Arrange(new Rect(0, 0, width, 160));
        page.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement parent) =>
        element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));

    private static bool Inside(Rect rect, Size size) => rect.Left >= -1 && rect.Top >= -1
        && rect.Right <= size.Width + 1 && rect.Bottom <= size.Height + 1;

    private static void Save(FrameworkElement page, string? directory, string file)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, file));
        png.Save(output);
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("Launch dock check failed: " + message);
    }
}
