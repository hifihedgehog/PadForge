using System.Collections.Generic;
using System.Text.RegularExpressions;
using PadForge.Models2D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the preview-grammar to raw-grid translation that makes
    /// click-to-record, the recording flash, and annotation chips work on
    /// Nintendo slots. The button-index correspondence must stay in
    /// lockstep with PadViewModel.UpdateNintendoPreviewFromRaw (state to
    /// preview) and InitializeRawSurfaceMappings (the grid rows).
    ///
    /// Two wire orders, and they are NOT one table with the other's tail:
    ///   switch-pro   B A Y X, L R, ZL ZR, Minus Plus, LS RS, Home, Capture
    ///                = raw 0..13, D-pad on hat 0.
    ///   switch2-pro  B A Y X, R ZR, Plus RS, D-pad(4), L ZL, Minus LS,
    ///                Home Capture, GR GL, C = raw 0..20, no hat.
    /// </summary>
    public class NintendoPreviewMapTests
    {
        private const string S1 = "switch-pro";
        private const string S2 = "switch2-pro-controller";

        [Theory]
        [InlineData("ButtonB", "RawBtn0")]
        [InlineData("ButtonA", "RawBtn1")]
        [InlineData("ButtonY", "RawBtn2")]
        [InlineData("ButtonX", "RawBtn3")]
        [InlineData("LeftShoulder", "RawBtn4")]
        [InlineData("RightShoulder", "RawBtn5")]
        [InlineData("LeftTrigger", "RawBtn6")]
        [InlineData("RightTrigger", "RawBtn7")]
        [InlineData("ButtonBack", "RawBtn8")]
        [InlineData("ButtonStart", "RawBtn9")]
        [InlineData("LeftThumbButton", "RawBtn10")]
        [InlineData("RightThumbButton", "RawBtn11")]
        [InlineData("ButtonGuide", "RawBtn12")]
        [InlineData("ButtonShare", "RawBtn13")]
        [InlineData("LeftThumbAxisX", "RawAxis0")]
        [InlineData("LeftThumbAxisY", "RawAxis1")]
        [InlineData("RightThumbAxisX", "RawAxis2")]
        [InlineData("RightThumbAxisY", "RawAxis3")]
        [InlineData("LeftThumbAxisXNeg", "RawAxis0Neg")]
        [InlineData("LeftThumbAxisYNeg", "RawAxis1Neg")]
        [InlineData("RightThumbAxisXNeg", "RawAxis2Neg")]
        [InlineData("RightThumbAxisYNeg", "RawAxis3Neg")]
        [InlineData("DPadUp", "RawPov0Up")]
        [InlineData("DPadDown", "RawPov0Down")]
        [InlineData("DPadLeft", "RawPov0Left")]
        [InlineData("DPadRight", "RawPov0Right")]
        public void SwitchPro_ToRaw_And_ToPreview_AreInverse(string preview, string raw)
        {
            Assert.Equal(raw, NintendoPreviewMap.ToRaw(preview, S1));
            Assert.Equal(preview, NintendoPreviewMap.ToPreview(raw, S1));
        }

        /// <summary>The Switch 2 Pro wire, byte 3 then 4 then 5 of its report
        /// 0x09 button masks. Eleven of these disagree with the original's
        /// table at the same index, which is why the map forks by profile.</summary>
        [Theory]
        [InlineData("ButtonB", "RawBtn0")]
        [InlineData("ButtonA", "RawBtn1")]
        [InlineData("ButtonY", "RawBtn2")]
        [InlineData("ButtonX", "RawBtn3")]
        [InlineData("RightShoulder", "RawBtn4")]
        [InlineData("RightTrigger", "RawBtn5")]
        [InlineData("ButtonStart", "RawBtn6")]
        [InlineData("RightThumbButton", "RawBtn7")]
        [InlineData("DPadDown", "RawBtn8")]
        [InlineData("DPadRight", "RawBtn9")]
        [InlineData("DPadLeft", "RawBtn10")]
        [InlineData("DPadUp", "RawBtn11")]
        [InlineData("LeftShoulder", "RawBtn12")]
        [InlineData("LeftTrigger", "RawBtn13")]
        [InlineData("ButtonBack", "RawBtn14")]
        [InlineData("LeftThumbButton", "RawBtn15")]
        [InlineData("ButtonGuide", "RawBtn16")]
        [InlineData("ButtonShare", "RawBtn17")]
        [InlineData("RightPaddle", "RawBtn18")]
        [InlineData("LeftPaddle", "RawBtn19")]
        [InlineData("ButtonC", "RawBtn20")]
        public void Switch2Pro_ToRaw_And_ToPreview_AreInverse(string preview, string raw)
        {
            Assert.Equal(raw, NintendoPreviewMap.ToRaw(preview, S2));
            Assert.Equal(preview, NintendoPreviewMap.ToPreview(raw, S2));
        }

        /// <summary>The Switch 2 Pro spends four real buttons on the D-pad
        /// and declares no hat, so a POV target must never be produced for
        /// it. Emitting one would map input onto wire the descriptor does
        /// not have.</summary>
        [Theory]
        [InlineData("DPadUp")]
        [InlineData("DPadDown")]
        [InlineData("DPadLeft")]
        [InlineData("DPadRight")]
        public void Switch2Pro_DPad_IsButtonsNotHat(string preview)
        {
            string raw = NintendoPreviewMap.ToRaw(preview, S2);
            Assert.StartsWith("RawBtn", raw);
            Assert.DoesNotContain("Pov", raw);
        }

        /// <summary>C / GL / GR are drawn on the shared Switch Pro art but
        /// exist only on the Switch 2 wire. On an original Pro Controller
        /// they must resolve to nothing, the same posture their 3D meshes
        /// have had since the mesh was adopted: rendered, never addressed.</summary>
        [Theory]
        [InlineData("ButtonC")]
        [InlineData("LeftPaddle")]
        [InlineData("RightPaddle")]
        public void Switch2OnlyControls_AreInertOnOriginalSwitchPro(string preview)
        {
            Assert.Null(NintendoPreviewMap.ToRaw(preview, S1));
            Assert.NotNull(NintendoPreviewMap.ToRaw(preview, S2));
        }

        [Theory]
        [InlineData("LeftTriggerBase")]   // rest-art silhouette, not a control
        [InlineData("RightTriggerBase")]
        [InlineData("LeftThumbRing")]     // rings emit quadrant axis names
        [InlineData("RightThumbRing")]
        [InlineData("TouchpadClick")]     // no touchpad on the raw surface
        [InlineData("ButtonBNeg")]        // Neg is an axis-only suffix
        [InlineData("")]
        [InlineData(null)]
        public void ToRaw_UnmappableElements_ReturnNull(string preview)
        {
            Assert.Null(NintendoPreviewMap.ToRaw(preview, S1));
            Assert.Null(NintendoPreviewMap.ToRaw(preview, S2));
        }

        [Theory]
        [InlineData("RawBtn14")]          // roleless Joy-Con rail bits
        [InlineData("RawBtn17")]
        [InlineData("RawAxis4")]          // beyond the four stick axes
        [InlineData("RawAxis4Neg")]
        [InlineData("RawPov1Up")]         // only hat 0 exists on the art
        [InlineData("RawStick0Dz")]       // tuning keys, not targets
        [InlineData("")]
        [InlineData(null)]
        public void ToPreview_OutOfRangeRawNames_ReturnNull(string raw)
        {
            Assert.Null(NintendoPreviewMap.ToPreview(raw, S1));
        }

        [Theory]
        [InlineData("RawBtn21")]          // one past the Switch 2 wire
        [InlineData("RawBtn99")]
        [InlineData("RawAxis4")]
        [InlineData("RawStick0Dz")]
        public void Switch2Pro_ToPreview_OutOfRangeRawNames_ReturnNull(string raw)
        {
            Assert.Null(NintendoPreviewMap.ToPreview(raw, S2));
        }

        /// <summary>An unknown or null profile falls back to the original
        /// Pro Controller's table rather than throwing or silently picking
        /// the longer one, so a slot mid-initialisation cannot address wire
        /// the pad may not have.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("dualsense")]
        public void UnknownProfile_FallsBackToSwitchProTable(string profileId)
        {
            Assert.Equal("RawBtn4", NintendoPreviewMap.ToRaw("LeftShoulder", profileId));
            Assert.Null(NintendoPreviewMap.ToRaw("ButtonC", profileId));
        }

        /// <summary>Every clickable overlay on the shared art resolves to a
        /// well-formed, distinct raw target under the Switch 2 Pro profile,
        /// which is the one whose wire covers the whole drawing. StickRings
        /// are exempt (clicks route through the quadrant emitter) and
        /// TriggerBases are rest art.</summary>
        [Fact]
        public void EveryClickableOverlay_ResolvesToDistinctRawTarget_OnSwitch2()
        {
            var grammar = new Regex(@"^(RawBtn(\d+)|RawAxis[0-3](Neg)?)$");
            var seen = new HashSet<string>();
            foreach (var ov in SwitchProLayout.Overlays)
            {
                if (ov.ElementType is OverlayElementType.TriggerBase
                    or OverlayElementType.StickRing)
                    continue;
                string raw = NintendoPreviewMap.ToRaw(ov.TargetName, S2);
                Assert.True(raw != null,
                    $"overlay '{ov.TargetName}' has no raw counterpart");
                Assert.Matches(grammar, raw);
                Assert.True(seen.Add(raw),
                    $"raw target '{raw}' claimed by two overlays");
            }
            // The full Switch 2 lettered set is reachable from the art.
            for (int i = 0; i < 21; i++)
                Assert.Contains($"RawBtn{i}", (IEnumerable<string>)seen);
        }

        /// <summary>The original Pro Controller reaches its own 14 the same
        /// way, over the hat rather than four D-pad buttons.</summary>
        [Fact]
        public void EveryClickableOverlay_ResolvesToDistinctRawTarget_OnSwitchPro()
        {
            var grammar = new Regex(
                @"^(RawBtn(\d+)|RawAxis[0-3](Neg)?|RawPov0(Up|Down|Left|Right))$");
            var seen = new HashSet<string>();
            foreach (var ov in SwitchProLayout.Overlays)
            {
                if (ov.ElementType is OverlayElementType.TriggerBase
                    or OverlayElementType.StickRing)
                    continue;
                string raw = NintendoPreviewMap.ToRaw(ov.TargetName, S1);
                if (raw == null) continue;   // the three Switch 2-only controls
                Assert.Matches(grammar, raw);
                Assert.True(seen.Add(raw),
                    $"raw target '{raw}' claimed by two overlays");
            }
            for (int i = 0; i < 14; i++)
                Assert.Contains($"RawBtn{i}", (IEnumerable<string>)seen);
        }

        /// <summary>The quadrant emitter's names (screen convention,
        /// positive Y = down) pass through mechanically: down-quadrant
        /// "LeftThumbAxisY" lands on RawAxis1 whose HID-convention positive
        /// is also down, so no direction crossing on any axis.</summary>
        [Fact]
        public void QuadrantNames_MapWithoutDirectionCrossing()
        {
            Assert.Equal("RawAxis1", NintendoPreviewMap.ToRaw("LeftThumbAxisY", S1));
            Assert.Equal("RawAxis1Neg", NintendoPreviewMap.ToRaw("LeftThumbAxisYNeg", S1));
            Assert.Equal("RawAxis3", NintendoPreviewMap.ToRaw("RightThumbAxisY", S1));
            Assert.Equal("RawAxis3Neg", NintendoPreviewMap.ToRaw("RightThumbAxisYNeg", S1));
        }
    }
}
