using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PackUpdateProjectTest
{
    [TestMethod]
    public void SameIdAllowsRenameAndMinecraftUpgrade() => Assert.AreEqual(PackProjectMatch.SameProject,
        PackUpdateProject.Compare(new() { CurseForgeId = "123", Name = "Old", MinecraftVersion = "1.20.1" },
            new() { CurseForgeId = "123", Name = "New", MinecraftVersion = "1.21.1" }));

    [TestMethod]
    public void ConflictingIdWinsOverNameAndAnotherMatchingId() => Assert.AreEqual(PackProjectMatch.DifferentProject,
        PackUpdateProject.Compare(new() { Name = "Pack", CurseForgeId = "123", McmodId = "1" },
            new() { Name = "Pack", CurseForgeId = "456", McmodId = "1" }));

    [TestMethod]
    public void NameOnlyMatchIsNotAnIdVerification() => Assert.AreEqual(PackProjectMatch.NameMatch,
        PackUpdateProject.Compare(new() { Name = "All the Mods 9" }, new() { Name = "All the Mods 9" }));

    [TestMethod]
    public void MissingOrUnrelatedNamesNeedManualConfirmation()
    {
        Assert.AreEqual(PackProjectMatch.Unknown, PackUpdateProject.Compare(new(), new()));
        Assert.AreEqual(PackProjectMatch.Unknown, PackUpdateProject.Compare(new() { Name = "Sky Pack" }, new() { Name = "Ocean Life" }));
        Assert.AreEqual(PackProjectMatch.Unknown, PackUpdateProject.Compare(new() { CurseForgeId = "123" }, new() { ModrinthId = "12345678" }));
    }

    [TestMethod]
    public void ProjectIdsAreCaseSensitive() => Assert.AreEqual(PackProjectMatch.DifferentProject,
        PackUpdateProject.Compare(new() { ModrinthId = "AbcDef12" }, new() { ModrinthId = "abcdef12" }));

    [TestMethod]
    public void DownloadPagePrefersPlatformAndRejectsUnsafeFallback()
    {
        Assert.AreEqual("https://modrinth.com/modpack/AbcDef12/versions", PackUpdateProject.DownloadPage(new() { ModrinthId = "AbcDef12", Website = "https://www.mcmod.cn/modpack/1.html" }));
        Assert.AreEqual("https://www.curseforge.com/projects/123", PackUpdateProject.DownloadPage(new() { CurseForgeId = "123" }));
        Assert.AreEqual("https://www.mcmod.cn/modpack/1.html", PackUpdateProject.DownloadPage(new() { Website = "https://www.mcmod.cn/modpack/1.html" }));
        Assert.AreEqual("", PackUpdateProject.DownloadPage(new() { Website = "file:///C:/test" }));
        Assert.AreEqual("", PackUpdateProject.DownloadPage(new() { Website = "https://user:pass@example.com/" }));
        Assert.AreEqual("", PackUpdateProject.DownloadPage(new()));
    }
}
