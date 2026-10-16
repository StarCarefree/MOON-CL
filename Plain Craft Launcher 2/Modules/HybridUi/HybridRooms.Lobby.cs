using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.Multiplayer;

namespace PCL;

public partial class HybridRooms
{
    private readonly ObservableCollection<RoomMemberPresentation> _memberPage = [];
    private readonly ObservableCollection<RoomMemberPresentation> _hallPage = [];
    private readonly ObservableCollection<RoomRoutePresentation> _routePage = [];
    private int _memberPageIndex, _routePageIndex;
    private bool _paging;
    private string? _selectedRouteId, _suggestedRouteName;
    private IInputElement? _overlayFocus;
    private McInstance? _selectedInstance;
    private bool _manualLocalPort, _settingSuggestedPort;
    private void LocalPort_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialized && !_settingSuggestedPort) _manualLocalPort = !string.IsNullOrWhiteSpace(LocalPort.Text);
    }
    private RoomRoutePresentation? SelectedRoute => _routes.FirstOrDefault(r => r.Route.Id == _selectedRouteId);

    // Keep the authoritative collections intact. Heartbeats update existing view models and only
    // change page contents when membership changes, never navigation or the verified winner.
    private static void SyncPage<T>(ObservableCollection<T> page, T[] values)
    {
        if (page.SequenceEqual(values)) return;
        page.Clear();
        foreach (var value in values) page.Add(value);
    }
    private void RefreshPages()
    {
        _memberPageIndex = Math.Clamp(_memberPageIndex, 0, Math.Max(0, (_members.Count - 1) / 4));
        _routePageIndex = Math.Clamp(_routePageIndex, 0, Math.Max(0, (_routes.Count - 1) / 3));
        SyncPage(_memberPage, _members.Skip(_memberPageIndex * 4).Take(4).ToArray());
        SyncPage(_hallPage, _members.Take(4).ToArray());
        if (!_routes.Any(r => r.Route.Id == _selectedRouteId)) _selectedRouteId = _routes.FirstOrDefault()?.Route.Id;
        _paging = true;
        try
        {
            SyncPage(_routePage, _routes.Skip(_routePageIndex * 3).Take(3).ToArray());
            RouteList.SelectedItem = _routePage.FirstOrDefault(r => r.Route.Id == _selectedRouteId);
        }
        finally { _paging = false; }
        MemberCount.Text = $"队伍 / {_members.Count} 人 · 最多 16 人";
        MemberPageLabel.Text = $"{_memberPageIndex + 1} / {Math.Max(1, (_members.Count + 3) / 4)}";
        RoutePageLabel.Text = $"{_routePageIndex + 1} / {Math.Max(1, (_routes.Count + 2) / 3)}";
        MemberPrev.IsEnabled = _memberPageIndex > 0;
        MemberNext.IsEnabled = (_memberPageIndex + 1) * 4 < _members.Count;
        RoutePrev.IsEnabled = _routePageIndex > 0;
        RouteNext.IsEnabled = (_routePageIndex + 1) * 3 < _routes.Count;
        RemoveRouteButton.IsEnabled = !_busy && SelectedRoute is not null;
    }
    private void MemberPrev_Click(object sender, RoutedEventArgs e) { if (_initialized && !_closed) { _memberPageIndex--; RefreshPages(); } }
    private void MemberNext_Click(object sender, RoutedEventArgs e) { if (_initialized && !_closed) { _memberPageIndex++; RefreshPages(); } }
    private void RoutePrev_Click(object sender, RoutedEventArgs e) { if (_initialized && !_closed) { _routePageIndex--; RefreshPages(); } }
    private void RouteNext_Click(object sender, RoutedEventArgs e) { if (_initialized && !_closed) { _routePageIndex++; RefreshPages(); } }

    private void OpenOverlay(FrameworkElement form, string title, Control? focus = null)
    {
        if (!_initialized || _closed) return;
        if (Overlay.Visibility != Visibility.Visible) _overlayFocus = Keyboard.FocusedElement;
        foreach (var panel in new FrameworkElement[] { CreateForm, JoinForm, SettingsForm, RouteEditor, ToolsForm })
            panel.Visibility = Show(ReferenceEquals(panel, form));
        OverlayTitle.Text = title;
        Overlay.Visibility = Visibility.Visible;
        LobbyContent.IsEnabled = false;
        if (focus is null || !focus.Focus()) form.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void CloseOverlay()
    {
        if (!_initialized || _closed) return;
        Overlay.Visibility = Visibility.Collapsed;
        LobbyContent.IsEnabled = true;
        SaveSettings();
        if (_overlayFocus is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        _overlayFocus = null;
    }
    private void CloseOverlay_Click(object sender, RoutedEventArgs e) => CloseOverlay();
    private void Lobby_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Overlay.Visibility != Visibility.Visible) return;
        CloseOverlay(); e.Handled = true;
    }
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenOverlay(SettingsForm, "玩家与房间服务", PlayerName);
    private void OpenCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.Room is not null || _busy) return;
        SuggestRoomName();
        OpenOverlay(CreateForm, "创建房间", RoomName);
    }
    private void OpenJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.Room is not null || _busy) return;
        OpenOverlay(JoinForm, "加入队伍", RoomCode);
    }
    private void OpenRouteEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.IsHost != true || _busy || _closed) return;
        PrepareRouteDefaults();
        RouteBasic.Visibility = Visibility.Visible; RouteAdvanced.Visibility = Visibility.Collapsed;
        OpenOverlay(RouteEditor, "发布游戏线路", RouteName);
    }
    private void RouteBasic_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _closed) return;
        RouteBasic.Visibility = Visibility.Visible; RouteAdvanced.Visibility = Visibility.Collapsed; RouteName.Focus();
    }
    private void RouteAdvanced_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _closed) return;
        FillModeDefaults();
        RouteBasic.Visibility = Visibility.Collapsed; RouteAdvanced.Visibility = Visibility.Visible;
        RouteAdvanced.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _closed) return;
        DiscoverTools();
        OpenOverlay(ToolsForm, "本机工具 · 凭据仅本次会话", ToolsPanel);
    }
    private void ReturnToRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.IsHost == true) OpenOverlay(RouteEditor, "发布游戏线路", RouteName);
        else OpenOverlay(SettingsForm, "玩家与房间服务", PlayerName);
    }
    private void Lobby_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LobbyContent is null || OverlayFrame is null) return;
        var compact = ActualWidth < 850 || ActualHeight < 640;
        LobbyContent.Margin = new Thickness(compact ? 12 : 22);
        OverlayFrame.Margin = new Thickness(compact ? 10 : 22);
        OverlayFrame.Padding = new Thickness(compact ? 12 : 20);
        IdentitySummary.Visibility = Show(ActualWidth >= 720);
    }

    private void SuggestRoomName()
    {
        if (!string.IsNullOrWhiteSpace(RoomName.Text) && RoomName.Text != "一起玩 Minecraft") return;
        var player = string.IsNullOrWhiteSpace(PlayerName.Text) ? "Player" : PlayerName.Text.Trim();
        RoomName.Text = RoomPresentation.Bounded(player + " 的 " + (string.IsNullOrWhiteSpace(_selectedInstance?.Name) ? "Minecraft" : _selectedInstance.Name), 48);
    }
    private void SuggestRouteName()
    {
        if (!string.IsNullOrWhiteSpace(RouteName.Text) && RouteName.Text != "游戏线路" && RouteName.Text != _suggestedRouteName) return;
        _suggestedRouteName = RoomSetupAutomation.SuggestRouteName(Mode, _routes.Count + 1);
        RouteName.Text = _suggestedRouteName;
    }
    private void PrepareRouteDefaults()
    {
        SuggestRouteName(); DiscoverTools(); FillModeDefaults();
        if (_manualLocalPort) return;
        try
        {
            var instance = RoomPresentation.LatestRunningInstance();
            if (instance is null) { PortHint.Text = "尚无运行中的游戏；请开放局域网后重新打开向导，或手动填写。"; return; }
            var port = RoomSetupAutomation.FindMinecraftLanPort(Path.Combine(instance.PathIndie, "logs", "latest.log"));
            if (port is null) { PortHint.Text = "未发现开放到局域网的端口；请在游戏内开放 LAN，或手动填写。"; return; }
            _settingSuggestedPort = true;
            try { LocalPort.Text = port.Value.ToString(); }
            finally { _settingSuggestedPort = false; }
            PortHint.Text = "已从当前游戏日志读取 LAN 端口；可手动覆盖。";
        }
        catch (Exception) { PortHint.Text = "无法读取当前游戏日志，请手动填写 LAN 端口。"; }
    }
    private void FillModeDefaults()
    {
        SuggestRouteName();
        if (Mode != "ipv6" || !string.IsNullOrWhiteSpace(Ipv6Address.Text)) return;
        // No status messages on automatic discovery, and no network requests or OS changes.
        try
        {
            var address = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(n => n.Address.ToString()).FirstOrDefault(RoomConnection.IsGlobalIpv6);
            if (address is not null) Ipv6Address.Text = address;
        }
        catch (Exception) { }
    }
    private void PreparePublication()
    {
        SuggestRouteName();
        if (Mode != "easytier") return;
        if (string.IsNullOrWhiteSpace(NetworkName.Text)) NetworkName.Text = RoomSetupAutomation.GenerateNetworkName(_session?.Room?.Code ?? "");
        if (string.IsNullOrWhiteSpace(NetworkSecret.Password)) NetworkSecret.Password = RoomSetupAutomation.GenerateNetworkSecret();
    }
    private void DiscoverTools()
    {
        var root = AppContext.BaseDirectory;
        var directories = new System.Collections.Generic.List<string> { root, Path.Combine(root, "Binary"), Path.Combine(root, "tools"), Path.Combine(root, "tools", "frp"), Path.Combine(root, "tools", "easytier") };
        try { directories.Add(PCL.Core.Link.Scaffolding.EasyTier.EasyTierMetadata.EasyTierFilePath); }
        catch (Exception ex) when (ex is TypeInitializationException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { /* Discovery is optional; an unavailable launcher-global path must not prevent manual setup. */ }
        if (string.IsNullOrWhiteSpace(FrpcPath.Text)) FrpcPath.Text = RoomSetupAutomation.FindExecutable("frpc.exe", directories) ?? "";
        if (string.IsNullOrWhiteSpace(EasyTierPath.Text)) EasyTierPath.Text = RoomSetupAutomation.FindExecutable("easytier-core.exe", directories) ?? "";
    }
}
