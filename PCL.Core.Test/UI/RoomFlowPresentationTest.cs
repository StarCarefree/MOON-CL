using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public class RoomFlowPresentationTest
{
    [TestMethod]
    public void HostPreparationTracksRealGameAndLan()
    {
        Assert.AreEqual(RoomFlowAction.StartHost, RoomFlowPresentation.Decide(true, true, "stopped", false, 0, false).Action);
        Assert.IsFalse(RoomFlowPresentation.Decide(true, true, "launching", false, 0, false).Enabled);
        var waiting = RoomFlowPresentation.Decide(true, true, "running", false, 0, false);
        Assert.AreEqual(RoomFlowAction.Publish, waiting.Action);
        StringAssert.Contains(waiting.Hint, "对局域网开放");
        StringAssert.Contains(RoomFlowPresentation.Decide(true, true, "running", true, 0, false).Stage, "3 / 3");
        Assert.AreEqual(RoomFlowAction.Manage, RoomFlowPresentation.Decide(true, true, "running", false, 1, false).Action);
        Assert.AreEqual(RoomFlowAction.Manage, RoomFlowPresentation.Decide(true, true, "running", false, 0, true).Action);
    }
    [TestMethod]
    public void GuestWaitsWithoutTargetAndPrefersRoutes()
    {
        Assert.IsFalse(RoomFlowPresentation.Decide(false, true, "running", false, 0, false).Enabled);
        Assert.AreEqual("连接并进入游戏", RoomFlowPresentation.Decide(false, true, "running", false, 1, true).Button);
        Assert.AreEqual("进入房主的服务器", RoomFlowPresentation.Decide(false, true, "running", false, 0, true).Button);
        Assert.IsFalse(RoomFlowPresentation.Decide(false, false, "running", false, 1, true).Enabled);
    }
    [TestMethod]
    public void DeliveryAndStaleCallbacksNeverCountAsConnected()
    {
        var state = new GameLinkState("game", "client", 1, "connecting", "example.com:25565", "", "new");
        Assert.IsFalse(RoomFlowPresentation.Confirmed(state, "game", "example.com", "new"));
        state = state with { State = "connected" };
        Assert.IsTrue(RoomFlowPresentation.Confirmed(state, "game", "EXAMPLE.com", "new"));
        Assert.IsFalse(RoomFlowPresentation.Confirmed(state, "other-game", "example.com", "new"));
        Assert.IsFalse(RoomFlowPresentation.Confirmed(state, "game", "example.com:25566", "new"));
        Assert.IsFalse(RoomFlowPresentation.Confirmed(state, "game", "example.com", "old"));
        Assert.IsFalse(RoomFlowPresentation.Confirmed(null, "game", "example.com", "new"));
        Assert.IsFalse(RoomFlowPresentation.Confirmed(state with { State = "unsupported" }, "game", "example.com", "new"));
        Assert.IsFalse(RoomFlowPresentation.Confirmed(state with { State = "disconnected" }, "game", "example.com", "new"));
    }
    [TestMethod]
    public void VersionDiscoveryRejectsAmbiguousRanges()
    {
        Assert.AreEqual("1.21.1", RoomFlowPresentation.MinecraftVersion("NeoForge 1.21.1"));
        Assert.AreEqual("1.21.1-pre1", RoomFlowPresentation.MinecraftVersion("1.21.1-pre1"));
        Assert.AreEqual("26.1", RoomFlowPresentation.MinecraftVersion("26.1"));
        Assert.AreEqual("24w14a", RoomFlowPresentation.MinecraftVersion("24w14a"));
        Assert.AreEqual("", RoomFlowPresentation.MinecraftVersion("1.20.1 - 1.21.1"));
        Assert.AreEqual("", RoomFlowPresentation.MinecraftVersion("Minecraft"));
        Assert.IsTrue(RoomFlowPresentation.SameAddress("[2001:db8::1]:25565", "[2001:db8::1]"));
        Assert.IsFalse(RoomFlowPresentation.SameAddress("", ""));
    }

    [TestMethod]
    public void RoomInviteCodeReadsOnlyTheSharedInvitePhrase()
    {
        Assert.AreEqual("AB12CD34", RoomFlowPresentation.RoomInviteCode("邀请你加入联机房间「晚间」，邀请码 AB12cd34。"));
        Assert.IsNull(RoomFlowPresentation.RoomInviteCode("随便聊聊 AB12CD34"));
        Assert.IsNull(RoomFlowPresentation.RoomInviteCode("邀请码 只有七位"));
        Assert.IsNull(RoomFlowPresentation.RoomInviteCode(null));
        Assert.AreEqual("邀请你加入联机房间「晚间」，邀请码 AB12CD34。", RoomFlowPresentation.ShareText("晚间", "ab12cd34"));
        Assert.IsTrue(RoomFlowPresentation.IsOwnShare("邀请码 AB12CD34", "ab12cd34"));
        Assert.IsFalse(RoomFlowPresentation.IsOwnShare("邀请码 AB12CD34", "ZZZZZZZZ"));
    }
}


