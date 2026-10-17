using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Secret;

namespace PCL;

internal static class Program
{
    /// <summary>
    /// Program startup point
    /// </summary>
    [STAThread]
    public static void Main()
    {
        if (TryHandJoinToRunning()) return;
        if (Basics.CommandLineArguments.Contains("--console")) KernelInterop.AllocateConsole();

#if DEBUG
        if (Basics.CommandLineArguments.Contains("--debug"))
        {
            Console.WriteLine("Waiting for debugger...");
            while (!Debugger.IsAttached) Thread.Sleep(50);
        }
#endif
        // 识别码依赖 WMI 查询，首次调用可能明显耗时。尽早丢到后台，
        // 让它与生命周期加载并行，而不是在启动线程上干等。
        Identify.Prewarm();
        // Preloading tasks
        ApplicationService.Loading = static () =>
        {
            var app = new Application();
            app.InitializeComponent();
            return app;
        };
        MainWindowService.Loading = static () =>
        {
            var form = new FormMain();
            return form;
        };
        // From dotnet/wpf #2393: fix tablet devices broken on .NET Core 3.0+
        _ = Tablet.TabletDevices;
        // Start lifecycle
        Lifecycle.OnInitialize();
    }

    private static bool TryHandJoinToRunning()
    {
        var args = Basics.CommandLineArguments;
        var index = Array.IndexOf(args, "--join-room");
        if (index < 0 || index + 1 >= args.Length) return false;
        var code = args[index + 1].Trim().ToUpperInvariant();
        if (code.Length != 8 || !code.All(char.IsAsciiLetterOrDigit)) return false;
        FormMain.QueueJoinRoom(code);
        var existing = Process.GetProcessesByName(Basics.ExecutableNameWithoutExtension)
            .FirstOrDefault(process => process.Id != Environment.ProcessId && process.MainWindowHandle != 0);
        if (existing is null) return false;
        var handoff = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MOONCL", "join-room.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(handoff)!);
        File.WriteAllText(handoff, code);
        ModMain.ShowWindowToTop(existing.MainWindowHandle);
        return true;
    }
}

