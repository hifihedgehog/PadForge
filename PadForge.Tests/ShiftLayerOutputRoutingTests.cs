using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #220: a shift layer that remaps a physical input to a
    /// DIFFERENT Extended output than the Base layer uses for it. The
    /// gamepad output path (ApplyMappingSetToGamepad) resolved the active
    /// layer, but the non-gamepad per-VC evaluators (Extended / MIDI /
    /// KBM / Touchpad) resolved rows through FindBaseRowForTarget, which
    /// hard-filtered to LayerMask=="Base". So with the layer active the
    /// layer's own Extended target never fired and the Base target it was
    /// meant to replace stayed live. These tests drive the Extended button
    /// evaluator directly with the layer engaged and assert the remapped
    /// target fires while the base target is suppressed.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ShiftLayerOutputRoutingTests
    {
        private static MappingSource Btn(int n) => new() { Descriptor = $"Button {n}" };

        private static MappingRow Row(string target, string layer, params MappingSource[] sources)
        {
            var r = new MappingRow { Target = target, LayerMask = layer };
            foreach (var s in sources) r.Sources.Add(s);
            return r;
        }

        [Fact]
        public void ViewLayerActive_RemappedExtendedButtonFires_AndBaseTargetSuppressed()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 0;
            const string guid = ""; // empty = "the device currently being evaluated"

            var ms = new MappingSet();
            // Base: physical Button 16 -> RawBtn16.
            ms.Rows.Add(Row("RawBtn16", "Base", Btn(16)));
            // View: physical Button 16 -> RawBtn60 (the remap under test).
            ms.Rows.Add(Row("RawBtn60", "View", Btn(16)));
            // The clone quirk from disc #220: an empty Base copy of the View
            // target. FindBaseRowForTarget would return THIS (zero-source)
            // row for RawBtn60, which is why the pre-fix output was dead.
            ms.Rows.Add(Row("RawBtn60", "Base"));
            // Toggle activator on Button 28 engaging "View", replace mode.
            // DelayMs=0 so a single rising edge engages deterministically
            // (long-press timing is covered by ShiftLongPressTests).
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = "Button 28",
                Mode = "Toggle",
                LayerMask = "View",
                LayerName = "View",
                InheritUnmapped = false,
                Kind = "Button",
                DelayMs = 0,
                AutoCancelMs = 0,
            });

            var state = new CustomInputState();
            state.Buttons[16] = true; // remapped physical input held
            state.Buttons[28] = true; // activator pressed -> toggles View on

            // Engage View via the real activator tick (rising edge, no delay).
            string mask = InputManager.ResolveActiveLayerMask(slot, ms, state, guid);
            Assert.Equal("View", mask);

            // The layer's remapped target MUST fire.
            bool handled60 = InputManager.TryEvaluateMappingSetButton(
                state, ms, guid, slot, "RawBtn60", 50, out bool v60);
            Assert.True(handled60, "RawBtn60 (View target) must be evaluated while View is active");
            Assert.True(v60, "RawBtn60 output must fire from the View row");

            // The base target for the same physical input MUST be suppressed
            // (InheritUnmapped=false => replace mode).
            InputManager.TryEvaluateMappingSetButton(
                state, ms, guid, slot, "RawBtn16", 50, out bool v16);
            Assert.False(v16, "RawBtn16 (base target) must NOT fire while View replaces Base");

            InputManager.ClearAllShiftRuntime();
        }

        [Fact]
        public void InheritUnmapped_UncoveredBaseTarget_FallsThrough_WhileLayerTargetFires()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 1;
            const string guid = "";

            var ms = new MappingSet();
            ms.Rows.Add(Row("RawBtn16", "Base", Btn(16))); // not covered by View
            ms.Rows.Add(Row("RawBtn60", "View", Btn(16))); // View remap
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = "Button 28",
                Mode = "Toggle",
                LayerMask = "View",
                InheritUnmapped = true, // overlay-with-fallthrough
                Kind = "Button",
                DelayMs = 0,
                AutoCancelMs = 0,
            });

            var state = new CustomInputState();
            state.Buttons[16] = true;
            state.Buttons[28] = true;

            Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            InputManager.TryEvaluateMappingSetButton(state, ms, guid, slot, "RawBtn60", 50, out bool v60);
            Assert.True(v60, "View target still fires under InheritUnmapped");

            // Uncovered base target inherits (falls through) instead of being suppressed.
            InputManager.TryEvaluateMappingSetButton(state, ms, guid, slot, "RawBtn16", 50, out bool v16);
            Assert.True(v16, "Base target must fall through when the layer inherits and does not cover it");

            InputManager.ClearAllShiftRuntime();
        }

        [Fact]
        public void BaseActive_NoActivator_RoutesBaseRowUnchanged()
        {
            // Regression guard: with nothing engaged the base row still drives,
            // exactly as before the fix (the common non-shift case).
            InputManager.ClearAllShiftRuntime();
            const int slot = 2;

            var ms = new MappingSet();
            ms.Rows.Add(Row("RawBtn16", "Base", Btn(16)));

            var state = new CustomInputState();
            state.Buttons[16] = true;

            bool handled = InputManager.TryEvaluateMappingSetButton(
                state, ms, "", slot, "RawBtn16", 50, out bool v16);
            Assert.True(handled);
            Assert.True(v16);
        }
    }
}
