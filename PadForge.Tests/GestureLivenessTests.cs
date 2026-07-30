using System;
using System.Collections.Generic;
using System.Numerics;
using PadForge.Engine;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-33 audit guards for the GestureRecognizer liveness family:
    /// a LIFTED finger's frozen path must never drive the radial zones,
    /// the two-finger pair, the joystick output, or the end-of-gesture
    /// finger count while a LIVE finger exists. Every test here was
    /// written to FAIL against the pre-fix selection ("first non-empty
    /// path" / accumulated path count), which is its mutation proof.
    /// Also covers ComputeJoystickAxis / ComputeJoystickDPad, which had
    /// zero test references against 50 for sibling Update.
    /// </summary>
    public class GestureLivenessTests
    {
        private static TouchpadInputState Pad(int fingers = 3) => new TouchpadInputState(fingers);

        private static void SetFinger(TouchpadInputState pad, int slot, bool down, float x, float y, int contactId)
        {
            pad.FingerDown[slot] = down;
            pad.FingerX[slot] = x;
            pad.FingerY[slot] = y;
            pad.FingerContactId[slot] = down ? contactId : -1;
        }

        // ── ComputeJoystickAxis basics (zero-coverage gap, C9) ────────

        [Fact]
        public void JoystickAxis_InsideDeadzone_IsZero()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings { EnableJoystickOutput = true, JoystickInnerDeadzone = 0.05f };
            var pad = Pad();
            SetFinger(pad, 0, true, 0.5f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 100);
            SetFinger(pad, 0, true, 0.51f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 116);
            Assert.Equal((0f, 0f), GestureRecognizer.ComputeJoystickAxis(ctx, s));
        }

        [Fact]
        public void JoystickAxis_AnchorRelative_Proportional_AndClamped()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings { EnableJoystickOutput = true, JoystickMaxRadius = 0.30f };
            var pad = Pad();
            SetFinger(pad, 0, true, 0.4f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 100);
            SetFinger(pad, 0, true, 0.55f, 0.5f, 1); // +0.15 = half of maxR
            GestureRecognizer.Update(0, ctx, pad, s, 116);
            var (x, y) = GestureRecognizer.ComputeJoystickAxis(ctx, s);
            Assert.Equal(0.5f, x, 2);
            Assert.Equal(0f, y, 2);

            SetFinger(pad, 0, true, 0.95f, 0.5f, 1); // +0.55 >> maxR: clamps to unit
            GestureRecognizer.Update(0, ctx, pad, s, 132);
            (x, y) = GestureRecognizer.ComputeJoystickAxis(ctx, s);
            Assert.Equal(1f, MathF.Sqrt(x * x + y * y), 2);
        }

        // ── Liveness: joystick follows the live finger (C10) ──────────

        [Fact]
        public void JoystickAxis_FollowsLiveFinger_NotLiftedPath()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings { EnableJoystickOutput = true, JoystickMaxRadius = 0.30f };
            var pad = Pad();

            // Finger A anchors at (0.5, 0.5) and deflects hard right.
            SetFinger(pad, 0, true, 0.5f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 100);
            SetFinger(pad, 0, true, 0.8f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 116);

            // Finger B lands, then A lifts, then B deflects DOWN.
            SetFinger(pad, 1, true, 0.3f, 0.5f, 2);
            GestureRecognizer.Update(0, ctx, pad, s, 132);
            SetFinger(pad, 0, false, 0.8f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 148);
            SetFinger(pad, 1, true, 0.3f, 0.65f, 2);
            GestureRecognizer.Update(0, ctx, pad, s, 164);

            var (x, y) = GestureRecognizer.ComputeJoystickAxis(ctx, s);
            // Pre-fix: A's frozen path drove (+1, 0). Live finger B says (0, +0.5).
            Assert.Equal(0f, x, 2);
            Assert.Equal(0.5f, y, 2);
        }

        [Fact]
        public void JoystickDPad_FollowsLiveFinger_NotLiftedPath()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings
            {
                EnableJoystickOutput = true,
                JoystickDPadMode = "FourWay",
                JoystickDPadActivationThreshold = 0.10f,
            };
            var pad = Pad();

            SetFinger(pad, 0, true, 0.5f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 100);
            SetFinger(pad, 0, true, 0.8f, 0.5f, 1);   // A: right
            GestureRecognizer.Update(0, ctx, pad, s, 116);
            SetFinger(pad, 1, true, 0.5f, 0.6f, 2);   // B lands
            GestureRecognizer.Update(0, ctx, pad, s, 132);
            SetFinger(pad, 0, false, 0.8f, 0.5f, 1);  // A lifts
            GestureRecognizer.Update(0, ctx, pad, s, 148);
            SetFinger(pad, 1, true, 0.5f, 0.8f, 2);   // B: down
            GestureRecognizer.Update(0, ctx, pad, s, 164);

            var (up, right, down, left) = GestureRecognizer.ComputeJoystickDPad(ctx, s);
            Assert.False(right); // pre-fix: A's frozen path said right
            Assert.True(down);
            Assert.False(up);
            Assert.False(left);
        }

        // ── Cooldown no longer blacks out joystick-only mode (C7) ─────

        [Fact]
        public void JoystickOnly_TracksThroughCooldown_WithTrueAnchor()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings
            {
                Enabled = false,
                EnableJoystickOutput = true,
                CooldownMs = 200,
                JoystickMaxRadius = 0.30f,
            };
            var pad = Pad();

            // Touch, move, lift: enters Cooldown until t=~348+200.
            SetFinger(pad, 0, true, 0.5f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 300);
            SetFinger(pad, 0, true, 0.6f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 316);
            SetFinger(pad, 0, false, 0.6f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 332);

            // New touch INSIDE the cooldown window, anchored at (0.2, 0.2),
            // deflecting right. Pre-fix: Update returned before path
            // tracking, so the stick was dead for the whole window.
            SetFinger(pad, 0, true, 0.2f, 0.2f, 2);
            GestureRecognizer.Update(0, ctx, pad, s, 380);
            SetFinger(pad, 0, true, 0.35f, 0.2f, 2);
            GestureRecognizer.Update(0, ctx, pad, s, 396);

            var (x, y) = GestureRecognizer.ComputeJoystickAxis(ctx, s);
            Assert.Equal(0.5f, x, 2);
            Assert.Equal(0f, y, 2);
        }

        // ── Radial zones follow the live finger (C5) ──────────────────

        [Fact]
        public void RadialZone_FollowsLiveFinger_AfterFirstLifts()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings
            {
                Enabled = true,
                EnableRadialZones = true,
                RadialZoneCount = 8,
                RadialCenterDeadzone = 0.10f,
                Mode = "Both",
            };
            var pad = Pad();

            // A moves RIGHT (zone 2 on the 8-wheel).
            SetFinger(pad, 0, true, 0.5f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 100);
            SetFinger(pad, 0, true, 0.8f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 116);
            Assert.Contains("Touchpad 0 RadialZone8_2", ctx.FiredGesturesThisFrame);

            // B lands (2 fingers: radial pauses), A lifts, B moves UP.
            SetFinger(pad, 1, true, 0.5f, 0.5f, 2);
            GestureRecognizer.Update(0, ctx, pad, s, 132);
            SetFinger(pad, 0, false, 0.8f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 148);
            SetFinger(pad, 1, true, 0.5f, 0.2f, 2);
            GestureRecognizer.Update(0, ctx, pad, s, 164);

            // Pre-fix: path[0] was A's frozen RIGHT, zone stayed 2 forever.
            Assert.Contains("Touchpad 0 RadialZone8_0", ctx.FiredGesturesThisFrame);
        }

        // ── Two-finger pair uses live fingers (C6/C11) ────────────────

        [Fact]
        public void Pinch_UsesLiveFingers_AfterSubstitution()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings
            {
                Enabled = true,
                EnablePinchSpread = true,
                PinchThreshold = 0.30f,
                Mode = "Both",
            };
            var pad = Pad();

            // A + B down (a session forms after the 30 ms entry delay).
            SetFinger(pad, 0, true, 0.2f, 0.5f, 1);
            SetFinger(pad, 1, true, 0.8f, 0.5f, 2);
            GestureRecognizer.Update(0, ctx, pad, s, 100);
            GestureRecognizer.Update(0, ctx, pad, s, 140);

            // A lifts; C lands. Live pair is now B + C.
            SetFinger(pad, 0, false, 0.2f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, 156);
            SetFinger(pad, 2, true, 0.6f, 0.5f, 3);
            GestureRecognizer.Update(0, ctx, pad, s, 172);
            GestureRecognizer.Update(0, ctx, pad, s, 210); // entry delay passes; baseline B-C = 0.2

            // C closes toward B: live-pair ratio -0.6 crosses the pinch
            // threshold. Pre-fix the pair was frozen-A + B, whose distance
            // never changes, so Pinch never fired.
            SetFinger(pad, 2, true, 0.72f, 0.5f, 3);
            GestureRecognizer.Update(0, ctx, pad, s, 226);
            Assert.Contains("Touchpad 0 Pinch", ctx.FiredGesturesThisFrame);
        }

        // ── Rotation unwrap past 180 degrees (C8) ─────────────────────

        [Fact]
        public void Rotate_Past180Degrees_NeverFiresOppositeDirection()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings
            {
                Enabled = true,
                EnableRotate = true,
                RotateThresholdDegrees = 45f,
                Mode = "Both",
            };
            var pad = Pad();

            // A fixed at center; B orbits it in +10 degree steps for 220
            // degrees total. Pre-fix the folded delta wrapped at 180 and
            // fired the opposite rotation on top of the first.
            SetFinger(pad, 0, true, 0.5f, 0.5f, 1);
            SetFinger(pad, 1, true, 0.8f, 0.5f, 2);
            GestureRecognizer.Update(0, ctx, pad, s, 100);
            long t = 140;
            GestureRecognizer.Update(0, ctx, pad, s, t); // session entry
            for (int deg = 10; deg <= 220; deg += 10)
            {
                t += 16;
                float rad = deg * MathF.PI / 180f;
                SetFinger(pad, 1, true,
                    0.5f + 0.3f * MathF.Cos(rad),
                    0.5f + 0.3f * MathF.Sin(rad), 2);
                GestureRecognizer.Update(0, ctx, pad, s, t);
            }

            Assert.Contains("Touchpad 0 RotateCW", ctx.FiredGesturesThisFrame);
            Assert.DoesNotContain("Touchpad 0 RotateCCW", ctx.FiredGesturesThisFrame);
        }

        // ── Contact bounce keeps the gesture's true finger count (C13) ─

        [Fact]
        public void TwoFingerSwipe_SurvivesContactBounce()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings
            {
                Enabled = true,
                EnableTwoFingerSwipes = true,
                EnableEightWaySwipes = false,
                EnableFourWaySwipes = true,
                SwipeDistanceThreshold = 0.20f,
                TwoFingerSwipeAngularTolerance = 25f,
                SwipeTimeWindowMs = 2000,
                Mode = "Both",
            };
            var pad = Pad();

            // A and B swipe right together; B bounces early (new contact
            // id) and finishes the swipe. Pre-fix the accumulated path
            // count (3) reclassified this as a "three finger" gesture and
            // the two-finger branch never ran.
            SetFinger(pad, 0, true, 0.20f, 0.4f, 1);
            SetFinger(pad, 1, true, 0.20f, 0.6f, 2);
            GestureRecognizer.Update(0, ctx, pad, s, 100);

            SetFinger(pad, 0, true, 0.30f, 0.4f, 1);
            SetFinger(pad, 1, false, 0.30f, 0.6f, 2); // B lifts for one tick
            GestureRecognizer.Update(0, ctx, pad, s, 116);

            SetFinger(pad, 1, true, 0.32f, 0.6f, 5);  // B re-lands, NEW id
            GestureRecognizer.Update(0, ctx, pad, s, 132);

            SetFinger(pad, 0, true, 0.70f, 0.4f, 1);
            SetFinger(pad, 1, true, 0.70f, 0.6f, 5);
            GestureRecognizer.Update(0, ctx, pad, s, 200);

            SetFinger(pad, 0, false, 0.70f, 0.4f, 1);
            SetFinger(pad, 1, false, 0.70f, 0.6f, 5);
            GestureRecognizer.Update(0, ctx, pad, s, 216);

            Assert.Contains("Touchpad 0 TwoFingerSwipeRight", ctx.FiredGesturesThisFrame);
        }

        // ── Shape gate: in-box templates stay off with the toggle (C12) ─

        [Fact]
        public void InBoxShape_DoesNotFire_WhenShapeGesturesOff_DespiteCustomPresent()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings
            {
                Enabled = true,
                EnableShapeGestures = false,
                GestureMatchThreshold = 3.0f,
                Mode = "Both",
            };
            // Catalog: full in-box set PLUS one enabled custom 1-finger
            // zigzag. Pre-fix the presence of the custom template let the
            // point-cloud matcher see the WHOLE catalog and fire "Circle".
            var catalog = new List<ShapeTemplate>(InBoxShapeTemplates.Build());
            catalog.Add(MakeCustomZigzag());

            DrawCircle(ctx, s, catalog);

            foreach (var name in InBoxShapeTemplates.Names)
                Assert.DoesNotContain($"Touchpad 0 {name}", ctx.FiredGesturesThisFrame);
        }

        // ── Mode filter is case-insensitive (C15) ─────────────────────

        [Fact]
        public void Mode_CustomOnly_LowerCase_StillFiltersInBoxShapes()
        {
            var ctx = new TouchpadGestureContext();
            var s = new TouchpadGestureSettings
            {
                Enabled = true,
                EnableShapeGestures = true,
                GestureMatchThreshold = 3.0f,
                Mode = "customonly", // hand-edited XML casing
            };
            var catalog = new List<ShapeTemplate>(InBoxShapeTemplates.Build());

            DrawCircle(ctx, s, catalog);

            foreach (var name in InBoxShapeTemplates.Names)
                Assert.DoesNotContain($"Touchpad 0 {name}", ctx.FiredGesturesThisFrame);
        }

        // ── helpers ───────────────────────────────────────────────────

        private static ShapeTemplate MakeCustomZigzag()
        {
            var pts = new List<Vector2>();
            for (int i = 0; i <= 16; i++)
            {
                float x = i / 16f;
                float y = (i % 2 == 0) ? 0.2f : 0.8f;
                pts.Add(new Vector2(x, y));
            }
            return new ShapeTemplate
            {
                Name = "TestZigzag",
                FingerCount = 1,
                IsCustom = true,
                Enabled = true,
                PointCloud = ShapeRecognizer.BuildCloud(
                    new List<IReadOnlyList<Vector2>> { pts }, 32),
            };
        }

        private static void DrawCircle(TouchpadGestureContext ctx,
            TouchpadGestureSettings s, List<ShapeTemplate> catalog)
        {
            var pad = Pad();
            long t = 100;
            for (int i = 0; i <= 32; i++)
            {
                float a = i / 32f * 2f * MathF.PI;
                SetFinger(pad, 0, true,
                    0.5f + 0.25f * MathF.Cos(a),
                    0.5f + 0.25f * MathF.Sin(a), 1);
                GestureRecognizer.Update(0, ctx, pad, s, t, catalog);
                t += 10;
            }
            SetFinger(pad, 0, false, 0.75f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, s, t + 10, catalog);
        }
    }
}
