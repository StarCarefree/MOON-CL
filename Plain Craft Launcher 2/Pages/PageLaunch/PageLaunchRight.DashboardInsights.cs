using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCL.Core.App;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.UI;

namespace PCL;

public partial class PageLaunchRight
{
    private sealed class DashboardInsightState
    {
        public DateOnly Month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        public DateOnly Selected = DateOnly.FromDateTime(DateTime.Today);
        public int Range = 1;
        public string? InstancePath;
    }

    private readonly Dictionary<string, DashboardInsightState> _dashboardInsights = new();
    private Func<IReadOnlyList<GamePlayDay>> _dashboardHistoryProvider = InstanceActivity.HistorySnapshot;
    private GameCalendarNotes? _dashboardCalendarNotes;
    private GameCalendarNotes DashboardNotes => _dashboardCalendarNotes ??= new GameCalendarNotes(Path.Combine(Paths.SharedData, "game-calendar-notes.json"));
    private DashboardInsightState DashboardInsightFor(GameDashboardWidget widget)
    {
        if (!_dashboardInsights.TryGetValue(widget.Id, out var state)) _dashboardInsights[widget.Id] = state = new();
        return state;
    }

    private static string DashboardDuration(long seconds) => seconds < 60 ? $"{seconds} 秒" : seconds < 3600
        ? $"{seconds / 60} 分钟" : $"{seconds / 3600:N0} 小时 {seconds % 3600 / 60} 分钟";

    private ComboBox DashboardHistoryFilter(DashboardInsightState state, Action changed)
    {
        var filter = new ComboBox { Style = (Style)FindResource("Dashboard.Filter"), MinHeight = 32, MaxDropDownHeight = 250, Margin = new Thickness(0, 4, 0, 12), Tag = "History.VersionFilter" };
        filter.Items.Add(new ComboBoxItem { Content = "全部已记录版本", Tag = "" });
        var options = _dashboardHistoryProvider().Select(day => (Path: day.InstancePath, Name: day.InstanceName))
            .Concat(DashboardInstances().Select(instance => (Path: instance.PathInstance, Name: instance.Name)))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).OrderBy(item => item.Name);
        foreach (var item in options) filter.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Path });
        filter.SelectedItem = filter.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals((string)item.Tag, state.InstancePath ?? "", StringComparison.OrdinalIgnoreCase)) ?? filter.Items[0];
        filter.IsEnabled = !_dashboardEditing;
        filter.SelectionChanged += (_, _) =>
        {
            state.InstancePath = (filter.SelectedItem as ComboBoxItem)?.Tag as string;
            if (state.InstancePath == "") state.InstancePath = null;
            changed();
        };
        return filter;
    }

    private FrameworkElement CreateDashboardCalendar(GameDashboardWidget widget)
    {
        var state = DashboardInsightFor(widget);
        var panel = new StackPanel();
        var body = new StackPanel();
        void Refresh()
        {
            body.Children.Clear();
            var first = state.Month;
            var end = first.AddMonths(1).AddDays(-1);
            var report = GamePlayReport.Create(_dashboardHistoryProvider(), first, end, state.InstancePath);
            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var navigation = new StackPanel { Orientation = Orientation.Horizontal };
            var previous = DashboardButton("‹", () => ChangeMonth(-1), "上个月");
            previous.Tag = "Calendar.Previous";
            previous.IsEnabled = !_dashboardEditing && first.Year > 1900;
            navigation.Children.Add(previous);
            var today = DashboardButton("今天", () =>
            {
                state.Selected = DateOnly.FromDateTime(DateTime.Today);
                state.Month = new DateOnly(state.Selected.Year, state.Selected.Month, 1);
                Refresh();
            });
            today.Tag = "Calendar.Today";
            today.IsEnabled = !_dashboardEditing;
            navigation.Children.Add(today);
            var next = DashboardButton("›", () => ChangeMonth(1), "下个月");
            next.Tag = "Calendar.Next";
            next.IsEnabled = !_dashboardEditing && first.Year < 9998;
            navigation.Children.Add(next);
            DockPanel.SetDock(navigation, Dock.Right);
            toolbar.Children.Add(navigation);
            var title = DashboardText(first.ToString("yyyy 年 M 月"), 18);
            title.FontWeight = FontWeights.SemiBold;
            title.VerticalAlignment = VerticalAlignment.Center;
            toolbar.Children.Add(title);
            body.Children.Add(toolbar);
            void ChangeMonth(int offset)
            {
                state.Month = state.Month.AddMonths(offset);
                state.Selected = state.Month;
                Refresh();
            }
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
            for (var column = 0; column < 7; column++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            var offset = ((int)first.DayOfWeek + 6) % 7;
            var days = DateTime.DaysInMonth(first.Year, first.Month);
            var weeks = (offset + days + 6) / 7;
            for (var row = 0; row <= weeks; row++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(row == 0 ? 24 : 44) });
            var labels = new[] { "一", "二", "三", "四", "五", "六", "日" };
            for (var column = 0; column < 7; column++)
            {
                var label = DashboardText(labels[column], 10, true);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(label, column);
                grid.Children.Add(label);
            }
            foreach (var day in report.Daily)
            {
                var date = day.Date;
                var isToday = date == DateOnly.FromDateTime(DateTime.Today);
                var note = DashboardNotes.Get(date);
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var number = DashboardText(date.Day.ToString(), 12);
                number.HorizontalAlignment = HorizontalAlignment.Center;
                number.FontWeight = date == state.Selected || isToday ? FontWeights.Bold : FontWeights.Normal;
                stack.Children.Add(number);
                var marker = DashboardText(note.Length > 0 ? "•" : day.Seconds > 0 || day.Sessions > 0 ? "·" : "", 10);
                marker.HorizontalAlignment = HorizontalAlignment.Center;
                marker.Foreground = DashboardBrush("Hybrid.Accent");
                marker.LineHeight = 10;
                stack.Children.Add(marker);
                var button = DashboardButton(stack, () => { state.Selected = date; Refresh(); });
                button.Tag = date;
                button.IsEnabled = !_dashboardEditing;
                button.Height = double.NaN;
                button.MinWidth = 0;
                button.Padding = new Thickness(0);
                button.Margin = new Thickness(2);
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.Background = new SolidColorBrush(day.Seconds >= 7200 ? Color.FromRgb(120, 82, 31) : day.Seconds >= 1800 ? Color.FromRgb(83, 64, 35) : day.Seconds > 0 || day.Sessions > 0 ? Color.FromRgb(53, 49, 36) : Color.FromRgb(32, 34, 38));
                button.BorderBrush = date == state.Selected ? DashboardBrush("Hybrid.Accent") : isToday ? new SolidColorBrush(Color.FromRgb(140, 112, 64)) : Brushes.Transparent;
                button.ToolTip = $"{date:yyyy-MM-dd} · {DashboardDuration(day.Seconds)} · {day.Sessions} 次会话" + (note.Length > 0 ? "\n" + note : "");
                var position = offset + date.Day - 1;
                Grid.SetRow(button, position / 7 + 1);
                Grid.SetColumn(button, position % 7);
                grid.Children.Add(button);
            }
            body.Children.Add(grid);
            body.Children.Add(DashboardText($"本月 {DashboardDuration(report.Seconds)} · {report.ActiveDays} 天活跃 · {report.Sessions} 次会话", 11, true));
            body.Children.Add(DashboardText("色深代表时长 · 圆点代表日期便签", 10, true));
            var selectedReport = GamePlayReport.Create(report.Records, state.Selected, state.Selected, state.InstancePath);
            var detail = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var selectedTitle = DashboardText($"{state.Selected:MM.dd}  {DashboardDuration(selectedReport.Seconds)}", 15);
            selectedTitle.FontWeight = FontWeights.SemiBold;
            detail.Children.Add(selectedTitle);
            foreach (var version in selectedReport.Versions.Take(5))
                detail.Children.Add(DashboardText($"{version.Name} · {DashboardDuration(version.Seconds)} · {version.Sessions} 次会话", 11, true));
            if (selectedReport.ActiveDays == 0) detail.Children.Add(DashboardText("当天暂无已记录的游玩。", 11, true));
            var selectedNote = DashboardNotes.Get(state.Selected);
            if (selectedNote.Length > 0)
            {
                var preview = DashboardText(selectedNote, 12);
                preview.MaxHeight = 96;
                preview.TextTrimming = TextTrimming.CharacterEllipsis;
                preview.Margin = new Thickness(0, 6, 0, 0);
                detail.Children.Add(preview);
            }
            var editNote = DashboardButton(selectedNote.Length > 0 ? "编辑日期便签" : "＋ 日期便签", () => EditDashboardDateNote(state.Selected, Refresh));
            editNote.Tag = "Calendar.Note";
            editNote.HorizontalAlignment = HorizontalAlignment.Left;
            editNote.IsEnabled = !_dashboardEditing && DashboardNotes.LastError is null;
            detail.Children.Add(editNote);
            if (DashboardNotes.LastError is not null) detail.Children.Add(DashboardText("便签读取失败，原文件已保留：" + DashboardNotes.LastError, 10, true));
            body.Children.Add(detail);
            AddDashboardHistoryNotice(body);
        }
        panel.Children.Add(DashboardHistoryFilter(state, Refresh));
        panel.Children.Add(body);
        panel.Tag = (Action)Refresh;
        Refresh();
        return panel;
    }

    private void EditDashboardDateNote(DateOnly date, Action refreshed)
    {
        var dialog = CreateDashboardDialog(date.ToString("yyyy.MM.dd") + " 日期便签", "可记录坐标、联机约定或当天的冒险；留空保存可删除");
        var panel = new StackPanel();
        var input = DashboardInput(DashboardNotes.Get(date), 2000);
        input.AcceptsReturn = true;
        input.TextWrapping = TextWrapping.Wrap;
        input.Height = 220;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        panel.Children.Add(input);
        var error = DashboardText("", 12, true);
        panel.Children.Add(error);
        panel.Children.Add(DashboardButton("保存日期便签", () =>
        {
            try { DashboardNotes.Set(date, input.Text); dialog.DialogResult = true; }
            catch (Exception ex) { error.Text = "保存失败，原便签未更改：" + ex.Message; }
        }));
        SetDashboardDialogBody(dialog, panel);
        if (dialog.ShowDialog() == true) refreshed();
    }

    private FrameworkElement CreateDashboardStats(GameDashboardWidget widget)
    {
        var state = DashboardInsightFor(widget);
        var panel = new StackPanel();
        var body = new StackPanel();
        var ranges = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        string[] names = ["今日", "近7天", "近30天", "本月", "全部"];
        void Refresh()
        {
            foreach (var button in ranges.Children.OfType<Button>()) button.Foreground = DashboardBrush((int)button.Tag == state.Range ? "Hybrid.Accent" : "Hybrid.Muted");
            body.Children.Clear();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var history = _dashboardHistoryProvider();
            var start = state.Range switch
            {
                0 => today, 1 => today.AddDays(-6), 2 => today.AddDays(-29),
                3 => new DateOnly(today.Year, today.Month, 1),
                _ => history.Where(day => day.Date <= today && (state.InstancePath is null || string.Equals(day.InstancePath, state.InstancePath, StringComparison.OrdinalIgnoreCase)))
                    .Select(day => day.Date).DefaultIfEmpty(today).Min()
            };
            var report = GamePlayReport.Create(history, start, today, state.InstancePath);
            var duration = DashboardText(DashboardDuration(report.Seconds), 27);
            duration.FontWeight = FontWeights.SemiBold;
            duration.Foreground = DashboardBrush("Hybrid.Accent");
            duration.Margin = new Thickness(0, 4, 0, 6);
            body.Children.Add(duration);
            body.Children.Add(DashboardText($"{start:yyyy.MM.dd} — {today:yyyy.MM.dd}", 10, true));
            var summary = DashboardText($"{report.Sessions} 次会话  ·  {report.ActiveDays} 天活跃  ·  {report.Versions.Count} 个版本\n活跃日均 {DashboardDuration(report.AverageActiveDaySeconds)}  ·  最长连续 {report.LongestStreak} 天", 12);
            summary.Margin = new Thickness(0, 8, 0, 12);
            body.Children.Add(summary);
            var chartDays = report.Daily.TakeLast(30).ToArray();
            body.Children.Add(DashboardText(report.Daily.Count > 30 ? "每日趋势 · 最近30天" : "每日趋势 · 点击柱条查看当天", 11, true));
            var chart = new Grid { Height = 82, Margin = new Thickness(0, 10, 0, 4) };
            var max = Math.Max(1, chartDays.Max(day => day.Seconds));
            for (var index = 0; index < chartDays.Length; index++)
            {
                var day = chartDays[index];
                chart.ColumnDefinitions.Add(new ColumnDefinition());
                var bar = DashboardButton("", () => OpenDashboardDay(day.Date, state.InstancePath));
                bar.Height = Math.Max(4, 76.0 * day.Seconds / max);
                bar.MinWidth = 0;
                bar.Padding = new Thickness(0);
                bar.Margin = new Thickness(1, 0, 1, 0);
                bar.VerticalAlignment = VerticalAlignment.Bottom;
                bar.Background = day.Seconds > 0 ? DashboardBrush("Hybrid.Accent") : DashboardBrush("Hybrid.Line");
                bar.BorderBrush = Brushes.Transparent;
                bar.Tag = day.Date;
                bar.IsEnabled = !_dashboardEditing;
                bar.ToolTip = $"{day.Date:MM.dd} · {DashboardDuration(day.Seconds)} · {day.Sessions} 次会话";
                Grid.SetColumn(bar, index);
                chart.Children.Add(bar);
            }
            body.Children.Add(chart);
            var axis = new DockPanel();
            var last = DashboardText(today.ToString("MM.dd"), 10, true);
            last.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(last, Dock.Right);
            axis.Children.Add(last);
            axis.Children.Add(DashboardText(chartDays[0].Date.ToString("MM.dd"), 10, true));
            body.Children.Add(axis);
            var ranking = DashboardText("版本排行", 12);
            ranking.FontWeight = FontWeights.SemiBold;
            ranking.Margin = new Thickness(0, 16, 0, 6);
            body.Children.Add(ranking);
            foreach (var version in report.Versions.Take(5))
            {
                body.Children.Add(DashboardText($"{version.Name} · {DashboardDuration(version.Seconds)}", 12));
                body.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, report.Seconds), Value = version.Seconds, Height = 3, Margin = new Thickness(0, 4, 0, 9), Foreground = DashboardBrush("Hybrid.Accent"), Background = DashboardBrush("Hybrid.Line"), BorderThickness = new Thickness(0) });
            }
            if (report.ActiveDays == 0) body.Children.Add(DashboardText("此范围内暂无记录。游玩后会自动积累，不会把旧累计时长填入图表。", 12, true));
            var export = DashboardButton("导出当前筛选 CSV  ↗", () => ExportDashboardReport(report));
            export.Tag = "Stats.Export";
            export.IsEnabled = !_dashboardEditing && report.Records.Count > 0;
            export.HorizontalAlignment = HorizontalAlignment.Left;
            export.Margin = new Thickness(0, 8, 0, 8);
            body.Children.Add(export);
            var instances = DashboardInstances().Where(instance => state.InstancePath is null || string.Equals(instance.PathInstance, state.InstancePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            var lifetime = instances.Sum(instance => (long)InstanceActivity.PlayedSeconds(instance.PathInstance));
            var launches = instances.Sum(instance => (long)Math.Max(0, States.Instance.LaunchCount[instance.PathInstance]));
            body.Children.Add(DashboardText($"当前游戏库累计（含旧记录）：{DashboardDuration(lifetime)} · {launches} 次启动", 10, true));
            body.Children.Add(DashboardText("会话数按同一版本的一段连续运行计，多开不重复计时；跨日会话只记在开始日。", 10, true));
            AddDashboardHistoryNotice(body);
        }
        for (var index = 0; index < names.Length; index++)
        {
            var range = index;
            var button = DashboardButton(names[index], () => { state.Range = range; Refresh(); });
            button.Tag = index;
            button.IsEnabled = !_dashboardEditing;
            button.Padding = new Thickness(9, 0, 9, 0);
            ranges.Children.Add(button);
        }
        panel.Children.Add(ranges);
        panel.Children.Add(DashboardHistoryFilter(state, Refresh));
        panel.Children.Add(body);
        panel.Tag = (Action)Refresh;
        Refresh();
        return panel;
    }

    private void OpenDashboardDay(DateOnly date, string? instancePath)
    {
        var widget = _dashboard?.Widgets.FirstOrDefault(item => item.Kind == "calendar");
        if (widget is not null && _dashboardContents.TryGetValue(widget.Id, out var content))
        {
            var state = DashboardInsightFor(widget);
            state.Selected = date;
            state.Month = new DateOnly(date.Year, date.Month, 1);
            state.InstancePath = instancePath;
            content.Content = CreateDashboardCalendar(widget);
            content.BringIntoView();
            return;
        }
        var report = GamePlayReport.Create(_dashboardHistoryProvider(), date, date, instancePath);
        var dialog = CreateDashboardDialog(date.ToString("yyyy.MM.dd") + " 游玩记录", "本地每日记录 · 不含历史未记录时长");
        var panel = new StackPanel();
        panel.Children.Add(DashboardText(DashboardDuration(report.Seconds), 24));
        foreach (var version in report.Versions) panel.Children.Add(DashboardText($"{version.Name} · {DashboardDuration(version.Seconds)} · {version.Sessions} 次会话", 13));
        if (report.ActiveDays == 0) panel.Children.Add(DashboardText("当天暂无记录。", 12, true));
        SetDashboardDialogBody(dialog, panel);
        dialog.ShowDialog();
    }

    private void AddDashboardHistoryNotice(Panel panel)
    {
        var message = DashboardText($"每日记录始于 {InstanceActivity.HistoryTrackingSince.LocalDateTime:yyyy.MM.dd}；保留最近三年。旧累计时长无法还原每日分布。", 10, true);
        message.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(message);
        if (InstanceActivity.HistoryError is { } error) panel.Children.Add(DashboardText("游玩记录暂未能保存，将自动重试：" + error, 10, true));
    }

    private void ExportDashboardReport(GamePlayReport report)
    {
        var picker = new SaveFileDialog { Title = "导出游玩记录", Filter = "CSV 表格 (*.csv)|*.csv", FileName = $"游玩记录-{report.Start:yyyyMMdd}-{report.End:yyyyMMdd}.csv", AddExtension = true };
        var owner = Window.GetWindow(this);
        if ((owner is null ? picker.ShowDialog() : picker.ShowDialog(owner)) != true) return;
        try { File.WriteAllText(picker.FileName, report.ToCsv(), new UTF8Encoding(true)); HintService.Hint("游玩记录已导出"); }
        catch (Exception ex) { ModMain.MyMsgBox("导出失败：" + ex.Message, "游玩统计"); }
    }
}
