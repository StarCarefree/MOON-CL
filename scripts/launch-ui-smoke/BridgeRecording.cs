using System.IO;
using PCL.Core.Minecraft.Launcher;

internal static partial class Program
{
    private static void CheckBridgeRecording(string file)
    {
        var packets = File.ReadAllLines(file);
        var sawEntrypoint = false; var sawCustom = false; var sawComplete = false;
        foreach (var packet in packets)
        {
            var state = new LaunchProgressState();
            Check(state.AcceptTelemetry(packet, "harness-token"), "C# rejected real MLS bridge output");
            if (state.Snapshot.Progress == .25) sawEntrypoint = true;
            if (state.Snapshot.Progress == .42) sawCustom = true;
            if (state.ModLoadingComplete)
            {
                Check(!state.IsTerminal, "Mod completion alone cannot complete the game window handoff");
                state.WindowAppeared();
                Check(state.IsTerminal && state.Snapshot.Phase == LaunchProgressPhase.Ready, "Ready/window rendezvous failed");
                sawComplete = true;
            }
        }
        Check(sawEntrypoint && sawCustom && sawComplete, "Missing real JAR producer -> Java relay -> C# receiver sequence");
    }
}
