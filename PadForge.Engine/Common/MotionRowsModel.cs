using System;
using System.Numerics;

namespace PadForge.Engine.Common
{
    /// <summary>One device's Motion Pitch, Yaw and Roll row values for one
    /// Step 3 pass (#475), each -1 to +1 in the row frame.</summary>
    public struct MotionRowValues
    {
        public float Pitch, Yaw, Roll;

        /// <summary>The Step 3 pass that computed the values. Zero means
        /// never.</summary>
        public long Frame;
    }

    /// <summary>
    /// The motion the Motion Pitch, Motion Yaw and Motion Roll rows drive
    /// (#475). One simulated orientation per slot, and the virtual
    /// controller's gyro and accelerometer are both read from it, the
    /// single-model structure of Dolphin's emulated Wii Remote
    /// (WiimoteEmu.cpp GetAcceleration and GetAngularVelocity share one
    /// rotation).
    ///
    /// <para>The orientation, body to world, is
    /// <c>q = speed · pitch(θ) · roll(φ)</c>. Speed rows turn <c>speed</c> as a
    /// local rotation, the way GamepadMotionHelpers integrates a real gyro
    /// (GamepadMotion.hpp Motion::Update, <c>Quaternion *= rotation</c>).
    /// Angle rows set the lean θ and φ, moved by Dolphin's
    /// ApproachAngleWithAccel (Dynamics.cpp) at its default acceleration
    /// (Tilt.cpp, 7 turns per second). Pitch is the outer lean axis, Dolphin's
    /// effective order (WiimoteEmu.cpp GetTransformation applies its rotation
    /// matrix with negated angles), and the lean sits inside the Speed
    /// rotation, so a Speed turn carries a held lean along and the lean
    /// against gravity stays what the stick asks.</para>
    ///
    /// <para>The model steps in fixed slices no longer than Dolphin's 5 ms
    /// (Wiimote.h UPDATE_FREQ 200), as many as each frame's interval needs.
    /// Dolphin's approach is a bang-bang step whose overshoot guard holds only
    /// at that size: one long step in its braking phase swings the lean past
    /// its target. The gyro reports the mean of the slices' rotation rates,
    /// so it integrates back to the orientation on combined tilts (Dolphin
    /// sums the per-axis angle rates, which drifts from its own accelerometer
    /// once two axes move), and no slice turns far enough to read
    /// backward.</para>
    ///
    /// <para>Frame: SDL's sensor frame. +X right, +Y up through the top face,
    /// +Z toward the player, rotation right-handed about each axis, so
    /// positive pitch raises the nose, positive yaw turns left and positive
    /// roll lowers the left side (SDL_sensor.h). At rest the controller lies
    /// flat and its accelerometer reads +1 g on Y.</para>
    /// </summary>
    public sealed class MotionRowsModel
    {
        /// <summary>Dolphin's Tilt acceleration, (7 × 2π)² / 2π rad/s²
        /// (Tilt.cpp velocity default 7, Dynamics.cpp EmulateTilt).</summary>
        public const float LeanAcceleration = 7f * 7f * 2f * MathF.PI;

        /// <summary>The fastest the lean moves, and the top of the Speed
        /// range: 1,600°/s fits every wire the rows reach (Sony int16 at 16
        /// counts per °/s is 2,048, Valve 2,000, Switch 2,294) and eden's
        /// 1,800°/s gyro clamp.</summary>
        public const float MaxRateDps = 1600f;

        /// <summary>Longest model slice, Dolphin's 200 Hz step.</summary>
        public const float SliceSeconds = 0.005f;

        /// <summary>A frame interval longer than this restarts the clock
        /// without turning, the resume-from-sleep bound Step 3's frame delta
        /// uses.</summary>
        public const long MaxFrameMicroseconds = 250_000;

        /// <summary>One row's reading for a frame.</summary>
        public struct Axis
        {
            /// <summary>The row's combined value, -1 to +1, in the stick
            /// frame a row value carries: positive is down for a Y axis and
            /// right for an X axis.</summary>
            public float Value;
            /// <summary>Angle response: deflection sets a lean instead of a
            /// turn speed. Ignored on yaw.</summary>
            public bool Angle;
            /// <summary>Degrees per second at full deflection.</summary>
            public float SpeedDps;
            /// <summary>Degrees per second just past the deadzone.</summary>
            public float MinSpeedDps;
            /// <summary>Degrees of lean at full deflection.</summary>
            public float AngleDeg;
            /// <summary>Deadzone, a fraction of full deflection.</summary>
            public float Deadzone;
        }

        private Quaternion _speed = Quaternion.Identity;
        private float _pitch, _pitchVelocity, _roll, _rollVelocity;
        private long _lastUs;
        private bool _clockRunning;

        /// <summary>The simulated controller's orientation, body to world.</summary>
        public Quaternion Orientation => _speed * Lean;

        /// <summary>The Angle lean alone, body to world.</summary>
        public Quaternion Lean => LeanOf(_pitch, _roll);

        /// <summary>The body-frame rate of the last frame, degrees per
        /// second.</summary>
        public Vector3 GyroDps { get; private set; }

        /// <summary>Returns the Speed rotation to level without reporting the
        /// jump as rotation. A held lean is the stick's, so it stays.</summary>
        public void Level() => _speed = Quaternion.Identity;

        /// <summary>The next frame starts the clock over instead of
        /// integrating the gap since the last one.</summary>
        public void RestartClock()
        {
            _clockRunning = false;
            GyroDps = Vector3.Zero;
        }

        /// <summary>Back to rest, the state of a new slot.</summary>
        public void Reset()
        {
            _speed = Quaternion.Identity;
            _pitch = _pitchVelocity = _roll = _rollVelocity = 0f;
            RestartClock();
        }

        /// <summary>Advances one frame.</summary>
        public void Step(in Axis pitch, in Axis yaw, in Axis roll, long timestampUs)
        {
            float dt = 0f;
            if (_clockRunning)
            {
                long us = timestampUs - _lastUs;
                if (us > 0 && us <= MaxFrameMicroseconds) dt = us * 1e-6f;
            }
            _clockRunning = true;
            _lastUs = timestampUs;
            if (dt <= 0f)
            {
                GyroDps = Vector3.Zero;
                return;
            }

            // Speed: a stick pushed up (negative in the row frame) raises the
            // nose, positive pitch, the way a camera looks up. Pushed right
            // (positive) it turns right and lowers the right side, negative
            // yaw and roll. Angle: pushed up it tips the controller forward,
            // nose down, as Dolphin's Tilt does with its Forward input, so a
            // tilt maze's ball rolls away. Pushed right it lowers the right
            // side.
            const float DegToRad = MathF.PI / 180f;
            var rate = new Vector3(
                pitch.Angle ? 0f : -SpeedOf(pitch),
                -SpeedOf(yaw),
                roll.Angle ? 0f : -SpeedOf(roll)) * DegToRad;
            float pitchTarget = pitch.Angle ? Shape(pitch.Value, pitch.Deadzone) * Clamp(pitch.AngleDeg, 0f, 90f) * DegToRad : 0f;
            float rollTarget = roll.Angle ? -Shape(roll.Value, roll.Deadzone) * Clamp(roll.AngleDeg, 0f, 90f) * DegToRad : 0f;

            int slices = (int)MathF.Ceiling(dt / SliceSeconds);
            float h = dt / slices;
            var turned = Vector3.Zero;
            for (int i = 0; i < slices; i++)
            {
                var before = Orientation;
                float angle = rate.Length() * h;
                if (angle > 0f)
                    _speed = Quaternion.Normalize(_speed
                        * Quaternion.CreateFromAxisAngle(Vector3.Normalize(rate), angle));
                Approach(ref _pitch, ref _pitchVelocity, pitchTarget, h);
                Approach(ref _roll, ref _rollVelocity, rollTarget, h);
                turned += BodyRotation(before, Orientation);
            }
            GyroDps = turned / dt * (180f / MathF.PI);
        }

        /// <summary>A row's turn speed in degrees per second, signed with
        /// its value.</summary>
        private static float SpeedOf(in Axis a)
        {
            float v = Shape(a.Value, a.Deadzone);
            if (v == 0f) return 0f;
            float max = Clamp(a.SpeedDps, 0f, MaxRateDps);
            float min = Clamp(a.MinSpeedDps, 0f, max);
            return MathF.CopySign(min + (max - min) * MathF.Abs(v), v);
        }

        private static float Clamp(float v, float lo, float hi)
            => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : lo;

        /// <summary>The deadzone with the range past it rescaled, so motion
        /// starts at the deadzone's edge.</summary>
        public static float Shape(float value, float deadzone)
        {
            if (!float.IsFinite(value)) return 0f;
            float v = Math.Clamp(value, -1f, 1f);
            float dz = Clamp(deadzone, 0f, 0.9f);
            float m = MathF.Abs(v);
            if (m <= dz) return 0f;
            return MathF.CopySign((m - dz) / (1f - dz), v);
        }

        /// <summary>Pitch about X, then roll about Z inside it.</summary>
        public static Quaternion LeanOf(float pitch, float roll)
            => Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch)
             * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, roll);

        /// <summary>The body-frame rotation vector, radians, that turns
        /// <paramref name="from"/> into <paramref name="to"/>, taken on the
        /// shorter arc.</summary>
        public static Vector3 BodyRotation(Quaternion from, Quaternion to)
        {
            var d = Quaternion.Conjugate(from) * to;
            if (d.W < 0f) d = Quaternion.Negate(d);
            var v = new Vector3(d.X, d.Y, d.Z);
            float s = v.Length();
            if (s < 1e-9f) return Vector3.Zero;
            return v * (2f * MathF.Atan2(s, d.W) / s);
        }

        /// <summary>A world vector in a frame whose orientation is
        /// <paramref name="orientation"/>.</summary>
        public static Vector3 ToBody(Quaternion orientation, Vector3 world)
            => Vector3.Transform(world, Quaternion.Conjugate(orientation));

        /// <summary>Dolphin's ApproachAngleWithAccel for one axis
        /// (Dynamics.cpp, with CalculateStopDistance): accelerate toward the
        /// target, brake in time to stop on it, land on it exactly. The speed
        /// cap is PadForge's, so the lean never outruns a wire.</summary>
        private static void Approach(ref float angle, ref float velocity, float target, float dt)
        {
            const float a = LeanAcceleration;
            const float vmax = MaxRateDps * MathF.PI / 180f;
            float stop = velocity * velocity / (2f * MathF.CopySign(a, velocity));
            float offset = target - angle;
            float accel = MathF.Sign(offset - stop) * a;
            float v = Math.Clamp(velocity + accel * dt, -vmax, vmax);
            float change = v * dt + (MathF.Abs(v) < vmax ? accel * dt * dt / 2f : 0f);
            if (MathF.Abs(offset) < 0.0001f || change / offset > 1f)
            {
                velocity = offset / dt;
                angle = target;
            }
            else
            {
                velocity = v;
                angle += change;
            }
        }
    }
}
