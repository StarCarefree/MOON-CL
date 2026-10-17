using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static class AccountChecks
{
    internal static void Run(Assembly assembly)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var core = Assembly.Load("PCL.Core");
        var service = core.GetType("PCL.Core.Minecraft.Profile.ProfileService", true)!;
        // In-memory fixtures only: no config providers, credentials or network authentication.
        service.GetField("_canPersist", statics)!.SetValue(null, false);
        var profiles = service.GetField("_profiles", statics)!.GetValue(null)!;
        profiles.GetType().GetMethod("LoadFromString")!.Invoke(profiles, new object[]
        {
            "{\"lastUsed\":0,\"profiles\":[{\"profileId\":\"smoke-a\",\"userName\":\"SmokeAlpha\",\"uuid\":\"00000000000000000000000000000001\"},{\"profileId\":\"smoke-b\",\"userName\":\"SmokeBeta\",\"uuid\":\"00000000000000000000000000000002\"}]}"
        });
        var main = assembly.GetType("PCL.ModMain", true)!;
        var launchType = assembly.GetType("PCL.PageLaunchLeft", true)!;
        var launch = (FrameworkElement)Activator.CreateInstance(launchType)!;
        main.GetField("frmLaunchLeft", statics)!.SetValue(null, launch);
        launchType.GetMethod("RefreshPage")!.Invoke(launch, new object[] { false, 0 });
        var skin = (FrameworkElement)main.GetField("frmLoginProfileSkin", statics)!.GetValue(null)!;
        skin.GetType().GetMethod("Reload")!.Invoke(skin, null);
        Check(((TextBlock)skin.FindName("TextName")).Text == "SmokeAlpha", "fixture shows initial account");
        var overlayType = assembly.GetType("PCL.HybridAccountOverlay", true)!;
        var overlay = (FrameworkElement)Activator.CreateInstance(overlayType)!;
        overlayType.GetMethod("ReloadAccounts", hidden)!.Invoke(overlay, new object[] { false });
        var rows = (StackPanel)overlay.FindName("AccountList");
        foreach (var (index, name) in new[] { (1, "SmokeBeta"), (0, "SmokeAlpha"), (1, "SmokeBeta") })
        {
            var button = (Button)((Grid)rows.Children[index]).Children[0];
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var current = service.GetProperty("Current", statics)!.GetValue(null)!;
            Check((string)current.GetType().GetProperty("UserName")!.GetValue(current)! == name, "selected profile changes to " + name);
            var input = (IEnumerable)launchType.GetMethod("SkinLegacyInput", statics)!.Invoke(null, null)!;
            Check(input.Cast<string>().First() == name, "skin input follows selected profile");
            Check(((TextBlock)skin.FindName("TextName")).Text == name, "same-type switch refreshes displayed account to " + name);
            Check(ReferenceEquals(skin.Parent, overlay.FindName("PanProfileHost")), "profile management is hosted in account window");
            var login = assembly.GetType("PCL.ModLaunch", true)!.GetMethod("McLoginInput", statics)!.Invoke(null, null)!;
            Check((string)login.GetType().GetField("UserName")!.GetValue(login)! == name, "launch login uses selected account");
            Check(((FrameworkElement)overlay.FindName("CurrentScroll")).Visibility == Visibility.Visible, "selected account opens management");
        }
        Console.WriteLine("PASS existing-account A/B/A/B selection and displayed identity");
        var createHost = (Panel)overlay.FindName("PanCreateHost");
        launchType.GetProperty("ExternalLoginHost", hidden)!.SetValue(launch, createHost);
        service.GetProperty("IsCreatingProfile", statics)!.SetValue(null, true);
        // Emulate leaving a new-account form to select an existing account.
        ((Button)((Grid)rows.Children[0]).Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!(bool)service.GetProperty("IsCreatingProfile", statics)!.GetValue(null)!, "existing selection exits creation mode");
        Check(launchType.GetProperty("ExternalLoginHost", hidden)!.GetValue(launch) is null, "creation host detached");
        var start = (FrameworkElement)launch.FindName("BtnLaunch");
        Point? position = null;
        foreach (var height in new[] { 360d, 800d })
        {
            launch.Measure(new Size(340, height)); launch.Arrange(new Rect(0, 0, 340, height)); launch.UpdateLayout();
            var point = start.TranslatePoint(new Point(), launch);
            Check(position is null || point == position.Value, "launch position stays fixed across window heights");
            position = point;
        }
        Check(((FrameworkElement)launch.FindName("PanVersionActions")).Visibility == Visibility.Collapsed, "floating launch area contains no version actions");
        Check(((FrameworkElement)launch.FindName("PanLogin")).Visibility == Visibility.Collapsed, "home account pane removed");
        var skinButton = (Button)skin.FindName("BtnSkin");
        Check(skinButton.Visibility == Visibility.Visible && skinButton.ContextMenu.Items.Count == 4, "skin and cape actions migrated");
        Check(((Button)skin.FindName("BtnEdit")).ContextMenu.Items.Count == 2, "profile editing actions migrated");
        Console.WriteLine("PASS creation-mode exit / profile host / fixed launch geometry / migrated actions");
        var loginType = assembly.GetType("PCL.ModLaunch+McLoginType", true)!;
        overlayType.GetMethod("OpenLogin", hidden)!.Invoke(overlay, new[] { Enum.Parse(loginType, "Legacy") });
        Check(((FrameworkElement)overlay.FindName("CreateScroll")).Visibility == Visibility.Visible, "external login entry opens visible form");
        Check(ReferenceEquals(launchType.GetProperty("ExternalLoginHost", hidden)!.GetValue(launch), createHost), "external login uses overlay host");
        ((Button)overlay.FindName("CurrentTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!(bool)service.GetProperty("IsCreatingProfile", statics)!.GetValue(null)!, "returning to current account exits creation");
        Check(((FrameworkElement)overlay.FindName("CurrentScroll")).Visibility == Visibility.Visible, "return to current account management");
        Console.WriteLine("PASS external login form entry and return to current account");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("Account check failed: " + message);
    }
}
