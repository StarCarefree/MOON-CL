using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackDownloadCacheTest
{
    [TestMethod]
    public void SavedFolderWinsEvenWhenTempHasRoom()
    {
        var chosen = PackDownloadCache.Resolve(@"D:\packs", @"C:\Temp", (_, _) => true, _ => true);
        Assert.AreEqual(Path.Combine(@"D:\packs", PackDownloadCache.FolderName), chosen);
    }

    [TestMethod]
    public void SavedCacheFolderIsNotNestedAgain()
    {
        var existing = Path.Combine(@"D:\packs", PackDownloadCache.FolderName);
        Assert.AreEqual(existing, PackDownloadCache.Resolve(existing, @"C:\Temp", (_, _) => false, _ => false));
        Assert.AreEqual(existing, PackDownloadCache.NormalizeChosenFolder(existing + @"\"));
    }

    [TestMethod]
    public void AutomaticChoiceUsesTempThenFallbacks()
    {
        Assert.AreEqual(Path.Combine(@"C:\Temp", PackDownloadCache.FolderName),
            PackDownloadCache.Resolve("", @"C:\Temp", (_, _) => true, _ => true));
        Assert.AreEqual(Path.Combine(@"I:\tmp-pcl", PackDownloadCache.FolderName),
            PackDownloadCache.Resolve("  ", @"C:\Temp", (path, _) => path.StartsWith(@"I:\", StringComparison.OrdinalIgnoreCase), _ => true));
    }

    [TestMethod]
    public void AutomaticChoiceSkipsMissingDrivesAndThrowsWhenNothingFits()
    {
        Assert.ThrowsExactly<IOException>(() =>
            PackDownloadCache.Resolve(null, @"C:\Temp", (_, _) => false, root => root.StartsWith(@"Z:\", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ClearIncludesSavedFolderAndKnownAutomaticFolders()
    {
        var targets = PackDownloadCache.ClearTargets(@"E:\cache", @"C:\Temp");
        CollectionAssert.Contains(targets, Path.Combine(@"C:\Temp", PackDownloadCache.FolderName));
        CollectionAssert.Contains(targets, Path.Combine(@"G:\download", PackDownloadCache.FolderName));
        CollectionAssert.Contains(targets, Path.Combine(@"E:\cache", PackDownloadCache.FolderName));
    }
}
