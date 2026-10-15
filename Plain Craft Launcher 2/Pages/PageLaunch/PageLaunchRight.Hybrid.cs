using System.Windows;

namespace PCL;

public partial class PageLaunchRight
{
    private void PlayHybridEntrance() => HybridMotion.Rise(HeroCard, NativeHintCard);

    public void PlayVersionSwitch() => HybridMotion.Swap(HeroIdentity);

    private async void NativeBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (ModMain.frmMain is { } form) await form.NavigateHybridAsync("browse");
    }

    private async void NativeVersions_Click(object sender, RoutedEventArgs e)
    {
        if (ModMain.frmMain is { } form) await form.NavigateHybridAsync("versions");
    }
}
