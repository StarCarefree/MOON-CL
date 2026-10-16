using System;
using System.Windows;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

/// <summary>
/// Non-modal, ownerless launch presentation. Closing it never cancels a launch or shuts down the application.
/// Create a new instance to reopen after Closed; BeginLaunch shows the window without ShowDialog.
/// </summary>
public partial class HybridLaunchWindow : Window
{
    private bool _closed;
    public event EventHandler? CancelRequested;

    public HybridLaunchWindow()
    {
        InitializeComponent();
        Screen.HomeRequested += ScreenHomeRequested;
        Screen.CancelRequested += ScreenCancelRequested;
    }

    public void BeginLaunch(string instanceName)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => BeginLaunch(instanceName));
            return;
        }
        if (_closed) throw new InvalidOperationException("启动窗口已关闭，请创建新的 HybridLaunchWindow 实例。");
        Screen.BeginLaunch(instanceName);
        // Do not assign Owner: minimizing the launcher must not minimize this window.
        if (!IsVisible) Show();
        Screen.SetPresentationSuspended(WindowState == WindowState.Minimized);
    }

    public void ApplyProgress(LaunchProgressSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyProgress(snapshot)));
            return;
        }
        if (!_closed) Screen.ApplyProgress(snapshot);
    }

    private void ScreenHomeRequested(object? sender, EventArgs e) => Close();
    private void ScreenCancelRequested(object? sender, EventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowStateChanged(object? sender, EventArgs e) =>
        Screen?.SetPresentationSuspended(WindowState == WindowState.Minimized);

    private void WindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        Screen.HomeRequested -= ScreenHomeRequested;
        Screen.CancelRequested -= ScreenCancelRequested;
        Screen.EndLaunch();
    }
}
