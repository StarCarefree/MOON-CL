using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class QuarkClientLaunchTest
{
    [TestMethod]
    public void ParsesQuotedQuarkCommand()
    {
        Assert.AreEqual(@"G:\Quark\quark.exe", QuarkClientLaunch.ExeFromCommand("\"G:\\Quark\\quark.exe\" --single-argument %1"));
    }

    [TestMethod]
    public void AcceptsOnlyPlainShareUrls()
    {
        Assert.IsTrue(QuarkClientLaunch.IsShareUrl("https://pan.quark.cn/s/e3f623dd58d6"));
        Assert.IsFalse(QuarkClientLaunch.IsShareUrl("https://pan.quark.cn/s/e3f623dd58d6?token=secret"));
        Assert.IsFalse(QuarkClientLaunch.IsShareUrl("http://pan.quark.cn/s/e3f623dd58d6"));
    }

    [TestMethod]
    public void DoesNotStartWhenExecutableIsMissing()
    {
        var started = false;
        Assert.IsFalse(QuarkClientLaunch.TryOpenShare("https://pan.quark.cn/s/e3f623dd58d6", @"G:\missing\quark.exe", (_, _) => started = true));
        Assert.IsFalse(started);
    }

    [TestMethod]
    public void StartsOnlyTheShareUrl()
    {
        var exe = Path.GetTempFileName();
        try
        {
            string? seen = null;
            Assert.IsTrue(QuarkClientLaunch.TryOpenShare("https://pan.quark.cn/s/e3f623dd58d6", exe, (path, url) =>
            {
                seen = path + "|" + url;
                return true;
            }));
            Assert.AreEqual(exe + "|https://pan.quark.cn/s/e3f623dd58d6", seen);
        }
        finally { File.Delete(exe); }
    }

    [TestMethod]
    public void DriveShareUriHasNoLoginToken()
    {
        var uri = QuarkClientLaunch.BuildDriveShareUri("https://pan.quark.cn/s/e3f623dd58d6");
        Assert.AreEqual("qkclouddrive://openShareModal?pwdid=e3f623dd58d6&source=13&pwd_id=e3f623dd58d6&share_dn=e3f623dd58d6", uri);
        Assert.IsFalse(uri!.Contains("user_token"));
        Assert.IsFalse(uri.Contains("cookie"));
        Assert.IsNull(QuarkClientLaunch.BuildDriveShareUri("https://pan.quark.cn/s/e3f623dd58d6?token=secret"));
    }

    [TestMethod]
    public void DriveShareUriKeepsOnlyOfficialPasscode()
    {
        Assert.AreEqual(
            "qkclouddrive://openShareModal?pwdid=e3f623dd58d6&source=13&pwd_id=e3f623dd58d6&share_dn=e3f623dd58d6&passcode=ab12",
            QuarkClientLaunch.BuildDriveShareUri("https://pan.quark.cn/s/e3f623dd58d6?pwd=ab12"));
    }

    [TestMethod]
    public void DriveShareDoesNotStartForInvalidUrl()
    {
        var started = false;
        Assert.IsFalse(QuarkClientLaunch.TryOpenDriveShare("https://pan.quark.cn/s/e3f623dd58d6?token=secret", _ => started = true));
        Assert.IsFalse(started);
        Assert.IsTrue(QuarkClientLaunch.TryOpenDriveShare("https://pan.quark.cn/s/e3f623dd58d6", uri =>
        {
            started = uri.StartsWith("qkclouddrive://openShareModal?");
            return started;
        }));
        Assert.IsTrue(started);
    }

    [TestMethod]
    public void AuthorizePageIsOfficialHttpsOnly()
    {
        Assert.IsTrue(QuarkClientLaunch.IsAuthorizePage("https://pan.quark.cn/open/v1/oauth/authorize?client_id=third_party_agent"));
        Assert.IsFalse(QuarkClientLaunch.IsAuthorizePage("http://pan.quark.cn/open/v1/oauth/authorize"));
        Assert.IsFalse(QuarkClientLaunch.IsAuthorizePage("https://user:secret@pan.quark.cn/open"));
        Assert.IsFalse(QuarkClientLaunch.IsAuthorizePage("qkclouddrive://openShareModal?pwdid=e3f623dd58d6"));
        var started = false;
        Assert.IsFalse(QuarkClientLaunch.TryOpenAuthorizePage("https://evil.example/auth?page_code=secret-page", _ => started = true));
        Assert.IsFalse(started);
    }

    [TestMethod]
    public void MatchShareUsesOnlyParsedShareId()
    {
        const string owned = "https://pan.quark.cn/s/ff96d10800cd?pwd=ab12";
        Assert.AreEqual("ff96d10800cd", QuarkClientLaunch.ShareId(owned));
        Assert.AreEqual(owned, QuarkClientLaunch.MatchShare([owned, "https://example.org/demo.zip"], "ff96d10800cd"));
        Assert.IsNull(QuarkClientLaunch.MatchShare([owned], "other"));
        Assert.IsNull(QuarkClientLaunch.MatchShare(["https://pan.quark.cn/s/ff96d10800cd?token=secret"], "ff96d10800cd"));
        Assert.IsNull(QuarkClientLaunch.ShareId("https://pan.quark.cn.evil/s/ff96d10800cd"));
    }
}
