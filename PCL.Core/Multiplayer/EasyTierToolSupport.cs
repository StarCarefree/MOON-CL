using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Multiplayer;

public sealed record EasyTierToolInfo(string Version, bool HasCli, bool Supported)
{
    public string Summary => $"EasyTier {Version}" + (Supported ? " · 可用于房间联机" : " · 请使用 2.6.4 或更高版本") +
        (HasCli ? " · CLI 已就绪" : " · 未发现可选 CLI");
}

/// <summary>Local, explicit version checks only. Never downloads or starts a network instance.</summary>
public static class EasyTierToolSupport
{
    public static void ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            !File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("未找到 EasyTier 工具。请在房间的工具面板选择或获取 easytier-core.exe。");
    }

    public static EasyTierToolInfo ParseVersion(string output, bool hasCli)
    {
        if (output.Length > 4096) throw new IOException("EasyTier 版本响应过长，请确认所选程序来源。");
        var match = Regex.Match(output, @"\beasytier-core\s+(\d+)\.(\d+)\.(\d+)(?:[-+][^\s]+)?", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (!match.Success || !Version.TryParse($"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}", out var version))
            throw new IOException("所选程序未返回 EasyTier Core 版本，请重新选择正确的工具。");
        return new EasyTierToolInfo(version.ToString(), hasCli, version >= new Version(2, 6, 4));
    }

    public static async Task<EasyTierToolInfo> CheckAsync(string executable, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateExecutable(executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--version");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var process = Process.Start(start) ?? throw new IOException("无法执行 EasyTier 版本检查。");
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            // A faulty executable must not block the UI or accumulate unbounded diagnostic output.
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("EasyTier 版本检查失败，请检查程序架构和所需组件。");
            return ParseVersion(await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false),
                File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "easytier-cli.exe")));
        }
        finally
        {
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[512];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return result.ToString();
            if (result.Length + read > 4096) throw new IOException("工具版本响应过长。");
            result.Append(buffer, 0, read);
        }
    }
}
