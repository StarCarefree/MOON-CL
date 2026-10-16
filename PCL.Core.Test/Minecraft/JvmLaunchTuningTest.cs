using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class JvmLaunchTuningTest
{
    [TestMethod]
    [DataRow(21, true, "-XX:+UseG1GC -XX:-UseAdaptiveSizePolicy", false)]
    [DataRow(25, true, "-XX:+UseG1GC", false)]
    [DataRow(17, true, "-XX:+UseG1GC", true)]
    [DataRow(8, true, "-XX:+UseG1GC", true)]
    [DataRow(0, true, "-XX:+UseG1GC", true)]
    [DataRow(21, false, "-XX:+UseG1GC", true)]
    [DataRow(21, true, "", true)]
    [DataRow(21, true, "-XX:+UseParallelGC", true)]
    [DataRow(21, true, "-XX:+UseZGC", true)]
    [DataRow(21, true, "-XX:+UseShenandoahGC", true)]
    [DataRow(21, true, "-XX:+UseG1GC -XX:-UseG1GC", true)]
    [DataRow(21, true, "-XX:+UseG1GC -XX:+UseSerialGC", true)]
    [DataRow(21, true, "\"-XX:+UseG1GC\"", false)]
    [DataRow(21, true, "-Dnote=\"example -XX:+UseG1GC\"", true)]
    [DataRow(21, true, "-XX:+UseG1GC -Dnote=\"example -Xmn1m\"", false)]
    [DataRow(21, true, "-XX:+UseG1GC @options.txt", true)]
    [DataRow(21, true, "-XX:+UseG1GC -XX:Flags=options.txt", true)]
    [DataRow(21, true, "-XX:+UseG1GC \"unclosed", true)]
    [DataRow(21, true, "-XX:+UseG1GC\t-Dname=test", false)]
    public void OnlyModernExplicitG1OptsIntoAdaptiveYoungGeneration(int java, bool is64Bit, string args, bool expected)
    {
        Assert.AreEqual(expected, JvmLaunchTuning.ShouldAddLegacyYoungGeneration(java, is64Bit, [args]));
    }

    [TestMethod]
    [DataRow("-Xmn512m")]
    [DataRow("-XX:NewSize=512m")]
    [DataRow("-XX:MaxNewSize=1024m")]
    [DataRow("-XX:NewRatio=2")]
    [DataRow("-XX:G1NewSizePercent=20")]
    [DataRow("-XX:G1MaxNewSizePercent=40")]
    public void ExplicitYoungGenerationSettingsAreNeverOverridden(string argument)
    {
        foreach (var java in new[] { 8, 17, 21 })
            Assert.IsFalse(JvmLaunchTuning.ShouldAddLegacyYoungGeneration(java, true, [argument]));
    }

    [TestMethod]
    public void InputPartsAreNotChangedAndXmsXmxDoNotDisablePolicy()
    {
        string[] parts = ["-XX:+UseG1GC -Xms512m", "-Xmx4096m", "-Dname=\"a b\""];
        var original = (string[])parts.Clone();
        Assert.IsFalse(JvmLaunchTuning.ShouldAddLegacyYoungGeneration(21, true, parts));
        CollectionAssert.AreEqual(original, parts);
    }
}
