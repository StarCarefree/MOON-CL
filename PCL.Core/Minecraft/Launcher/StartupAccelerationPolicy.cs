using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>Experimental JAR lookup acceleration is strictly opt-in, never a default JVM tuning.</summary>
public static class StartupAccelerationPolicy
{
    public static bool ShouldInject(int javaMajor, bool is64Bit, string arguments)
    {
        if (javaMajor is not (17 or 21) || !is64Bit || string.IsNullOrEmpty(arguments)) return false;
        if (arguments.Count(ch => ch == '"') % 2 != 0 || arguments.Contains("\\\"", StringComparison.Ordinal)) return false;
        var enabled = false;
        var disabled = false;
        foreach (Match match in Regex.Matches(arguments, """(?:[^\s"]|"[^"]*")+"""))
        {
            var token = match.Value.Replace("\"", "", StringComparison.Ordinal);
            // Do not guess options hidden in argument files, or inject the same agent twice.
            if (token.StartsWith('@') || token.StartsWith("-XX:Flags=", StringComparison.Ordinal) ||
                (token.StartsWith("-javaagent:", StringComparison.Ordinal) &&
                 token.Contains("mooncl-startup-agent", StringComparison.OrdinalIgnoreCase))) return false;
            if (token.StartsWith("-Dmooncl.startup.enabled=", StringComparison.Ordinal))
                enabled = token[25..].Equals("true", StringComparison.OrdinalIgnoreCase);
            if (token.StartsWith("-Dmooncl.startup.disabled=", StringComparison.Ordinal))
                disabled = token[26..].Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return enabled && !disabled;
    }
}
