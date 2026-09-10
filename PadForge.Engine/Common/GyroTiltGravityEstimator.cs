using System;
using System.Numerics;

namespace PadForge.Engine
{
    /// <summary>
    /// Gravity propagation for the Gyro Tilt inputs. Gyro is in radians per
    /// second and acceleration is in meters per second squared, in SDL's frame.
    /// The gravity update follows GamepadMotionHelpers, GamepadMotion.hpp:555-651,
    /// revision 39b578aacf34c3a1c584d8f7f194adc776f88055 (MIT).
    /// Copyright (c) 2020-2023 Julian "Jibb" Smart. Full notice in LICENSE.
    /// </summary>
    public sealed class GyroTiltGravityEstimator
    {
        public const float StandardGravity = 9.81f;
        private Vector3 _gravityDown;
        private Vector3 _smoothedAcceleration;
        private float _shakiness;

        public bool HasValue { get; private set; }

        /// <summary>Reaction-force direction, matching GravityProvider's units and sign.</summary>
        public Vector3 Gravity => HasValue ? -_gravityDown * StandardGravity : Vector3.Zero;

        public void Reset()
        {
            HasValue = false;
            _gravityDown = Vector3.Zero;
            _smoothedAcceleration = Vector3.Zero;
            _shakiness = 0;
        }

        /// <summary>
        /// Seed from a real acceleration sample, following InputService's
        /// first-sample gravity initialization. The mapping layer treats
        /// magnitudes below 4 m/s2 as missing gravity, so they cannot seed a pose.
        /// </summary>
        public bool Seed(Vector3 acceleration)
        {
            Reset();
            float magnitude = acceleration.Length();
            if (!Finite(acceleration) || !float.IsFinite(magnitude) || magnitude < 4f)
                return false;
            _gravityDown = -acceleration / magnitude;
            _smoothedAcceleration = acceleration / StandardGravity;
            HasValue = true;
            return true;
        }

        public bool Update(Vector3 calibratedGyro, Vector3 acceleration, float deltaSeconds)
        {
            if (!Finite(calibratedGyro) || !Finite(acceleration)
                || !float.IsFinite(deltaSeconds) || deltaSeconds < 0)
            {
                Reset();
                return false;
            }
            if (deltaSeconds == 0) return HasValue;
            if (!HasValue) return Seed(acceleration);

            float angularSpeed = calibratedGyro.Length();
            Vector3 accelG = acceleration / StandardGravity;
            float accelMagnitude = accelG.Length();
            if (!float.IsFinite(angularSpeed) || !float.IsFinite(accelMagnitude))
            {
                Reset();
                return false;
            }

            // A local sensor rotation moves world gravity in the inverse direction.
            Quaternion rotation = angularSpeed > 0
                ? Quaternion.CreateFromAxisAngle(calibratedGyro / angularSpeed,
                    -angularSpeed * deltaSeconds)
                : Quaternion.Identity;
            _gravityDown = Vector3.Transform(_gravityDown, rotation);

            if (accelMagnitude > 0)
            {
                _smoothedAcceleration = Vector3.Transform(_smoothedAcceleration, rotation);
                float smoothFactor = MathF.Pow(2f, -deltaSeconds / 0.25f);
                _shakiness = MathF.Max(_shakiness * smoothFactor,
                    (accelG - _smoothedAcceleration).Length());
                _smoothedAcceleration = Vector3.Lerp(accelG, _smoothedAcceleration, smoothFactor);

                // Reference defaults: quiet/shaky correction speeds of 1/0.1 g/s,
                // with shakiness thresholds 0.01/0.4 g. Correction near agreement
                // is bounded by 10% of angular speed, with a 0.01 g/s minimum.
                Vector3 target = -accelG / accelMagnitude;
                Vector3 error = target - _gravityDown;
                float distance = error.Length();
                float correctionSpeed = 1f - 0.9f * Math.Clamp(
                    (_shakiness - 0.01f) / (0.4f - 0.01f), 0f, 1f);
                float gyroLimit = MathF.Max(angularSpeed * 0.1f, 0.01f);
                if (correctionSpeed > gyroLimit)
                {
                    float disagreement = Math.Clamp((distance - 0.05f) / (0.25f - 0.05f), 0f, 1f);
                    correctionSpeed = gyroLimit + (correctionSpeed - gyroLimit) * disagreement;
                }
                float step = correctionSpeed * deltaSeconds;
                _gravityDown = distance > step
                    ? _gravityDown + error * (step / distance)
                    : target;
            }

            if (!Finite(_gravityDown) || !Finite(_smoothedAcceleration) || !float.IsFinite(_shakiness))
            {
                Reset();
                return false;
            }
            return true;
        }

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    /// <summary>A copied gravity estimate and the identity of its current initialization.</summary>
    public readonly record struct GyroTiltGravitySample(float X, float Y, float Z, long Generation);
}
