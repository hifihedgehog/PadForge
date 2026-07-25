using System;
using PadForge.Engine.Data;
using static SDL3.SDL;

namespace PadForge.Engine
{
    /// <summary>
    /// FFB effect type constants matching Extended FFBEType enum values.
    /// Defined here so the Engine can interpret Vibration.EffectType without
    /// referencing the App-layer ExtendedVirtualController.
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

        /// <summary>Whether the last rumble emit failed, so the HAPTICDIAG
        /// probe logs a failure run once instead of every poll (the cache
        /// below advances only on success, which re-enters that block
        /// forever while a write keeps failing).</summary>
        private bool _lastEmitFailed;

        private ushort _cachedLeftMotorSpeed;
        private ushort _cachedRightMotorSpeed;
        private ushort _cachedLeftTriggerMotorSpeed;
        private ushort _cachedRightTriggerMotorSpeed;

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

        // Software auto-center spring (generic SDL wheels) change detection
        private bool _autoCenterActive;
        private short _autoCenterCoeff;

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
        /// The most recent left impulse trigger motor speed sent to the device (0–65535).
        /// </summary>
        public ushort LeftTriggerMotorSpeed { get; private set; }

        /// <summary>
        /// The most recent right impulse trigger motor speed sent to the device (0–65535).
        /// </summary>
        public ushort RightTriggerMotorSpeed { get; private set; }

        /// <summary>
        /// Whether force feedback is currently active on the device.
        /// </summary>
        public bool IsActive { get; private set; }

        // ─────────────────────────────────────────────
        //  Xbox One+ skip-SDL path bookkeeping
        // ─────────────────────────────────────────────

        /// <summary>Used by <c>InputManager.Step2.ApplyForceFeedback</c> for
        /// Xbox One+ controllers (Microsoft VID 0x045E + an impulse-trigger
        /// PID — see <see cref="XboxControllerIdentity"/>). PadForge writes
        /// those devices via raw HID through
        /// <c>XboxImpulseHidWriter</c>, bypassing SDL entirely. This method
        /// owns the change-detection bookkeeping that
        /// <see cref="SetDeviceForces"/> normally provides for SDL devices:
        /// returns true when motor values differ from the last write
        /// (caller should issue a fresh HID write), false when they match
        /// (skip the HID write entirely). The public motor-speed and
        /// <see cref="IsActive"/> fields are always updated so the UI
        /// activity meter and FFB tab reflect what was dispatched.</summary>
        public bool TryRecordXboxImpulseSnapshot(
            ushort leftMotor, ushort rightMotor,
            ushort leftTrigger, ushort rightTrigger)
        {
            bool changed = leftMotor != _cachedLeftMotorSpeed
                        || rightMotor != _cachedRightMotorSpeed
                        || leftTrigger != _cachedLeftTriggerMotorSpeed
                        || rightTrigger != _cachedRightTriggerMotorSpeed;

            _cachedLeftMotorSpeed = leftMotor;
            _cachedRightMotorSpeed = rightMotor;
            _cachedLeftTriggerMotorSpeed = leftTrigger;
            _cachedRightTriggerMotorSpeed = rightTrigger;

            LeftMotorSpeed = leftMotor;
            RightMotorSpeed = rightMotor;
            LeftTriggerMotorSpeed = leftTrigger;
            RightTriggerMotorSpeed = rightTrigger;
            IsActive = leftMotor > 0 || rightMotor > 0 || leftTrigger > 0 || rightTrigger > 0;

            return changed;
        }

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
                StopAndDestroyHapticEffect(device);
            }
            else if (device.HasRumble)
            {
                device.StopRumble();
            }
            else
            {
                return;
            }

            // Impulse triggers stop in parallel — different SDL handle
            // (gamepad vs joystick) but the same "kill all motors" semantic.
            if (device.HasRumbleTriggers && device.GamepadHandle != IntPtr.Zero)
            {
                SDL_RumbleGamepadTriggers(device.GamepadHandle, 0, 0, 0);
            }

            _cachedLeftMotorSpeed = 0;
            _cachedRightMotorSpeed = 0;
            _cachedLeftTriggerMotorSpeed = 0;
            _cachedRightTriggerMotorSpeed = 0;
            _cachedEffectType = 0;
            _cachedSignedMag = 0;
            _cachedDirection = 0;
            _cachedPeriod = 0;
            _cachedHasDirectional = false;
            _cachedHasCondition = false;
            _autoCenterActive = false;
            _autoCenterCoeff = 0;
            LeftMotorSpeed = 0;
            RightMotorSpeed = 0;
            LeftTriggerMotorSpeed = 0;
            RightTriggerMotorSpeed = 0;
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
        ///
        /// <para>══════════════════════════════════════════════════════════════</para>
        /// <para><b>NOT REACHED FOR SONY DUALSENSE / DUALSHOCK 4.</b></para>
        /// <para>══════════════════════════════════════════════════════════════</para>
        /// <para><c>InputManager.Step2.ApplyForceFeedback</c> returns early
        /// for Sony VID 0x054C with DS5 / DS5 Edge / DS4 PIDs before this
        /// method is called. Sony pads receive their entire effect packet
        /// (rumble + lightbar + AT + mic LED) from
        /// <c>UserEffectsDispatcher</c> via <c>PlayStationEffectWriter</c>. SDL is
        /// the sole writer for Xbox / generic gamepads / FFB joysticks —
        /// never for Sony.</para>
        /// <para>If a future change wants to route Sony pads through this
        /// path again, undo BOTH the Step 2 skip AND the dispatcher's
        /// always-write rumble bytes. Half-and-half produces the
        /// audio-rumble + animated-lightbar race that motivated the
        /// architecture (see memory: sony-rumble-sole-writer-architecture.md).</para>
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
                    // The game's effect now owns the shared haptic slot, so the
                    // auto-center spring (if any) is gone; force a re-apply the
                    // next idle frame instead of trusting the change-gate.
                    _autoCenterActive = false;
                }

                LeftMotorSpeed = v.LeftMotorSpeed;
                RightMotorSpeed = v.RightMotorSpeed;
                IsActive = v.LeftMotorSpeed > 0 || v.RightMotorSpeed > 0 ||
                           v.HasDirectionalData || v.HasConditionData;
                return;
            }

            // ── Path 2: Standard scalar rumble ──
            // If we were previously in the directional path, reset directional cache
            // so re-entering the directional path is always detected as a change.
            if (_cachedHasDirectional || _cachedHasCondition)
            {
                _cachedHasDirectional = false;
                _cachedHasCondition = false;
                _cachedEffectType = 0;
                _cachedSignedMag = 0;
                _cachedDirection = 0;
                _cachedPeriod = 0;
            }

            // ── Path 1b: Software auto-center spring (generic FFB wheels) ──
            // Non-vendor wheels routed through SDL get no firmware centering. When
            // the game isn't sending its own FFB this frame and the Auto Centering
            // Strength slider is set, hold a steering-axis spring centered at 0 so
            // the wheel returns to center. Vendor wheels (Logitech / Fanatec /
            // Thrustmaster) own centering in their native writers and never reach
            // SetDeviceForces. Centering takes precedence over scalar rumble here —
            // a wheel has no rumble motor, and a held spring is its expected idle.
            if (TryApplyAutoCenterSpring(device, ps))
            {
                LeftMotorSpeed = 0;
                RightMotorSpeed = 0;
                IsActive = true;
                return;
            }

            // Scalar values are already audio-mixed and gain/swap-scaled by
            // InputManager.ComputeFinalVibrationStates so the FFB-tab activity
            // meter, the SDL physical-rumble path, and the DS5/DS4 effect
            // packet stay in sync. Reapplying leftGain/rightGain/overallGain/
            // swap here would double-attenuate. The directional/haptic
            // branches above still consume overallGain via SignedMagnitude
            // scaling — that's intentional and stays.
            ushort finalLeft = v.LeftMotorSpeed;
            ushort finalRight = v.RightMotorSpeed;

            // Main rumble — only send to hardware when values change.
            if (finalLeft != _cachedLeftMotorSpeed || finalRight != _cachedRightMotorSpeed)
            {
                bool scalarSuccess;
                if (device.HasHaptic)
                {
                    scalarSuccess = SetHapticForces(device, finalLeft, finalRight);
                }
                else if (finalLeft == 0 && finalRight == 0)
                {
                    scalarSuccess = device.StopRumble();
                }
                else
                {
                    scalarSuccess = device.SetRumble(finalLeft, finalRight, uint.MaxValue);
                }

                // HAPTICDIAG (2026-07-24 rumble regression): the emit's
                // success was invisible, so a failing SDL write and a
                // successful one that the firmware ignores were
                // indistinguishable. Nintendo only. NOT transition-only on
                // its own: the cache below advances only on success, so a
                // persistently failing write re-enters this block every
                // poll. Log the failure once per (device, value) run and
                // let success re-arm it (audit 2026-07-24, lens 1n).
                bool emitLogWorthy = scalarSuccess || !_lastEmitFailed;
                _lastEmitFailed = !scalarSuccess;
                if (ud != null && ud.VendorId == 0x057E && emitLogWorthy)
                    SdlDiagLog.WriteLine(
                        $"HAPTICDIAG emit L={finalLeft} R={finalRight} ok={scalarSuccess}"
                        + $" viaHaptic={device.HasHaptic} gamepadHandle={(device.GamepadHandle != IntPtr.Zero)}");

                if (scalarSuccess)
                {
                    _cachedLeftMotorSpeed = finalLeft;
                    _cachedRightMotorSpeed = finalRight;
                }
            }

            LeftMotorSpeed = finalLeft;
            RightMotorSpeed = finalRight;

            // Impulse triggers — gated on device capability; uses SDL3's
            // gamepad-level trigger rumble (Xbox One+ family). Already
            // scaled and swapped by ApplyForceFeedback / ScaleTriggerRumble
            // before the Vibration lands here.
            ushort finalLT = v.LeftTriggerMotorSpeed;
            ushort finalRT = v.RightTriggerMotorSpeed;
            if (device.HasRumbleTriggers
                && device.GamepadHandle != IntPtr.Zero
                && (finalLT != _cachedLeftTriggerMotorSpeed || finalRT != _cachedRightTriggerMotorSpeed))
            {
                if (SDL_RumbleGamepadTriggers(device.GamepadHandle, finalLT, finalRT, uint.MaxValue))
                {
                    _cachedLeftTriggerMotorSpeed = finalLT;
                    _cachedRightTriggerMotorSpeed = finalRT;
                }
            }
            LeftTriggerMotorSpeed = finalLT;
            RightTriggerMotorSpeed = finalRT;

            IsActive = finalLeft > 0 || finalRight > 0 || finalLT > 0 || finalRT > 0;
        }

        // ─────────────────────────────────────────────
        //  Directional haptic (FFB joysticks / wheels)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Signed 16-bit steering-axis force level for a single-axis FFB wheel,
        /// from a directional <see cref="Vibration"/>: applies device + overall
        /// gain and projects the polar direction onto the X (steering) axis.
        /// Mirrors the single-axis branch of <see cref="SetDirectionalHapticForces"/>
        /// (constant.level) so the native vendor HID writers (Logitech / Fanatec)
        /// produce the same force the SDL haptic path would. Returns 0 when there
        /// is no directional data. Keep in sync with the projection in
        /// SetDirectionalHapticForces.
        /// </summary>
        public static short ComputeWheelSteeringLevel(Vibration v, int overallGain)
        {
            // Sampled steering level: the projected peak modulated by the periodic
            // waveform at this instant, so wheels that render periodics in host
            // software (Logitech / Fanatec, which have no firmware periodic generator
            // and software-render via an hrtimer in lg4ff / ftecff) reproduce the
            // effect as an oscillating constant force. Constant / ramp keep the peak.
            short peak = ComputeWheelSteeringPeak(v, overallGain);
            if (peak == 0) return 0;
            double level = peak * PeriodicWaveform(v.EffectType, v.Period);
            return (short)Math.Clamp(level, -32767, 32767);
        }

        /// <summary>The steering-axis peak of a directional effect — the gain-scaled
        /// magnitude projected onto the wheel axis, WITHOUT periodic-waveform sampling.
        /// Thrustmaster uploads this as the amplitude of a firmware periodic effect (the
        /// T300 runs the waveform onboard, higher fidelity than host sampling), so this
        /// stays the steady peak; <see cref="ComputeWheelSteeringLevel"/> samples the
        /// waveform for wheels that have no firmware periodic generator.</summary>
        public static short ComputeWheelSteeringPeak(Vibration v, int overallGain)
        {
            if (v == null || !v.HasDirectionalData) return 0;
            double gainScale = (v.DeviceGain / 255.0) * (Math.Clamp(overallGain, 0, 100) / 100.0);
            double scaledMag = Math.Clamp(v.SignedMagnitude * gainScale, -10000, 10000);
            double angleRad = (v.Direction / 32767.0) * 2.0 * Math.PI;
            double projected = Math.Clamp(scaledMag * Math.Sin(angleRad), -10000, 10000);
            return (short)(projected * 32767 / 10000);
        }

        /// <summary>True for the periodic effect types (square/sine/triangle/sawtooth)
        /// that a firmware periodic generator can run, vs constant/ramp/condition.</summary>
        public static bool IsPeriodicEffect(uint effectType) =>
            effectType >= FfbEffectTypes.Square && effectType <= FfbEffectTypes.SawDown;

        /// <summary>Instantaneous -1..+1 multiplier for a periodic effect type at the
        /// current time, so a native wheel reproduces the waveform by sampling it each
        /// frame. Returns 1.0 for constant/ramp or a missing period (steady magnitude).</summary>
        private static double PeriodicWaveform(uint effectType, uint periodMs)
        {
            if (periodMs == 0 || effectType < FfbEffectTypes.Square || effectType > FfbEffectTypes.SawDown)
                return 1.0;
            double phase = (Environment.TickCount % (long)periodMs) / (double)periodMs; // 0..1
            return effectType switch
            {
                FfbEffectTypes.Square   => phase < 0.5 ? 1.0 : -1.0,
                FfbEffectTypes.Sine     => Math.Sin(phase * 2.0 * Math.PI),
                FfbEffectTypes.Triangle => 4.0 * Math.Abs(phase - 0.5) - 1.0,  // +1 at 0, -1 at 0.5
                FfbEffectTypes.SawUp    => 2.0 * phase - 1.0,                  // -1 -> +1
                FfbEffectTypes.SawDown  => 1.0 - 2.0 * phase,                  // +1 -> -1
                _ => 1.0,
            };
        }

        /// <summary>Translates rumble (the main and impulse-trigger motors, from any
        /// virtual controller, Xbox or Sony) into a steering-axis vibration for
        /// native-FFB wheels, which have no rumble motor. Mirrors the Sine haptic strategy joysticks get in
        /// <see cref="SetHapticForces"/>: magnitude from the dominant motor, a low
        /// frequency for the heavy motor and a higher one for the light motor. The
        /// returned constant-force level oscillates over time so the wheel buzzes
        /// instead of pulling to one side. Returns 0 when there is no rumble.</summary>
        public static short ComputeWheelRumbleLevel(Vibration v, int overallGain)
        {
            if (v == null) return 0;
            // Include the impulse-trigger motors: Xbox racing FFB routes engine and
            // road feel through them. Left motor + left trigger are the heavy /
            // low-frequency channels, right motor + right trigger the light / high.
            int heavy = Math.Max(v.LeftMotorSpeed, v.LeftTriggerMotorSpeed);
            int light = Math.Max(v.RightMotorSpeed, v.RightTriggerMotorSpeed);
            if (heavy <= 0 && light <= 0) return 0;
            double gainScale = (v.DeviceGain / 255.0) * (Math.Clamp(overallGain, 0, 100) / 100.0);
            int mag = (int)(Math.Min(Math.Max(heavy, light) >> 1, 32767) * gainScale);
            if (mag <= 0) return 0;
            int periodMs = heavy >= light ? 120 : 40; // heavy channel -> low freq, light -> high (matches SetHapticForces)
            double phase = (Environment.TickCount % periodMs) / (double)periodMs;
            return (short)Math.Clamp(mag * Math.Sin(phase * 2.0 * Math.PI), -32767, 32767);
        }

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
                return true;
            }

            // HID output reads raw HID logical units (0–32767). Convert to SDL polar (0–36000 hundredths of degrees).
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
                    // sin(angle) gives X component: 0°=N → sin=0, 90°→sin=1 (CW), 270°→sin=-1 (CCW).
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

            return ApplyHapticEffect(device, ref effect);
        }

        /// <summary>
        /// Holds a software centering spring on a generic FFB wheel routed through
        /// SDL, driven by the Wheel-tab Auto Centering Strength slider. Mirrors what
        /// Logitech / Fanatec / Thrustmaster get from their native writers, for wheels
        /// that aren't one of those vendors. Single-axis haptic gates this to wheels —
        /// gamepads report two or more haptic axes (or no spring support), so it never
        /// fires for them. Returns false (leaving the rumble path to run) when the
        /// device isn't a spring-capable single-axis wheel or the slider is at 0.
        /// </summary>
        private bool TryApplyAutoCenterSpring(ISdlInputDevice device, PadSetting ps)
        {
            if (device == null || !device.HasHaptic || device.NumHapticAxes > 1)
                return false;
            if ((device.HapticFeatures & SDL_HAPTIC_SPRING) == 0)
                return false;

            int strength = Math.Clamp(TryParseInt(ps.AutoCenterStrength, 0), 0, 100);
            if (strength <= 0)
            {
                // Slider at 0: tear down a spring we were holding so the wheel goes
                // slack instead of staying sprung from a prior frame's value.
                if (_autoCenterActive)
                {
                    StopAndDestroyHapticEffect(device);
                    _autoCenterActive = false;
                }
                return false;
            }

            short coeff = (short)(strength * 32767 / 100); // 0..100% -> 0..32767
            if (_autoCenterActive && coeff == _autoCenterCoeff)
                return true; // unchanged — the spring is already holding, no HID write

            var effect = new SDL_HapticEffect();
            effect.condition.type = (ushort)SDL_HAPTIC_SPRING;
            effect.condition.direction.type = SDL_HAPTIC_STEERING_AXIS;
            effect.condition.length = SDL_HAPTIC_INFINITY;
            // Symmetric spring centered at 0 on the steering axis: the wheel pulls
            // back toward center with force proportional to displacement.
            effect.condition.right_coeff0 = coeff;
            effect.condition.left_coeff0 = coeff;
            effect.condition.right_sat0 = 0xffff;
            effect.condition.left_sat0 = 0xffff;
            effect.condition.center0 = 0;
            effect.condition.deadband0 = 0;

            bool ok = ApplyHapticEffect(device, ref effect);
            if (ok)
            {
                _autoCenterActive = true;
                _autoCenterCoeff = coeff;
            }
            return ok;
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
                    return false;
                }
                _hapticEffectCreated = true;

                bool run = SDL_RunHapticEffect(haptic, _hapticEffectId, SDL_HAPTIC_INFINITY);
                return run;
            }
            else
            {
                bool upd = SDL_UpdateHapticEffect(haptic, _hapticEffectId, ref effect);
                if (!upd)
                {
                    // Update failed — effect may be stale (e.g., another app acquired the
                    // device in Exclusive mode and released it). Destroy and recreate.
                    StopAndDestroyHapticEffect(device);
                    _hapticEffectId = SDL_CreateHapticEffect(haptic, ref effect);
                    if (_hapticEffectId < 0)
                    {
                        return false;
                    }
                    _hapticEffectCreated = true;
                    return SDL_RunHapticEffect(haptic, _hapticEffectId, SDL_HAPTIC_INFINITY);
                }
                return true;
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
    /// data (for haptic joysticks/wheels). Xbox / PlayStation HIDMaestro
    /// feedback callbacks only set the scalar fields; Extended FFB callback
    /// populates directional fields as well.
    /// </summary>
    public class Vibration
    {
        // ── Scalar (used by Xbox / PlayStation feedback callbacks and rumble path) ──

        /// <summary>Left motor (low-frequency, heavy rumble) speed. Range: 0–65535.</summary>
        public ushort LeftMotorSpeed { get; set; }

        /// <summary>Right motor (high-frequency, light buzz) speed. Range: 0–65535.</summary>
        public ushort RightMotorSpeed { get; set; }

        /// <summary>Left impulse trigger motor speed (Xbox One+ controllers).
        /// Driven by XINPUT_VIBRATION_EX / GameInput's per-trigger vibration API.
        /// 0 on devices without impulse-trigger motors. Range: 0–65535.</summary>
        public ushort LeftTriggerMotorSpeed { get; set; }

        /// <summary>Right impulse trigger motor speed (Xbox One+ controllers).
        /// 0 on devices without impulse-trigger motors. Range: 0–65535.</summary>
        public ushort RightTriggerMotorSpeed { get; set; }

        // ── Directional FFB (populated by Extended FFB callback for haptic devices) ──

        /// <summary>True when directional FFB data is available (Extended path).</summary>
        public bool HasDirectionalData { get; set; }

        /// <summary>Primary effect type for the dominant running effect.</summary>
        public uint EffectType { get; set; }

        /// <summary>Signed magnitude. Range: -10000 to +10000.
        /// Negative = opposite direction for constant force.
        /// For periodic effects: always positive (amplitude).</summary>
        public short SignedMagnitude { get; set; }

        /// <summary>Polar direction in HID logical units (0–32767, maps to 0–360°).
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
