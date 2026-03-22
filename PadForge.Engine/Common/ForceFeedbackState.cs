using System;
using PadForge.Engine.Data;
using static SDL3.SDL;

namespace PadForge.Engine
{
    /// <summary>
    /// FFB effect type constants matching vJoy FFBEType enum values.
    /// Defined here so the Engine can interpret Vibration.EffectType without
    /// referencing the App-layer VJoyVirtualController.
    /// </summary>
    public static class FfbEffectTypes
    {
        public const uint None    = 0;
        public const uint Const   = 1;
        public const uint Ramp    = 2;
        public const uint Square  = 3;
        public const uint Sine    = 4;
        public const uint Triangle = 5;
        public const uint SawUp   = 6;
        public const uint SawDown = 7;
        public const uint Spring  = 8;
        public const uint Damper  = 9;
        public const uint Inertia = 10;
        public const uint Friction = 11;
    }

    /// <summary>
    /// Manages force feedback (rumble) state for a single device.
    /// Tracks cached settings values for change detection and converts
    /// XInput vibration motor speeds to SDL rumble calls.
    ///
    /// Uses change-detection to only send rumble when motor values differ,
    /// with uint.MaxValue duration (~49 days) to mimic XInput's "set and
    /// forget" behavior. This avoids the brief hardware restart gaps that
    /// occur when SDL_RumbleJoystick is called redundantly at high frequency.
    /// </summary>
    public class ForceFeedbackState
    {
        // ─────────────────────────────────────────────
        //  Cached motor speeds for change detection
        // ─────────────────────────────────────────────

        private ushort _cachedLeftMotorSpeed;
        private ushort _cachedRightMotorSpeed;

        // Haptic effect tracking
        private int _hapticEffectId = -1;
        private bool _hapticEffectCreated;

        // Directional haptic change detection
        private uint _cachedEffectType;
        private short _cachedSignedMag;
        private ushort _cachedDirection;
        private uint _cachedPeriod;
        private bool _cachedHasCondition;
        private bool _cachedHasDirectional;

        // ─────────────────────────────────────────────
        //  Public state
        // ─────────────────────────────────────────────

        /// <summary>
        /// The most recent left (low-frequency) motor speed sent to the device (0–65535).
        /// </summary>
        public ushort LeftMotorSpeed { get; private set; }

        /// <summary>
        /// The most recent right (high-frequency) motor speed sent to the device (0–65535).
        /// </summary>
        public ushort RightMotorSpeed { get; private set; }

        /// <summary>
        /// Whether force feedback is currently active on the device.
        /// </summary>
        public bool IsActive { get; private set; }

        // ─────────────────────────────────────────────
        //  Stop
        // ─────────────────────────────────────────────

        /// <summary>
        /// Stops all rumble on the device and resets cached state.
        /// </summary>
        /// <param name="device">The SDL device wrapper to stop.</param>
        public void StopDeviceForces(ISdlInputDevice device)
        {
            if (device == null)
                return;

            if (device.HasHaptic)
            {
                RumbleLogger.Log("StopDeviceForces (haptic) called");
                StopAndDestroyHapticEffect(device);
            }
            else if (device.HasRumble)
            {
                RumbleLogger.Log("StopDeviceForces called");
                device.StopRumble();
            }
            else
            {
                return;
            }

            _cachedLeftMotorSpeed = 0;
            _cachedRightMotorSpeed = 0;
            _cachedEffectType = 0;
            _cachedSignedMag = 0;
            _cachedDirection = 0;
            _cachedPeriod = 0;
            _cachedHasDirectional = false;
            _cachedHasCondition = false;
            LeftMotorSpeed = 0;
            RightMotorSpeed = 0;
            IsActive = false;
        }

        // ─────────────────────────────────────────────
        //  Set
        // ─────────────────────────────────────────────

        /// <summary>
        /// Calculates and applies rumble forces to the device based on PadSetting
        /// configuration and incoming XInput vibration values.
        ///
        /// The method:
        /// 1. Reads gain (overall strength) and per-motor strength from PadSetting.
        /// 2. Applies gain scaling to the raw XInput motor speeds.
        /// 3. Swaps motors if configured.
        /// 4. Only sends to hardware when values change (avoids SDL rumble restart gaps).
        /// </summary>
        /// <param name="ud">The user device data model (for device reference).</param>
        /// <param name="device">The SDL device wrapper to rumble.</param>
        /// <param name="ps">PadSetting containing force feedback configuration.</param>
        /// <param name="v">Vibration values from the virtual controller callback (LeftMotorSpeed, RightMotorSpeed).</param>
        public void SetDeviceForces(UserDevice ud, ISdlInputDevice device, PadSetting ps, Vibration v)
        {
            if (device == null || (!device.HasRumble && !device.HasHaptic))
                return;

            if (ps == null || v == null)
            {
                StopDeviceForces(device);
                return;
            }

            // Parse gain settings from PadSetting.
            int overallGain = TryParseInt(ps.ForceOverall, 100);
            int leftGain = TryParseInt(ps.LeftMotorStrength, 100);
            int rightGain = TryParseInt(ps.RightMotorStrength, 100);
            bool swapMotors = TryParseBool(ps.ForceSwapMotor);

            // Clamp gains to 0–100.
            overallGain = Math.Clamp(overallGain, 0, 100);
            leftGain = Math.Clamp(leftGain, 0, 100);
            rightGain = Math.Clamp(rightGain, 0, 100);

            // ── Path 1: Directional haptic (FFB joysticks / wheels) ──
            // If the vibration carries directional FFB data and the device has haptic
            // support, route through the directional path for true force direction.
            if (device.HasHaptic && (v.HasDirectionalData || v.HasConditionData))
            {
                bool directionalChanged =
                    v.HasDirectionalData != _cachedHasDirectional ||
                    v.EffectType != _cachedEffectType ||
                    v.SignedMagnitude != _cachedSignedMag ||
                    v.Direction != _cachedDirection ||
                    v.Period != _cachedPeriod ||
                    v.HasConditionData != _cachedHasCondition;

                if (!directionalChanged)
                    return;

                bool success;
                if (v.HasConditionData && v.ConditionAxes != null && v.ConditionAxisCount > 0)
                {
                    success = SetConditionHapticForces(device, v, overallGain);
                }
                else if (v.HasDirectionalData)
                {
                    success = SetDirectionalHapticForces(device, v, overallGain);
                }
                else
                {
                    success = false;
                }

                if (success)
                {
                    _cachedHasDirectional = v.HasDirectionalData;
                    _cachedEffectType = v.EffectType;
                    _cachedSignedMag = v.SignedMagnitude;
                    _cachedDirection = v.Direction;
                    _cachedPeriod = v.Period;
                    _cachedHasCondition = v.HasConditionData;
                    // Also update scalar cache to stay in sync.
                    _cachedLeftMotorSpeed = v.LeftMotorSpeed;
                    _cachedRightMotorSpeed = v.RightMotorSpeed;
                }

                LeftMotorSpeed = v.LeftMotorSpeed;
                RightMotorSpeed = v.RightMotorSpeed;
                IsActive = v.LeftMotorSpeed > 0 || v.RightMotorSpeed > 0 ||
                           v.HasDirectionalData || v.HasConditionData;
                return;
            }

            // ── Path 2: Standard scalar rumble ──
            ushort rawLeft = v.LeftMotorSpeed;
            ushort rawRight = v.RightMotorSpeed;

            // Apply per-motor and overall gain.
            double left = rawLeft * (leftGain / 100.0) * (overallGain / 100.0);
            double right = rawRight * (rightGain / 100.0) * (overallGain / 100.0);

            // Clamp to ushort range.
            ushort finalLeft = (ushort)Math.Clamp(left, 0, 65535);
            ushort finalRight = (ushort)Math.Clamp(right, 0, 65535);

            // Swap motors if configured.
            if (swapMotors)
            {
                (finalLeft, finalRight) = (finalRight, finalLeft);
            }

            // Only send to hardware when values change.
            if (finalLeft == _cachedLeftMotorSpeed && finalRight == _cachedRightMotorSpeed)
                return;

            RumbleLogger.Log($"CHANGE L:{_cachedLeftMotorSpeed}->{finalLeft} R:{_cachedRightMotorSpeed}->{finalRight} (raw L:{rawLeft} R:{rawRight} gain:{overallGain})");

            bool scalarSuccess;
            if (device.HasHaptic)
            {
                scalarSuccess = SetHapticForces(device, finalLeft, finalRight);
            }
            else if (finalLeft == 0 && finalRight == 0)
            {
                scalarSuccess = device.StopRumble();
                RumbleLogger.Log($"StopRumble -> {scalarSuccess}");
            }
            else
            {
                scalarSuccess = device.SetRumble(finalLeft, finalRight, uint.MaxValue);
                RumbleLogger.Log($"SetRumble({finalLeft},{finalRight},MAX) -> {scalarSuccess}");
            }

            if (scalarSuccess)
            {
                _cachedLeftMotorSpeed = finalLeft;
                _cachedRightMotorSpeed = finalRight;
            }

            LeftMotorSpeed = finalLeft;
            RightMotorSpeed = finalRight;
            IsActive = finalLeft > 0 || finalRight > 0;
        }

        // ─────────────────────────────────────────────
        //  Directional haptic (FFB joysticks / wheels)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Sends a directional constant or periodic force to an SDL haptic device.
        /// For joysticks (2+ axes): uses polar direction for true 2D force.
        /// For wheels (1 axis): projects the polar direction onto the steering axis.
        /// Falls back to scalar SetHapticForces if the device lacks the required effect type.
        /// </summary>
        private bool SetDirectionalHapticForces(ISdlInputDevice device, Vibration v, int overallGain)
        {
            // Apply device-level and overall gains to magnitude.
            double gainScale = (v.DeviceGain / 255.0) * (overallGain / 100.0);
            short scaledMag = (short)Math.Clamp(v.SignedMagnitude * gainScale, -10000, 10000);

            if (scaledMag == 0)
            {
                StopAndDestroyHapticEffect(device);
                RumbleLogger.Log("Directional haptic stop (zero mag)");
                return true;
            }

            // Convert HID polar direction (0–32767 → 0–360°) to SDL polar (0–36000 hundredths).
            int sdlPolar = (int)(v.Direction / 32767.0 * 36000.0);
            uint features = device.HapticFeatures;
            bool isSingleAxis = device.NumHapticAxes <= 1;

            var effect = new SDL_HapticEffect();
            uint effectType = v.EffectType;

            if (effectType == FfbEffectTypes.Const || effectType == FfbEffectTypes.Ramp)
            {
                if ((features & SDL_HAPTIC_CONSTANT) == 0)
                    return SetHapticForces(device, v.LeftMotorSpeed, v.RightMotorSpeed);

                effect.constant.type = (ushort)SDL_HAPTIC_CONSTANT;
                effect.constant.length = SDL_HAPTIC_INFINITY;
                effect.constant.attack_length = 0;
                effect.constant.fade_length = 0;

                if (isSingleAxis)
                {
                    // Wheel: project 2D polar direction onto steering axis (X).
                    // sin(angle) gives X component: HID 0=N → sin=0, 90°→sin=1 (CW), 270°→sin=-1 (CCW).
                    double angleRad = (v.Direction / 32767.0) * 2.0 * Math.PI;
                    double xComponent = Math.Sin(angleRad);
                    short projectedMag = (short)Math.Clamp(scaledMag * xComponent, -10000, 10000);
                    // Scale -10000..+10000 → -32767..+32767.
                    effect.constant.level = (short)(projectedMag * 32767 / 10000);
                    effect.constant.direction.type = SDL_HAPTIC_STEERING_AXIS;
                }
                else
                {
                    // Joystick: full 2D polar direction.
                    // Scale -10000..+10000 → -32767..+32767.
                    effect.constant.level = (short)(scaledMag * 32767 / 10000);
                    effect.constant.direction.type = SDL_HAPTIC_POLAR;
                    effect.constant.direction.dir0 = sdlPolar;
                }
            }
            else if (effectType >= FfbEffectTypes.Square && effectType <= FfbEffectTypes.SawDown)
            {
                // Periodic effects: Sine, Square, Triangle, SawtoothUp, SawtoothDown.
                ushort sdlType = effectType switch
                {
                    FfbEffectTypes.Sine     => (ushort)SDL_HAPTIC_SINE,
                    FfbEffectTypes.Square   => (ushort)SDL_HAPTIC_SQUARE,
                    FfbEffectTypes.Triangle => (ushort)SDL_HAPTIC_TRIANGLE,
                    FfbEffectTypes.SawUp    => (ushort)SDL_HAPTIC_SAWTOOTHUP,
                    FfbEffectTypes.SawDown  => (ushort)SDL_HAPTIC_SAWTOOTHDOWN,
                    _ => (ushort)SDL_HAPTIC_SINE
                };

                if ((features & sdlType) == 0)
                    return SetHapticForces(device, v.LeftMotorSpeed, v.RightMotorSpeed);

                effect.periodic.type = sdlType;
                effect.periodic.length = SDL_HAPTIC_INFINITY;
                effect.periodic.magnitude = (short)Math.Clamp(Math.Abs(scaledMag) * 32767 / 10000, 0, 32767);
                effect.periodic.period = (ushort)Math.Clamp(v.Period, 1, 65535);

                if (isSingleAxis)
                {
                    effect.periodic.direction.type = SDL_HAPTIC_STEERING_AXIS;
                }
                else
                {
                    effect.periodic.direction.type = SDL_HAPTIC_POLAR;
                    effect.periodic.direction.dir0 = sdlPolar;
                }
            }
            else
            {
                // Unknown effect type — fall back to scalar rumble.
                return SetHapticForces(device, v.LeftMotorSpeed, v.RightMotorSpeed);
            }

            RumbleLogger.Log($"Directional haptic type={effectType} mag={scaledMag} dir={v.Direction} period={v.Period} singleAxis={isSingleAxis}");
            return ApplyHapticEffect(device, ref effect);
        }

        /// <summary>
        /// Sends a condition effect (spring/damper/friction/inertia) to an SDL haptic device
        /// with full per-axis coefficients. Falls back to scalar rumble if unsupported.
        /// </summary>
        private bool SetConditionHapticForces(ISdlInputDevice device, Vibration v, int overallGain)
        {
            uint features = device.HapticFeatures;
            uint effectType = v.EffectType;

            ushort sdlCondType = effectType switch
            {
                FfbEffectTypes.Spring   => (ushort)SDL_HAPTIC_SPRING,
                FfbEffectTypes.Damper   => (ushort)SDL_HAPTIC_DAMPER,
                FfbEffectTypes.Inertia  => (ushort)SDL_HAPTIC_INERTIA,
                FfbEffectTypes.Friction => (ushort)SDL_HAPTIC_FRICTION,
                _ => 0
            };

            if (sdlCondType == 0 || (features & sdlCondType) == 0)
                return SetHapticForces(device, v.LeftMotorSpeed, v.RightMotorSpeed);

            double gainScale = (v.DeviceGain / 255.0) * (overallGain / 100.0);

            var effect = new SDL_HapticEffect();
            effect.condition.type = sdlCondType;
            effect.condition.direction.type = SDL_HAPTIC_CARTESIAN;
            effect.condition.direction.dir0 = 1;
            effect.condition.length = SDL_HAPTIC_INFINITY;

            // Copy per-axis condition data (axis 0 = X, axis 1 = Y).
            int axisCount = Math.Min(v.ConditionAxisCount, 2);
            for (int i = 0; i < axisCount; i++)
            {
                var ca = v.ConditionAxes[i];
                // Scale coefficients: HID -10000..+10000 → SDL -32767..+32767 with gain.
                short rCoeff = (short)Math.Clamp(ca.PositiveCoefficient * gainScale * 32767 / 10000, -32767, 32767);
                short lCoeff = (short)Math.Clamp(ca.NegativeCoefficient * gainScale * 32767 / 10000, -32767, 32767);
                ushort rSat = (ushort)Math.Clamp(ca.PositiveSaturation * 65535 / 10000, 0, 65535);
                ushort lSat = (ushort)Math.Clamp(ca.NegativeSaturation * 65535 / 10000, 0, 65535);
                short center = (short)Math.Clamp(ca.Offset * 32767 / 10000, -32767, 32767);
                ushort dead = (ushort)Math.Clamp(ca.DeadBand * 65535 / 10000, 0, 65535);

                if (i == 0)
                {
                    effect.condition.right_coeff0 = rCoeff;
                    effect.condition.left_coeff0 = lCoeff;
                    effect.condition.right_sat0 = rSat;
                    effect.condition.left_sat0 = lSat;
                    effect.condition.center0 = center;
                    effect.condition.deadband0 = dead;
                }
                else
                {
                    effect.condition.right_coeff1 = rCoeff;
                    effect.condition.left_coeff1 = lCoeff;
                    effect.condition.right_sat1 = rSat;
                    effect.condition.left_sat1 = lSat;
                    effect.condition.center1 = center;
                    effect.condition.deadband1 = dead;
                }
            }

            RumbleLogger.Log($"Condition haptic type={effectType} axes={axisCount} gain={gainScale:F2}");
            return ApplyHapticEffect(device, ref effect);
        }

        // ─────────────────────────────────────────────
        //  Scalar haptic effect routing (fallback)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Translates dual-motor rumble values into an SDL haptic effect based on
        /// the device's <see cref="HapticEffectStrategy"/>. Creates the effect on
        /// first call, updates it on subsequent calls (same change-detection pattern
        /// as the rumble path).
        /// </summary>
        private bool SetHapticForces(ISdlInputDevice device, ushort left, ushort right)
        {
            if (left == 0 && right == 0)
            {
                StopAndDestroyHapticEffect(device);
                RumbleLogger.Log("Haptic stop (zero)");
                return true;
            }

            var effect = new SDL_HapticEffect();

            switch (device.HapticStrategy)
            {
                case HapticEffectStrategy.LeftRight:
                    effect.leftright.type = (ushort)SDL_HAPTIC_LEFTRIGHT;
                    effect.leftright.length = SDL_HAPTIC_INFINITY;
                    effect.leftright.large_magnitude = left;
                    effect.leftright.small_magnitude = right;
                    break;

                case HapticEffectStrategy.Sine:
                    effect.periodic.type = (ushort)SDL_HAPTIC_SINE;
                    effect.periodic.direction.type = SDL_HAPTIC_CARTESIAN;
                    effect.periodic.direction.dir0 = 1;
                    effect.periodic.length = SDL_HAPTIC_INFINITY;
                    // Magnitude from dominant motor, period varies by which motor is stronger.
                    short mag = (short)Math.Min(Math.Max(left, right) >> 1, 32767);
                    effect.periodic.magnitude = mag;
                    // Heavy motor → longer period (low freq), light motor → shorter period (high freq).
                    effect.periodic.period = (ushort)(left >= right ? 120 : 40);
                    break;

                case HapticEffectStrategy.Constant:
                    effect.constant.type = (ushort)SDL_HAPTIC_CONSTANT;
                    effect.constant.direction.type = SDL_HAPTIC_CARTESIAN;
                    effect.constant.direction.dir0 = 1;
                    effect.constant.length = SDL_HAPTIC_INFINITY;
                    // Level from max motor, scaled to signed range.
                    effect.constant.level = (short)Math.Min(Math.Max(left, right) >> 1, 32767);
                    break;

                default:
                    return false;
            }

            return ApplyHapticEffect(device, ref effect);
        }

        /// <summary>
        /// Creates or updates the haptic effect on the device. On first call, creates
        /// the effect and runs it. On subsequent calls, updates the existing effect
        /// in-place (avoids create/destroy churn).
        /// </summary>
        private bool ApplyHapticEffect(ISdlInputDevice device, ref SDL_HapticEffect effect)
        {
            IntPtr haptic = device.HapticHandle;
            if (haptic == IntPtr.Zero)
                return false;

            if (!_hapticEffectCreated)
            {
                _hapticEffectId = SDL_CreateHapticEffect(haptic, ref effect);
                if (_hapticEffectId < 0)
                {
                    RumbleLogger.Log($"SDL_CreateHapticEffect failed: {SDL_GetError()}");
                    return false;
                }
                _hapticEffectCreated = true;

                bool run = SDL_RunHapticEffect(haptic, _hapticEffectId, SDL_HAPTIC_INFINITY);
                RumbleLogger.Log($"Haptic create+run id={_hapticEffectId} -> {run}");
                return run;
            }
            else
            {
                bool upd = SDL_UpdateHapticEffect(haptic, _hapticEffectId, ref effect);
                RumbleLogger.Log($"Haptic update id={_hapticEffectId} -> {upd}");
                return upd;
            }
        }

        /// <summary>
        /// Stops and destroys the current haptic effect if one is active.
        /// </summary>
        private void StopAndDestroyHapticEffect(ISdlInputDevice device)
        {
            if (!_hapticEffectCreated || _hapticEffectId < 0)
                return;

            IntPtr haptic = device.HapticHandle;
            if (haptic != IntPtr.Zero)
            {
                SDL_StopHapticEffect(haptic, _hapticEffectId);
                SDL_DestroyHapticEffect(haptic, _hapticEffectId);
            }

            _hapticEffectId = -1;
            _hapticEffectCreated = false;
        }

        // ─────────────────────────────────────────────
        //  Change detection
        // ─────────────────────────────────────────────

        // ─────────────────────────────────────────────
        //  Parse helpers
        // ─────────────────────────────────────────────

        private static int TryParseInt(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        private static bool TryParseBool(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Vibration — force feedback / rumble state for a virtual controller slot
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents vibration/force feedback state for a virtual controller slot.
    /// Carries both scalar motor speeds (for rumble devices) and directional FFB
    /// data (for haptic joysticks/wheels). ViGEm Xbox/DS4 callbacks only set the
    /// scalar fields; vJoy FFB callback populates directional fields as well.
    /// </summary>
    public class Vibration
    {
        // ── Scalar (used by ViGEm Xbox/DS4 callbacks and rumble path) ──

        /// <summary>Left motor (low-frequency, heavy rumble) speed. Range: 0–65535.</summary>
        public ushort LeftMotorSpeed { get; set; }

        /// <summary>Right motor (high-frequency, light buzz) speed. Range: 0–65535.</summary>
        public ushort RightMotorSpeed { get; set; }

        // ── Directional FFB (populated by vJoy FFB callback for haptic devices) ──

        /// <summary>True when directional FFB data is available (vJoy path).</summary>
        public bool HasDirectionalData { get; set; }

        /// <summary>Primary effect type for the dominant running effect.</summary>
        public uint EffectType { get; set; }

        /// <summary>Signed magnitude. Range: -10000 to +10000.
        /// Negative = opposite direction for constant force.
        /// For periodic effects: always positive (amplitude).</summary>
        public short SignedMagnitude { get; set; }

        /// <summary>Polar direction in HID units (0–32767, maps to 0–360°).
        /// 0 = North/Up, ~8192 = East/Right, ~16384 = South, ~24576 = West/Left.</summary>
        public ushort Direction { get; set; }

        /// <summary>Period in ms for periodic effects (sine, square, triangle, sawtooth).</summary>
        public uint Period { get; set; }

        /// <summary>Device-level gain (0–255). Applied on top of per-effect gain.</summary>
        public byte DeviceGain { get; set; } = 255;

        // ── Condition effect data (for spring/damper/friction/inertia) ──

        /// <summary>True when per-axis condition data is available.</summary>
        public bool HasConditionData { get; set; }

        /// <summary>Per-axis condition coefficients.
        /// Index 0 = X axis, Index 1 = Y axis. Null when no condition data.</summary>
        public ConditionAxisData[] ConditionAxes { get; set; }

        /// <summary>Number of valid entries in ConditionAxes (1 for wheels, 2 for joysticks).</summary>
        public int ConditionAxisCount { get; set; }

        public Vibration() { }

        public Vibration(ushort leftMotor, ushort rightMotor)
        {
            LeftMotorSpeed = leftMotor;
            RightMotorSpeed = rightMotor;
        }
    }

    /// <summary>
    /// Per-axis condition effect parameters (spring/damper/friction/inertia).
    /// </summary>
    public struct ConditionAxisData
    {
        /// <summary>Positive coefficient (0–10000). Force when displacement > center.</summary>
        public short PositiveCoefficient;
        /// <summary>Negative coefficient (0–10000). Force when displacement &lt; center.</summary>
        public short NegativeCoefficient;
        /// <summary>Center point offset (-10000 to +10000).</summary>
        public short Offset;
        /// <summary>Dead band around center (0–10000).</summary>
        public uint DeadBand;
        /// <summary>Positive saturation (0–10000).</summary>
        public uint PositiveSaturation;
        /// <summary>Negative saturation (0–10000).</summary>
        public uint NegativeSaturation;
    }
}
