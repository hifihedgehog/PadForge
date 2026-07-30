using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine coverage for the translator v26 channels: the gravity-lean
    /// pair ("Gyro Lean X/Y", frame signs grounded on SDL_sensor.h's accel
    /// convention with Dolphin's SDLGamepad.h axis table as the proven
    /// consumer), the capsense bools (the fork's SDL_GetGamepadCapSense
    /// through CustomInputState.CapSense), the touchpad finger ring, the
    /// touch-surface flick stick vector, the POV any-direction read, the
    /// second AND companion, and the button-pair grid stepping.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopV26EngineTests
    {
        // ─── Gravity-lean pair: frame signs ─────────────────────────────
        //
        // Raw accelerometer = the REACTION force: +1g on the axis pointing
        // UP (SDL_sensor.h; Dolphin SDL_AXES_ACCELEROMETER consumes the
        // same frame). Held upright at rest: a = (0, +g, 0). Tilt RIGHT
        // (right edge down) leans world-up away from +X, so a.x goes
        // NEGATIVE; pitch the nose UP (top edge toward the player) turns
        // the face down, so a.z goes NEGATIVE. The lean read negates into
        // gravity-down and takes asin, so Lean X positive = tilt right and
        // Lean Y positive = nose up: the physical stick's own signs
        // (+X right, +Y down = pulled back).

        private const float G = 9.81f;

        private static (float, float, float) UprightRest => (0f, G, 0f);

        /// <summary>Tilted right by <paramref name="deg"/> from upright.</summary>
        private static (float, float, float) TiltedRight(double deg)
        {
            double r = deg * Math.PI / 180.0;
            return (-(float)(G * Math.Sin(r)), (float)(G * Math.Cos(r)), 0f);
        }

        /// <summary>Nose up by <paramref name="deg"/> from upright (top
        /// edge toward the player).</summary>
        private static (float, float, float) NoseUp(double deg)
        {
            double r = deg * Math.PI / 180.0;
            return (0f, (float)(G * Math.Cos(r)), -(float)(G * Math.Sin(r)));
        }

        private static float ReadLean(string axis, string guid)
        {
            var src = new MappingSource { Descriptor = axis, DeviceGuid = guid };
            return SourceCoercion.EvaluateForBipolarAxisTarget(new CustomInputState(), src,
                evaluatedDeviceGuid: guid);
        }

        [Fact]
        public void GyroLean_TiltRight_ReadsPositiveX_NoseUp_ReadsPositiveY()
        {
            var old = SourceCoercion.GravityProvider;
            string guid = Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                var sample = UprightRest;
                SourceCoercion.GravityProvider = _ => sample;

                // First stable sample captures the resting grip: zero lean.
                Assert.Equal(0f, ReadLean(SourceCoercion.GyroLeanXDescriptor, guid), 3);
                Assert.Equal(0f, ReadLean(SourceCoercion.GyroLeanYDescriptor, guid), 3);

                // Tilt right 30 degrees: +X, 30/90 of full scale, Y quiet.
                sample = TiltedRight(30);
                Assert.Equal(30f / 90f, ReadLean(SourceCoercion.GyroLeanXDescriptor, guid), 3);
                Assert.Equal(0f, ReadLean(SourceCoercion.GyroLeanYDescriptor, guid), 3);

                // Nose up 45 degrees: +Y (stick pulled back), X quiet.
                sample = NoseUp(45);
                Assert.Equal(0f, ReadLean(SourceCoercion.GyroLeanXDescriptor, guid), 3);
                Assert.Equal(45f / 90f, ReadLean(SourceCoercion.GyroLeanYDescriptor, guid), 3);

                // Tilt LEFT reads negative X.
                sample = TiltedRight(-30);
                Assert.Equal(-30f / 90f, ReadLean(SourceCoercion.GyroLeanXDescriptor, guid), 3);
            }
            finally
            {
                SourceCoercion.GravityProvider = old;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        [Fact]
        public void GyroLean_RestingGripIsTheNeutral_NotAbsoluteLevel()
        {
            // A natural grip pitched back 40 degrees captures as zero lean;
            // tilting right 30 from THAT grip still reads +X 30/90.
            var old = SourceCoercion.GravityProvider;
            string guid = Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                var sample = NoseUp(40);
                SourceCoercion.GravityProvider = _ => sample;

                Assert.Equal(0f, ReadLean(SourceCoercion.GyroLeanYDescriptor, guid), 3);

                // Roll right 30 degrees around the grip's own forward axis:
                // rotate the resting sample about the (pitched) Y' frame.
                // Simplest exact form: compose nose-up 40 with a pure right
                // tilt of the upright frame rotated into it.
                double p = 40 * Math.PI / 180.0, r = 30 * Math.PI / 180.0;
                // Upright right-tilt vector rotated nose-up by p about X:
                // a = Rx(-p) * (-g sin r, g cos r, 0).
                var tilted = (
                    -(float)(G * Math.Sin(r)),
                    (float)(G * Math.Cos(r) * Math.Cos(p)),
                    -(float)(G * Math.Cos(r) * Math.Sin(p)));
                sample = tilted;
                Assert.Equal(30f / 90f, ReadLean(SourceCoercion.GyroLeanXDescriptor, guid), 2);
            }
            finally
            {
                SourceCoercion.GravityProvider = old;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        [Fact]
        public void GyroLean_NoRealGravity_ReadsZero()
        {
            // The provider's unit-length no-data sentinel must not produce
            // a full-scale lean (the read gates on real ~9.8 magnitude).
            var old = SourceCoercion.GravityProvider;
            string guid = Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                SourceCoercion.GravityProvider = _ => (0f, 0f, -1f);
                Assert.Equal(0f, ReadLean(SourceCoercion.GyroLeanXDescriptor, guid), 3);
                Assert.Equal(0f, ReadLean(SourceCoercion.GyroLeanYDescriptor, guid), 3);
            }
            finally
            {
                SourceCoercion.GravityProvider = old;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        [Fact]
        public void GyroLean_WedgeBool_UsesHalfAxisAndInvertAsDirection()
        {
            var old = SourceCoercion.GravityProvider;
            string guid = Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                var sample = UprightRest;
                SourceCoercion.GravityProvider = _ => sample;
                _ = ReadLean(SourceCoercion.GyroLeanXDescriptor, guid); // capture neutral

                sample = TiltedRight(40);
                var east = new MappingSource
                {
                    Descriptor = SourceCoercion.GyroLeanXDescriptor,
                    DeviceGuid = guid,
                    HalfAxis = true,
                    DeadZone = 25,
                };
                var west = new MappingSource
                {
                    Descriptor = SourceCoercion.GyroLeanXDescriptor,
                    DeviceGuid = guid,
                    HalfAxis = true,
                    Invert = true,
                    DeadZone = 25,
                };
                var state = new CustomInputState();
                Assert.True(SourceCoercion.EvaluateForButtonTarget(state, east, 50,
                    evaluatedDeviceGuid: guid));
                Assert.False(SourceCoercion.EvaluateForButtonTarget(state, west, 50,
                    evaluatedDeviceGuid: guid));

                sample = TiltedRight(-40);
                Assert.False(SourceCoercion.EvaluateForButtonTarget(state, east, 50,
                    evaluatedDeviceGuid: guid));
                Assert.True(SourceCoercion.EvaluateForButtonTarget(state, west, 50,
                    evaluatedDeviceGuid: guid));

                // Below the 25% wedge (22.5 degrees): neither fires.
                sample = TiltedRight(15);
                Assert.False(SourceCoercion.EvaluateForButtonTarget(state, east, 50,
                    evaluatedDeviceGuid: guid));
                Assert.False(SourceCoercion.EvaluateForButtonTarget(state, west, 50,
                    evaluatedDeviceGuid: guid));
            }
            finally
            {
                SourceCoercion.GravityProvider = old;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        // ─── Capsense bools ─────────────────────────────────────────────

        [Fact]
        public void CapSense_ReadsTheChannelBool_NullArrayReadsFalse()
        {
            var s = new CustomInputState();
            var left = new MappingSource { Descriptor = SourceCoercion.CapSenseLeftStickDescriptor };
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, left, 50));

            s.CapSense = new bool[4];
            s.CapSense[0] = true; // SDL_GAMEPAD_CAPSENSE_LEFT_STICK
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, left, 50));
            Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(s, left), 3);
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(s, left), 3);

            var rightGrip = new MappingSource { Descriptor = SourceCoercion.CapSenseRightGripDescriptor };
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, rightGrip, 50));
            s.CapSense[3] = true; // SDL_GAMEPAD_CAPSENSE_RIGHT_GRIP
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, rightGrip, 50));
        }

        [Fact]
        public void CapSense_TableMapsAllFourChannels()
        {
            Assert.True(SourceCoercion.TryGetCapSenseChannel("Gamepad LeftStickTouch", out int c0));
            Assert.True(SourceCoercion.TryGetCapSenseChannel("Gamepad RightStickTouch", out int c1));
            Assert.True(SourceCoercion.TryGetCapSenseChannel("Gamepad LeftGripTouch", out int c2));
            Assert.True(SourceCoercion.TryGetCapSenseChannel("Gamepad RightGripTouch", out int c3));
            Assert.Equal(new[] { 0, 1, 2, 3 }, new[] { c0, c1, c2, c3 });
            Assert.False(SourceCoercion.TryGetCapSenseChannel("Gamepad ButtonA", out _));
        }

        // ─── Touchpad finger ring ───────────────────────────────────────

        private static CustomInputState PadState(float x, float y, bool down = true)
        {
            var s = new CustomInputState
            {
                Touchpads = new[] { new TouchpadInputState(2) },
            };
            s.Touchpads[0].FingerDown[0] = down;
            s.Touchpads[0].FingerX[0] = x;
            s.Touchpads[0].FingerY[0] = y;
            return s;
        }

        [Fact]
        public void TouchpadRing_OuterFiresPastTheRadius_InvertSelectsInner()
        {
            // Finger at x = 0.9: distance from center = 0.8.
            var s = PadState(0.9f, 0.5f);
            var outer = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Ring", DeadZone = 61 };
            var inner = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Ring", DeadZone = 61, Invert = true };
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, outer, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, inner, 50));

            // Centered touch: inside every inner ring, outside no outer.
            var centered = PadState(0.52f, 0.5f);
            Assert.False(SourceCoercion.EvaluateForButtonTarget(centered, outer, 50));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(centered, inner, 50));

            // No touch: nothing fires either way.
            var up = PadState(0.9f, 0.5f, down: false);
            Assert.False(SourceCoercion.EvaluateForButtonTarget(up, outer, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(up, inner, 50));
        }

        [Fact]
        public void TouchpadRing_HalfWindow_RenormalizesAndGates()
        {
            // Right-half ring: finger at x = 0.95 renormalizes to 0.9 in
            // the half's own square, distance 0.8.
            var s = PadState(0.95f, 0.5f);
            var src = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Ring Right", DeadZone = 61 };
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, src, 50));

            // A finger on the LEFT half never fires the right-half ring.
            var leftFinger = PadState(0.05f, 0.5f);
            Assert.False(SourceCoercion.EvaluateForButtonTarget(leftFinger, src, 50));
        }

        [Fact]
        public void TouchpadRing_AnalogReads_AreTheMagnitude()
        {
            var s = PadState(0.9f, 0.5f);
            var src = new MappingSource { Descriptor = "Touchpad 0 Finger 0 Ring" };
            Assert.Equal(0.8f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
            Assert.Equal(0.8f, SourceCoercion.EvaluateForTriggerTarget(s, src), 3);
        }

        // ─── POV any-direction ──────────────────────────────────────────

        [Fact]
        public void PovAny_FiresOnAnyDirection_NotOnCentered()
        {
            var s = new CustomInputState();
            var src = new MappingSource { Descriptor = "POV 0 Any" };
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, src, 50));
            s.Povs[0] = 9000; // East
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, src, 50));
            s.Povs[0] = 27000; // West
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, src, 50));
            s.Povs[0] = -1;
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, src, 50));
        }

        // ─── Second AND companion ───────────────────────────────────────

        [Fact]
        public void Gate2_BothCompanionsMustHold()
        {
            var s = new CustomInputState();
            s.Buttons[0] = true;  // host: Button 0
            var src = new MappingSource
            {
                Descriptor = "Button 0",
                GateDescriptor = "Button 4",
                Gate2Descriptor = "Button 5",
            };
            int slot = 0;
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(s, src, 50, slot, "t", 0, null, 0.016));
            s.Buttons[4] = true;
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(s, src, 50, slot, "t", 0, null, 0.016));
            s.Buttons[5] = true;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(s, src, 50, slot, "t", 0, null, 0.016));
            s.Buttons[4] = false;
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(s, src, 50, slot, "t", 0, null, 0.016));
        }

        // ─── Touch-surface flick stick ──────────────────────────────────

        [Fact]
        public void FlickStickTouchpad_ParsesAndExcludesTheAxisPair()
        {
            Assert.True(SourceCoercion.IsFlickStickDescriptor("Flick Stick Touchpad 1"));
            Assert.True(SourceCoercion.TryGetFlickStickTouchpad("Flick Stick Touchpad 1", out int p, out int h));
            Assert.Equal(1, p);
            Assert.Equal(0, h);
            Assert.True(SourceCoercion.TryGetFlickStickTouchpad("Flick Stick Touchpad 0 Right", out _, out int hr));
            Assert.Equal(2, hr); // TouchpadHalfRight
            // The touch forms never resolve as a stick axis pair (their
            // half suffix also ends with "Left").
            Assert.False(SourceCoercion.TryGetFlickStickAxes("Flick Stick Touchpad 0 Left", out _, out _));
        }

        [Fact]
        public void FlickStickTouchpad_EastTouchFlicksRight()
        {
            var rt = new SourceKindRuntime();
            var src = new MappingSource
            {
                Descriptor = "Flick Stick Touchpad 0",
                ParamFlickCountsPer360 = 14400,
                ParamFlickTime = 0.01,
            };

            // Arm idle with no finger, then touch the east edge: a 90
            // degree flick = 3600 counts, emitted through the easing.
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, PadState(0.5f, 0.5f, down: false), 0.004, seq++);
            var east = PadState(1f, 0.5f);
            for (int i = 0; i < 10; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, east, 0.004, seq++);
            Assert.InRange(total, 3550, 3650);

            // Lifting the finger releases (no further output).
            int after = rt.TickFlickStick(0, "KbmMouseX", 0, src,
                PadState(0.5f, 0.5f, down: false), 0.004, seq++);
            Assert.Equal(0, after);
        }

        [Fact]
        public void FlickStick_RotationOffset_LandsOnTheFlickAngle()
        {
            var rt = new SourceKindRuntime();
            var src = new MappingSource
            {
                Descriptor = "Flick Stick Touchpad 0",
                ParamFlickCountsPer360 = 14400,
                ParamFlickTime = 0.01,
                ParamFlickRotationOffsetDeg = 90,
            };
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, PadState(0.5f, 0.5f, down: false), 0.004, seq++);
            var east = PadState(1f, 0.5f);
            for (int i = 0; i < 10; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, east, 0.004, seq++);
            // East (90) plus the 90-degree clockwise offset = a 180 flick.
            Assert.InRange(total, 7100, 7300);
        }

        // ─── Button-pair grid stepping (hotbar) ─────────────────────────

        [Fact]
        public void ButtonPairGrid_StepsAndPulsesEachCell()
        {
            var st = new MenuRuntimeState();
            var def = new MenuDefinitionEntry { Kind = MenuKind.Grid, CellCount = 3 };
            long t = 1000;

            // First rising Right selects cell 0 and pulses it.
            MenuEvaluator.StepButtonPairGrid(st, def, true, false, false, false, true, t);
            Assert.Equal(0, st.StepIndex);
            Assert.True(MenuEvaluator.IsItemFired(st, 0, t));

            // Held Right does not step again; release then press steps to 1.
            MenuEvaluator.StepButtonPairGrid(st, def, true, false, false, false, true, t += 10);
            Assert.Equal(0, st.StepIndex);
            MenuEvaluator.StepButtonPairGrid(st, def, true, false, false, false, false, t += 10);
            MenuEvaluator.StepButtonPairGrid(st, def, true, false, false, false, true, t += 10);
            Assert.Equal(1, st.StepIndex);
            Assert.True(MenuEvaluator.IsItemFired(st, 1, t));

            // Left steps back; clamped at the low end.
            MenuEvaluator.StepButtonPairGrid(st, def, true, false, false, false, false, t += 10);
            MenuEvaluator.StepButtonPairGrid(st, def, true, false, false, true, false, t += 10);
            Assert.Equal(0, st.StepIndex);
            MenuEvaluator.StepButtonPairGrid(st, def, true, false, false, false, false, t += 10);
            MenuEvaluator.StepButtonPairGrid(st, def, true, false, false, true, false, t += 10);
            Assert.Equal(0, st.StepIndex); // clamp, no pulse for a non-move
            Assert.False(MenuEvaluator.IsItemFired(st, 0, t + MenuEvaluator.CommitPulseMs + 1));

            // The selection persists while the layer is off, and a press
            // during layer-off steps nothing.
            MenuEvaluator.StepButtonPairGrid(st, def, false, false, false, false, false, t += 10);
            MenuEvaluator.StepButtonPairGrid(st, def, false, false, false, false, true, t += 10);
            Assert.Equal(0, st.StepIndex);
        }

        // ─── Menu sensitivity DTO round-trip ────────────────────────────

        [Fact]
        public void MenuSensitivity_DefaultsTo100_AndPersistsAppendOnly()
        {
            Assert.Equal(100, new MenuDefinitionEntry().SensitivityPercent);
            var e = new MenuDefinitionEntry { SensitivityPercent = 150 };
            Assert.Equal(150, e.SensitivityPercent);
        }
    }
}
