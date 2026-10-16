using System.IO;
using PCL.Core.Minecraft.Launcher;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>Single-file Quark transfer using the launcher's real downloader and task presentation.</summary>
internal static class QuarkNativeDownload
{
    internal static ModLoader.LoaderCombo<string> Create(string name, string archive, QuarkOwnedDownload owned)
    {
        if (owned.ExpectedSize <= 0) throw new InvalidDataException("无法确认夸克目标文件大小，已停止下载。");
        var file = new DownloadFile([owned.Download.Url], archive,
            new ModBase.FileChecker(minSize: 1, actualSize: owned.ExpectedSize, canUseExistsFile: false),
            requestHeaders: owned.Download.Headers);
        return new QuarkTask("下载夸克整合包：" + name, file);
    }

    internal static async Task WaitAsync(ModLoader.LoaderCombo<string> task, CancellationToken token)
    {
        using var registration = token.Register(task.Abort);
        try
        {
            while (task.State == ModBase.LoadState.Loading && task is not QuarkTask { CancellationRequested: true })
                await Task.Delay(50, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (task.State == ModBase.LoadState.Aborted || task is QuarkTask { CancellationRequested: true })
                throw new OperationCanceledException("夸克下载已取消。", token);
            if (task.State != ModBase.LoadState.Finished)
                throw new QuarkOpenException("夸克文件下载失败。请重新获取下载地址后重试。");
        }
        finally
        {
            if (task.State == ModBase.LoadState.Loading) task.Abort();
            await Task.WhenAll(task.GetLoaderList().OfType<LoaderDownload>().Select(loader => loader.Completion)).ConfigureAwait(false);
        }
    }

    // Generic combo completion may race Abort. A cancellation must never be undone by
    // a late Finished state, nor may a queued child Start create IO after temp cleanup.
    private sealed class DownloadGate
    {
        internal readonly object Sync = new();
        internal volatile bool Cancelled;
    }

    private sealed class QuarkTask : ModLoader.LoaderCombo<string>
    {
        private readonly DownloadGate _gate;
        internal bool CancellationRequested => _gate.Cancelled;

        internal QuarkTask(string name, DownloadFile file) : this(name, file, new DownloadGate()) { }
        private QuarkTask(string name, DownloadFile file, DownloadGate gate)
            : base(name, [new QuarkLoaderDownload(file, gate) { block = true, ProgressWeight = 1 }]) => _gate = gate;

        public override void Start(object input = null!, bool isForceRestart = false)
        {
            lock (_gate.Sync)
            {
                if (_gate.Cancelled) return;
                base.Start(input, isForceRestart);
            }
        }

        public override void Abort()
        {
            lock (_gate.Sync)
            {
                _gate.Cancelled = true;
                base.Abort();
            }
        }
    }

    private sealed class QuarkLoaderDownload(DownloadFile file, DownloadGate gate) : LoaderDownload("下载整合包文件", [file])
    {
        public override void Start(object input = null!, bool isForceRestart = false)
        {
            lock (gate.Sync)
            {
                if (gate.Cancelled) return;
                base.Start(input, isForceRestart);
            }
        }
    }
}
