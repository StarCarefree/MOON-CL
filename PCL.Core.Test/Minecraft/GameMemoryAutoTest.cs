using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class GameMemoryAutoTest
{
    private const double Delta = 1e-9;

    [TestMethod]
    public void ModdedInstanceIsCappedAtMaxGb()
    {
        Assert.AreEqual(12, GameMemoryAuto.Calculate(64, 297, false), Delta);
    }

    [TestMethod]
    public void VanillaInstanceBelowCapUnchanged()
    {
        Assert.AreEqual(7.8, GameMemoryAuto.Calculate(32, null, false), Delta);
    }

    [TestMethod]
    public void LowMemoryKeepsMinimum()
    {
        Assert.AreEqual(1.8, GameMemoryAuto.Calculate(2, 0, false), Delta);
    }

    [TestMethod]
    public void HugeMinimumIsAlsoCapped()
    {
        Assert.AreEqual(12, GameMemoryAuto.Calculate(0, 2000, false), Delta);
    }

    [TestMethod]
    public void ExtremeInputsStayUnderCap()
    {
        Assert.IsTrue(GameMemoryAuto.Calculate(1000, 100000, true) <= GameMemoryAuto.MaxGb);
    }
}
