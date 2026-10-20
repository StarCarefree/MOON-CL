using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class MlsProgressChannelTest
{
    [TestMethod]
    public void SessionsHaveIndependentFilesAndTokensAndDoNotTouchGameArgumentsWhenDisabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-moon-channel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var first = new MlsProgressChannel(root); using var second = new MlsProgressChannel(root);
            Assert.AreNotEqual(first.Token, second.Token); Assert.AreNotEqual(first.SnapshotPath, second.SnapshotPath);
            var jar = Path.Combine(root, "bridge with spaces.jar"); File.WriteAllText(jar, "test");
            var info = new ProcessStartInfo("java") { Arguments = "-javaagent:original.jar Main", UseShellExecute = false };
            first.Configure(info, jar);
            Assert.IsTrue(info.Arguments.StartsWith("-javaagent:\"" + jar + "\" -javaagent:original.jar"));
            Assert.AreEqual(first.SnapshotPath, info.Environment["PCL_MOON_MLS_PROGRESS_FILE"]);
            Assert.AreEqual(first.Token, info.Environment["PCL_MOON_MLS_PROGRESS_TOKEN"]);
            var state = new LaunchProgressState();
            Assert.IsFalse(first.Poll(state));
            File.WriteAllText(first.SnapshotPath, $$"""{"protocol":1,"token":"{{second.Token}}","status":"ready"}""");
            Assert.IsFalse(first.Poll(state));
            File.WriteAllText(first.SnapshotPath, $$"""{"protocol":1,"token":"{{first.Token}}","status":"loading","current":2,"total":5}""");
            Assert.IsTrue(first.Poll(state)); Assert.AreEqual(.4, state.Snapshot.Progress);
            File.WriteAllText(first.SnapshotPath, new string('x', 17000)); Assert.IsFalse(first.Poll(state));
            var directory = first.DirectoryPath; first.Dispose();
            Assert.IsFalse(Directory.Exists(directory)); Assert.IsFalse(first.Poll(state));
        }
        finally { Directory.Delete(root, true); }
    }
}
