using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Flick stick engine tests (#225, wave 4a). Math and behavior are pinned
    /// against JoyShockMapper's handleFlickStick (JoyShock.cpp:852-1017) and
    /// getSmoothedStickRotation (:353-392): flick angle from atan2 with the
    /// SDL-frame fold, telescoped ease-out totals, shortest-arc rotation
    /// wrap, snap modes, the forward deadzone, the 0.9x release hysteresis,
    /// and the layer engage/disengage arming the frame-sequence gap encodes
    /// (the #225 shift-layer host requirement).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class FlickStickTests
    {
        private const double Dt = 0.004;          // 250 Hz tick
        private const double Dots = 14400;        // JSM RWC 40 x 360

        private static MappingSource Src(
            double dots = Dots, double snapStrength = 1.0, string snapMode = "None",
            double deadzoneAngle = 0, double smooth = 0, bool flickOnEngage = false,
            double flickTime = 0.1, string descriptor = "Flick Stick Right")
            => new()
            {
                Descriptor = descriptor,
                ParamFlickCountsPer360 = dots,
                ParamFlickSnapMode = snapMode,
                ParamFlickSnapStrength = snapStrength,
                ParamFlickDeadzoneAngle = deadzoneAngle,
                ParamFlickSmooth = smooth,
                ParamFlickOnEngage = flickOnEngage,
                ParamFlickTime = flickTime,
            };

        /// <summary>Right-stick state at flick angle <paramref name="angleDeg"/>
        /// (0 = forward, -90 = right, +90 = left, 180 = back) and deflection
        /// <paramref name="len"/>. Inverts flickAngle = Atan2(-x, -y) in the
        /// SDL down-positive frame: x = -len*sin, y = -len*cos.</summary>
        private static CustomInputState StateAt(double angleDeg, double len = 1.0)
        {
            double rad = angleDeg * Math.PI / 180.0;
            var s = new CustomInputState();
            s.Axis[3] = 32768 + (int)Math.Round(-len * Math.Sin(rad) * 32767);
            s.Axis[4] = 32768 + (int)Math.Round(-len * Math.Cos(rad) * 32767);
            return s;
        }

        private static CustomInputState Neutral() => StateAt(0, 0);

        // ─── Flick angle + easing totals ────────────────────────────────

        [Fact]
        public void Flick_Right_EmitsQuarterTurnCounts_Rightward()
        {
            // Stick right = flick angle -90; counts telescope to exactly
            // angle * countsPerRadian with the JSM sign fold (negative
            // angle x negative scale = positive = rightward mouse X).
            var rt = new SourceKindRuntime();
            var src = Src();
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            var right = StateAt(-90);
            for (int i = 0; i < 40; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, right, Dt, seq++);
            Assert.InRange(total, 3600 - 3, 3600 + 3);
        }

        [Fact]
        public void Flick_Back_Emits180Turn()
        {
            // Straight back sits exactly on the +/-PI seam. A centered X
            // axis reads +0.0, so Atan2(-0.0, -1) = -PI and the half turn
            // goes RIGHT, exactly what JSM's atan2f(-offsetX, offsetY)
            // computes for the same input (JoyShock.cpp:871). The magnitude
            // is the contract; the seam side is the atan2 zero-sign fold.
            var rt = new SourceKindRuntime();
            var src = Src();
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            var back = StateAt(180);
            for (int i = 0; i < 40; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, back, Dt, seq++);
            Assert.InRange(total, 7200 - 3, 7200 + 3);
        }

        [Fact]
        public void Flick_BelowThreshold_EmitsNothing()
        {
            var rt = new SourceKindRuntime();
            var src = Src();
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            var half = StateAt(-90, 0.5);
            for (int i = 0; i < 40; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, half, Dt, seq++);
            Assert.Equal(0, total);
        }

        [Fact]
        public void Flick_EasingContinues_AfterReleaseBelowThreshold()
        {
            // JSM's easing runs on time-since-flick regardless of rim state
            // (JoyShock.cpp:957+): releasing the stick mid-flick still
            // completes the turn while the row stays evaluated.
            var rt = new SourceKindRuntime();
            var src = Src();
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            var right = StateAt(-90);
            for (int i = 0; i < 3; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, right, Dt, seq++);
            var neutral = Neutral();
            for (int i = 0; i < 40; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, neutral, Dt, seq++);
            Assert.InRange(total, 3600 - 3, 3600 + 3);
        }

        // ─── Rotation tracking ──────────────────────────────────────────

        [Fact]
        public void Rotation_ClockwiseAtRim_EmitsRightwardCounts()
        {
            // Forward deadzone 181 folds every flick to 0 degrees so the
            // sums below are pure rotation. Clockwise = decreasing flick
            // angle = positive (rightward) counts, JoyShock.cpp:923-924.
            var rt = new SourceKindRuntime();
            var src = Src(deadzoneAngle: 181);
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            total += rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-90), Dt, seq++);
            Assert.Equal(0, total); // flick folded to 0 by the deadzone
            for (int i = 1; i <= 10; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-90 - 3 * i), Dt, seq++);
            Assert.InRange(total, 1200 - 5, 1200 + 5); // 30 deg = 1200 counts
        }

        [Fact]
        public void Rotation_WrapAroundBack_TakesShortestArc()
        {
            // +175 to -175 through the back is a 10-degree counterclockwise
            // move, not a 350-degree spin (the fmod wrap,
            // JoyShock.cpp:916-921).
            var rt = new SourceKindRuntime();
            var src = Src(deadzoneAngle: 181);
            long seq = 1;
            rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(175), Dt, seq++);
            int step = rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-175), Dt, seq++);
            Assert.InRange(step, -400 - 4, -400 + 4); // 10 deg CCW = -400 counts
        }

        [Fact]
        public void Rotation_SmallSteps_AreSmoothedOverWindow_TotalConserved()
        {
            // Default auto smoothing (JoyShock.cpp:926-933): a step below the
            // lower threshold is fully window-averaged. One 0.3-degree step
            // (12 counts, below the 2 x 0.01 rad threshold) must NOT arrive
            // in one tick, and must fully arrive over the 64 ms window.
            var rt = new SourceKindRuntime();
            var src = Src(smooth: -1, deadzoneAngle: 181);
            long seq = 1;
            rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-90), Dt, seq++);
            int first = rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-90.3), Dt, seq++);
            Assert.InRange(first, 0, 3); // 12 counts / 16-sample window per tick
            int total = first;
            var held = StateAt(-90.3);
            for (int i = 0; i < 30; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, held, Dt, seq++);
            Assert.InRange(total, 12 - 2, 12 + 2);
        }

        [Fact]
        public void Rotation_ReleaseHysteresis_TracksTo81Percent_ThenReleases()
        {
            // While flicking the threshold drops to 0.9 x 0.9 (JoyShock.cpp:
            // 864-868): 0.85 deflection keeps tracking, 0.7 releases, and
            // 0.85 does NOT re-arm afterward.
            var rt = new SourceKindRuntime();
            var src = Src(deadzoneAngle: 181);
            long seq = 1;
            rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-90), Dt, seq++);
            int tracked = rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-93, 0.85), Dt, seq++);
            Assert.InRange(tracked, 120 - 3, 120 + 3); // 3 deg CW while held
            rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-93, 0.7), Dt, seq++); // releases
            int after = rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-99, 0.85), Dt, seq++);
            Assert.Equal(0, after); // 0.85 < 0.9: no re-flick, no tracking
        }

        // ─── Snap modes + forward deadzone ──────────────────────────────

        [Theory]
        [InlineData("Four", -50.0, 3600)]   // nearest 90: -50 -> -90
        [InlineData("Four", -40.0, 0)]      // nearest 90: -40 -> 0
        [InlineData("Eight", -50.0, 1800)]  // nearest 45: -50 -> -45
        [InlineData("Half", -100.0, 7200)]  // nearest 180: -100 -> -180
        [InlineData("Sixths", -50.0, 2400)] // nearest 60: -50 -> -60
        [InlineData("Forward", -50.0, 0)]   // everything snaps to forward
        [InlineData("Forward", 180.0, 0)]   // the +/-PI boundary must fold to 0, not a 360 spin
        public void Snap_FullStrength_SnapsToNearestInterval(string mode, double angle, int expected)
        {
            var rt = new SourceKindRuntime();
            var src = Src(snapMode: mode);
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            var at = StateAt(angle);
            for (int i = 0; i < 40; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, at, Dt, seq++);
            Assert.InRange(total, expected - 4, expected + 4);
        }

        [Fact]
        public void Snap_HalfStrength_BlendsTowardInterval()
        {
            // JSM lerps by FLICK_SNAP_STRENGTH (JoyShock.cpp:892-895):
            // -80 blended halfway to -90 is -85 -> 3400 counts.
            var rt = new SourceKindRuntime();
            var src = Src(snapMode: "Four", snapStrength: 0.5);
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            var at = StateAt(-80);
            for (int i = 0; i < 40; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, at, Dt, seq++);
            Assert.InRange(total, 3400 - 4, 3400 + 4);
        }

        [Fact]
        public void ForwardDeadzone_FoldsSmallFlickToZero()
        {
            // FLICK_DEADZONE_ANGLE (JoyShock.cpp:897-900): a 5-degree flick
            // inside a 10-degree forward deadzone turns nothing.
            var rt = new SourceKindRuntime();
            var src = Src(deadzoneAngle: 10);
            long seq = 1;
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            var at = StateAt(-5);
            for (int i = 0; i < 40; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, at, Dt, seq++);
            Assert.Equal(0, total);
        }

        // ─── Layer engage / disengage (#225 headline) ───────────────────

        [Fact]
        public void LayerEngage_StickAlreadyDeflected_ArmsWithoutFlick_RotationLive()
        {
            // Default arm semantics: a frame-sequence gap (the row was
            // suppressed while its layer was off) with the stick already at
            // the rim arms at the current angle with NO flick; rotation
            // tracking is live from the baseline.
            var rt = new SourceKindRuntime();
            var src = Src(deadzoneAngle: 0);
            // First-ever evaluation with the stick already deflected right.
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-90), Dt, 10);
            var held = StateAt(-90);
            for (int i = 1; i <= 20; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, held, Dt, 10 + i);
            Assert.Equal(0, total); // no spurious flick, no drift
            // Rotation from the baseline works immediately.
            int rot = 0;
            for (int i = 1; i <= 4; i++)
                rot += rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-90 - 5 * i), Dt, 30 + i);
            Assert.InRange(rot, 800 - 5, 800 + 5); // 20 deg CW
        }

        [Fact]
        public void LayerEngage_FlickOnEngage_FiresTheFlick()
        {
            // ParamFlickOnEngage = JSM's behavior (a deflected stick is a
            // "new flick!", JoyShock.cpp:873-876) and Steam's "Allow Flick
            // on Awake" ON.
            var rt = new SourceKindRuntime();
            var src = Src(flickOnEngage: true);
            int total = rt.TickFlickStick(0, "KbmMouseX", 0, src, StateAt(-90), Dt, 10);
            var held = StateAt(-90);
            for (int i = 1; i <= 40; i++)
                total += rt.TickFlickStick(0, "KbmMouseX", 0, src, held, Dt, 10 + i);
            Assert.InRange(total, 3600 - 3, 3600 + 3);
        }

        [Fact]
        public void LayerExit_MidFlick_LeavesNoTail()
        {
            // Divergence from JSM (JoyShock.h:168-190 completes an in-flight
            // flick on a chord change): #225 requires no residual camera
            // motion after layer exit, so the easing tail dies with the
            // suppression gap and the re-engage starts clean.
            var rt = new SourceKindRuntime();
            var src = Src();
            long seq = 1;
            int before = rt.TickFlickStick(0, "KbmMouseX", 0, src, Neutral(), Dt, seq++);
            var right = StateAt(-90);
            for (int i = 0; i < 4; i++)
                before += rt.TickFlickStick(0, "KbmMouseX", 0, src, right, Dt, seq++);
            Assert.InRange(before, 1, 3599); // easing started but incomplete
            // Layer off: the row is not evaluated for many frames. Re-engage
            // with the stick back at neutral.
            int after = 0;
            long resume = seq + 50;
            var neutral = Neutral();
            for (int i = 0; i < 40; i++)
                after += rt.TickFlickStick(0, "KbmMouseX", 0, src, neutral, Dt, resume + i);
            Assert.Equal(0, after);
        }

        [Fact]
        public void SameFrameSecondDevicePass_Replays_WithoutDoubleCounting()
        {
            // The KBM evaluator runs once per assigned device per frame; the
            // second pass must replay the frame's counts, not re-advance
            // (the StickTrim LastSeq idiom).
            var rt = new SourceKindRuntime();
            var src = Src();
            long seq = 1;
            int total = 0;
            var right = StateAt(-90);
            var neutral = Neutral();
            for (int i = 0; i < 41; i++)
            {
                var st = i == 0 ? neutral : right;
                int first = rt.TickFlickStick(0, "KbmMouseX", 0, src, st, Dt, seq);
                int second = rt.TickFlickStick(0, "KbmMouseX", 0, src, st, Dt, seq);
                Assert.Equal(first, second);
                total += first;
                seq++;
            }
            Assert.InRange(total, 3600 - 3, 3600 + 3);
        }

        // ─── Descriptor grammar + persistence ───────────────────────────

        [Fact]
        public void Descriptors_ClassifyAndResolve()
        {
            Assert.True(SourceCoercion.IsFlickStickDescriptor("Flick Stick Right"));
            Assert.True(SourceCoercion.IsFlickStickDescriptor(" flick stick left "));
            Assert.False(SourceCoercion.IsFlickStickDescriptor("Flick Stick"));
            Assert.Equal(SourceCoercion.SourceType.FlickStick,
                SourceCoercion.ClassifyDescriptor("Flick Stick Right"));

            Assert.True(SourceCoercion.TryGetFlickStickAxes("Flick Stick Right", out var rx, out var ry));
            Assert.Equal("Axis 3", rx);
            Assert.Equal("Axis 4", ry);
            Assert.True(SourceCoercion.TryGetFlickStickAxes("Flick Stick Left", out var lx, out var ly));
            Assert.Equal("Axis 0", lx);
            Assert.Equal("Axis 1", ly);
            Assert.False(SourceCoercion.TryGetFlickStickAxes("Gyro Yaw", out _, out _));

            // Leading 'F': never a target of the legacy I/H prefix grammar,
            // and no generic sensitivity slider (the flick knobs own tuning).
            Assert.False(SourceCoercion.IsPrefixExemptDescriptor("Flick Stick Right"));
            Assert.False(SourceCoercion.IsGenericSensitivityDescriptor("Flick Stick Right"));
        }

        [Fact]
        public void FlickParams_SurviveXmlRoundTrip()
        {
            var set = new MappingSet();
            set.Rows.Add(new MappingRow
            {
                Target = "KbmMouseX",
                LayerMask = "Shift",
                Sources =
                {
                    new MappingSource
                    {
                        Descriptor = "Flick Stick Right",
                        ParamFlickCountsPer360 = 2788,
                        ParamFlickTime = 0.2,
                        ParamFlickThreshold = 0.8,
                        ParamFlickSnapMode = "Eight",
                        ParamFlickSnapStrength = 0.5,
                        ParamFlickDeadzoneAngle = 7,
                        ParamFlickSmooth = 0.02,
                        ParamFlickOnEngage = true,
                    },
                },
            });

            var ser = new System.Xml.Serialization.XmlSerializer(typeof(MappingSet));
            using var ms = new System.IO.MemoryStream();
            ser.Serialize(ms, set);
            ms.Position = 0;
            var back = (MappingSet)ser.Deserialize(ms);

            var s = Assert.Single(Assert.Single(back.Rows).Sources);
            Assert.Equal("Flick Stick Right", s.Descriptor);
            Assert.Equal(2788, s.ParamFlickCountsPer360, 3);
            Assert.Equal(0.2, s.ParamFlickTime, 3);
            Assert.Equal(0.8, s.ParamFlickThreshold, 3);
            Assert.Equal("Eight", s.ParamFlickSnapMode);
            Assert.Equal(0.5, s.ParamFlickSnapStrength, 3);
            Assert.Equal(7, s.ParamFlickDeadzoneAngle, 3);
            Assert.Equal(0.02, s.ParamFlickSmooth, 3);
            Assert.True(s.ParamFlickOnEngage);
        }

        [Fact]
        public void SnapIntervals_MatchVocabulary()
        {
            Assert.Equal(0.0, SourceKindRuntime.FlickSnapIntervalRad("None"));
            Assert.Equal(0.0, SourceKindRuntime.FlickSnapIntervalRad("Unknown"));
            Assert.Equal(2 * Math.PI, SourceKindRuntime.FlickSnapIntervalRad("Forward"), 9);
            Assert.Equal(Math.PI, SourceKindRuntime.FlickSnapIntervalRad("Half"), 9);
            Assert.Equal(Math.PI / 2, SourceKindRuntime.FlickSnapIntervalRad("Four"), 9);
            Assert.Equal(Math.PI / 3, SourceKindRuntime.FlickSnapIntervalRad("Sixths"), 9);
            Assert.Equal(Math.PI / 4, SourceKindRuntime.FlickSnapIntervalRad("Eight"), 9);
        }
    }
}
