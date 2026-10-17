using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.UI;

public sealed record GamePlayDailyTotal(DateOnly Date, long Seconds, int Sessions);
public sealed record GamePlayVersionTotal(string Path, string Name, long Seconds, int Sessions);

/// <summary>Inclusive local-date report. Never manufactures daily history from legacy lifetime totals.</summary>
public sealed class GamePlayReport
{
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    public required IReadOnlyList<GamePlayDay> Records { get; init; }
    public required IReadOnlyList<GamePlayDailyTotal> Daily { get; init; }
    public required IReadOnlyList<GamePlayVersionTotal> Versions { get; init; }
    public long Seconds => Daily.Sum(day => day.Seconds);
    public int Sessions => Daily.Sum(day => day.Sessions);
    public int ActiveDays => Daily.Count(day => day.Seconds > 0 || day.Sessions > 0);
    public long AverageActiveDaySeconds => ActiveDays == 0 ? 0 : Seconds / ActiveDays;
    public int LongestStreak
    {
        get
        {
            var longest = 0;
            var current = 0;
            foreach (var day in Daily)
            {
                current = day.Seconds > 0 || day.Sessions > 0 ? current + 1 : 0;
                longest = Math.Max(longest, current);
            }
            return longest;
        }
    }

    public static GamePlayReport Create(IEnumerable<GamePlayDay> history, DateOnly start, DateOnly end, string? instancePath = null)
    {
        if (end < start || end.DayNumber - start.DayNumber > 3660) throw new ArgumentOutOfRangeException(nameof(end));
        var records = history.Where(day => day.Date >= start && day.Date <= end &&
            (instancePath is null || string.Equals(day.InstancePath, instancePath, StringComparison.OrdinalIgnoreCase)))
            .Where(day => day.Seconds >= 0 && day.Sessions >= 0)
            .OrderBy(day => day.Date).ThenBy(day => day.InstancePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var totals = records.GroupBy(day => day.Date).ToDictionary(group => group.Key,
            group => new GamePlayDailyTotal(group.Key, group.Sum(day => day.Seconds), group.Sum(day => day.Sessions)));
        var daily = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1)
            .Select(offset => start.AddDays(offset)).Select(date => totals.GetValueOrDefault(date) ?? new GamePlayDailyTotal(date, 0, 0)).ToArray();
        var versions = records.GroupBy(day => day.InstancePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GamePlayVersionTotal(group.Key, group.Last().InstanceName, group.Sum(day => day.Seconds), group.Sum(day => day.Sessions)))
            .OrderByDescending(version => version.Seconds).ThenByDescending(version => version.Sessions).ThenBy(version => version.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new GamePlayReport { Start = start, End = end, Records = records, Daily = daily, Versions = versions };
    }

    /// <summary>One row per recorded day/version, with spreadsheet-formula-safe text fields.</summary>
    public string ToCsv()
    {
        var csv = new StringBuilder("日期,版本,版本路径,游玩秒数,游玩分钟,启动会话数\r\n");
        foreach (var row in Records)
            csv.Append(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(Cell(row.InstanceName)).Append(',').Append(Cell(row.InstancePath)).Append(',')
                .Append(row.Seconds.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((row.Seconds / 60.0).ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Sessions.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        return csv.ToString();
    }

    private static string Cell(string value)
    {
        var text = value ?? "";
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0 && ("=+-@".Contains(trimmed[0]) || text[0] is '\t' or '\r' or '\n')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
