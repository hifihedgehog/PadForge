using System.Linq;
using Xunit;
using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #315 voice macros: the registry must assign each phrase a STABLE
    /// raw-button index (macros bound to a phrase survive other phrases being
    /// added or removed), and phrase identity is the NORMALIZED text so
    /// near-duplicates cannot collide the way GAVPI's exact-string dispatch
    /// lets them. Tests share the static registry, so each resets it first.
    /// </summary>
    public class VoicePhraseRegistryTests
    {
        private static void Reset() => VoicePhraseRegistry.LoadRegistry(null);

        [Fact]
        public void Register_AssignsSequentialButtonsFromOne()
        {
            Reset();
            VoicePhraseRegistry.Register("reload weapon", "Reload");
            VoicePhraseRegistry.Register("open map", "Map");
            VoicePhraseRegistry.Register("quick save", "Save");
            Assert.Equal(1, VoicePhraseRegistry.ButtonForPhrase("reload weapon"));
            Assert.Equal(2, VoicePhraseRegistry.ButtonForPhrase("open map"));
            Assert.Equal(3, VoicePhraseRegistry.ButtonForPhrase("quick save"));
        }

        [Fact]
        public void Remove_DoesNotRenumberSurvivingPhrases()
        {
            Reset();
            VoicePhraseRegistry.Register("reload weapon", "Reload"); // button 1
            VoicePhraseRegistry.Register("open map", "Map");         // button 2
            VoicePhraseRegistry.Register("quick save", "Save");      // button 3

            VoicePhraseRegistry.Remove("reload weapon");

            // Map and Save keep their buttons. Macros bound to them stay valid.
            Assert.Equal(2, VoicePhraseRegistry.ButtonForPhrase("open map"));
            Assert.Equal(3, VoicePhraseRegistry.ButtonForPhrase("quick save"));
            Assert.Equal(-1, VoicePhraseRegistry.ButtonForPhrase("reload weapon"));
        }

        [Fact]
        public void Register_ReusesLowestFreeButton_AfterRemoval()
        {
            Reset();
            VoicePhraseRegistry.Register("reload weapon", "Reload"); // 1
            VoicePhraseRegistry.Register("open map", "Map");         // 2
            VoicePhraseRegistry.Remove("reload weapon");             // frees 1
            VoicePhraseRegistry.Register("crouch", "Crouch");        // lowest free = 1
            Assert.Equal(1, VoicePhraseRegistry.ButtonForPhrase("crouch"));
            Assert.Equal(2, VoicePhraseRegistry.ButtonForPhrase("open map"));
        }

        [Fact]
        public void Normalize_CollapsesCaseAndWhitespace()
        {
            // The GAVPI lesson: "Reload Weapon" and "reload  weapon" are ONE
            // phrase, or two registrations silently collide in the grammar.
            Assert.Equal("reload weapon", VoicePhraseRegistry.NormalizePhrase("  Reload   WEAPON "));
            Assert.Equal(string.Empty, VoicePhraseRegistry.NormalizePhrase("   "));
            Assert.Equal(string.Empty, VoicePhraseRegistry.NormalizePhrase(null));
        }

        [Fact]
        public void Register_NormalizedDuplicate_UpdatesInsteadOfAdding()
        {
            Reset();
            VoicePhraseRegistry.Register("Reload Weapon", "First");
            VoicePhraseRegistry.Register("reload   weapon", "Second");
            Assert.Equal(1, VoicePhraseRegistry.Count);
            // Same identity keeps its button and takes the new name.
            Assert.Equal(1, VoicePhraseRegistry.ButtonForPhrase("RELOAD WEAPON"));
            Assert.Equal("Second", VoicePhraseRegistry.Phrases.Single().Name);
        }

        [Fact]
        public void Register_DedupesDisplayNames()
        {
            Reset();
            VoicePhraseRegistry.Register("open map", "Action");
            VoicePhraseRegistry.Register("quick save", "Action");
            var names = VoicePhraseRegistry.Phrases.Select(p => p.Name).ToArray();
            Assert.Equal(2, names.Length);
            Assert.NotEqual(names[0], names[1]);
        }

        [Fact]
        public void LoadRegistry_HonorsStoredButtons_AndRepairsCollisions()
        {
            Reset();
            VoicePhraseRegistry.LoadRegistry(new[]
            {
                ("reload weapon", "Reload", 5),   // honored: bindings survive restarts
                ("open map", "Map", 5),           // collides: reassigned lowest free
                ("quick save", "Save", 0),        // out of range: reassigned
            });
            Assert.Equal(5, VoicePhraseRegistry.ButtonForPhrase("reload weapon"));
            Assert.Equal(1, VoicePhraseRegistry.ButtonForPhrase("open map"));
            Assert.Equal(2, VoicePhraseRegistry.ButtonForPhrase("quick save"));
            Assert.Equal(5, VoicePhraseRegistry.MaxButtonInUse);
        }

        [Fact]
        public void SaveRegistry_RoundTrips()
        {
            Reset();
            VoicePhraseRegistry.Register("reload weapon", "Reload");
            VoicePhraseRegistry.Register("open map", "Map");
            var saved = VoicePhraseRegistry.SaveRegistry();
            Reset();
            Assert.Equal(0, VoicePhraseRegistry.Count);
            VoicePhraseRegistry.LoadRegistry(saved.Select(t => (t.Phrase, t.Name, t.Button)));
            Assert.Equal(1, VoicePhraseRegistry.ButtonForPhrase("reload weapon"));
            Assert.Equal(2, VoicePhraseRegistry.ButtonForPhrase("open map"));
        }
    }
}
