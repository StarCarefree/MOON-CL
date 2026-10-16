using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PCL;

/// <summary>Presentation only: launch ownership and cancellation stay in ModLaunch.</summary>
public partial class HybridLaunchScreen : UserControl
{
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _active;
    private double _progress;
    public event EventHandler? HomeRequested;
    public event EventHandler? CancelRequested;

    public HybridLaunchScreen()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => Elapsed.Text = $"{(int)_elapsed.Elapsed.TotalMinutes:00}:{_elapsed.Elapsed.Seconds:00}";
    }

    public void BeginLaunch(string version)
    {
        _active = true;
        VersionName.Text = version;
        VersionName.ToolTip = version;
        LaunchTitle.Text = "正在启动游戏";
        StageName.Text = "初始化";
        DownloadSpeed.Text = "正在准备启动环境";
        Percentage.Text = "0%";
        Elapsed.Text = "00:00";
        _progress = 0;
        PaintProgress(false);
        _elapsed.Restart();
        Visibility = Visibility.Visible;
        UpdateMotion();
    }

    public void UpdateLaunch(string title, string stage, double progress, string? speed)
    {
        if (!_active) return;
        LaunchTitle.Text = title;
        StageName.Text = stage;
        StageName.ToolTip = stage;
        _progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        Percentage.Text = $"{_progress:P0}";
        DownloadSpeed.Text = speed is null ? "正在准备启动环境" : "下载补全文件  ·  " + speed;
        PaintProgress(true);
    }

    public void EndLaunch()
    {
        _active = false;
        _elapsed.Stop();
        Visibility = Visibility.Collapsed;
        StopMotion();
    }

    private void PaintProgress(bool animate)
    {
        if (ProgressFill.Parent is not FrameworkElement track) return;
        var target = Math.Max(0, track.ActualWidth * _progress);
        var from = ProgressFill.ActualWidth;
        ProgressFill.BeginAnimation(WidthProperty, null);
        ProgressFill.Width = target;
        if (animate && IsVisible && SystemParameters.ClientAreaAnimation)
            ProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
    }

    private void UpdateMotion()
    {
        StopMotion();
        if (!_active || !IsVisible) return;
        _clock.Start();
        if (!SystemParameters.ClientAreaAnimation) return;
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopMotion()
    {
        _clock.Stop();
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        ProgressFill.BeginAnimation(WidthProperty, null);
    }

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateMotion();
    private void ViewUnloaded(object sender, RoutedEventArgs e) => StopMotion();
    private void ProgressSizeChanged(object sender, SizeChangedEventArgs e) => PaintProgress(false);
    private void HomeClick(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);
    private void CancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
