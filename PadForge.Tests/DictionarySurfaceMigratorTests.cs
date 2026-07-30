using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The MIDI and KBM mapping surfaces are dictionary siblings of the
    /// raw surface, and the legacy-to-MappingSet migrator must carry all
    /// three. The save pipeline regenerates every device's PadSetting
    /// from the grid view, which rebuilds from the migrated set, so any
    /// surface the migrator drops gets WIPED on the next save (the MIDI
    /// automap "didn't stick", 2026-07-23). These tests pin the lanes.
    /// </summary>
    public class DictionarySurfaceMigratorTests
    {
        private const string Dev = "11111111-1111-1111-1111-111111111111";

        private static MappingSet Build(PadSetting ps)
            => MappingSetMigrator.BuildFromLegacy(0, new[]
            {
                (DeviceGuid: Dev, PadSetting: ps, IsGamepadEligible: true),
            });

        [Fact]
        public void MidiAutomapSurvivesMigration()
        {
            var ps = new PadSetting();
            for (int i = 0; i < 6; i++)
                ps.SetMidiMapping($"MidiCC{i}", $"Axis {i}");
            for (int i = 0; i < 11; i++)
                ps.SetMidiMapping($"MidiNote{i}", $"Button {i}");
            ps.FlushMidiMappings();

            var ms = Build(ps);

            for (int i = 0; i < 6; i++)
            {
                var row = Assert.Single(ms.Rows, r => r.Target == $"MidiCC{i}");
                var src = Assert.Single(row.Sources);
                Assert.Equal($"Axis {i}", src.Descriptor);
                Assert.Equal(Dev, src.DeviceGuid);
            }
            for (int i = 0; i < 11; i++)
            {
                var row = Assert.Single(ms.Rows, r => r.Target == $"MidiNote{i}");
                Assert.Equal($"Button {i}", Assert.Single(row.Sources).Descriptor);
            }
        }

        [Fact]
        public void MidiCcNegLegFoldsIntoBipolarRow()
        {
            var ps = new PadSetting();
            ps.SetMidiMapping("MidiCC0", "Button 4");
            ps.SetMidiMapping("MidiCC0Neg", "Button 5");
            ps.FlushMidiMappings();

            var ms = Build(ps);

            var row = Assert.Single(ms.Rows, r => r.Target == "MidiCC0");
            Assert.Equal(2, row.Sources.Count);
            var neg = Assert.Single(row.Sources, s => s.Descriptor == "Button 5");
            // Same polarity rule as every bipolar Neg leg: the button read
            // isn't half-axis-consumed, so the sign flip lands on Invert.
            Assert.True(neg.Invert || neg.InvertOutput);
        }

        [Fact]
        public void KbmMappingsSurviveMigration()
        {
            var ps = new PadSetting();
            ps.SetKbmMapping("KbmKey41", "Button 0");
            ps.SetKbmMapping("KbmMBtn0", "Button 1");
            ps.SetKbmMapping("KbmMouseX", "Axis 0");
            ps.SetKbmMapping("KbmMouseXNeg", "Axis 1");
            ps.SetKbmMapping("KbmScroll", "Axis 4");
            ps.FlushKbmMappings();

            var ms = Build(ps);

            Assert.Single(ms.Rows, r => r.Target == "KbmKey41");
            Assert.Single(ms.Rows, r => r.Target == "KbmMBtn0");
            Assert.Single(ms.Rows, r => r.Target == "KbmScroll");
            var mouseX = Assert.Single(ms.Rows, r => r.Target == "KbmMouseX");
            Assert.Equal(2, mouseX.Sources.Count);
        }

        [Fact]
        public void NonTargetDictionaryKeysDoNotEmitRows()
        {
            // The dictionaries also carry tuning keys; only exact target
            // grammar translates (same contract as the raw surface).
            var ps = new PadSetting();
            ps.SetMidiMapping("MidiChannelHint", "3");
            ps.SetKbmMapping("KbmSensitivity", "1.5");
            ps.FlushMidiMappings();
            ps.FlushKbmMappings();

            var ms = Build(ps);

            Assert.DoesNotContain(ms.Rows, r => r.Target == "MidiChannelHint");
            Assert.DoesNotContain(ms.Rows, r => r.Target == "KbmSensitivity");
        }
    }
}
