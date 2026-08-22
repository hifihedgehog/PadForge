using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The three Valve composite personas ship in HIDMaestro.Core but are
    /// not offered yet (#338, milestone v4.4.0).
    ///
    /// <para>They were reachable in the Extended dropdown, verified against
    /// the live catalog before this gate went in: all three resolved and
    /// all three reported inExtended=true. The block is artwork, not code.
    /// PadForge has no 3D mesh for any Valve device and no 2D set for the
    /// 2026 Steam Controller, so selecting one produced a working device
    /// drawn as something else.</para>
    /// </summary>
    public class WithheldProfileTests
    {
        public static readonly string[] Withheld =
        {
            "steam-deck-composite",
            "steam-controller-composite",
            "steam-controller-2",
        };

        /// <summary>Absent from every bucket, not merely from Extended. A
        /// profile that survives in AllProfiles is still reachable by id
        /// and still lands in whatever picker reads that list.</summary>
        [Fact]
        public void WithheldPersonas_AppearInNoBucket()
        {
            foreach (var id in Withheld)
            {
                Assert.DoesNotContain(HMaestroProfileCatalog.AllProfiles, p => p.Id == id);
                Assert.DoesNotContain(HMaestroProfileCatalog.ExtendedProfiles, p => p.Id == id);
                Assert.DoesNotContain(HMaestroProfileCatalog.XboxProfiles, p => p.Id == id);
                Assert.DoesNotContain(HMaestroProfileCatalog.PlayStationProfiles, p => p.Id == id);
                Assert.DoesNotContain(HMaestroProfileCatalog.NintendoProfiles, p => p.Id == id);
            }
        }

        /// <summary>The plain Valve profiles are NOT withheld. They predate
        /// this work and users already have them, so a gate that swept the
        /// whole vendor would be a removal rather than a hold.</summary>
        [Theory]
        [InlineData("steam-controller")]
        [InlineData("steam-deck")]
        public void PlainValveProfiles_StayAvailable(string id)
        {
            Assert.Contains(HMaestroProfileCatalog.AllProfiles, p => p.Id == id);
            Assert.Contains(HMaestroProfileCatalog.ExtendedProfiles, p => p.Id == id);
        }

        /// <summary>The gate is exactly three ids. A typo or a broadened
        /// match would silently take working profiles away, which is the
        /// failure this asserts against.</summary>
        [Theory]
        [InlineData("steam-deck-composite", true)]
        [InlineData("steam-controller-composite", true)]
        [InlineData("steam-controller-2", true)]
        [InlineData("steam-controller", false)]
        [InlineData("steam-deck", false)]
        [InlineData("steam-controller-re", false)]
        [InlineData("hori-horipad-steam", false)]
        [InlineData("dualsense-composite", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void TheGateMatchesExactIds(string id, bool withheld)
        {
            Assert.Equal(withheld, HMaestroProfileCatalog.IsWithheldProfile(id));
        }

        /// <summary>Every other profile the library ships still loads. The
        /// gate must cost three entries and nothing else.</summary>
        [Fact]
        public void TheCatalogKeepsEverythingElse()
        {
            Assert.True(HMaestroProfileCatalog.AllProfiles.Count > 100);
            Assert.All(HMaestroProfileCatalog.AllProfiles,
                p => Assert.False(HMaestroProfileCatalog.IsWithheldProfile(p.Id)));
        }
    }
}
