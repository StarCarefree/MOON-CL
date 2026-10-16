using System;
using System.Linq;
using PCL.Core.App.Localization;

namespace PCL;

public static partial class ModLaunch
{
    // Called on the launch worker, whose AsyncLocal selects the correct session (never the globally latest launch).
    private static void RefreshSessionProgress()
    {
        var session = _launchSession.Value;
        if (session?.Presentation is null || session.Real is null) return;
        try
        {
            var stage = session.Real.GetLoaderList(false).FirstOrDefault(loader =>
                loader.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting)?.name ?? Lang.Text("Launch.Status.Completed");
            session.Presentation.State.Launcher(stage, session.Real.Progress, "Java、账号验证与游戏文件准备");
        }
        catch (InvalidOperationException) { /* loader collection changed; the next refresh has the new stage */ }
    }
}
