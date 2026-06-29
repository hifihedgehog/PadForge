using System.Linq;
using Xunit;
using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #150 per-tag binding: the registry must assign each tag UID a STABLE
    /// raw-button index, so a macro bound to a tag keeps firing it even after other
    /// tags are added or removed. (The same "stable identity, not list position"
    /// lesson as the touchpad contact-ID fix and the #128 MIDI note binding.)
    /// Tests share the static registry, so each resets it first.
    /// </summary>
    public class NfcTagRegistryTests
    {
        private static void Reset() => NfcTagRegistry.LoadRegistry(null);

        [Fact]
        public void Register_AssignsSequentialButtonsFromOne()
        {
            Reset();
            NfcTagRegistry.Register("04A1", "A");
            NfcTagRegistry.Register("04B2", "B");
            NfcTagRegistry.Register("04C3", "C");
            Assert.Equal(1, NfcTagRegistry.ButtonForUid("04A1"));
            Assert.Equal(2, NfcTagRegistry.ButtonForUid("04B2"));
            Assert.Equal(3, NfcTagRegistry.ButtonForUid("04C3"));
        }

        [Fact]
        public void Remove_DoesNotRenumberSurvivingTags()
        {
            Reset();
            NfcTagRegistry.Register("04A1", "A"); // button 1
            NfcTagRegistry.Register("04B2", "B"); // button 2
            NfcTagRegistry.Register("04C3", "C"); // button 3

            NfcTagRegistry.Remove("04A1");

            // B and C keep their buttons -- macros bound to them stay valid.
            Assert.Equal(2, NfcTagRegistry.ButtonForUid("04B2"));
            Assert.Equal(3, NfcTagRegistry.ButtonForUid("04C3"));
            Assert.Equal(-1, NfcTagRegistry.ButtonForUid("04A1"));
        }

        [Fact]
        public void Register_ReusesLowestFreeButton_AfterRemoval()
        {
            Reset();
            NfcTagRegistry.Register("04A1", "A"); // 1
            NfcTagRegistry.Register("04B2", "B"); // 2
            NfcTagRegistry.Remove("04A1");        // frees 1
            NfcTagRegistry.Register("04D4", "D"); // takes lowest free = 1
            Assert.Equal(1, NfcTagRegistry.ButtonForUid("04D4"));
            Assert.Equal(2, NfcTagRegistry.ButtonForUid("04B2"));
        }

        [Fact]
        public void Reregister_SameUid_KeepsButton_UpdatesName()
        {
            Reset();
            NfcTagRegistry.Register("04A1", "A"); // 1
            NfcTagRegistry.Register("04B2", "B"); // 2
            string name = NfcTagRegistry.Register("04B2", "Renamed");
            Assert.Equal("Renamed", name);
            Assert.Equal(2, NfcTagRegistry.ButtonForUid("04B2")); // button unchanged
        }

        [Fact]
        public void NormalizeUid_UppercasesAndStripsSeparators()
        {
            Assert.Equal("04A1B2C3", NfcTagRegistry.NormalizeUid("04 a1:b2-c3"));
            // A tap's UID and the stored UID both normalise, so they compare equal.
            Reset();
            NfcTagRegistry.Register("04 a1 b2 c3", "Card");
            Assert.Equal(1, NfcTagRegistry.ButtonForUid("04a1b2c3"));
        }

        [Fact]
        public void SaveLoad_RoundTripsButtons()
        {
            Reset();
            NfcTagRegistry.Register("04A1", "A"); // 1
            NfcTagRegistry.Register("04B2", "B"); // 2
            NfcTagRegistry.Remove("04A1");        // B stays at 2
            var saved = NfcTagRegistry.SaveRegistry();

            NfcTagRegistry.LoadRegistry(saved);
            Assert.Equal(2, NfcTagRegistry.ButtonForUid("04B2")); // stable across save/load
            Assert.Equal(2, NfcTagRegistry.Tags.Single().Button);
        }

        [Fact]
        public void Load_ReassignsCollidingOrInvalidButtons()
        {
            Reset();
            // Two tags claim button 5; an out-of-range button. Loader must repair.
            NfcTagRegistry.LoadRegistry(new (string, string, int)[]
            {
                ("04A1", "A", 5),
                ("04B2", "B", 5),     // collision -> reassigned
                ("04C3", "C", 9999),  // out of range -> reassigned
            });
            var buttons = NfcTagRegistry.Tags.Select(t => t.Button).OrderBy(b => b).ToList();
            Assert.Equal(3, buttons.Count);
            Assert.Equal(buttons.Count, buttons.Distinct().Count()); // all unique
            Assert.All(buttons, b => Assert.InRange(b, 1, 255));
        }
    }
}
