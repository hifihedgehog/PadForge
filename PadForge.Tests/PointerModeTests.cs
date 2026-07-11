using PadForge.Common.Input;
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
        public void PointerModeCycle_Sits_At_The_Enum_Tail()
        {
            // The macro clipboard serializes MacroActionType numerically, so
            // the enum is APPEND-ONLY: a mid-enum insertion silently retypes
            // every previously copied macro. Caught by adversarial review
            // (2026-07-11) after the first cut inserted it beside
            // LightbarModeCycle.
            var values = (MacroActionType[])System.Enum.GetValues(typeof(MacroActionType));
            int max = 0;
            foreach (var v in values) max = System.Math.Max(max, (int)v);
            Assert.Equal(max, (int)MacroActionType.PointerModeCycle);
        }

        [Fact]
        public void Cycle_Index_Resets_When_Csv_Changes()
        {
            var a = new MacroAction { PointerCycleIndex = 3 };
            a.PointerCycleModesCsv = "Mouse,FpsMouse";
            Assert.Equal(0, a.PointerCycleIndex);
        }
    }
}
