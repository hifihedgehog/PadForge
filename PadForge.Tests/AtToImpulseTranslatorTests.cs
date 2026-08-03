using System.IO;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Selective adaptive-trigger to impulse translation (#271 item 3).
    /// Block layouts follow Nielk1's TriggerEffectGenerator
    /// (triggerFactory.h/.cpp, the cloned reference the production table
    /// is grounded in). The owner's constraint is the core contract:
    /// resistance-class programs translate to NOTHING.
    /// </summary>
    public class AtToImpulseTranslatorTests
    {
        private static byte[] Block(byte mode, params (int idx, byte val)[] p)
        {
            var b = new byte[11];
            b[0] = mode;
            foreach (var (idx, val) in p) b[idx] = val;
            return b;
        }

        // ── The mode-class split (the owner's constraint) ──

        [Theory]
        [InlineData(0x00)] // off
        [InlineData(0x05)] // off (official reset)
        [InlineData(0x01)] // Simple_Feedback (resistance)
        [InlineData(0x02)] // Simple_Weapon (resistance)
        [InlineData(0x11)] // Limited_Feedback
        [InlineData(0x12)] // Limited_Weapon
        [InlineData(0x21)] // Feedback (resistance)
        [InlineData(0x22)] // Bow (resistance)
        [InlineData(0x25)] // Weapon (resistance)
        public void ResistanceAndOffModes_TranslateToNothing(byte mode)
        {
            Assert.False(AtToImpulseTranslator.IsVibrationClass(mode));
            // Even with every param byte saturated, output stays 0.
            var b = new byte[11];
            b[0] = mode;
            for (int i = 1; i < 11; i++) b[i] = 0xFF;
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(b, 255, 0));
        }

        [Theory]
        [InlineData(0x06)] // Simple_Vibration
        [InlineData(0x23)] // Galloping
        [InlineData(0x26)] // Vibration
        [InlineData(0x27)] // Machine
        public void VibrationModes_AreVibrationClass(byte mode)
        {
            Assert.True(AtToImpulseTranslator.IsVibrationClass(mode));
        }

        // ── 0x06 Simple_Vibration: [freq@1, amplitude@2, startPos@3] ──

        [Fact]
        public void SimpleVibration_ActiveAboveStartPosition()
        {
            // High frequency renders continuous (no duty gating).
            var b = Block(0x06, (1, 200), (2, 255), (3, 128));
            Assert.Equal(65535, AtToImpulseTranslator.Evaluate(b, 200, 0));
        }

        [Fact]
        public void SimpleVibration_SilentBelowStartPosition()
        {
            var b = Block(0x06, (1, 200), (2, 255), (3, 128));
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(b, 100, 0));
        }

        [Fact]
        public void SimpleVibration_ZeroFrequencyOrAmplitude_IsOff()
        {
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(Block(0x06, (1, 0), (2, 255)), 255, 0));
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(Block(0x06, (1, 30), (2, 0)), 255, 0));
        }

        [Fact]
        public void SimpleVibration_LowFrequencyPulses()
        {
            // 10 Hz -> 100 ms period, 50% duty: on at phase 0, off at 60.
            var b = Block(0x06, (1, 10), (2, 255), (3, 0));
            Assert.NotEqual(0, AtToImpulseTranslator.Evaluate(b, 255, 0));
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(b, 255, 60));
        }

        // ── 0x26 Vibration: zone bitmap + packed 3-bit amplitudes + freq@9 ──

        [Fact]
        public void Vibration_ZoneGatedAmplitude()
        {
            // Zones 5..9 active at wire amplitude 7 (user strength 8/8),
            // freq 100 (continuous). triggerFactory.cpp packs strength
            // (amplitude-1)&7 into 3 bits per zone.
            uint ampZones = 0;
            int activeZones = 0;
            for (int z = 5; z < 10; z++)
            {
                ampZones |= (uint)(7 << (3 * z));
                activeZones |= 1 << z;
            }
            var b = Block(0x26,
                (1, (byte)(activeZones & 0xFF)), (2, (byte)(activeZones >> 8)),
                (3, (byte)(ampZones & 0xFF)), (4, (byte)((ampZones >> 8) & 0xFF)),
                (5, (byte)((ampZones >> 16) & 0xFF)), (6, (byte)((ampZones >> 24) & 0xFF)),
                (9, 100));
            // Trigger at zone 2 (pos 64): inactive zone, silent.
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(b, 64, 0));
            // Trigger at zone 7 (pos 192): active, full amplitude.
            Assert.Equal(65535, AtToImpulseTranslator.Evaluate(b, 192, 0));
        }

        // ── 0x23 Galloping / 0x27 Machine: start/stop bitmap ──

        [Fact]
        public void Galloping_ActiveBetweenStartAndStopZones()
        {
            // Start zone 2, end zone 8, freq 100 (continuous render).
            int zones = (1 << 2) | (1 << 8);
            var b = Block(0x23, (1, (byte)(zones & 0xFF)), (2, (byte)(zones >> 8)), (4, 100));
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(b, 0, 0));       // zone 0: before start
            Assert.NotEqual(0, AtToImpulseTranslator.Evaluate(b, 128, 0));  // zone 5: inside
            Assert.NotEqual(0, AtToImpulseTranslator.Evaluate(b, 220, 0));  // zone 8: end, inclusive
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(b, 255, 0));     // zone 9: past end
        }

        [Fact]
        public void Machine_AlternatesPackedAmplitudes()
        {
            // Zones 0..9, ampA=7 ampB=3 packed at byte 3, freq 10
            // (100 ms half-period alternation).
            int zones = (1 << 0) | (1 << 9);
            byte packed = (7 & 0x07) | ((3 & 0x07) << 3);
            var b = Block(0x27, (1, (byte)(zones & 0xFF)), (2, (byte)(zones >> 8)),
                (3, packed), (4, 10));
            ushort first = AtToImpulseTranslator.Evaluate(b, 128, 0);
            ushort second = AtToImpulseTranslator.Evaluate(b, 128, 150);
            Assert.Equal(65535, first);                    // (7+1)/8
            Assert.Equal((ushort)(65535 * 4 / 8), second); // (3+1)/8
        }

        [Fact]
        public void Machine_ZeroFrequency_IsOff()
        {
            int zones = (1 << 0) | (1 << 9);
            var b = Block(0x27, (1, (byte)(zones & 0xFF)), (2, (byte)(zones >> 8)), (3, 0x3F));
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(b, 128, 0));
        }

        [Fact]
        public void ShortBlock_IsSilent()
        {
            Assert.Equal(0, AtToImpulseTranslator.Evaluate(new byte[5], 255, 0));
        }

        // ── Persistence surface ──

        [Fact]
        public void PadSetting_AtVibrationToImpulse_DefaultsOff()
        {
            Assert.Equal("0", new PadSetting().AtVibrationToImpulseEnabled);
        }

        [Fact]
        public void PadSetting_AtVibrationToImpulse_RoundTripsThroughXml()
        {
            var ps = new PadSetting { AtVibrationToImpulseEnabled = "1" };
            var ser = new XmlSerializer(typeof(PadSetting));
            using var sw = new StringWriter();
            ser.Serialize(sw, ps);
            using var sr = new StringReader(sw.ToString());
            var back = (PadSetting)ser.Deserialize(sr);
            Assert.Equal("1", back.AtVibrationToImpulseEnabled);
        }

        [Fact]
        public void PadSetting_AtVibrationToImpulse_ChangesChecksum()
        {
            var off = new PadSetting { AtVibrationToImpulseEnabled = "0" };
            var on = new PadSetting { AtVibrationToImpulseEnabled = "1" };
            Assert.NotEqual(off.ComputeChecksum(), on.ComputeChecksum());
        }
    }
}
