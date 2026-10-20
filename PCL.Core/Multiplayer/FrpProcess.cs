using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PCL.Core.Multiplayer;

/// <summary>Owns only the frpc process it starts. Job object prevents orphans even on launcher crashes.</summary>
public sealed partial class FrpProcess : IDisposable
{
    private Process? _process;
    private SafeFileHandle? _job;
    private string? _directory;
    public bool IsRunning => _process is { HasExited: false };

    public static async Task<FrpProcess> StartAsync(string executable, string toml, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择可信的 frpc.exe 完整路径（frp 0.61 或更高版本）。");
        var owner = new FrpProcess();
        try
        {
            owner._directory = CreatePrivateDirectory();
            var config = Path.Combine(owner._directory, "frpc.toml");
            await File.WriteAllTextAsync(config, toml, new UTF8Encoding(false), ct).ConfigureAwait(false);
            owner._job = NativeJob.Create();
            using (var verify = Start(executable, config, owner._directory, true, owner._job))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try { await verify.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch { if (!verify.HasExited) verify.Kill(true); throw; }
                if (verify.ExitCode != 0) throw new IOException("frpc 配置校验失败，请检查版本与节点参数。");
            }
            owner._process = Start(executable, config, owner._directory, false, owner._job);
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (!owner.IsRunning) throw new IOException("frpc 已退出。请检查节点地址、凭据、端口占用和版本。");
            ct.ThrowIfCancellationRequested();
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    internal static async Task<FrpProcess> StartToolAsync(string executable, NetworkToolPlan plan, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择可信网络工具的完整 exe 路径。");
        var owner = new FrpProcess();
        try
        {
            owner._directory = CreatePrivateDirectory();
            owner._job = NativeJob.Create();
            owner._process = StartTool(executable, plan.Arguments, plan.Environment, Path.GetDirectoryName(executable)!, owner._job);
            await Task.Delay(700, ct).ConfigureAwait(false);
            if (!owner.IsRunning) throw new IOException("网络工具已退出，请检查程序版本、所需组件和线路配置。");
            ct.ThrowIfCancellationRequested();
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    private static string CreatePrivateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pcl-moon-frp-" + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security); return directory;
    }
    private static Process Start(string exe, string config, string directory, bool verify, SafeFileHandle job) =>
        StartTool(exe, verify ? ["verify", "-c", config] : ["-c", config], null, directory, job);
    private static Process StartTool(string exe, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, string directory, SafeFileHandle job)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        var process = Process.Start(start) ?? throw new IOException("无法启动网络工具。");
        try
        {
            if (NativeJob.AssignProcessToJobObject(job, process.Handle) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ = DrainAsync(process.StandardOutput.BaseStream); _ = DrainAsync(process.StandardError.BaseStream);
            return process;
        }
        catch { if (!process.HasExited) process.Kill(true); process.Dispose(); throw; }
    }
    private static async Task DrainAsync(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    public void Dispose()
    {
        _job?.Dispose(); _job = null; // KILL_ON_JOB_CLOSE, including any child processes.
        if (_process is { } process)
        {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(2000); } }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            process.Dispose(); _process = null;
        }
        if (_directory is { } directory)
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _directory = null;
        }
    }

    private static partial class NativeJob
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinWorkingSet, MaxWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Limits
        {
            public BasicLimits Basic;
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial int SetInformationJobObject(SafeFileHandle job, int infoClass, ref Limits limits, uint size);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        internal static SafeFileHandle Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            var limits = new Limits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (job.IsInvalid || SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Limits>()) == 0)
            {
                var error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error);
            }
            return job;
        }
    }
}
