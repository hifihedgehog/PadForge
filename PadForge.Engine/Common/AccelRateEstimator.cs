using System;
using System.Numerics;

namespace PadForge.Engine
{
    /// <summary>
    /// The rotation rate an accelerometer can see, in radians per second, in
    /// SDL's sensor frame (#472). At rest the accelerometer measures gravity,
    /// a direction fixed in the world, so in the controller's frame it turns
    /// opposite to the controller: dS/dt = -w x S (Oshman and Dellus,
    /// J. Spacecraft and Rockets 40(2), 2003, Eq. 5). Between two unit
    /// directions the visible rate is normalize(S2 x S1) * angle / dt, the
    /// part of the rotation perpendicular to gravity. Rotation about gravity
    /// leaves S where it was and reads zero.
    ///
    /// The rate between consecutive smoothed directions follows
    /// GamepadMotionHelpers' AddSampleSensorFusion, GamepadMotion.hpp:952-970,
    /// revision 39b578aacf34c3a1c584d8f7f194adc776f88055 (MIT).
    /// Copyright (c) 2020-2023 Julian "Jibb" Smart. Full notice in LICENSE.
    /// Unlike that calibration routine, the smoothing advances on every poll
    /// with the poll's own time, as the Gyro Tilt estimator does, instead of
    /// skipping repeated samples. The exponential composes exactly over the
    /// repeats between a device's reports, while a skipped repeat holds the
    /// last rate over the next interval and reads slow turns high.
    /// </summary>
    public sealed class AccelRateEstimator
    {
        public const float StandardGravity = 9.80665f;

        /// <summary>A gap longer than this reseeds instead of turning unseen
        /// motion into one burst, the bound the Gyro Tilt estimator and the
        /// mapping pipeline use. A shove that lasts longer reseeds too.</summary>
        public const float ResumeGapSeconds = 0.25f;

        /// <summary>A sample whose length strays more than this, in g, from
        /// the length the pad rests at is a shove rather than gravity. yuzu
        /// gates calibrated input on 0.75 g to 1.25 g for controllers without
        /// a gyro (eden 1dcc5745, src/hid_core/frontend/motion_input.cpp:166).
        /// PadForge's own DS3 path publishes the accelerometer against 512
        /// with no per-pad zero, and a genuine pad lying flat reads 1.18 g
        /// there (DsHidMini docs/MOTION.md, DS3-E), so the window centers on
        /// the pad's own resting length. Calibrated input rests at 1 g and
        /// gets yuzu's window. A push sideways or fore and aft under about
        /// 0.75 g keeps the length inside it and reads as tilt.</summary>
        public const float ShoveG = 0.25f;

        /// <summary>The estimate starts only from a sample inside this
        /// window, in g, which holds every resting length on record, and
        /// the resting length stays inside it.</summary>
        public const float MinGravityG = 0.75f;
        public const float MaxGravityG = 1.25f;

        /// <summary>How fast the resting length follows the pad. Without a
        /// per-pad zero the length at rest changes with the pad's attitude:
        /// DS3-E reads 1.18 g lying flat and 0.81 g upside down.</summary>
        public const float RestSeconds = 1f;

        private Vector3 _smoothed;
        private float _shoveSeconds;
        private float _restG;

        public bool HasValue { get; private set; }

        /// <summary>The visible rotation rate, rad/s. Zero until seeded and
        /// while a shove is under way.</summary>
        public Vector3 Rate { get; private set; }

        public void Reset()
        {
            HasValue = false;
            _smoothed = Vector3.Zero;
            _shoveSeconds = 0f;
            _restG = 0f;
            Rate = Vector3.Zero;
        }

        /// <param name="acceleration">The latest accelerometer reading, m/s2.</param>
        /// <param name="deltaSeconds">Time since the previous call.</param>
        /// <param name="smoothingSeconds">The smoothing time constant.</param>
        public void Update(Vector3 acceleration, float deltaSeconds, float smoothingSeconds)
        {
            // No real accelerometer reads exactly zero on all three axes. The
            // SXS feature cache does until the pad's first report.
            // GamepadMotionHelpers resets only when the gyro reads zero too
            // (GamepadMotion.hpp:904-905), but a DS3's yaw rests off center.
            if (!Finite(acceleration) || acceleration == Vector3.Zero
                || !float.IsFinite(deltaSeconds) || deltaSeconds < 0f)
            {
                Reset();
                return;
            }
            if (!HasValue || deltaSeconds > ResumeGapSeconds)
            {
                Seed(acceleration);
                return;
            }
            if (deltaSeconds == 0f) return;

            float g = acceleration.Length() / StandardGravity;
            if (!(MathF.Abs(g - _restG) <= ShoveG))
            {
                // Past a quarter second outside, a sample that could be
                // gravity starts the estimate over. The pad may now rest in an
                // attitude whose length the follower never reached: face up
                // after a seed face down reads 1.18 g against 0.80 g on a pad
                // with no per-pad zero, and would read zero for good.
                if (_shoveSeconds > ResumeGapSeconds && g >= MinGravityG && g <= MaxGravityG)
                {
                    Seed(acceleration);
                    return;
                }
                // The smoothed direction holds, so a turn made during the
                // shove arrives when it ends, as the smoothing catches up.
                Rate = Vector3.Zero;
                _shoveSeconds += deltaSeconds;
                return;
            }
            if (_shoveSeconds > ResumeGapSeconds)
            {
                Seed(acceleration);
                return;
            }
            // Paid down rather than cleared, so a shake that dips back into
            // the window every cycle still adds up to a reseed instead of
            // releasing the turn it hid as one late swing.
            _shoveSeconds = MathF.Max(0f, _shoveSeconds - deltaSeconds);
            _restG = Math.Clamp(_restG + (g - _restG) * (1f - MathF.Exp(-deltaSeconds / RestSeconds)),
                MinGravityG, MaxGravityG);

            // Framerate-independent lerp, GamepadMotion.hpp:952-960, with a
            // time constant in place of its half-life strength.
            float keep = MathF.Exp(-deltaSeconds / SmoothingSeconds(smoothingSeconds));
            Vector3 next = Vector3.Lerp(acceleration, _smoothed, keep);

            // GamepadMotion.hpp:962-970: the newer direction crossed with the
            // older one, scaled to the angle between them over the time. The
            // angle comes from atan2 so a tiny angle keeps its precision.
            Vector3 older = Vector3.Normalize(_smoothed);
            Vector3 newer = Vector3.Normalize(next);
            Vector3 axis = Vector3.Cross(newer, older);
            float sine = axis.Length();
            float angle = MathF.Atan2(sine, Vector3.Dot(newer, older));
            Vector3 rate = sine > 0f ? axis * (angle / (sine * deltaSeconds)) : Vector3.Zero;
            if (!Finite(next) || !Finite(rate))
            {
                Reset();
                return;
            }
            _smoothed = next;
            Rate = rate;
        }

        /// <summary>The time constant, clamped to the setting's range. A
        /// hand-edited zero would turn every count into a spike, a negative
        /// value would extrapolate, and NaN would poison the state.</summary>
        public static float SmoothingSeconds(float seconds)
        {
            if (!float.IsFinite(seconds)) return SimulatedGyro.DefaultSmoothingMs / 1000f;
            return Math.Clamp(seconds, SimulatedGyro.MinSmoothingMs / 1000f, SimulatedGyro.MaxSmoothingMs / 1000f);
        }

        /// <summary>Starts from a sample inside the gravity window. A shove
        /// cannot seed the direction, and the estimator waits for the next
        /// sample.</summary>
        private void Seed(Vector3 acceleration)
        {
            Reset();
            float g = acceleration.Length() / StandardGravity;
            if (!(g >= MinGravityG && g <= MaxGravityG)) return;
            _smoothed = acceleration;
            _restG = g;
            HasValue = true;
        }

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
