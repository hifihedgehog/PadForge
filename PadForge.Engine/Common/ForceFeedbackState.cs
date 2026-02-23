using System;
using PadForge.Engine.Data;

namespace PadForge.Engine
{
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
        //  Cached settings for change detection
        // ─────────────────────────────────────────────

        private int _cachedForceType;
        private bool _cachedForceSwapMotor;
        private int _cachedLeftStrength = -1;
        private int _cachedRightStrength = -1;
        private int _cachedOverallStrength = -1;
        private ushort _cachedLeftMotorSpeed;
        private ushort _cachedRightMotorSpeed;

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
            if (device == null || !device.HasRumble)
                return;

            RumbleLogger.Log("StopDeviceForces called");
            device.StopRumble();
            _cachedLeftMotorSpeed = 0;
            _cachedRightMotorSpeed = 0;
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
        /// <param name="v">Vibration values from the XInput state (LeftMotorSpeed, RightMotorSpeed).</param>
        public void SetDeviceForces(UserDevice ud, ISdlInputDevice device, PadSetting ps, Vibration v)
        {
            if (device == null || !device.HasRumble)
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

            // Raw XInput motor speeds (0–65535).
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

            // Only send to hardware when values change. Each SDL_RumbleJoystick call
            // restarts the hardware rumble, which can cause brief gaps. By only sending
            // on change with a very long duration, rumble stays continuous.
            if (finalLeft == _cachedLeftMotorSpeed && finalRight == _cachedRightMotorSpeed)
                return;

            RumbleLogger.Log($"CHANGE L:{_cachedLeftMotorSpeed}->{finalLeft} R:{_cachedRightMotorSpeed}->{finalRight} (raw L:{rawLeft} R:{rawRight} gain:{overallGain})");

            bool success;
            if (finalLeft == 0 && finalRight == 0)
            {
                // Explicit stop — no need for a duration.
                success = device.StopRumble();
                RumbleLogger.Log($"StopRumble -> {success}");
            }
            else
            {
                // Effectively infinite duration (~49 days). Rumble persists until
                // we explicitly send different values or stop. If the app exits,
                // the OS/driver cleans up the controller state.
                success = device.SetRumble(finalLeft, finalRight, uint.MaxValue);
                RumbleLogger.Log($"SetRumble({finalLeft},{finalRight},MAX) -> {success}");
            }

            if (success)
            {
                _cachedLeftMotorSpeed = finalLeft;
                _cachedRightMotorSpeed = finalRight;
            }

            LeftMotorSpeed = finalLeft;
            RightMotorSpeed = finalRight;
            IsActive = finalLeft > 0 || finalRight > 0;
        }

        // ─────────────────────────────────────────────
        //  Change detection
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns true if any force feedback setting in the <see cref="PadSetting"/>
        /// has changed since the last call to <see cref="SetDeviceForces"/>. This is used
        /// to avoid redundant rumble updates when settings haven't changed.
        /// </summary>
        /// <param name="ps">The current PadSetting to compare against cached values.</param>
        /// <returns>True if any setting differs from the cached value.</returns>
        public bool Changed(PadSetting ps)
        {
            if (ps == null)
                return false;

            int forceType = TryParseInt(ps.ForceType, 0);
            bool swapMotor = TryParseBool(ps.ForceSwapMotor);
            int leftStrength = TryParseInt(ps.LeftMotorStrength, 100);
            int rightStrength = TryParseInt(ps.RightMotorStrength, 100);
            int overallStrength = TryParseInt(ps.ForceOverall, 100);

            bool changed =
                _cachedForceType != forceType ||
                _cachedForceSwapMotor != swapMotor ||
                _cachedLeftStrength != leftStrength ||
                _cachedRightStrength != rightStrength ||
                _cachedOverallStrength != overallStrength;

            if (changed)
            {
                _cachedForceType = forceType;
                _cachedForceSwapMotor = swapMotor;
                _cachedLeftStrength = leftStrength;
                _cachedRightStrength = rightStrength;
                _cachedOverallStrength = overallStrength;
            }

            return changed;
        }

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
    //  Vibration — lightweight struct matching XInput XINPUT_VIBRATION
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents XInput vibration motor speeds. Matches the layout of
    /// XINPUT_VIBRATION so it can be populated directly from XInput state.
    /// </summary>
    public class Vibration
    {
        /// <summary>Left motor (low-frequency, heavy rumble) speed. Range: 0–65535.</summary>
        public ushort LeftMotorSpeed { get; set; }

        /// <summary>Right motor (high-frequency, light buzz) speed. Range: 0–65535.</summary>
        public ushort RightMotorSpeed { get; set; }

        /// <summary>
        /// Creates a zeroed vibration (no rumble).
        /// </summary>
        public Vibration() { }

        /// <summary>
        /// Creates a vibration with the specified motor speeds.
        /// </summary>
        public Vibration(ushort leftMotor, ushort rightMotor)
        {
            LeftMotorSpeed = leftMotor;
            RightMotorSpeed = rightMotor;
        }
    }
}
