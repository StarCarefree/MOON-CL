using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class LaunchProgressStateTest
{
    private const string Token = "test-session";
    private static string Packet(string status = "loading", int current = 3, int total = 10, string token = Token)
        => $$"""{"protocol":1,"token":"{{token}}","status":"{{status}}","stage":"初始化模组","detail":"Example Mod","current":{{current}},"total":{{total}}} """;

    [TestMethod]
    public void LauncherAndGameProgressAreSeparate()
    {
        var state = new LaunchProgressState();
        state.Launcher("补全资源", .8, "12 MiB/s");
        Assert.AreEqual(.8, state.Snapshot.Progress);
        state.GameStarted();
        Assert.IsNull(state.Snapshot.Progress);
        Assert.AreEqual(LaunchProgressPhase.Game, state.Snapshot.Phase);
        Assert.IsTrue(state.AcceptTelemetry(Packet(), Token));
        Assert.AreEqual(.3, state.Snapshot.Progress);
        state.Launcher("late", 1, "late");
        Assert.AreEqual("初始化模组", state.Snapshot.Stage);
    }

    [TestMethod]
    public void AppearedWindowDoesNotCompleteConnectedGame()
    {
        var state = new LaunchProgressState();
        state.AcceptTelemetry(Packet(), Token);
        state.WindowAppeared();
        Assert.IsFalse(state.IsTerminal);
        Assert.AreEqual(LaunchProgressPhase.Game, state.Snapshot.Phase);
        state.AcceptTelemetry(Packet("ready"), Token);
        Assert.IsTrue(state.IsTerminal);
        Assert.AreEqual(LaunchProgressPhase.Ready, state.Snapshot.Phase);
        Assert.IsFalse(state.AcceptTelemetry(Packet(), Token));
    }

    [TestMethod]
    public void UnsupportedInstanceFallsBackWithoutInventingPercentage()
    {
        var state = new LaunchProgressState();
        state.GameStarted(); state.WindowAppeared();
        Assert.AreEqual(LaunchProgressPhase.Ready, state.Snapshot.Phase);
        Assert.IsNull(state.Snapshot.Progress);
        Assert.IsFalse(state.Snapshot.TelemetryConnected);
        Assert.IsNotNull(state.Snapshot.Warning);
    }

    [TestMethod]
    public void TelemetryIsBoundToItsOwnSession()
    {
        var first = new LaunchProgressState(); var second = new LaunchProgressState();
        Assert.IsFalse(first.AcceptTelemetry(Packet(token: "other"), Token));
        first.AcceptTelemetry(Packet(), Token);
        Assert.AreEqual(LaunchProgressPhase.Launcher, second.Snapshot.Phase);
        Assert.IsFalse(first.AcceptTelemetry("{}", Token));
        Assert.IsFalse(first.AcceptTelemetry("[]", Token));
        Assert.IsFalse(first.AcceptTelemetry("{", Token));
        Assert.IsFalse(first.AcceptTelemetry(new string('x', 17000), Token));
        Assert.IsFalse(first.AcceptTelemetry("{\"token\":[],\"protocol\":1}", Token));
    }

    [TestMethod]
    public void UnknownTotalsAreIndeterminateAndProgressIsBounded()
    {
        var state = new LaunchProgressState();
        state.AcceptTelemetry(Packet(total: 0), Token); Assert.IsNull(state.Snapshot.Progress);
        state.AcceptTelemetry(Packet(current: 50), Token); Assert.AreEqual(1d, state.Snapshot.Progress);
        state.AcceptTelemetry(Packet(current: -1), Token); Assert.IsNull(state.Snapshot.Progress);
    }

    [TestMethod]
    public void LostSignalDoesNotClaimSuccessAndCannotBeRevived()
    {
        var state = new LaunchProgressState();
        state.AcceptTelemetry(Packet(), Token); state.SignalLost();
        Assert.IsTrue(state.IsTerminal);
        Assert.AreEqual(LaunchProgressPhase.Failed, state.Snapshot.Phase);
        Assert.IsNull(state.Snapshot.Progress);
        Assert.IsFalse(state.AcceptTelemetry(Packet("ready"), Token));
    }

    [TestMethod]
    public void ModCompletionBeforeWindowStillWaitsForWindow()
    {
        var state = new LaunchProgressState();
        state.AcceptTelemetry(Packet("ready"), Token);
        Assert.IsTrue(state.ModLoadingComplete);
        Assert.IsFalse(state.IsTerminal);
        Assert.AreEqual(LaunchProgressPhase.Game, state.Snapshot.Phase);
        state.WindowAppeared();
        Assert.IsTrue(state.IsTerminal);
        Assert.AreEqual(LaunchProgressPhase.Ready, state.Snapshot.Phase);
    }
}
