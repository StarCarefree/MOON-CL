using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PCL;

public sealed class HybridNavigationEventArgs(string route, McInstance? instance = null) : EventArgs
{
    public string Route { get; } = route;
    public McInstance? Instance { get; } = instance;
}

public partial class HybridSidebar : UserControl
{
    public sealed class VersionEntry : INotifyPropertyChanged
    {
        public VersionEntry(string name, string displayName, string description, ImageSource? icon, bool isLaunching, McInstance instance)
        {
            Name = name;
            DisplayName = displayName;
            Description = description;
            Icon = icon;
            IsLaunching = isLaunching;
            Instance = instance;
        }

        public string Name { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public ImageSource? Icon { get; }
        public bool IsLaunching { get; }
        public McInstance Instance { get; }
        public int RunningCount { get; private set; }
        public string RunningLabel { get; private set; } = "";
        public string PlayText { get; private set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetActivity(int running, string label, string play)
        {
            if (RunningCount != running)
            {
                RunningCount = running;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningCount)));
            }
            if (RunningLabel != label)
            {
                RunningLabel = label;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningLabel)));
            }
            if (PlayText != play)
            {
                PlayText = play;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayText)));
            }
        }
    }

    private bool _versionListHover;
    public event EventHandler<HybridNavigationEventArgs>? NavigationRequested;
    private bool _synchronising;
    private string _versionsSignature = "";
    private readonly HashSet<string> _seenLaunching = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource> Icons = new(StringComparer.OrdinalIgnoreCase);

    public HybridSidebar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            foreach (var button in Nav.Children.OfType<Button>().Append(AccountButton))
                button.PreviewMouseLeftButtonDown += (_, _) => HybridMotion.Press(button);
        };
    }

    public void PlayEntrance() => HybridMotion.SlideIn(Nav.Children.OfType<FrameworkElement>().Append(AccountButton).ToArray());

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string route })
            NavigationRequested?.Invoke(this, new HybridNavigationEventArgs(route));
    }

    private void Version_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronising && Versions.SelectedItem is VersionEntry entry)
            NavigationRequested?.Invoke(this, new HybridNavigationEventArgs("instance", entry.Instance));
    }

    private void VersionSettings_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionEntry entry }) return;
        e.Handled = true;
        NavigationRequested?.Invoke(this, new HybridNavigationEventArgs("instanceSettings", entry.Instance));
    }

    public void SetActive(string route)
    {
        foreach (var button in Nav.Children.OfType<Button>())
            HybridNavigation.SetIsActive(button, Equals(button.Tag, route));
    }

    public void UpdateVersions(McInstance[] instances, McInstance? selected, string[]? launchingPaths = null, string? launchingPrefix = null)
    {
        var launching = launchingPaths ?? [];
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < launching.Length; i++)
            if (!string.IsNullOrEmpty(launching[i])) rank[launching[i]] = i;
        var prefix = string.IsNullOrWhiteSpace(launchingPrefix) ? "启动中" : launchingPrefix;
        var ordered = instances
            .Select((instance, index) => (instance, index))
            .OrderBy(item => rank.ContainsKey(item.instance.PathInstance ?? "") ? 0 : 1)
            .ThenBy(item => rank.TryGetValue(item.instance.PathInstance ?? "", out var place) ? place : item.index)
            .Select(item => item.instance)
            .ToArray();
        var newlyLaunching = rank.Keys.Any(path => !_seenLaunching.Contains(path));
        _seenLaunching.Clear();
        foreach (var path in rank.Keys) _seenLaunching.Add(path);
        var signature = string.Join("\n", ordered.Select(instance =>
            instance.PathInstance + "|" + instance.Name + "|" + instance.Desc + "|" + VersionIconPath(instance) + "|" + rank.ContainsKey(instance.PathInstance ?? "")));
        _synchronising = true;
        try
        {
            if (signature != _versionsSignature)
            {
                _versionsSignature = signature;
                Versions.ItemsSource = ordered.Select(instance =>
                {
                    var starting = rank.ContainsKey(instance.PathInstance ?? "");
                    return new VersionEntry(instance.Name, starting ? prefix + " " + instance.Name : instance.Name,
                        VersionLine(instance), IconFor(instance), starting, instance);
                }).ToArray();
            }
            Versions.SelectedItem = Versions.Items.OfType<VersionEntry>().FirstOrDefault(entry =>
                string.Equals(entry.Instance.PathInstance, selected?.PathInstance, StringComparison.OrdinalIgnoreCase));
            Empty.Visibility = instances.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _synchronising = false; }
        RefreshActivity();
        if (newlyLaunching && Versions.Items.Count > 0)
            Versions.Dispatcher.BeginInvoke(ScrollVersionsToTop, DispatcherPriority.Loaded);
    }

    private void ScrollVersionsToTop()
    {
        if (Versions.Items.Count == 0) return;
        Versions.ScrollIntoView(Versions.Items[0]);
        if (Versions.Template?.FindName("VersionScroll", Versions) is ScrollViewer viewer)
            viewer.ScrollToVerticalOffset(0);
    }

    private void RefreshActivity()
    {
        foreach (var entry in Versions.Items.OfType<VersionEntry>())
        {
            var count = InstanceActivity.Count(entry.Instance.PathInstance);
            entry.SetActivity(count, count == 0 ? "" : InstanceActivity.RunningText(count), InstanceActivity.PlayText(entry.Instance.PathInstance));
        }
        var total = InstanceActivity.DistinctCount();
        VersionListRunning.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
        VersionListRunningText.Text = total == 0 ? "" : _versionListHover ? InstanceActivity.RunningText(total) : total.ToString();
    }

    private void VersionList_MouseEnter(object sender, MouseEventArgs e)
    {
        _versionListHover = true;
        RefreshActivity();
    }

    private void VersionList_MouseLeave(object sender, MouseEventArgs e)
    {
        _versionListHover = false;
        RefreshActivity();
    }
    private static string VersionLine(McInstance instance)
    {
        var desc = instance.Desc?.Trim() ?? "";
        var missing = PCL.Core.App.Localization.Lang.Text("Select.Instance.Description.NotLoaded");
        if (desc.Length > 0 && desc != missing) return desc;
        try
        {
            if (!instance.IsLoaded) instance.Load();
            var fallback = instance.GetDefaultDescription()?.Trim();
            if (!string.IsNullOrEmpty(fallback)) return fallback;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取版本与加载器失败", ModBase.LogLevel.Debug);
        }
        return desc == missing ? "" : desc;
    }

    public void UpdateAccount(string name, ImageSource? face, ImageSource? hat)
    {
        Account.Text = name;
        Avatar.Source = face;
        AvatarFore.Source = hat;
        AvatarFore.Visibility = hat is null ? Visibility.Collapsed : Visibility.Visible;
        AvatarLetter.Text = string.IsNullOrEmpty(name) ? "?" : name[..1];
        AvatarLetter.Visibility = face is null && hat is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void AnimateSelected(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Versions.ItemContainerGenerator.ContainerFromItem(Versions.SelectedItem) is not FrameworkElement item) return;
            if (!string.Equals((item.DataContext as VersionEntry)?.Instance.PathInstance, path, StringComparison.OrdinalIgnoreCase)) return;
            HybridMotion.Swap(item);
        }, DispatcherPriority.Loaded);
    }

    private static string VersionIconPath(McInstance instance)
    {
        var logo = instance.Logo;
        if (!string.IsNullOrWhiteSpace(logo))
        {
            if (logo.EndsWith(@"PCL\Logo.png", StringComparison.OrdinalIgnoreCase) ||
                logo.EndsWith("PCL/Logo.png", StringComparison.OrdinalIgnoreCase))
                return instance.PathInstance + @"PCL\Logo.png";
            return logo;
        }
        return instance.state switch
        {
            McInstanceState.Original => ModBase.pathImage + "Blocks/Grass.png",
            McInstanceState.Snapshot => ModBase.pathImage + "Blocks/CommandBlock.png",
            McInstanceState.Old => ModBase.pathImage + "Blocks/CobbleStone.png",
            McInstanceState.Forge => ModBase.pathImage + "Blocks/Anvil.png",
            McInstanceState.NeoForge => ModBase.pathImage + "Blocks/NeoForge.png",
            McInstanceState.Cleanroom => ModBase.pathImage + "Blocks/Cleanroom.png",
            McInstanceState.Fabric or McInstanceState.LegacyFabric => ModBase.pathImage + "Blocks/Fabric.png",
            McInstanceState.Quilt => ModBase.pathImage + "Blocks/Quilt.png",
            McInstanceState.OptiFine => ModBase.pathImage + "Blocks/GrassPath.png",
            McInstanceState.LiteLoader => ModBase.pathImage + "Blocks/Egg.png",
            McInstanceState.Fool => ModBase.pathImage + "Blocks/GoldBlock.png",
            McInstanceState.LabyMod => ModBase.pathImage + "Blocks/LabyMod.png",
            _ => ModBase.pathImage + "Blocks/RedstoneBlock.png"
        };
    }

    internal static ImageSource? IconFor(McInstance instance) => LoadIcon(VersionIconPath(instance));


    private static ImageSource? LoadIcon(string path)
    {
        if (Icons.TryGetValue(path, out var cached)) return cached;
        try
        {
            ImageSource image = new MyBitmap(path);
            Icons[path] = image;
            return image;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "侧栏版本图标加载失败：" + path, ModBase.LogLevel.Debug);
            var fallback = ModBase.pathImage + "Blocks/RedstoneBlock.png";
            return string.Equals(path, fallback, StringComparison.OrdinalIgnoreCase) ? null : LoadIcon(fallback);
        }
    }
}
