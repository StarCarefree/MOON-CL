using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Server;

public enum ServerLoader { NeoForge, Fabric }

public sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新服务端";
    public ServerLoader Loader { get; set; }
    public string MinecraftVersion { get; set; } = "";
    public string LoaderVersion { get; set; } = "";
    public string JavaPath { get; set; } = "java";
    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;
    public string JvmArguments { get; set; } = "";
    public string EntryPoint { get; set; } = "";
}

/// <summary>All commands use ArgumentList except cmd.exe's explicitly quoted /c script command.</summary>
public static class ServerCommands
{
    public static void Validate(ServerProfile profile)
    {
        if (!Guid.TryParseExact(profile.Id, "N", out _)) throw new ArgumentException("服务端 ID 无效。");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 150 || profile.Name.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("服务端名称无效。");
        if (!Enum.IsDefined(profile.Loader)) throw new ArgumentException("不支持的服务端加载器。");
        if (string.IsNullOrWhiteSpace(profile.JavaPath) || profile.JavaPath.IndexOfAny(['\r', '\n', '\0', '"']) >= 0)
            throw new ArgumentException("Java 路径无效，请使用 java.exe。");
        if (Path.GetFileNameWithoutExtension(profile.JavaPath).Equals("javaw", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("服务端需要 java.exe，不能使用没有控制台流的 javaw.exe。");
        if (profile.MinMemoryMb < 256 || profile.MaxMemoryMb < profile.MinMemoryMb || profile.MaxMemoryMb > 1048576)
            throw new ArgumentException("内存必须满足 256 ≤ 最小内存 ≤ 最大内存 ≤ 1048576 MiB。");
        foreach (var version in new[] { profile.MinecraftVersion, profile.LoaderVersion })
            if (version is null || !Regex.IsMatch(version, @"\A[0-9A-Za-z.+_-]*\z")) throw new ArgumentException("版本号不能包含路径或空格。");
        _ = ParseJvmArguments(profile.JvmArguments);
    }

    public static IReadOnlyList<string> ParseJvmArguments(string text)
    {
        var result = new List<string>(); var token = new StringBuilder(); var quoted = false;
        foreach (var ch in text ?? "")
        {
            if (ch is '\r' or '\n' or '\0') throw new ArgumentException("JVM 参数不能包含换行。");
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted) { if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); } }
            else token.Append(ch);
        }
        if (quoted) throw new ArgumentException("JVM 参数引号未闭合。");
        if (token.Length > 0) result.Add(token.ToString());
        foreach (var arg in result)
            if (!arg.StartsWith('-') || arg.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase)
                || arg is "-jar" or "-cp" or "-classpath" or "--class-path" or "-m" or "--module" || arg.StartsWith("--class-path=", StringComparison.Ordinal))
                throw new ArgumentException("额外 JVM 参数不能包含入口、类路径或内存参数；请使用对应的版本设置。");
        return result;
    }

    public static ProcessStartInfo Java(ServerProfile profile, string directory)
    {
        var info = new ProcessStartInfo(profile.JavaPath) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        SetJavaEnvironment(info, profile.JavaPath);
        return info;
    }

    public static ProcessStartInfo Start(ServerProfile profile, string directory)
    {
        Validate(profile);
        var entry = ServerPaths.Inside(directory, profile.EntryPoint);
        if (!File.Exists(entry)) throw new FileNotFoundException("未找到启动入口，请先完成服务端安装或检查版本设置。", entry);
        var extension = Path.GetExtension(entry).ToLowerInvariant();
        if (extension is ".bat" or ".cmd" or ".ps1") return Script(profile, directory, entry);
        var info = Java(profile, directory);
        info.ArgumentList.Add($"-Xms{profile.MinMemoryMb}M"); info.ArgumentList.Add($"-Xmx{profile.MaxMemoryMb}M");
        foreach (var argument in ParseJvmArguments(profile.JvmArguments)) info.ArgumentList.Add(argument);
        if (extension == ".jar") { info.ArgumentList.Add("-jar"); info.ArgumentList.Add(profile.EntryPoint); }
        else if (Path.GetFileName(entry).Equals("win_args.txt", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add("@" + profile.EntryPoint.Replace('\\', '/'));
        else throw new ArgumentException("启动入口必须为服务端 JAR、win_args.txt 或启动脚本。");
        info.ArgumentList.Add("nogui"); return info;
    }

    public static ProcessStartInfo Installer(ServerProfile profile, string directory, string jar)
    {
        if (string.IsNullOrWhiteSpace(profile.MinecraftVersion) || string.IsNullOrWhiteSpace(profile.LoaderVersion))
            throw new ArgumentException("请填写精确的 Minecraft 和加载器版本。");
        var info = Java(profile, directory); info.ArgumentList.Add("-jar"); info.ArgumentList.Add(jar);
        if (profile.Loader == ServerLoader.NeoForge) info.ArgumentList.Add("--installServer");
        else foreach (var arg in new[] { "server", "-mcversion", profile.MinecraftVersion, "-loader", profile.LoaderVersion, "-downloadMinecraft", "-dir", directory }) info.ArgumentList.Add(arg);
        return info;
    }

    public static ProcessStartInfo Script(ServerProfile profile, string directory, string script)
    {
        var path = ServerPaths.Inside(directory, Path.GetRelativePath(directory, script));
        var info = Java(profile, directory);
        if (Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) info.ArgumentList.Add(arg);
        }
        else if (Path.GetExtension(path).ToLowerInvariant() is ".bat" or ".cmd")
        {
            if (path.IndexOfAny(['%', '!', '"', '\r', '\n']) >= 0) throw new ArgumentException("批处理脚本路径不能包含 %、! 或引号。");
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            info.Arguments = "/d /s /c \"\"" + path + "\"\"";
        }
        else throw new ArgumentException("只支持 .bat、.cmd、.ps1 脚本。");
        return info;
    }

    private static void SetJavaEnvironment(ProcessStartInfo info, string java)
    {
        var executable = java;
        if (!Path.IsPathRooted(executable))
            foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(path.Trim('"'), java.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? java : java + ".exe");
                if (File.Exists(candidate)) { executable = candidate; break; }
            }
        if (!Path.IsPathRooted(executable)) return;
        var bin = Path.GetDirectoryName(executable)!;
        info.Environment["JAVA_HOME"] = Path.GetDirectoryName(bin)!;
        info.Environment["PATH"] = bin + Path.PathSeparator + info.Environment["PATH"];
    }
}

internal static class ServerPaths
{
    internal static string Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("需要服务端目录内的相对路径。");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("路径不能离开服务端目录。");
        for (var current = result; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("服务端目录不能包含符号链接或目录联接。");
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return result;
    }
}
