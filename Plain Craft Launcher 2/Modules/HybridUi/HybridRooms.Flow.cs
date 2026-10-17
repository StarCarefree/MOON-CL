using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.Multiplayer;

namespace PCL;

public partial class HybridRooms
{
    private TaskCompletionSource<RoomPresentation.JoinChoice>? _choiceCompletion;
    private string? _manualRouteId;
    private RoomRouteProbeResult? _verifiedProbe;
    private string _flowStage = "", _flowMessage = "", _flowAddress = "", _flowSessionId = "";
    private string? _flowRequestId;
    private bool _flowFailed;
    private TimeSpan _flowWaitTimeout = TimeSpan.FromSeconds(120);
    private int? _hostLanPort;
    private DateTime _nextLanCheck;

    private RoomGameStatus? HostGameStatus => _session?.IsHost == true ? _presence.Game : _session?.Room?.Members.FirstOrDefault(m => m.IsHost)?.Game;
    private RoomFlowDecision FlowDecision => RoomFlowPresentation.Decide(_session?.IsHost == true, _session?.IsFresh == true,
        HostGameStatus?.State ?? "stopped", _hostLanPort is not null, _session?.Room?.EffectiveRoutes.Count(r => r.Enabled) ?? 0,
        !string.IsNullOrWhiteSpace(HostGameStatus?.Server));

    private void RefreshHostLan()
    {
        if (_session?.IsHost != true || DateTime.UtcNow < _nextLanCheck) return;
        _nextLanCheck = DateTime.UtcNow.AddSeconds(3);
        _hostLanPort = null;
        try
        {
            if (RoomPresentation.LatestRunningInstance() is { } instance)
                _hostLanPort = RoomSetupAutomation.FindMinecraftLanPort(Path.Combine(instance.PathIndie, "logs", "latest.log"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The manual route editor remains available. */ }
    }

    private void RenderFlow()
    {
        if (!_initialized || _closed) return;
        RefreshHostLan();
        var decision = FlowDecision;
        var game = HostGameStatus;
        GameSummary.Text = game is null ? "等待房主选择游戏" : RoomPresentation.GameText(game);
        GameSummary.ToolTip = GameSummary.Text;
        RoomHealth.Text = _session?.Room is null ? "尚未加入房间" : _session.IsFresh ? "房间在线 · 心跳独立保持" : "房间状态待确认 · 不代表游戏断线";
        HostPublishShortcut.Visibility = Show(_session?.IsHost == true);
        HostPublishShortcut.IsEnabled = !_busy;
        var route = _routes.FirstOrDefault(r => r.Route.Id == _verifiedRouteId);
        ConnectionSummary.Text = route is null ? (_session?.Room?.EffectiveRoutes.Length > 0 ? "线路尚未验证" : "尚无房间线路") : "已验证 · " + route.Title;
        ConnectionDetail.Text = route?.Result ?? (_session?.IsHost == true ? "发布线路后，队友通过游戏握手检测可达性。" : "点主按钮自动检测，或在管理线路中手动选择。 ");
        if (_manualRouteId is not null) ConnectionDetail.Text = "手动选路 · " + ConnectionDetail.Text;
        ConnectionDetail.ToolTip = ConnectionDetail.Text;
        var confirmed = RoomFlowPresentation.Confirmed(GameLinkSession.Snapshot().FirstOrDefault(s => s.SessionId == _flowSessionId), _flowSessionId, _flowAddress, _flowRequestId);
        var completed = !_busy && confirmed;
        var hasIssue = !_busy && _flowFailed;
        FlowStage.Text = _busy && _flowStage.Length > 0 ? _flowStage : completed ? "游戏已进服" : hasIssue ? "操作未完成" : decision.Stage;
        FlowHint.Text = _busy && _flowMessage.Length > 0 || hasIssue ? _flowMessage : completed ? "游戏已确认连接到 " + _flowAddress + "；房间与游戏状态分别保持。" : decision.Hint;
        FlowHint.ToolTip = FlowHint.Text;
        FlowStage.Foreground = new SolidColorBrush(completed ? Color.FromRgb(143, 196, 160) : hasIssue ? Color.FromRgb(235, 165, 128) : Color.FromRgb(217, 188, 117));
        PrimaryAction.Content = _busy ? "操作进行中…" : completed ? "重新选择 / 进入" : hasIssue && decision.Enabled ? "重试 · " + decision.Button : decision.Button;
        PrimaryAction.IsEnabled = !_busy && !_gameLinkSending && decision.Enabled;
    }

    private void FlowProgress(string stage, string message)
    {
        _flowStage = stage; _flowMessage = message; _flowFailed = false;
        Status.Text = message; RenderFlow();
    }
    private void FlowIssue(string message)
    {
        _flowSessionId = ""; _flowRequestId = null;
        _flowFailed = true; _flowMessage = message; Status.Text = message;
    }
    private void ResetFlow()
    {
        _choiceCompletion?.TrySetCanceled(); _choiceCompletion = null;
        _manualRouteId = null; _verifiedProbe = null; _flowStage = _flowMessage = _flowAddress = _flowSessionId = "";
        _flowRequestId = null; _flowFailed = false; _hostLanPort = null; _nextLanCheck = DateTime.MinValue;
    }

    private void OpenConnections_Click(object sender, RoutedEventArgs e) => OpenOverlay(ConnectionForm, "连接管理 · 线路与高级控制", RouteList);

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_initialized || _closed) return;
        var decision = FlowDecision;
        if (!decision.Enabled) return;
        _flowFailed = false;
        switch (decision.Action)
        {
            case RoomFlowAction.Manage: OpenConnections_Click(sender, e); break;
            case RoomFlowAction.Publish: OpenRouteEditor_Click(sender, e); break;
            case RoomFlowAction.StartHost: await RunAsync(StartHostAsync); break;
            case RoomFlowAction.Join: await RunAsync(JoinRoomGameAsync); break;
        }
    }

    private async Task StartHostAsync(CancellationToken ct)
    {
        var session = RequireRoom(true);
        FlowProgress("选择游戏", "选择要开放世界的本地客户端；不会连接任何服务器。");
        var sessions = GameLinkSession.Snapshot();
        var choices = RoomPresentation.AllInstances().Select(instance => new RoomPresentation.JoinChoice("install", instance.Name,
            (instance.Info?.VanillaName ?? "未知版本") + " · " + RoomPresentation.LoaderText(instance),
            !sessions.Any(s => s.Instance == instance.Name) && !RoomPresentation.IsLaunchingOrRunning(instance), "", instance)).ToArray();
        var picked = await ChooseGameAsync(choices, ct);
        Guard(ct, session);
        var instance = picked.Instance ?? throw new InvalidOperationException("请先在我的游戏中安装并选择客户端。");
        if (RoomPresentation.IsLaunchingOrRunning(instance) || GameLinkSession.Snapshot().Any(s => s.Instance == instance.Name))
            throw new InvalidOperationException("所选游戏已经在启动或运行，请进入游戏开放 LAN 后重新检测。");
        CloseOverlay();
        FlowProgress("启动中", "正在启动 " + instance.Name + "；进入世界后请在游戏内开放局域网。");
        if (!ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { instance = instance })) throw new InvalidOperationException("未开始启动，请检查登录状态与游戏版本。");
        // Host preparation ends at accepted launch; actual running/LAN state comes from process observation.
        _presence = _presence with { Game = new RoomGameStatus(RoomPresentation.Bounded(instance.Name), RoomPresentation.Bounded(instance.Info?.VanillaName), "launching", _presence.Game?.Pack) };
        _presenceDue = DateTime.UtcNow;
    }

    private async Task JoinRoomGameAsync(CancellationToken ct)
    {
        var session = RequireRoom();
        var previousLeases = _leases.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var previousRoute = _verifiedRouteId; var previousProbe = _verifiedProbe;
        var committed = false;
        try
        {
            string address, version;
            RoomRoute? route = null;
            var host = session.Room!.Members.FirstOrDefault(m => m.IsHost)?.Game;
            if (session.Room.EffectiveRoutes.Length > 0)
            {
                FlowProgress("检测线路", "正在建立传输并检测 Minecraft 握手；这一步不会启动游戏。");
                if (!await ConnectAsync(_manualRouteId, ct, retainPrevious: true)) throw new InvalidOperationException("未检测到可用线路；请在管理线路中查看原因。原可用连接保留。");
                Guard(ct, session);
                var target = GetLaunchTarget() ?? throw new InvalidOperationException("线路已变化，请重新检测。");
                route = session.Room!.EffectiveRoutes.First(r => r.Id == _verifiedRouteId);
                address = RoomConnection.FormatEndpoint(target.Host, target.Port);
                version = RoomFlowPresentation.MinecraftVersion(_verifiedProbe?.Version);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(host?.Server)) throw new InvalidOperationException("等待房主发布线路或分享服务器。");
                address = host.Server; version = host.Version;
            }
            await EnterGameAsync(session, address, version, route, null, ct);
            committed = true;
        }
        finally
        {
            if (ReferenceEquals(_session, session))
            {
                if (committed)
                {
                    if (!session.IsHost) foreach (var id in _leases.Keys.Where(id => id != _verifiedRouteId).ToArray()) RemoveLease(id);
                }
                else
                {
                    foreach (var pair in _leases.ToArray())
                        if (!previousLeases.TryGetValue(pair.Key, out var previous) || !ReferenceEquals(pair.Value, previous)) RemoveLease(pair.Key);
                    _verifiedRouteId = previousRoute is not null && _leases.TryGetValue(previousRoute, out var old) && old.IsAlive &&
                        previousLeases.TryGetValue(previousRoute, out var previousLease) && ReferenceEquals(old, previousLease) ? previousRoute : null;
                    _verifiedProbe = _verifiedRouteId is null ? null : previousProbe;
                }
            }
        }
    }

    private async Task EnterGameAsync(RoomSession session, string address, string version, RoomRoute? route, string? memberId, CancellationToken ct)
    {
        GameLinkSession.ValidateAddress(address);
        void CheckTarget()
        {
            Guard(ct, session);
            RequireRoom();
            if (route is not null)
            {
                if (!session.Room!.EffectiveRoutes.Any(r => r.Enabled && SameRoute(r, route)) || GetLaunchTarget() is not { } target ||
                    !RoomFlowPresentation.SameAddress(RoomConnection.FormatEndpoint(target.Host, target.Port), address))
                    throw new InvalidOperationException("线路已变化，请重新连接；不会继续启动旧目标。");
            }
            else
            {
                var game = session.Room!.Members.FirstOrDefault(m => memberId is null ? m.IsHost : m.Id == memberId)?.Game;
                if (game?.Version != version || !RoomFlowPresentation.SameAddress(game.Server, address) ||
                    memberId is null && session.Room.EffectiveRoutes.Length > 0)
                    throw new InvalidOperationException("分享的服务器已变化，请重新选择。");
            }
        }
        CheckTarget();
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("服务器未提供明确版本；请在连接管理中复制地址并手动加入。");
        FlowProgress("选择客户端", "目标 " + address + " · Minecraft " + version + "。相同版本不保证模组兼容。");
        var picked = await ChooseGameAsync(RoomPresentation.JoinChoices(version).ToArray(), ct);
        CheckTarget();
        CloseOverlay();
        var before = GameLinkSession.Snapshot();
        string? request = null;
        string sessionId;
        if (picked.Kind == "running")
        {
            var current = before.FirstOrDefault(s => s.SessionId == picked.SessionId) ?? throw new InvalidOperationException("所选游戏已退出，请重新选择。");
            if (current.State is "waiting" or "unsupported" or "connecting") throw new InvalidOperationException("游戏尚未就绪或不可控；请等待或手动加入。");
            if (current.State == "connected")
            {
                if (RoomFlowPresentation.SameAddress(current.Address, address))
                { _flowAddress = address; _flowSessionId = current.SessionId; _flowRequestId = null; _flowFailed = false; Status.Text = "所选游戏已经在目标服务器。"; return; }
                if (!ConfirmSwitch(current.Address, address)) throw new OperationCanceledException(ct);
                CheckTarget();
            }
            FlowProgress("连接中", "正在向所选游戏发送进服指令，等待游戏实际确认。");
            sessionId = current.SessionId;
            request = await GameLinkSession.SendAsync(sessionId, address);
        }
        else
        {
            var instance = picked.Instance ?? throw new InvalidOperationException("所选客户端不在本机。");
            if (!string.Equals(instance.Info?.VanillaName, version, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("客户端版本已变化，请重新选择。");
            if (RoomPresentation.IsLaunchingOrRunning(instance) || before.Any(s => s.Instance == instance.Name)) throw new InvalidOperationException("所选游戏已在启动或运行，请重新选择，避免重复启动。");
            FlowProgress("启动中", "正在启动 " + instance.Name + " 并进入 " + address + "；尚未确认进服。");
            CheckTarget();
            if (!ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { instance = instance, ServerIp = address })) throw new InvalidOperationException("未开始启动，请检查登录状态和游戏版本。");
            var until = DateTime.UtcNow + _flowWaitTimeout;
            GameLinkState? started = null;
            while (DateTime.UtcNow < until)
            {
                CheckTarget();
                started = GameLinkSession.Snapshot().FirstOrDefault(s => s.Instance == instance.Name && !before.Any(old => old.SessionId == s.SessionId));
                if (started is not null) break;
                await Task.Delay(250, ct);
            }
            if (started is null) throw new TimeoutException("启动已请求，但未观察到可控游戏会话；请检查游戏窗口或复制地址手动加入。不会重复启动。");
            sessionId = started.SessionId;
        }
        _flowAddress = address; _flowSessionId = sessionId; _flowRequestId = request;
        await WaitForGameAsync(sessionId, address, request, CheckTarget, ct);
        CheckTarget();
        _flowFailed = false; Status.Text = "游戏已确认进入 " + address + "。";
    }

    private async Task WaitForGameAsync(string sessionId, string address, string? requestId, Action check, CancellationToken ct)
    {
        var until = DateTime.UtcNow + _flowWaitTimeout;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested(); check();
            var state = GameLinkSession.Snapshot().FirstOrDefault(s => s.SessionId == sessionId);
            if (RoomFlowPresentation.Confirmed(state, sessionId, address, requestId)) return;
            if (state is null) throw new InvalidOperationException("游戏会话已结束；未确认进服。");
            if (state.State == "unsupported") throw new InvalidOperationException("当前客户端不支持运行时控制，请复制地址手动加入。");
            if (state.State is "error" or "disconnected" && (requestId is null || state.RequestId == requestId))
                throw new InvalidOperationException("游戏未连接：" + RoomPresentation.Bounded(state.Message) + "。可重试或复制地址手动加入。");
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("未在等待时间内确认进服；游戏不会被关闭。请检查游戏窗口、重试或复制地址手动加入。");
    }

    private async Task<RoomPresentation.JoinChoice> ChooseGameAsync(RoomPresentation.JoinChoice[] choices, CancellationToken ct)
    {
        if (choices.Length == 0) throw new InvalidOperationException("本机没有对应版本的客户端。请先在我的游戏中安装，或下载房主分享的整合包；也可复制地址手动加入。");
        _choiceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FlowInstanceChoices.ItemsSource = choices;
        FlowInstanceChoices.SelectedItem = choices.FirstOrDefault(c => c.Enabled && c.Kind == "running");
        if (FlowInstanceChoices.SelectedIndex < 0) FlowInstanceChoices.SelectedItem = choices.FirstOrDefault(c => c.Enabled);
        ChoiceTarget.Text = _flowMessage + (choices.Any(c => c.Enabled) ? "" : "\n没有可用客户端：请等待游戏就绪、安装同版本游戏或复制地址手动加入。");
        OpenOverlay(GameChoiceForm, "选择游戏客户端", FlowInstanceChoices);
        using var registration = ct.Register(() => _choiceCompletion?.TrySetCanceled(ct));
        try { return await _choiceCompletion.Task; }
        finally { _choiceCompletion = null; if (!_closed && GameChoiceForm.IsVisible) CloseOverlay(); }
    }
    private void FlowChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FlowConfirm is not null) FlowConfirm.IsEnabled = FlowInstanceChoices.SelectedItem is RoomPresentation.JoinChoice { Enabled: true };
    }
    private void FlowConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (FlowInstanceChoices.SelectedItem is RoomPresentation.JoinChoice { Enabled: true } picked) _choiceCompletion?.TrySetResult(picked);
    }
    private bool ConfirmSwitch(string from, string to) => ModMain.MyMsgBox("当前游戏正在 " + from + "。是否离开并进入 " + to + "？未保存的进度请先在游戏内处理。",
        "确认切换服务器", "切换服务器", "取消", isWarn: true) == 1;
}
