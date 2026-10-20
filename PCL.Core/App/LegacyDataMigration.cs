using System;
using System.IO;
using System.Linq;
using PCL.Core.Logging;

namespace PCL.Core.App;

/// <summary>Migrates user data from the legacy MOONCL LocalAppData folder to PCL-MOON.</summary>
public static class LegacyDataMigration
{
    private const string LegacyFolderName = "MOONCL";
    private const string CurrentFolderName = "PCL-MOON";

    public static void Migrate()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Migrate(Path.Combine(localAppData, LegacyFolderName), Path.Combine(localAppData, CurrentFolderName));
    }

    public static void Migrate(string oldRoot, string newRoot)
    {
        try
        {
            if (!Directory.Exists(oldRoot)) return;
            Directory.CreateDirectory(newRoot);
            foreach (var entry in Directory.EnumerateFileSystemEntries(oldRoot))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, "bundle", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(newRoot, name);
                try
                {
                    if (File.Exists(target) || Directory.Exists(target)) continue;
                    if (Directory.Exists(entry))
                        Directory.Move(entry, target);
                    else
                        File.Move(entry, target);
                }
                catch (Exception ex)
                {
                    LogWrapper.Warn(ex, "Migration", $"跳过旧数据迁移: {entry}");
                }
            }
            if (!Directory.EnumerateFileSystemEntries(oldRoot).Any())
                Directory.Delete(oldRoot);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Migration", "旧数据目录迁移失败");
        }
    }
}
