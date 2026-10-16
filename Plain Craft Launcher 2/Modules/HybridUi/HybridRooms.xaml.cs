using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PCL.Core.Multiplayer;

namespace PCL;

public partial class HybridRooms : UserControl, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RoomTransportFactory _transports = new();
    private readonly Dictionary<string, RoomTransportLease> _leases = new(StringComparer.Ordinal);
    private readonly ObservableCollection<RoomMemberPresentation> _members = [];
    private readonly ObservableCollection<RoomRoutePresentation> _routes = [];
    private readonly RoomAvatarCache _avatars = new();
    private readonly DispatcherTimer _heartbeat;
    private CancellationTokenSource? _operation;
    private RoomSession? _session;
    private RoomPresence _presence = new();
    private ImageSource? _face, _hat;
    private DateTime _nextHeartbeat = DateTime.MinValue;
    private DateTime? _presenceDue;
    private string? _verifiedRouteId;
    private string _profileName = "";
    private bool _busy, _refreshing, _closed, _initialized, _inRoom, _roomView, _sessionOfficial = true;
    private readonly string _settingsPath;
    private string SettingsPath => _settingsPath;
    private bool IsOfficial => Coordinator.SelectedIndex != 1;
    private string Mode => RouteMode.SelectedIndex switch { 1 => "frp", 2 => "p2p", 3 => "zerotier", 4 => "easytier", _ => "ipv6" };
    internal bool IsInRoom => _inRoom;
    internal int OwnedLeaseCount => _leases.Count;
    internal RoomPresence LocalPresence => _presence;

    public HybridRooms() : this(null) { }

    internal HybridRooms(string? settingsPath)
    {
        _settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MOONCL", "multiplayer.json");
        InitializeComponent();
        LoadSettings();
        Members.ItemsSource = _memberPage; HallMembers.ItemsSource = _hallPage; RouteList.ItemsSource = _routePage;
        _initialized = true;
        SaveSettings(); // Migrate legacy URL fields without persisting the built-in address.
        _heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _heartbeat.Tick += Heartbeat_Tick;
        _heartbeat.Start();
        RenderMode(); Render();
    }

    /// <summary>Called by the native shell even while the hall or another page is displayed.</summary>
    public void UpdateLocalState(string playerName, ImageSource? face, ImageSource? hat, McInstance? selected)
    {
        if (_closed || !_initialized) return;
        _selectedInstance = selected ?? _selectedInstance;
        var name = RoomPresentation.Bounded(playerName, 32);
        if (_session?.Room is null && !PlayerName.IsKeyboardFocusWithin &&
            (string.IsNullOrWhiteSpace(PlayerName.Text) || PlayerName.Text == "Player" || PlayerName.Text == _profileName))
            PlayerName.Text = string.IsNullOrWhiteSpace(name) ? "Player" : name;
        _profileName = name;
        IdentitySummary.Text = PlayerName.Text + " · 联机大厅";
        var avatar = _presence.Avatar;
        if (!ReferenceEquals(_face, face) || !ReferenceEquals(_hat, hat))
        { _face = face; _hat = hat; avatar = RoomPresentation.EncodeAvatar(face, hat); }
        var next = new RoomPresence(avatar, RoomPresentation.LocalGame(selected));
        if (next == _presence) return;
        _presence = next;
        _presenceDue ??= DateTime.UtcNow.AddSeconds(3);
        // Update only text on the same view models; avatar bitmaps remain cached by source/base64.
        if (_session?.Ticket is { } ticket)
        {
            var self = ticket.Room.Members.FirstOrDefault(m => m.Id == ticket.MemberId);
            var view = _members.FirstOrDefault(m => m.Id == ticket.MemberId);
            if (self is not null) view?.Update(self with { Avatar = next.Avatar, Game = next.Game }, true, _avatars);
            if (_session.IsHost) HostGame.Text = CurrentHostGame.Text = "房主游戏：" + RoomPresentation.GameText(next.Game);
        }
    }

    private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void Render()
    {
        if (!_initialized || _closed) return;
        var room = _session?.Room;
        var joined = room is not null;
        if (!joined) _inRoom = false;
        HallPanel.Visibility = Show(!_inRoom); RoomPanel.Visibility = Show(_inRoom && joined);
        var roomView = RoomPanel.Visibility == Visibility.Visible;
        var viewChanged = roomView != _roomView;
        _roomView = roomView;
        IdentityPanel.IsEnabled = !_busy && !joined;
        CustomUrl.Visibility = Show(!IsOfficial);
        SecurityHint.Text = IsOfficial && RoomServiceDirectory.OfficialUsesPlaintext
            ? (_session is { IsFresh: true, Room: not null } && _sessionOfficial
                ? "已连接官方中控；当前为明文 HTTP，请勿复用重要密码。建议服务端尽快启用 HTTPS。"
                : "安全提示：官方中控暂用明文 HTTP，尚未确认部署可用。请勿复用重要密码；只有实际请求成功才代表已连接。")
            : "中控只协调房间，不转发游戏流量；自定义中控请优先使用可信 HTTPS 服务。";
        CurrentCard.Visibility = Show(joined);
        EntryForms.Visibility = Show(!joined); EntryForms.IsEnabled = !_busy && !joined;
        LeaveButton.IsEnabled = HallLeaveButton.IsEnabled = !_busy;
        LeaveButton.Content = HallLeaveButton.Content = _session?.IsHost == true ? "解散房间" : "退出房间";
        CancelButton.Visibility = Show(_busy);
        RouteEditor.IsEnabled = ConnectActions.IsEnabled = !_busy && joined;
        CreateForm.IsEnabled = JoinForm.IsEnabled = !_busy && !joined;
        ToolsPanel.IsEnabled = !_busy;
        EditorCard.Visibility = RemoveRouteButton.Visibility = Show(_session?.IsHost == true);
        RemoveRouteButton.IsEnabled = !_busy && SelectedRoute is not null;
        if (room is null)
        {
            ClearLeases(); _members.Clear(); _routes.Clear(); Endpoint.Text = ""; RefreshPages();
            if (viewChanged && IsVisible) PlayPageSwitch();
            return;
        }
        ReconcileLeases(room);
        RoomTitle.Text = CurrentTitle.Text = room.Name + (_session!.IsHost ? " · 你是房主" : " · 已加入") + (_session.IsFresh ? "" : " · 等待中控重新确认");
        Invitation.Text = room.Code;
        HostGame.Text = CurrentHostGame.Text = "房主游戏：" + RoomPresentation.GameText(room.Members.FirstOrDefault(m => m.IsHost)?.Game);
        foreach (var stale in _members.Where(v => !room.Members.Any(m => m.Id == v.Id)).ToArray()) _members.Remove(stale);
        foreach (var member in room.Members)
        {
            var view = _members.FirstOrDefault(v => v.Id == member.Id);
            if (view is null) { view = new RoomMemberPresentation(member.Id); _members.Add(view); }
            var self = member.Id == _session.Ticket?.MemberId;
            view.Update(self ? member with { Avatar = _presence.Avatar, Game = _presence.Game } : member, self, _avatars);
        }
        if (_session.IsHost) HostGame.Text = CurrentHostGame.Text = "房主游戏：" + RoomPresentation.GameText(_presence.Game);
        var published = room.EffectiveRoutes;
        foreach (var stale in _routes.Where(v => !published.Any(r => r.Id == v.Route.Id)).ToArray()) _routes.Remove(stale);
        foreach (var route in published)
        {
            var view = _routes.FirstOrDefault(v => v.Route.Id == route.Id);
            if (view is null) { view = new RoomRoutePresentation(route); _routes.Add(view); }
            else view.Update(route);
        }
        NoRoutes.Visibility = Show(published.Length == 0);
        RefreshPages();
        var target = GetLaunchTarget();
        Endpoint.Text = target is null ? "" : RoomConnection.FormatEndpoint(target.Host, target.Port);
        if (viewChanged && IsVisible) PlayPageSwitch();
    }

    internal void PlayPageSwitch()
    {
        if (Visibility != Visibility.Visible) return;
        if (Overlay.Visibility == Visibility.Visible)
        {
            HybridMotion.Enter("Hybrid Rooms Enter", OverlayFrame);
            return;
        }
        var sections = new List<FrameworkElement?>();
        if (LobbyContent.Children.Count > 0)
            sections.Add(LobbyContent.Children[0] as FrameworkElement);
        if (HallPanel.Visibility == Visibility.Visible)
        {
            foreach (var child in HallPanel.Children.OfType<FrameworkElement>())
            {
                if (!ReferenceEquals(child, EntryForms)) { sections.Add(child); continue; }
                if (EntryForms.Visibility != Visibility.Visible) continue;
                foreach (var card in EntryForms.Children.OfType<FrameworkElement>())
                    sections.Add(card);
            }
        }
        if (RoomPanel.Visibility == Visibility.Visible)
            foreach (var child in RoomPanel.Children.OfType<FrameworkElement>())
                sections.Add(child);
        var status = LobbyContent.Children.OfType<FrameworkElement>().LastOrDefault();
        if (status is not null && !ReferenceEquals(status, HallPanel) && !ReferenceEquals(status, RoomPanel))
            sections.Add(status);
        HybridMotion.Enter("Hybrid Rooms Enter", sections.ToArray());
    }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _closed) return;
        RenderMode();
        if (Overlay.Visibility == Visibility.Visible && RouteEditor.Visibility == Visibility.Visible) FillModeDefaults();
    }
    private void RenderMode()
    {
        Ipv6Fields.Visibility = Show(Mode == "ipv6"); FrpFields.Visibility = Show(Mode == "frp");
        ZtFields.Visibility = Show(Mode == "zerotier"); EtFields.Visibility = Show(Mode == "easytier");
        ModeHelp.Text = Mode switch
        {
            "frp" => "公网游戏端口与 frps 控制端口不同；请在本机工具设置填写该节点凭据。",
            "p2p" => "从本机工具设置读取 frps 节点；使用 XTCP 打洞，不保证成功，可同时发布其他线路。",
            "zerotier" => "需要本机 ZeroTier 服务；请自行在网络控制台授权成员。已有用户网络不会被退出。",
            "easytier" => "使用无 TUN 映射；访客将获得动态本机接入地址。网络密码仅在当前房间共享。",
            _ => "双方需具备公网 IPv6；请手动允许游戏端口入站，不会自动修改防火墙。"
        };
    }
    private void Coordinator_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _closed) return;
        SaveSettings(); Render();
    }
    private static bool ContainsOfficialAddress(string? value) => value?.Contains(new Uri(RoomServiceDirectory.OfficialUrl).Host, StringComparison.OrdinalIgnoreCase) == true;
    private void CustomUrl_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _closed) return;
        if (ContainsOfficialAddress(CustomUrl.Text)) { CustomUrl.Clear(); Coordinator.SelectedIndex = 0; }
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (!_initialized || _closed) return; _inRoom = false; Render(); }
    private void Reenter_Click(object sender, RoutedEventArgs e) { if (!_initialized || _closed) return; _inRoom = _session?.Room is not null; Render(); }
    private void Route_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _closed || _paging) return;
        if (RouteList.SelectedItem is RoomRoutePresentation selected) _selectedRouteId = selected.Route.Id;
        RemoveRouteButton.IsEnabled = !_busy && SelectedRoute is not null;
    }

    internal RoomLaunchTarget? GetLaunchTarget()
    {
        if (_closed || _session is not { IsFresh: true, Room: { } room } || _verifiedRouteId is null) return null;
        return room.EffectiveRoutes.Any(r => r.Id == _verifiedRouteId && r.Enabled) &&
            _leases.TryGetValue(_verifiedRouteId, out var lease) && lease.IsAlive ? lease.Target : null;
    }
    private void Guard(CancellationToken ct, RoomSession? session = null)
    {
        ct.ThrowIfCancellationRequested();
        if (_closed || (session is not null && (!ReferenceEquals(_session, session) || session.Room is null))) throw new OperationCanceledException(ct);
    }
    private string Error(Exception ex) => RoomServiceDirectory.FriendlyError(ex, _session is null ? IsOfficial : _sessionOfficial);
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!_initialized || _busy || _closed) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation; _busy = true; Render();
        try { await action(operation.Token); }
        catch (Exception ex) { if (!_closed) Status.Text = Error(ex); }
        finally { _operation = null; _busy = false; if (!_closed) Render(); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_initialized && !_closed) _operation?.Cancel(); }
    private RoomSession OpenSession()
    {
        if (_session?.Room is not null) throw new InvalidOperationException("请先明确退出或解散当前房间。");
        if (string.IsNullOrWhiteSpace(PlayerName.Text)) PlayerName.Text = "Player";
        _session?.Dispose();
        _sessionOfficial = IsOfficial;
        _session = new RoomSession(new RoomApiClient(IsOfficial ? RoomServiceDirectory.OfficialUrl : CustomUrl.Text.Trim()));
        SaveSettings(); return _session;
    }
    private async void Create_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        SuggestRoomName();
        if (string.IsNullOrWhiteSpace(RoomName.Text)) throw new ArgumentException("请填写房间名称。");
        var session = OpenSession();
        try
        {
            Status.Text = "正在请求创建房间……";
            await session.CreateAsync(new CreateRoomRequest(RoomName.Text.Trim(), PlayerName.Text.Trim(), CreatePassword.Password,
                Connection: null, Avatar: _presence.Avatar, Game: _presence.Game), ct);
            Guard(ct, session); EnterRoom();
            Status.Text = "房间已创建。现在可添加多条线路；不会自动启动 Minecraft。";
        }
        catch { if (session.Room is null) { session.Dispose(); if (ReferenceEquals(_session, session)) _session = null; } throw; }
    });
    private async void Join_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var session = OpenSession();
        try
        {
            Status.Text = "正在请求加入房间……";
            await session.JoinAsync(RoomCode.Text.Trim(), new JoinRoomRequest(PlayerName.Text.Trim(), JoinPassword.Password, _presence.Avatar, _presence.Game), ct);
            Guard(ct, session); EnterRoom();
            Status.Text = "已加入房间。请选择游戏线路进行真实握手检测，房间成员身份不代表游戏网络可达。";
        }
        catch { if (session.Room is null) { session.Dispose(); if (ReferenceEquals(_session, session)) _session = null; } throw; }
    });
    private void EnterRoom()
    {
        CreatePassword.Clear(); JoinPassword.Clear(); _inRoom = true; CloseOverlay();
        _nextHeartbeat = DateTime.UtcNow.AddSeconds(15); _presenceDue = null;
    }
    private async void Leave_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var session = _session;
        if (session is null) return;
        try
        {
            await session.LeaveAsync(ct); Guard(ct);
            Status.Text = "已退出房间，已清理本房间拥有的本机传输。";
        }
        finally
        {
            session.Dispose(); if (ReferenceEquals(_session, session)) _session = null;
            ClearLeases();
            if (!_closed) { _inRoom = false; NodeToken.Clear(); ZeroTierToken.Clear(); NetworkSecret.Clear(); }
        }
    });

    private static int ParsePort(string text)
    {
        if (!int.TryParse(text, out var port)) throw new ArgumentException("请输入有效端口。");
        RoomConnection.CheckPort(port); return port;
    }
    private FrpNode ReadNode() => new(NodeHost.Text.Trim(), ParsePort(NodePort.Text), NodeToken.Password, NodeCa.Text.Trim());
    private RoomTransportSettings ReadTools(RoomRoute route, bool host)
    {
        FrpNode? node = null;
        if (route.Connection.Mode is "frp" or "p2p")
            node = !host && route.Connection.Mode == "p2p"
                ? new FrpNode(route.Connection.ServerHost, route.Connection.ServerPort, NodeToken.Password, NodeCa.Text.Trim())
                : host ? ReadNode() : null;
        return new RoomTransportSettings(FrpcPath.Text.Trim(), node, EasyTierPath.Text.Trim(), ZeroTierToken.Password);
    }
    private RoomConnection ReadConnection(int local) => Mode switch
    {
        "frp" => new RoomConnection("frp", PublicHost.Text.Trim(), ParsePort(RemotePort.Text)),
        "p2p" => FrpConfiguration.CreateP2p(ReadNode()),
        "zerotier" => new RoomConnection("zerotier", ZtAddress.Text.Trim(), local, NetworkId: NetworkId.Text.Trim()),
        "easytier" => new RoomConnection("easytier", EtAddress.Text.Trim(), local, SecretKey: NetworkSecret.Password,
            NetworkName: NetworkName.Text.Trim(), Peers: Peers.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        _ => new RoomConnection("ipv6", Ipv6Address.Text.Trim(), local)
    };
    private async void AddRoute_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var session = RequireRoom(true);
        PreparePublication();
        var local = ParsePort(LocalPort.Text);
        var route = new RoomRoute(Guid.NewGuid().ToString("N"), RouteName.Text.Trim(), ReadConnection(local));
        var routes = session.Room!.EffectiveRoutes.Append(route).ToArray(); RoomRoute.ValidateAll(routes);
        SaveSettings();
        RoomTransportLease? lease = null;
        try
        {
            Status.Text = "正在启动此线路的本机传输；启动进程不代表游戏可达……";
            lease = await _transports.CreateAsync(route, session.Room.Code, true, local, ReadTools(route, true), ct);
            Guard(ct, session);
            await session.PublishRoutesAsync(routes, ct);
            Guard(_lifetime.Token, session); // A completed publication remains owned even if Cancel raced its response.
            _leases[route.Id] = lease; lease = null;
            _selectedRouteId = route.Id; Render(); CloseOverlay();
            Status.Text = "线路已发布，其他线路与本机传输保持不变；访客需检测真实游戏握手。";
        }
        finally { lease?.Dispose(); }
    });
    private async void RemoveRoute_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var session = RequireRoom(true);
        if (SelectedRoute is not { } selected) throw new InvalidOperationException("请选择要移除的线路。");
        await session.PublishRoutesAsync(session.Room!.EffectiveRoutes.Where(r => r.Id != selected.Route.Id).ToArray(), ct);
        Guard(ct, session);
        // Only after the server accepts removal may the corresponding owned transport stop.
        RemoveLease(selected.Route.Id);
        Status.Text = "所选线路已移除；其他线路与传输保持运行。";
    });
    private RoomSession RequireRoom(bool host = false)
    {
        if (_session is not { IsFresh: true, Room: not null } session) throw new InvalidOperationException("房间身份尚未确认或已经过期，请等待心跳更新。");
        if (host && !session.IsHost) throw new InvalidOperationException("只有房主可以发布或移除线路。");
        return session;
    }
    private async void AutoConnect_Click(object sender, RoutedEventArgs e) => await RunAsync(ct => ConnectAsync(null, ct));
    private async void ManualConnect_Click(object sender, RoutedEventArgs e) => await RunAsync(ct =>
        SelectedRoute is { } selected ? ConnectAsync(selected.Route.Id, ct) : throw new InvalidOperationException("请选择一条线路。"));
    private async Task ConnectAsync(string? manual, CancellationToken ct)
    {
        var session = RequireRoom();
        var routes = session.Room!.EffectiveRoutes.Where(r => r.Enabled && (manual is null || r.Id == manual)).ToArray();
        if (routes.Length == 0) throw new InvalidOperationException("没有可检测的启用线路，请等待房主发布。");
        var candidates = new Dictionary<string, RoomTransportLease>(StringComparer.Ordinal);
        var created = new Dictionary<string, RoomTransportLease>(StringComparer.Ordinal);
        var results = new List<RoomRouteProbeResult>();
        SaveSettings();
        try
        {
            foreach (var route in routes)
            {
                Guard(ct, session);
                var view = _routes.FirstOrDefault(v => v.Route.Id == route.Id);
                view?.SetResult("正在建立传输并检测 Minecraft 握手……");
                Status.Text = "正在逐条检测游戏线路；房间心跳独立保持。";
                RoomTransportLease? lease = null;
                var isNew = false;
                try
                {
                    if (_leases.TryGetValue(route.Id, out var cached) && cached.IsAlive && SameRoute(cached.Route, route)) lease = cached;
                    else
                    {
                        if (session.IsHost) throw new InvalidOperationException("房主此线路的传输未运行，请移除后重新发布。其他线路不受影响。");
                        lease = await _transports.CreateAsync(route, session.Room!.Code, false, 25565, ReadTools(route, false), ct);
                        isNew = true; created[route.Id] = lease; Guard(ct, session);
                    }
                    candidates[route.Id] = lease;
                    var result = await RoomRouteProbe.ProbeAsync(route, lease, ct);
                    Guard(ct, session);
                    if (!session.Room!.EffectiveRoutes.Any(r => SameRoute(r, route))) throw new InvalidOperationException("此线路已被房主更新，请重试。");
                    results.Add(result);
                    view?.SetResult(ProbeText(result, lease));
                    if (!result.Reachable && isNew) { lease.Dispose(); created.Remove(route.Id); candidates.Remove(route.Id); }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Guard(ct, session);
                    view?.SetResult(Error(ex));
                    if (isNew && lease is not null) { lease.Dispose(); created.Remove(route.Id); candidates.Remove(route.Id); }
                }
            }
            var winner = RoomRouteSelector.Choose(results, manual);
            if (winner is null || !candidates.TryGetValue(winner.RouteId, out var winningLease) || !winningLease.IsAlive)
            { Status.Text = "未发现可响应 Minecraft 握手的线路。原有已验证连接不会因切换失败而被停止。"; return; }
            Guard(ct, session);
            if (!session.Room!.EffectiveRoutes.Any(r => SameRoute(r, winningLease.Route))) throw new InvalidOperationException("所选线路已更新，请重新检测。");
            if (_leases.TryGetValue(winner.RouteId, out var replaced) && !ReferenceEquals(replaced, winningLease)) replaced.Dispose();
            _leases[winner.RouteId] = winningLease; created.Remove(winner.RouteId);
            _verifiedRouteId = winner.RouteId;
            if (!session.IsHost)
                foreach (var id in _leases.Keys.Where(id => id != winner.RouteId).ToArray()) RemoveLease(id);
            _selectedRouteId = winner.RouteId; RefreshPages();
            Status.Text = "已选择可响应游戏握手的线路 · " + (winner.GameLatencyMs?.ToString() ?? "—") + " ms。可复制接入地址；不会自动启动游戏。";
        }
        finally { foreach (var lease in created.Values) lease.Dispose(); }
    }
    private string ProbeText(RoomRouteProbeResult result, RoomTransportLease lease)
    {
        var ping = lease.Target is { Host: "127.0.0.1" or "::1" or "localhost" } ? "不适用（本机代理）" : result.PingMs is { } ms ? ms + " ms" : "无响应 / 不可用";
        var text = result.Reachable ? "游戏握手 " + (result.GameLatencyMs?.ToString() ?? "—") + " ms · " + RoomPresentation.Bounded(result.Version) : "游戏未响应";
        text += " · ICMP Ping " + ping;
        if (!string.IsNullOrWhiteSpace(result.Error)) text += " · " + Error(new InvalidOperationException(result.Error));
        if (lease.Target is { } target) text += " · 接入 " + RoomConnection.FormatEndpoint(target.Host, target.Port);
        return text;
    }
    private static bool SameRoute(RoomRoute left, RoomRoute right) => left.Id == right.Id && left.Enabled == right.Enabled &&
        left.Connection with { Peers = null } == right.Connection with { Peers = null } &&
        (left.Connection.Peers ?? []).SequenceEqual(right.Connection.Peers ?? [], StringComparer.Ordinal);
    private void ReconcileLeases(RoomSnapshot room)
    {
        foreach (var pair in _leases.ToArray())
            if (!room.EffectiveRoutes.Any(r => r.Enabled && SameRoute(r, pair.Value.Route))) RemoveLease(pair.Key);
    }
    private void RemoveLease(string id)
    {
        if (_leases.Remove(id, out var lease)) lease.Dispose();
        if (_verifiedRouteId == id) _verifiedRouteId = null;
    }
    private void ClearLeases() { foreach (var lease in _leases.Values) lease.Dispose(); _leases.Clear(); _verifiedRouteId = null; }

    // Deliberately independent of _busy: twelve slow probes must never starve the 75-second room lease.
    private async void Heartbeat_Tick(object? sender, EventArgs e)
    {
        if (_closed) return;
        RefreshEasyTierDownload();
        if (_refreshing || _session?.Room is null) return;
        var now = DateTime.UtcNow;
        if (now < _nextHeartbeat && (_presenceDue is null || now < _presenceDue.Value)) return;
        var session = _session; var presence = _presence;
        _refreshing = true; _nextHeartbeat = now.AddSeconds(15); _presenceDue = null;
        try
        {
            await session.RefreshAsync(presence, _lifetime.Token);
            if (_closed || !ReferenceEquals(_session, session)) return;
            if (session.Room is null) { _operation?.Cancel(); ClearLeases(); }
            Render();
        }
        catch (Exception ex)
        {
            if (_closed || !ReferenceEquals(_session, session)) return;
            Status.Text = Error(ex);
            if (session.Room is null) { _operation?.Cancel(); ClearLeases(); }
            Render();
        }
        finally { _refreshing = false; }
    }
    private void CopyCode_Click(object sender, RoutedEventArgs e) { if (_initialized && !_closed) Copy(_session?.Room?.Code); }
    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    { if (_initialized && !_closed && GetLaunchTarget() is { } target) Copy(RoomConnection.FormatEndpoint(target.Host, target.Port)); }
    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Status.Text = "已复制。"; }
        catch (System.Runtime.InteropServices.ExternalException) { Status.Text = "剪贴板被占用，请重试。"; }
    }
    private void SelectFrpc_Click(object sender, RoutedEventArgs e) => SelectTool(FrpcPath, "frpc.exe");
    private void SelectEasyTier_Click(object sender, RoutedEventArgs e) => SelectTool(EasyTierPath, "easytier-core.exe");
    private void SelectTool(TextBox box, string executable)
    {
        if (!_initialized || _closed) return;
        var dialog = new OpenFileDialog { Filter = executable + "|" + executable + "|应用程序|*.exe", Title = "选择可信的 " + executable, CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { box.Text = dialog.FileName; SaveSettings(); }
    }
    private void DetectIpv6_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _closed) return;
        try
        {
            var address = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(n => n.Address.ToString()).FirstOrDefault(RoomConnection.IsGlobalIpv6);
            if (address is null) { Status.Text = "未检测到公网 IPv6，可选择其他线路；不会修改防火墙。"; return; }
            Ipv6Address.Text = address; Status.Text = "已填入本机公网 IPv6，请确认网卡与游戏端口入站规则。";
        }
        catch (Exception ex) { Status.Text = Error(ex); }
    }

    private async void ReadZeroTier_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var session = RequireRoom(true);
        var info = await _transports.GetZeroTierInfoAsync(NetworkId.Text.Trim(),
            new RoomTransportSettings(ZeroTierToken: ZeroTierToken.Password), ct);
        Guard(ct, session);
        var address = info?.Addresses.FirstOrDefault();
        if (address is null) { Status.Text = "当前网络尚无本机虚拟地址。请检查 ZeroTier 服务、加入状态及控制台授权；此操作不会加入或退出网络。"; return; }
        ZtAddress.Text = address;
        Status.Text = "已读取本机 ZeroTier 虚拟地址；仍需检测远端游戏握手。";
    });

    private static string SavedCustomUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsOfficialAddress(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return "";
        return uri.AbsoluteUri;
    }
    private static string SafePreference(string? value) => ContainsOfficialAddress(value) ? "" : value?.Trim() ?? "";

    private sealed record Preferences(bool UseCustom = false, string CustomUrl = "", string FrpcPath = "", string EasyTierPath = "", string NodeHost = "", string NodePort = "7000", string NodeCa = "");
    private void LoadSettings()
    {
        try
        {
            var data = File.Exists(SettingsPath) && new FileInfo(SettingsPath).Length < 16384
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath)) : null;
            // Old ServiceUrl preferences cannot silently opt a user into a custom coordinator.
            CustomUrl.Text = SavedCustomUrl(data?.CustomUrl);
            Coordinator.SelectedIndex = data?.UseCustom == true ? 1 : 0;
            FrpcPath.Text = SafePreference(data?.FrpcPath); EasyTierPath.Text = SafePreference(data?.EasyTierPath);
            NodeHost.Text = SafePreference(data?.NodeHost); NodePort.Text = data?.NodePort ?? "7000"; NodeCa.Text = SafePreference(data?.NodeCa);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }
    private void SaveSettings()
    {
        if (_closed) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var custom = SavedCustomUrl(CustomUrl.Text);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Preferences(!IsOfficial, custom, SafePreference(FrpcPath.Text), SafePreference(EasyTierPath.Text), SafePreference(NodeHost.Text), NodePort.Text, SafePreference(NodeCa.Text))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        if (_closed) return;
        SaveSettings(); _closed = true;
        _heartbeat.Stop(); _heartbeat.Tick -= Heartbeat_Tick; _lifetime.Cancel(); _operation?.Cancel();
        ClearLeases(); _transports.Dispose();
        var session = _session; _session = null;
        if (session is not null) _ = CloseSessionAsync(session);
    }
    private static async Task CloseSessionAsync(RoomSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await session.LeaveAsync(timeout.Token); }
        catch (Exception) { /* Best effort; no room identifiers, addresses or credentials are logged. */ }
        finally { session.Dispose(); }
    }
}
