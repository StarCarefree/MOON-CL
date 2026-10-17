using System;
using System.Collections.Generic;
 using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Server;

 public enum ServerLoader { NeoForge, Fabric, Vanilla, Paper, Purpur, Spigot, Folia }

public sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新服务端";
    public ServerLoader Loader { get; set; }
    public string MinecraftVersion { get; set; } = "";
    public string LoaderVersion { get; set; } = "";
    /// <summary>Major Java version recorded from install metadata, when the source provides one.</summary>
    public int? RequiredJavaMajor { get; set; }
    [JsonIgnore]
    public bool SupportsPlugins => ServerCommands.SupportsPlugins(Loader);
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
         if (!IsFolderId(profile.Id)) throw new ArgumentException("服务端 ID 无效。");
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
        if (profile.RequiredJavaMajor is < 8 or > 64) throw new ArgumentException("所需 Java 主版本无效。");
        _ = ParseJvmArguments(profile.JvmArguments);
    }
 
     internal static bool IsFolderId(string? id)
     {
         if (string.IsNullOrWhiteSpace(id) || id != Path.GetFileName(id) || id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
         return id.Length <= 80;
     }
 
     internal static string FolderName(string name)
     {
         var chars = name.Trim().Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || char.IsControl(ch) ? '-' : ch).ToArray();
         var folder = new string(chars).Trim().TrimEnd('.', ' ');
         if (folder.Length == 0) folder = "server";
         if (folder.Length > 80) folder = folder[..80].TrimEnd('.', ' ');
         if (folder is "." or ".." || ReservedFolder(folder)) folder = "_" + folder;
         return folder;
     }
 
     private static bool ReservedFolder(string name)
     {
         var bare = Path.GetFileNameWithoutExtension(name);
         return bare.Equals("CON", StringComparison.OrdinalIgnoreCase) || bare.Equals("PRN", StringComparison.OrdinalIgnoreCase)
             || bare.Equals("AUX", StringComparison.OrdinalIgnoreCase) || bare.Equals("NUL", StringComparison.OrdinalIgnoreCase)
             || (bare.Length == 4 && (bare.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || bare.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(bare[3]));
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

     public static ProcessStartInfo Java(ServerProfile profile, string directory, string? tempDirectory = null)
     {
         var info = new ProcessStartInfo(profile.JavaPath) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
             RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
             StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
         if (!string.IsNullOrWhiteSpace(tempDirectory))
         {
             var temp = Path.GetFullPath(tempDirectory);
             Directory.CreateDirectory(temp);
             info.ArgumentList.Add("-Djava.io.tmpdir=" + temp);
         }
         SetJavaEnvironment(info, profile.JavaPath);
         return info;
     }

     public static ProcessStartInfo Start(ServerProfile profile, string directory, string? tempDirectory = null)
    {
         Validate(profile);
         var entry = ServerPaths.Inside(directory, profile.EntryPoint);
         if (!File.Exists(entry)) throw new FileNotFoundException("未找到启动入口，请先完成服务端安装或检查版本设置。", entry);
         var extension = Path.GetExtension(entry).ToLowerInvariant();
         if (extension is ".bat" or ".cmd" or ".ps1") return Script(profile, directory, entry, tempDirectory);
         var info = Java(profile, directory, tempDirectory);
        info.ArgumentList.Add($"-Xms{profile.MinMemoryMb}M"); info.ArgumentList.Add($"-Xmx{profile.MaxMemoryMb}M");
        foreach (var argument in ParseJvmArguments(profile.JvmArguments)) info.ArgumentList.Add(argument);
        if (extension == ".jar") { info.ArgumentList.Add("-jar"); info.ArgumentList.Add(profile.EntryPoint); }
        else if (Path.GetFileName(entry).Equals("win_args.txt", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add("@" + profile.EntryPoint.Replace('\\', '/'));
        else throw new ArgumentException("启动入口必须为服务端 JAR、win_args.txt 或启动脚本。");
         info.ArgumentList.Add("nogui"); return info;
     }

     public static ProcessStartInfo Installer(ServerProfile profile, string directory, string jar, string? tempDirectory = null)
     {
         if (IsDirectCore(profile.Loader)) throw new ArgumentException("原版、Paper、Purpur 直接下载 server.jar，不会执行 Fabric 或 NeoForge 安装器。");
         if (string.IsNullOrWhiteSpace(profile.MinecraftVersion) || string.IsNullOrWhiteSpace(profile.LoaderVersion))
             throw new ArgumentException("请填写精确的 Minecraft 和加载器版本。");
         var info = Java(profile, directory, tempDirectory); info.ArgumentList.Add("-jar"); info.ArgumentList.Add(jar);
        if (profile.Loader == ServerLoader.NeoForge) info.ArgumentList.Add("--installServer");
        else if (profile.Loader == ServerLoader.Fabric)
            foreach (var arg in new[] { "server", "-mcversion", profile.MinecraftVersion, "-loader", profile.LoaderVersion, "-downloadMinecraft", "-dir", directory }) info.ArgumentList.Add(arg);
        else throw new ArgumentException("不支持的服务端加载器。");
        return info;
    }

     public static bool IsDirectCore(ServerLoader loader) => loader is ServerLoader.Vanilla or ServerLoader.Paper or ServerLoader.Purpur or ServerLoader.Spigot or ServerLoader.Folia;
 
     public static bool SupportsPlugins(ServerLoader loader) => loader is ServerLoader.Paper or ServerLoader.Purpur or ServerLoader.Spigot or ServerLoader.Folia;

    public static int? ParseJavaMajor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"version\s+""(?<v>\d+(?:\.\d+)*)""");
        if (!match.Success) match = Regex.Match(text, @"\b(?<v>\d+(?:\.\d+)*)\b");
        if (!match.Success || !int.TryParse(match.Groups["v"].Value.Split('.')[0], out var major)) return null;
        var parts = match.Groups["v"].Value.Split('.');
        if (major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy)) return legacy;
        return major;
    }

    public static int? ProbeJavaMajor(string javaPath)
    {
        try
        {
            var info = new ProcessStartInfo(javaPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            info.ArgumentList.Add("-version");
            using var process = Process.Start(info);
            if (process is null) return null;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(8000)) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return null; }
            return ParseJavaMajor(error.GetAwaiter().GetResult() + "\n" + output.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { return null; }
    }

    public static void EnsureJava(ServerProfile profile, Action<string>? log = null)
    {
        if (profile.RequiredJavaMajor is not int required) return;
        var actual = ProbeJavaMajor(profile.JavaPath);
        if (actual is null)
        {
            var message = $"无法确认 Java 版本。此服务端需要 Java {required} 或更高。请在运行环境中选择对应的 java.exe，不要使用 javaw.exe。";
            log?.Invoke(message); throw new InvalidOperationException(message);
        }
        if (actual < required)
        {
            var message = $"当前 Java {actual} 低于所需 Java {required}。请更换 java.exe 后重试。";
            log?.Invoke(message); throw new InvalidOperationException(message);
        }
         log?.Invoke($"Java {actual} 满足所需 Java {required}。");
     }

     public static ProcessStartInfo Script(ServerProfile profile, string directory, string script, string? tempDirectory = null)
     {
         var path = ServerPaths.Inside(directory, Path.GetRelativePath(directory, script));
         var info = Java(profile, directory, tempDirectory);
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
