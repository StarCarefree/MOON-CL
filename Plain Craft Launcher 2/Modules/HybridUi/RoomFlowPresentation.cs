using System;
using System.Linq;
using System.Text.RegularExpressions;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal enum RoomFlowAction { Wait, StartHost, Publish, Manage, Join }
internal sealed record RoomFlowDecision(RoomFlowAction Action, string Button, string Stage, string Hint, bool Enabled = true);

/// <summary>Pure UI policy. Room membership, transport reachability and game confirmation are independent.</summary>
internal static class RoomFlowPresentation
{
    internal static RoomFlowDecision Decide(bool host, bool fresh, string gameState, bool lan, int routes, bool server)
    {
        if (!fresh) return new(RoomFlowAction.Wait, "等待房间确认", "房间状态待确认", "心跳正在重新确认身份；这不代表游戏已经断线。", false);
        if (host)
        {
            if (routes > 0 || server) return new(RoomFlowAction.Manage, "管理连接", "已准备邀请", "线路已发布或服务器已分享；可邀请队友，并在连接面板检查可达性。");
            if (gameState == "launching") return new(RoomFlowAction.Wait, "游戏启动中", "准备世界 · 1 / 3", "等待游戏加载；进入世界后，请在游戏内对局域网开放。", false);
            if (gameState != "running") return new(RoomFlowAction.StartHost, "选择游戏并启动", "准备世界 · 1 / 3", "先启动一个本地客户端，再进入世界并对局域网开放。");
            return new(RoomFlowAction.Publish, lan ? "发布游戏线路" : "检测 LAN / 发布线路", lan ? "准备世界 · 3 / 3" : "准备世界 · 2 / 3",
                lan ? "已读取局域网端口；选择连接方式并发布，队友才能检测线路。" : "在游戏中按 Esc → 对局域网开放，再检测端口；也可手动填写或发布已有服务器。");
        }
        if (routes > 0) return new(RoomFlowAction.Join, "连接并进入游戏", "加入房主的世界", "检测线路 → 选择本地客户端 → 进入游戏。不会静默切换正在游玩的服务器。");
        if (server) return new(RoomFlowAction.Join, "进入房主的服务器", "房主已分享服务器", "目标是房主当前所在服务器，不是 LAN 世界；进入前可确认地址和客户端。");
        return new(RoomFlowAction.Wait, "等待房主准备", "房间已加入 · 游戏尚未连接", "房主还没有发布启用线路或分享服务器；可先邀请其他队友。", false);
    }

    // A status display may contain loader names; ambiguous ranges must never become an invented version.
    internal static string MinecraftVersion(string? display)
    {
        var matches = Regex.Matches(display ?? "", @"(?<![\w.])(?:1\.\d+(?:\.\d+)?|[2-9]\d\.\d+(?:\.\d+)?)(?:-pre\d+|-rc\d+)?(?![\w.])|(?<![\w.])\d{2}w\d{2}[a-z](?![\w.])", RegexOptions.IgnoreCase)
            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return matches.Length == 1 ? matches[0] : "";
    }

    internal static bool SameAddress(string? left, string? right)
    {
        static Uri? Parse(string? address) => Uri.TryCreate("tcp://" + address, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(address) ? uri : null;
        var a = Parse(left); var b = Parse(right);
        return a is not null && b is not null && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
            (a.Port == -1 ? 25565 : a.Port) == (b.Port == -1 ? 25565 : b.Port);
    }

    internal static bool Confirmed(GameLinkState? state, string sessionId, string address, string? requestId) =>
        state is { State: "connected" } && state.SessionId == sessionId && SameAddress(state.Address, address) &&
        (requestId is null || state.RequestId == requestId);

    private static readonly Regex RoomInvite = new(@"邀请码\s*([A-Za-z0-9]{8})\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>邀请码只认好友房间邀请文案，避免普通聊天里的 8 位词被当成房间。</summary>
    internal static string? RoomInviteCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = RoomInvite.Match(text);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    internal static string ShareText(string? name, string code) =>
        "邀请你加入联机房间「" + (string.IsNullOrWhiteSpace(name) ? "房间" : name.Trim()) + "」，邀请码 " + code.Trim().ToUpperInvariant() + "。";

    internal static bool IsOwnShare(string? text, string? ownCode) =>
        RoomInviteCode(text) is { } code && string.Equals(code, ownCode?.Trim(), StringComparison.OrdinalIgnoreCase);
}


