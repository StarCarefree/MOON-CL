using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>One running Minecraft instance. Duplicate processes of the same path count as one.</summary>
public sealed record GamePlayInstance(string Path, string Name);

/// <summary>Play time aggregated for one local calendar day and one instance path.</summary>
public sealed record GamePlayDay(DateOnly Date, string InstancePath, string InstanceName, long Seconds, int Sessions);

/// <summary>
/// Real daily play history. Time is counted only while instances are reported live;
/// gaps, shutdowns and restarts never invent offline play. Not a migration of the old cumulative counter.
/// </summary>
public sealed class GamePlayHistory
{
    private const int Schema = 1;
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxNameLength = 256;
    private const int MaxPathLength = 1024;
    private const long MaxSecondsPerDay = 48L * 60 * 60;
    private const int RetentionYears = 3;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeZoneInfo _zone;
    private readonly Func<DateTimeOffset>? _clock;
    private readonly Action<string, string>? _save;
    private readonly Dictionary<string, Live> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bucket> _days = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastSaveAttempt;
    private bool _dirty;
    private string? _lastError;
    private string? _loggedError;

    public GamePlayHistory(string path) : this(path, TimeZoneInfo.Local, clock: null, save: null) { }

    /// <param name="zone">Local calendar used to split midnight and DST. Tests inject a fixed zone.</param>
    /// <param name="clock">Optional clock. When set, every public method uses it and ignores the passed timestamp.</param>
    /// <param name="save">Optional writer. When set, replaces atomic file I/O so tests can fail a save.</param>
    public GamePlayHistory(string path, TimeZoneInfo zone, Func<DateTimeOffset>? clock = null, Action<string, string>? save = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _clock = clock;
        _save = save;
        Load();
    }

    /// <summary>When daily tracking began. Existing files keep their stored start; a missing file starts at construction and is not written until play happens.</summary>
    public DateTimeOffset TrackingSince { get; private set; }

    public string? LastError { get { lock (_gate) return _lastError; } }

    /// <summary>Fold still-running instances into daily buckets. First sighting records one session even at zero seconds. Stopped instances get a tail. Does not throw.</summary>
    public void Update(IEnumerable<GamePlayInstance> live, DateTimeOffset now)
    {
        try
        {
            now = Now(now);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                if (live is not null)
                {
                    foreach (var item in live)
                    {
                        if (!TryNormalize(item, out var path, out var name)) continue;
                        if (!seen.Add(path)) continue;
                        if (_live.TryGetValue(path, out var current))
                        {
                            current.Name = name;
                            Accrue(current, now);
                        }
                        else
                        {
                            var started = new Live(path, name, now);
                            _live[path] = started;
                            Touch(started, now, seconds: 0, session: true);
                            _dirty = true;
                            TrySave(now, force: true);
                        }
                    }
                }
                foreach (var path in Stopped(seen))
                {
                    var current = _live[path];
                    Accrue(current, now);
                    _live.Remove(path);
                    _dirty = true;
                    TrySave(now, force: true);
                }
                if (_dirty) TrySave(now, force: false);
            }
        }
        catch (Exception ex) { Remember(ex); }
    }

    /// <summary>Commit accrued seconds for still-live instances without ending them or adding sessions. Does not throw.</summary>
    public void Flush(DateTimeOffset now)
    {
        try
        {
            now = Now(now);
            lock (_gate)
            {
                foreach (var current in _live.Values) Accrue(current, now);
                TrySave(now, force: true);
            }
        }
        catch (Exception ex) { Remember(ex); }
    }

    /// <summary>Committed days plus uncommitted live seconds. Does not mutate state or touch the disk. Does not throw.</summary>
    public IReadOnlyList<GamePlayDay> Snapshot(DateTimeOffset now)
    {
        try
        {
            now = Now(now);
            lock (_gate)
            {
                var merged = new Dictionary<string, Bucket>(StringComparer.Ordinal);
                foreach (var pair in _days) merged[pair.Key] = pair.Value.Copy();
                foreach (var current in _live.Values) Project(current, now, merged);
                var list = new List<GamePlayDay>(merged.Count);
                var cutoff = LocalDate(now).AddYears(-RetentionYears);
                foreach (var bucket in merged.Values)
                    if (bucket.Date >= cutoff)
                        list.Add(new GamePlayDay(bucket.Date, bucket.Path, bucket.Name, bucket.Seconds, bucket.Sessions));
                list.Sort(static (a, b) =>
                {
                    var date = a.Date.CompareTo(b.Date);
                    return date != 0 ? date : string.Compare(a.InstancePath, b.InstancePath, StringComparison.OrdinalIgnoreCase);
                });
                return list;
            }
        }
        catch (Exception ex)
        {
            Remember(ex);
            return [];
        }
    }

    private DateTimeOffset Now(DateTimeOffset supplied) => _clock?.Invoke() ?? supplied;

    private void Accrue(Live current, DateTimeOffset now)
    {
        var before = current.Seconds;
        var beforeDay = current.AccruedUntil;
        Touch(current, now, Pending(current, now), session: false);
        if (current.Seconds != before || current.AccruedUntil != beforeDay) _dirty = true;
    }

    private long Pending(Live current, DateTimeOffset now)
    {
        var elapsed = (long)Math.Floor((now - current.AccruedUntil).TotalSeconds);
        return elapsed < 0 ? 0 : elapsed;
    }

    private void Touch(Live current, DateTimeOffset now, long seconds, bool session)
    {
        if (seconds < 0) seconds = 0;
        // Advance by whole accounted seconds, retaining the fractional remainder across frequent ticks.
        var end = current.AccruedUntil.AddSeconds(seconds);
        foreach (var (day, chunk) in Split(current.AccruedUntil, end, seconds))
            Add(day, current.Path, current.Name, chunk, session && day == LocalDate(current.Started) ? 1 : 0);
        current.AccruedUntil = end;
        current.Seconds += seconds;
    }

    private void Project(Live current, DateTimeOffset now, Dictionary<string, Bucket> merged)
    {
        var end = now < current.AccruedUntil ? current.AccruedUntil : now;
        foreach (var (day, chunk) in Split(current.AccruedUntil, end, Pending(current, now)))
        {
            var key = Key(day, current.Path);
            if (!merged.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket(day, current.Path, current.Name, 0, 0);
                merged[key] = bucket;
            }
            bucket.Seconds = Clamp(bucket.Seconds + chunk);
            bucket.Name = current.Name;
        }
    }

    private List<(DateOnly Day, long Seconds)> Split(DateTimeOffset from, DateTimeOffset to, long seconds)
    {
        var result = new List<(DateOnly, long)>();
        if (to < from) to = from;
        if (seconds < 0) seconds = 0;
        var startDay = LocalDate(from);
        var endDay = LocalDate(to);
        if (seconds == 0 || startDay == endDay)
        {
            result.Add((startDay, seconds));
            return result;
        }
        var remaining = seconds;
        for (var day = startDay; day <= endDay; day = day.AddDays(1))
        {
            long chunk;
            if (day == endDay) chunk = remaining;
            else
            {
                var origin = day == startDay ? from : Midnight(day);
                var width = (long)Math.Floor((Midnight(day.AddDays(1)) - origin).TotalSeconds);
                chunk = Math.Min(remaining, Math.Max(0, width));
            }
            result.Add((day, chunk));
            remaining -= chunk;
        }
        return result;
    }

    private void Add(DateOnly day, string path, string name, long seconds, int sessions)
    {
        if (!Keep(day)) return;
        var key = Key(day, path);
        if (!_days.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket(day, path, name, 0, 0);
            _days[key] = bucket;
        }
        bucket.Seconds = Clamp(bucket.Seconds + Math.Max(0, seconds));
        bucket.Sessions = ClampCount(bucket.Sessions + Math.Max(0, sessions));
        bucket.Name = name;
    }

    private bool Keep(DateOnly day)
    {
        var today = LocalDate(Now(DateTimeOffset.UtcNow));
        return day >= today.AddYears(-RetentionYears) && day <= today.AddYears(1);
    }

    private List<string> Stopped(HashSet<string> seen)
    {
        var stopped = new List<string>();
        foreach (var path in _live.Keys)
            if (!seen.Contains(path)) stopped.Add(path);
        return stopped;
    }

    private void TrySave(DateTimeOffset now, bool force)
    {
        if (!_dirty) return;
        if (!force && _lastSaveAttempt is { } previous && now - previous < SaveInterval) return;
        _lastSaveAttempt = now;
        try
        {
            var cutoff = LocalDate(now).AddYears(-RetentionYears);
            var expired = new List<string>();
            foreach (var pair in _days)
                if (pair.Value.Date < cutoff) expired.Add(pair.Key);
            foreach (var key in expired) _days.Remove(key);
            Write(Build());
            _dirty = false;
            _lastError = null;
        }
        catch (Exception ex) { Remember(ex); }
    }

    private string Build()
    {
        var days = new List<DayDto>(_days.Count);
        foreach (var bucket in _days.Values)
        {
            if (bucket.Seconds <= 0 && bucket.Sessions <= 0) continue;
            days.Add(new DayDto(bucket.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), bucket.Path, bucket.Name, bucket.Seconds, bucket.Sessions));
        }
        days.Sort(static (a, b) =>
        {
            var date = string.CompareOrdinal(a.Date, b.Date);
            return date != 0 ? date : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });
        return JsonSerializer.Serialize(new FileDto(Schema, TrackingSince, days), JsonOptions);
    }

    private void Write(string json)
    {
        if (_save is not null)
        {
            _save(_path, json);
            return;
        }
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            if (File.Exists(_path)) File.Replace(temp, _path, destinationBackupFileName: null);
            else File.Move(temp, _path);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            throw;
        }
    }

    private void Load()
    {
        TrackingSince = _clock?.Invoke() ?? DateTimeOffset.Now;
        try
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) return;
            var info = new FileInfo(_path);
            if (info.Length > MaxFileBytes) return;
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<FileDto>(json, JsonOptions);
            if (file is null) return;
            if (file.TrackingSince != default) TrackingSince = file.TrackingSince;
            if (file.Days is null) return;
            foreach (var day in file.Days)
            {
                if (day is null || string.IsNullOrWhiteSpace(day.Path)) continue;
                if (!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                if (!Keep(date)) continue;
                var path = Clean(day.Path, MaxPathLength);
                if (path.Length == 0) continue;
                Add(date, path, Clean(day.Name, MaxNameLength), Clamp(day.Seconds), ClampCount(day.Sessions));
            }
            _dirty = false;
        }
        catch (Exception ex) { Remember(ex); }
    }

    private void Remember(Exception ex)
    {
        lock (_gate)
        {
            _lastError = ex.GetType().Name + ": " + ex.Message;
            if (_loggedError == _lastError) return;
            _loggedError = _lastError;
            System.Diagnostics.Debug.WriteLine("[GamePlayHistory] " + _lastError);
        }
    }

    private DateTimeOffset Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _zone);

    private DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(Local(instant).DateTime);

    private DateTimeOffset Midnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        if (_zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = _zone.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified));
        return new DateTimeOffset(local, offset);
    }

    private static bool TryNormalize(GamePlayInstance? item, out string path, out string name)
    {
        path = "";
        name = "";
        if (item is null || string.IsNullOrWhiteSpace(item.Path)) return false;
        path = Clean(item.Path, MaxPathLength);
        name = Clean(string.IsNullOrWhiteSpace(item.Name) ? path : item.Name, MaxNameLength);
        return path.Length > 0;
    }

    private static string Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        return text.Length <= max ? text : text[..max];
    }

    private static string Key(DateOnly day, string path) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\n" + path.ToUpperInvariant();

    private static long Clamp(long seconds) => seconds < 0 ? 0 : seconds > MaxSecondsPerDay ? MaxSecondsPerDay : seconds;

    private static int ClampCount(int sessions) => sessions < 0 ? 0 : sessions > 10_000 ? 10_000 : sessions;

    private sealed class Live(string path, string name, DateTimeOffset started)
    {
        public string Path { get; } = path;
        public string Name { get; set; } = name;
        public DateTimeOffset Started { get; } = started;
        public DateTimeOffset AccruedUntil { get; set; } = started;
        public long Seconds { get; set; }
    }

    private sealed class Bucket(DateOnly date, string path, string name, long seconds, int sessions)
    {
        public DateOnly Date { get; } = date;
        public string Path { get; } = path;
        public string Name { get; set; } = name;
        public long Seconds { get; set; } = seconds;
        public int Sessions { get; set; } = sessions;
        public Bucket Copy() => new(Date, Path, Name, Seconds, Sessions);
    }

    private sealed record FileDto(int Schema, DateTimeOffset TrackingSince, List<DayDto>? Days);

    private sealed record DayDto(string Date, string Path, string? Name, long Seconds, int Sessions);
}
