using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>决定是否沿用旧版固定年轻代参数；不修改用户提供的任何参数。</summary>
public static class JvmLaunchTuning
{
    public static bool ShouldAddLegacyYoungGeneration(int javaMajorVersion, bool is64Bit, IEnumerable<string> argumentParts)
    {
        var arguments = string.Join(" ", argumentParts);
        // 不猜测不完整或含转义引号的命令行，保留原启动行为。
        if (arguments.Count(ch => ch == '"') % 2 != 0 || arguments.Contains("\\\"", StringComparison.Ordinal))
            return true;
        // 整个 -Dname="..." 是一个参数，不能把属性值里的示例误认为 JVM 选项。
        var tokens = Regex.Matches(arguments, """(?:[^\s"]|"[^"]*")+""")
            .Select(match => match.Value.Trim('"')).ToArray();
        if (tokens.Any(token => token.StartsWith("-Xmn", StringComparison.Ordinal) ||
                                token.StartsWith("-XX:NewSize=", StringComparison.Ordinal) ||
                                token.StartsWith("-XX:MaxNewSize=", StringComparison.Ordinal) ||
                                token.StartsWith("-XX:NewRatio=", StringComparison.Ordinal) ||
                                token.StartsWith("-XX:G1NewSizePercent=", StringComparison.Ordinal) ||
                                token.StartsWith("-XX:G1MaxNewSizePercent=", StringComparison.Ordinal)))
            return false; // 用户/整合包已经调过年轻代，不能追加 -Xmn 覆盖它。
        if (javaMajorVersion < 21 || !is64Bit)
            return true;
        // 仅对已确认的现代 G1 恢复自适应；其他 GC、外置参数文件和未知组合保持原样。
        if (tokens.Any(token => token.StartsWith('@') || token.StartsWith("-XX:Flags=", StringComparison.Ordinal) ||
                                token == "-XX:-UseG1GC" ||
                                (token.StartsWith("-XX:+Use", StringComparison.Ordinal) &&
                                 token.EndsWith("GC", StringComparison.Ordinal) && token != "-XX:+UseG1GC")))
            return true;
        return !tokens.Contains("-XX:+UseG1GC", StringComparer.Ordinal);
    }
}
