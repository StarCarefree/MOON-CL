using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL;

public partial class HybridAccountOverlay
{
    public event EventHandler? Closed;
    private bool _closing;
    private bool _probing;

    public HybridAccountOverlay()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    public void Open()
    {
        if (IsVisible && !_closing) 
        {
            ReloadAccounts(false);
            return;
        }
        _closing = false;
        if (ModMain.frmLaunchLeft is { } launch)
            launch.ExternalLoginEnded += OnLoginEnded;
        ShowMethods();
        Visibility = Visibility.Visible;
        ReloadAccounts(true);
        HybridMotion.Pop(Card, Backdrop);
        HybridMotion.SlideIn(LeftPane, RightPane);
        Keyboard.Focus(this);
    }

    public void Close()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        DetachHost();
        if (ModMain.frmLaunchLeft is { } launch)
            launch.ExternalLoginEnded -= OnLoginEnded;
        HybridMotion.Dismiss(Card, Backdrop, () =>
        {
            _closing = false;
            Visibility = Visibility.Collapsed;
            Closed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnLoginEnded(object? sender, EventArgs e)
    {
        ShowMethods();
        ReloadAccounts(false);
    }

    private void ShowMethods()
    {
        Methods.Visibility = Visibility.Visible;
        CreateScroll.Visibility = Visibility.Collapsed;
        BackMethods.Visibility = Visibility.Collapsed;
    }

    private void ShowForm()
    {
        Methods.Visibility = Visibility.Collapsed;
        CreateScroll.Visibility = Visibility.Visible;
        BackMethods.Visibility = Visibility.Visible;
        if (ModMain.frmLaunchLeft is not { } launch) return;
        launch.ExternalLoginHost = PanCreateHost;
    }

    private void DetachHost()
    {
        if (ModMain.frmLaunchLeft is not { } launch) return;
        if (!ReferenceEquals(launch.ExternalLoginHost, PanCreateHost)) return;
        launch.CancelCreateLogin();
    }

    private void ReloadAccounts(bool animate)
    {
        AccountList.Children.Clear();
        ProfileService.Load();
        var current = ProfileService.Current?.ProfileId;
        foreach (var profile in ProfileService.Profiles)
            AccountList.Children.Add(CreateRow(profile, profile.ProfileId == current));
        EmptyAccounts.Visibility = AccountList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (animate)
            HybridMotion.SlideIn(AccountList.Children.OfType<FrameworkElement>().ToArray());
    }

    private UIElement CreateRow(McProfile profile, bool selected)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var select = new Button
        {
            Style = (Style)FindResource("Hybrid.QuietButton"),
            Height = 52,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Tag = profile
        };
        AutomationProperties.SetAutomationId(select, "Hybrid.Account.Row");
        HybridNavigation.SetIsActive(select, selected);
        select.Click += Select_Click;
        select.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                CreateAvatar(profile),
                new StackPanel
                {
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = profile.UserName,
                            Foreground = (Brush)FindResource("Hybrid.Text"),
                            FontSize = 13,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        },
                        new TextBlock
                        {
                            Text = (selected ? Lang.Text("Hybrid.Account.Current") + " · " : "") + ProfileUi.GetProfileInfo(profile),
                            Foreground = (Brush)FindResource("Hybrid.Muted"),
                            FontSize = 10,
                            Margin = new Thickness(0, 2, 0, 0),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    }
                }
            }
        };
        var delete = new Button
        {
            Style = (Style)FindResource("Hybrid.QuietButton"),
            Width = 36,
            Height = 36,
            Margin = new Thickness(6, 0, 0, 0),
            Content = "×",
            Tag = profile,
            ToolTip = Lang.Text("Hybrid.Account.Delete")
        };
        AutomationProperties.SetAutomationId(delete, "Hybrid.Account.Delete");
        delete.Click += Delete_Click;
        Grid.SetColumn(delete, 1);
        row.Children.Add(select);
        row.Children.Add(delete);
        return row;
    }

    private UIElement CreateAvatar(McProfile profile)
    {
        var letter = new TextBlock
        {
            Text = string.IsNullOrEmpty(profile.UserName) ? "?" : profile.UserName[..1],
            Foreground = (Brush)FindResource("Hybrid.Accent"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        var image = new Image { Stretch = Stretch.Uniform };
        var path = ModBase.pathTemp + $@"Cache\Skin\Head\{profile.SkinHeadId}.png";
        if (File.Exists(path))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image.Source = bitmap;
                letter.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "读取账号头像失败", ModBase.LogLevel.Debug);
            }
        }
        return new Border
        {
            Width = 30,
            Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(40, 43, 48)),
            CornerRadius = new CornerRadius(5),
            Child = new Grid { Children = { letter, image } }
        };
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: McProfile profile } || ModMain.frmLaunchLeft is not { } launch) return;
        launch.ExternalLoginHost = null;
        ProfileService.IsCreatingProfile = false;
        ProfileService.Select(profile);
        ModLaunch.mcLoginMsLoader.State = ModBase.LoadState.Waiting;
        ModLaunch.mcLoginAuthLoader.State = ModBase.LoadState.Waiting;
        ModLaunch.mcLoginLegacyLoader.State = ModBase.LoadState.Waiting;
        launch.RefreshPage(true);
        launch.BtnLaunch.IsEnabled = true;
        Close();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: McProfile profile }) return;
        if (ModMain.MyMsgBox(Lang.Text("Launch.Account.Profile.DeleteConfirm.Message"),
                Lang.Text("Launch.Account.Profile.DeleteConfirm.Title"),
                Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true) == 2)
            return;
        ProfileUi.RemoveProfile(profile);
        ReloadAccounts(false);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void LittleSkin_Click(object sender, RoutedEventArgs e) => BeginAuth(PageLoginAuth.DefaultAuthServer);

    private void Microsoft_Click(object sender, RoutedEventArgs e) => Begin(ModLaunch.McLoginType.Ms);

    private void Offline_Click(object sender, RoutedEventArgs e) => Begin(ModLaunch.McLoginType.Legacy);

    private void Custom_Click(object sender, RoutedEventArgs e)
    {
        var server = ModMain.MyMsgBoxAuthServer("", new Dictionary<string, string>
        {
            { Lang.Text("Hybrid.Account.Custom"), "" }
        }, Lang.Text("Hybrid.Account.CustomTitle"));
        if (string.IsNullOrWhiteSpace(server)) return;
        BeginAuth(server);
    }

    private void Begin(ModLaunch.McLoginType type)
    {
        if (_probing || ModMain.frmLaunchLeft is null) return;
        ProfileService.IsCreatingProfile = true;
        ShowForm();
        ModMain.frmLaunchLeft.ShowCreateLogin(type);
    }

    private void BeginAuth(string server)
    {
        if (_probing || string.IsNullOrWhiteSpace(server) || ModMain.frmLaunchLeft is null) return;
        _probing = true;
        Methods.IsEnabled = false;
        ModBase.RunInNewThread(() =>
        {
            bool? oauth = null;
            try { oauth = PageLoginAuth.IsOAuthSupportedAsync(server).GetAwaiter().GetResult(); }
            catch (Exception ex) { ModBase.Log(ex, "检测皮肤站登录方式失败", ModBase.LogLevel.Debug); }
            ModBase.RunInUi(() =>
            {
                _probing = false;
                Methods.IsEnabled = true;
                if (!IsVisible || _closing) return;
                PageLoginAuth.draggedAuthServer = server;
                PageLoginAuth.draggedAuthServerOAuthSupported = oauth;
                ProfileService.IsCreatingProfile = true;
                ShowForm();
                ModMain.frmLaunchLeft.ShowCreateLogin(ModLaunch.McLoginType.Auth);
            });
        });
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        DetachHost();
        ShowMethods();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Backdrop_Click(object sender, MouseButtonEventArgs e) => Close();

    private void Card_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
