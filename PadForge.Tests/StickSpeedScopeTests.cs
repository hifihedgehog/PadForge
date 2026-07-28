using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Locks WHERE the per-stick speed multiplier may be applied.
    ///
    /// <para>The knob is a rate control. It belongs to the keyboard-and-mouse
    /// lane, whose stick 0 is mouse movement and stick 1 is the scroll wheel,
    /// and it must never touch an Extended or Nintendo raw-HID stick: those
    /// are absolute-position gamepad sticks whose deadzone stage has already
    /// mapped full deflection to full scale, so a multiplier can only rescale
    /// partial deflections and clip. The owner ruled on this 2026-07-27.</para>
    ///
    /// <para>The first cut of that change put the call in
    /// MapInputToExtendedRaw, which is gated on Extended or Nintendo, so it
    /// scaled exactly the sticks the ruling excluded while the keyboard-and-
    /// mouse rate outputs it was written for stayed untouched. Both halves of
    /// that mistake are pinned here.</para>
    /// </summary>
    public class StickSpeedScopeTests
    {
        private static PadSetting SettingWithSensitivity(string left, string right)
        {
            var ps = new PadSetting();
            ps.LeftThumbSensitivity = left;
            ps.RightThumbSensitivity = right;
            // Keep every other stage neutral so any output difference can
            // only come from the multiplier under test.
            ps.LeftThumbDeadZoneX = "0"; ps.LeftThumbDeadZoneY = "0";
            ps.RightThumbDeadZoneX = "0"; ps.RightThumbDeadZoneY = "0";
            return ps;
        }

        private static CustomInputState DeflectedState()
        {
            var s = new CustomInputState();
            // Mid-deflection: the region a multiplier would visibly rescale.
            // Full scale would clip and hide the difference.
            for (int i = 0; i < s.Axis.Length; i++) s.Axis[i] = 32768;
            if (s.Axis.Length > 0) s.Axis[0] = 49152;   // ~+50%
            if (s.Axis.Length > 1) s.Axis[1] = 49152;
            return s;
        }

        private static short[] ExtendedAxesFor(string sensitivity)
        {
            // Axes is what the raw-axis evaluation loop bounds on; leaving it
            // at zero left every axis unwritten and made the whole test inert.
            var cfg = new CustomControllerLayout
            { Axes = 4, Sticks = 2, Triggers = 0, Buttons = 4, Povs = 0 };
            var raw = new RawHidState();
            InputManager.EnsureRawShapeForTest(ref raw, cfg);

            var ms = new MappingSet();
            for (int i = 0; i < 4; i++)
            {
                var row = new MappingRow { Target = "RawAxis" + i, LayerMask = "Base" };
                row.Sources.Add(new MappingSource
                { Kind = "Direct", Descriptor = "Axis " + i, DeviceGuid = "" });
                ms.Rows.Add(row);
            }

            InputManager.MapInputToExtendedRaw(ref raw, DeflectedState(),
                SettingWithSensitivity(sensitivity, sensitivity), cfg, ms, "", 0);

            var copy = new short[raw.Axes.Length];
            System.Array.Copy(raw.Axes, copy, raw.Axes.Length);
            return copy;
        }

        /// <summary>An Extended / Nintendo raw stick must read the SAME at
        /// every sensitivity setting. If the multiplier is ever wired into
        /// that lane again, these arrays diverge.</summary>
        [Fact]
        public void ExtendedRawSticks_IgnoreTheSpeedMultiplier()
        {
            var atOne = ExtendedAxesFor("1");
            // Positive control: the fixture must actually drive an axis, or
            // "unchanged" would be vacuously true. This is the assertion the
            // first cut of this test was missing, which let the mutation pass.
            Assert.Contains(atOne, v => v != 0);

            var atFive = ExtendedAxesFor("5");
            var atHalf = ExtendedAxesFor("0.5");

            Assert.Equal(atOne.Length, atFive.Length);
            for (int i = 0; i < atOne.Length; i++)
            {
                Assert.True(atOne[i] == atFive[i],
                    $"axis {i}: a 5x speed setting changed an Extended stick ({atOne[i]} -> {atFive[i]}). "
                    + "The multiplier belongs to the keyboard-and-mouse lane only.");
                Assert.True(atOne[i] == atHalf[i],
                    $"axis {i}: a 0.5x speed setting changed an Extended stick ({atOne[i]} -> {atHalf[i]}).");
            }
        }

        /// <summary>The positive control for the helper itself: it really
        /// does scale, so the test above is asserting absence of a live
        /// effect rather than absence of any effect at all.</summary>
        [Theory]
        [InlineData(1.0, 10000, 10000)]
        [InlineData(2.0, 10000, 20000)]
        [InlineData(0.5, 10000, 5000)]
        public void SpeedMultiplier_ScalesARateOutput(double sens, short input, short expected)
        {
            short x = input, y = input;
            InputManager.ApplyKbmStickSpeedForTest(ref x, ref y, sens);
            Assert.Equal(expected, x);
            Assert.Equal(expected, y);
        }

        [Fact]
        public void SpeedMultiplier_ClampsRatherThanWrapping()
        {
            short x = 30000, y = -30000;
            InputManager.ApplyKbmStickSpeedForTest(ref x, ref y, 5.0);
            Assert.Equal(short.MaxValue, x);
            Assert.Equal(short.MinValue, y);
        }

        [Fact]
        public void SpeedMultiplier_IsANoOpAtOneAndAtNonPositive()
        {
            short x = 1234, y = -4321;
            InputManager.ApplyKbmStickSpeedForTest(ref x, ref y, 1.0);
            Assert.Equal(1234, x);
            Assert.Equal(-4321, y);

            InputManager.ApplyKbmStickSpeedForTest(ref x, ref y, 0);
            Assert.Equal(1234, x);
            Assert.Equal(-4321, y);
        }
    }
}
