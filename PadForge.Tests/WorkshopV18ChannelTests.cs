using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine coverage for the translator v18 channels: the curve/range
    /// shaping on the unipolar trigger tail, the anti-deadzone floor, the
    /// per-source AND gate (MappingSource.GateDescriptor), and the new
    /// touchpad windows (Upper / Lower vertical halves, the diamond
    /// quadrants, the half-composed quadrants, and the windowed click).
    /// Every knob defaults off, so unstamped sources pin pass-through.
    /// </summary>
    public class WorkshopV18ChannelTests
    {
        // ── Curve / range on the unipolar trigger tail ──

        [Fact]
        public void Trigger_CurveExponent_ShapesThePull()
        {
            var s = new CustomInputState();
            s.Axis[2] = 32768; // half pull on the 0..65535 trigger scale
            var src = new MappingSource { Descriptor = "Axis 2", ParamCurveExponent = 2.0 };
            Assert.Equal(0.25f, SourceCoercion.EvaluateForTriggerTarget(s, src), 3);
        }

        [Fact]
        public void Trigger_RangeOuter_ReachesFullPullAtTheRadius()
        {
            var s = new CustomInputState();
            s.Axis[2] = 32768; // 0.5 pull
            var src = new MappingSource { Descriptor = "Axis 2", ParamRangeOuter = 0.5 };
            Assert.Equal(1.0f, SourceCoercion.EvaluateForTriggerTarget(s, src), 3);
        }

        [Fact]
        public void Trigger_Defaults_ArePassThrough()
        {
            var s = new CustomInputState();
            s.Axis[2] = 32768;
            var src = new MappingSource { Descriptor = "Axis 2" };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(s, src), 3);
        }

        [Fact]
        public void AntiDeadzone_FloorsTheOutput_AndZeroStaysZero()
        {
            var s = new CustomInputState();
            for (int i = 0; i < 6; i++) s.Axis[i] = 32768;
            var src = new MappingSource { Descriptor = "Axis 0", ParamAntiDeadzone = 0.2 };

            // Rest reads exactly 0: the floor applies to real input only.
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);

            s.Axis[0] = 32768 + 16384; // +0.5 -> 0.2 + 0.8 * 0.5 = 0.6
            Assert.Equal(0.6f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);

            s.Axis[0] = 32768 - 16384; // sign carried
            Assert.Equal(-0.6f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        // ── The per-source AND gate ──

        [Fact]
        public void GateDescriptor_GatesEveryEvaluatorLane()
        {
            var s = new CustomInputState();
            for (int i = 0; i < 6; i++) s.Axis[i] = 32768;
            s.Buttons[0] = true;      // the source
            s.Axis[2] = 65535;        // a full trigger pull source
            var gatedButton = new MappingSource { Descriptor = "Button 0", GateDescriptor = "Button 1" };
            var gatedAxis = new MappingSource { Descriptor = "Axis 2", GateDescriptor = "Button 1" };

            // Gate up: nothing contributes.
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(s, gatedButton, 50, 0, "ButtonA", 0, null, 0));
            Assert.Equal(0f, SourceEvaluator.EvaluateForBipolarAxisTarget(s, gatedButton, 0, "LeftThumbAxisX", 0, null, 0));
            Assert.Equal(0f, SourceEvaluator.EvaluateForTriggerTarget(s, gatedAxis, 0, "LeftTrigger", 0, null, 0));

            // Gate held: the source reads normally.
            s.Buttons[1] = true;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(s, gatedButton, 50, 0, "ButtonA", 0, null, 0));
            Assert.Equal(1f, SourceEvaluator.EvaluateForBipolarAxisTarget(s, gatedButton, 0, "LeftThumbAxisX", 0, null, 0));
            Assert.Equal(1f, SourceEvaluator.EvaluateForTriggerTarget(s, gatedAxis, 0, "LeftTrigger", 0, null, 0), 3);
        }

        [Fact]
        public void GateDescriptor_Empty_IsUngated()
        {
            var s = new CustomInputState();
            s.Buttons[0] = true;
            var src = new MappingSource { Descriptor = "Button 0" };
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(s, src, 50, 0, "ButtonA", 0, null, 0));
        }

        // ── Touchpad windows (v18) ──

        private static CustomInputState Touch(float x, float y, bool down = true, bool clicked = false)
        {
            var s = new CustomInputState();
            var pad = new TouchpadInputState(1);
            pad.FingerDown[0] = down;
            pad.FingerX[0] = x;
            pad.FingerY[0] = y;
            s.Touchpads = new[] { pad };
            if (clicked && s.Buttons.Length > 16) s.Buttons[16] = true;
            return s;
        }

        private static bool DownReads(string descriptor, float x, float y, bool clicked = false)
            => SourceCoercion.EvaluateForButtonTarget(
                Touch(x, y, down: true, clicked), new MappingSource { Descriptor = descriptor }, 50);

        [Fact]
        public void VerticalHalves_SplitAtHalfHeight()
        {
            Assert.True(DownReads("Touchpad 0 Finger 0 Down Upper", 0.5f, 0.2f));
            Assert.False(DownReads("Touchpad 0 Finger 0 Down Upper", 0.5f, 0.8f));
            Assert.True(DownReads("Touchpad 0 Finger 0 Down Lower", 0.5f, 0.8f));
            // Boundary Y == 0.5 belongs to Lower, the X convention's twin.
            Assert.True(DownReads("Touchpad 0 Finger 0 Down Lower", 0.5f, 0.5f));
            Assert.False(DownReads("Touchpad 0 Finger 0 Down Upper", 0.5f, 0.5f));
        }

        [Fact]
        public void DiamondQuadrants_PartitionByDominantAxis()
        {
            // North: above center with |dy| >= |dx|.
            Assert.True(DownReads("Touchpad 0 Finger 0 Down North", 0.5f, 0.1f));
            Assert.False(DownReads("Touchpad 0 Finger 0 Down North", 0.1f, 0.45f)); // West wins
            Assert.True(DownReads("Touchpad 0 Finger 0 Down West", 0.1f, 0.45f));
            Assert.True(DownReads("Touchpad 0 Finger 0 Down East", 0.9f, 0.55f));
            Assert.True(DownReads("Touchpad 0 Finger 0 Down South", 0.55f, 0.9f));
            // The four windows are exhaustive off-center: exactly one fires.
            int firing = 0;
            foreach (var q in new[] { "North", "South", "East", "West" })
                if (DownReads($"Touchpad 0 Finger 0 Down {q}", 0.62f, 0.31f)) firing++;
            Assert.Equal(1, firing);
        }

        [Fact]
        public void ComposedQuadrants_TestAgainstTheHalfCenter()
        {
            // "North Left": the quadrant runs against the LEFT half's own
            // center (0.25, 0.5), so a touch north of THAT fires.
            Assert.True(DownReads("Touchpad 0 Finger 0 Down North Left", 0.25f, 0.1f));
            // Same spot fails the whole-pad North (it is west of center).
            Assert.False(DownReads("Touchpad 0 Finger 0 Down North", 0.25f, 0.35f));
            // A right-half touch never fires a Left-composed window.
            Assert.False(DownReads("Touchpad 0 Finger 0 Down North Left", 0.75f, 0.1f));
            Assert.True(DownReads("Touchpad 0 Finger 0 Down North Right", 0.75f, 0.1f));
        }

        [Fact]
        public void WindowedClick_RequiresClickAndTheHalf()
        {
            // Clicked with the finger on the right half.
            Assert.True(DownReads("Touchpad 0 Click Right", 0.75f, 0.5f, clicked: true));
            // Clicked but the finger sits left: the Right window fails.
            Assert.False(DownReads("Touchpad 0 Click Right", 0.25f, 0.5f, clicked: true));
            // Finger in the half but no click.
            Assert.False(DownReads("Touchpad 0 Click Right", 0.75f, 0.5f, clicked: false));
        }

        // ── Legacy per-key path twin sync (audit 2026-07-17 G1) ──

        [Fact]
        public void LegacyMapTouchpadButton_MatchesReadTouchpadBool_OnEveryWindowForm()
        {
            // MapTouchpadButton was a hand-kept mirror of ReadTouchpadBool
            // and missed every v18 window token. It now delegates; this pin
            // holds the two paths identical over the whole grammar,
            // including forms neither should accept (X as a button).
            string[] forms =
            {
                "Touchpad 0 Click",
                "Touchpad 0 Click Left",
                "Touchpad 0 Click Upper",
                "Touchpad 0 Finger 0 Down",
                "Touchpad 0 Finger 0 Down Right",
                "Touchpad 0 Finger 0 Down Lower",
                "Touchpad 0 Finger 0 Down North",
                "Touchpad 0 Finger 0 Down North Left",
                "Touchpad 0 Finger 0 X",
                "Touchpad 0 Finger 0 X Left",
            };
            var probes = new (float X, float Y, bool Clicked)[]
            {
                (0.2f, 0.2f, true), (0.8f, 0.8f, false), (0.5f, 0.1f, true), (0.1f, 0.6f, false),
            };
            foreach (var d in forms)
                foreach (var (x, y, clicked) in probes)
                {
                    var s = Touch(x, y, down: true, clicked);
                    Assert.True(
                        SourceCoercion.ReadTouchpadBool(s, d)
                            == PadForge.Common.Input.InputManager.MapTouchpadButton(s, d),
                        $"Twin divergence on '{d}' at ({x}, {y}, clicked={clicked}).");
                }
        }

        [Fact]
        public void LegacyMapTouchpadButton_ReadsV18Windows()
        {
            // Positive controls through the LEGACY path itself: before the
            // delegation these all read permanently false there.
            Assert.True(PadForge.Common.Input.InputManager.MapTouchpadButton(
                Touch(0.2f, 0.3f, down: true, clicked: true), "Touchpad 0 Click Left"));
            Assert.True(PadForge.Common.Input.InputManager.MapTouchpadButton(
                Touch(0.5f, 0.1f, down: true), "Touchpad 0 Finger 0 Down North"));
            Assert.True(PadForge.Common.Input.InputManager.MapTouchpadButton(
                Touch(0.5f, 0.8f, down: true), "Touchpad 0 Finger 0 Down Lower"));
        }

        // ── Clone hygiene (audit 2026-07-17 G4) ──

        [Fact]
        public void MappingSourceClone_DropsGateCacheAndFeelState()
        {
            var s = new CustomInputState();
            s.Buttons[0] = true;
            s.Buttons[1] = true;
            var src = new MappingSource { Descriptor = "Button 0", GateDescriptor = "Button 1" };
            // A real evaluation builds the runtime gate cache.
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(s, src, 50, 0, "ButtonA", 0, null, 0));
            Assert.NotNull(src.GateSourceCache);

            var clone = src.Clone();

            // The cached synthetic source pins the ORIGINAL's device guid;
            // a retargeted clone re-reading it would gate against the stale
            // device. The per-device feel map is likewise not the clone's.
            Assert.Null(clone.GateSourceCache);
            Assert.Null(clone.GateSourceCacheKey);
            Assert.Null(clone.MouseFeelByDevice);
            // The serialized gate itself still travels.
            Assert.Equal("Button 1", clone.GateDescriptor);
        }

        // ── Per-device feel state (audit 2026-07-17 P4) ──

        [Fact]
        public void PerSourceSmoothing_TwoDevices_KeepIndependentFilters()
        {
            // A device-free source on a two-device slot evaluates once per
            // device each poll. The single per-source scalar handed device B
            // device A's smoothed value through the seq gate; the per-device
            // map must keep the filters apart.
            var saved = SourceCoercion.TouchpadGestureAxisProvider;
            try
            {
                SourceCoercion.TouchpadGestureAxisProvider =
                    (slot, dev, pad, name) => dev == "p4-dev-a" ? 1f : 0f;
                var src = new MappingSource
                {
                    Descriptor = "Touchpad 0 StickX",
                    ParamSmoothingAlpha = 0.5,
                };
                var s = new CustomInputState();

                SourceCoercion.BeginPollFrame();
                float a = SourceCoercion.EvaluateForBipolarAxisTarget(s, src, 0, false, "p4-dev-a");
                float b = SourceCoercion.EvaluateForBipolarAxisTarget(s, src, 0, false, "p4-dev-b");

                Assert.Equal(0.5f, a, 3); // 0 -> 1 through alpha 0.5
                Assert.Equal(0f, b, 3);   // device B's own filter, not A's replay

                // Same device re-read in the same poll still replays (the
                // seq-gate contract survives the keying change).
                float aAgain = SourceCoercion.EvaluateForBipolarAxisTarget(s, src, 0, false, "p4-dev-a");
                Assert.Equal(a, aAgain, 5);
            }
            finally
            {
                SourceCoercion.TouchpadGestureAxisProvider = saved;
            }
        }

        // ── Gyro lanes consume ParamAccel (audit 2026-07-17 T4) ──

        [Fact]
        public void GyroLanes_ConsumeParamAccel_WithTheTouchpadFeelFormula()
        {
            var saved = SourceCoercion.AimEngageStateProvider;
            SourceCoercion.AimEngageStateProvider = null;
            try
            {
                var s = new CustomInputState();
                s.Gyro[1] = 2.0f; // yaw, rad/s
                var plain = new MappingSource { Descriptor = "Gyro Yaw" };
                var accel = new MappingSource { Descriptor = "Gyro Yaw", ParamAccel = 2.0 };

                float v0 = SourceCoercion.EvaluateForBipolarAxisTarget(s, plain, 0, false, "t4-dev");
                Assert.True(v0 > 0f && v0 < 1f, "probe rate must land strictly inside (0, 1)");
                float v1 = SourceCoercion.EvaluateForBipolarAxisTarget(s, accel, 0, false, "t4-dev");
                Assert.Equal(System.Math.Min(1f, v0 * (1f + 2f * v0)), v1, 3);

                // Unipolar twin (trigger lane).
                float u0 = SourceCoercion.EvaluateForTriggerTarget(s, plain, 0, "t4-dev");
                float u1 = SourceCoercion.EvaluateForTriggerTarget(s, accel, 0, "t4-dev");
                Assert.Equal(System.Math.Min(1f, u0 * (1f + 2f * u0)), u1, 3);
            }
            finally
            {
                SourceCoercion.AimEngageStateProvider = saved;
            }
        }
    }
}
