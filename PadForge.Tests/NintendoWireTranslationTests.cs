using PadForge.Models2D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Raw targets are WIRE-RELATIVE. The two Switch families share almost no
    /// indices, so a binding that keeps its index across a profile change
    /// silently changes which button it presses: RawBtn8 is Minus on the
    /// original Pro Controller and the D-pad's Down on the Switch 2 Pro.
    /// TranslateRawTarget moves bindings by ROLE instead.
    /// </summary>
    public class NintendoWireTranslationTests
    {
        private const string S1 = "switch-pro";
        private const string S2 = "switch2-pro-controller";

        /// <summary>Round-tripping any role through both wires lands back
        /// where it started, which is the property that makes switching the
        /// profile back and forth lossless for shared controls.</summary>
        [Theory]
        [InlineData("RawBtn0")]    // B
        [InlineData("RawBtn1")]    // A
        [InlineData("RawBtn2")]    // Y
        [InlineData("RawBtn3")]    // X
        [InlineData("RawBtn4")]    // L
        [InlineData("RawBtn5")]    // R
        [InlineData("RawBtn6")]    // ZL
        [InlineData("RawBtn7")]    // ZR
        [InlineData("RawBtn8")]    // Minus
        [InlineData("RawBtn9")]    // Plus
        [InlineData("RawBtn10")]   // LS
        [InlineData("RawBtn11")]   // RS
        [InlineData("RawBtn12")]   // Home
        [InlineData("RawBtn13")]   // Capture
        public void SharedControls_RoundTripThroughBothWires(string s1Target)
        {
            string onS2 = NintendoPreviewMap.TranslateRawTarget(s1Target, S1, S2);
            Assert.NotNull(onS2);
            Assert.Equal(s1Target, NintendoPreviewMap.TranslateRawTarget(onS2, S2, S1));
        }

        /// <summary>The four the owner named. Each keeps its meaning and
        /// changes index, which is the whole point.</summary>
        [Theory]
        [InlineData("RawBtn8", "RawBtn14")]    // Minus
        [InlineData("RawBtn9", "RawBtn6")]     // Plus
        [InlineData("RawBtn12", "RawBtn16")]   // Home
        [InlineData("RawBtn13", "RawBtn17")]   // Capture
        public void CarryOverSystemButtons_MoveToTheirSwitch2Index(string from, string to)
        {
            Assert.Equal(to, NintendoPreviewMap.TranslateRawTarget(from, S1, S2));
        }

        /// <summary>A hat-encoded D-pad becomes four discrete buttons going
        /// one way, and back to the hat coming the other. A binding must not
        /// be dropped just because the encoding differs.</summary>
        [Theory]
        [InlineData("RawPov0Up", "RawBtn11")]
        [InlineData("RawPov0Down", "RawBtn8")]
        [InlineData("RawPov0Left", "RawBtn10")]
        [InlineData("RawPov0Right", "RawBtn9")]
        public void DPad_CrossesBetweenHatAndButtons(string pov, string btn)
        {
            Assert.Equal(btn, NintendoPreviewMap.TranslateRawTarget(pov, S1, S2));
            Assert.Equal(pov, NintendoPreviewMap.TranslateRawTarget(btn, S2, S1));
        }

        /// <summary>Controls the target pad does not have are dropped, not
        /// pointed at wire that is not there.</summary>
        [Theory]
        [InlineData("RawBtn18")]   // GR
        [InlineData("RawBtn19")]   // GL
        [InlineData("RawBtn20")]   // C
        public void Switch2OnlyControls_DropWhenMovingToTheOriginal(string target)
        {
            Assert.Null(NintendoPreviewMap.TranslateRawTarget(target, S2, S1));
        }

        /// <summary>Axis and tuning keys are wire-independent and must pass
        /// through untouched, or a profile change would wipe every stick
        /// binding and deadzone along with the buttons.</summary>
        [Theory]
        [InlineData("RawAxis0")]
        [InlineData("RawAxis1Neg")]
        [InlineData("RawAxis3")]
        [InlineData("RawStick0Dz")]
        [InlineData("")]
        [InlineData(null)]
        public void WireIndependentKeys_PassThrough(string key)
        {
            Assert.Equal(key, NintendoPreviewMap.TranslateRawTarget(key, S1, S2));
            Assert.Equal(key, NintendoPreviewMap.TranslateRawTarget(key, S2, S1));
        }

        /// <summary>Same wire on both sides is a no-op, so re-selecting the
        /// profile a slot already has cannot disturb its mappings.</summary>
        [Theory]
        [InlineData("RawBtn8")]
        [InlineData("RawPov0Up")]
        [InlineData("RawAxis2")]
        public void SameWire_IsANoOp(string key)
        {
            Assert.Equal(key, NintendoPreviewMap.TranslateRawTarget(key, S1, S1));
            Assert.Equal(key, NintendoPreviewMap.TranslateRawTarget(key, S2, S2));
        }

        /// <summary>Every one of the original's 14 buttons survives the move
        /// to the Switch 2 wire, and no two land on the same index.</summary>
        [Fact]
        public void EverySwitchProButton_LandsSomewhereDistinctOnSwitch2()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 14; i++)
            {
                string dst = NintendoPreviewMap.TranslateRawTarget($"RawBtn{i}", S1, S2);
                Assert.True(dst != null, $"RawBtn{i} was dropped");
                Assert.True(seen.Add(dst), $"two originals collided on {dst}");
            }
        }
    }
}
