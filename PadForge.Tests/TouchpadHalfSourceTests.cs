using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #9 B-1: region-windowed touchpad finger sources. Steam splits a
    /// single physical trackpad (DS4 / DualSense) into left/right halves;
    /// the "Touchpad {p} Finger {f} X|Y Left|Right" descriptors read the
    /// finger coordinate only while the finger is in that half, absolute X
    /// re-normalizes the half to the full range, the relative-delta read
    /// gates per sample, and "Down Left|Right" is the half-windowed contact
    /// bool (B-19's region-windowed Down). Coverage: all three analog read
    /// paths, the bool read, the boundary convention (X == 0.5 is Right),
    /// the widened sensitivity predicate, the display strings, the macro
    /// trigger conversion, the VM gates, and the lens-1k persisted
    /// round-trip through the legacy migrator.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class TouchpadHalfSourceTests
    {
        /// <summary>Away from the frame-gate tests (slots 0/1) and the B-13
        /// sensitivity tests (slot 31) so static delta trackers never
        /// collide across parallel test classes.</summary>
        private const int Slot = 37;

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

        // ── Absolute position read (stick / passthrough targets) ──

        [Fact]
        public void Absolute_XHalf_RenormalizesToFullRange()
        {
            var left = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X Left" };
            // Left half [0..0.5] maps onto [0..1]: 0.25 is its center.
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.25f), left));
            Assert.Equal(-1f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.0f), left));
            // 0.375 renormalizes to 0.75 → bipolar +0.5.
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.375f), left));

            var right = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X Right" };
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.75f), right));
            Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(1.0f), right));
        }

        [Fact]
        public void Absolute_OutsideHalf_ReadsNeutral()
        {
            var left = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X Left" };
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.75f), left));

            var rightY = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Y Right" };
            // Finger on the LEFT half: the windowed Y reads neutral even
            // though Y itself is fully deflected.
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.25f, y: 1.0f), rightY));
        }

        [Fact]
        public void Absolute_YHalf_PassesFullPadHeightThrough()
        {
            // The halves split X only; a windowed Y spans the whole pad
            // height un-renormalized.
            var rightY = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Y Right" };
            Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.75f, y: 1.0f), rightY));
            Assert.Equal(-1f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.75f, y: 0.0f), rightY));
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.75f, y: 0.5f), rightY));
        }

        [Fact]
        public void Boundary_ExactCenter_BelongsToRight()
        {
            var left = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X Left" };
            var right = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X Right" };
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.5f), left));
            Assert.Equal(-1f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.5f), right));
        }

        // ── Unipolar read (trigger targets) ──

        [Fact]
        public void Unipolar_RenormalizesAndGates()
        {
            var right = new MappingSource { Descriptor = "Touchpad 0 Finger 0 X Right" };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(TouchState(0.75f), right));
            Assert.Equal(1.0f, SourceCoercion.EvaluateForTriggerTarget(TouchState(1.0f), right));
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(TouchState(0.25f), right)); // outside
        }

        // ── Relative delta read (the touchpad-to-mouse path) ──

        [Fact]
        public void Delta_InHalf_ProducesDelta_OutsideGatesPerSample()
        {
            var src = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X Right", DeviceGuid = "b1-d1" };
            var s = TouchState(0.75f);

            // Seed frame inside the half: 0.
            SourceCoercion.BeginPollFrame();
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(
                s, src, Slot, relativeTouchpad: true));

            // 1/256 pad fraction × TouchpadDeltaScale 128 = 0.5, exact.
            s.Touchpads[0].FingerX[0] = 0.75f + 1f / 256f;
            SourceCoercion.BeginPollFrame();
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(
                s, src, Slot, relativeTouchpad: true));

            // Finger crosses out of the half: neutral, no delta.
            s.Touchpads[0].FingerX[0] = 0.25f;
            SourceCoercion.BeginPollFrame();
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(
                s, src, Slot, relativeTouchpad: true));

            // Re-entry seeds fresh: the 0.25 → 0.80 jump produces NO spike.
            s.Touchpads[0].FingerX[0] = 0.80f;
            SourceCoercion.BeginPollFrame();
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(
                s, src, Slot, relativeTouchpad: true));

            // And motion after re-entry reads normally again.
            s.Touchpads[0].FingerX[0] = 0.80f + 1f / 256f;
            SourceCoercion.BeginPollFrame();
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(
                s, src, Slot, relativeTouchpad: true));
        }

        [Fact]
        public void Delta_HalfAndWholeRows_KeepIndependentTrackers()
        {
            var whole = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X", DeviceGuid = "b1-d2" };
            var half = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X Right", DeviceGuid = "b1-d2" };
            var s = TouchState(0.75f);

            SourceCoercion.BeginPollFrame();
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, whole, Slot, relativeTouchpad: true));
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, half, Slot, relativeTouchpad: true));

            s.Touchpads[0].FingerX[0] = 0.75f + 1f / 256f;
            SourceCoercion.BeginPollFrame();
            // Both rows see the same motion: the tracker key carries the
            // half window, so neither consumes the other's delta.
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, whole, Slot, relativeTouchpad: true));
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, half, Slot, relativeTouchpad: true));
        }

        [Fact]
        public void Delta_SensitivityAppliesToWindowedRead()
        {
            var src = new MappingSource
            { Descriptor = "Touchpad 0 Finger 0 X Left", DeviceGuid = "b1-d3", Sensitivity = 2.0 };
            var s = TouchState(0.25f);
            SourceCoercion.BeginPollFrame();
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src, Slot, relativeTouchpad: true));
            s.Touchpads[0].FingerX[0] = 0.25f + 1f / 256f;
            SourceCoercion.BeginPollFrame();
            Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src, Slot, relativeTouchpad: true));
        }

        // ── Windowed Down (B-19's region-windowed contact bool) ──

        [Fact]
        public void DownHalf_TracksContactInsideTheHalfOnly()
        {
            var left = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Down Left" };
            var right = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Down Right" };

            Assert.True(SourceCoercion.EvaluateForButtonTarget(TouchState(0.25f), left, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(TouchState(0.25f), right, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(TouchState(0.75f), left, 50));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(TouchState(0.75f), right, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(TouchState(0.25f, down: false), left, 50));
            // Boundary: X == 0.5 belongs to Right.
            Assert.False(SourceCoercion.EvaluateForButtonTarget(TouchState(0.5f), left, 50));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(TouchState(0.5f), right, 50));
        }

        // ── Grammar: rejected spellings stay dead ──

        [Theory]
        [InlineData("Touchpad 0 Finger 0 Pressure Left")]  // no windowed Pressure
        [InlineData("Touchpad 0 Finger 0 X Up")]           // unknown half token
        [InlineData("Touchpad 0 Finger 0 Down Center")]
        public void UnknownWindowSpellings_ReadDead(string descriptor)
        {
            var src = new MappingSource { Descriptor = descriptor };
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(TouchState(0.25f), src));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(TouchState(0.25f), src, 50));
        }

        // ── Predicate widening (sensitivity slider + Half checkbox) ──

        [Theory]
        [InlineData("Touchpad 0 Finger 0 X Left", true)]
        [InlineData("Touchpad 0 Finger 0 X Right", true)]
        [InlineData("Touchpad 0 Finger 0 Y Left", true)]
        [InlineData("Touchpad 0 Finger 0 Y Right", true)]
        [InlineData("Touchpad 0 Finger 0 Down Left", false)]  // bool, nothing analog
        [InlineData("Touchpad 0 Finger 0 Down Right", false)]
        [InlineData("Touchpad 0 Finger 0 Pressure Left", false)]
        public void Predicate_CoversWindowedFingerAxes(string descriptor, bool expected)
        {
            Assert.Equal(expected, SourceCoercion.IsGenericSensitivityDescriptor(descriptor));
            Assert.Equal(expected, SourceCoercion.IsTouchpadFingerAxisDescriptor(descriptor));
        }

        [Fact]
        public void VmGates_HalfAxisApplicable_DeadZoneHidden()
        {
            var msi = new MappingSourceItem { Descriptor = "Touchpad 0 Finger 0 X Left" };
            Assert.True(msi.IsHalfAxisApplicable);
            Assert.True(msi.IsGenericSensitivitySource);
            msi.ParentTargetIsDiscrete = true;
            Assert.False(msi.IsDeadZoneApplicable);

            var down = new MappingSourceItem
            { Descriptor = "Touchpad 0 Finger 0 Down Right", ParentTargetIsDiscrete = true };
            Assert.False(down.IsHalfAxisApplicable);
            Assert.False(down.IsDeadZoneApplicable);

            var mi = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mi.LoadDescriptor("Touchpad 0 Finger 0 Y Right");
            Assert.True(mi.IsHalfAxisApplicable);
        }

        // ── Display strings (chips, picker, report surfaces) ──

        [Fact]
        public void Display_RendersHalfMarkedNames()
        {
            var si = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal(string.Format(si.Mapping_TouchpadFingerXLeft_Format, 1, 1),
                MappingDisplayResolver.ResolveDescriptorText("Touchpad 0 Finger 0 X Left", null));
            Assert.Equal(string.Format(si.Mapping_TouchpadFingerYRight_Format, 1, 1),
                MappingDisplayResolver.ResolveDescriptorText("Touchpad 0 Finger 0 Y Right", null));
            Assert.Equal(string.Format(si.Mapping_TouchpadFingerTouchRight_Format, 1, 1),
                MappingDisplayResolver.ResolveDescriptorText("Touchpad 0 Finger 0 Down Right", null));
            // The whole-pad spellings keep their pre-wave rendering.
            Assert.Equal(string.Format(si.Mapping_TouchpadFingerX_Format, 1, 1),
                MappingDisplayResolver.ResolveDescriptorText("Touchpad 0 Finger 0 X", null));
        }

        [Fact]
        public void AnyDeviceGroup_OffersPadZeroHalfVariants()
        {
            var choices = MappingDisplayResolver.BuildDeviceAgnosticChoices();
            var descriptors = System.Array.ConvertAll(choices, c => c.Descriptor);
            Assert.Contains("Touchpad 0 Finger 0 X Left", descriptors);
            Assert.Contains("Touchpad 0 Finger 0 X Right", descriptors);
            Assert.Contains("Touchpad 0 Finger 0 Y Left", descriptors);
            Assert.Contains("Touchpad 0 Finger 0 Y Right", descriptors);
            Assert.Contains("Touchpad 0 Finger 0 Down Left", descriptors);
            Assert.Contains("Touchpad 0 Finger 0 Down Right", descriptors);
            // Halves model a SINGLE pad's split: no pad-1 variants.
            Assert.DoesNotContain("Touchpad 1 Finger 0 X Left", descriptors);
        }

        // ── Macro trigger conversion ──

        [Fact]
        public void TriggerEntry_WindowedDownConverts_AxesDoNot()
        {
            var down = new InputChoice { Descriptor = "Touchpad 0 Finger 0 Down Right", DeviceGuid = "" };
            Assert.True(MacroItem.TryBuildTriggerEntry(down, out var entry));
            Assert.Equal("Touchpad 0 Finger 0 Down Right", entry.SourceDescriptor);

            var axis = new InputChoice { Descriptor = "Touchpad 0 Finger 0 X Right", DeviceGuid = "" };
            Assert.False(MacroItem.TryBuildTriggerEntry(axis, out _));
        }

        [Fact]
        public void TriggerEntry_PadZeroClickIsRawButton_PadOneClickIsDescriptor()
        {
            var pad0 = new InputChoice { Descriptor = "Touchpad 0 Click", DeviceGuid = "" };
            Assert.True(MacroItem.TryBuildTriggerEntry(pad0, out var e0));
            Assert.Equal(16, e0.RawButton);
            Assert.Null(e0.SourceDescriptor);

            // Pad 1 has no Buttons[16] backing; a raw-16 entry would fire
            // on the WRONG pad, so it rides the descriptor read (quiet
            // until the multi-touchpad click extension lands there).
            var pad1 = new InputChoice { Descriptor = "Touchpad 1 Click", DeviceGuid = "" };
            Assert.True(MacroItem.TryBuildTriggerEntry(pad1, out var e1));
            Assert.Equal(-1, e1.RawButton);
            Assert.Equal("Touchpad 1 Click", e1.SourceDescriptor);
        }

        // ── Lens 1k: persisted round-trip through the legacy migrator ──

        [Theory]
        [InlineData("Touchpad 0 Finger 0 X Left")]
        [InlineData("Touchpad 0 Finger 0 Y Right")]
        [InlineData("Touchpad 0 Finger 0 Down Left")]
        public void Migrator_KeepsWindowedDescriptorsByteIdentical(string descriptor)
        {
            var ps = new PadSetting { LeftThumbAxisX = descriptor };
            var ms = MappingSetMigrator.BuildFromLegacy(
                0, new[] { ("22222222-2222-2222-2222-222222222222", ps) });
            var row = System.Linq.Enumerable.FirstOrDefault(ms.Rows, r => r.Target == "LeftThumbAxisX");
            Assert.NotNull(row);
            var src = Assert.Single(row.Sources);
            Assert.Equal(descriptor, src.Descriptor);
            Assert.False(src.Invert);
            Assert.False(src.HalfAxis);
        }
    }
}
