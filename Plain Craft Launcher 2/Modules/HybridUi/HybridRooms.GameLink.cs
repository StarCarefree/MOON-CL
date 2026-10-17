using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.Multiplayer;

namespace PCL;

public partial class HybridRooms
{
    private sealed record GameLinkChoice(string Id, string Label)
    {
        public override string ToString() => Label; // The native ComboBox template renders SelectionBoxItem directly.
    }
    private string _gameLinkListKey = "";
    private bool _gameLinkSending;

    private void RefreshGameLink()
    {
        if (!_initialized || _closed) return;
        var sessions = GameLinkSession.Snapshot();
        var key = string.Join(";", sessions.Select(s => s.SessionId + ":" + s.ProcessId));
        if (_gameLinkListKey != key)
        {
            var selected = GameLinkClients.SelectedValue as string;
            _gameLinkListKey = key;
            GameLinkClients.ItemsSource = sessions.Select(s => new GameLinkChoice(s.SessionId, s.Instance + " · PID " + s.ProcessId)).ToArray();
            GameLinkClients.SelectedValue = selected;
            if (GameLinkClients.SelectedIndex < 0 && sessions.Length == 1) GameLinkClients.SelectedIndex = 0;
        }
        var current = sessions.FirstOrDefault(s => s.SessionId == GameLinkClients.SelectedValue as string);
        var available = !_busy && !_gameLinkSending && current is { State: "ready" or "connected" or "disconnected" or "error" };
        GameLinkConnect.IsEnabled = available;
        GameLinkRoom.IsEnabled = !_gameLinkSending && !_busy && GetLaunchTarget() is not null && (available || sessions.Length == 0);
        GameLinkDisconnect.IsEnabled = available;
        GameLinkPanel.Header = "游戏联动 · " + (current is null ? "选服 / 切服 / 实时状态" : StateText(current.State));
        GameLinkStatus.Text = current is null
            ? sessions.Length == 0 ? "尚无运行中的游戏；在我的游戏中选择版本后，可启动并进服。" : "请选择要控制的游戏实例。"
            : StateText(current.State) + (string.IsNullOrEmpty(current.Address) ? "" : " · " + current.Address) +
                (string.IsNullOrEmpty(current.Message) ? "" : " · " + current.Message);
    }

    private static string StateText(string state) => state switch
    {
        "waiting" => "正在加载游戏", "ready" => "游戏已就绪", "connecting" => "正在连接",
        "connected" => "已进入服务器", "disconnected" => "未连接服务器", "unsupported" => "当前客户端适配不可用",
        "error" => "操作失败", _ => state
    };

    private void GameLinkClient_Changed(object sender, SelectionChangedEventArgs e) => RefreshGameLink();
    private async void GameLinkConnect_Click(object sender, RoutedEventArgs e) => await SendGameLinkAsync(GameLinkAddress.Text.Trim());
    private async void GameLinkDisconnect_Click(object sender, RoutedEventArgs e) => await SendGameLinkAsync(null);
    private void GameLinkRoom_Click(object sender, RoutedEventArgs e) => PrimaryAction_Click(sender, e);

    private async Task SendGameLinkAsync(string? address)
    {
        if (_closed || !_initialized || _busy || _gameLinkSending) return;
        try
        {
            var id = GameLinkClients.SelectedValue as string ?? throw new InvalidOperationException("请选择运行中的游戏实例。");
            var current = GameLinkSession.Snapshot().FirstOrDefault(s => s.SessionId == id);
            if (address is not null && current?.State == "connected" && !RoomFlowPresentation.SameAddress(current.Address, address) && !ConfirmSwitch(current.Address, address)) return;
            _gameLinkSending = true; RefreshGameLink();
            await GameLinkSession.SendAsync(id, address);
            if (!_closed) Status.Text = "命令已发送，等待游戏确认；实际连接状态显示在游戏联动栏。";
        }
        catch (Exception ex) { if (!_closed) Status.Text = "游戏联动：" + ex.Message; }
        finally { _gameLinkSending = false; RefreshGameLink(); }
    }

    private void GameLinkLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || !_initialized) return;
        try
        {
            var address = GameLinkAddress.Text.Trim();
            LaunchGameLink(address);
        }
        catch (Exception ex) { Status.Text = "游戏联动：" + ex.Message; }
    }
    private void LaunchGameLink(string address)
    {
        GameLinkSession.ValidateAddress(address);
        if (_busy || _gameLinkSending) throw new InvalidOperationException("请先完成或取消当前操作。");
        var instance = _selectedInstance ?? ModInstanceList.McMcInstanceSelected
            ?? throw new InvalidOperationException("请先在我的游戏中选择一个版本。");
        if (RoomPresentation.IsLaunchingOrRunning(instance) || GameLinkSession.Snapshot().Any(s => s.Instance == instance.Name))
            throw new InvalidOperationException("所选游戏已经在启动或运行；请使用进入 / 切服，避免重复启动。");
        Status.Text = ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { instance = instance, ServerIp = address })
            ? "正在启动 " + instance.Name + "，游戏启动后自动进入 " + address + "。"
            : "未开始启动，请检查登录状态和游戏版本。";
    }
}
