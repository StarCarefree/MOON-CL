#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.UI.WebHost;

namespace PCL;

// Only compiled in Debug and enabled by an explicit test launch flag. No game launch, account or install automation.
public partial class FormMain
{
    private async Task StartHybridSmokeAsync()
    {
        if (!Basics.CommandLineArguments.Contains("--hybrid-smoke")) return;
        var directory = Environment.GetEnvironmentVariable("PCL_UI_TEST_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            Width = 1280; Height = 780;
            for (var i = 0; i < 120 && (PanMsgBackground.Visibility == Visibility.Visible || PanMsg.Children.Count > 0); i++)
                await Task.Delay(250);
            if (PanMsgBackground.Visibility == Visibility.Visible) return; // Never confirm legal/authentication dialogs automatically.
            UpdateLayout();
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "native-default.json"), HybridStatus().ToJsonString());
            CaptureHybridNativeForTest();
            await NavigateHybridAsync("browse");
        }
        catch (Exception ex) { ModBase.Log(ex, "互补界面测试初始化失败", ModBase.LogLevel.Debug); }
    }

    internal async Task NavigateHybridForTestAsync(string? page, string? id)
    {
        if (PanMsgBackground.Visibility == Visibility.Visible) return;
        Activate(); // A synthetic test click must model the activation caused by a real user click.
        if (id is not null)
        {
            var route = LauncherPageCatalog.Find(id);
            if (route is null || route.Id == "host.status") return;
            PageChange(new PageStackData { page = (PageType)route.NativePage }, (PageSubType)route.NativeSubType);
        }
        else if (HybridSidebarView.Nav.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, page)) is { } button)
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        else if (page == "versions" && ModMain.frmLaunchLeft is { } launch && launch.BtnInstance.IsEnabled)
            launch.BtnInstance.WebClick();
        else if (page == "versionSettings" && ModMain.frmLaunchLeft is { } version && version.BtnMore.IsEnabled)
            version.BtnMore.WebClick();
        else if (page is "versions" or "versionSettings" or "tasks")
            await NavigateHybridAsync(page);
        await Task.Delay(20);
    }

    internal void CaptureHybridNativeForTest()
    {
        var directory = Environment.GetEnvironmentVariable("PCL_UI_TEST_DIR");
        if (_webUi.IsPresented || string.IsNullOrWhiteSpace(directory) || PanMsgBackground.Visibility == Visibility.Visible) return;
        if (Content is not FrameworkElement root || root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
        UpdateLayout();
        var scale = PresentationSource.FromVisual(root)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale.M11), (int)Math.Ceiling(root.ActualHeight * scale.M22), 96 * scale.M11, 96 * scale.M22, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, $"native-{(int)(PageType)pageCurrent}-{(int)PageCurrentSub}.png"));
        encoder.Save(output);
    }
}
#endif
