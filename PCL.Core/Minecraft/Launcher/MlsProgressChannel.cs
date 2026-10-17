using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>One bounded local snapshot file per launch; no shared port, log scraping or game-directory modifications.</summary>
public sealed class MlsProgressChannel : IDisposable
{
    public string DirectoryPath { get; }
    public string SnapshotPath => Path.Combine(DirectoryPath, "progress.json");
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public DateTime LastSignalUtc { get; private set; } = DateTime.UtcNow;
    private bool _disposed;

    public MlsProgressChannel(string temporaryRoot)
    {
        DirectoryPath = Path.Combine(temporaryRoot, "MoonclLoading", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public void Configure(ProcessStartInfo startInfo, string bridgeJar, string? target = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MlsProgressChannel));
        if (!File.Exists(bridgeJar) || bridgeJar.Contains('"')) throw new IOException("Invalid progress bridge JAR");
        startInfo.Arguments = "-javaagent:\"" + bridgeJar + "\" " + startInfo.Arguments;
        startInfo.Environment["MOONCL_MLS_PROGRESS_FILE"] = SnapshotPath;
        startInfo.Environment["MOONCL_MLS_PROGRESS_TOKEN"] = Token;
        if (string.IsNullOrWhiteSpace(target)) startInfo.Environment.Remove("MOONCL_MLS_TARGET");
        else startInfo.Environment["MOONCL_MLS_TARGET"] = target;
    }

    public bool Poll(LaunchProgressState state)
    {
        if (_disposed) return false;
        try
        {
            using var stream = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 16384) return false;
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024);
            var json = reader.ReadToEnd();
            if (!state.AcceptTelemetry(json, Token)) return false;
            LastSignalUtc = File.GetLastWriteTimeUtc(SnapshotPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(DirectoryPath, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
