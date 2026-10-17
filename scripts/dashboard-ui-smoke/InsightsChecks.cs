using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCL;
using PCL.Core.App;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.UI;

internal static partial class Program
{
    private static string InsightText(DependencyObject root) => string.Join("\n", Tree(root).OfType<TextBlock>().Select(text => text.Text));
    private static Button InsightButton(DependencyObject root, string tag) => Tree(root).OfType<Button>().Single(button => button.Tag as string == tag);
    private static void Tap(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

    private static void CheckDashboardInsights(PageLaunchRight page)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        GamePlayDay[] sample =
        [
            new(today, "D:/fixture/A", "生存", 3600, 1),
            new(today, "D:/fixture/B", "创造", 1800, 1),
            new(today.AddDays(-1), "D:/fixture/A", "生存", 900, 1),
            new(today.AddDays(-15), "D:/fixture/B", "创造", 7200, 1)
        ];
        Set(page, "_dashboardHistoryProvider", (Func<IReadOnlyList<GamePlayDay>>)(() => sample));
        var calendar = new GameDashboardWidget { Kind = "calendar" };
        var stats = new GameDashboardWidget { Kind = "stats" };
        var layout = new GameDashboardLayout
        {
            Widgets = [calendar, stats, new() { Kind = "favorites" }, new() { Kind = "worlds" }, new() { Kind = "servers" }, new() { Kind = "actions" }, new() { Kind = "folders" }]
        };
        Set(page, "_dashboard", layout);
        Call(page, "RenderDashboard");
        var contents = Field<Dictionary<string, ContentControl>>(page, "_dashboardContents");
        var cal = (FrameworkElement)contents[calendar.Id].Content;
        var stat = (FrameworkElement)contents[stats.Id].Content;
        Render(cal, 380, 850);
        Render(stat, 380, 850);
        Check(Tree(cal).OfType<Button>().Count(button => button.Tag is DateOnly) == DateTime.DaysInMonth(today.Year, today.Month), "Calendar must render all days in month");
        Check(InsightText(cal).Contains("1 小时 30 分钟"), "Calendar selected-day total must include both versions");
        Check(InsightText(stat).Contains("1 小时 45 分钟"), "7-day total must exclude old activity");
        Check(Tree(stat).OfType<Button>().Count(button => button.Tag is DateOnly) == 7, "7-day trend must include empty days");
        Check(InsightButton(stat, "Stats.Export").IsEnabled, "Export must be available for recorded data");
        var calendarState = Call(page, "DashboardInsightFor", calendar)!;
        var monthField = calendarState.GetType().GetField("Month")!;
        var selectedField = calendarState.GetType().GetField("Selected")!;
        monthField.SetValue(calendarState, new DateOnly(2026, 1, 1));
        ((Action)cal.Tag)();
        Tap(InsightButton(cal, "Calendar.Previous"));
        Check((DateOnly)monthField.GetValue(calendarState)! == new DateOnly(2025, 12, 1), "Previous month must cross year boundary");
        ((Action)cal.Tag)();
        Check((DateOnly)monthField.GetValue(calendarState)! == new DateOnly(2025, 12, 1), "Refresh must retain browsed month");
        Tap(InsightButton(cal, "Calendar.Next"));
        Check((DateOnly)monthField.GetValue(calendarState)! == new DateOnly(2026, 1, 1), "Next month must cross year boundary");
        Tap(InsightButton(cal, "Calendar.Today"));
        Check((DateOnly)selectedField.GetValue(calendarState)! == today, "Today shortcut must reset selected date");
        var yesterday = Tree(cal).OfType<Button>().FirstOrDefault(button => button.Tag is DateOnly date && date == today.AddDays(-1));
        if (yesterday is not null)
        {
            Tap(yesterday);
            Check((DateOnly)selectedField.GetValue(calendarState)! == today.AddDays(-1), "Day click must select exact date");
        }
        var changed = false;
        DialogTest(page, "EditDashboardDateNote", dialog =>
        {
            Tree(dialog).OfType<TextBox>().Single().Text = "明晚联机 · 基地 120 64 -250";
            Button(dialog, "保存日期便签").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }, today, (Action)(() => changed = true));
        Check(changed, "Saving date note must refresh calendar");
        Check(new GameCalendarNotes(Path.Combine(Paths.SharedData, "game-calendar-notes.json")).Get(today).Contains("明晚联机"), "Date note must survive reopening");
        Tap(InsightButton(cal, "Calendar.Today"));
        Check(InsightText(cal).Contains("明晚联机"), "Saved note must display for selected day");
        Render(cal, 380, 820, "calendar-expanded.png");
        Tap(Tree(stat).OfType<Button>().Single(button => button.Tag is int range && range == 2));
        Check(InsightText(stat).Contains("3 小时 45 分钟"), "30-day range must include older activity");
        var filter = Tree(stat).OfType<ComboBox>().Single();
        filter.SelectedItem = filter.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == "D:/fixture/B");
        Check(InsightText(stat).Contains("2 小时 30 分钟"), "Version filter must narrow totals");
        Render(stat, 380, 820, "stats-expanded.png");
        var bar = Tree(stat).OfType<Button>().Single(button => button.Tag is DateOnly date && date == today);
        Tap(bar);
        cal = (FrameworkElement)contents[calendar.Id].Content;
        Check((DateOnly)selectedField.GetValue(calendarState)! == today, "Trend bar must open exact calendar date");
        var calFilter = Tree(cal).OfType<ComboBox>().Single();
        Check((calFilter.SelectedItem as ComboBoxItem)?.Tag as string == "D:/fixture/B", "Trend drill-down must retain version filter");
        Check(InsightText(cal).Contains("30 分钟"), "Calendar must display filtered day total");
        foreach (var kind in new[] { "favorites", "worlds", "servers", "actions", "folders" })
        {
            var widget = layout.Widgets.Single(item => item.Kind == kind);
            var view = (FrameworkElement)contents[widget.Id].Content;
            Render(view, 340, 220, kind + "-empty.png");
            Check(view.ActualWidth > 0 && InsightText(view).Length > 0, kind + " must render an empty/useful state");
        }
        var folderView = (FrameworkElement)contents[layout.Widgets.Single(widget => widget.Kind == "folders").Id].Content;
        Check(Tree(folderView).OfType<Button>().All(button => !button.IsEnabled), "Folder actions must be disabled without selected version");
        Call(page, "RenderDashboard");
        var rebuilt = (FrameworkElement)Field<Dictionary<string, ContentControl>>(page, "_dashboardContents")[stats.Id].Content;
        Check((Tree(rebuilt).OfType<ComboBox>().Single().SelectedItem as ComboBoxItem)?.Tag as string == "D:/fixture/B", "Full dashboard rebuild must retain stats filter");
        Render(page, 900, 1000, "expanded-dashboard.png");
        Render(page, 480, 1000, "expanded-dashboard-narrow.png");
        Click(page, "DashboardEdit_Click");
        var editContents = Field<Dictionary<string, ContentControl>>(page, "_dashboardContents");
        Check(Tree((FrameworkElement)editContents[calendar.Id].Content).OfType<Button>().All(button => !button.IsEnabled), "Calendar content actions must be disabled while arranging");
        Click(page, "DashboardCancel_Click");
        Console.WriteLine("PASS expanded calendar / date notes / statistics filters / new widgets");
    }
}
