using System.Diagnostics;
using System.IO;
using System.Reflection;
using PCL;
using PCL.Core.Minecraft.Launcher;

internal static partial class Program
{
    private static async Task CheckPresentations()
    {
        var type = typeof(HybridLaunchWindow).Assembly.GetType("PCL.LaunchProgressPresentation")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var cancelled = new int[2];
        object New(int index) => Activator.CreateInstance(type, flags, null,
            new object[] { "隔离启动 " + index, (System.Action)(() => cancelled[index]++) }, null)!;
        object Field(object instance, string name) => type.GetField(name, flags)!.GetValue(instance)!;
        void Call(object instance, string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(instance, args);
        var first = New(0); var second = New(1);
        try
        {
            var one = (HybridLaunchWindow)Field(first, "_window"); var two = (HybridLaunchWindow)Field(second, "_window");
            Check(one.IsVisible && two.IsVisible && one.Owner is null && two.Owner is null, "Each launch needs an independent window");
            var channel = (MlsProgressChannel)Field(second, "_channel");
            var info = new ProcessStartInfo("java") { Arguments = "-Xmx1G Main", UseShellExecute = false };
            await Task.Run(() => Call(second, "PrepareProcess", info, true));
            Check(info.Arguments.StartsWith("-javaagent:\""), "Real embedded bridge resource was not injected from the loader worker");
            Check(File.Exists(info.Arguments.Split('"')[1]), "Extracted bridge missing");
            Check(info.Environment["PCL_MOON_MLS_PROGRESS_FILE"] == channel.SnapshotPath, "Wrong session progress path");
            var unsupported = new ProcessStartInfo("java") { Arguments = "Main", UseShellExecute = false };
            Call(first, "PrepareProcess", unsupported, false);
            Check(unsupported.Arguments == "Main" && !unsupported.Environment.ContainsKey("PCL_MOON_MLS_PROGRESS_FILE"), "Unsupported loaders must not be instrumented");
            Call(second, "GameStarted");
            File.WriteAllText(channel.SnapshotPath, $$"""{"protocol":1,"token":"{{channel.Token}}","status":"loading","stage":"测试阶段","current":3,"total":5}""");
            await Task.Run(() => Call(second, "Poll")); await Task.Delay(200);
            var view = (HybridLaunchScreen)two.FindName("Screen");
            Check(Control<System.Windows.Controls.TextBlock>(view, "Percentage").Text == "60%", "Per-session file did not reach actual independent window");
            var state = (LaunchProgressState)type.GetProperty("State", flags)!.GetValue(second)!;
            Action((HybridLaunchScreen)one.FindName("Screen"), "LaunchScreen.Cancel").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(cancelled[0] == 1 && cancelled[1] == 0, "Cancel crossed launch sessions");
            two.Close();
            Check(cancelled[1] == 0 && !state.IsTerminal, "Closing display must not cancel or complete a game");
            File.WriteAllText(channel.SnapshotPath, $$"""{"protocol":1,"token":"{{channel.Token}}","status":"ready"}""");
            await Task.Run(() => { Call(second, "Poll"); Call(second, "WindowAppeared"); });
            Check(state.IsTerminal && state.Snapshot.Phase == LaunchProgressPhase.Ready, "Progress must continue after display was dismissed");
        }
        finally
        {
            var firstDirectory = ((MlsProgressChannel)Field(first, "_channel")).DirectoryPath;
            var secondDirectory = ((MlsProgressChannel)Field(second, "_channel")).DirectoryPath;
            ((IDisposable)first).Dispose(); ((IDisposable)second).Dispose();
            await Task.Delay(850);
            Check(!Directory.Exists(firstDirectory) && !Directory.Exists(secondDirectory), "Session telemetry files leaked");
        }
    }
}
