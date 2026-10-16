using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class InstanceListPinTest
{
    [TestMethod]
    public void LivePathsStayFirstUntilTheyLeave()
    {
        var list = new[] { "old", "live-b", "star", "live-a" };
        var pinned = InstanceListPin.Pin(list, item => item, ["live-a", "live-b"]);
        CollectionAssert.AreEqual(new[] { "live-a", "live-b", "old", "star" }, pinned);
        CollectionAssert.AreEqual(new[] { "old", "live-b", "star", "live-a" }, list);

        var released = InstanceListPin.Pin(list, item => item, ["live-b"]);
        CollectionAssert.AreEqual(new[] { "live-b", "old", "star", "live-a" }, released);
    }

    [TestMethod]
    public void EmptyOrUnknownPathsDoNotReorder()
    {
        var list = new[] { "a", "b" };
        var pinned = InstanceListPin.Pin(list, item => item, ["", "missing", "missing"]);
        CollectionAssert.AreEqual(list, pinned);
    }
}
