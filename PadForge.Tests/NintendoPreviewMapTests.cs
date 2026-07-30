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
    /// preview) and InitializeRawSurfaceMappings (the grid rows): B A Y X,
    /// L R, ZL ZR, Minus Plus, LS RS, Home, Capture = raw 0..13.
    /// </summary>
    public class NintendoPreviewMapTests
    {
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
        public void ToRaw_And_ToPreview_AreInverse(string preview, string raw)
        {
            Assert.Equal(raw, NintendoPreviewMap.ToRaw(preview));
            Assert.Equal(preview, NintendoPreviewMap.ToPreview(raw));
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
            Assert.Null(NintendoPreviewMap.ToRaw(preview));
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
            Assert.Null(NintendoPreviewMap.ToPreview(raw));
        }

        /// <summary>Every clickable Switch Pro overlay element resolves to a
        /// well-formed raw grid target, with no two elements colliding.
        /// StickRings are exempt (their clicks route through the quadrant
        /// emitter) and TriggerBases are rest art.</summary>
        [Fact]
        public void EveryClickableSwitchProOverlay_ResolvesToDistinctRawTarget()
        {
            var grammar = new Regex(
                @"^(RawBtn(\d+)|RawAxis[0-3](Neg)?|RawPov0(Up|Down|Left|Right))$");
            var seen = new HashSet<string>();
            foreach (var ov in SwitchProLayout.Overlays)
            {
                if (ov.ElementType is OverlayElementType.TriggerBase
                    or OverlayElementType.StickRing)
                    continue;
                string raw = NintendoPreviewMap.ToRaw(ov.TargetName);
                Assert.True(raw != null,
                    $"overlay '{ov.TargetName}' has no raw counterpart");
                Assert.Matches(grammar, raw);
                Assert.True(seen.Add(raw),
                    $"raw target '{raw}' claimed by two overlays");
            }
            // The full lettered set is reachable from the art: 14 buttons.
            for (int i = 0; i < 14; i++)
                Assert.Contains($"RawBtn{i}",
                    (IEnumerable<string>)seen);
        }

        /// <summary>The quadrant emitter's names (screen convention,
        /// positive Y = down) pass through mechanically: down-quadrant
        /// "LeftThumbAxisY" lands on RawAxis1 whose HID-convention positive
        /// is also down, so no direction crossing on any axis.</summary>
        [Fact]
        public void QuadrantNames_MapWithoutDirectionCrossing()
        {
            Assert.Equal("RawAxis1", NintendoPreviewMap.ToRaw("LeftThumbAxisY"));
            Assert.Equal("RawAxis1Neg", NintendoPreviewMap.ToRaw("LeftThumbAxisYNeg"));
            Assert.Equal("RawAxis3", NintendoPreviewMap.ToRaw("RightThumbAxisY"));
            Assert.Equal("RawAxis3Neg", NintendoPreviewMap.ToRaw("RightThumbAxisYNeg"));
        }
    }
}
