using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Server;

public sealed class ServerSession
{
    internal object SyncRoot { get; } = new();
    private readonly ServerManager _manager;
     private string _id;
    private readonly Queue<string> _log = new();
    private Process? _process;
    private bool _installing;
    private bool _stopping;
    private string _status = "已停止";

     internal ServerSession(ServerManager manager, string id) { _manager = manager; _id = id; }
     internal void Bind(string id) => _id = id;
    public bool IsRunning { get { lock (SyncRoot) return _process is not null && !_installing; } }
    public bool IsInstalling { get { lock (SyncRoot) return _installing; } }
    public string Status { get { lock (SyncRoot) return _status; } }
    public string[] GetLog() { lock (SyncRoot) return _log.ToArray(); }

    internal void Log(string line)
    {
        lock (SyncRoot)
        {
            if (line.Length > 16000) line = line[..16000] + "…";
            _log.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_log.Count > 2000) _log.Dequeue();
        }
    }

    internal void RequireIdle()
    {
        if (_process is not null || _installing) throw new InvalidOperationException("服务端正在运行或安装，请先停止 / 取消。");
    }

    internal void BeginInstall()
    {
        lock (SyncRoot) { RequireIdle(); _installing = true; _status = "正在安装"; }
        Log("开始安装服务端（与客户端隔离）。");
    }

    internal void EndInstall(string status)
    {
        lock (SyncRoot) { _installing = false; _status = status; }
        Log(status);
    }

    public Task StartAsync()
    {
        lock (SyncRoot)
        {
            RequireIdle();
            var profile = _manager.Load(_id);
            if (!_manager.IsEulaAccepted(profile)) throw new InvalidOperationException("请先阅读并明确同意 Minecraft EULA。");
            ServerCommands.EnsureJava(profile, Log);
             var info = ServerCommands.Start(profile, _manager.GetDirectory(profile), _manager.JavaTempDirectory);
            _process = Launch(info);
            _stopping = false; _status = "运行中（启动输出见控制台）";
            _ = MonitorServerAsync(_process);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Process process;
        lock (SyncRoot)
        {
            if (_installing) throw new InvalidOperationException("请在安装窗口取消安装。");
            process = _process ?? throw new InvalidOperationException("服务端尚未运行。");
            if (!_stopping) { SendCommand("stop"); _stopping = true; _status = "正在安全停止"; }
        }
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { /* Monitor already observed and disposed the exited process. */ }
    }

    public void SendCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("请输入单行命令。");
        lock (SyncRoot)
        {
            if (_installing || _process is null || _process.HasExited) throw new InvalidOperationException("服务端尚未运行。");
            _process.StandardInput.WriteLine(command.TrimStart('/')); _process.StandardInput.Flush();
            // Do not echo arbitrary command text: commands may contain credentials.
            Log("已发送控制台命令。");
        }
    }

    public void Kill()
    {
        lock (SyncRoot)
        {
            if (_installing) throw new InvalidOperationException("请在安装窗口取消安装。");
            if (_process is null) return;
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            Log("已请求强制结束服务端进程树。");
        }
    }

    internal async Task RunInstallerAsync(ProcessStartInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process process;
        lock (SyncRoot)
        {
            if (!_installing || _process is not null) throw new InvalidOperationException("安装会话状态无效。");
            _process = process = Launch(info);
            process.StandardInput.Close(); // Install scripts must not require interactive input.
        }
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"安装程序退出码 {process.ExitCode}，请检查控制台日志。");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            finally { lock (SyncRoot) { _process = null; process.Dispose(); } }
        }
    }

    private Process Launch(ProcessStartInfo info)
    {
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        try
        {
            if (!process.Start()) throw new IOException("无法启动服务端程序。");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            Log("已启动：" + Path.GetFileName(info.FileName));
            return process;
        }
        catch
        {
            try { if (process.Id > 0 && !process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose(); throw;
        }
    }

    private async Task MonitorServerAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            lock (SyncRoot) { _status = $"已退出（代码 {process.ExitCode}）"; Log(_status); }
        }
        catch (Exception ex) { lock (SyncRoot) { _status = "进程监控失败"; Log(ex.Message); } }
        finally
        {
            lock (SyncRoot) { if (ReferenceEquals(_process, process)) _process = null; _stopping = false; process.Dispose(); }
        }
    }
}
