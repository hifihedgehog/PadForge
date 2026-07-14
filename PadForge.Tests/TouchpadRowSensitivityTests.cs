using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #9 B-13: the generic per-source <see cref="MappingSource.Sensitivity"/>
    /// extends to touchpad finger X/Y reads, so a workshop config's per-group
    /// touch tuning (and the row's own slider) acts on the row instead of
    /// punting to the Touchpad tab. Coverage: the relative finger-delta read
    /// scales in both directions and stays bit-identical at 1.0, the absolute
    /// and unipolar position reads scale with the same clamp discipline,
    /// Pressure never scales, the widened predicate gates the slider for
    /// finger X/Y only, and touchpad-as-button (Click / Finger Down, plus the
    /// finger axes' absent bool read) is unaffected.
    /// </summary>
    public class TouchpadRowSensitivityTests
    {
        /// <summary>Slot indices far away from the frame-gate tests (slots
        /// 0/1) so the static delta trackers never collide across parallel
        /// test classes. Each test also keys its own DeviceGuid.</summary>
        private const int Slot = 31;

        private static CustomInputState TouchState(float x, float y = 0.5f, bool down = true)
        {
            var s = new CustomInputState();
            var pad = new TouchpadInputState(1);
            pad.FingerDown[0] = down;
            pad.FingerX[0] = x;
            pad.FingerY[0] = y;
            s.Touchpads = new[] { pad };
            return s;
        }

        /// <summary>Seeds the delta tracker (first contact reads 0), then
        /// moves the finger by <paramref name="delta"/> in one poll and
        /// returns the relative read. Fresh key per (slot, guid).</summary>
        private static float SeededDeltaRead(MappingSource src, float delta)
        {
            var s = TouchState(0.5f);
            SourceCoercion.BeginPollFrame();
            float seed = SourceCoercion.EvaluateForBipolarAxisTarget(
                s, src, Slot, relativeTouchpad: true);
            Assert.Equal(0f, seed);
            s.Touchpads[0].FingerX[0] = 0.5f + delta;
            SourceCoercion.BeginPollFrame();
            return SourceCoercion.EvaluateForBipolarAxisTarget(
                s, src, Slot, relativeTouchpad: true);
        }

        // ── Relative finger-delta read (the touchpad-to-mouse path) ──

        [Fact]
        public void Delta_SensitivityScales_BothDirections()
        {
            // 1/256 pad fraction × TouchpadDeltaScale 128 = 0.5 base, all
            // exactly representable so the comparisons are exact.
            var up2 = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", DeviceGuid = "b13-d1", Sensitivity = 2.0 };
            Assert.Equal(1.0f, SeededDeltaRead(up2, 1f / 256f));

            var down2 = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", DeviceGuid = "b13-d2", Sensitivity = 2.0 };
            Assert.Equal(-1.0f, SeededDeltaRead(down2, -1f / 256f));

            var half = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", DeviceGuid = "b13-d3", Sensitivity = 0.5 };
            Assert.Equal(0.25f, SeededDeltaRead(half, 1f / 256f));
        }

        [Fact]
        public void Delta_SensitivityUnity_IsBitIdentical()
        {
            // Explicit 1.0 and the constructor default must produce the
            // exact same float as each other and as the unscaled math
            // (the != 1 guard skips the multiply entirely).
            var explicit1 = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", DeviceGuid = "b13-u1", Sensitivity = 1.0 };
            var default1 = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", DeviceGuid = "b13-u2" };
            float a = SeededDeltaRead(explicit1, 1f / 256f);
            float b = SeededDeltaRead(default1, 1f / 256f);
            Assert.Equal(0.5f, a);
            Assert.Equal(a, b);
        }

        [Fact]
        public void Delta_ClampsAfterScaling()
        {
            var src = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", DeviceGuid = "b13-c1", Sensitivity = 3.0 };
            Assert.Equal(1.0f, SeededDeltaRead(src, 1f / 64f)); // 2.0 × 3 → clamp
        }

        // ── Absolute position read (stick / passthrough targets) ──

        [Fact]
        public void Absolute_SensitivityScales_DeviationFromCenter()
        {
            var src = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", Sensitivity = 2.0 };
            // X 0.75 recenters to +0.5, × 2 = 1.0; X 0.25 mirrors to -1.0.
            Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.75f), src));
            Assert.Equal(-1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.25f), src));

            src.Sensitivity = 0.5;
            Assert.Equal(0.25f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.75f), src));

            // Unity is bit-identical to the unscaled recentering.
            src.Sensitivity = 1.0;
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.75f), src));
        }

        // ── Unipolar position read (trigger targets) ──

        [Fact]
        public void Unipolar_SensitivityScales_MagnitudeFromZero_AndClamps()
        {
            var src = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", Sensitivity = 2.0 };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(TouchState(0.25f), src));
            Assert.Equal(1.0f, SourceCoercion.EvaluateForTriggerTarget(TouchState(0.75f), src)); // 1.5 → clamp

            src.Sensitivity = 1.0;
            Assert.Equal(0.25f, SourceCoercion.EvaluateForTriggerTarget(TouchState(0.25f), src));
        }

        // ── Pressure stays outside the contract ──

        [Fact]
        public void Pressure_NeverScales()
        {
            var src = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 Pressure", Sensitivity = 2.0 };
            var s = TouchState(0.5f);
            s.Touchpads[0].FingerPressure[0] = 0.6f;
            Assert.Equal(0.6f, SourceCoercion.EvaluateForTriggerTarget(s, src), 3);
            Assert.Equal(0.6f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        // ── Predicate widening (drives the slider on both VM twins) ──

        [Theory]
        [InlineData("Touchpad 0 Finger 0 X", true)]
        [InlineData("Touchpad 0 Finger 0 Y", true)]
        [InlineData("Touchpad 1 Finger 1 X", true)]
        [InlineData("Touchpad 0 Finger 0 Pressure", false)] // physical magnitude
        [InlineData("Touchpad 0 Finger 0 Down", false)]     // bool, nothing analog
        [InlineData("Touchpad 0 Click", false)]
        [InlineData("Touchpad 0 SwipeUp", false)]           // gesture family
        [InlineData("Touchpad 0 StickX", false)]            // gesture stick output
        public void Predicate_IncludesFingerXYOnly(string descriptor, bool expected)
        {
            Assert.Equal(expected, SourceCoercion.IsGenericSensitivityDescriptor(descriptor));
        }

        [Fact]
        public void VmGates_SliderVisible_OnBothTwins_WithPrefixInterplay()
        {
            // Grid extra source (clean descriptor).
            var msi = new MappingSourceItem { Descriptor = "Touchpad 0 Finger 0 X" };
            Assert.True(msi.IsGenericSensitivitySource);

            // Primary row, bare and legacy-I-prefixed encodings (the
            // 9fc26119 strip interplay: the VM strips I/H before the
            // predicate, and 'T' is never stripped).
            var mi = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mi.LoadDescriptor("Touchpad 0 Finger 0 X");
            Assert.True(mi.IsGenericSensitivitySource);
            mi.LoadDescriptor("ITouchpad 0 Finger 0 X");
            Assert.True(mi.IsGenericSensitivitySource);
        }

        [Fact]
        public void VmGates_DeadZoneColumn_StaysHiddenForFingerAxes()
        {
            // The finger axes have no axis-to-button threshold read
            // (ReadAsBool's touchpad branch reads Click / Finger Down
            // only), so the widened predicate must not reveal a dead
            // deadzone knob. Gesture CONTINUOUS axes keep their opt-in.
            var fingerX = new MappingSourceItem
            { Descriptor = "Touchpad 0 Finger 0 X", ParentTargetIsDiscrete = true };
            Assert.False(fingerX.IsDeadZoneApplicable);

            var pinch = new MappingSourceItem
            { Descriptor = "Touchpad 0 PinchAxis", ParentTargetIsDiscrete = true };
            Assert.True(pinch.IsDeadZoneApplicable);

            var mi = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mi.LoadDescriptor("Touchpad 0 Finger 0 X");
            Assert.False(mi.IsDeadZoneApplicable);
        }

        // ── Touchpad-as-button unaffected (the documented B-13 decision) ──

        [Fact]
        public void ButtonReads_Unaffected_BySensitivity()
        {
            // Finger Down keeps firing as a plain bool.
            var down = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 Down", Sensitivity = 5.0 };
            Assert.True(SourceCoercion.EvaluateForButtonTarget(TouchState(0.9f), down, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(TouchState(0.9f, down: false), down, 50));

            // Finger X has no bool read at all: false regardless of the
            // knob, exactly as before the widening.
            var fingerX = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", Sensitivity = 5.0 };
            Assert.False(SourceCoercion.EvaluateForButtonTarget(TouchState(1.0f), fingerX, 50));

            // Click rides Buttons[16], untouched by the knob.
            var click = new MappingSource
            { Descriptor = "Touchpad 0 Click", Sensitivity = 5.0 };
            var s = TouchState(0.5f);
            s.Buttons[16] = true;
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, click, 50));
        }
    }
}
