using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ServerThemeChecks
{
    internal static void Run(Assembly assembly, Application application, string[] args)
    {
        var theme = new ResourceDictionary { Source = new Uri("/Plain Craft Launcher 2;component/Modules/HybridUi/HybridTheme.xaml", UriKind.Relative) };
        var host = new Window { Width = 1060, Height = 720, Title = "服务端管理 · 隔离检查", WindowStartupLocation = WindowStartupLocation.CenterScreen };
        host.Resources.MergedDictionaries.Add(theme);
        host.SetResourceReference(Control.BackgroundProperty, "Hybrid.Background");
        application.MainWindow = host;
        var list = (FrameworkElement)Activator.CreateInstance(assembly.GetType("PCL.ServerManagementView", true)!, nonPublic: true)!;
        host.Content = list;
        var create = (Window)Activator.CreateInstance(assembly.GetType("PCL.ServerInstallWindow", true)!, BindingFlags.Instance | BindingFlags.NonPublic, null, new object?[] { (Action)(() => { }), null }, null)!;
        Action? cleanup = null;
        try
        {
            cleanup = AddFixtures(assembly);
            host.Show(); create.Show(); Pump();
            AssertSurface(create, theme, "Hybrid.Background");
            CheckContrast(create); CheckContrast(list);
            var combo = Descendants<ComboBox>(create).First();
            combo.IsDropDownOpen = true; Pump();
            if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: DependencyObject popup }) CheckContrast(popup);
            combo.IsDropDownOpen = false;
            Capture(host, args, "server-list-dark.png"); Capture(create, args, "server-create-dark.png");
            var width = create.Width; var height = create.Height;
            create.Width = create.MinWidth; create.Height = 660; Pump(); CheckContrast(create);
            var installButton = Descendants<Button>(create).Single(button => Equals(button.Content, "安装并打开控制台"));
            var buttonBounds = installButton.TransformToAncestor(create).TransformBounds(new Rect(installButton.RenderSize));
            if (buttonBounds.Bottom > create.ActualHeight || buttonBounds.Right > create.ActualWidth) throw new InvalidOperationException("Install action is clipped in the compact window.");
            Capture(create, args, "server-create-compact.png");
            create.Width = width; create.Height = height;
            foreach (var expander in Descendants<Expander>(create).ToArray()) expander.IsExpanded = true;
            Pump(); CheckContrast(create); Capture(create, args, "server-create-advanced.png");
            foreach (var expander in Descendants<Expander>(create).ToArray()) expander.IsExpanded = false;
            Pump();
            Console.WriteLine("PASS server list / create window dark surfaces, text, inputs and dropdown");
            var light = new Dictionary<string, string>
            {
                ["Hybrid.Background"] = "#F4F5F7", ["Hybrid.Sidebar"] = "#EDEEF0", ["Hybrid.Surface"] = "#FFFFFF",
                ["Hybrid.Line"] = "#C7CBD1", ["Hybrid.Text"] = "#20252B", ["Hybrid.Muted"] = "#505963", ["Hybrid.Accent"] = "#FFB942"
            };
            foreach (var pair in light) theme[pair.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            Pump(); AssertSurface(create, theme, "Hybrid.Background");
            CheckContrast(create); CheckContrast(list);
            Capture(host, args, "server-list-light.png"); Capture(create, args, "server-create-light.png");
            Console.WriteLine("PASS server UI follows palette changes with readable text");
        }
        finally { create.Close(); host.Close(); cleanup?.Invoke(); }
    }

    private static void AssertSurface(Window window, ResourceDictionary theme, string key)
    {
        if (window.Background is not SolidColorBrush actual || actual.Color != ((SolidColorBrush)theme[key]).Color)
            throw new InvalidOperationException("ServerInstallWindow uses a system/default background instead of the launcher theme.");
    }

    private static void CheckContrast(DependencyObject root)
    {
        var checkedCount = 0;
        foreach (var element in Descendants<FrameworkElement>(root))
        {
            if (!element.IsVisible || !element.IsEnabled || element.Opacity < .8) continue;
            var foreground = element switch { TextBlock text when !string.IsNullOrWhiteSpace(text.Text) => text.Foreground, TextBox text => text.Foreground, _ => null };
            if (foreground is not SolidColorBrush ink || ink.Color.A < 240) continue;
            var background = Background(element);
            var contrast = (Math.Max(Luminance(ink.Color), Luminance(background)) + .05) / (Math.Min(Luminance(ink.Color), Luminance(background)) + .05);
            if (contrast < 4.45) throw new InvalidOperationException($"Unreadable server UI {element.GetType().Name} '{(element as TextBlock)?.Text}': contrast {contrast:F2}, foreground {ink.Color}, background {background}.");
            checkedCount++;
        }
        if (checkedCount == 0) throw new InvalidOperationException("Server theme check did not inspect any visible text.");
    }

    private static Color Background(DependencyObject element)
    {
        // A Control's unused default Background need not be painted by its template.
        if (element is TextBox { Background: SolidColorBrush input } && input.Color.A == 255) return input.Color;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            var brush = current switch { Border border => border.Background, Panel panel => panel.Background, Window window => window.Background, _ => null };
            if (brush is SolidColorBrush solid && solid.Color.A == 255) return solid.Color;
        }
        return Colors.White;
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T value) yield return value;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(node, index))) yield return child;
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Capture(Window window, string[] args, string name)
    {
        var index = Array.IndexOf(args, "--capture-dir");
        if (index < 0 || index + 1 >= args.Length) return;
        var directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
    private static Action AddFixtures(Assembly assembly)
    {
        var ui = assembly.GetType("PCL.ServerUi", true)!;
        var manager = ui.GetField("Manager", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var managerType = manager.GetType();
        var profileType = managerType.Assembly.GetType("PCL.Core.Minecraft.Server.ServerProfile", true)!;
        var paths = new List<string>();
        foreach (var loader in new[] { "NeoForge", "Fabric" })
        {
            var profile = Activator.CreateInstance(profileType)!;
            profileType.GetProperty("Name")!.SetValue(profile, loader == "NeoForge" ? "星穹列车 · 生存服务端" : "轻量联机 · Fabric");
            profileType.GetProperty("MinecraftVersion")!.SetValue(profile, "1.21.1");
            profileType.GetProperty("LoaderVersion")!.SetValue(profile, loader == "NeoForge" ? "21.1.172" : "0.16.14");
            var loaderProperty = profileType.GetProperty("Loader")!;
            loaderProperty.SetValue(profile, Enum.Parse(loaderProperty.PropertyType, loader));
            profileType.GetProperty("EntryPoint")!.SetValue(profile, "server.jar");
            managerType.GetMethod("Save")!.Invoke(manager, new[] { profile });
            paths.Add((string)managerType.GetMethod("GetDirectory")!.Invoke(manager, new[] { profile })!);
        }
        return () => { foreach (var path in paths) if (Directory.Exists(path)) Directory.Delete(path, true); };
    }

}
