using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>Every OpenRGB registration of the HyperX peripheral and
    /// mouse families ported in wave 4 lane W1 answers with a model of its
    /// family through the shared worker's family list, by the generated
    /// registration table (#557), so a registration a family misses
    /// fails.</summary>
    public class HyperXAndMouseFamilyRegistrationTests
    {
        /// <summary>Each family by its OpenRGB folder, with its registration
        /// count at the pinned commit.</summary>
        public static IEnumerable<object[]> Families() => new[]
        {
            new object[] { "HyperXMouseController", 12 },
            new object[] { "HyperXMousematController", 3 },
            new object[] { "HyperXMicrophoneController", 8 },
            new object[] { "HyperXMicrophoneV2Controller", 1 },
            // The M901-K-2 Perdition came with OpenRGB fc3aa079, and the M901P-KS, wired and on
            // its dongle, went to the Areson family.
            new object[] { "RedragonController", 12 },
            new object[] { "AresonController", 8 },
            new object[] { "InstantMouseController", 4 },
            new object[] { "HoltekController", 2 },
        };

        private static IHidLightingFamily FamilyOf(string folder) => folder switch
        {
            "HyperXMouseController" => HyperXMouseLighting.Instance,
            "HyperXMousematController" => HyperXMousematLighting.Instance,
            "HyperXMicrophoneController" => HyperXMicrophoneLighting.Instance,
            "HyperXMicrophoneV2Controller" => HyperXMicrophoneV2Lighting.Instance,
            "RedragonController" => RedragonMouseLighting.Instance,
            "AresonController" => AresonLighting.Instance,
            "InstantMouseController" => InstantMouseLighting.Instance,
            "HoltekController" => HoltekLighting.Instance,
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
                // Report lengths long enough for every family's frames.
                var collection = CollectionFor(r, output: 512, feature: 512, input: 64);
                var model = family.Match(collection);
                Assert.True(model != null, $"{folder} {r.Name} {r.ProductId:X4}");
                Assert.Same(family, model.Family);
                Assert.Equal(r.Name, model.Name);
                Assert.True(model.Map.Lights.Count > 0);
                Assert.NotNull(model.Map.Product);
                Assert.NotNull(model.Begin);
                // A product's collections answer with one model.
                Assert.Same(model, family.Match(collection));
                // The shared worker reaches it through the family list.
                Assert.Same(model, HidLightingFamilies.Match(collection));
                // Another vendor's collection with the same fields is not it.
                Assert.Null(family.Match(collection with { VendorId = (ushort)(r.VendorId ^ 0x5555) }));
            }
        }

        [Fact]
        public void TheEightFamilies_AreInTheWorkersList_Once()
        {
            foreach (object[] row in Families())
            {
                var family = FamilyOf((string)row[0]);
                Assert.Single(HidLightingFamilies.All, f => ReferenceEquals(f, family));
            }
        }
    }
}
