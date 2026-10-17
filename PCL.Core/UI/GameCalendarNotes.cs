using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PCL.Core.UI;

/// <summary>User-owned date notes, independent of widget layout and game folders.</summary>
public sealed class GameCalendarNotes
{
    private readonly string _path;
    private Dictionary<DateOnly, string> _notes = [];
    public string? LastError { get; private set; }

    public GameCalendarNotes(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("日历便签文件过大");
            var loaded = JsonSerializer.Deserialize<Dictionary<DateOnly, string>>(File.ReadAllText(path));
            foreach (var pair in loaded ?? [])
            {
                if (_notes.Count >= 3650) break;
                if (!string.IsNullOrWhiteSpace(pair.Value)) _notes[pair.Key] = pair.Value.Length > 2000 ? pair.Value[..2000] : pair.Value;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { LastError = ex.Message; }
    }

    public string Get(DateOnly date) => _notes.GetValueOrDefault(date) ?? "";

    /// <summary>Commit only after a successful atomic write; failed edits never erase existing notes.</summary>
    public void Set(DateOnly date, string text)
    {
        if (LastError is not null) throw new IOException("便签文件未能完整读取；请先备份并修复该文件，避免覆盖原有内容：" + LastError);
        if (text.Length > 2000) throw new ArgumentOutOfRangeException(nameof(text), "便签最多 2000 字");
        var copy = new Dictionary<DateOnly, string>(_notes);
        if (string.IsNullOrWhiteSpace(text)) copy.Remove(date);
        else copy[date] = text.Trim();
        if (copy.Count > 3650) throw new InvalidOperationException("最多保存 3650 天的日期便签");
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(copy));
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
            _notes = copy;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
