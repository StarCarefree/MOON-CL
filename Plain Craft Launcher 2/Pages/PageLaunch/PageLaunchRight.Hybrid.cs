using System.Globalization;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Launcher;
using System.Windows;

namespace PCL;

public partial class PageLaunchRight
{
    private string _packDatePath = "";
    private DateTime _packDateChecked;
    private string? _packDate;

    public void PlayPageSwitch()
    {
        var sections = VisibleSections();
        foreach (var section in sections)
            foreach (var control in GetAllAnimControls(section))
                if (!disabledPageAnimControls.Contains(control))
                    disabledPageAnimControls.Add(control);
        HybridMotion.Enter("Hybrid Games Enter", sections);
    }

    private void PlayPageExit()
    {
        if (PageState is not (PageStates.ContentEnter or PageStates.ContentStay)) return;
        HybridMotion.Leave("Hybrid Games Enter", VisibleSections());
    }

    private FrameworkElement[] VisibleSections()
    {
        var sections = new List<FrameworkElement>();
        foreach (var child in PanMain.Children)
            if (child is FrameworkElement element && element.Visibility == Visibility.Visible)
                sections.Add(element);
        return sections.ToArray();
    }

    public void PlayVersionSwitch() => HybridMotion.Crossfade(HeroText);

    public void ShowSelectedVersion(McInstance? instance)
    {
        LabNativeGameName.Text = instance?.Name ?? Lang.Text("Hybrid.EmptyGameTitle");
        NativeVersionSettings.IsEnabled = instance is not null && !ModLaunch.IsInstanceLaunching(instance);
        NativeVersionFolder.IsEnabled = instance is not null;
        LabNativeGameDetail.Text = instance is null ? Lang.Text("Hybrid.EmptyGameDetail") : DescribeVersion(instance);
        var icon = instance is null ? null : HybridSidebar.IconFor(instance);
        HeroIcon.Source = icon;
        HeroIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        HeroIconLetter.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
        HeroIconLetter.Text = string.IsNullOrEmpty(instance?.Name) ? "M" : instance.Name[..1];
        RunningInstanceCards.Refresh(PanRunning, instance);
        RefreshDashboardData(instance);
    }

    private string DescribeVersion(McInstance instance)
    {
        var version = VersionAndLoader(instance);
        var intro = PackIdentityStore.TryRead(instance.PathInstance)?.HeroText;
         var text = string.IsNullOrEmpty(intro) ? CustomDescription(instance) ?? version : version + "\n" + intro;
         var release = PackReleaseWatcher.NoticeText(instance.PathInstance);
         if (!string.IsNullOrEmpty(release)) text += "\n" + release;
         var updated = PackUpdateDate(instance);
         return string.IsNullOrEmpty(updated) ? text : text + " · " + updated;
    }

    private static string? CustomDescription(McInstance instance)
    {
        try
        {
            var custom = States.Instance.CustomInfo[instance.PathInstance]?.Trim();
            if (string.IsNullOrEmpty(custom) || custom == Lang.Text("Select.Instance.Description.NotLoaded")) return null;
            if (custom == instance.GetDefaultDescription()) return null;
            return custom;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取版本描述失败", ModBase.LogLevel.Debug);
            return null;
        }
    }

    private static string VersionAndLoader(McInstance instance)
    {
        try
        {
            var info = instance.Info;
            var game = info.VanillaName?.Trim() ?? "";
            var unknown = Lang.Text("Minecraft.Version.Unknown");
            var loaders = new List<string>();
            void Add(bool has, string name, string? version)
            {
                if (!has) return;
                version = version?.Trim();
                loaders.Add(string.IsNullOrEmpty(version) || version == unknown ? name : name + " " + version.Replace('_', ' '));
            }
            Add(info.HasForge, "Forge", info.Forge);
            Add(info.HasNeoForge, "NeoForge", info.NeoForge);
            Add(info.HasCleanroom, "Cleanroom", info.Cleanroom);
            Add(info.HasFabric, "Fabric", info.Fabric);
            Add(info.HasLegacyFabric, "Legacy Fabric", info.LegacyFabric);
            Add(info.HasQuilt, "Quilt", info.Quilt);
            Add(info.HasLabyMod, "LabyMod", info.LabyMod);
            Add(info.HasOptiFine, "OptiFine", info.OptiFine);
            if (info.HasLiteLoader) loaders.Add("LiteLoader");
            if (game.Length == 0 && loaders.Count == 0) return instance.Name;
            if (loaders.Count == 0) return game;
            return game.Length == 0 ? string.Join(" + ", loaders) : game + " + " + string.Join(" + ", loaders);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取版本与加载器失败", ModBase.LogLevel.Debug);
            return instance.Name;
        }
    }

    private string? PackUpdateDate(McInstance instance)
    {
        var path = instance.PathInstance ?? "";
        if (string.Equals(path, _packDatePath, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _packDateChecked < TimeSpan.FromSeconds(5))
            return _packDate;
        _packDatePath = path;
        _packDateChecked = DateTime.UtcNow;
        _packDate = ReadPackUpdateDate(instance);
        return _packDate;
    }

    private static string? ReadPackUpdateDate(McInstance instance)
    {
        try
        {
            DateTime? latest = null;
            void Consider(string file)
            {
                if (!File.Exists(file)) return;
                var time = File.GetLastWriteTime(file);
                if (latest is null || time > latest) latest = time;
            }
            void Scan(string root)
            {
                if (string.IsNullOrEmpty(root)) return;
                Consider(Path.Combine(root, "PCL", "launcher-pack.json"));
                Consider(Path.Combine(root, ".updatemod", "packwiz.hash"));
                var sources = Path.Combine(root, ".updatemod", "sources");
                if (!Directory.Exists(sources)) return;
                foreach (var hash in Directory.EnumerateFiles(sources, "packwiz.hash", SearchOption.AllDirectories))
                    Consider(hash);
            }
            Scan(instance.PathInstance);
            var marker = Path.Combine(instance.PathInstance, "PCL", "launcher-pack.json");
            var props = Path.Combine(instance.PathInstance, "config", "mod-loading-screen", "update.properties");
            if ((File.Exists(marker) || File.Exists(props)) &&
                !Config.Instance.IndieV2Config.IsDefault(instance.PathInstance) &&
                !Config.Instance.IndieV2[instance.PathInstance])
                Scan(ModFolder.mcFolderSelected);
            return latest?.ToString("yy.MM.dd", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取整合包更新日期失败", ModBase.LogLevel.Debug);
            return null;
        }
    }

    private async void NativeBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (ModMain.frmMain is { } form) await form.NavigateHybridAsync("browse");
    }

    private async void NativeVersions_Click(object sender, RoutedEventArgs e)
    {
        if (ModMain.frmMain is { } form) await form.NavigateHybridAsync("versions");
    }

    private void NativeVersionSettings_Click(object sender, RoutedEventArgs e)
    {
        ModMain.frmLaunchLeft?.OpenSelectedVersionSettings();
    }

    private void NativeVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_dashboardSelected is { } instance)
            DashboardOpenFolder(instance.PathInstance);
    }

}
