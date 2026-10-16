using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.UI;

namespace PCL;

public sealed class RunningInstanceCard : Border
{
    private readonly int _processId;
    private readonly Func<nint> _window;
    private readonly Func<IReadOnlyList<string?>> _lines;
    private readonly TextBlock _cpu;
    private readonly TextBlock _memory;
    private readonly TextBlock _network;
    private readonly DispatcherTimer _timer;
    private ProcessUsage.Counters _previous;
    private DateTime _previousAt;
    private bool _hasPrevious;
    private GameLogWindow? _log;

    public RunningInstanceCard(string name, int processId, Func<nint> window, Func<IReadOnlyList<string?>> lines)
    {
        _processId = processId;
        _window = window;
        _lines = lines;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 33));
        BorderBrush = new SolidColorBrush(Color.FromRgb(48, 50, 55));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Margin = new Thickness(0, 0, 0, 12);
        Padding = new Thickness(16, 14, 12, 14);
        Child = Build(name, out _cpu, out _memory, out _network);
        Cursor = Cursors.Hand;
        MouseLeftButtonUp += (_, e) =>
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
            FocusGame();
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _timer.Tick += (_, _) => Sample();
        Loaded += (_, _) => { Sample(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public int ProcessId => _processId;

    private Grid Build(string name, out TextBlock cpu, out TextBlock memory, out TextBlock network)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = new SolidColorBrush(Color.FromRgb(236, 237, 239)),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        });
        var stats = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        cpu = Stat("CPU");
        memory = Stat("内存");
        network = Stat("网络");
        stats.Children.Add(cpu);
        stats.Children.Add(memory);
        stats.Children.Add(network);
        body.Children.Add(stats);
        var log = QuietButton("查看日志");
        log.Margin = new Thickness(0, 10, 0, 0);
        log.HorizontalAlignment = HorizontalAlignment.Left;
        log.Click += (_, _) => OpenLog();
        body.Children.Add(log);
        var close = QuietButton("×");
        close.Width = 28;
        close.Height = 28;
        close.FontSize = 16;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Click += (_, _) => CloseGame();
        Grid.SetColumn(close, 1);
        grid.Children.Add(body);
        grid.Children.Add(close);
        return grid;
    }

    private static Button QuietButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(10, 4, 10, 4),
        Background = new SolidColorBrush(Color.FromRgb(36, 38, 41)),
        Foreground = new SolidColorBrush(Color.FromRgb(255, 185, 66)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(58, 61, 66)),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand
    };

    private static TextBlock Stat(string label) => new()
    {
        Text = label + " —",
        Foreground = new SolidColorBrush(Color.FromRgb(182, 187, 193)),
        FontSize = 12,
        Margin = new Thickness(0, 0, 18, 0)
    };

    private void Sample()
    {
        try
        {
            using var process = Process.GetProcessById(_processId);
            if (process.HasExited) { Remove(); return; }
            process.Refresh();
            var received = 0L;
            var sent = 0L;
            var current = new ProcessUsage.Counters(process.TotalProcessorTime, process.WorkingSet64, received, sent);
            var now = DateTime.UtcNow;
            if (_hasPrevious)
            {
                var sample = ProcessUsage.Measure(_previous, current, now - _previousAt, Environment.ProcessorCount);
                _cpu.Text = "CPU " + sample.CpuText;
                _memory.Text = "内存 " + sample.MemoryText;
                _network.Text = "网络 ↓" + sample.ReceiveText + " ↑" + sample.SendText;
            }
            _previous = current;
            _previousAt = now;
            _hasPrevious = true;
            _log?.Refresh();
        }
        catch (Exception)
        {
            Remove();
        }
    }

    private void FocusGame()
    {
        try
        {
            var handle = _window();
            if (handle == 0) return;
            ModMain.ShowWindowToTop(handle);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "置顶游戏窗口失败", ModBase.LogLevel.Debug);
        }
    }

    private void CloseGame()
    {
        try
        {
            using var process = Process.GetProcessById(_processId);
            if (!process.HasExited) process.CloseMainWindow();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "关闭游戏窗口失败", ModBase.LogLevel.Debug);
        }
    }

    private void OpenLog()
    {
        if (_log is { IsVisible: true })
        {
            _log.Activate();
            _log.Refresh();
            return;
        }
        _log = new GameLogWindow("游戏日志", _lines) { Owner = Window.GetWindow(this) };
        _log.Show();
    }

    private void Remove()
    {
        _timer.Stop();
        if (Parent is Panel panel) panel.Children.Remove(this);
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
}
