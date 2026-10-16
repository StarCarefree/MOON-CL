using System;

namespace PCL;

public partial class PageLaunchRight
{
    private void LaunchScreen_HomeRequested(object? sender, EventArgs e)
    {
        // Returning home never aborts the underlying launch.
        ModMain.frmLaunchLeft?.PageChangeToLogin();
        LaunchScreen.EndLaunch();
    }

    private void LaunchScreen_CancelRequested(object? sender, EventArgs e)
        => ModMain.frmLaunchLeft?.CancelLaunch();
}
