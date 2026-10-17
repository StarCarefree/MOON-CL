using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class StartupAccelerationPolicyTest
{
    [TestMethod]
    [DataRow(21, true, "", false)]
    [DataRow(21, true, "-Xmx4G", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true", true)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=TRUE", true)]
    [DataRow(21, true, "\"-Dmooncl.startup.enabled=true\"", true)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=\"true\"", true)]
    [DataRow(17, true, "-Dmooncl.startup.enabled=true", true)]
    [DataRow(17, true, "-Dmooncl.startup.enabled=true -Dmooncl.startup.disabled=true", false)]
    [DataRow(17, false, "-Dmooncl.startup.enabled=true", false)]
    [DataRow(17, true, "-Dmooncl.startup.enabled=true @options.txt", false)]
    [DataRow(8, true, "-Dmooncl.startup.enabled=true", false)]
    [DataRow(18, true, "-Dmooncl.startup.enabled=true", false)]
    [DataRow(25, true, "-Dmooncl.startup.enabled=true", false)]
    [DataRow(21, false, "-Dmooncl.startup.enabled=true", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=false", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true -Dmooncl.startup.enabled=false", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=false -Dmooncl.startup.enabled=true", true)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true -Dmooncl.startup.disabled=true", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true -Dmooncl.startup.disabled=TRUE", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true -Dmooncl.startup.disabled=true -Dmooncl.startup.disabled=false", true)]
    [DataRow(21, true, "-Dnote=\"example -Dmooncl.startup.enabled=true\"", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true @options.txt", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true -XX:Flags=options.txt", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true \"unclosed", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true -javaagent:\"C:/a b/mooncl-startup-agent-hash.jar\"", false)]
    public void RequiresExplicitCompatibleOptIn(int java, bool is64Bit, string arguments, bool expected)
    {
        Assert.AreEqual(expected, StartupAccelerationPolicy.ShouldInject(java, is64Bit, arguments));
    }
}
