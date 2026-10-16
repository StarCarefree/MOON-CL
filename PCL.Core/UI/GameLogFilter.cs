using System;
using System.Collections.Generic;

namespace PCL.Core.UI;

public enum GameLogKind
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    Fatal = 4
}

public readonly record struct GameLogLine(string Text, GameLogKind Kind);

/// <summary>Classifies Minecraft log prefixes and filters the viewer. Continuation lines keep the previous level.</summary>
public static class GameLogFilter
{
    public static GameLogKind Classify(string? line, GameLogKind previous)
    {
        if (string.IsNullOrEmpty(line)) return previous;
        if (line.StartsWith("\tat ", StringComparison.Ordinal) ||
            line.StartsWith("Caused by: ", StringComparison.Ordinal) ||
            line.StartsWith("\t... ", StringComparison.Ordinal))
            return previous;
        var head = line.Split(": ", 2)[0];
        if (head.Contains("FATAL", StringComparison.OrdinalIgnoreCase)) return GameLogKind.Fatal;
        if (head.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) return GameLogKind.Error;
        if (head.Contains("WARN", StringComparison.OrdinalIgnoreCase)) return GameLogKind.Warn;
        if (head.Contains("INFO", StringComparison.OrdinalIgnoreCase)) return GameLogKind.Info;
        if (head.Contains("DEBUG", StringComparison.OrdinalIgnoreCase)) return GameLogKind.Debug;
        if (line.StartsWith("Exception in thread \"", StringComparison.Ordinal)) return GameLogKind.Error;
        if ((line.Contains("Exception", StringComparison.Ordinal) ||
             line.Contains("Realms authentication error with message ", StringComparison.Ordinal)) &&
            previous >= GameLogKind.Warn)
            return previous;
        return GameLogKind.Info;
    }

    public static IEnumerable<GameLogLine> Select(IEnumerable<string?> lines, bool debug, bool info, bool warn, bool error)
    {
        var previous = GameLogKind.Info;
        foreach (var raw in lines)
        {
            var text = raw ?? "";
            var kind = Classify(text, previous);
            previous = kind;
            if (Allows(kind, debug, info, warn, error))
                yield return new GameLogLine(text, kind);
        }
    }

    public static bool Allows(GameLogKind kind, bool debug, bool info, bool warn, bool error) => kind switch
    {
        GameLogKind.Debug => debug,
        GameLogKind.Info => info,
        GameLogKind.Warn => warn,
        GameLogKind.Error or GameLogKind.Fatal => error,
        _ => false
    };

    public static string ColorHex(GameLogKind kind) => kind switch
    {
        GameLogKind.Debug => "#7C8490",
        GameLogKind.Info => "#C9CED6",
        GameLogKind.Warn => "#FFB942",
        GameLogKind.Error => "#FF6B6B",
        GameLogKind.Fatal => "#FF4D6A",
        _ => "#C9CED6"
    };
}
