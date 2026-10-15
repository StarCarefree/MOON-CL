using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PCL.Core.Minecraft.Server;

public static class ServerProperties
{
    private sealed record Line(string Raw, string? Key, string? Value);

    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(text)) if (line.Key is not null) values[line.Key] = line.Value!;
        return values;
    }

    public static string Merge(string original, IReadOnlyDictionary<string, string> changes)
    {
        Validate(changes);
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : Environment.NewLine;
        var output = new StringBuilder(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(original))
        {
            if (line.Key is not null && changes.TryGetValue(line.Key, out var value))
            {
                seen.Add(line.Key);
                output.Append(value == line.Value ? line.Raw : Escape(line.Key, true) + "=" + Escape(value, false) + newline);
            }
            else output.Append(line.Raw);
        }
        foreach (var item in changes.Where(item => !seen.Contains(item.Key)))
        {
            if (output.Length > 0 && output[^1] is not '\r' and not '\n') output.Append(newline);
            output.Append(Escape(item.Key, true)).Append('=').Append(Escape(item.Value, false)).Append(newline);
        }
        return output.ToString();
    }

    public static void Validate(IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.IndexOfAny(['\0', '\r', '\n']) >= 0 || pair.Value is null || pair.Value.Contains('\0'))
                throw new ArgumentException("属性名不能包含换行，属性值不能包含空字符。");
        foreach (var key in new[] { "server-port", "query.port", "rcon.port" })
            if (values.TryGetValue(key, out var value) && (!int.TryParse(value, out var port) || port is < 1 or > 65535)) throw new ArgumentException(key + " 必须为 1–65535。");
        if (values.TryGetValue("max-players", out var players) && (!int.TryParse(players, out var count) || count < 1)) throw new ArgumentException("max-players 必须为正整数。");
    }

    private static IEnumerable<Line> Lines(string text)
    {
        var physical = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        for (var index = 0; index < physical.Length; index++)
        {
            if (index == physical.Length - 1 && physical[index].Length == 0) yield break;
            var raw = new StringBuilder(physical[index]); if (index < physical.Length - 1) raw.Append(newline);
            var logical = new StringBuilder(physical[index]);
            var trimmed = physical[index].TrimStart(' ', '\t', '\f');
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!') { yield return new Line(raw.ToString(), null, null); continue; }
            while (TrailingSlashes(logical) % 2 == 1 && index + 1 < physical.Length)
            {
                logical.Length--; index++;
                raw.Append(physical[index]); if (index < physical.Length - 1) raw.Append(newline);
                logical.Append(physical[index].TrimStart(' ', '\t', '\f'));
            }
            var line = logical.ToString().TrimStart(' ', '\t', '\f'); var separator = line.Length; var escaped = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (!escaped && (line[i] is '=' or ':' or ' ' or '\t' or '\f')) { separator = i; break; }
                if (line[i] == '\\') escaped = !escaped; else escaped = false;
            }
            var start = separator;
            while (start < line.Length && line[start] is ' ' or '\t' or '\f') start++;
            if (start < line.Length && line[start] is '=' or ':') start++;
            while (start < line.Length && line[start] is ' ' or '\t' or '\f') start++;
            yield return new Line(raw.ToString(), Unescape(line[..separator]), Unescape(line[start..]));
        }
    }

    private static int TrailingSlashes(StringBuilder value)
    {
        var count = 0; for (var i = value.Length - 1; i >= 0 && value[i] == '\\'; i--) count++; return count;
    }

    private static string Unescape(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\\' || i + 1 == text.Length) { result.Append(ch); continue; }
            ch = text[++i];
            if (ch == 'u')
            {
                if (i + 4 >= text.Length || !ushort.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    throw new FormatException("properties 文件含有无效的 Unicode 转义。");
                result.Append((char)code); i += 4;
            }
            else result.Append(ch switch { 't' => '\t', 'n' => '\n', 'r' => '\r', 'f' => '\f', _ => ch });
        }
        return result.ToString();
    }

    private static string Escape(string text, bool key)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                case '\f': result.Append("\\f"); break;
                default:
                    if ((key && ch is ' ' or '=' or ':' or '#' or '!') || (!key && i == 0 && ch == ' ')) result.Append('\\');
                    result.Append(ch); break;
            }
        }
        return result.ToString();
    }
}
