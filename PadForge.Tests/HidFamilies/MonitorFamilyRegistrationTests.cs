using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests.MonitorFamilies
{
    /// <summary>Every OpenRGB registration of the six monitor families
    /// answers with a model of its family through the worker's list (#557),
    /// so a registration a family misses fails.</summary>
    public class MonitorFamilyRegistrationTests
    {
        public static IEnumerable<object[]> Families() => new[]
        {
            new object[] { "AlienwareMonitorController", 2 },
            new object[] { "AsusMonitorController", 7 },
            new object[] { "LGMonitorController", 1 },
            new object[] { "MSIMonitorController", 1 },
            new object[] { "MSIOptixController", 1 },
            new object[] { "ViewSonicController", 3 },
        };

        private static IHidLightingFamily FamilyOf(string folder) => folder switch
        {
            "AlienwareMonitorController" => AlienwareMonitorLighting.Instance,
            "AsusMonitorController" => AsusMonitorLighting.Instance,
            "LGMonitorController" => LgMonitorLighting.Instance,
            "MSIMonitorController" => MsiMonitorLighting.Instance,
            "MSIOptixController" => MsiOptixLighting.Instance,
            "ViewSonicController" => ViewSonicMonitorLighting.Instance,
            _ => null,
        };

        [Theory]
        [MemberData(nameof(Families))]
        public void EveryRegistration_AnswersWithAModelOfItsFamily(string folder, int count)
        {
            var family = FamilyOf(folder);
            var registrations = OpenRgbRegistrations.Of(folder).ToList();
            Assert.Equal(count, registrations.Count);
            foreach (var r in registrations)
            {
                var collection = MonitorTest.CollectionOf(r);
                var model = family.Match(collection);
                Assert.True(model != null, $"{folder} {r.Name} {r.ProductId:X4}");
                Assert.Same(family, model.Family);
                Assert.Equal(r.Name, model.Name);
                Assert.True(model.Map.Lights.Count > 0);
                Assert.NotNull(model.Begin);
                Assert.Same(model, family.Match(collection));
                Assert.Same(model, HidLightingFamilies.Match(collection));
                Assert.Null(family.Match(collection with { VendorId = (ushort)(r.VendorId ^ 0x5555) }));
                Assert.Null(family.Match(collection with { ProductId = (ushort)(r.ProductId ^ 0x5555) }));
            }
        }

        [Fact]
        public void TheSixFamilies_AreInTheWorkersList_Once()
        {
            foreach (object[] row in Families())
            {
                var family = FamilyOf((string)row[0]);
                Assert.Single(HidLightingFamilies.All, f => ReferenceEquals(f, family));
            }
        }

        /// <summary>1462:3FA4 has two collections, one per family, as
        /// OpenRGB makes a controller on each.</summary>
        [Fact]
        public void TheMsiPair_LightsTheUsagePage1CollectionAsAMonitor_AndTheVendorOneAsAnOptix()
        {
            var monitor = MonitorTest.CollectionOf(Assert.Single(OpenRgbRegistrations.Of("MSIMonitorController")));
            var optix = MonitorTest.CollectionOf(Assert.Single(OpenRgbRegistrations.Of("MSIOptixController")));
            Assert.Equal((ushort)0x0001, monitor.UsagePage);
            Assert.Equal((ushort)0xFF00, optix.UsagePage);
            Assert.Same(MsiMonitorLighting.Instance, HidLightingFamilies.Match(monitor).Family);
            Assert.Same(MsiOptixLighting.Instance, HidLightingFamilies.Match(optix).Family);
            Assert.Null(MsiOptixLighting.Instance.Match(monitor));
            Assert.Null(MsiMonitorLighting.Instance.Match(optix));
        }

        [Fact]
        public void EveryModel_NamesItsVendorsPrograms()
        {
            Assert.Equal(new[] { VendorPrograms.AlienwareCommandCenter },
                Model("AlienwareMonitorController").Programs);
            Assert.Equal(new[] { VendorPrograms.ArmouryCrate }, Model("AsusMonitorController").Programs);
            Assert.Equal(new[] { VendorPrograms.MsiMysticLight }, Model("MSIMonitorController").Programs);
            Assert.Equal(new[] { VendorPrograms.MsiMysticLight }, Model("MSIOptixController").Programs);
            // No source names the process of LG's or ViewSonic's program.
            Assert.Empty(Model("LGMonitorController").Programs);
            Assert.Empty(Model("ViewSonicController").Programs);
            foreach (var program in new[] { VendorPrograms.AlienwareCommandCenter, VendorPrograms.ArmouryCrate, VendorPrograms.MsiMysticLight })
                Assert.Contains(program, VendorPrograms.All);
        }

        private static HidLightingModel Model(string folder)
            => FamilyOf(folder).Match(MonitorTest.CollectionOf(OpenRgbRegistrations.Of(folder).First()));
    }
}
