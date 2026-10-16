using System;
using System.Net.Http;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.WebHost;

namespace PCL;

/// <summary>Optional browse/pack-services surface. The window, game launch and all original pages remain native.</summary>
internal sealed partial class WebUiController : IDisposable
{
    private WebView2? _view;
    private LauncherLibrary? _library;
    private FormMain? _form;
    private DispatcherTimer? _timer;
    private Task? _initializing;
    private DependencyPropertyDescriptor? _modalVisibility;
    private int _generation;
    private bool _ready;
    private bool _disposed;
    private string _runtimeVersion = "";
    private bool _bundledRuntime;
    private string _lastSnapshot = "";
    private bool? _lastPresentation;

    public bool IsAttached => _view is not null;
    public bool IsPresented { get; private set; }
    internal LauncherLibrary Library { get { _library ??= new LauncherLibrary(); return _library; } }
    public bool HasKeyboardFocus => IsPresented && _view?.Visibility == Visibility.Visible &&
        ((_view?.IsKeyboardFocusWithin ?? false) || (_form is { } form && HybridBrowserFocus.HasNativeChildFocus(form)));
    public static bool RequestedByArguments(System.Collections.Generic.IEnumerable<string> args) => WebView2RuntimePlan.UseWebUi(args.ToArray());
    private static Task SwitchToUiAsync(Dispatcher dispatcher)
    {
        if (dispatcher.CheckAccess()) return Task.CompletedTask;
        return dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Normal).Task;
    }

    private void PostUi(Action action)
    {
        var dispatcher = _form?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    public async Task AttachAsync(FormMain form)
    {
        if (_disposed) return;
        var replay = _ready;
        _form = form;
        IsPresented = true;
        form.SetBrowseVisible(true);
        if (_view is null && _initializing is { IsCompleted: true }) _initializing = null;
        _initializing ??= InitializeAsync(form);
        await _initializing;
        if (_disposed) return;
        await SwitchToUiAsync(form.Dispatcher);
        if (_disposed) return;
        if (replay) _lastPresentation = null;
        UpdatePresentation();
        if (_ready && IsPresented && form.PanMsgBackground.Visibility != Visibility.Visible && _view is { } view)
        {
            view.UpdateLayout();
            await form.Dispatcher.InvokeAsync(() =>
            {
                if (IsPresented && !_disposed)
                {
                    view.Focus();
                    System.Windows.Input.Keyboard.Focus(view);
                }
            }, DispatcherPriority.Input);
        }
        Refresh(force: true);
    }

    private async Task InitializeAsync(FormMain form)
    {
        var generation = ++_generation;
        try
        {
            var folder = WebView2RuntimePlan.FindBundledFolder(AppContext.BaseDirectory, File.Exists);
            _bundledRuntime = folder is not null;
            Environment.SetEnvironmentVariable(SystemWebView2Plan.LoaderOverride, folder);
            _runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(folder);
            _library ??= new LauncherLibrary();
            var view = new WebView2 { Focusable = true, DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 19, 21) };
            _view = view;
            form.PanBrowse.Children.Add(view);
            _modalVisibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement));
            _modalVisibility.AddValueChanged(form.PanMsgBackground, OnModalChanged);
            form.Closed += OnClosed;
            UpdatePresentation();
            var environment = await CoreWebView2Environment.CreateAsync(folder, WebUiAssets.UserDataFolder());
            if (_disposed || generation != _generation) return;
            await SwitchToUiAsync(form.Dispatcher);
            if (_disposed || generation != _generation) return;
            await view.EnsureCoreWebView2Async(environment);
            await SwitchToUiAsync(form.Dispatcher);
            if (_disposed || generation != _generation || view.CoreWebView2 is null) return;
            var core = view.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = ModBase.modeDebug;
            core.Settings.AreDevToolsEnabled = ModBase.modeDebug;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.SetVirtualHostNameToFolderMapping(WebUiAssets.HostName, WebUiAssets.Extract(), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) =>
            {
                if (WebView2RuntimePlan.IsTrustedSource(e.Uri, WebUiAssets.HostName)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += (_, e) =>
            {
                if (_disposed || generation != _generation || !WebView2RuntimePlan.IsTrustedSource(e.Source, WebUiAssets.HostName)) return;
                var json = e.WebMessageAsJson;
                form.Dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed && generation == _generation) OnMessage(core, json, form);
                });
            };
            core.NavigationCompleted += (_, e) => PostUi(() =>
            {
                if (_disposed || generation != _generation || !e.IsSuccess) return;
                _ready = true;
                SendBootstrap(core);
                _lastPresentation = null;
                UpdatePresentation();
                try { _library?.Capture(); }
                catch (Exception ex) { ModBase.Log(ex, "浏览页首次扫描整合包失败", ModBase.LogLevel.Debug); }
                Refresh(force: true);
                SendPendingPluginBrowse();
                if (IsPresented && form.PanMsgBackground.Visibility != Visibility.Visible) view.Focus();
            });
            _timer = new DispatcherTimer(DispatcherPriority.Background, form.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += OnTick;
            _timer.Start();
            core.Navigate("https://" + WebUiAssets.HostName + "/index.html");
            ModBase.Log($"[WebUi] 浏览补充页已启动：{(_bundledRuntime ? "随包固定运行时" : "系统运行时")} {_runtimeVersion}");
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _generation) return;
            try { await SwitchToUiAsync(form.Dispatcher); }
            catch (Exception switchEx)
            {
                ModBase.Log(switchEx, "浏览补充页回到界面线程失败", ModBase.LogLevel.Debug);
                ModBase.Log(ex, "浏览补充页启动失败，原生界面保持可用");
                return;
            }
            if (_disposed || generation != _generation) return;
            Hide();
            ReleaseView();
            _initializing = null;
            ModBase.Log(ex, "浏览补充页启动失败，原生界面保持可用");
            ModMain.MyMsgBox(Lang.Text("Hybrid.BrowserFailed") + "\n\n" + ex.Message, Lang.Text("Hybrid.Browse"));
        }
    }

    public void Hide()
    {
        IsPresented = false;
        _form?.SetBrowseVisible(false);
        UpdatePresentation();
        if (_form is { } form && form.PanMsgBackground.Visibility != Visibility.Visible)
            System.Windows.Input.Keyboard.Focus(form.PanMain);
    }

    private void OnModalChanged(object? sender, EventArgs e) => UpdatePresentation();
    private void UpdatePresentation()
    {
        if (_view is null || _form is null) return;
        // A WPF overlay cannot cover WebView2's child HWND. Hide the browser during native modal dialogs.
        var visible = IsPresented && _form.PanMsgBackground.Visibility != Visibility.Visible;
        _view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!_ready || _lastPresentation == visible || _view.CoreWebView2 is not { } core) return;
        try
        {
            core.PostWebMessageAsJson(new JsonObject { ["type"] = "presentation", ["visible"] = visible }.ToJsonString());
            _lastPresentation = visible;
        }
        catch (Exception ex) { ModBase.Log(ex, "同步浏览页可见状态失败", ModBase.LogLevel.Debug); }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || !IsPresented || _form is not { } form || form.PanMsgBackground.Visibility == Visibility.Visible) return;
        try
        {
            try { _library?.Capture(); }
            catch (Exception ex) { ModBase.Log(ex, "浏览页扫描整合包失败", ModBase.LogLevel.Debug); }
            Refresh();
            if (_library is not null) await _library.RefreshIfNeededAsync(form);
        }
        catch (Exception ex) { ModBase.Log(ex, "刷新浏览补充页失败", ModBase.LogLevel.Debug); }
    }

    public async Task CheckPackUpdatesOnceAsync()
    {
        if (_disposed) return;
        _library ??= new LauncherLibrary();
        await _library.CheckManagedOnceAsync();
    }

    public bool TryTakeCheckNotice(out string text, out HintType type)
    {
        text = "";
        type = HintType.Info;
        return _library?.TryTakeCheckNotice(out text, out type) == true;
    }

    private void Refresh(bool force = false)
    {
        if (!_ready || !IsPresented || _form is null || _library is null || _view?.CoreWebView2 is not { } core || _form.WindowState == WindowState.Minimized) return;
        try
        {
            var snapshot = new JsonObject { ["type"] = "browse", ["launcher"] = _library.Capture(scan: false) };
            var json = snapshot.ToJsonString();
            if (!force && json == _lastSnapshot) return;
            _lastSnapshot = json;
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { ModBase.Log(ex, "浏览数据同步失败", ModBase.LogLevel.Debug); }
    }

    private async void OnMessage(CoreWebView2 core, string json, FormMain form)
    {
        if (_disposed || json.Length > 131072) return;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject message) return;
            var type = ReadString(message, "type");
            if (type == "status") { core.PostWebMessageAsJson(form.HybridStatus().ToJsonString()); return; }
#if DEBUG
            if (Basics.CommandLineArguments.Contains("--hybrid-smoke"))
            {
                if (type == "testNavigate") { await form.NavigateHybridForTestAsync(ReadString(message, "page"), ReadString(message, "id")); return; }
                if (type == "testCaptureNative") { form.CaptureHybridNativeForTest(); core.PostWebMessageAsJson(form.HybridStatus().ToJsonString()); return; }
            }
#endif
            if (!IsPresented || form.PanMsgBackground.Visibility == Visibility.Visible) return;
            switch (type)
            {
                case "bootstrap": SendBootstrap(core); Refresh(force: true); break;
                case "mcmod": await HandleMcmodAsync(core, message, form); break;
                case "platform": await HandlePlatformAsync(core, message, form); break;
                case "launcher" when _library is not null:
                    await _library.HandleAsync(message, form);
                    Refresh(force: true);
                    break;
                case "native": await form.NavigateHybridAsync(ReadString(message, "page")); break;
                case "servers": await HandleServersAsync(core); break;
                case "openExternal": OpenExternal(ReadString(message, "url")); break;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "浏览补充页消息处理失败", ModBase.LogLevel.Debug);
            var text = ex.Message;
            PostUi(() =>
            {
                if (_disposed || _view?.CoreWebView2 is null) return;
                core.PostWebMessageAsJson(new JsonObject { ["type"] = "error", ["message"] = text }.ToJsonString());
            });
        }
    }

    private void SendBootstrap(CoreWebView2 core) => core.PostWebMessageAsJson(new JsonObject
    {
        ["type"] = "bootstrap", ["primaryUi"] = "native", ["view"] = "browse",
        ["language"] = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
        ["version"] = Basics.VersionName, ["branch"] = Basics.VersionBranch,
        ["runtimeVersion"] = _runtimeVersion, ["bundledRuntime"] = _bundledRuntime
    }.ToJsonString());

    private async Task HandleServersAsync(CoreWebView2 core)
    {
        JsonArray items = [];
        var message = "";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.mclists.cn/");
            request.Headers.TryAddWithoutValidation("Accept", "text/html");
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(html, @"href=[""'](?<url>[^""']+)[""'][^>]*>(?<name>[^<]{2,80})</a>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var name = System.Net.WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
                var url = match.Groups["url"].Value;
                if (name.Length is < 2 or > 80 || !seen.Add(name) || url.StartsWith('#')) continue;
                if (name.Contains("登录") || name.Contains("注册") || name.Contains("首页")) continue;
                var website = Uri.TryCreate(new Uri("https://www.mclists.cn/"), url, out var absolute) ? absolute.AbsoluteUri : "https://www.mclists.cn/";
                items.Add(new JsonObject { ["id"] = "mclists-" + items.Count, ["name"] = name, ["description"] = "来自 MCLists", ["website"] = website, ["address"] = "", ["minecraft"] = "", ["vanilla"] = false });
                if (items.Count >= 60) break;
            }
            if (items.Count == 0) message = "MCLists 页面没有解析出服务器，仍可使用 MCL 目录。";
        }
        catch (Exception ex) { message = "MCLists 暂时不可用：" + ex.Message; }
        core.PostWebMessageAsJson(new JsonObject { ["type"] = "serverList", ["items"] = items, ["message"] = message }.ToJsonString());
    }
    private static string? ReadString(JsonObject message, string name) => message[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !ChromeLinkPlan.IsHttp(url)) return;
        if (!SystemChromeLink.TryOpenHttp(url)) ModBase.OpenWebsite(url);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();
    private void ReleaseView()
    {
        ++_generation;
        _ready = false;
        _lastSnapshot = "";
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
        if (_form is not null)
        {
            _form.Closed -= OnClosed;
            _modalVisibility?.RemoveValueChanged(_form.PanMsgBackground, OnModalChanged);
            if (_view is not null) _form.PanBrowse.Children.Remove(_view);
        }
        _modalVisibility = null;
        _view?.Dispose();
        _view = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mcmodLifetime.Cancel();
        _mcmodHttp.Dispose();
        _quarkListHttp.Dispose();
        _xyeHttp.Dispose();
        ReleaseView();
        _library?.Dispose();
        _library = null;
        _form = null;
    }
}
