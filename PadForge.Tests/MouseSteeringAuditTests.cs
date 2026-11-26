using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Mouse steering (#600) as a row reads it: the sign From the Game's
    /// Force works in, a shift layer's own wheel, Mouse Speed's window over
    /// every device of a frame, the Motion rows and Rapid Trigger on a
    /// relative axis, and Mouse Speed and Mouse Position keeping the plain
    /// read's shape on a trigger or a button.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MouseSteeringAuditTests : IDisposable
    {
        private const string Target = "LeftThumbAxisX";
        private const string MouseGuid = "6b29fc40-ca47-1067-b31d-00dd010662da";

        private readonly Func<int, double> _savedForce = SourceCoercion.MouseTravelForceProvider;
        private readonly Func<(double speed, double grip, bool lockAtSpeed)> _savedRoad = SourceCoercion.MouseTravelRoadProvider;
        private readonly Func<string, bool> _savedIsMouse = SourceCoercion.IsMouseDeviceProvider;
        private readonly Func<string, string, bool> _savedRests = SourceCoercion.SourceRestsAtZeroProvider;
        private readonly Func<string, bool, int, long?> _savedTotal = SourceCoercion.RelativeAxisTotalProvider;
        private readonly Func<(float, float)> _savedCursor = SourceCoercion.MouseCursorProvider;
        private readonly Func<(double, double, double, double, double)?> _savedPoint = SourceCoercion.MouseCursorPointProvider;
        private readonly Func<string, string, int, bool> _savedKeyHeld = SourceCoercion.KeyHeldProvider;

        public MouseSteeringAuditTests()
        {
            SourceCoercion.MouseTravelForceProvider = null;
            SourceCoercion.MouseTravelRoadProvider = null;
            SourceCoercion.RelativeAxisTotalProvider = null;
            SourceCoercion.IsMouseDeviceProvider = g => string.Equals(g, MouseGuid, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            SourceCoercion.MouseTravelForceProvider = _savedForce;
            SourceCoercion.MouseTravelRoadProvider = _savedRoad;
            SourceCoercion.IsMouseDeviceProvider = _savedIsMouse;
            SourceCoercion.SourceRestsAtZeroProvider = _savedRests;
            SourceCoercion.RelativeAxisTotalProvider = _savedTotal;
            SourceCoercion.MouseCursorProvider = _savedCursor;
            SourceCoercion.KeyHeldProvider = _savedKeyHeld;
            SourceCoercion.MouseCursorPointProvider = _savedPoint;
        }

        private static MappingSource Source(string descriptor, string device = "") => new()
        {
            Kind = "Direct",
            Descriptor = descriptor,
            DeviceGuid = device,
        };

        private static CustomInputState Mouse(int dx = 0, bool steer = false)
        {
            var s = new CustomInputState { MouseRawDX = dx };
            s.Axis[0] = s.Axis[1] = s.Axis[2] = 32767;
            s.Buttons[2] = steer;
            return s;
        }

        private static float Stick(SourceKindRuntime rt, MappingSource src, CustomInputState state,
            string layer = null, string device = null)
        {
            rt.FrameSeq++;
            return SourceEvaluator.EvaluateForBipolarAxisTarget(state, src, 0, Target, 0, rt, 0.001, device, layer);
        }

        // ── From the Game's Force works in the axis's own sign ──

        private static MappingSource ForceTravel()
        {
            var src = Source("Mouse Travel X");
            src.ParamTravelCentering = MappingSource.TravelCenteringGameForce;
            return src;
        }

        [Fact]
        public void TheGamesForce_UnderInvert_PushesTheAxisTowardLowerValues()
        {
            SourceCoercion.MouseTravelForceProvider = _ => 0.5;
            var rt = new SourceKindRuntime();
            var src = ForceTravel();
            src.Invert = true;
            float v = Stick(rt, src, Mouse(1000));
            for (int i = 0; i < 199; i++) v = Stick(rt, src, Mouse());
            // Half a force for two tenths of a second is a tenth of the lock,
            // lower: the axis falls, as it does on a row with no Invert.
            Assert.Equal(-0.6f, v, 3);
        }

        [Fact]
        public void TheGamesForce_UnderHalfWithInvert_PushesTheAxisTowardLowerValues()
        {
            SourceCoercion.MouseTravelForceProvider = _ => 0.5;
            var rt = new SourceKindRuntime();
            var src = ForceTravel();
            src.HalfAxis = true;
            src.Invert = true;
            float v = Stick(rt, src, Mouse(-1000));
            for (int i = 0; i < 199; i++) v = Stick(rt, src, Mouse());
            // The axis reads the wheel's left side: it was at half, the force
            // brings it down by a tenth.
            Assert.Equal(0.4f, v, 3);
        }

        [Fact]
        public void TheGamesForce_KeepsTheAxisSign_WhenTheButtonLaneReadsTheRowAfterTheAxisLane()
        {
            SourceCoercion.MouseTravelForceProvider = _ => 0.5;
            var rt = new SourceKindRuntime();
            var src = ForceTravel();
            src.Invert = true;
            float v = 0;
            for (int i = 0; i < 200; i++)
            {
                var state = Mouse(i == 0 ? 1000 : 0);
                rt.FrameSeq++;
                v = SourceEvaluator.EvaluateForBipolarAxisTarget(state, src, 0, Target, 0, rt, 0.001);
                SourceEvaluator.EvaluateForButtonTarget(state, src, 50, 0, Target, 0, rt, 0.001);
            }
            Assert.Equal(-0.6f, v, 3);
        }

        [Fact]
        public void TheGamesForce_TakesTheRowsOwnSign_ForAHeldInvertOnHoldOrAStickY()
        {
            SourceCoercion.MouseTravelForceProvider = _ => 0.5;
            var rt = new SourceKindRuntime();
            var src = ForceTravel();
            float v = 0;
            for (int i = 0; i < 200; i++)
            {
                rt.FrameSeq++;
                v = SourceEvaluator.EvaluateForBipolarAxisTarget(Mouse(i == 0 ? 1000 : 0), src, 0, "LeftThumbAxisY", 0, rt, 0.001,
                    rowSign: -1.0);
            }
            // The row writes the axis negated, so the force raises the
            // evaluator's answer, which lowers the axis.
            Assert.Equal(0.6f, v, 3);
        }

        // ── A shift layer's row keeps its own wheel ──

        [Fact]
        public void ALayersRow_StartsItsWheelAtCenter_NotWhereTheBaseRowsWheelStands()
        {
            var rt = new SourceKindRuntime();
            var src = Source("Mouse Travel X");
            Assert.Equal(0.5f, Stick(rt, src, Mouse(1000)), 4);
            // The shift engages: the layer's row on the same target is read.
            Assert.Equal(0f, Stick(rt, src, Mouse(), layer: "Shift"), 4);
        }

        // ── Mouse Speed reads every device of the frame ──

        [Fact]
        public void MouseSpeed_ReadTwiceInAFrame_TakesTheSecondDevicesCounts()
        {
            var rt = new SourceKindRuntime();
            var src = Source("Axis 0", MouseGuid);
            src.ParamMouseSpeedSensitivity = 2;
            double v = 0;
            for (int i = 0; i < 40; i++)
            {
                rt.FrameSeq++;
                rt.TickMouseSpeed(0, Target, 0, src, Mouse(), 0.001, false, false);
                v = rt.TickMouseSpeed(0, Target, 0, src, Mouse(8), 0.001, false, false);
            }
            // 8,000 counts a second times 2 is the whole stick.
            Assert.InRange(v, 0.99, 1.0);
        }

        // ── A relative axis on a Motion row reads its wheel ──

        private static CustomInputState Spinner(int slider)
        {
            var s = new CustomInputState();
            s.Sliders[0] = slider;
            return s;
        }

        [Fact]
        public void ARelativeSlider_OnAMotionRow_FollowsItsWheel_AndWaitsOnItsSteerInput()
        {
            SourceCoercion.SourceRestsAtZeroProvider = (d, g) => d != null && d.StartsWith("Slider ", StringComparison.Ordinal);
            var src = Source("Slider 0");
            src.ParamAxisRelative = true;
            var rt = new SourceKindRuntime();
            rt.FrameSeq++;
            SourceEvaluator.EvaluateForBipolarAxisTarget(Spinner(1000), src, 0, MappingSetMigrator.MotionYawTarget, 0, rt, 0.001);
            rt.FrameSeq++;
            float moved = SourceEvaluator.EvaluateForBipolarAxisTarget(Spinner(1200), src, 0, MappingSetMigrator.MotionYawTarget, 0, rt, 0.001);
            Assert.Equal(0.1f, moved, 4);                                       // 200 counts of 2,000 to the lock

            var gated = Source("Slider 0");
            gated.ParamAxisRelative = true;
            gated.GateDescriptor = "Button 2";
            gated.ParamTravelSteer = MappingSource.TravelSteerHeldCenter;
            rt = new SourceKindRuntime();
            float v = 1f;
            for (int i = 0; i < 4; i++)
            {
                rt.FrameSeq++;
                v = SourceEvaluator.EvaluateForBipolarAxisTarget(Spinner(1000 + 300 * i), gated, 0, MappingSetMigrator.MotionYawTarget, 0, rt, 0.001);
            }
            Assert.Equal(0f, v);                                                // not held: no steering
        }

        // ── Rapid Trigger on a relative axis reads its wheel ──

        [Fact]
        public void RapidTrigger_OnARelativeAxis_ReadsItsWheel_AndWaitsOnItsSteerInput()
        {
            var src = Source("Axis 2");
            src.Kind = "RapidTrigger";
            src.ParamAxisRelative = true;
            src.GateDescriptor = "Button 2";
            src.ParamTravelSteer = MappingSource.TravelSteerHeldCenter;
            var rt = new SourceKindRuntime();
            foreach (int reading in new[] { 30000, 60000, 62000, 64000 })
            {
                var s = new CustomInputState();
                s.Axis[2] = reading;
                rt.FrameSeq++;
                Assert.Equal(0f, SourceEvaluator.EvaluateForTriggerTarget(s, src, 0, "RightTrigger", 0, rt, 0.001));
                Assert.False(SourceEvaluator.EvaluateForButtonTarget(s, src, 50, 0, "ButtonA", 0, rt, 0.001));
            }
        }

        // ── A Steer input on another device, on a source at its defaults ──

        [Fact]
        public void MouseSpeedAtItsDefaults_ReadsASteerInputOnAKeyboard_OnThatKeyboard()
        {
            const string Keyboard = "11111111-2222-3333-4444-555555555555";
            var src = Source("Axis 0", MouseGuid);
            src.GateDescriptor = "Button 65";
            src.ParamTravelSteerDeviceGuid = Keyboard;
            src.ParamTravelSteer = MappingSource.TravelSteerHeldCenter;
            Assert.Equal(SourceEvaluator.MouseSteerFamily.None, SourceEvaluator.MouseSteerOf(src, MouseGuid, out _));
            bool down = false;
            SourceCoercion.KeyHeldProvider = (device, descriptor, slot) =>
                down && device == Keyboard && descriptor == "Button 65";
            var moving = Mouse();
            moving.Axis[0] = 49151;                               // half way right
            var rt = new SourceKindRuntime();
            Assert.Equal(0f, Stick(rt, src, moving, device: MouseGuid));
            down = true;
            Assert.InRange(Stick(rt, src, moving, device: MouseGuid), 0.4f, 0.6f);
            rt.FrameSeq++;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(moving, src, 25, 0, "ButtonA", 0, rt, 0.001, MouseGuid));
            rt.FrameSeq++;
            Assert.True(SourceEvaluator.EvaluateForTriggerTarget(moving, src, 0, "RightTrigger", 0, rt, 0.001, MouseGuid) > 0.5f);
        }

        // ── Mouse Speed and Mouse Position keep the plain read's shape ──

        private static float Trigger(MappingSource src)
        {
            var rt = new SourceKindRuntime();
            rt.FrameSeq++;
            return SourceEvaluator.EvaluateForTriggerTarget(Mouse(), src, 0, "RightTrigger", 0, rt, 0.001, MouseGuid);
        }

        private static bool Button(MappingSource src)
        {
            var rt = new SourceKindRuntime();
            rt.FrameSeq++;
            return SourceEvaluator.EvaluateForButtonTarget(Mouse(), src, 50, 0, "ButtonA", 0, rt, 0.001, MouseGuid);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void MouseSpeed_AtRest_ReadsTheSameOnBothSidesOfItsDefault(bool half, bool invert)
        {
            var plain = Source("Axis 0", MouseGuid);
            plain.HalfAxis = half;
            plain.Invert = invert;
            var own = Source("Axis 0", MouseGuid);
            own.HalfAxis = half;
            own.Invert = invert;
            own.ParamMouseSpeedSensitivity = 1.01;
            Assert.Equal(SourceEvaluator.MouseSteerFamily.None, SourceEvaluator.MouseSteerOf(plain, MouseGuid, out _));
            Assert.Equal(SourceEvaluator.MouseSteerFamily.Speed, SourceEvaluator.MouseSteerOf(own, MouseGuid, out _));
            Assert.Equal(Trigger(plain), Trigger(own), 4);
            Assert.Equal(Button(plain), Button(own));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MousePosition_AtRest_ReadsTheSameOnBothSidesOfItsDefault(bool invert)
        {
            SourceCoercion.MouseCursorProvider = () => (0f, 0f);
            SourceCoercion.MouseCursorPointProvider = null;
            var plain = Source("Mouse Position X");
            plain.Invert = invert;
            var own = Source("Mouse Position X");
            own.Invert = invert;
            own.ParamTravelSmoothingMs = 1;
            Assert.Equal(SourceEvaluator.MouseSteerFamily.Position, SourceEvaluator.MouseSteerOf(own, null, out _));
            Assert.Equal(Trigger(plain), Trigger(own), 4);
            Assert.Equal(Button(plain), Button(own));
        }
    }
}
