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
        Check(button.IsEnabled, id + " is disabled"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        Control<ComboBox>(view, "Coordinator").SelectedIndex = 1;
        Control<TextBox>(view, "CustomUrl").Text = url;
        Control<TextBox>(view, "PlayerName").Text = name;
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
                Check(Control<ComboBox>(host, "Coordinator").SelectedIndex == 0, "Official must be the default");
                Check(Control<TextBox>(host, "CustomUrl").Visibility == Visibility.Collapsed, "Official mode must hide the URL");
                Check(Control<StackPanel>(host, "HallPanel").Visibility == Visibility.Visible, "Hall is the initial view");
                AssertPrivate(host); Screenshot(host, Path.Combine(args[2], "multiplayer-hall.png"));
                Configure(host, args[0], "房主烟测");
                var face = new DrawingImage(new GeometryDrawing(Brushes.Goldenrod, null, new RectangleGeometry(new Rect(0, 0, 8, 8)))); face.Freeze();
                host.UpdateLocalState("房主烟测", face, null, null);
                Click(host, "Rooms.Create"); await Idle(host);
                var session = Field<RoomSession>(host, "_session");
                Check(session.IsHost && session.Room is not null, "Create failed: " + Control<TextBlock>(host, "Status").Text);
                Check(session.Room!.EffectiveRoutes.Length == 0, "Create must not need a transport");
                Check(session.Room.Members[0].Avatar.Length > 0, "PNG avatar did not round-trip through the coordinator");
                Check(Control<StackPanel>(host, "RoomPanel").Visibility == Visibility.Visible, "Create did not enter the room");
                var lockedToggle = Descendants(Control<ComboBox>(host, "Coordinator")).OfType<System.Windows.Controls.Primitives.ToggleButton>().First();
                var lockedBorder = Descendants(lockedToggle).OfType<Border>().First();
                Check(lockedBorder.Background is SolidColorBrush lockedBrush && lockedBrush.Color.R + lockedBrush.Color.G + lockedBrush.Color.B < 180, "Disabled coordinator dropdown lost its dark fill");
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
                    Control<TextBox>(host, "RouteName").Text = name;
                    Control<TextBox>(host, "Ipv6Address").Text = "2606:4700::1";
                    Click(host, "Rooms.PublishRoute"); await Idle(host);
                }
                Check(session.Room.EffectiveRoutes.Length == 2 && Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count == 2, "Multiple host routes not retained");
                Click(host, "Rooms.Back");
                Check(Control<StackPanel>(host, "HallPanel").Visibility == Visibility.Visible && session.Room is not null, "Back left membership");
                Check(Control<Border>(host, "CurrentCard").Visibility == Visibility.Visible, "Current-room hall card missing");
                Check(Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count == 2, "Back stopped transports");
                Screenshot(host, Path.Combine(args[2], "multiplayer-current-room.png"));
                Click(host, "Rooms.Reenter");
                Click(host, "Rooms.RemoveRoute"); await Idle(host);
                Check(session.Room!.EffectiveRoutes.Length == 1 && Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count == 1, "Removal affected the wrong transports");
                AssertPrivate(host); Screenshot(host, Path.Combine(args[2], "multiplayer-room.png"));
                for (var mode = 0; mode < 5; mode++)
                {
                    Control<ComboBox>(host, "RouteMode").SelectedIndex = mode;
                    var visible = new[] { "Ipv6Fields", "FrpFields", "ZtFields", "EtFields" }.Count(n => Control<StackPanel>(host, n).Visibility == Visibility.Visible);
                    Check(visible == (mode == 2 ? 0 : 1), "Mode-specific fields overlap");
                }
                await friend.LeaveAsync(CancellationToken.None);
                await using var fast = new FakeGame(5);
                await using var slow = new FakeGame(1200);
                var fastRoute = new RoomRoute("fast", "低延迟线路", new("frp", "127.0.0.1", fast.Port));
                var slowRoute = new RoomRoute("slow", "较慢线路", new("frp", "127.0.0.1", slow.Port));
                await session.PublishRoutesAsync([fastRoute, slowRoute], CancellationToken.None);
                await Refresh(host);
                Check(Field<Dictionary<string, RoomTransportLease>>(host, "_leases").Count == 0, "Updated routes retained obsolete resources");
                window.Content = guest; window.UpdateLayout(); Configure(guest, args[0], "访客烟测");
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
            catch (Exception ex) { Console.Error.WriteLine(RoomServiceDirectory.FriendlyError(ex, false)); Console.Error.WriteLine(ex.StackTrace); Environment.ExitCode = 1; }
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
