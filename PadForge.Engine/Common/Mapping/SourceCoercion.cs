using System;
using System.Collections.Concurrent;
using PadForge.Engine.Data;

namespace PadForge.Engine.Common.Mapping
{
    /// <summary>
    /// Reads one <see cref="MappingSource"/> against a
    /// <see cref="CustomInputState"/> and coerces the per-source value
    /// into the target's natural range. Centralizes the
    /// source-type × target-type table from the multi-source recipe.
    ///
    /// <para>
    /// v1 supports the <c>Direct</c> source kind only. <c>Incremental</c>
    /// and <c>InvertOnHold</c> land in Commit 4 with a state-aware
    /// extension that wraps this helper.
    /// </para>
    /// </summary>
    public static class SourceCoercion
    {
        /// <summary>Source-type discriminator parsed out of the
        /// <see cref="MappingSource.Descriptor"/>.</summary>
        public enum SourceType
        {
            Unmapped,
            Button,
            Axis,
            Slider,
            PovDirection,
            TouchpadButton,  // "Touchpad N Click" / "Touchpad N Finger M Down"
            Gyro,            // "Gyro Pitch" / "Gyro Yaw" / "Gyro Roll"
            TouchpadGesture, // "Touchpad N <GestureName>" — one of the in-box
                             // names (SwipeUp / DoubleTap / Pinch / RadialZone8_3
                             // / Circle / ...) or "Custom_<UserName>" for a
                             // user-recorded template. PinchAxis / RotateAxis
                             // are continuous-axis variants; everything else
                             // is a one-shot button-fire descriptor.
            Motion,          // "Motion Gyro" / "Motion Accel" — bundled 3-axis
                             // sensor source. Used by motion-passthrough rows
                             // (target = MotionGyro / MotionAccel). The row's
                             // existence binds the device's sensor stream to
                             // the slot's motion channel; per-axis values are
                             // not coerced through this enum's scalar path.
            Midi,            // "Midi Note N" / "Midi CC N" / "Midi Pitch Bend"
                             // — read from CustomInputState.Midi (the full
                             // MIDI namespace sub-state), never the gamepad
                             // axis/button arrays.
            MouseCursor,     // "Mouse Position X" / "Mouse Position Y" (issue #107).
                             // Absolute desktop cursor position normalized to
                             // [-1..+1] per screen axis, read from the global
                             // MouseCursorProvider, not any device's axis array.
        }

        /// <summary>Sensitivity constant for gyro bipolar coercion.
        /// 500°/s rotation maps to ±1.0 deflection — users tune fine
        /// sensitivity at the target's existing curve / sensitivity
        /// knobs (LeftThumb sens for mouse, stick deadzone for stick).
        /// </summary>
        private const float GyroScale = 1.0f / (500f * (float)Math.PI / 180f);

        /// <summary>Per-source button threshold for gyro → button
        /// coercion: rotation magnitude (rad/s) above which the
        /// activator counts as "pressed." 30°/s ≈ a deliberate
        /// twist, not idle hand tremor.</summary>
        private static readonly float GyroButtonThreshold = 30f * (float)Math.PI / 180f;

        /// <summary>Static lookup hook so SourceCoercion can subtract
        /// per-(device, slot) at-rest gyro bias without taking a
        /// PadSetting reference (the Engine library is self-contained).
        /// The App layer wires this provider at startup from the per-
        /// slot PadSetting. Returns the three-axis bias tuple for the
        /// given (deviceGuid, slotIndex), or zero for unknown /
        /// uncalibrated (device, slot) pairs. NOTE: the per-source
        /// <c>Invert</c> toggle handles user-perception direction
        /// inversion — do NOT apply any cemuhook-style (-gx, gy, -gz)
        /// flip here. Those flips live exclusively in the DSU /
        /// MotionSnapshot aggregation path and would silently break
        /// user expectations if synced.</summary>
        public static Func<string, int, (float pitch, float yaw, float roll)> GyroBiasProvider { get; set; }

        /// <summary>v3.3 per-(device, slot) gyro tuning bundle. App
        /// layer wires <see cref="GyroTuningProvider"/> at startup with
        /// a lookup against the slot's <c>PadSetting</c> for the named
        /// device. Returned struct's fields:
        /// <list type="bullet">
        /// <item><c>SensH</c> / <c>SensV</c> — multipliers, default 1.0</item>
        /// <item><c>DeadZoneRadPerSec</c> — gyro deadzone, rad/s</item>
        /// <item><c>SmoothingAlpha</c> — EMA alpha 0–1, 0 = off</item>
        /// <item><c>Acceleration</c> — rate-dependent gain 0–2, 0 = off</item>
        /// <item><c>OutputCurve</c> — preset name (Linear / Aggressive /
        ///   Relaxed / Wide / ExtraWide)</item>
        /// <item><c>EasyAimStickThreshold01</c> — right-stick deflection
        ///   (0..1) below which gyro output is zeroed. 0 = always on.</item>
        /// </list>
        /// </summary>
        public struct GyroTuning
        {
            public float SensH;
            public float SensV;
            public float DeadZoneRadPerSec;
            public float SmoothingAlpha;             // legacy EMA (unused when the dual-threshold pair is active)
            public float Acceleration;
            public string OutputCurve;
            public float EasyAimStickThreshold01;

            // Player / World space
            public string Space;                     // "Local" / "Player" / "World"
            public float PlayerYawRelax;
            public float WorldSideReduction;

            // dual-threshold smoothing
            public float TighteningRadPerSec;
            public float SmoothingThresholdRadPerSec;
            public float SmoothingWindowSeconds;

            // real-world calibration (0 = disabled)
            public float RealWorldCalibration;

            // aim-engage button — kept on the tuning bundle for back-
            // compat with consumers that still snapshot the configured
            // descriptor (e.g. the UI mirror). The evaluator no longer
            // reads these to gate; it reads AimEngageStateProvider for
            // the resolved per-slot bit (Hold/Toggle + macro OR-combined,
            // settled once per tick by InputManager.UpdateGyroEngageStates).
            public string AimEngageDevice;
            public string AimEngageDescriptor;

            // per-axis invert toggles
            public bool InvertPitch;
            public bool InvertYawRoll;

            // When true, this whole tuning chain is applied to the
            // virtual controller's motion passthrough (Sony report
            // packer + DSU broadcast), not only to gyro-as-mapping-
            // source reads. Default false — fresh profiles relay the
            // raw sensor reading. See GetPassthroughGyro.
            public bool ApplyToPassthrough;
        }

        /// <summary>Looks up the per-(device, slot) gyro tuning bundle
        /// from the slot's PadSetting. <paramref name="slotIndex"/>
        /// distinguishes the same device's tuning across different
        /// game-binding configurations.</summary>
        public static Func<string, int, GyroTuning> GyroTuningProvider { get; set; }

        /// <summary>Reads the slot's right-stick deflection (0..1) so
        /// Easy Aim can gate gyro output on aim-stick movement without
        /// the binding layer needing direct access to the combined
        /// gamepad state. App wires this against
        /// <c>InputManager.CombinedOutputStates[slot]</c> at startup.
        /// Returns 0 when slot is empty / state unavailable.</summary>
        public static Func<int, float> SlotRightStickDeflectionProvider { get; set; }

        /// <summary>— per-device gravity vector estimator. The app
        /// layer low-pass-filters <c>state.Accel[]</c> per device and
        /// exposes the smoothed result here. Returns the gravity-aligned
        /// vector in the controller's local frame. Used by Player
        /// Space / World Space gyro projection (slot-specific framing
        /// is applied downstream by the per-slot GyroTuning that
        /// consumes this vector). App returns <c>(0, 0, -1)</c> (flat,
        /// face-up) for unknown devices.</summary>
        public static Func<string, (float gx, float gy, float gz)> GravityProvider { get; set; }

        /// <summary>— reads whether the given (deviceGuid,
        /// descriptor) is currently pressed on the named slot. Used
        /// by the gyro "Aim Engage button" gate. App wires this
        /// against the per-device InputState bool reader.</summary>
        public static Func<string, string, int, bool> ButtonHeldProvider { get; set; }

        /// <summary>— resolved Aim-Engage state for the slot. App
        /// runs the per-tick Hold/Toggle logic in
        /// <c>InputManager.UpdateGyroEngageStates</c> against the
        /// slot's configured engage button and mode, then OR-combines
        /// with the <c>SetGyroEngaged</c> macro action's per-slot bit.
        /// Returns true (always-on) when unwired or when no engage
        /// source is configured on the slot. Both gyro evaluators
        /// (mapping-row and motion passthrough) read this single
        /// resolved bit so the engage decision is consistent within
        /// a tick regardless of how many rows reference gyro.</summary>
        public static Func<int, bool> AimEngageStateProvider { get; set; }

        /// <summary>— current polling frequency (Hz). Used by the
        /// dual-threshold smoothing buffer to convert
        /// <c>GyroSmoothingWindowMs</c> into a sample count. App
        /// returns <c>1000 / Settings.PollingIntervalMs</c>; returns
        /// 60Hz if unwired.</summary>
        public static Func<float> PollHzProvider { get; set; }

        /// <summary>Absolute desktop cursor position normalized to the [-1..+1]
        /// stick range per screen axis (issue #107), as <c>(normX, normY)</c>.
        /// Unclamped: the magnitude can exceed 1 toward the screen edges (the
        /// per-source <see cref="ReadTunedMouseCursor"/> applies sensitivity then
        /// clamps). Screen center reads (0, 0). The App layer's CursorControlService
        /// samples <c>GetCursorPos</c> at 200 Hz, normalizes against the primary
        /// monitor, and publishes here. Returns (0, 0) when unwired.</summary>
        public static Func<(float normX, float normY)> MouseCursorProvider { get; set; }

        private static GyroTuning GetGyroTuning(string deviceGuid, int slotIndex)
        {
            var provider = GyroTuningProvider;
            if (provider == null || string.IsNullOrEmpty(deviceGuid))
                return new GyroTuning
                {
                    SensH = 1f, SensV = 1f, OutputCurve = "Linear",
                    Space = "Local", PlayerYawRelax = 1.41f, WorldSideReduction = 0.125f,
                    TighteningRadPerSec = 0f, SmoothingThresholdRadPerSec = 0f, SmoothingWindowSeconds = 0.05f,
                    RealWorldCalibration = 0f,
                    ApplyToPassthrough = false,
                };
            return provider(deviceGuid, slotIndex);
        }

        // dual-threshold gyro smoothing buffer. Keyed by
        // (deviceGuid, slotIndex). Single-threaded (polling thread only).
        private static readonly Dictionary<(string, int), (float x, float y)[]> _gyroSampleBuffers = new();
        private static readonly Dictionary<(string, int), int> _gyroSampleHeads = new();

        private static (float, float) ApplyDualThresholdSmoothing(
            string deviceGuid, int slotIndex, float yaw, float pitch, GyroTuning tuning)
        {
            float bottom = tuning.TighteningRadPerSec;
            float top    = tuning.SmoothingThresholdRadPerSec;
            // Disabled (both zero) → pass through.
            if (bottom <= 0f && top <= 0f) return (yaw, pitch);

            float mag = (float)System.Math.Sqrt(yaw * yaw + pitch * pitch);
            float immediate = top <= bottom
                ? (mag < bottom ? 0f : 1f)
                : System.Math.Clamp((mag - bottom) / (top - bottom), 0f, 1f);
            float smooth = 1f - immediate;

            float hz = PollHzProvider?.Invoke() ?? 60f;
            int N = (int)System.Math.Max(1, tuning.SmoothingWindowSeconds * hz);

            var key = (deviceGuid ?? "", slotIndex);
            if (!_gyroSampleBuffers.TryGetValue(key, out var buf) || buf.Length != N)
            {
                buf = new (float x, float y)[N];
                _gyroSampleBuffers[key] = buf;
                _gyroSampleHeads[key] = 0;
            }
            int head = (_gyroSampleHeads[key] + 1) % N;
            _gyroSampleHeads[key] = head;
            buf[head] = (yaw * smooth, pitch * smooth);

            float xSum = 0, ySum = 0;
            for (int i = 0; i < N; i++) { xSum += buf[i].x; ySum += buf[i].y; }
            return (xSum / N + yaw * immediate, ySum / N + pitch * immediate);
        }

        /// <summary>Player Space projection. Yaw projected onto
        /// the controller's gravity-vertical axis; pitch stays local.
        /// Mirrors GamepadMotion.hpp:CalculatePlayerSpaceGyro. The
        /// gravX argument is unused (the player-space formula only
        /// needs gravity's Y and Z components) but kept in the
        /// signature for symmetry with WorldSpaceProject.</summary>
        private static (float yaw, float pitch) PlayerSpaceProject(
            float gPitch, float gYaw, float gRoll,
            float _gravX, float gravY, float gravZ, float yawRelax)
        {
            // worldYaw = -(gravY * gyroY + gravZ * gyroZ)
            float worldYaw = -(gravY * gYaw + gravZ * gRoll);
            float worldSign = worldYaw < 0f ? -1f : 1f;
            float yzMag = (float)Math.Sqrt(gYaw * gYaw + gRoll * gRoll);
            float yawOut = worldSign * Math.Min(Math.Abs(worldYaw) * yawRelax, yzMag);
            return (yawOut, gPitch);
        }

        /// <summary>World Space projection. Both yaw and pitch
        /// projected onto world axes. Mirrors
        /// GamepadMotion.hpp:CalculateWorldSpaceGyro.</summary>
        private static (float yaw, float pitch) WorldSpaceProject(
            float gPitch, float gYaw, float gRoll,
            float gravX, float gravY, float gravZ, float sideReduce)
        {
            float worldYaw = -gravX * gPitch - gravY * gYaw - gravZ * gRoll;

            // pitchAxis = (1 - gravX*gravX, -gravY*gravX, -gravZ*gravX), normalized
            float pxX = 1f - gravX * gravX;
            float pxY = -gravY * gravX;
            float pxZ = -gravZ * gravX;
            float pxLenSq = pxX * pxX + pxY * pxY + pxZ * pxZ;
            float pitchOut = 0f;
            if (pxLenSq > 0f)
            {
                float inv = 1f / (float)System.Math.Sqrt(pxLenSq);
                pxX *= inv; pxY *= inv; pxZ *= inv;
                float flatness = System.Math.Abs(gravY);
                float upness   = System.Math.Abs(gravZ);
                float maxFU    = System.Math.Max(flatness, upness);
                float reduction = sideReduce <= 0f
                    ? 1f
                    : System.Math.Clamp((maxFU - sideReduce) / sideReduce, 0f, 1f);
                pitchOut = reduction * (pxX * gPitch + pxY * gYaw + pxZ * gRoll);
            }
            return (worldYaw, pitchOut);
        }

        // Per-device EMA smoothing state for gyro rates. Single-threaded
        // (polling thread is the only reader/writer for binding-layer
        // gyro reads); a stale read post-recalibration self-heals in
        // 1/(1-α) frames so no explicit clear is required.
        private static readonly Dictionary<string, float[]> _gyroSmoothingState = new();

        private static float ApplyGyroSmoothing(string deviceGuid, int axis, float rawRate, float alpha)
        {
            if (alpha <= 0f) return rawRate;
            if (alpha > 0.99f) alpha = 0.99f; // pinning at 1 freezes the output
            string key = deviceGuid ?? "";
            if (!_gyroSmoothingState.TryGetValue(key, out var smoothed))
            {
                smoothed = new float[3];
                _gyroSmoothingState[key] = smoothed;
            }
            if (axis < 0 || axis >= smoothed.Length) return rawRate;
            smoothed[axis] = smoothed[axis] * alpha + rawRate * (1f - alpha);
            return smoothed[axis];
        }

        private static float ApplyOutputCurve(float normalized, string curveName)
        {
            // normalized is in [-1..+1] before the caller's clamp.
            // Curves preserve sign and map |x| → |y| in [0..1].
            if (string.IsNullOrEmpty(curveName) || curveName == "Linear") return normalized;
            float sign = normalized < 0 ? -1f : 1f;
            float abs = normalized < 0 ? -normalized : normalized;
            float shaped = curveName switch
            {
                "Aggressive" => abs * abs,                                          // x²: slow stays slow
                "Relaxed"    => (float)System.Math.Sqrt(abs),                       // √x: slow amplifies
                "Wide"       => (float)System.Math.Pow(abs, 1.5),                   // between linear and aggressive
                "ExtraWide"  => (float)System.Math.Pow(abs, 2.5),                   // more than aggressive
                _            => abs,
            };
            return sign * shaped;
        }

        private static float ApplyGyroAcceleration(float normalized, float accel)
        {
            // Rate-dependent gain: slow movements pass through unchanged,
            // fast movements amplify. accel=0 → no-op. accel=2 → ~3× boost
            // at saturation (|x|=1). Clamping happens at the caller.
            if (accel <= 0f) return normalized;
            float absX = normalized < 0 ? -normalized : normalized;
            return normalized * (1f + accel * absX);
        }

        /// <summary>Inspects the descriptor of a MappingSource (without
        /// the legacy "I" / "H" / "IH" prefix — the new schema stores
        /// flags separately).</summary>
        public static SourceType ClassifyDescriptor(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor) || descriptor == "0")
                return SourceType.Unmapped;

            string s = descriptor.Trim();
            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // "Touchpad N ..." can be a touchpad-button (Click /
                // Finger M Down), a touchpad-finger axis (Finger M X /
                // Y / Pressure), or a touchpad-gesture. Disambiguate by
                // the third token: anything that isn't "Click" or
                // "Finger" is a gesture name. Touchpad-finger axes fall
                // through TouchpadButton classification today since the
                // axis readers special-case them by descriptor pattern
                // rather than enum tag.
                var tpParts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tpParts.Length >= 3
                    && !tpParts[2].Equals("Click", StringComparison.Ordinal)
                    && !tpParts[2].Equals("Finger", StringComparison.Ordinal))
                    return SourceType.TouchpadGesture;
                return SourceType.TouchpadButton;
            }
            // Order matters: "Motion " before "Gyro " (a "Motion Gyro" must not
            // fall through to the per-axis Gyro classifier).
            if (s.StartsWith("Motion ", StringComparison.Ordinal))
                return SourceType.Motion;
            if (s.StartsWith("Gyro ", StringComparison.Ordinal))
                return SourceType.Gyro;
            if (s.StartsWith("Mouse Position ", StringComparison.Ordinal))
                return SourceType.MouseCursor;
            if (s.StartsWith("Midi ", StringComparison.Ordinal))
                return SourceType.Midi;

            string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return SourceType.Unmapped;

            return parts[0].ToLowerInvariant() switch
            {
                "button" => SourceType.Button,
                "axis"   => SourceType.Axis,
                "slider" => SourceType.Slider,
                "pov"    => SourceType.PovDirection,
                _        => SourceType.Unmapped,
            };
        }

        /// <summary>True for any MIDI-input descriptor
        /// (<c>"Midi Note N"</c> / <c>"Midi CC N"</c> /
        /// <c>"Midi Pitch Bend"</c>).</summary>
        public static bool IsMidiDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && descriptor.StartsWith("Midi ", StringComparison.Ordinal);

        /// <summary>Parses a MIDI descriptor into a kind and index.
        /// kind: 'N' note, 'C' cc absolute, 'U' cc encoder-up pulse,
        /// 'D' cc encoder-down pulse, 'P' pitch bend (index unused).
        /// Returns false for anything that isn't a MIDI descriptor.</summary>
        private static bool TryParseMidi(string descriptor, out char kind, out int index)
        {
            kind = '\0';
            index = -1;
            if (string.IsNullOrEmpty(descriptor)) return false;
            var parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("Midi", StringComparison.Ordinal))
                return false;
            if (parts[1].Equals("Note", StringComparison.Ordinal) && parts.Length >= 3
                && int.TryParse(parts[2], out index))
            { kind = 'N'; return index >= 0 && index < MidiInputState.NoteCount; }
            if (parts[1].Equals("CC", StringComparison.Ordinal) && parts.Length >= 3
                && int.TryParse(parts[2], out index))
            {
                // "Midi CC N" absolute, "Midi CC N Up"/"Down" encoder pulses.
                kind = 'C';
                if (parts.Length >= 4)
                {
                    if (parts[3].Equals("Up", StringComparison.Ordinal)) kind = 'U';
                    else if (parts[3].Equals("Down", StringComparison.Ordinal)) kind = 'D';
                }
                return index >= 0 && index < MidiInputState.CcCount;
            }
            if (parts[1].Equals("Pitch", StringComparison.Ordinal))
            { kind = 'P'; index = 0; return true; }
            return false;
        }

        /// <summary>True for the bundled motion-source descriptors
        /// <c>"Motion Gyro"</c> and <c>"Motion Accel"</c>. The mapping-row
        /// path uses these to bind a device's 3-axis sensor stream to the
        /// slot's <c>MotionGyro</c> / <c>MotionAccel</c> targets. Per-axis
        /// reads (gyro-as-stick) keep using <see cref="IsGyroDescriptor"/>
        /// against <c>"Gyro Pitch/Yaw/Roll"</c>.</summary>
        public static bool IsMotionDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && descriptor.StartsWith("Motion ", StringComparison.Ordinal);

        /// <summary>True for touchpad-gesture descriptors —
        /// <c>"Touchpad N <GestureName>"</c> where GestureName is
        /// neither <c>Click</c> nor <c>Finger ...</c>. Distinguishes
        /// gesture sources from the legacy touchpad-button and per-
        /// finger axis descriptors that share the same <c>Touchpad </c>
        /// prefix.</summary>
        public static bool IsTouchpadGestureDescriptor(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return false;
            if (!descriptor.StartsWith("Touchpad ", StringComparison.Ordinal)) return false;
            var parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3
                && !parts[2].Equals("Click", StringComparison.Ordinal)
                && !parts[2].Equals("Finger", StringComparison.Ordinal);
        }

        /// <summary>Parses a touchpad-gesture descriptor into its pad
        /// index + gesture name. Returns true on success;
        /// <paramref name="padIdx"/> is the integer N from
        /// <c>"Touchpad N ..."</c> and <paramref name="gestureName"/>
        /// is the remainder (joined with single spaces — gesture names
        /// are conventionally single tokens but the parser doesn't
        /// enforce that).</summary>
        public static bool TryParseTouchpadGesture(string descriptor,
            out int padIdx, out string gestureName)
        {
            padIdx = -1;
            gestureName = null;
            if (string.IsNullOrEmpty(descriptor)) return false;
            if (!descriptor.StartsWith("Touchpad ", StringComparison.Ordinal)) return false;
            var parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!int.TryParse(parts[1], out padIdx)) return false;
            if (parts[2].Equals("Click", StringComparison.Ordinal)) return false;
            if (parts[2].Equals("Finger", StringComparison.Ordinal)) return false;
            gestureName = parts.Length == 3
                ? parts[2]
                : string.Join(" ", parts, 2, parts.Length - 2);
            return true;
        }

        /// <summary>Returns true if the named gesture fired on the
        /// given <c>(slotIndex, deviceGuid, padIdx)</c> on the current
        /// polling tick. Slot-keyed because the gesture engine runs
        /// per-slot now: two slots sharing one physical touchpad each
        /// keep their own GestureContext / FiredGesturesThisFrame, so
        /// the toggles on each slot's Touchpad tab apply only to that
        /// slot's mapping rows. Returns false when unwired (engine not
        /// running, no touchpad device).</summary>
        public static Func<int, string, int, string, bool> TouchpadGestureFiredProvider { get; set; }

        /// <summary>Returns the current value of a continuous gesture
        /// axis (<c>PinchAxis</c> / <c>RotateAxis</c>, plus the per-slot
        /// Stick X/Y output) on the given <c>(slotIndex, deviceGuid,
        /// padIdx)</c>. Slot-keyed for the same reason as
        /// <see cref="TouchpadGestureFiredProvider"/>: each slot reads
        /// its own JoystickMaxRadius / InnerDeadzone tuning. Range
        /// -1..+1, 0 when no source is active. Returns 0 when
        /// unwired.</summary>
        public static Func<int, string, int, string, float> TouchpadGestureAxisProvider { get; set; }

        /// <summary>Returns the per-(slotIndex, deviceGuid, padIdx) touchpad
        /// settings snapshot used by <see cref="TryReadTouchpadAxis"/> to
        /// apply per-axis mouse sensitivity and inversion to the touchpad
        /// finger → KBM mouse delta. Slot-keyed so the same touchpad in
        /// two slots can carry different mouse tuning (each slot's
        /// PadSetting lives on its own UserSetting). Returns null when
        /// unwired, in which case the reader falls back to a neutral
        /// 1.0× / non-inverted multiplier so existing behavior is
        /// preserved.</summary>
        public static Func<int, string, int, PadForge.Engine.Touchpad.TouchpadGestureSettings> TouchpadMouseSettingsProvider { get; set; }

        /// <summary>True for the bipolar continuous-axis gesture
        /// descriptors. These return a float value via
        /// <see cref="TouchpadGestureAxisProvider"/> rather than a
        /// button-fire bool via
        /// <see cref="TouchpadGestureFiredProvider"/>. Includes the
        /// two-finger Pinch / Rotate axes and the single-finger
        /// Stick X / Y output channels.</summary>
        public static bool IsTouchpadGestureAxis(string gestureName)
            => string.Equals(gestureName, "PinchAxis", StringComparison.Ordinal)
            || string.Equals(gestureName, "RotateAxis", StringComparison.Ordinal)
            || string.Equals(gestureName, "StickX", StringComparison.Ordinal)
            || string.Equals(gestureName, "StickY", StringComparison.Ordinal);

        /// <summary>Parses a bundled motion descriptor into its sub-channel.
        /// <c>"Motion Gyro"</c> → 0, <c>"Motion Accel"</c> → 1, anything
        /// else → -1.</summary>
        public static int ParseMotionSubChannel(string descriptor)
        {
            string s = (descriptor ?? "").Trim();
            if (!s.StartsWith("Motion ", StringComparison.Ordinal)) return -1;
            string sub = s.Substring(7).Trim();
            if (sub.Equals("Gyro",  StringComparison.OrdinalIgnoreCase)) return 0;
            if (sub.Equals("Accel", StringComparison.OrdinalIgnoreCase)) return 1;
            return -1;
        }

        /// <summary>Parses a gyro descriptor "Gyro Pitch/Yaw/Roll" into
        /// the corresponding <see cref="CustomInputState.Gyro"/> index
        /// (0=pitch, 1=yaw, 2=roll). Returns -1 on unrecognized.
        /// "Gyro Horizontal" returns 1 (yaw is the horizontal anchor;
        /// callers must check <see cref="IsHorizontalBlendDescriptor"/>
        /// to apply the yaw+roll blend logic).</summary>
        private static int ParseGyroAxisIndex(string descriptor)
        {
            string s = (descriptor ?? "").Trim();
            if (!s.StartsWith("Gyro ", StringComparison.Ordinal)) return -1;
            string axis = s.Substring(5).Trim();
            if (axis.Equals("Pitch",      StringComparison.OrdinalIgnoreCase)) return 0;
            if (axis.Equals("Yaw",        StringComparison.OrdinalIgnoreCase)) return 1;
            if (axis.Equals("Roll",       StringComparison.OrdinalIgnoreCase)) return 2;
            if (axis.Equals("Horizontal", StringComparison.OrdinalIgnoreCase)) return 1; // yaw anchor
            return -1;
        }

        /// <summary>True for the <c>Gyro Horizontal</c> auto-blend
        /// descriptor — caller reads BOTH yaw and roll and picks the
        /// dominant axis with sign. Steam's Handheld+Roll style: works
        /// whether the user grips the controller upright (yaw drives
        /// horizontal aim) or flat (roll drives it).</summary>
        private static bool IsHorizontalBlendDescriptor(string descriptor)
        {
            string s = (descriptor ?? "").Trim();
            if (!s.StartsWith("Gyro ", StringComparison.Ordinal)) return false;
            return s.Substring(5).Trim().Equals("Horizontal", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for "Gyro Pitch" / "Gyro Yaw" / "Gyro Roll"
        /// descriptors. Public so SourceEvaluator can special-case gyro:
        /// both stick and mouse targets are rate-direct, and the stick
        /// (absolute-axis) path flips the sign so the stick deflects toward
        /// the twist. Saves SourceEvaluator re-parsing the descriptor.</summary>
        public static bool IsGyroDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && descriptor.StartsWith("Gyro ", StringComparison.Ordinal);

        /// <summary>True for the absolute cursor-position descriptors
        /// ("Mouse Position X" / "Mouse Position Y", issue #107). Drives the
        /// per-source Mouse Cursor Sensitivity slider's visibility and the
        /// reader branches that pull from <see cref="MouseCursorProvider"/>.</summary>
        public static bool IsMouseCursorDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && descriptor.StartsWith("Mouse Position ", StringComparison.Ordinal);

        /// <summary>Reads the per-source absolute cursor axis (issue #107): pulls
        /// the normalized cursor position from <see cref="MouseCursorProvider"/>,
        /// selects the X or Y component from the descriptor, applies the per-source
        /// <see cref="MappingSource.MouseCursorSensitivity"/>, then clamps to
        /// [-1..+1]. With sensitivity 1.0 the stick reaches full deflection at 10%
        /// of screen width from center (the provider already divides by width/10).
        /// Returns 0 for non-cursor descriptors or an unwired provider. Invert is
        /// applied by the public Evaluate* wrappers, not here (matches the gyro and
        /// generic-axis paths).</summary>
        private static float ReadTunedMouseCursor(MappingSource src)
        {
            if (src == null) return 0f;
            var provider = MouseCursorProvider;
            if (provider == null) return 0f;
            var (normX, normY) = provider();

            string s = src.Descriptor ?? "";
            float baseVal;
            if (s.EndsWith(" X", StringComparison.Ordinal)) baseVal = normX;
            else if (s.EndsWith(" Y", StringComparison.Ordinal)) baseVal = normY;
            else return 0f;

            float v = baseVal * (float)src.MouseCursorSensitivity;
            if (v < -1f) v = -1f;
            else if (v > 1f) v = 1f;
            return v;
        }

        /// <summary>The gravity-lean input descriptor. A first-class picker
        /// entry (like "Gyro Roll"): mapping it to an axis target drives that
        /// axis from controller tilt via <c>SourceKindRuntime.TickMotionLean</c>.
        /// SourceEvaluator routes a Direct source carrying this descriptor into
        /// the same math as Kind="MotionLeanX"; per-source ParamMotionInnerDz /
        /// ParamMotionOuterDz / ParamControllerOrientation tune it (defaults
        /// 15 / 135 / Forward — the JSM motion-deadzone defaults).</summary>
        public const string MotionLeanDescriptor = "Motion Lean";

        /// <summary>True when the descriptor is <see cref="MotionLeanDescriptor"/>.</summary>
        public static bool IsMotionLeanDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && string.Equals(descriptor.Trim(), MotionLeanDescriptor, StringComparison.OrdinalIgnoreCase);

        /// <summary>Public form of <see cref="ReadCalibratedGyroRate"/>:
        /// returns the bias-subtracted gyro rate (rad/s) for the source's
        /// descriptor on the given state, or 0 for non-gyro descriptors /
        /// unknown axes / null state.Gyro. <paramref name="slotIndex"/>
        /// selects which slot's per-(device, slot) bias to subtract; pass
        /// -1 for callers that have no slot context (no bias subtraction
        /// is applied in that case — the read passes through raw).</summary>
        public static float GetCalibratedGyroRate(CustomInputState state, MappingSource src, int slotIndex = -1)
        {
            if (src == null) return 0f;
            int axis = ParseGyroAxisIndex(src.Descriptor);
            if (axis < 0) return 0f;
            return ReadCalibratedGyroRate(state, axis, src.DeviceGuid, slotIndex);
        }

        /// <summary>Returns a gyro reading processed through the full
        /// per-device tuning chain:
        /// <list type="number">
        /// <item>bias subtraction (per-device calibration)</item>
        /// <item>deadzone (subtract-style: rates within deadzone → 0,
        ///   rates past deadzone pass through with deadzone subtracted
        ///   so there is no discontinuous jump at the threshold)</item>
        /// <item>axis sensitivity (H for Yaw/Roll, V for Pitch)</item>
        /// <item>per-source <see cref="MappingSource.GyroSensitivity"/>
        ///   multiplier on top of device-level H/V</item>
        /// </list>
        /// Returns 0 for non-gyro descriptors / unknown axes / null
        /// state.Gyro. Used by all three reader branches (bool / bipolar
        /// / unipolar) so device-level tuning applies uniformly.</summary>
        private static float ReadTunedGyroRate(CustomInputState state, MappingSource src, int slotIndex, out int gyroAxis, out GyroTuning tuning)
        {
            gyroAxis = -1;
            tuning = default;
            if (state == null || src == null) return 0f;

            tuning = GetGyroTuning(src.DeviceGuid, slotIndex);

            int descAxis = ParseGyroAxisIndex(src.Descriptor);
            bool isHorizontal = IsHorizontalBlendDescriptor(src.Descriptor);
            bool isPitchSource = descAxis == 0;
            bool isRollSource  = descAxis == 2;
            gyroAxis = isHorizontal ? 1 : descAxis;
            if (descAxis < 0 && !isHorizontal) return 0f;

            // ─── Gates ───────────────────────────────────────────
            // Easy Aim — gate gyro on right-stick deflection past the
            // configured threshold. Threshold 0 = always-on (default).
            if (tuning.EasyAimStickThreshold01 > 0f && slotIndex >= 0)
            {
                float defl = SlotRightStickDeflectionProvider?.Invoke(slotIndex) ?? 1f;
                if (defl < tuning.EasyAimStickThreshold01) return 0f;
            }
            // Aim Engage — per-slot resolved engaged bit. Held button or
            // sticky Toggle bit OR macro engagement; the App layer's
            // UpdateGyroEngageStates settles the bit once per tick from
            // the engage button + GyroAimEngageMode, then OR-combines
            // with the SetGyroEngaged macro action's slot bit. Composes
            // AND-style with Easy Aim (both must be active).
            if (slotIndex >= 0)
            {
                bool engaged = AimEngageStateProvider?.Invoke(slotIndex) ?? true;
                if (!engaged) return 0f;
            }

            // ─── Bias-subtracted gyro components ─────────────────
            string deviceGuid = src.DeviceGuid;
            float gPitch = ReadCalibratedGyroRate(state, 0, deviceGuid, slotIndex);
            float gYaw   = ReadCalibratedGyroRate(state, 1, deviceGuid, slotIndex);
            float gRoll  = ReadCalibratedGyroRate(state, 2, deviceGuid, slotIndex);

            // ─── Space projection ────────────────────────────────
            float yaw, pitch;
            string space = tuning.Space ?? "Local";
            if (space == "Player")
            {
                var grav = GravityProvider?.Invoke(deviceGuid) ?? (0f, 0f, -1f);
                (yaw, pitch) = PlayerSpaceProject(
                    gPitch, gYaw, gRoll, grav.gx, grav.gy, grav.gz, tuning.PlayerYawRelax);
            }
            else if (space == "World")
            {
                var grav = GravityProvider?.Invoke(deviceGuid) ?? (0f, 0f, -1f);
                (yaw, pitch) = WorldSpaceProject(
                    gPitch, gYaw, gRoll, grav.gx, grav.gy, grav.gz, tuning.WorldSideReduction);
            }
            else // Local
            {
                pitch = gPitch;
                if (isHorizontal)
                    yaw = Math.Abs(gYaw) >= Math.Abs(gRoll) ? gYaw : gRoll;
                else if (isRollSource)
                    yaw = gRoll;
                else
                    yaw = gYaw;
            }

            // ─── Smoothing (dual-threshold supersedes legacy EMA) ───
            bool useDualThreshold =
                tuning.TighteningRadPerSec > 0f || tuning.SmoothingThresholdRadPerSec > 0f;
            if (useDualThreshold)
            {
                (yaw, pitch) = ApplyDualThresholdSmoothing(
                    deviceGuid, slotIndex, yaw, pitch, tuning);
            }
            else if (tuning.SmoothingAlpha > 0f)
            {
                // v3.3 legacy EMA path — kept for back-compat when the
                // user has a non-zero SmoothingAlpha and both v3.4
                // thresholds at zero.
                yaw   = ApplyGyroSmoothing(deviceGuid, 1, yaw,   tuning.SmoothingAlpha);
                pitch = ApplyGyroSmoothing(deviceGuid, 0, pitch, tuning.SmoothingAlpha);
            }

            // In non-Local space, Gyro Roll source has no independent
            // output (roll folds into the yaw projection).
            if (isRollSource && space != "Local") return 0f;

            // ─── Per-axis tuning (deadzone, sens, RWC, invert) ───
            float perSourceSens = (float)(src.GyroSensitivity > 0 ? src.GyroSensitivity : 1.0);
            float rwc = tuning.RealWorldCalibration > 0f ? tuning.RealWorldCalibration : 1f;
            float rate;
            if (isPitchSource)
            {
                rate = ApplyDeadZone(pitch, tuning.DeadZoneRadPerSec)
                       * tuning.SensV * perSourceSens * rwc;
                if (tuning.InvertPitch) rate = -rate;
            }
            else
            {
                rate = ApplyDeadZone(yaw, tuning.DeadZoneRadPerSec)
                       * tuning.SensH * perSourceSens * rwc;
                if (tuning.InvertYawRoll) rate = -rate;
            }
            return rate;
        }

        /// <summary>Applies the per-(device, slot) gyro tuning chain to
        /// the raw motion-sensor reading so the virtual controller's
        /// motion passthrough (the Sony report packer and the DSU
        /// broadcast) reflects the Gyro tab settings — calibration bias,
        /// deadzone, sensitivity, smoothing, space projection, real-world
        /// calibration, response curve, acceleration, invert, and the
        /// Easy Aim / Aim Engage gates.
        ///
        /// <para>Outputs the tuned pitch / yaw / roll in rad/s, the same
        /// frame and unit as <see cref="CustomInputState.Gyro"/>. The
        /// caller (<c>InputManager.UpdateMotionSnapshots</c>) handles the
        /// rad-to-deg conversion and the DSU sign convention exactly as
        /// before.</para>
        ///
        /// <para>Calibration bias subtraction always applies, both
        /// toggle states — it is drift correction, not tuning. When the
        /// slot's <see cref="GyroTuning.ApplyToPassthrough"/> flag is
        /// off, only the calibrated reading is returned and the
        /// discretionary tuning is skipped. With the flag on and every
        /// Gyro tab control at its default, the tuning chain is the
        /// identity, so the on and off paths agree: both relay the
        /// calibrated reading.</para>
        ///
        /// <para>Distinct from <see cref="ReadTunedGyroRate"/>: that
        /// produces one normalized axis for a mapping source; this
        /// produces all three physical-rate axes for the motion report
        /// and is not clamped to the mapping [-1, +1] range.</para></summary>
        public static void GetPassthroughGyro(
            CustomInputState state, string deviceGuid, int slotIndex,
            out float pitch, out float yaw, out float roll)
        {
            pitch = yaw = roll = 0f;
            if (state == null || state.Gyro == null || state.Gyro.Length < 3) return;

            var tuning = GetGyroTuning(deviceGuid, slotIndex);

            // Calibration bias subtraction is mandatory drift correction,
            // NOT optional tuning — it always applies to the passthrough,
            // both toggle states. The at-rest bias the calibration
            // measured would otherwise relay straight into the motion
            // report and the consuming game / emulator would integrate it
            // as continuous drift. The Gyro tab's Live rate readout
            // subtracts this same bias for display, so a drifting
            // passthrough still reads ~0 there — the readout was masking
            // the bug.
            float gPitch = ReadCalibratedGyroRate(state, 0, deviceGuid, slotIndex);
            float gYaw   = ReadCalibratedGyroRate(state, 1, deviceGuid, slotIndex);
            float gRoll  = ReadCalibratedGyroRate(state, 2, deviceGuid, slotIndex);

            if (!tuning.ApplyToPassthrough)
            {
                // Toggle off: send the calibrated reading only — no
                // discretionary tuning (sensitivity, smoothing, deadzone,
                // curve, invert, space projection, Easy Aim / Aim Engage
                // gates). Calibration still applies; it is not tuning.
                pitch = gPitch;
                yaw   = gYaw;
                roll  = gRoll;
                return;
            }

            // Gates — Easy Aim (right-stick deflection) and Aim Engage
            // (held button). Both default to no-op; when either is set
            // and not satisfied the passthrough gyro zeroes, the same as
            // the mapping path. Intentional: a user with Toggle/Hold
            // engage configured expects the virtual pad's gyro report
            // to follow the same gate so an emulator sees motion only
            // while engage is active.
            if (tuning.EasyAimStickThreshold01 > 0f && slotIndex >= 0)
            {
                float defl = SlotRightStickDeflectionProvider?.Invoke(slotIndex) ?? 1f;
                if (defl < tuning.EasyAimStickThreshold01) return;
            }
            if (slotIndex >= 0)
            {
                bool engaged = AimEngageStateProvider?.Invoke(slotIndex) ?? true;
                if (!engaged) return;
            }

            // Space projection. Local keeps three independent axes;
            // Player / World fold roll into the yaw projection so roll
            // has no separate output (matches the mapping path).
            string space = tuning.Space ?? "Local";
            bool local = space != "Player" && space != "World";
            float pPitch, pYaw, pRoll;
            if (space == "Player")
            {
                var grav = GravityProvider?.Invoke(deviceGuid) ?? (0f, 0f, -1f);
                (pYaw, pPitch) = PlayerSpaceProject(
                    gPitch, gYaw, gRoll, grav.gx, grav.gy, grav.gz, tuning.PlayerYawRelax);
                pRoll = 0f;
            }
            else if (space == "World")
            {
                var grav = GravityProvider?.Invoke(deviceGuid) ?? (0f, 0f, -1f);
                (pYaw, pPitch) = WorldSpaceProject(
                    gPitch, gYaw, gRoll, grav.gx, grav.gy, grav.gz, tuning.WorldSideReduction);
                pRoll = 0f;
            }
            else
            {
                pPitch = gPitch; pYaw = gYaw; pRoll = gRoll;
            }

            // Smoothing. Dual-threshold supersedes the legacy EMA. The
            // dual-threshold filter works on the (yaw, pitch) aim pair;
            // roll gets its own buffer via a channel-suffixed key.
            //
            // The buffers are keyed by device. The passthrough and the
            // gyro-mapping path (ReadTunedGyroRate) are separate signal
            // chains, so the passthrough takes a distinct key suffix —
            // the bare deviceGuid would advance the shared buffer twice
            // per frame on a slot running both, halving the window.
            string smKey = (deviceGuid ?? "") + "pt";
            bool useDualThreshold =
                tuning.TighteningRadPerSec > 0f || tuning.SmoothingThresholdRadPerSec > 0f;
            if (useDualThreshold)
            {
                (pYaw, pPitch) = ApplyDualThresholdSmoothing(
                    smKey, slotIndex, pYaw, pPitch, tuning);
                if (local)
                    (pRoll, _) = ApplyDualThresholdSmoothing(
                        smKey + "roll", slotIndex, pRoll, 0f, tuning);
            }
            else if (tuning.SmoothingAlpha > 0f)
            {
                pYaw   = ApplyGyroSmoothing(smKey, 1, pYaw,   tuning.SmoothingAlpha);
                pPitch = ApplyGyroSmoothing(smKey, 0, pPitch, tuning.SmoothingAlpha);
                if (local)
                    pRoll = ApplyGyroSmoothing(smKey, 2, pRoll, tuning.SmoothingAlpha);
            }

            float rwc = tuning.RealWorldCalibration > 0f ? tuning.RealWorldCalibration : 1f;

            // Pitch uses vertical sensitivity; yaw and roll use
            // horizontal. Invert pitch / yaw flags mirror the mapping
            // path (the yaw flag also covers roll).
            pitch = ShapePassthroughAxis(pPitch, tuning.DeadZoneRadPerSec,
                tuning.SensV * rwc, tuning.InvertPitch, tuning.OutputCurve, tuning.Acceleration);
            yaw = ShapePassthroughAxis(pYaw, tuning.DeadZoneRadPerSec,
                tuning.SensH * rwc, tuning.InvertYawRoll, tuning.OutputCurve, tuning.Acceleration);
            roll = ShapePassthroughAxis(pRoll, tuning.DeadZoneRadPerSec,
                tuning.SensH * rwc, tuning.InvertYawRoll, tuning.OutputCurve, tuning.Acceleration);
        }

        /// <summary>Per-axis tail of the passthrough chain: deadzone,
        /// sensitivity, invert, then response curve + acceleration in the
        /// normalized space the mapping path uses. Unlike the mapping
        /// path the result is NOT clamped — the motion report carries a
        /// physical rate, not a [-1, +1] deflection, so a fast spin past
        /// the curve's reference rate must stay a fast spin.</summary>
        private static float ShapePassthroughAxis(
            float rate, float deadZone, float sens, bool invert,
            string curve, float accel)
        {
            float v = ApplyDeadZone(rate, deadZone) * sens;
            if (invert) v = -v;
            bool linear = string.IsNullOrEmpty(curve) || curve == "Linear";
            if (linear && accel <= 0f) return v;
            float norm = v * GyroScale;
            norm = ApplyOutputCurve(norm, curve);
            norm = ApplyGyroAcceleration(norm, accel);
            return norm / GyroScale;
        }

        /// <summary>Subtract-style deadzone: rates within ±dz zero out,
        /// rates past pass through with dz subtracted (no discontinuity
        /// at the threshold).</summary>
        private static float ApplyDeadZone(float rate, float dz)
        {
            if (dz <= 0f) return rate;
            if (rate > dz)  return rate - dz;
            if (rate < -dz) return rate + dz;
            return 0f;
        }

        /// <summary>Reads <c>state.Gyro[gyroAxis]</c> minus the
        /// (device, slot) at-rest bias (looked up via
        /// <see cref="GyroBiasProvider"/>). Returns 0 when the
        /// (device, slot) has no calibration entry — caller gets the
        /// raw reading minus zero, which is the right default for
        /// "uncalibrated yet, just connected." Defensive against null
        /// state.Gyro[].</summary>
        private static float ReadCalibratedGyroRate(CustomInputState state, int gyroAxis, string deviceGuid, int slotIndex)
        {
            if (state == null || state.Gyro == null) return 0f;
            if (gyroAxis < 0 || gyroAxis >= state.Gyro.Length) return 0f;
            float raw = state.Gyro[gyroAxis];
            var provider = GyroBiasProvider;
            if (provider == null || string.IsNullOrEmpty(deviceGuid)) return raw;
            var bias = provider(deviceGuid, slotIndex);
            return gyroAxis switch
            {
                0 => raw - bias.pitch,
                1 => raw - bias.yaw,
                2 => raw - bias.roll,
                _ => raw,
            };
        }

        // ─── Per-target-type evaluators ────────────────────────────────

        /// <summary>Evaluates a source for a button-class target. Returns
        /// the post-Invert pressed state. Axis and slider sources cross a
        /// threshold (per-source DeadZone overrides the global threshold
        /// when set).</summary>
        public static bool EvaluateForButtonTarget(
            CustomInputState state, MappingSource src, int globalThresholdPercent, int slotIndex = -1)
        {
            if (state == null || src == null) return false;

            bool raw = ReadAsBool(state, src, globalThresholdPercent, slotIndex);

            // Axis sources internalize Invert inside ReadAsBool — for
            // half-axis it picks which half to test, for full-axis it
            // flips the comparison. Applying Invert again here would
            // double-cancel, which is what broke the standard "two
            // opposing buttons on a centered axis" pattern (Left half
            // never fired because the inner branch returned true and
            // this outer flip turned it back to false).
            string desc = src.Descriptor ?? "";
            if (desc.StartsWith("Axis", System.StringComparison.Ordinal)) return raw;

            return src.Invert ? !raw : raw;
        }

        /// <summary>Evaluates a source for a bipolar axis target. Returns
        /// a float in [-1, +1]. Buttons map to ±1 (sign from Invert);
        /// unipolar sliders map to 0..+1 → -1..+1 only when not HalfAxis;
        /// otherwise they stay 0..+1 then sign-flipped via Invert.
        /// <paramref name="slotIndex"/> is required for gyro-target
        /// tuning lookups (per-(device, slot) PadSetting); pass -1 for
        /// non-slot contexts (legacy / utility callers).
        /// <para><paramref name="relativeTouchpad"/> picks between the
        /// two touchpad-source readings: <c>true</c> = per-frame delta
        /// (KBM mouse / scroll consume this), <c>false</c> = absolute
        /// pad position (touchpad-output passthrough, stick axes,
        /// extended axes all want this). Default is absolute because
        /// the relative case is the narrower one — only the KBM mouse
        /// path opts in.</para></summary>
        public static float EvaluateForBipolarAxisTarget(
            CustomInputState state, MappingSource src, int slotIndex = -1,
            bool relativeTouchpad = false)
        {
            if (state == null || src == null) return 0f;

            float raw = ReadAsBipolar(state, src, slotIndex, relativeTouchpad);
            return src.Invert ? -raw : raw;
        }

        /// <summary>Evaluates a source for a unipolar trigger target.
        /// Returns a float in [0, +1]. Bipolar axes contribute their
        /// absolute value; buttons map to 0/1; HalfAxis still respects
        /// the active half.</summary>
        public static float EvaluateForTriggerTarget(
            CustomInputState state, MappingSource src, int slotIndex = -1)
        {
            if (state == null || src == null) return 0f;

            float raw = ReadAsUnipolar(state, src, slotIndex);
            return src.Invert ? 1f - raw : raw;
        }

        /// <summary>Evaluates a source for a POV-direction target
        /// (DPadUp/Down/Left/Right). Same shape as button-target with
        /// PovDirection sources matching the descriptor's direction.</summary>
        public static bool EvaluateForPovDirectionTarget(
            CustomInputState state, MappingSource src, int globalThresholdPercent, int slotIndex = -1)
        {
            // POV-direction targets are bool; reuse the button path (which
            // already special-cases POV-direction sources via the parser).
            return EvaluateForButtonTarget(state, src, globalThresholdPercent, slotIndex);
        }

        // ─── Internal readers ──────────────────────────────────────────

        private static bool ReadAsBool(CustomInputState state, MappingSource src, int globalThresholdPercent, int slotIndex)
        {
            string s = (src.Descriptor ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return false;

            // Touchpad-gesture descriptors route through the per-tick
            // gesture engine's fire set; continuous-axis variants
            // (PinchAxis / RotateAxis) read as "fired" when their
            // magnitude exceeds the source's deadzone (engine-side
            // threshold semantics; one-shot variants ignore deadzone).
            if (IsTouchpadGestureDescriptor(s))
            {
                if (!TryParseTouchpadGesture(s, out int gPad, out string gName)) return false;
                if (IsTouchpadGestureAxis(gName))
                {
                    float axisVal = TouchpadGestureAxisProvider?.Invoke(
                        slotIndex, src.DeviceGuid ?? "", gPad, gName) ?? 0f;
                    float gThresh = src.DeadZone > 0 ? src.DeadZone / 100f : 0.5f;
                    return Math.Abs(axisVal) > gThresh;
                }
                return TouchpadGestureFiredProvider?.Invoke(
                    slotIndex, src.DeviceGuid ?? "", gPad, gName) ?? false;
            }

            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
                return ReadTouchpadBool(state, s);

            if (s.StartsWith("Midi ", StringComparison.Ordinal))
            {
                if (state.Midi == null || !TryParseMidi(s, out char mk, out int mi)) return false;
                switch (mk)
                {
                    case 'N': return state.Midi.Notes[mi];
                    // CC as a button: pressed past the source deadzone
                    // (default half-scale). Covers sustain pedals and
                    // encoder/pad CC buttons.
                    case 'C':
                        int cdz = src.DeadZone > 0 ? src.DeadZone : 50;
                        return state.Midi.Cc[mi] > (int)(127 * cdz / 100.0);
                    case 'U': return state.Midi.CcUp[mi];   // encoder CW pulse
                    case 'D': return state.Midi.CcDown[mi]; // encoder CCW pulse
                    case 'P':
                        int pdelta = state.Midi.PitchBend - MidiInputState.PitchBendCenter;
                        if (pdelta < 0) pdelta = -pdelta;
                        return pdelta > 32767 / 2;
                }
                return false;
            }

            if (s.StartsWith("Gyro ", StringComparison.Ordinal))
            {
                float tunedRate = ReadTunedGyroRate(state, src, slotIndex, out int gyroAxis, out _);
                if (gyroAxis < 0) return false;
                // Per-source DeadZone (when set) overrides the default
                // 30°/s button threshold so users can dial in sensitivity.
                // Device-level deadzone has already been applied inside
                // ReadTunedGyroRate; this knob is the button-activation
                // threshold ON TOP of that.
                float gyroThresh = src.DeadZone > 0
                    ? src.DeadZone / 100f * GyroButtonThreshold * 3f  // DeadZone% × ~90°/s headroom
                    : GyroButtonThreshold;
                return Math.Abs(tunedRate) > gyroThresh;
            }

            if (s.StartsWith("Mouse Position ", StringComparison.Ordinal))
            {
                // Cursor-to-button: fire when the normalized, sensitivity-scaled
                // cursor offset clears the per-source deadzone (or the global
                // threshold when none is set).
                float v = ReadTunedMouseCursor(src);
                int cdz = src.DeadZone > 0 ? src.DeadZone : globalThresholdPercent;
                return Math.Abs(v) > Math.Max(cdz, 1) / 100f;
            }

            if (!TryParseTypeIndex(s, out var t, out int idx, out string povDir))
                return false;

            int dz = src.DeadZone > 0 ? src.DeadZone : globalThresholdPercent;
            double thresh = Math.Max(dz, 1) / 100.0;

            switch (t)
            {
                case SourceType.Button:
                    return idx >= 0 && idx < state.Buttons.Length && state.Buttons[idx];

                case SourceType.Axis:
                    if (idx < 0 || idx >= CustomInputState.MaxAxis) return false;
                    int av = state.Axis[idx];
                    if (src.HalfAxis)
                    {
                        if (src.Bidirectional)
                        {
                            // Either side of center past deadzone counts —
                            // |av − 32768| > 32767 * thresh. Invert is
                            // irrelevant here since mirroring around center
                            // already covers both directions.
                            int delta = av - 32768;
                            if (delta < 0) delta = -delta;
                            return delta > (int)(32767 * thresh);
                        }
                        if (src.Invert)
                            return av < (int)(32767 * (1.0 - thresh));
                        return av > (int)(32768 + 32767 * thresh);
                    }
                    int hi = (int)(thresh * 65535);
                    if (src.Invert)
                        return av < 65535 - hi;
                    return av > hi;

                case SourceType.Slider:
                    if (idx < 0 || idx >= CustomInputState.MaxSliders) return false;
                    int sv = state.Sliders[idx];
                    int shi = (int)(thresh * 65535);
                    return sv > shi;

                case SourceType.PovDirection:
                    if (idx < 0 || idx >= CustomInputState.MaxPovs) return false;
                    return PovMatches(state.Povs[idx], povDir);

                default:
                    return false;
            }
        }

        private static float ReadAsBipolar(CustomInputState state, MappingSource src, int slotIndex, bool relativeTouchpad)
        {
            string s = (src.Descriptor ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return 0f;

            // Touchpad-gesture sources: continuous axes (PinchAxis,
            // RotateAxis) read their bipolar value from the gesture
            // engine's axis provider; one-shot gestures map to ±1
            // when fired (1 on the firing tick, 0 otherwise).
            if (IsTouchpadGestureDescriptor(s))
            {
                if (!TryParseTouchpadGesture(s, out int gPad, out string gName)) return 0f;
                if (IsTouchpadGestureAxis(gName))
                {
                    return TouchpadGestureAxisProvider?.Invoke(
                        slotIndex, src.DeviceGuid ?? "", gPad, gName) ?? 0f;
                }
                bool fired = TouchpadGestureFiredProvider?.Invoke(
                    slotIndex, src.DeviceGuid ?? "", gPad, gName) ?? false;
                return fired ? 1f : 0f;
            }

            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // Two readings for touchpad sources:
                //   relative — per-frame delta scaled to mouse-style
                //     bipolar, used by KBM mouse / scroll targets.
                //   absolute — raw pad position [0..1] mapped to
                //     [-1..+1], used by touchpad-output passthrough,
                //     stick axes, and extended-config axes (everything
                //     that needs "where is the finger right now," not
                //     "how far has it moved this frame").
                // Caller signals which one it wants via relativeTouchpad.
                if (relativeTouchpad)
                {
                    if (TryReadTouchpadAxis(state, src, s, slotIndex, out float bipolar)) return bipolar;
                }
                else
                {
                    if (TryReadTouchpadAxisAbsolute(state, s, out float bipolar)) return bipolar;
                }
                return ReadTouchpadBool(state, s) ? 1f : 0f;
            }

            if (s.StartsWith("Midi ", StringComparison.Ordinal))
            {
                if (state.Midi == null || !TryParseMidi(s, out char mk, out int mi)) return 0f;
                switch (mk)
                {
                    case 'N': return state.Midi.Notes[mi] ? 1f : 0f;
                    // CC 0..127 → unipolar 0..1, then mapped to bipolar
                    // [-1..+1] the same way a slider source is.
                    case 'C': return state.Midi.Cc[mi] / 127f * 2f - 1f;
                    case 'U': return state.Midi.CcUp[mi] ? 1f : 0f;   // pulse as 0/1
                    case 'D': return state.Midi.CcDown[mi] ? 1f : 0f;
                    case 'P': return Math.Max(-1f, Math.Min(1f,
                        (state.Midi.PitchBend - MidiInputState.PitchBendCenter) / 32767f));
                }
                return 0f;
            }

            if (s.StartsWith("Gyro ", StringComparison.Ordinal))
            {
                float tunedRate = ReadTunedGyroRate(state, src, slotIndex, out int gyroAxis, out var tuning);
                if (gyroAxis < 0) return 0f;
                float v = tunedRate * GyroScale;
                // Phase 2 response shaping in normalized space.
                v = ApplyOutputCurve(v, tuning.OutputCurve);
                v = ApplyGyroAcceleration(v, tuning.Acceleration);
                if (v < -1f) v = -1f;
                else if (v > 1f) v = 1f;
                return v;
            }

            if (s.StartsWith("Mouse Position ", StringComparison.Ordinal))
                return ReadTunedMouseCursor(src);

            if (!TryParseTypeIndex(s, out var t, out int idx, out string povDir))
                return 0f;

            switch (t)
            {
                case SourceType.Button:
                    return (idx >= 0 && idx < state.Buttons.Length && state.Buttons[idx]) ? 1f : 0f;

                case SourceType.Axis:
                    if (idx < 0 || idx >= CustomInputState.MaxAxis) return 0f;
                    int av = state.Axis[idx];
                    if (src.HalfAxis)
                    {
                        // Active half ranges to [0, +1].
                        if (av >= 32768)
                            return Math.Min(1f, (av - 32768) / 32767f);
                        return Math.Min(1f, (32767 - av) / 32767f);
                    }
                    return Math.Max(-1f, Math.Min(1f, (av - 32768) / 32767f));

                case SourceType.Slider:
                    if (idx < 0 || idx >= CustomInputState.MaxSliders) return 0f;
                    return Math.Max(0f, Math.Min(1f, state.Sliders[idx] / 65535f)) * 2f - 1f;

                case SourceType.PovDirection:
                    if (idx < 0 || idx >= CustomInputState.MaxPovs) return 0f;
                    return PovMatches(state.Povs[idx], povDir) ? 1f : 0f;

                default:
                    return 0f;
            }
        }

        private static float ReadAsUnipolar(CustomInputState state, MappingSource src, int slotIndex)
        {
            string s = (src.Descriptor ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return 0f;

            // Touchpad-gesture sources: continuous-axis variants use
            // the absolute value of their bipolar reading (a trigger
            // target driven by PinchAxis fires harder as the pinch
            // gets more extreme in either direction); one-shot fires
            // return 0/1.
            if (IsTouchpadGestureDescriptor(s))
            {
                if (!TryParseTouchpadGesture(s, out int gPad, out string gName)) return 0f;
                if (IsTouchpadGestureAxis(gName))
                {
                    float v = TouchpadGestureAxisProvider?.Invoke(
                        slotIndex, src.DeviceGuid ?? "", gPad, gName) ?? 0f;
                    return Math.Abs(v);
                }
                bool fired = TouchpadGestureFiredProvider?.Invoke(
                    slotIndex, src.DeviceGuid ?? "", gPad, gName) ?? false;
                return fired ? 1f : 0f;
            }

            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // Touchpad axis → unipolar: return [0..1] directly (raw finger
                // position; no bipolar centering).
                if (TryReadTouchpadAxisRaw(state, s, out float unipolar)) return unipolar;
                return ReadTouchpadBool(state, s) ? 1f : 0f;
            }

            if (s.StartsWith("Midi ", StringComparison.Ordinal))
            {
                if (state.Midi == null || !TryParseMidi(s, out char mk, out int mi)) return 0f;
                switch (mk)
                {
                    case 'N': return state.Midi.Notes[mi] ? 1f : 0f;
                    // CC 0..127 → unipolar 0..1 (a fader/expression pedal
                    // driving a trigger).
                    case 'C': return state.Midi.Cc[mi] / 127f;
                    case 'U': return state.Midi.CcUp[mi] ? 1f : 0f;
                    case 'D': return state.Midi.CcDown[mi] ? 1f : 0f;
                    case 'P': return Math.Abs(state.Midi.PitchBend - MidiInputState.PitchBendCenter) / 32767f;
                }
                return 0f;
            }

            if (s.StartsWith("Gyro ", StringComparison.Ordinal))
            {
                float tunedRate = ReadTunedGyroRate(state, src, slotIndex, out int gyroAxis, out var tuning);
                if (gyroAxis < 0) return 0f;
                float v = Math.Abs(tunedRate) * GyroScale;
                // Phase 2 response shaping in normalized space (unsigned trigger).
                v = ApplyOutputCurve(v, tuning.OutputCurve);
                v = ApplyGyroAcceleration(v, tuning.Acceleration);
                if (v > 1f) v = 1f;
                return v;
            }

            if (s.StartsWith("Mouse Position ", StringComparison.Ordinal))
                return Math.Abs(ReadTunedMouseCursor(src));

            if (!TryParseTypeIndex(s, out var t, out int idx, out string povDir))
                return 0f;

            switch (t)
            {
                case SourceType.Button:
                    return (idx >= 0 && idx < state.Buttons.Length && state.Buttons[idx]) ? 1f : 0f;

                case SourceType.Axis:
                    if (idx < 0 || idx >= CustomInputState.MaxAxis) return 0f;
                    int av = state.Axis[idx];
                    if (src.HalfAxis)
                    {
                        // Half-axis trigger: clip to the upper half. Lets a
                        // bipolar stick axis feed a trigger sensibly (rest =
                        // 0, full deflection one way = 1).
                        if (av >= 32768)
                            return Math.Min(1f, (av - 32768) / 32767f);
                        return Math.Min(1f, (32767 - av) / 32767f);
                    }
                    // Trigger axes are unipolar 0..65535 with 0 = released
                    // (matches the legacy MapToTriggerSingle clamp). Stick
                    // axes mapped to triggers without HalfAxis sit at ~50 %
                    // at rest — same as legacy; users who want a clean
                    // stick→trigger map opt in via HalfAxis.
                    return Math.Max(0f, Math.Min(1f, av / 65535f));

                case SourceType.Slider:
                    if (idx < 0 || idx >= CustomInputState.MaxSliders) return 0f;
                    return Math.Max(0f, Math.Min(1f, state.Sliders[idx] / 65535f));

                case SourceType.PovDirection:
                    if (idx < 0 || idx >= CustomInputState.MaxPovs) return 0f;
                    return PovMatches(state.Povs[idx], povDir) ? 1f : 0f;

                default:
                    return 0f;
            }
        }

        // ─── Descriptor helpers ────────────────────────────────────────

        private static bool TryParseTypeIndex(string s, out SourceType t, out int index, out string povDir)
        {
            t = SourceType.Unmapped;
            index = 0;
            povDir = null;

            string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;

            t = parts[0].ToLowerInvariant() switch
            {
                "button" => SourceType.Button,
                "axis"   => SourceType.Axis,
                "slider" => SourceType.Slider,
                "pov"    => SourceType.PovDirection,
                _        => SourceType.Unmapped,
            };
            if (t == SourceType.Unmapped) return false;
            if (!int.TryParse(parts[1], out index)) return false;
            if (t == SourceType.PovDirection && parts.Length >= 3) povDir = parts[2];
            return true;
        }

        private static bool PovMatches(int povCentidegrees, string direction)
        {
            // -1 (or any negative) signals POV centered.
            if (povCentidegrees < 0 || string.IsNullOrEmpty(direction)) return false;

            // Normalize to 0..35999.
            int v = ((povCentidegrees % 36000) + 36000) % 36000;
            return direction.ToLowerInvariant() switch
            {
                "up"    => v >= 31500 || v <= 4500,    // 315°..360°/0°..45°
                "right" => v >= 4500 && v <= 13500,    // 45°..135°
                "down"  => v >= 13500 && v <= 22500,   // 135°..225°
                "left"  => v >= 22500 && v <= 31500,   // 225°..315°
                _       => false,
            };
        }

        // ─── Touchpad bool descriptors ─────────────────────────────────

        // Mirrors the legacy InputManager.MapTouchpadButton helper so the
        // new pipeline can recognize "Touchpad N Click" / "Touchpad N
        // Finger M Down" descriptors. Kept here so SourceCoercion is
        // self-contained (Engine library has no reference back into
        // PadForge.App's InputManager).
        private static bool ReadTouchpadBool(CustomInputState state, string descriptor)
        {
            string[] parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!int.TryParse(parts[1], out int padIdx)) return false;

            // "Touchpad N Click"
            if (parts.Length == 3 && parts[2].Equals("Click", StringComparison.Ordinal))
            {
                // Canonical touchpad click rides Buttons[16] (the slot
                // SdlDeviceWrapper populates from SDL_GAMEPAD_BUTTON_TOUCHPAD,
                // matching SDL's enum position between paddles and Misc2-6).
                // Multi-touchpad devices (Steam Controller 2026) route their
                // additional clicks through the SDL3 fork patch into other
                // Buttons[] slots; that mapping lives in the device-specific
                // recipe, not here.
                if (padIdx != 0) return false;
                if (state.Buttons == null || state.Buttons.Length <= 16) return false;
                return state.Buttons[16];
            }

            // "Touchpad N Finger M Down"
            if (parts.Length == 5
                && parts[2].Equals("Finger", StringComparison.Ordinal)
                && parts[4].Equals("Down", StringComparison.Ordinal))
            {
                if (!int.TryParse(parts[3], out int fingerIdx)) return false;
                var pad = GetTouchpad(state, padIdx);
                if (pad == null) return false;
                if (fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return false;
                return pad.FingerDown[fingerIdx];
            }

            return false;
        }

        /// <summary>Returns the <see cref="TouchpadInputState"/> for the
        /// requested pad index, or <c>null</c> when the device has no
        /// touchpad or the requested pad index is out of range. Centralizes
        /// the null + bounds guards every touchpad descriptor reader needs.</summary>
        private static TouchpadInputState GetTouchpad(CustomInputState state, int padIdx)
        {
            if (state == null || state.Touchpads == null) return null;
            if (padIdx < 0 || padIdx >= state.Touchpads.Length) return null;
            return state.Touchpads[padIdx];
        }

        // ─── Touchpad axis descriptors ──────────────────────────────────
        //
        // "Touchpad N Finger M X" / "Touchpad N Finger M Y" — physical finger
        // X/Y as an axis source. Pressure variants ("Pressure") return the
        // pressure scalar where supported. Lets the touchpad output path
        // (and any future user mapping of finger position to other targets)
        // participate in multi-source rows the same way stick axes do.
        //
        // CustomInputState.TouchpadFingers layout matches the legacy passthrough
        // reader in InputManager: [F0.X, F0.Y, F0.Pressure, F1.X, F1.Y,
        // F1.Pressure]. So finger M's X index is M*3, Y index is M*3+1.

        /// <summary>Per-(deviceGuid, finger, axis) delta tracker for the
        /// touchpad bipolar reader. Touchpad X/Y feeding a bipolar target
        /// (notably KBM mouse X/Y) reads as a relative-motion delta, not as
        /// absolute pad position. The state machine here remembers the
        /// previous frame's position so the bipolar reader can return
        /// (current - previous), and seeds itself on every fresh touch-down
        /// so a re-touch doesn't generate a jump. Lifted finger collapses
        /// the entry back to "needs seeding."</summary>
        private struct TouchpadAxisDelta
        {
            public float PrevValue;
            public bool Seeded;
        }

        private static readonly ConcurrentDictionary<string, TouchpadAxisDelta> _touchpadDeltas = new();

        /// <summary>Per-frame multiplier applied to (current - previous)
        /// touchpad position to convert pad fraction into bipolar source
        /// magnitude. Calibrated to match the proven DualSenseY-v2
        /// touchpad-as-mouse model (see
        /// <c>GitHub/DualSenseY-v2/source/keyboardMouseMapper.cpp:76-102</c>):
        /// the DualSense touchpad reports raw deltas in a 1920×1080
        /// native-pixel space, and DualSenseY-v2 maps 1 native pad-pixel
        /// directly to 1 cursor pixel at sensitivity = 1.0. SDL3
        /// normalizes touchpad position to [0..1], so 1920 native pixels
        /// = 1.0 SDL units. We need
        /// <c>bipolar × KbmMouseSensitivity = native_pixel_delta</c>,
        /// where <c>KbmMouseSensitivity = 15</c>
        /// (see <c>KeyboardMouseVirtualController.cs:38</c>), giving
        /// <c>scale = 1920 / 15 ≈ 128</c>. A full horizontal pad sweep
        /// at sensitivity 1.0 moves the cursor 1920 pixels, matching a
        /// typical laptop trackpad's non-accelerated feel. Users dial
        /// further via the Touchpad tab's per-axis Mouse Sensitivity
        /// multipliers and per-row sensitivity curves.
        /// <para>Steam Controller 2026 and other touchpads with
        /// different native resolutions still feel intuitive: SDL3
        /// normalizes every pad to [0..1] before this scale applies,
        /// so the cursor-delta-per-pad-fraction is constant regardless
        /// of the source pad's native pixel resolution.</para></summary>
        private const float TouchpadDeltaScale = 128f;

        /// <summary>Returns the relative-motion delta of a touchpad finger
        /// axis as bipolar [-1..+1]. Used by ReadAsBipolar so touchpad-to-
        /// mouse mappings behave like a real trackpad (finger motion →
        /// proportional cursor motion) instead of absolute position
        /// (holding finger at edge → cursor pegged to that edge at max
        /// speed). Pressure (axisOffset == 2) bypasses delta and returns
        /// the raw [0..1] magnitude — pressure is a unipolar level,
        /// recentering it at 0.5 was nonsense.
        /// <para>Behavior:</para>
        /// <list type="bullet">
        /// <item>Finger not in contact: return 0, mark state as needs-seeding.</item>
        /// <item>First frame after touch-down: seed prev=current, return 0
        /// (no jump on re-touch).</item>
        /// <item>Subsequent frames: return (current - prev) * scale,
        /// clamped to [-1, +1], and update prev=current.</item>
        /// </list>
        /// <para>State is keyed by (DeviceGuid, fingerIdx, axisOffset).
        /// Selected by <c>ReadAsBipolar</c> only when its caller flags
        /// the target as relative-motion (KBM mouse / scroll). Absolute-
        /// position targets — touchpad-output passthrough, stick axes,
        /// extended axes — go through <c>TryReadTouchpadAxisAbsolute</c>
        /// instead.</para></summary>
        private static bool TryReadTouchpadAxis(CustomInputState state, MappingSource src, string descriptor, int slotIndex, out float bipolar)
        {
            bipolar = 0f;
            if (!TryParseTouchpadAxis(descriptor, out int padIdx, out int fingerIdx, out int axisOffset))
                return false;
            var pad = GetTouchpad(state, padIdx);
            if (pad == null) return false;
            if (fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return false;

            string deviceGuid = src?.DeviceGuid ?? string.Empty;
            string key = deviceGuid + "|" + padIdx + "|" + fingerIdx + "|" + axisOffset;

            // Lifted finger → reset delta tracker, return 0.
            if (!pad.FingerDown[fingerIdx])
            {
                _touchpadDeltas.TryRemove(key, out _);
                return true; // bipolar already 0
            }

            float raw = axisOffset switch
            {
                0 => pad.FingerX[fingerIdx],
                1 => pad.FingerY[fingerIdx],
                2 => pad.FingerPressure[fingerIdx],
                _ => 0f
            }; // [0..1]

            // Pressure is unipolar — pass it through directly (no delta,
            // no recentering) so a pressure → axis mapping reads the
            // actual pressure magnitude.
            if (axisOffset == 2)
            {
                bipolar = raw < 0f ? 0f : (raw > 1f ? 1f : raw);
                return true;
            }

            // X / Y → delta from previous frame. Seed on first contact.
            var prev = _touchpadDeltas.GetOrAdd(key, _ => new TouchpadAxisDelta { PrevValue = raw, Seeded = false });
            if (!prev.Seeded)
            {
                _touchpadDeltas[key] = new TouchpadAxisDelta { PrevValue = raw, Seeded = true };
                return true; // bipolar 0 on the seed frame
            }
            float delta = raw - prev.PrevValue;
            _touchpadDeltas[key] = new TouchpadAxisDelta { PrevValue = raw, Seeded = true };

            // Y sign: return the RAW delta in SDL convention (raw_y=0 at top,
            // so finger-DOWN → positive delta). DO NOT flip Y here. The KbmMouseY
            // and KbmScroll paths in Step 3 already NegateAxis the evaluator's
            // output — they explicitly document the contract "the evaluator
            // returns SDL convention (positive = down)" (InputManager.Step3.
            // UpdateOutputStates) — and the KBM virtual controller negates once
            // more into screen-Y. A stick → KbmMouseY source rides exactly those
            // two negations. An extra flip here made the touchpad path negate a
            // third time, so finger-up drove the cursor DOWN. X needs no negate
            // at any layer and is already correct.

            // Per-(slot, pad) mouse tuning: sensitivity multiplier per
            // axis plus optional invert. Slot-keyed so two slots sharing
            // the same physical touchpad can carry independent tuning.
            // Falls back to 1.0× / non-inverted when the provider isn't
            // wired (engine standalone tests, early startup before
            // InputService binds).
            var tpSettings = TouchpadMouseSettingsProvider?.Invoke(slotIndex, deviceGuid, padIdx);
            float sens = (axisOffset == 0)
                ? (tpSettings?.MouseSensitivityX ?? 1.0f)
                : (tpSettings?.MouseSensitivityY ?? 1.0f);
            bool invert = (axisOffset == 0)
                ? (tpSettings?.MouseInvertX ?? false)
                : (tpSettings?.MouseInvertY ?? false);
            if (invert) delta = -delta;

            bipolar = delta * TouchpadDeltaScale * sens;
            if (bipolar < -1f) bipolar = -1f;
            else if (bipolar > 1f) bipolar = 1f;
            return true;
        }

        /// <summary>Returns finger position as bipolar [-1..+1] without
        /// delta tracking. Used by ReadAsBipolar for absolute-position
        /// targets — touchpad-output passthrough, stick axes, extended
        /// axes. SDL touchpad X/Y is reported as [0..1] (top/left = 0,
        /// bottom/right = 1); this reader maps that to [-1..+1] directly
        /// so a DualSense touchpad → DualSense virtual touchpad
        /// passthrough preserves SDL's convention end-to-end. No Y flip
        /// here, and none belongs here: the per-target Y sign is applied
        /// downstream, per consumer. The stick path negates Y in
        /// <c>InputManager.WriteBipolarAxisTarget</c> (finger-up →
        /// stick-up); the touchpad→touchpad passthrough keeps SDL's top=0
        /// as-is; the KBM mouse / scroll path negates in Step 3 plus the
        /// virtual controller. A Y flip added here would corrupt ALL of
        /// them at once — keep this a faithful [0..1] → [-1..+1] pass.
        /// Pressure (axisOffset == 2) is unipolar, kept as
        /// [0..1] without recentering — pressure isn't a signed axis.
        /// Returns 0 when the finger is not in contact (the caller's
        /// gating wrapper usually filters us out first, but this is
        /// the right defensive default).</summary>
        private static bool TryReadTouchpadAxisAbsolute(CustomInputState state, string descriptor, out float bipolar)
        {
            bipolar = 0f;
            if (!TryParseTouchpadAxis(descriptor, out int padIdx, out int fingerIdx, out int axisOffset))
                return false;
            var pad = GetTouchpad(state, padIdx);
            if (pad == null) return false;
            if (fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return false;
            if (!pad.FingerDown[fingerIdx]) return true; // bipolar already 0
            float raw = axisOffset switch
            {
                0 => pad.FingerX[fingerIdx],
                1 => pad.FingerY[fingerIdx],
                2 => pad.FingerPressure[fingerIdx],
                _ => 0f
            };
            if (raw < 0f) raw = 0f; else if (raw > 1f) raw = 1f;
            bipolar = axisOffset == 2 ? raw : (raw * 2f - 1f);
            return true;
        }

        /// <summary>Returns finger position as unipolar [0..1]. Used by
        /// ReadAsUnipolar so a touchpad axis feeding a trigger target reads
        /// the raw position. Returns 0 when the finger is not in contact.</summary>
        private static bool TryReadTouchpadAxisRaw(CustomInputState state, string descriptor, out float unipolar)
        {
            unipolar = 0f;
            if (!TryParseTouchpadAxis(descriptor, out int padIdx, out int fingerIdx, out int axisOffset))
                return false;
            var pad = GetTouchpad(state, padIdx);
            if (pad == null) return false;
            if (fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return false;
            if (!pad.FingerDown[fingerIdx]) return true; // unipolar already 0
            float raw = axisOffset switch
            {
                0 => pad.FingerX[fingerIdx],
                1 => pad.FingerY[fingerIdx],
                2 => pad.FingerPressure[fingerIdx],
                _ => 0f
            };
            if (raw < 0f) raw = 0f; else if (raw > 1f) raw = 1f;
            unipolar = raw;
            return true;
        }

        /// <summary>Parses "Touchpad N Finger M X" / "...Y" / "...Pressure".
        /// <paramref name="axisOffset"/> = 0 for X, 1 for Y, 2 for Pressure.
        /// Returns false for "Click" / "Down" / unrecognized formats.</summary>
        private static bool TryParseTouchpadAxis(string descriptor,
            out int padIdx, out int fingerIdx, out int axisOffset)
        {
            padIdx = 0; fingerIdx = 0; axisOffset = -1;
            string[] parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // Expected: "Touchpad N Finger M X|Y|Pressure" — 5 parts.
            if (parts.Length != 5) return false;
            if (!parts[0].Equals("Touchpad", StringComparison.Ordinal)) return false;
            if (!int.TryParse(parts[1], out padIdx)) return false;
            if (!parts[2].Equals("Finger", StringComparison.Ordinal)) return false;
            if (!int.TryParse(parts[3], out fingerIdx)) return false;
            axisOffset = parts[4] switch
            {
                "X"        => 0,
                "Y"        => 1,
                "Pressure" => 2,
                _          => -1,
            };
            return axisOffset >= 0;
        }
    }
}
