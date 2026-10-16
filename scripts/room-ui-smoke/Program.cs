using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCL.Core.Multiplayer;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static int _checks;
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("Usage: Room.UiSmoke <local coordinator URL> <scratch directory> <screenshot directory>");
        if (!Uri.TryCreate(args[0], UriKind.Absolute, out var coordinator) || !coordinator.IsLoopback)
            throw new ArgumentException("Smoke tests must use an isolated loopback coordinator, never a public service.");
        var launcher = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Plain Craft Launcher 2", "bin", "Debug", "net10.0-windows", "win-x64"));
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(launcher, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(launcher, name.Name + ".dll")) : null;
        Run(args);
    }
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception(message); }
    private static T Field<T>(HybridRooms view, string name) => (T)typeof(HybridRooms).GetField(name, Private)!.GetValue(view)!;
    private static void Set(HybridRooms view, string name, object? value) => typeof(HybridRooms).GetField(name, Private)!.SetValue(view, value);
    private static object? Invoke(HybridRooms view, string name, params object?[] args) => typeof(HybridRooms).GetMethod(name, Private)!.Invoke(view, args);
    private static T Control<T>(HybridRooms view, string name) where T : FrameworkElement => (T)view.FindName(name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Click(HybridRooms view, string id)
    {
        view.UpdateLayout();
        var button = Descendants(view).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b) == id);
        Check(button.IsEnabled && button.IsVisible, id + " is disabled or hidden");
        AssertInViewport(view, button, id); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task Idle(HybridRooms view)
    {
        var until = DateTime.UtcNow.AddSeconds(25);
        while (Field<bool>(view, "_busy") && DateTime.UtcNow < until) await Task.Delay(25);
        Check(!Field<bool>(view, "_busy"), "UI operation timed out"); view.UpdateLayout();
    }
    private static async Task Refresh(HybridRooms view)
    {
        Set(view, "_nextHeartbeat", DateTime.MinValue);
        Invoke(view, "Heartbeat_Tick", null, EventArgs.Empty);
        var until = DateTime.UtcNow.AddSeconds(8);
        while (Field<bool>(view, "_refreshing") && DateTime.UtcNow < until) await Task.Delay(20);
        Check(!Field<bool>(view, "_refreshing"), "Heartbeat timed out"); view.UpdateLayout();
    }
    private static RoomLaunchTarget? Target(HybridRooms view) => (RoomLaunchTarget?)Invoke(view, "GetLaunchTarget");
    private static HybridRooms NewView(string preferences) => (HybridRooms)Activator.CreateInstance(typeof(HybridRooms),
        BindingFlags.Instance | BindingFlags.NonPublic, null, [preferences], null)!;
    private static void Configure(HybridRooms view, string url, string name)
    {
        Click(view, "Rooms.OpenSettings");
        Control<ComboBox>(view, "Coordinator").SelectedIndex = 1;
        Control<TextBox>(view, "CustomUrl").Text = url;
        Control<TextBox>(view, "PlayerName").Text = name;
        AssertNoScrollBars(view);
        Click(view, "Rooms.CloseOverlay");
    }
    private static void Screenshot(HybridRooms view, string path)
    {
        view.UpdateLayout();
        var image = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(view); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var output = File.Create(path); png.Save(output);
    }
    private static void AssertPrivate(HybridRooms view)
    {
        var hidden = new Uri(RoomServiceDirectory.OfficialUrl).Host;
        Check(!Descendants(view).OfType<TextBlock>().Any(t => t.Text.Contains(hidden)), "Official address leaked into visible text");
        Check(!Descendants(view).OfType<TextBox>().Any(t => t.Text.Contains(hidden)), "Official address leaked into an input");
    }
    private static void AssertNoScrollBars(HybridRooms view)
    {
        view.UpdateLayout();
        Check(!Descendants(view).OfType<System.Windows.Controls.Primitives.ScrollBar>().Any(bar => bar.IsVisible && bar.ActualWidth > 0 && bar.ActualHeight > 0),
            "The lobby must not display any scroll bars");
    }
    private static void AssertInViewport(HybridRooms view, FrameworkElement element, string label)
    {
        view.UpdateLayout();
        Check(element.IsVisible && element.ActualWidth > 1 && element.ActualHeight > 1, label + " is not visible");
        var bounds = element.TransformToAncestor(view).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= view.ActualWidth + 1 && bounds.Bottom <= view.ActualHeight + 1,
            label + " extends beyond the fixed lobby viewport");
        for (var parent = VisualTreeHelper.GetParent(element); parent is FrameworkElement ancestor && !ReferenceEquals(ancestor, view); parent = VisualTreeHelper.GetParent(parent))
        {
            if (!ancestor.ClipToBounds) continue;
            var local = element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            Check(local.Left >= -1 && local.Top >= -1 && local.Right <= ancestor.ActualWidth + 1 && local.Bottom <= ancestor.ActualHeight + 1,
                label + " is clipped by a container");
        }
    }
    private static Button ButtonById(HybridRooms view, string id) => Descendants(view).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b) == id);
    private static async Task ResizeViewport(Window window, HybridRooms view, double width, double height)
    {
        view.Width = width; view.Height = height;
        window.SizeToContent = SizeToContent.WidthAndHeight;
        await view.Dispatcher.InvokeAsync(() => view.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Loaded);
        AssertNoScrollBars(view);
    }
    private static void AssertSeatGeometry(HybridRooms view)
    {
        var seats = Control<ItemsControl>(view, "Members"); view.UpdateLayout();
        for (var i = 0; i < seats.Items.Count; i++)
        {
            var slot = (FrameworkElement)seats.ItemContainerGenerator.ContainerFromIndex(i);
            AssertInViewport(view, slot, "Player seat " + i);
            var image = Descendants(slot).OfType<Image>().First();
            var avatar = (Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(image));
            Check(avatar.ActualWidth >= 48 && Math.Abs(avatar.ActualWidth - avatar.ActualHeight) <= 1, "Avatar must be enlarged proportionally");
            var bounds = avatar.TransformToAncestor(slot).TransformBounds(new Rect(0, 0, avatar.ActualWidth, avatar.ActualHeight));
            Check(Math.Abs(bounds.Left + bounds.Width / 2 - slot.ActualWidth / 2) <= 1.5, "Avatar is not centered inside its player seat");
            Check(image.Stretch == Stretch.Uniform && RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.NearestNeighbor,
                "Minecraft avatars must use uniform nearest-neighbor scaling");
        }
    }
    private static void AssertPanelAlignment(HybridRooms view)
    {
        var left = Control<FrameworkElement>(view, "PlayerPanelSurface");
        var right = Control<FrameworkElement>(view, "NetworkPanelSurface");
        var a = left.TransformToAncestor(view).TransformBounds(new Rect(0, 0, left.ActualWidth, left.ActualHeight));
        var b = right.TransformToAncestor(view).TransformBounds(new Rect(0, 0, right.ActualWidth, right.ActualHeight));
        Check(Math.Abs(a.Top - b.Top) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1, "Player and network panels must align at both top and bottom");
    }
    private static void AssertAvatarComposition()
    {
        var encode = typeof(HybridRooms).Assembly.GetType("PCL.RoomPresentation")!.GetMethod("EncodeAvatar", BindingFlags.Static | BindingFlags.NonPublic)!;
        var face = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        var hat = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 128 }, 4);
        var png = (string)encode.Invoke(null, [face, hat])!;
        new RoomPresence(png).Validate();
        using (var stream = new MemoryStream(Convert.FromBase64String(png)))
        {
            var image = new FormatConvertedBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            var sample = new byte[4]; image.CopyPixels(new Int32Rect(0, 0, 1, 1), sample, 4, 0);
            Check(Math.Abs(sample[0] - 128) <= 1 && Math.Abs(sample[2] - 127) <= 1 && sample[3] == 255, "Avatar hat alpha composition is incorrect");
        }
        var rectangular = BitmapSource.Create(1, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }, 4);
        using (var stream = new MemoryStream(Convert.FromBase64String((string)encode.Invoke(null, [rectangular, null])!)))
        {
            var image = new FormatConvertedBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            var edge = new byte[4]; var center = new byte[4];
            image.CopyPixels(new Int32Rect(0, 32, 1, 1), edge, 4, 0); image.CopyPixels(new Int32Rect(32, 32, 1, 1), center, 4, 0);
            Check(edge[3] == 0 && center[3] == 255, "Avatar input must retain its aspect ratio and transparent padding");
        }
    }
    private static async Task AssertGameLinkPanel(HybridRooms view, string scratch, string screenshots)
    {
        var panel = Control<Expander>(view, "GameLinkPanel");
        Check(!panel.IsExpanded, "Game link controls must not crowd compact room layouts by default");
        panel.IsExpanded = true;
        view.UpdateLayout();
        Invoke(view, "RefreshGameLink");
        Check(!ButtonById(view, "Rooms.GameConnect").IsEnabled, "Switch server must be disabled without an Agent");
        Check(!ButtonById(view, "Rooms.GameRoom").IsEnabled, "Unverified room route must not be joinable");
        AssertInViewport(view, ButtonById(view, "Rooms.GameLaunch"), "Game launch control");
        var jar = Path.Combine(scratch, "agent-fixture.jar"); File.WriteAllText(jar, "test");
        using var game = new PCL.Core.Minecraft.Launcher.GameLinkSession("模拟客户端");
        var info = new ProcessStartInfo("java") { UseShellExecute = false };
        game.Configure(info, jar);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, int.Parse(info.Environment["MOONCL_LINK_PORT"]!));
        using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("HELLO\t1\t" + info.Environment["MOONCL_LINK_TOKEN"]);
        Check(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "OK", "Mock Agent authentication failed");
        await writer.WriteLineAsync("STATE\t\tready\t\t");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (game.State.State != "ready" && DateTime.UtcNow < deadline) await Task.Delay(20);
        Invoke(view, "RefreshGameLink");
        Check(Control<ComboBox>(view, "GameLinkClients").SelectedValue as string == game.Id, "Only running game should be selected automatically");
        view.UpdateLayout();
        Check(!Descendants(Control<ComboBox>(view, "GameLinkClients")).OfType<TextBlock>().Any(t => t.Text.Contains("GameLinkChoice")), "Game selector must show its label, not a record dump");
        Control<TextBox>(view, "GameLinkAddress").Text = "play.example.com:25565";
        Click(view, "Rooms.GameConnect");
        var command = (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)))!.Split('\t');
        Check(command.Length == 3 && command[0] == "CONNECT" && Encoding.UTF8.GetString(Convert.FromBase64String(command[2])) == "play.example.com:25565", "Server switch was not delivered to the selected game");
        await writer.WriteLineAsync("STATE\t" + command[1] + "\tconnected\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes("play.example.com:25565")) + "\t");
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (game.State.State != "connected" && DateTime.UtcNow < deadline) await Task.Delay(20);
        Invoke(view, "RefreshGameLink");
        Check(Control<TextBlock>(view, "GameLinkStatus").Text.Contains("已进入服务器") && Control<TextBlock>(view, "GameLinkStatus").Text.Contains("play.example.com:25565"), "Actual connected state is not displayed");
        Screenshot(view, Path.Combine(screenshots, "multiplayer-game-link.png"));
        Click(view, "Rooms.GameDisconnect");
        Check((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)))!.StartsWith("DISCONNECT\t"), "Disconnect command missing");
        game.Dispose();
        Invoke(view, "RefreshGameLink");
        Check(!ButtonById(view, "Rooms.GameConnect").IsEnabled, "Ended game must no longer be controllable");
        panel.IsExpanded = false;
    }
    private static void Run(string[] args)
    {
        Directory.CreateDirectory(args[1]); Directory.CreateDirectory(args[2]);
        var app = new System.Windows.Application();
        var host = NewView(Path.Combine(args[1], "host-preferences.json"));
        var guest = NewView(Path.Combine(args[1], "guest-preferences.json"));
        var window = new Window { Width = 1080, Height = 900, Left = -10000, Top = -10000, Content = host, ShowInTaskbar = false };
        window.Loaded += async (_, _) =>
        {
            try
            {
                AssertAvatarComposition();
                Check(Control<ComboBox>(host, "Coordinator").SelectedIndex == 0, "Official must be the default");
                Check(Control<TextBox>(host, "CustomUrl").Visibility == Visibility.Collapsed, "Official mode must hide the URL");
                Check(Control<FrameworkElement>(host, "HallPanel").Visibility == Visibility.Visible, "Hall is the initial view");
                await ResizeViewport(window, host, 750, 540);
                AssertInViewport(host, ButtonById(host, "Rooms.OpenCreate"), "Create entry at compact size");
                AssertInViewport(host, ButtonById(host, "Rooms.OpenJoin"), "Join entry at compact size");
                Screenshot(host, Path.Combine(args[2], "multiplayer-hall-compact.png"));
                await ResizeViewport(window, host, 1080, 860);
                AssertPrivate(host);
                await AssertGameLinkPanel(host, args[1], args[2]);
                if (args.Contains("--game-link-only"))
                {
                    Console.WriteLine("GAME_LINK_UI_PASS · " + _checks + " checks (isolated mock Agent; no real Minecraft)");
                    return;
                }
                foreach (var entry in new[] { "Rooms.OpenCreate", "Rooms.OpenJoin" })
                {
                    var card = ButtonById(host, entry);
                    Check(card.ActualHeight > 120 && card.ActualWidth > 200, "Entry interaction must cover the entire card");
                    Check(!Descendants(card).OfType<Button>().Any(button => !ReferenceEquals(button, card)), "Hall entry still contains a nested button");
                }
                Screenshot(host, Path.Combine(args[2], "multiplayer-hall.png"));
                Configure(host, args[0], "房主烟测");
                var facePixels = new byte[8 * 8 * 4];
                for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
                {
                    var offset = (y * 8 + x) * 4; var alternate = (x + y) % 2 == 0;
                    facePixels[offset] = alternate ? (byte)45 : (byte)190;
                    facePixels[offset + 1] = alternate ? (byte)180 : (byte)105;
                    facePixels[offset + 2] = alternate ? (byte)240 : (byte)65;
                    facePixels[offset + 3] = 255;
                }
                var face = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, facePixels, 8 * 4); face.Freeze();
                host.UpdateLocalState("房主烟测", face, null, null);
                Click(host, "Rooms.OpenCreate"); await Idle(host);
                Check(!string.IsNullOrWhiteSpace(Control<TextBox>(host, "RoomName").Text), "Room name should be suggested automatically");
                Check(Control<FrameworkElement>(host, "Overlay").Visibility == Visibility.Collapsed, "Clicking the create card must create directly without a second button");
                var session = Field<RoomSession>(host, "_session");
                Check(session.IsHost && session.Room is not null, "Create failed: " + Control<TextBlock>(host, "Status").Text);
                Check(session.Room!.EffectiveRoutes.Length == 0, "Create must not need a transport");
                Check(session.Room.Members[0].Avatar.Length > 0, "PNG avatar did not round-trip through the coordinator");
                using (var png = new MemoryStream(Convert.FromBase64String(session.Room.Members[0].Avatar)))
                {
                    var avatar = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    Check(avatar.PixelWidth == 64 && avatar.PixelHeight == 64, "Avatar must preserve a crisp 64x64 source");
                    var pixels = new FormatConvertedBitmap(avatar, PixelFormats.Bgra32, null, 0);
                    var sample = new byte[64 * 64 * 4]; pixels.CopyPixels(sample, 64 * 4, 0);
                    var colors = Enumerable.Range(0, 64 * 64).Select(i => BitConverter.ToUInt32(sample, i * 4)).Distinct().Count();
                    Check(colors == 2, "Avatar scaling introduced blended pixels");
                }
                Check(Control<FrameworkElement>(host, "RoomPanel").Visibility == Visibility.Visible, "Create did not enter the room");
                Click(host, "Rooms.OpenSettings");
                var lockedToggle = Descendants(Control<ComboBox>(host, "Coordinator")).OfType<System.Windows.Controls.Primitives.ToggleButton>().First();
                var lockedBorder = Descendants(lockedToggle).OfType<Border>().First();
                Check(lockedBorder.Background is SolidColorBrush lockedBrush && lockedBrush.Color.R + lockedBrush.Color.G + lockedBrush.Color.B < 180, "Disabled coordinator dropdown lost its dark fill");
                Click(host, "Rooms.CloseOverlay");
                await ResizeViewport(window, host, 750, 540);
                AssertInViewport(host, ButtonById(host, "Rooms.AutoConnect"), "Auto connect at compact size");
                AssertInViewport(host, Control<ItemsControl>(host, "Members"), "Player seats at compact size");
                AssertSeatGeometry(host);
                AssertPanelAlignment(host);
                Screenshot(host, Path.Combine(args[2], "multiplayer-room-compact.png"));
                await ResizeViewport(window, host, 1080, 860);
                var presence = new RoomPresence(session.Room.Members[0].Avatar, new("NeoForge 联机实例", "1.21.1", "launching"));
                Set(host, "_presence", presence); await Refresh(host);
                Check(Control<TextBlock>(host, "HostGame").Text.Contains("正在启动"), "Host launch state missing");
                using var friend = new RoomSession(new RoomApiClient(args[0]));
                await friend.JoinAsync(session.Room.Code, new("好友烟测", "", Game: new("Fabric 实例", "1.21.1", "running")), CancellationToken.None);
                await Refresh(host);
                Check(Control<ItemsControl>(host, "Members").Items.Count == 2, "Member list missing friend");
                var labels = Descendants(Control<ItemsControl>(host, "Members")).OfType<TextBlock>().Where(t => t.Text.Contains("烟测")).ToArray();
                Check(labels.Length == 2, "Member name labels were not materialized");
                Check(labels.All(t => t.Foreground is SolidColorBrush brush && brush.Color.R > 180 && brush.Color.G > 180 && brush.Color.B > 180), "Member labels are unreadable on the dark background");
                foreach (var name in new[] { "IPv6 主线路", "IPv6 备用线路" })
                {
                    Click(host, "Rooms.OpenRouteEditor");
                    AssertNoScrollBars(host);
                    Control<TextBox>(host, "RouteName").Text = name;
                    Control<TextBox>(host, "Ipv6Address").Text = "2606:4700::1";
                    Click(host, "Rooms.PublishRoute"); await Idle(host);
                }
                Check(session.Room.EffectiveRoutes.Length == 2 && Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count == 2, "Multiple host routes not retained");
                Click(host, "Rooms.Back");
                Check(Control<FrameworkElement>(host, "HallPanel").Visibility == Visibility.Visible && session.Room is not null, "Back left membership");
                Check(Control<Border>(host, "CurrentCard").Visibility == Visibility.Visible, "Current-room hall card missing");
                Check(Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count == 2, "Back stopped transports");
                Screenshot(host, Path.Combine(args[2], "multiplayer-current-room.png"));
                Click(host, "Rooms.Reenter");
                Click(host, "Rooms.RemoveRoute"); await Idle(host);
                Check(session.Room!.EffectiveRoutes.Length == 1 && Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count == 1, "Removal affected the wrong transports");
                AssertPrivate(host); Screenshot(host, Path.Combine(args[2], "multiplayer-room.png"));
                Click(host, "Rooms.OpenRouteEditor");
                await ResizeViewport(window, host, 750, 540);
                for (var mode = 0; mode < 6; mode++)
                {
                    Click(host, "Rooms.RouteBasic");
                    Control<ComboBox>(host, "RouteMode").SelectedIndex = mode;
                    Click(host, "Rooms.RouteAdvanced");
                    var visible = new[] { "Ipv6Fields", "IpFields", "FrpFields", "ZtFields", "EtFields" }.Count(n => Control<FrameworkElement>(host, n).Visibility == Visibility.Visible);
                    Check(visible == (mode == 3 ? 0 : 1), "Mode-specific fields overlap");
                    AssertNoScrollBars(host);
                    AssertInViewport(host, ButtonById(host, "Rooms.PublishRoute"), "Publish button at compact size");
                    foreach (var field in new[] { "Ipv6Address", "CustomHost", "CustomPort", "PublicHost", "NetworkId", "ZtAddress", "EtAddress", "NetworkName", "NetworkSecret", "Peers" })
                        if (Control<FrameworkElement>(host, field).IsVisible) AssertInViewport(host, Control<FrameworkElement>(host, field), "Visible mode field " + field);
                }
                Invoke(host, "PreparePublication");
                Check(Control<TextBox>(host, "NetworkName").Text.StartsWith("mooncl-"), "EasyTier name not generated before publication");
                Check(Control<PasswordBox>(host, "NetworkSecret").Password.Length >= 32, "EasyTier secret not generated");
                Control<TextBox>(host, "NetworkName").Text = "custom-preserved-network";
                Control<TextBox>(host, "LocalPort").Text = "51234";
                Click(host, "Rooms.CloseOverlay"); Click(host, "Rooms.OpenRouteEditor");
                Check(Control<TextBox>(host, "NetworkName").Text == "custom-preserved-network", "Auto setup overwrote a manual network name");
                Check(Control<TextBox>(host, "LocalPort").Text == "51234", "Auto setup overwrote a manual port");
                Control<TextBox>(host, "LocalPort").Text = "25565";
                Check(Field<bool>(host, "_manualLocalPort"), "A manually entered default port must remain an explicit override");
                Click(host, "Rooms.RouteTools");
                var tools = Control<TabControl>(host, "ToolsPanel");
                for (var tab = 0; tab < tools.Items.Count; tab++)
                {
                    tools.SelectedIndex = tab; host.UpdateLayout();
                    var nested = Descendants(tools).OfType<TabControl>().FirstOrDefault(control => !ReferenceEquals(control, tools));
                    var pages = nested?.Items.Count ?? 1;
                    for (var sub = 0; sub < pages; sub++)
                    {
                        if (nested is not null) { nested.SelectedIndex = sub; host.UpdateLayout(); }
                        AssertNoScrollBars(host);
                        foreach (var field in new[] { "FrpcPath", "NodeHost", "NodePort", "NodeToken", "NodeCa", "EasyTierPath", "ZeroTierToken" })
                            if (Control<FrameworkElement>(host, field).IsVisible) AssertInViewport(host, Control<FrameworkElement>(host, field), "Tool field " + field);
                    }
                }
                tools.SelectedIndex = 1; host.UpdateLayout();
                AssertInViewport(host, ButtonById(host, "Rooms.CheckEasyTier"), "EasyTier check entry");
                AssertInViewport(host, ButtonById(host, "Rooms.GetEasyTier"), "EasyTier acquisition entry");
                var priorTool = Control<TextBox>(host, "EasyTierPath").Text;
                Control<TextBox>(host, "EasyTierPath").Text = Path.Combine(args[1], "missing-easytier-core.exe");
                Click(host, "Rooms.CheckEasyTier"); await Idle(host);
                Check(Control<TextBlock>(host, "EasyTierToolStatus").Text.Contains("工具面板"), "Missing EasyTier must show an actionable error inside the tools panel");
                Control<TextBox>(host, "EasyTierPath").Text = priorTool;
                var nativeEasyTier = Environment.GetEnvironmentVariable("MOONCL_TEST_EASYTIER_CORE");
                if (!string.IsNullOrWhiteSpace(nativeEasyTier) && File.Exists(nativeEasyTier))
                {
                    var leaseCount = Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count;
                    Control<TextBox>(host, "EasyTierPath").Text = nativeEasyTier;
                    Click(host, "Rooms.CheckEasyTier"); await Idle(host);
                    Check(Control<TextBlock>(host, "EasyTierToolStatus").Text.Contains("2.6.4"), "EasyTier tool check did not show the native version");
                    Check(Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count == leaseCount, "Version check changed room transports");
                }
                Screenshot(host, Path.Combine(args[2], "multiplayer-tools-compact.png"));
                Click(host, "Rooms.ReturnToRoute");
                Click(host, "Rooms.RouteAdvanced");
                Screenshot(host, Path.Combine(args[2], "multiplayer-setup-compact.png"));
                var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                host.RaiseEvent(escape);
                Check(Control<FrameworkElement>(host, "Overlay").Visibility == Visibility.Collapsed, "Escape must close the setup panel");
                await ResizeViewport(window, host, 1080, 860);
                await friend.LeaveAsync(CancellationToken.None);
                await using var fast = new FakeGame(5);
                await using var slow = new FakeGame(1200);
                var fastRoute = new RoomRoute("fast", "低延迟线路", new("frp", "127.0.0.1", fast.Port));
                var slowRoute = new RoomRoute("slow", "较慢线路", new("frp", "127.0.0.1", slow.Port));
                await session.PublishRoutesAsync([fastRoute, slowRoute], CancellationToken.None);
                await Refresh(host);
                Check(Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count == 0, "Updated routes retained obsolete resources");
                window.Content = guest; window.UpdateLayout(); Configure(guest, args[0], "访客烟测");
                Click(guest, "Rooms.OpenJoin");
                Control<TextBox>(guest, "RoomCode").Text = session.Room!.Code;
                Click(guest, "Rooms.Join"); await Idle(guest);
                Check(Field<RoomSession>(guest, "_session").Room?.EffectiveRoutes.Length == 2, "Guest did not receive both routes");
                Set(guest, "_presence", new RoomPresence("", new("访客实例", "1.21.1", "running")));
                Click(guest, "Rooms.AutoConnect");
                await Task.Delay(100); await Refresh(guest);
                Check(Field<bool>(guest, "_busy"), "Probe fixture must still be running during heartbeat");
                await session.RefreshAsync(CancellationToken.None);
                Check(session.Room!.Members.Any(m => m.Name == "访客烟测" && m.Game?.State == "running"), "Slow probing starved presence heartbeat");
                await Idle(guest);
                Check(Target(guest)?.Port == fast.Port, "Auto selection did not choose the fastest game response");
                Control<ListBox>(guest, "RouteList").SelectedIndex = 1;
                Click(guest, "Rooms.Connect"); await Idle(guest);
                Check(Target(guest)?.Port == slow.Port, "Manual selection failed");
                var bad = new RoomRoute("bad", "不可达线路", new("frp", "127.0.0.1", FreePort()));
                await session.PublishRoutesAsync([fastRoute, slowRoute, bad], CancellationToken.None); await Refresh(guest);
                Control<ListBox>(guest, "RouteList").SelectedIndex = 2;
                Click(guest, "Rooms.Connect"); await Idle(guest);
                Check(Target(guest)?.Port == slow.Port, "A failed switch destroyed the working connection");
                Click(guest, "Rooms.Back");
                Check(Target(guest)?.Port == slow.Port, "Hall return destroyed guest connection");
                Click(guest, "Rooms.Reenter"); AssertPrivate(guest);
                Screenshot(guest, Path.Combine(args[2], "multiplayer-lines.png"));
                var extraMembers = new List<RoomSession>();
                try
                {
                    for (var i = 0; i < 14; i++)
                    {
                        var member = new RoomSession(new RoomApiClient(args[0])); extraMembers.Add(member);
                        await member.JoinAsync(session.Room!.Code, new("队员" + (i + 3), ""), CancellationToken.None);
                    }
                    await Refresh(guest);
                    Check(Field<RoomSession>(guest, "_session").Room!.Members.Length == 16, "Full room fixture was not created");
                    await ResizeViewport(window, guest, 750, 540);
                    var seenMembers = new HashSet<string>();
                    for (var page = 0; page < 2; page++)
                    {
                        var seats = Control<ItemsControl>(guest, "Members");
                        Check(seats.Items.Count == 8, "A full room must show eight seats per page");
                        foreach (var item in seats.Items)
                            seenMembers.Add((string)item.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!);
                        AssertNoScrollBars(guest); AssertInViewport(guest, seats, "Full-room seats at compact size");
                        AssertSeatGeometry(guest); AssertPanelAlignment(guest);
                        if (page < 1) Click(guest, "Rooms.MemberNext");
                    }
                    Check(seenMembers.Count == 16, "Member pagination lost players");
                    Check(!ButtonById(guest, "Rooms.MemberNext").IsEnabled, "Member pages must stop at the last page");
                    await Refresh(guest);
                    Check(Control<TextBlock>(guest, "MemberPageLabel").Text.Contains("2 / 2"), "Heartbeat unexpectedly reset the member page");
                    var manyRoutes = Enumerable.Range(0, 12).Select(i => fastRoute with { Id = "paged" + i, Name = "游戏线路 " + (i + 1) }).ToArray();
                    await session.PublishRoutesAsync(manyRoutes, CancellationToken.None); await Refresh(guest);
                    var seenRoutes = new HashSet<string>();
                    for (var page = 0; page < 4; page++)
                    {
                        var list = Control<ListBox>(guest, "RouteList");
                        Check(list.Items.Count <= 3 && list.Items.Count > 0, "Routes must use bounded pages");
                        foreach (var item in list.Items)
                            seenRoutes.Add(((RoomRoute)item.GetType().GetProperty("Route", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!).Id);
                        AssertNoScrollBars(guest); AssertInViewport(guest, list, "Route pages at compact size");
                        if (page < 3) Click(guest, "Rooms.RouteNext");
                    }
                    Check(seenRoutes.Count == 12, "Route pagination lost routes");
                    Control<ListBox>(guest, "RouteList").SelectedIndex = 2;
                    Click(guest, "Rooms.Connect"); await Idle(guest);
                    Check(Target(guest)?.Port == fast.Port, "Last-page route cannot be connected");
                    Screenshot(guest, Path.Combine(args[2], "multiplayer-full-room-compact.png"));
                    await ResizeViewport(window, guest, 1080, 860);
                }
                finally { foreach (var member in extraMembers) { try { await member.LeaveAsync(CancellationToken.None); } finally { member.Dispose(); } } }
                await session.PublishRoutesAsync([fastRoute], CancellationToken.None); await Refresh(guest);
                Check(Target(guest) is null && Field<Dictionary<string, RoomTransportLease>>(guest, "_leases").Count == 0, "Deleted route remained selectable");
                Click(guest, "Rooms.Leave"); await Idle(guest);
                Check(Field<RoomSession?>(guest, "_session") is null, "Guest leave retained membership");
                window.Content = host; window.UpdateLayout(); Click(host, "Rooms.Leave"); await Idle(host);
                Check(Field<RoomSession?>(host, "_session") is null, "Host dissolve retained membership");
                foreach (var file in Directory.GetFiles(args[1], "*-preferences.json"))
                {
                    var text = File.ReadAllText(file);
                    Check(!text.Contains(new Uri(RoomServiceDirectory.OfficialUrl).Host), "Preferences leaked the official address");
                    Check(!text.Contains("Token") && !text.Contains("Secret"), "Preferences persisted sensitive fields");
                }
                Console.WriteLine($"ROOM_UI_HTTP_SMOKE_PASS: {_checks} checks, actual WPF + Java HTTP + game probe fixtures");
            }
            catch (Exception ex)
            {
                try { Screenshot((HybridRooms)window.Content, Path.Combine(args[2], "multiplayer-failure.png")); } catch { }
                Console.Error.WriteLine(ex); Environment.ExitCode = 1;
            }
            finally { host.Dispose(); guest.Dispose(); window.Close(); app.Shutdown(); }
        };
        app.Run(window);
    }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private sealed class FakeGame : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Task _loop;
        private readonly int _delay;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public FakeGame(int delay) { _delay = delay; _listener.Start(); _loop = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _clients.Add(ReplyAsync(client)); } }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task ReplyAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream(); var length = new byte[1]; await stream.ReadExactlyAsync(length, _stop.Token);
                    if (length[0] >= 128) throw new IOException("Unexpected fixture handshake size.");
                    var request = new byte[length[0] + 2]; await stream.ReadExactlyAsync(request, _stop.Token);
                    await Task.Delay(_delay, _stop.Token);
                    var json = Encoding.UTF8.GetBytes("{\"version\":{\"name\":\"1.21.1\",\"protocol\":767}}");
                    var packet = new byte[json.Length + 3]; packet[0] = (byte)(json.Length + 2); packet[1] = 0; packet[2] = (byte)json.Length; json.CopyTo(packet, 3);
                    await stream.WriteAsync(packet, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _loop; await Task.WhenAll(_clients.ToArray()); _stop.Dispose(); }
    }
}
