using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #240 (button SOCD for virtual controllers): the
    /// SlotButtonSocd adapter over #205's hitboxer-grounded StepPair, on
    /// both output surfaces, plus the MappingSet persistence contract.
    /// StepPair's own semantics (LastWins / Neutral / FirstWins, re-press
    /// on winner release) are pinned by the #205 suite; these tests pin
    /// the pair grammar, the bit surfaces, and the config lanes.
    /// </summary>
    public class SlotButtonSocdTests
    {
        // ── Gamepad mask surface ──

        [Fact]
        public void LastWins_SecondPressSuppressesFirst_ReleaseRepressesPartner()
        {
            var socd = new SlotButtonSocd();
            socd.Configure("LastWins", "ButtonA:ButtonB", extendedIndices: false);

            // A held alone passes through.
            Assert.Equal(Gamepad.A, socd.ApplyGamepad(Gamepad.A));
            // B joins: B is the later press, A is suppressed.
            Assert.Equal(Gamepad.B, socd.ApplyGamepad((ushort)(Gamepad.A | Gamepad.B)));
            // B releases with A still held: A re-presses the same frame.
            Assert.Equal(Gamepad.A, socd.ApplyGamepad(Gamepad.A));
        }

        [Fact]
        public void Neutral_BothHeldCancels()
        {
            var socd = new SlotButtonSocd();
            socd.Configure("Neutral", "DPadLeft:DPadRight", extendedIndices: false);
            Assert.Equal(0, socd.ApplyGamepad((ushort)(Gamepad.DPAD_LEFT | Gamepad.DPAD_RIGHT)));
            Assert.Equal(Gamepad.DPAD_LEFT, socd.ApplyGamepad(Gamepad.DPAD_LEFT));
        }

        [Fact]
        public void UnpairedButtons_PassThroughUntouched()
        {
            var socd = new SlotButtonSocd();
            socd.Configure("LastWins", "ButtonA:ButtonB", extendedIndices: false);
            ushort state = (ushort)(Gamepad.A | Gamepad.B | Gamepad.X | Gamepad.LEFT_SHOULDER);
            ushort cleaned = socd.ApplyGamepad(state);
            Assert.Equal(Gamepad.X, cleaned & Gamepad.X);
            Assert.Equal(Gamepad.LEFT_SHOULDER, cleaned & Gamepad.LEFT_SHOULDER);
            Assert.Equal(0, cleaned & Gamepad.A);   // pair member, suppressed
        }

        [Fact]
        public void OffMode_And_EmptyPairs_AreInactive()
        {
            var socd = new SlotButtonSocd();
            socd.Configure("Off", "ButtonA:ButtonB", extendedIndices: false);
            Assert.False(socd.IsActive);
            socd.Configure("LastWins", "", extendedIndices: false);
            Assert.False(socd.IsActive);
        }

        [Theory]
        [InlineData("ButtonA:ButtonA")]      // self pair
        [InlineData("ButtonA")]              // no colon
        [InlineData("Nope:ButtonB")]         // unknown name
        [InlineData(":ButtonB")]             // empty side
        public void MalformedPairs_AreDropped(string pairs)
        {
            var socd = new SlotButtonSocd();
            socd.Configure("LastWins", pairs, extendedIndices: false);
            Assert.False(socd.IsActive);
        }

        [Fact]
        public void IdenticalReconfigure_KeepsWinnerState()
        {
            var socd = new SlotButtonSocd();
            socd.Configure("LastWins", "ButtonA:ButtonB", extendedIndices: false);
            socd.ApplyGamepad(Gamepad.A);
            socd.ApplyGamepad((ushort)(Gamepad.A | Gamepad.B)); // B wins
            // The per-tick refresh re-sends identical config.
            socd.Configure("LastWins", "ButtonA:ButtonB", extendedIndices: false);
            // Still both held: B keeps the win (state survived).
            Assert.Equal(Gamepad.B, socd.ApplyGamepad((ushort)(Gamepad.A | Gamepad.B)));
        }

        [Fact]
        public void ResolveGamepadMask_MatchesTheWriteBoolTargetVocabulary()
        {
            Assert.Equal(Gamepad.A, SlotButtonSocd.ResolveGamepadMask("ButtonA"));
            Assert.Equal(Gamepad.DPAD_UP, SlotButtonSocd.ResolveGamepadMask("DPadUp"));
            Assert.Equal(Gamepad.LEFT_THUMB, SlotButtonSocd.ResolveGamepadMask("LeftThumbButton"));
            Assert.Equal(Gamepad.GUIDE, SlotButtonSocd.ResolveGamepadMask("ButtonGuide"));
            Assert.Equal(0, SlotButtonSocd.ResolveGamepadMask("ButtonShare")); // not maskable
            Assert.Equal(0, SlotButtonSocd.ResolveGamepadMask(""));
        }

        // ── Extended flat-index surface ──

        [Fact]
        public void Extended_LastWins_WorksAcrossWords()
        {
            var socd = new SlotButtonSocd();
            socd.Configure("LastWins", "3:35", extendedIndices: true);

            var words = new uint[4];
            words[0] = 1u << 3;                    // A held
            socd.ApplyExtended(words);
            Assert.Equal(1u << 3, words[0]);

            words[0] = 1u << 3;
            words[1] = 1u << 3;                    // flat 35 joins (word 1 bit 3)
            socd.ApplyExtended(words);
            Assert.Equal(0u, words[0]);            // earlier press suppressed
            Assert.Equal(1u << 3, words[1]);

            words[0] = 1u << 3;
            words[1] = 0;                          // 35 released, 3 still held
            socd.ApplyExtended(words);
            Assert.Equal(1u << 3, words[0]);       // re-pressed
        }

        // ── MappingSet persistence ──

        [Fact]
        public void SocdFields_CountAsAuthoredContent()
        {
            Assert.False(new MappingSet().HasAuthoredContent);
            Assert.True(new MappingSet { SocdMode = "LastWins" }.HasAuthoredContent);
            Assert.True(new MappingSet { SocdPairs = "ButtonA:ButtonB" }.HasAuthoredContent);
        }

        [Fact]
        public void SocdFields_SurviveXmlRoundTrip()
        {
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(MappingSet));
            using var sw = new System.IO.StringWriter();
            ser.Serialize(sw, new MappingSet { SocdMode = "Neutral", SocdPairs = "DPadLeft:DPadRight|ButtonA:ButtonB" });
            using var sr = new System.IO.StringReader(sw.ToString());
            var back = (MappingSet)ser.Deserialize(sr);
            Assert.Equal("Neutral", back.SocdMode);
            Assert.Equal("DPadLeft:DPadRight|ButtonA:ButtonB", back.SocdPairs);
        }
    }
}
