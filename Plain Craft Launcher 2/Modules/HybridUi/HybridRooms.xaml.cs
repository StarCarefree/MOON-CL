using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PCL.Core.Multiplayer;

namespace PCL;

public partial class HybridRooms : UserControl, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _heartbeat;
    private RoomSession? _session;
    private FrpProcess? _tunnel;
    private RoomConnection? _seenConnection;
    private int _visitorPort;
    private bool _busy, _closed, _initialized;
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MOONCL", "multiplayer.json");
    private string Mode => FrpMode.IsChecked == true ? "frp" : P2pMode.IsChecked == true ? "p2p" : "ipv6";

    public HybridRooms()
    {
        InitializeComponent();
        LoadSettings();
        _initialized = true;
        _heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _heartbeat.Tick += Heartbeat_Tick;
        _heartbeat.Start();
        Render();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e) { if (_initialized) Render(); }
    private void Render()
    {
        var room = _session?.Room;
        var joined = room is not null;
        var connection = room?.Connection;
        if (_seenConnection != connection)
        {
            if (_session?.IsHost != true) StopTunnel();
            _seenConnection = connection;
        }
        IdentityPanel.IsEnabled = !_busy && !joined;
        EntryForms.Visibility = joined ? Visibility.Collapsed : Visibility.Visible;
        EntryForms.IsEnabled = !_busy;
        CurrentCard.Visibility = joined ? Visibility.Visible : Visibility.Collapsed;
        Ipv6Fields.Visibility = Mode == "ipv6" ? Visibility.Visible : Visibility.Collapsed;
        FrpFields.Visibility = Mode == "frp" ? Visibility.Visible : Visibility.Collapsed;
        ModeHelp.Text = Mode switch
        {
            "frp" => "需自备 frps 节点及远程端口；游戏流量经过该节点。",
            "p2p" => "使用 frp XTCP 打洞，游戏流量尽量点对点传输。需自备支持 XTCP 的 frps 节点。",
            _ => "双方需具备 IPv6；房主须手动允许游戏 / LAN 端口入站。"
        };
        NodeCard.Visibility = (!joined && Mode != "ipv6") || (joined && connection?.Mode == "p2p") ? Visibility.Visible : Visibility.Collapsed;
        NodeCard.IsEnabled = !_busy && (!joined || _session?.IsHost != true);
        NodeAddressFields.Visibility = joined ? Visibility.Collapsed : Visibility.Visible;
        VisitorFields.Visibility = joined && _session?.IsHost != true ? Visibility.Visible : Visibility.Collapsed;
        LeaveButton.IsEnabled = !_busy;
        LeaveButton.Content = _session?.IsHost == true ? "解散房间" : "退出房间";
        ConnectButton.IsEnabled = !_busy && connection is not null && _session?.IsFresh == true;
        var target = GetLaunchTarget();
        Endpoint.Text = target is null ? "" : RoomConnection.FormatEndpoint(target.Host, target.Port);
        CopyAddressButton.IsEnabled = !_busy && target is not null;
        if (room is null) return;
        RoomTitle.Text = room.Name + (_session?.IsHost == true ? " · 你是房主" : " · 已加入");
        Invitation.Text = room.Code;
        Members.ItemsSource = room.Members.Select(member => member.Name + (member.IsHost ? "  · 房主" : "") +
            (member.Id == _session?.Ticket?.MemberId ? "  · 你" : "")).ToArray();
        ConnectionInfo.Text = connection is null ? "等待房主发布游戏连接信息。" : connection.Mode switch
        {
            "p2p" => $"P2P / frp XTCP · 节点 {RoomConnection.FormatEndpoint(connection.ServerHost, connection.ServerPort)} · " +
                (_session?.IsHost == true ? "房主隧道已启动，访客需建立连接。" : _tunnel?.IsRunning == true ? "访客进程运行中；请检测游戏端口。" : "请在下方配置本机 frpc 与节点凭据，然后建立连接。"),
            "frp" => "FRP / TCP · 访客无需安装 frpc；检测通过后可复制地址到游戏。",
            _ => "IPv6 直连 · 检测通过后可复制地址到游戏。"
        };
    }

    /// <summary>Reserved for one-click launch. Does not start a process or claim network reachability.</summary>
    internal RoomLaunchTarget? GetLaunchTarget()
    {
        if (_session is not { IsFresh: true, Room: { Connection: { } connection } room }) return null;
        connection.Validate();
        if (connection.Mode == "p2p")
        {
            if (_session.IsHost) return null;
            return _tunnel?.IsRunning == true ? new RoomLaunchTarget("127.0.0.1", _visitorPort, room.Code) : null;
        }
        if (_session.IsHost && connection.Mode == "frp" && _tunnel?.IsRunning != true) return null;
        return new RoomLaunchTarget(connection.Address, connection.Port, room.Code);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) return;
        _busy = true; Render();
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) Status.Text = "操作超时或已取消，请重试。"; }
        catch (Exception ex)
        {
            // Show a bounded error, but never log room credentials, descriptors, or frpc output.
            if (!_closed) Status.Text = ex.Message.Length <= 600 ? ex.Message : "联机操作失败，请检查配置和网络。";
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void OpenSession()
    {
        if (string.IsNullOrWhiteSpace(PlayerName.Text)) throw new ArgumentException("请填写玩家名称。");
        _session?.Dispose();
        _session = new RoomSession(new RoomApiClient(ServiceUrl.Text));
        SaveSettings();
    }
    private FrpNode ReadNode() => new(NodeHost.Text.Trim(), ParsePort(NodePort.Text), NodeToken.Password, NodeCa.Text.Trim());
    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, out var port)) throw new ArgumentException("请输入有效端口。");
        RoomConnection.CheckPort(port); return port;
    }

    private async void Create_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(RoomName.Text)) throw new ArgumentException("请填写房间名称。");
        OpenSession();
        try
        {
            var local = ParsePort(LocalPort.Text);
            RoomConnection connection;
            if (Mode == "ipv6") connection = new RoomConnection("ipv6", Ipv6Address.Text.Trim(), local);
            else
            {
                var node = ReadNode();
                connection = Mode == "p2p" ? FrpConfiguration.CreateP2p(node) : new RoomConnection("frp", PublicHost.Text.Trim(), ParsePort(RemotePort.Text));
                var config = FrpConfiguration.Host(node, connection, local);
                Status.Text = "正在校验配置并启动 frpc；进程启动不代表游戏端口可达……";
                await StartTunnelAsync(config);
            }
            connection.Validate();
            Status.Text = "正在创建房间……";
            await _session!.CreateAsync(new CreateRoomRequest(RoomName.Text.Trim(), PlayerName.Text.Trim(), CreatePassword.Password, connection), _lifetime.Token);
            CreatePassword.Clear(); JoinPassword.Clear();
            Status.Text = "房间已创建。将邀请码分享给好友；本轮不会自动启动游戏，请自行打开世界并开放到局域网。";
        }
        catch { StopTunnel(); _session?.Dispose(); _session = null; throw; }
    });

    private async void Join_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        OpenSession();
        try
        {
            Status.Text = "正在加入房间……";
            await _session!.JoinAsync(RoomCode.Text, new JoinRoomRequest(PlayerName.Text.Trim(), JoinPassword.Password), _lifetime.Token);
            JoinPassword.Clear(); CreatePassword.Clear();
            Status.Text = "已加入房间。请建立 / 检测连接；加入房间不等于游戏网络已连通。";
        }
        catch { _session?.Dispose(); _session = null; throw; }
    });

    private async void Leave_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        StopTunnel();
        var session = _session;
        try
        {
            if (session is not null) await session.LeaveAsync(_lifetime.Token);
            Status.Text = "已退出房间，本机隧道已清理。";
        }
        catch { Status.Text = "本机已退出并清理隧道；服务端通知失败，将在心跳超时后自动移除。"; }
        finally { session?.Dispose(); _session = null; NodeToken.Clear(); }
    });

    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_session is not { IsFresh: true, Room.Connection: { } connection }) throw new InvalidOperationException("房间已过期或房主尚未发布连接信息。");
        connection.Validate();
        if (connection.Mode == "p2p" && !_session.IsHost && _tunnel?.IsRunning != true)
        {
            StopTunnel();
            _visitorPort = ParsePort(VisitorPort.Text);
            var credentials = new FrpNode(connection.ServerHost, connection.ServerPort, NodeToken.Password, NodeCa.Text.Trim());
            Status.Text = "正在启动 P2P 访客进程……";
            await StartTunnelAsync(FrpConfiguration.Visitor(credentials, connection, _visitorPort));
            SaveSettings();
        }
        var target = GetLaunchTarget();
        if (target is null)
        {
            if (_session.IsHost && connection.Mode == "p2p")
            { Status.Text = _tunnel?.IsRunning == true ? "房主 P2P 进程运行中；端到端检测需由另一台设备的访客执行。" : "房主 P2P 进程已退出，请解散后检查节点并重新创建房间。"; return; }
            throw new InvalidOperationException("连接尚未建立。请检查 frpc 后重试。");
        }
        Status.Text = "正在检测远端 Minecraft 状态（最多等待 8 秒）……";
        var version = await RoomConnectivity.ProbeAsync(target, _lifetime.Token);
        Status.Text = $"游戏端点已响应 · {version}。可以复制地址进入游戏；模组和存档兼容性仍需自行确认。";
    });

    private async void Heartbeat_Tick(object? sender, EventArgs e)
    {
        if (_session?.Room is null || _busy || _closed) return;
        await RunAsync(async () =>
        {
            try
            {
                await _session!.RefreshAsync(_lifetime.Token);
                if (_session.Room is null) StopTunnel();
                if (_tunnel is not null && !_tunnel.IsRunning)
                { StopTunnel(); Status.Text = "frpc 进程已退出，游戏连接不可用；访客可重试，房主请重新建房。"; }
            }
            catch
            {
                if (_session?.Room is null || _session.IsFresh != true)
                { StopTunnel(); _session?.Dispose(); _session = null; }
                throw;
            }
        });
    }
    private async Task StartTunnelAsync(string config)
    {
        var tunnel = await FrpProcess.StartAsync(FrpcPath.Text.Trim(), config, _lifetime.Token);
        if (_closed) { tunnel.Dispose(); throw new OperationCanceledException(); }
        _tunnel = tunnel;
    }
    private void StopTunnel() { _tunnel?.Dispose(); _tunnel = null; _visitorPort = 0; }
    private void CopyCode_Click(object sender, RoutedEventArgs e) => Copy(_session?.Room?.Code);
    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (GetLaunchTarget() is { } target) Copy(RoomConnection.FormatEndpoint(target.Host, target.Port));
    }
    private void Copy(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try { Clipboard.SetText(value); Status.Text = "已复制。"; }
        catch (System.Runtime.InteropServices.ExternalException) { Status.Text = "剪贴板被占用，请稍后重试。"; }
    }
    private void SelectFrpc_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "frpc.exe|frpc.exe|应用程序|*.exe", Title = "选择官方可信的 frpc.exe" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FrpcPath.Text = dialog.FileName;
    }
    private void DetectIpv6_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(n => n.Address)
                .Where(ip => RoomConnection.IsGlobalIpv6(ip.ToString()))
                .Select(ip => ip.ToString()).Distinct().ToArray();
            if (addresses.Length == 0) { Status.Text = "未检测到公网 IPv6。可选择 FRP 或 P2P；不会自动修改防火墙。"; return; }
            Ipv6Address.Text = addresses[0];
            Status.Text = "已填入本机公网 IPv6，请确认地址对应正确网卡并允许游戏端口入站。";
        }
        catch (NetworkInformationException) { Status.Text = "无法读取网卡信息，请手动填写 IPv6。"; }
    }

    private sealed record Preferences(string ServiceUrl = "", string PlayerName = "", string FrpcPath = "", string NodeHost = "", string NodePort = "7000", string NodeCa = "");
    private void LoadSettings()
    {
        try
        {
            var data = File.Exists(SettingsPath) && new FileInfo(SettingsPath).Length < 16384
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath)) : null;
            ServiceUrl.Text = data?.ServiceUrl ?? Environment.GetEnvironmentVariable("MOONCL_ROOM_SERVICE") ?? "";
            PlayerName.Text = data?.PlayerName ?? "";
            FrpcPath.Text = data?.FrpcPath ?? ""; NodeHost.Text = data?.NodeHost ?? "";
            NodePort.Text = data?.NodePort ?? "7000"; NodeCa.Text = data?.NodeCa ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Preferences(ServiceUrl.Text.Trim(), PlayerName.Text.Trim(), FrpcPath.Text.Trim(), NodeHost.Text.Trim(), NodePort.Text, NodeCa.Text.Trim())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; _heartbeat.Stop(); _heartbeat.Tick -= Heartbeat_Tick;
        _lifetime.Cancel(); StopTunnel();
        var session = _session; _session = null;
        if (session is not null) _ = CloseSessionAsync(session);
    }
    private static async Task CloseSessionAsync(RoomSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await session.LeaveAsync(timeout.Token); }
        catch (Exception) { /* Best effort; server TTL is the crash/offline fallback. */ }
        finally { session.Dispose(); }
    }
}
