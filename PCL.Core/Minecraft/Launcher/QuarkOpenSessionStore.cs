using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>DPAPI session shared across launcher restarts and single-exe payload versions.</summary>
public sealed class QuarkOpenSessionStore
{
    private readonly string _launcherDirectory;
    private readonly string _dataDirectory;
    private bool _migrationChecked;
    public string SessionPath { get; }

    public QuarkOpenSessionStore(string launcherDirectory, string? dataDirectory = null)
    {
        _launcherDirectory = launcherDirectory;
        _dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL-MOON");
        SessionPath = Path.Combine(_dataDirectory, "quark-open-session.bin");
    }

    public QuarkOpenSession? Load()
    {
        var saved = QuarkOpenSessionFile.TryLoad(SessionPath);
        // Never replace an existing session with an older token from an extraction cache.
        if (saved is not null) return saved.IsReusable ? saved : null;
        if (_migrationChecked) return null;
        _migrationChecked = true;
        foreach (var path in LegacyPaths().Where(File.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var legacy = QuarkOpenSessionFile.TryLoad(path);
            if (legacy?.IsReusable != true) continue;
            Save(legacy);
            return legacy;
        }
        return null;
    }

    public void Save(QuarkOpenSession session)
    {
        QuarkOpenSessionFile.Save(SessionPath, session);
        _migrationChecked = true;
    }

    private IEnumerable<string> LegacyPaths()
    {
        var paths = new List<string> { Path.Combine(_launcherDirectory, "PCL", "quark-open-session.bin") };
        var single = Path.Combine(_dataDirectory, "single");
        try
        {
            if (Directory.Exists(single))
                foreach (var directory in Directory.EnumerateDirectories(single))
                {
                    var name = Path.GetFileName(directory);
                    if (name.Length == 64 && name.All(Uri.IsHexDigit))
                        paths.Add(Path.Combine(directory, "PCL", "quark-open-session.bin"));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return paths;
    }
}
