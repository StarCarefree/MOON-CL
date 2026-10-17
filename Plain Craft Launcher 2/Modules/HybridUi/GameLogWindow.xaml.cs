using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.UI;

namespace PCL;

public class GameLogWindow : Window
{
    private const int PageSize = 400;
    private readonly Func<IReadOnlyList<string?>> _lines;
    private readonly ListBox _list;
    private readonly TextBlock _count;
    private bool _debug;
    private bool _info = true;
    private bool _warn = true;
    private bool _error = true;
    private int _shown;
    private bool _follow = true;
    private bool _rendering;
    private bool _dragging;
    private ScrollViewer? _viewer;

    public GameLogWindow(string title, Func<IReadOnlyList<string?>> lines)
    {
        _lines = lines;
        Title = title;
        Width = 860;
        Height = 560;
        MinWidth = 520;
        MinHeight = 320;
        Background = BrushOf(17, 19, 21);
        Foreground = BrushOf(236, 237, 239);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _list = new ListBox
        {
            Background = BrushOf(17, 19, 21),
            BorderThickness = new Thickness(0),
            Foreground = BrushOf(201, 206, 214),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        _list.ItemContainerStyle = RowStyle();
        _list.ItemTemplate = LineTemplate();
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll));
        _list.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _list.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
        _count = new TextBlock { Foreground = BrushOf(146, 153, 163), FontSize = 11, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Content = Build();
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => RenderLog(true), DispatcherPriority.Background);
    }

    public void Refresh() => RenderLog(reset: false);

    private UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(bar, Dock.Top);
        bar.Children.Add(Toggle("DEBUG", false, BrushOf(124, 132, 144), value => { _debug = value; RenderLog(true); }));
        bar.Children.Add(Toggle("INFO", true, BrushOf(201, 206, 214), value => { _info = value; RenderLog(true); }));
        bar.Children.Add(Toggle("WARN", true, BrushOf(255, 185, 66), value => { _warn = value; RenderLog(true); }));
        bar.Children.Add(Toggle("ERROR", true, BrushOf(255, 107, 107), value => { _error = value; RenderLog(true); }));
        bar.Children.Add(_count);
        root.Children.Add(bar);
        root.Children.Add(_list);
        return root;
    }

    private void RenderLog(bool reset)
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            if (reset) _shown = 0;
            IReadOnlyList<string?> source;
            try { source = _lines() ?? []; }
            catch (Exception) { source = []; }
            var matched = 0;
            var batch = new List<GameLogLine>(PageSize);
            var previous = GameLogKind.Info;
            foreach (var raw in source)
            {
                var kind = GameLogFilter.Classify(raw, previous);
                previous = kind;
                if (!GameLogFilter.Allows(kind, _debug, _info, _warn, _error)) continue;
                matched++;
                if (matched <= _shown) continue;
                if (batch.Count < PageSize) batch.Add(new GameLogLine(raw ?? "", kind));
            }
            if (reset) _list.Items.Clear();
            foreach (var line in batch) _list.Items.Add(line);
            _shown += batch.Count;
            _count.Text = matched == 0 ? "没有匹配的日志" : _shown < matched ? _shown + " / " + matched : matched + " 行";
            if (_follow && !_dragging && _list.Items.Count > 0)
                Dispatcher.BeginInvoke(FollowTail, DispatcherPriority.Background);
        }
        finally { _rendering = false; }
    }

    private void FollowTail()
    {
        if (!_follow || _dragging || _list.Items.Count == 0) return;
        var viewer = Viewer();
        if (viewer is null) _list.ScrollIntoView(_list.Items[^1]);
        else viewer.ScrollToVerticalOffset(viewer.ExtentHeight);
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        var viewer = Viewer();
        if (viewer is null) return;
        _follow = viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ExtentHeight - 24;
        if (_follow) RenderLog(reset: false);
    }

    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        var viewer = e.OriginalSource as ScrollViewer ?? Viewer();
        if (viewer is null) return;
        if (_dragging || viewer.IsMouseCaptureWithin) return;
        if (e.ExtentHeightChange != 0) return;
        _follow = viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ExtentHeight - 24;
        if (_follow) RenderLog(reset: false);
    }

    private ScrollViewer? Viewer()
    {
        if (_viewer is not null) return _viewer;
        _viewer = FindViewer(_list);
        return _viewer;
    }

    private static ScrollViewer? FindViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static Style RowStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 18d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = presenter;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static DataTemplate LineTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(GameLogLine.Text)));
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(GameLogLine.Kind)) { Converter = LogColor.Instance });
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
        text.SetValue(TextBlock.FontSizeProperty, 12d);
        text.SetValue(TextBlock.MarginProperty, new Thickness(0, 1, 8, 1));
        return new DataTemplate { VisualTree = text };
    }

    private static CheckBox Toggle(string label, bool on, Brush color, Action<bool> changed)
    {
        var box = new CheckBox
        {
            Content = new TextBlock { Text = label, Foreground = color, FontSize = 12 },
            IsChecked = on,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = color
        };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    private static SolidColorBrush BrushOf(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed class LogColor : System.Windows.Data.IValueConverter
    {
        public static readonly LogColor Instance = new();
        private static readonly SolidColorBrush Debug = BrushOf(124, 132, 144);
        private static readonly SolidColorBrush Info = BrushOf(201, 206, 214);
        private static readonly SolidColorBrush Warn = BrushOf(255, 185, 66);
        private static readonly SolidColorBrush Error = BrushOf(255, 107, 107);
        private static readonly SolidColorBrush Fatal = BrushOf(255, 77, 106);

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is GameLogKind kind ? kind switch
            {
                GameLogKind.Debug => Debug,
                GameLogKind.Warn => Warn,
                GameLogKind.Error => Error,
                GameLogKind.Fatal => Fatal,
                _ => Info
            } : Info;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
