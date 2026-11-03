using System.Runtime.CompilerServices;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>The PC's DMI names are not read in the test host (#557):
    /// a family that picks its light table by them is handed the names
    /// through its test seam.</summary>
    internal static class HidFamilyDmiOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => HidFamilyDmi.OffLimitsForTest = true;
    }

    public class HidFamilyDmiFenceTests
    {
        [Fact]
        public void TheDmiReads_AreFencedInTheTestHost()
        {
            Assert.True(HidFamilyDmi.OffLimitsForTest);
            Assert.Null(HidFamilyDmi.Manufacturer());
            Assert.Null(HidFamilyDmi.ProductName());
            Assert.Null(HidFamilyDmi.ProductSku());
        }
    }
}
