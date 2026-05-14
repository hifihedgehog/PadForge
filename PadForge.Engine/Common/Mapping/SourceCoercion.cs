using System;
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
        /// per-device at-rest gyro bias without taking a UserDevice
        /// reference (the Engine library is self-contained). The App
        /// layer wires this provider at startup from UserDevices.
        /// Returns the three-axis bias tuple for the given device GUID
        /// string, or zero for unknown / uncalibrated devices. NOTE:
        /// the per-source <c>Invert</c> toggle handles user-perception
        /// direction inversion — do NOT apply any cemuhook-style
        /// (-gx, gy, -gz) flip here. Those flips live exclusively in
        /// the DSU / MotionSnapshot aggregation path and would silently
        /// break user expectations if synced.</summary>
        public static Func<string, (float pitch, float yaw, float roll)> GyroBiasProvider { get; set; }

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
            public float SmoothingAlpha;
            public float Acceleration;
            public string OutputCurve;
            public float EasyAimStickThreshold01;
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

        private static GyroTuning GetGyroTuning(string deviceGuid, int slotIndex)
        {
            var provider = GyroTuningProvider;
            if (provider == null || string.IsNullOrEmpty(deviceGuid))
                return new GyroTuning { SensH = 1f, SensV = 1f, OutputCurve = "Linear" };
            return provider(deviceGuid, slotIndex);
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
                return SourceType.TouchpadButton;
            if (s.StartsWith("Gyro ", StringComparison.Ordinal))
                return SourceType.Gyro;

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
        /// descriptors. Public so SourceEvaluator can decide between
        /// rate-direct coercion (mouse / scroll targets) and rate-
        /// additive integration (virtual stick targets) without
        /// re-parsing.</summary>
        public static bool IsGyroDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && descriptor.StartsWith("Gyro ", StringComparison.Ordinal);

        /// <summary>Public form of <see cref="ReadCalibratedGyroRate"/>:
        /// returns the bias-subtracted gyro rate (rad/s) for the source's
        /// descriptor on the given state, or 0 for non-gyro descriptors /
        /// unknown axes / null state.Gyro.</summary>
        public static float GetCalibratedGyroRate(CustomInputState state, MappingSource src)
        {
            if (src == null) return 0f;
            int axis = ParseGyroAxisIndex(src.Descriptor);
            if (axis < 0) return 0f;
            return ReadCalibratedGyroRate(state, axis, src.DeviceGuid);
        }

        /// <summary>Public access to the full per-(device, slot) gyro
        /// tuning chain — bias, smoothing, deadzone, H/V sensitivity,
        /// per-source sensitivity, Easy Aim gating. Returns the tuned
        /// rate in rad/s (pre-scale, pre-curve, pre-acceleration). Used
        /// by the gyro→stick integrator path so that path picks up the
        /// same tuning as the mouse/scroll bipolar/unipolar readers.
        /// <paramref name="tuning"/> exits with the resolved tuning
        /// bundle so callers can apply curve + acceleration to the
        /// post-integration normalized value.</summary>
        public static float GetTunedGyroRate(CustomInputState state, MappingSource src, int slotIndex, out GyroTuning tuning)
        {
            tuning = default;
            return ReadTunedGyroRate(state, src, slotIndex, out _, out tuning);
        }

        /// <summary>Applies the Phase 2 output curve + acceleration to a
        /// normalized [-1..+1] value. Composes the same way as the
        /// in-line application inside <c>ReadAsBipolar</c>, but exposed
        /// for the gyro→stick integrator path to call after its
        /// integration step.</summary>
        public static float ShapeGyroNormalized(float normalized, GyroTuning tuning)
        {
            float v = ApplyOutputCurve(normalized, tuning.OutputCurve);
            v = ApplyGyroAcceleration(v, tuning.Acceleration);
            if (v < -1f) v = -1f;
            else if (v > 1f) v = 1f;
            return v;
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

            // Easy Aim — gate gyro on right-stick deflection past the
            // configured threshold. Threshold 0 = always-on (default).
            // Slot must be valid; otherwise pass through unconditionally.
            tuning = GetGyroTuning(src.DeviceGuid, slotIndex);
            if (tuning.EasyAimStickThreshold01 > 0f && slotIndex >= 0)
            {
                float defl = SlotRightStickDeflectionProvider?.Invoke(slotIndex) ?? 1f;
                if (defl < tuning.EasyAimStickThreshold01)
                {
                    gyroAxis = ParseGyroAxisIndex(src.Descriptor);
                    if (IsHorizontalBlendDescriptor(src.Descriptor)) gyroAxis = 1;
                    return 0f;
                }
            }

            // Gyro Horizontal — auto-blend of yaw + roll for grip-style-
            // agnostic horizontal aim. Read both axes, smooth + deadzone
            // each independently, pick the larger absolute, return its
            // signed value. Same H sensitivity multiplier as plain Yaw/Roll.
            if (IsHorizontalBlendDescriptor(src.Descriptor))
            {
                float yaw  = ProcessSingleAxis(state, src, 1, tuning); // Yaw
                float roll = ProcessSingleAxis(state, src, 2, tuning); // Roll
                gyroAxis = (Math.Abs(yaw) >= Math.Abs(roll)) ? 1 : 2;
                return gyroAxis == 1 ? yaw : roll;
            }

            gyroAxis = ParseGyroAxisIndex(src.Descriptor);
            if (gyroAxis < 0) return 0f;
            return ProcessSingleAxis(state, src, gyroAxis, tuning);
        }

        /// <summary>Shared per-axis processing chain: bias subtract,
        /// smoothing, deadzone, axis sensitivity, per-source multiplier.
        /// Returns the tuned rate in rad/s (pre-scale, pre-curve).</summary>
        private static float ProcessSingleAxis(CustomInputState state, MappingSource src, int axis, GyroTuning tuning)
        {
            float rate = ReadCalibratedGyroRate(state, axis, src.DeviceGuid);

            // Smoothing: single-pole EMA on the bias-subtracted rate.
            rate = ApplyGyroSmoothing(src.DeviceGuid, axis, rate, tuning.SmoothingAlpha);

            // Subtract-style deadzone.
            float deadzone = tuning.DeadZoneRadPerSec;
            if (deadzone > 0f)
            {
                if (rate > deadzone) rate -= deadzone;
                else if (rate < -deadzone) rate += deadzone;
                else rate = 0f;
            }

            // Axis sensitivity: Yaw (1) and Roll (2) take H; Pitch (0) takes V.
            float axisSens = axis == 0 ? tuning.SensV : tuning.SensH;
            float perSourceSens = (float)(src.GyroSensitivity > 0 ? src.GyroSensitivity : 1.0);
            return rate * axisSens * perSourceSens;
        }

        /// <summary>Reads <c>state.Gyro[gyroAxis]</c> minus the device's
        /// at-rest bias (looked up via <see cref="GyroBiasProvider"/>).
        /// Returns 0 when the device has no calibration entry — caller
        /// gets the raw reading minus zero, which is the right default
        /// for "uncalibrated yet, just connected." Defensive against
        /// null state.Gyro[].</summary>
        private static float ReadCalibratedGyroRate(CustomInputState state, int gyroAxis, string deviceGuid)
        {
            if (state == null || state.Gyro == null) return 0f;
            if (gyroAxis < 0 || gyroAxis >= state.Gyro.Length) return 0f;
            float raw = state.Gyro[gyroAxis];
            var provider = GyroBiasProvider;
            if (provider == null || string.IsNullOrEmpty(deviceGuid)) return raw;
            var bias = provider(deviceGuid);
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
        /// non-slot contexts (legacy / utility callers).</summary>
        public static float EvaluateForBipolarAxisTarget(
            CustomInputState state, MappingSource src, int slotIndex = -1)
        {
            if (state == null || src == null) return 0f;

            float raw = ReadAsBipolar(state, src, slotIndex);
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

            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
                return ReadTouchpadBool(state, s);

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

        private static float ReadAsBipolar(CustomInputState state, MappingSource src, int slotIndex)
        {
            string s = (src.Descriptor ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return 0f;

            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // "Touchpad N Finger M X" / "...Y" — physical finger position
                // as a bipolar axis: [0..1] mapped to [-1..+1] (left/top = -1,
                // center = 0, right/bottom = +1). Lets passthrough sources
                // participate in multi-source rows the same way stick axes do.
                if (TryReadTouchpadAxis(state, s, out float bipolar)) return bipolar;
                return ReadTouchpadBool(state, s) ? 1f : 0f;
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

            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // Touchpad axis → unipolar: return [0..1] directly (raw finger
                // position; no bipolar centering).
                if (TryReadTouchpadAxisRaw(state, s, out float unipolar)) return unipolar;
                return ReadTouchpadBool(state, s) ? 1f : 0f;
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
                // Single-touchpad model: only Touchpad 0 has a Click bool today.
                return padIdx == 0 && state.TouchpadClick;
            }

            // "Touchpad N Finger M Down"
            if (parts.Length == 5
                && parts[2].Equals("Finger", StringComparison.Ordinal)
                && parts[4].Equals("Down", StringComparison.Ordinal))
            {
                if (!int.TryParse(parts[3], out int fingerIdx)) return false;
                if (padIdx != 0) return false; // single touchpad supported today
                if (state.TouchpadDown == null) return false;
                if (fingerIdx < 0 || fingerIdx >= state.TouchpadDown.Length) return false;
                return state.TouchpadDown[fingerIdx];
            }

            return false;
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

        /// <summary>Returns finger position as bipolar [-1..+1] (center = 0).
        /// Used by ReadAsBipolar so touchpad-passthrough sources combine with
        /// stick / button sources in the same multi-source row.</summary>
        private static bool TryReadTouchpadAxis(CustomInputState state, string descriptor, out float bipolar)
        {
            bipolar = 0f;
            if (!TryParseTouchpadAxis(descriptor, out int padIdx, out int fingerIdx, out int axisOffset))
                return false;
            if (padIdx != 0) return false; // single touchpad supported today
            if (state.TouchpadFingers == null) return false;
            int idx = fingerIdx * 3 + axisOffset;
            if (idx < 0 || idx >= state.TouchpadFingers.Length) return false;
            float raw = state.TouchpadFingers[idx]; // [0..1]
            bipolar = (raw - 0.5f) * 2f;            // → [-1..+1]
            if (bipolar < -1f) bipolar = -1f;
            else if (bipolar > 1f) bipolar = 1f;
            return true;
        }

        /// <summary>Returns finger position as unipolar [0..1]. Used by
        /// ReadAsUnipolar so a touchpad axis feeding a trigger target reads
        /// the raw position.</summary>
        private static bool TryReadTouchpadAxisRaw(CustomInputState state, string descriptor, out float unipolar)
        {
            unipolar = 0f;
            if (!TryParseTouchpadAxis(descriptor, out int padIdx, out int fingerIdx, out int axisOffset))
                return false;
            if (padIdx != 0) return false;
            if (state.TouchpadFingers == null) return false;
            int idx = fingerIdx * 3 + axisOffset;
            if (idx < 0 || idx >= state.TouchpadFingers.Length) return false;
            float raw = state.TouchpadFingers[idx];
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
