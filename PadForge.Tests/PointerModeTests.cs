using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>Covers the #203 Wii pointer modes: the aspect-region and
    /// border-pin math (Ryochan7 lightgun lineage), the FPS Mouse response
    /// curve (Suegrini lineage), the IR Offscreen debounce, the PointerMode
    /// persistence legs, and the cycle action's CSV.</summary>
    public class PointerModeTests
    {
        // ── Aspect region (Ryochan7 dead-band math) ──

        [Fact]
        public void Aspect_Matching_Target_Is_Full_Screen()
        {
            var (halfW, halfH) = InputManager.ComputeAspectRegion(1920, 1080, 16f / 9f);
            Assert.Equal(0.5f, halfW, 3);
            Assert.Equal(0.5f, halfH, 3);
        }

        [Fact]
        public void Aspect_43_On_Widescreen_Pillarboxes()
        {
            // dead = (1 - (4/3)/(16/9))/2 = 0.125 on X.
            var (halfW, halfH) = InputManager.ComputeAspectRegion(1920, 1080, 4f / 3f);
            Assert.Equal(0.375f, halfW, 3);
            Assert.Equal(0.5f, halfH, 3);
        }

        [Fact]
        public void Aspect_169_On_43_Screen_Letterboxes()
        {
            // dead = (1 - (4/3)/(16/9))/2 = 0.125 on Y.
            var (halfW, halfH) = InputManager.ComputeAspectRegion(1024, 768, 16f / 9f);
            Assert.Equal(0.5f, halfW, 3);
            Assert.Equal(0.375f, halfH, 3);
        }

        // ── Border pin (max-norm ray-to-edge, the lineage's atan2 intersection) ──

        [Fact]
        public void Border_Inside_Passes_Through()
        {
            var (rx, ry, inside) = InputManager.TransformBorderAim(0.5f, -0.5f);
            Assert.True(inside);
            Assert.Equal(0.5f, rx, 3);
            Assert.Equal(-0.5f, ry, 3);
        }

        [Fact]
        public void Border_Outside_Pins_Along_The_Aim_Ray()
        {
            // Aim twice as far right as up: pins on the right edge at half height.
            var (rx, ry, inside) = InputManager.TransformBorderAim(2f, 0.5f);
            Assert.False(inside);
            Assert.Equal(1f, rx, 3);
            Assert.Equal(0.25f, ry, 3);

            // Steeper than wide: pins on the bottom edge.
            (rx, ry, inside) = InputManager.TransformBorderAim(-0.5f, 2f);
            Assert.False(inside);
            Assert.Equal(-0.25f, rx, 3);
            Assert.Equal(1f, ry, 3);
        }

        // ── FPS response curve (Suegrini three-segment) ──

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.2f, 0.13f)]     // 0.65 * 0.2
        [InlineData(0.4f, 0.26f)]     // segment boundary: 0.65*0.4 == 0.4-0.14
        [InlineData(0.6f, 0.46f)]     // 0.6 - 0.14
        [InlineData(0.75f, 0.61f)]    // boundary: 0.75-0.14 == 1.56*0.75-0.56
        [InlineData(0.9f, 0.844f)]    // 1.56*0.9 - 0.56
        [InlineData(1f, 1f)]
        public void Fps_Curve_Matches_The_Lineage_Segments(float n, float expected)
        {
            Assert.Equal(expected, InputManager.FpsResponseCurve(n), 3);
        }

        // ── IR Offscreen debounce ──

        [Fact]
        public void Offscreen_Requires_Sustained_Loss()
        {
            long last = 0;
            // Detected: on-screen, timestamp refreshes.
            Assert.False(SourceCoercion.ComputeIrOffscreen(true, ref last, 1000, 150));
            Assert.Equal(1000, last);

            // Single dropped frame: still on-screen.
            Assert.False(SourceCoercion.ComputeIrOffscreen(false, ref last, 1010, 150));

            // Sustained loss past the debounce: offscreen.
            Assert.True(SourceCoercion.ComputeIrOffscreen(false, ref last, 1150, 150));

            // Reacquired: clears instantly.
            Assert.False(SourceCoercion.ComputeIrOffscreen(true, ref last, 1200, 150));
        }

        [Fact]
        public void Offscreen_Starts_True_Before_First_Detection()
        {
            // The lineage's OutOfReach starts true; a remote that has never
            // seen the bar reads offscreen immediately.
            long last = 0;
            Assert.True(SourceCoercion.ComputeIrOffscreen(false, ref last, 5000, 150));
        }

        [Fact]
        public void Offscreen_Classifies_As_Its_Own_SourceType()
        {
            // The classifier must not drift from the three evaluator
            // branches that each carry an "IR Offscreen" read.
            Assert.Equal(SourceCoercion.SourceType.IrOffscreen,
                SourceCoercion.ClassifyDescriptor("IR Offscreen"));
        }

        [Fact]
        public void Offscreen_Debounce_Keys_On_The_Evaluated_Device()
        {
            // An "any device" row (empty src.DeviceGuid) must not collapse
            // onto one shared "" debounce entry: evaluatedDeviceGuid carries
            // the device whose state is being read, so device A being
            // on-screen cannot mask device B's offscreen answer.
            var src = new MappingSource { Descriptor = "IR Offscreen" };
            var seen = new CustomInputState(); seen.Ir.Detected = true;
            var lost = new CustomInputState(); lost.Ir.Detected = false;

            // Device A sees the bar: refreshes A's own timestamp.
            Assert.False(SourceCoercion.EvaluateForButtonTarget(
                seen, src, 50, 0, evaluatedDeviceGuid: "offscr-dev-a"));
            // Device B has never seen it: offscreen immediately, unmasked by A.
            Assert.True(SourceCoercion.EvaluateForButtonTarget(
                lost, src, 50, 0, evaluatedDeviceGuid: "offscr-dev-b"));
            // Device A stays on-screen inside its debounce window.
            Assert.False(SourceCoercion.EvaluateForButtonTarget(
                lost, src, 50, 0, evaluatedDeviceGuid: "offscr-dev-a"));
        }

        [Fact]
        public void KbmCombine_Keeps_PerAxis_Absolute_Validity()
        {
            // Mixed mappings across devices: A drives an absolute X, B an
            // absolute Y. Combine must carry each driven coordinate and both
            // per-axis flags; the un-driven 0f default may not mask the
            // other device's tracked coordinate.
            var a = new PadForge.Engine.KbmRawState { MouseAbsX = 0.6f, MouseAbsXValid = true, MouseAbsValid = true };
            var b = new PadForge.Engine.KbmRawState { MouseAbsY = -0.4f, MouseAbsYValid = true, MouseAbsValid = true };
            var c = PadForge.Engine.KbmRawState.Combine(a, b);
            Assert.True(c.MouseAbsValid);
            Assert.True(c.MouseAbsXValid);
            Assert.True(c.MouseAbsYValid);
            Assert.Equal(0.6f, c.MouseAbsX, 3);
            Assert.Equal(-0.4f, c.MouseAbsY, 3);
        }

        [Fact]
        public void Offscreen_Descriptor_Joins_Both_Grammars()
        {
            // The I-prefix trap: "IR Offscreen" must be exempt from the
            // legacy Invert-prefix strip or the migrator mangles it into
            // Invert + "R Offscreen" (the descriptor-grammar-collision
            // lesson from "IR Pointer X").
            Assert.True(SourceCoercion.IsPrefixExemptDescriptor("IR Offscreen"));
            Assert.True(SourceCoercion.IsPrefixExemptDescriptor("IR Pointer X"));
        }

        // ── PointerMode persistence legs ──

        [Fact]
        public void Checksum_Distinguishes_Pointer_Modes()
        {
            // Without the checksum leg, two devices differing only in
            // pointer mode dedup to one PadSetting on save and the second
            // device's mode is silently dropped (the TPS/MGS lesson).
            var a = new PadSetting();
            var b = new PadSetting { PointerMode = "FpsMouse" };
            Assert.NotEqual(a.ComputeChecksum(), b.ComputeChecksum());

            var c = new PadSetting { PointerFpsSpeed = "60" };
            Assert.NotEqual(a.ComputeChecksum(), c.ComputeChecksum());
        }

        [Fact]
        public void CopyFrom_Carries_Pointer_Fields()
        {
            var src = new PadSetting { PointerMode = "Mouse43", PointerFpsSpeed = "50" };
            var clone = src.CloneDeep();
            Assert.Equal("Mouse43", clone.PointerMode);
            Assert.Equal("50", clone.PointerFpsSpeed);
        }

        // ── Cycle CSV ──

        [Fact]
        public void Cycle_Csv_Parses_In_Order_And_Drops_Unknowns()
        {
            var a = new MacroAction { PointerCycleModesCsv = "FpsMouse, Mouse, Bogus, fpsmouse" };
            var modes = a.ParsedPointerCycleModes();
            Assert.Equal(new[] { "FpsMouse", "Mouse" }, modes);
        }

        [Fact]
        public void Cycle_Csv_Rewrite_Uses_Canonical_Order()
        {
            var a = new MacroAction();
            a.WritePointerCycleCsv(new[] { "Mouse169", "Mouse" });
            Assert.Equal("Mouse,Mouse169", a.PointerCycleModesCsv);
        }

        [Fact]
        public void PointerMode_Actions_Sit_At_The_Enum_Tail()
        {
            // The macro clipboard serializes MacroActionType numerically, so
            // the enum is APPEND-ONLY: a mid-enum insertion silently retypes
            // every previously copied macro. Caught by adversarial review
            // (2026-07-11) after the first cut inserted PointerModeCycle
            // beside LightbarModeCycle. PointerModeSet appended later the
            // same day. GuideLedBrightness (#209) is the tail now; the
            // pointer members' values must never move again.
            var values = (MacroActionType[])System.Enum.GetValues(typeof(MacroActionType));
            int max = 0;
            foreach (var v in values) max = System.Math.Max(max, (int)v);
            Assert.Equal((int)MacroActionType.GuideLedBrightness, max);
            Assert.Equal((int)MacroActionType.PointerModeSet, max - 1);
            Assert.Equal((int)MacroActionType.PointerModeCycle, max - 2);
        }

        [Theory]
        [InlineData("FpsMouse", "FpsMouse")]
        [InlineData("mouse169", "Mouse169")] // case-insensitive
        [InlineData("Bogus", "Mouse")]       // unknown normalizes to Mouse
        [InlineData("", "Mouse")]
        public void SetMode_Normalizes_To_Known_Names(string stored, string expected)
        {
            // PointerModeSet writes into PadSetting.PointerMode, so a
            // hand-edited XML value must never inject garbage.
            var a = new MacroAction { PointerSetMode = stored };
            Assert.Equal(expected, a.NormalizedPointerSetMode());
        }

        [Fact]
        public void SetMode_Rides_The_Macro_Dto_RoundTrip()
        {
            // The MacroAction VM never serializes directly: settings XML,
            // the macro clipboard, AND the action Duplicate command all
            // funnel through the ActionData DTO. A field missing from the
            // DTO silently resets on every reload (the dirty-gate
            // persistence trap; adversarial review caught exactly that in
            // the first cut of this action, 2026-07-11).
            var m = new PadForge.ViewModels.MacroItem { Name = "SetMode" };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.PointerModeSet,
                PointerSetMode = "FpsMouse",
            });
            var data = PadForge.Services.SettingsService.BuildMacroDataForMacro(m, 0);
            var clone = PadForge.Services.SettingsService.LoadMacroFromData(
                data, PadForge.Engine.VirtualControllerType.Xbox, null);

            Assert.Equal(MacroActionType.PointerModeSet, clone.Actions[0].Type);
            Assert.Equal("FpsMouse", clone.Actions[0].PointerSetMode);
        }

        [Fact]
        public void Cycle_Index_Resets_When_Csv_Changes()
        {
            var a = new MacroAction { PointerCycleIndex = 3 };
            a.PointerCycleModesCsv = "Mouse,FpsMouse";
            Assert.Equal(0, a.PointerCycleIndex);
        }

        // ── IR aim: pair-only midpoint (the #203 bench "double walk" fix) ──

        [Fact]
        public void IrAim_Midpoint_Of_Both_Dots_Mirrored_X_Direct_Y()
        {
            // Dots at (200,300) and (400,400): midpoint (300,350).
            var (x, y, det) = PadForge.Engine.SdlDeviceWrapper.ComputeIrAim(200, 300, 400, 400);
            Assert.True(det);
            Assert.Equal((0.5f - 300f / 1023.5f) * 2f, x, 4);
            Assert.Equal((350f / 767.5f - 0.5f) * 2f, y, 4);
        }

        [Fact]
        public void IrAim_Centered_Pair_Reads_Near_Zero()
        {
            var (x, y, det) = PadForge.Engine.SdlDeviceWrapper.ComputeIrAim(412, 384, 612, 384);
            Assert.True(det);
            Assert.Equal(0f, x, 2);
            Assert.Equal(0f, y, 2);
        }

        [Theory]
        [InlineData((short)300, (short)350, (short)-1, (short)-1)] // dot1 lost
        [InlineData((short)-1, (short)-1, (short)300, (short)350)] // dot0 lost
        [InlineData((short)300, (short)-1, (short)400, (short)400)] // dot0 half-lost
        [InlineData((short)-1, (short)-1, (short)-1, (short)-1)]   // no dots
        public void IrAim_Requires_Both_Dots(short d0x, short d0y, short d1x, short d1y)
        {
            // Every proven reference aims only from a dot PAIR and calls
            // fewer dots OutOfReach (Touchmote ScreenPositionCalculator.cs
            // :89-160, Ryochan7 :207-315, Suegrini :183-291). The old
            // single-dot fallback snapped the aim from the midpoint to the
            // surviving raw dot, half a separation away, making a steady
            // sweep re-walk part of the screen (owner bench, 2026-07-11).
            var (_, _, det) = PadForge.Engine.SdlDeviceWrapper.ComputeIrAim(d0x, d0y, d1x, d1y);
            Assert.False(det);
        }

        [Fact]
        public void IrAim_All_Zero_Means_No_Report_Yet()
        {
            var (_, _, det) = PadForge.Engine.SdlDeviceWrapper.ComputeIrAim(0, 0, 0, 0);
            Assert.False(det);
        }

        // ── Border modes: sight loss freezes, never projects to the border ──

        [Fact]
        public void Border_Sight_Loss_After_Tracked_Frame_Drives_Nothing()
        {
            // The Touchmote lastPos idiom (ScreenPositionCalculator.cs:153-160)
            // that plain Mouse mode already ships: when tracking ends, the
            // cursor freezes at its last driven position. The first cut
            // projected the remembered aim out to the region border instead
            // (Ryochan7 lightbar MouseHandler idiom), which snapped the cursor
            // border-ward whenever tracking ended INSIDE the region and
            // oscillated on boundary dot flicker (owner bench, 2026-07-11).
            //
            // The seeded off-center tracked frame is what makes this test
            // DISCRIMINATE: the projection code remembered that frame's aim
            // and re-drove the cursor to the border on the lost frame
            // (asserting MouseAbsValid true), while the freeze drives
            // nothing. An unseeded variant passes under BOTH designs (the
            // old code's never-aimed else-return also drove nothing), which
            // adversarial review proved empirically by running the seeded
            // shape against the reverted code (2026-07-11).
            var prev = SourceCoercion.IrPointerModeProvider;
            try
            {
                SourceCoercion.IrPointerModeProvider = (dev, slot) => (2, 35f);

                // Precondition: without a screen the border branch is inert
                // under both designs and the assertion below is vacuous.
                Assert.True(PadForge.Services.CursorControlService.TryGetPrimarySize(out int w, out int h));
                Assert.True(w > 0 && h > 0);

                // Seed one off-center tracked frame.
                var raw = new PadForge.Engine.KbmRawState
                {
                    MouseAbsValid = true,
                    MouseAbsX = 0.4f,
                    MouseAbsY = 0.2f,
                };
                InputManager.ApplyPointerMode(ref raw, "test-dev", 0, true, true, true);
                Assert.True(raw.MouseAbsValid);

                // Sight lost on the next frame: nothing may drive.
                raw = new PadForge.Engine.KbmRawState
                {
                    MouseAbsValid = false,
                    MouseDeltaX = 12,
                    MouseDeltaY = -7,
                };
                InputManager.ApplyPointerMode(ref raw, "test-dev", 0, true, true, true);
                Assert.False(raw.MouseAbsValid); // no drive: cursor holds
                Assert.Equal(12, raw.MouseDeltaX); // other lanes untouched
                Assert.Equal(-7, raw.MouseDeltaY);
            }
            finally
            {
                SourceCoercion.IrPointerModeProvider = prev;
            }
        }

        [Fact]
        public void Border_Center_Aim_Stays_Centered_While_Tracked()
        {
            // The region map is centered, so a dead-center aim must come out
            // dead-center on any screen aspect. Environment-independent
            // anchor for the tracked path.
            var prev = SourceCoercion.IrPointerModeProvider;
            try
            {
                SourceCoercion.IrPointerModeProvider = (dev, slot) => (2, 35f);
                var raw = new PadForge.Engine.KbmRawState
                {
                    MouseAbsValid = true,
                    MouseAbsX = 0f,
                    MouseAbsY = 0f,
                };
                InputManager.ApplyPointerMode(ref raw, "test-dev", 0, true, true, true);
                Assert.True(raw.MouseAbsValid);
                Assert.Equal(0f, raw.MouseAbsX, 3);
                Assert.Equal(0f, raw.MouseAbsY, 3);
            }
            finally
            {
                SourceCoercion.IrPointerModeProvider = prev;
            }
        }
    }
}
