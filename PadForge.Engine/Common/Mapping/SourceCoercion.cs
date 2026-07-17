using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            IrPointer,       // "IR Pointer X" / "IR Pointer Y" (issue #146).
                             // Wii Remote IR-camera pointer, normalized to
                             // [-1..+1] per screen axis from the two sensor-bar
                             // dots. Read PER DEVICE from CustomInputState.Ir, so
                             // two remotes keep separate pointers.
            BalanceBoard,    // "Balance Total Weight" / "Balance Lean X" /
                             // "Balance Lean Y" (issue #146). Derived from the Wii
                             // Balance Board's four corner load cells (carried on
                             // the gamepad stick axes) plus the per-board kg
                             // calibration. Weight is unipolar [0..1]; lean is
                             // bipolar [-1..+1] center-of-gravity offset.
            JoyConIr,        // "IR Brightness" (issue #151). Right Joy-Con NIR
                             // camera average intensity, unipolar [0..1], read
                             // PER DEVICE from CustomInputState.JoyConIrIntensity.
                             // Covered sensor = bright = 1; uncovered = dark = 0,
                             // so it works as a cover button or proximity trigger.
            JoyCon2Mouse,    // "Mouse Motion X" / "Mouse Motion Y" (issue #154).
                             // Joy-Con 2 optical mouse sensor motion, bipolar
                             // [-1..+1] per-poll velocity, read PER DEVICE from
                             // CustomInputState.JoyCon2MouseDX/DY. Scaled to
                             // match a real mouse's motion axes (SdlMouseWrapper
                             // MotionScale) so both feel identical in the grid.
            MouseGesture,    // "Mouse Gesture Left/Right/Up/Down/Click" (issue
                             // #200). One-shot pulses from the hold-button-and-
                             // flick recognizer, read through
                             // MouseGestureFiredProvider like the touchpad
                             // gesture family.
            IrOffscreen,     // "IR Offscreen" (issue #203). Debounced
                             // NOT-Ir.Detected per device (the lightgun-reload
                             // mechanic); bool-natured, keyed by device GUID.
            FlickStick,      // "Flick Stick Right" / "Flick Stick Left" (#225).
                             // Whole-stick flick-stick read on a KbmMouseX row:
                             // Step 3 routes it to the exact-counts mouse lane
                             // via SourceKindRuntime.TickFlickStick instead of
                             // this enum's scalar coercion path (any other
                             // target reads it as 0). Leading 'F' keeps it
                             // clear of the I/H prefix grammar.
            TouchpadPointer, // "Touchpad N Pointer X/Y[ Left|Right]" (#9 B-15).
                             // ABSOLUTE finger-0 position on the pad (or on a
                             // region-windowed half), bipolar [-1..+1] where
                             // -1 = pad left/top edge. On a KbmMouseX/Y row
                             // Step 3 routes it to the absolute cursor channel
                             // (KbmRawState.MouseAbs*) exactly like the Wii
                             // "IR Pointer" family; no finger in the window
                             // reads 0 and the validity gate freezes the
                             // cursor. Leading 'T' keeps it clear of the I/H
                             // prefix grammar.
            MenuItem,        // "Menu {menuId} Item {k}" (#9 B-17 radial /
                             // touch menus). Fires while the menu runtime
                             // asserts the item (hold-shaped fire types) or
                             // within a commit's pulse window (one-shot fire
                             // types), read through MenuItemFiredProvider
                             // exactly like the touchpad-gesture family.
                             // Leading 'M' keeps it clear of the I/H prefix
                             // grammar.
            StickRing,       // "Gamepad LeftStickRing" / "Gamepad
                             // RightStickRing" (translator v17, Steam's
                             // joystick Outer Ring Binding). Deflection
                             // MAGNITUDE of the stick pair,
                             // sqrt(x*x + y*y) clamped to [0..1], read from
                             // the same canonical axis pair flick stick
                             // resolves (Left = Axis 0/1, Right = Axis 3/4).
                             // Bool read: outer = magnitude at or past the
                             // radius (per-source DeadZone percent), inner
                             // (Invert) = deflected but inside the radius.
                             // Lives in the "Gamepad " abstract namespace so
                             // the picker's any-device group and the
                             // gamepad-capability gate cover it. Leading 'G'
                             // keeps it clear of the I/H prefix grammar.
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
            // Which stick the Easy-Aim threshold gates on (issue #120):
            // "Right" (default), "Left", or "Either" (the larger of the
            // two deflections). Empty / unrecognized reads as "Right" for
            // back-compat with profiles saved before the selector existed.
            public string EasyAimStickSide;
            // Which component of the selected stick(s) the threshold gates
            // on (issue #120): "Full" (default, radial max(|x|,|y|), the
            // legacy behavior), "X"/"Y" (full horizontal/vertical), or
            // "XNeg"/"XPos"/"YNeg"/"YPos" (single direction: left/right/
            // down/up). Empty / unrecognized reads as "Full".
            public string EasyAimStickDirection;

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

        /// <summary>Reads the slot's stick as signed (x, y) in -1..+1 so
        /// Easy Aim can gate on radial magnitude OR one direction (issue
        /// #120). App wires this against the slot's PRE-DEADZONE mapped
        /// thumbs (per-device RawMappedState, combined per axis), so the
        /// engage threshold can sit BELOW the stick's own deadzone and a
        /// micro-deflection activates gyro without moving the camera (the
        /// requester's dual-deadzone QoL; Steam gates on physical deflection
        /// the same way). XInput frame: x&gt;0 = right, x&lt;0 = left,
        /// y&gt;0 = up, y&lt;0 = down. Returns (0, 0) when slot is empty /
        /// state unavailable. The bool argument selects the stick:
        /// true = left, false = right.</summary>
        public static Func<int, bool, (float x, float y)> SlotStickDeflectionProvider { get; set; }

        /// <summary>Reduces a stick's signed (x, y) to the 0..1 deflection
        /// the Easy-Aim threshold compares against, per the direction gate
        /// (issue #120). "Full" (default / empty / unrecognized) = radial
        /// max(|x|,|y|), matching the legacy gate. "X"/"Y" = full-axis
        /// magnitude. "XNeg"/"XPos"/"YNeg"/"YPos" = one direction, clamped
        /// to 0 so the opposite push never gates (x&gt;0 right, y&gt;0 up).</summary>
        private static float ApplyDirectionGate(float x, float y, string direction)
        {
            switch (direction)
            {
                case "X":    return x < 0 ? -x : x;
                case "Y":    return y < 0 ? -y : y;
                case "XNeg": return x < 0 ? -x : 0f;
                case "XPos": return x > 0 ?  x : 0f;
                case "YNeg": return y < 0 ? -y : 0f;
                case "YPos": return y > 0 ?  y : 0f;
                default:
                    // "Full" (default) is the original radial magnitude.
                    float ax = x < 0 ? -x : x;
                    float ay = y < 0 ? -y : y;
                    return ax > ay ? ax : ay;
            }
        }

        /// <summary>Resolves the Easy-Aim gating deflection (0..1) for the
        /// configured stick side and direction. "Left" / "Either" read
        /// accordingly; anything else (including "Right", empty, or null)
        /// reads the right stick so profiles saved before the selector
        /// existed keep their original right-stick behavior. "Either"
        /// applies the direction gate to each stick independently, then
        /// takes the larger. Returns 1f (gate fully open) when the provider
        /// is unwired, matching the prior <c>?? 1f</c> fallback.</summary>
        private static float ResolveStickDeflection(int slotIndex, string side, string direction)
        {
            var p = SlotStickDeflectionProvider;
            if (p == null) return 1f;
            if (string.Equals(side, "Either", StringComparison.OrdinalIgnoreCase))
            {
                var (lx, ly) = p(slotIndex, true);
                var (rx, ry) = p(slotIndex, false);
                float l = ApplyDirectionGate(lx, ly, direction);
                float r = ApplyDirectionGate(rx, ry, direction);
                return l > r ? l : r;
            }
            bool isLeft = string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase);
            var (x, y) = p(slotIndex, isLeft);
            return ApplyDirectionGate(x, y, direction);
        }

        /// <summary>— per-device gravity vector estimator. The app
        /// layer low-pass-filters <c>state.Accel[]</c> per device and
        /// exposes the smoothed result here. Returns the gravity-aligned
        /// vector in the controller's local frame. Used by Player
        /// Space / World Space gyro projection (slot-specific framing
        /// is applied downstream by the per-slot GyroTuning that
        /// consumes this vector). App returns <c>(0, 0, -1)</c> (flat,
        /// face-up) for unknown devices.</summary>
        public static Func<string, (float gx, float gy, float gz)> GravityProvider { get; set; }

        /// <summary>Twin of <see cref="GravityProvider"/> for the auxiliary
        /// (left-side) accelerometer (issue #199): the Nunchuk's own sensor on
        /// a Nunchuk-attached Wii Remote, or the left half of a combined
        /// Joy-Con pair. Smoothed over <c>CustomInputState.AccelAux</c>. Only
        /// the "Motion Lean L" family reads it; the gyro Player/World space
        /// projections stay on the primary gravity (the primary gyro lives on
        /// the same body as the primary accel).</summary>
        public static Func<string, (float gx, float gy, float gz)> GravityProviderAux { get; set; }

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

        /// <summary>Per-Wii-Balance-Board kg calibration (issue #146), keyed by the
        /// device GUID string. Returns a 12-float array laid out as four corners
        /// (TopLeft, BottomLeft, TopRight, BottomRight) × three reference points
        /// (Kg0, Kg17, Kg34), each the raw load-cell reading at 0/17/34 kg, or null
        /// when the board has not reported its calibration yet. The App layer reads
        /// the SDL "SDL.joystick.wii.balance_board_calibration" hex property once
        /// and parses it here. <see cref="ReadTunedBalanceBoard"/> interpolates each
        /// raw corner to kg through this; without it, lean still works (a pure
        /// ratio) but Total Weight reports raw-proportional, not kg.</summary>
        public static Func<string, float[]> BalanceCalibrationProvider { get; set; }

        /// <summary>Per-board tare offset in kg (issue #146), keyed by device GUID.
        /// Subtracted from Total Weight so the user can zero the board with a stool
        /// or shoes already on it. Returns 0 when untared.</summary>
        public static Func<string, float> BalanceTareKgProvider { get; set; }

        /// <summary>Per-(device, slot) Wii IR pointer tuning (issue #146 Pointer
        /// tab), keyed by device GUID + VC slot so two virtual controllers
        /// sharing one remote keep independent pointer feel (stored on
        /// PadSetting like every other pad-page tunable). Returns the normalized
        /// vertical offset that compensates for the sensor bar sitting above or
        /// below the screen, and the 0..1 smoothing factor that low-passes the
        /// jittery camera. Grounded in Touchmote ScreenPositionCalculator.cs
        /// (the sensor-bar offsetY at 162-171 and the position smoothingBuffer).
        /// Returns (0, 0) when unset, i.e. no offset and no smoothing.</summary>
        public static Func<string, int, (float barOffset, float smoothing)> IrTuningProvider { get; set; }

        /// <summary>Per-(device, slot) Wii pointer mode (issue #203), same
        /// lookup shape as <see cref="IrTuningProvider"/>. Mode ints: 0 =
        /// Mouse (absolute, default), 1 = FpsMouse (velocity), 2 = Mouse43,
        /// 3 = Mouse169 (aspect border modes). fpsSpeed is the FPS Mouse
        /// speed knob (pixels per 10 ms at full deflection, lineage default
        /// 35). Returns (0, 35) when unset. Consumed by Step 3's KBM
        /// pointer path, never by the mapping sources, which read the raw
        /// pointer regardless of mode.</summary>
        public static Func<string, int, (int mode, float fpsSpeed)> IrPointerModeProvider { get; set; }

        // ── "IR Offscreen" (issue #203) ─────────────────
        // Debounced NOT-Ir.Detected, per device, so pointing away from the
        // sensor bar can drive a mapping or a shift-layer activator (the
        // lightgun-reload mechanic). The on-delay keeps single dropped
        // frames and the wrapper's no-report-yet guard from flickering a
        // whole button layer; coming back on-screen clears instantly. The
        // lineage's only temporal handling is fpsmouse's 1000/125 ms hold,
        // so the 150 ms here is a detection-layer debounce by design, not a
        // lineage constant. Keyed by device GUID: offscreen is physical,
        // one answer per remote no matter how many slots consume it.
        internal const int IrOffscreenDebounceMs = 150;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long>
            _irLastDetectedMs = new();

        /// <summary>The debounce decision, shared with the unit tests (the
        /// HoldEngaged idiom): detected refreshes the timestamp and reads
        /// on-screen; lost reads offscreen only after
        /// <paramref name="debounceMs"/> of sustained loss. A device that
        /// has never seen the bar (lastDetectedMs 0) reads offscreen
        /// immediately, matching the lineage's OutOfReach-starts-true.</summary>
        internal static bool ComputeIrOffscreen(bool detected, ref long lastDetectedMs,
            long nowMs, int debounceMs)
        {
            if (detected)
            {
                lastDetectedMs = nowMs;
                return false;
            }
            if (lastDetectedMs == 0) return true;
            return (nowMs - lastDetectedMs) >= debounceMs;
        }

        private static bool ReadIrOffscreen(CustomInputState state, MappingSource src, string deviceGuid)
        {
            string dev = deviceGuid ?? "";
            long last = _irLastDetectedMs.TryGetValue(dev, out var v) ? v : 0;
            bool off = ComputeIrOffscreen(state.Ir.Detected, ref last,
                Environment.TickCount64, IrOffscreenDebounceMs);
            _irLastDetectedMs[dev] = last;
            return off;
        }

        // Per-(device, slot, axis) EMA state for the IR pointer smoothing. The
        // smoothing must live at the slot-scoped read (not the per-device
        // wrapper) so each virtual controller's Pointer-tab setting applies
        // independently. Entries are removed on sight loss so a re-acquire
        // snaps instead of sliding in from stale, and the population is
        // bounded by (devices x slots x 2 axes). Seq gates the EMA to one
        // step per poll: a second row reading the same pointer axis (e.g.
        // an axis target plus a button-threshold target) re-serves the
        // frame's smoothed value instead of advancing the filter again.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            (string Dev, int Slot, char Axis), (float Value, ulong Seq)> _irEmaPrev = new();

        /// <summary>Full-scale weight (kg) that maps Total Weight to 1.0. A normal
        /// adult stays well under this, so the normalized source keeps useful
        /// resolution.</summary>
        public const float BalanceMaxKg = 150f;

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

        // ── Poll-frame gate for the smoothing / delta caches ──
        // Every cache in this class that carries state ACROSS polls
        // (smoothing rings, EMAs, previous-position stores) must advance
        // exactly once per poll, no matter how many mapping rows read the
        // same source. The polling loop calls BeginPollFrame() once per
        // tick; each cache compares its stored sequence and re-serves the
        // frame's value on repeat reads. Without this, every extra row
        // advanced the shared state again: two gyro rows halved the
        // smoothing window the Gyro tab promised (the passthrough path's
        // "pt" key suffix guarded exactly this collision against the
        // mapping path, but mapping rows collided with each other), and a
        // second relative-touchpad row consumed the first one's delta.
        // Polling thread only, like the caches it gates.
        private static ulong _pollFrameSeq = 1; // starts at 1 so "seq 0" means never-seen
        public static void BeginPollFrame() => _pollFrameSeq++;

        // dual-threshold gyro smoothing buffer. Keyed by
        // (deviceGuid, slotIndex). Single-threaded (polling thread only).
        private static readonly Dictionary<(string, int), (float x, float y)[]> _gyroSampleBuffers = new();
        private static readonly Dictionary<(string, int), int> _gyroSampleHeads = new();
        private static readonly Dictionary<(string, int), ulong> _gyroSampleFrames = new();

        // Internal for the poll-frame-gate test pins (PadForge.Tests).
        internal static (float, float) ApplyDualThresholdSmoothing(
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
            // Advance the ring once per poll, not once per mapping row: the
            // buffer is sized as window-seconds x polls, so a second row
            // pushing the same device sample again halved the wall-clock
            // span the window covered. Repeat reads in the same poll refresh
            // the head slot in place (their smooth weighting may differ,
            // e.g. a roll-source row) without advancing.
            bool firstThisPoll = !_gyroSampleFrames.TryGetValue(key, out ulong seenSeq)
                || seenSeq != _pollFrameSeq;
            int head = _gyroSampleHeads[key];
            if (firstThisPoll)
            {
                _gyroSampleFrames[key] = _pollFrameSeq;
                head = (head + 1) % N;
                _gyroSampleHeads[key] = head;
            }
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

        // Per-(device, slot) EMA smoothing state for gyro rates.
        // Single-threaded (polling thread is the only reader/writer for
        // binding-layer gyro reads); a stale read post-recalibration
        // self-heals in 1/(1-α) frames so no explicit clear is required.
        // Keyed by device AND slot because SmoothingAlpha is a per-(device,
        // slot) PadSetting: one device mapped to two slots with different
        // alphas must not share (and double-advance) one EMA state.
        private sealed class GyroEmaState
        {
            public readonly float[] Values = new float[3];
            public readonly ulong[] Seq = new ulong[3];
        }
        private static readonly Dictionary<string, GyroEmaState> _gyroSmoothingState = new();

        /// <summary>Zeroes every accumulated gyro-rate reference held for a
        /// slot (the GyroRecenter macro action, issue #9 wave 1b): the
        /// dual-threshold smoothing rings and the per-axis EMA histories for
        /// every (device, slot) pair on the slot. Both caches are polling-
        /// thread-only, so callers MUST be on the polling thread (the macro
        /// evaluator is). Cleared entries rebuild lazily from the next
        /// sample, exactly like a fresh (device, slot) pair.</summary>
        public static void ResetGyroAimStateForSlot(int slotIndex)
        {
            List<(string, int)> deadRings = null;
            foreach (var k in _gyroSampleBuffers.Keys)
                if (k.Item2 == slotIndex) (deadRings ??= new()).Add(k);
            if (deadRings != null)
            {
                foreach (var k in deadRings)
                {
                    _gyroSampleBuffers.Remove(k);
                    _gyroSampleHeads.Remove(k);
                    _gyroSampleFrames.Remove(k);
                }
            }

            // EMA keys are "deviceGuid|slotIndex"; parse the tail rather than
            // suffix-match ("|1" would also match slot 11).
            List<string> deadEma = null;
            foreach (var k in _gyroSmoothingState.Keys)
            {
                int bar = k.LastIndexOf('|');
                if (bar >= 0 && int.TryParse(k.AsSpan(bar + 1), out int keySlot) && keySlot == slotIndex)
                    (deadEma ??= new()).Add(k);
            }
            if (deadEma != null)
                foreach (var k in deadEma) _gyroSmoothingState.Remove(k);
        }

        // Internal for the poll-frame-gate test pins (PadForge.Tests).
        internal static float ApplyGyroSmoothing(string deviceGuid, int slotIndex, int axis, float rawRate, float alpha)
        {
            if (alpha <= 0f) return rawRate;
            if (alpha > 0.99f) alpha = 0.99f; // pinning at 1 freezes the output
            string key = (deviceGuid ?? "") + "|" + slotIndex;
            if (!_gyroSmoothingState.TryGetValue(key, out var st))
            {
                st = new GyroEmaState();
                _gyroSmoothingState[key] = st;
            }
            if (axis < 0 || axis >= st.Values.Length) return rawRate;
            // One EMA step per poll per axis: a second mapping row reading
            // the same axis in the same poll gets the already-smoothed value
            // instead of advancing the filter again (which effectively
            // squared alpha and weakened smoothing per extra row).
            if (st.Seq[axis] == _pollFrameSeq) return st.Values[axis];
            st.Seq[axis] = _pollFrameSeq;
            st.Values[axis] = st.Values[axis] * alpha + rawRate * (1f - alpha);
            return st.Values[axis];
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

        /// <summary>Per-source response-curve / outer-range shaping
        /// (translator v11, the Workshop channel for Steam's curve cluster).
        /// Order is outer range FIRST, then exponent: Steam remaps the
        /// deflection so full output lands at the outer radius, then shapes
        /// the remapped 0..1 value. Sign is extracted once and re-applied to
        /// the shaped magnitude; a second sign operation here would recreate
        /// the multi-layer sign bug. Both params 0 (the serialized default)
        /// returns the input unchanged, as does exponent 1 with no outer.</summary>
        private static float ApplyCurveRangeShaping(float v, MappingSource src)
        {
            double outer = src.ParamRangeOuter;
            double exponent = src.ParamCurveExponent;
            bool hasOuter = outer > 0.0 && outer < 1.0;
            bool hasCurve = exponent > 0.0 && exponent != 1.0;
            if (!hasOuter && !hasCurve) return v;
            float mag = v < 0f ? -v : v;
            if (mag <= 0f) return 0f;
            if (hasOuter) mag = Math.Min(1f, mag / (float)outer);
            if (hasCurve) mag = (float)Math.Pow(mag, exponent);
            return v < 0f ? -mag : mag;
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

            // Fold an abstract "Gamepad ..." alias to its canonical form so
            // it classifies as the type it resolves to (Button / Axis / POV).
            string s = CanonicalDescriptor(descriptor);
            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // "Touchpad N ..." can be a touchpad-button (Click /
                // Finger M Down), a touchpad-finger axis (Finger M X /
                // Y / Pressure), the absolute pointer (Pointer X/Y,
                // #9 B-15), or a touchpad-gesture. Disambiguate by
                // the third token: anything that isn't "Click",
                // "Finger", or "Pointer" is a gesture name.
                // Touchpad-finger axes fall
                // through TouchpadButton classification today since the
                // axis readers special-case them by descriptor pattern
                // rather than enum tag.
                var tpParts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tpParts.Length >= 3
                    && tpParts[2].Equals("Pointer", StringComparison.Ordinal))
                    return SourceType.TouchpadPointer;
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
            if (s.StartsWith("Mouse Motion ", StringComparison.Ordinal))
                return SourceType.JoyCon2Mouse;
            if (s.StartsWith("Mouse Gesture ", StringComparison.Ordinal))
                return SourceType.MouseGesture;
            if (s.StartsWith("IR Pointer ", StringComparison.Ordinal))
                return SourceType.IrPointer;
            if (s.Equals("IR Offscreen", StringComparison.Ordinal))
                return SourceType.IrOffscreen;
            if (s.Equals("IR Brightness", StringComparison.Ordinal))
                return SourceType.JoyConIr;
            if (s.StartsWith("Balance ", StringComparison.Ordinal))
                return SourceType.BalanceBoard;
            if (s.StartsWith("Midi ", StringComparison.Ordinal))
                return SourceType.Midi;
            if (IsFlickStickDescriptor(s))
                return SourceType.FlickStick;
            if (IsStickRingDescriptor(s))
                return SourceType.StickRing;
            if (IsMenuItemDescriptor(s))
                return SourceType.MenuItem;

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

        // ─── Abstract "Gamepad ..." descriptor family (issue #9) ───────────
        //
        // A device-agnostic namespace that resolves through SDL's gamepad
        // API to whichever physical controller feeds the slot. The wrapper
        // (SdlDeviceWrapper.GetGamepadState) already normalizes any
        // recognized pad into the canonical CustomInputState layout. Face
        // buttons land at Buttons[0..10], paddles at 12..15, sticks/triggers
        // at Axis[0..5], the D-pad synthesized onto Povs[0]. So the whole
        // family is a THIN ALIAS layer: each "Gamepad <Name>" descriptor
        // maps 1:1 onto the existing per-device canonical descriptor and
        // rides the same coercion path with zero duplicated read logic
        // (the design the #9 plan calls for, where "gyro aliases route to
        // the existing coercion"). Gyro and touchpad members of the family use
        // the existing "Gyro ..." / "Touchpad ..." descriptors directly:
        // those already resolve per-device through the gamepad API and need
        // no rename. The Workshop config translator (Phase B) emits these
        // descriptors with an empty DeviceGuid ("first device on the slot").
        //
        // Ordered so the picker can iterate it to emit the family. The
        // paddle indices follow SDL's gamepad-button enum order exactly as
        // GetGamepadState writes them (RIGHT_PADDLE1=12, LEFT_PADDLE1=13,
        // RIGHT_PADDLE2=14, LEFT_PADDLE2=15); the display layer names each
        // one so the user sees which physical paddle a family index means.
        public static readonly (string Member, string Canonical)[] GamepadAliasTable =
        {
            ("ButtonA",       "Button 0"),
            ("ButtonB",       "Button 1"),
            ("ButtonX",       "Button 2"),
            ("ButtonY",       "Button 3"),
            ("LeftShoulder",  "Button 4"),
            ("RightShoulder", "Button 5"),
            ("ButtonBack",    "Button 6"),
            ("ButtonStart",   "Button 7"),
            ("LeftStick",     "Button 8"),   // click
            ("RightStick",    "Button 9"),   // click
            ("ButtonGuide",   "Button 10"),
            ("Paddle1",       "Button 12"),
            ("Paddle2",       "Button 13"),
            ("Paddle3",       "Button 14"),
            ("Paddle4",       "Button 15"),
            ("DPadUp",        "POV 0 Up"),
            ("DPadDown",      "POV 0 Down"),
            ("DPadLeft",      "POV 0 Left"),
            ("DPadRight",     "POV 0 Right"),
            ("LeftStickX",    "Axis 0"),
            ("LeftStickY",    "Axis 1"),
            ("RightStickX",   "Axis 3"),
            ("RightStickY",   "Axis 4"),
            ("LeftTrigger",   "Axis 2"),
            ("RightTrigger",  "Axis 5"),
        };

        private static readonly Dictionary<string, string> _gamepadAliasLookup = BuildGamepadAliasLookup();

        private static Dictionary<string, string> BuildGamepadAliasLookup()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (member, canonical) in GamepadAliasTable)
                d[member] = canonical;
            return d;
        }

        /// <summary>True for any descriptor in the abstract gamepad family
        /// (starts with <c>"Gamepad "</c>). The picker gates these on the
        /// device being a gamepad; here it is a cheap prefix test used by
        /// the canonicalizer and the display layer.</summary>
        public static bool IsGamepadAliasDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && descriptor.StartsWith("Gamepad ", StringComparison.Ordinal);

        /// <summary>Translates a <c>"Gamepad &lt;Name&gt;"</c> alias into the
        /// canonical per-device descriptor it resolves to
        /// (<c>"Gamepad LeftStickX"</c> → <c>"Axis 0"</c>). Returns
        /// <c>null</c> for anything that is not a recognized gamepad alias,
        /// so callers can fall through to the raw descriptor.</summary>
        public static string ResolveGamepadAlias(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return null;
            string s = descriptor.Trim();
            if (!s.StartsWith("Gamepad ", StringComparison.Ordinal)) return null;
            string member = s.Substring("Gamepad ".Length).Trim();
            return _gamepadAliasLookup.TryGetValue(member, out string canonical) ? canonical : null;
        }

        /// <summary>Returns the descriptor the coercion pipeline should read:
        /// a recognized <c>"Gamepad ..."</c> alias is folded to its canonical
        /// per-device form so every existing reader / evaluator branch (and
        /// the Invert-internalization checks that key off <c>"Axis "</c>)
        /// sees the resolved type; everything else is returned trimmed,
        /// unchanged. Runs at the top of each reader and every
        /// descriptor-type inspection so persisted and displayed descriptors
        /// keep the "Gamepad ..." form while evaluation stays on the proven
        /// path.</summary>
        internal static string CanonicalDescriptor(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return "";
            string s = descriptor.Trim();
            if (s.StartsWith("Gamepad ", StringComparison.Ordinal))
            {
                string canonical = ResolveGamepadAlias(s);
                if (!string.IsNullOrEmpty(canonical)) return canonical;
            }
            return s;
        }

        /// <summary>True when a source carries the generic per-source
        /// <see cref="MappingSource.Sensitivity"/> knob: it resolves to a
        /// plain <c>"Axis N"</c> / <c>"Slider N"</c> read (including the
        /// abstract Gamepad sticks / triggers that canonicalize to one),
        /// or to a touchpad finger X/Y position read (#9 B-13, so a
        /// workshop config's per-group touch sensitivity can live on the
        /// row instead of punting to the Touchpad tab). Drives the
        /// picker/VM slider visibility, mirroring
        /// <see cref="IsGyroDescriptor"/> and the other per-family
        /// sensitivity predicates. Gyro / mouse / IR carry their own
        /// specialized sensitivity and stay excluded. Touchpad Pressure,
        /// Click, and "Finger M Down" also stay excluded: Pressure is a
        /// physical magnitude, and the bool descriptors have no analog
        /// read to scale.</summary>
        public static bool IsGenericSensitivityDescriptor(string descriptor)
        {
            string c = CanonicalDescriptor(descriptor);
            if (c.StartsWith("Axis ", StringComparison.Ordinal)
                || c.StartsWith("Slider ", StringComparison.Ordinal))
                return true;
            return IsTouchpadFingerAxisDescriptor(c);
        }

        /// <summary>True for the touchpad finger-position axes
        /// <c>"Touchpad N Finger M X"</c> / <c>"... Y"</c> (#9 B-13),
        /// including the region-windowed half variants <c>"... X Left"</c> /
        /// <c>"X Right"</c> / <c>"Y Left"</c> / <c>"Y Right"</c> (#9 B-1),
        /// which are the same position reads gated to one half of the pad.
        /// Pressure (<c>"... Pressure"</c>) is excluded on purpose: the
        /// generic Sensitivity knob scales finger POSITION reads (delta,
        /// absolute, and unipolar), never the physical pressure level.
        /// No "Gamepad ..." alias maps to a touchpad member
        /// (GamepadAliasTable carries buttons/POV/axes only), so the bare
        /// spelling is the only one; callers pass the canonical form.</summary>
        public static bool IsTouchpadFingerAxisDescriptor(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)
                || !descriptor.StartsWith("Touchpad ", StringComparison.Ordinal))
                return false;
            return TryParseTouchpadAxis(descriptor, out _, out _, out int axisOffset, out _)
                && (axisOffset == 0 || axisOffset == 1);
        }

        /// <summary>Per-source generic Sensitivity multiplier, guarded like
        /// the specialized ones (a persisted 0 from a legacy row reads as
        /// the 1.0 default rather than zeroing the source).</summary>
        private static float PerSourceSensitivity(MappingSource src)
            => (float)(src.Sensitivity > 0 ? src.Sensitivity : 1.0);

        /// <summary>True for any MIDI-input descriptor
        /// (<c>"Midi Note N"</c> / <c>"Midi CC N"</c> /
        /// <c>"Midi Pitch Bend"</c>).</summary>
        /// <summary>True when the descriptor belongs to an engine-evaluated
        /// family whose name legitimately begins with 'I' ("IR Pointer X/Y",
        /// "IR Brightness"), so the legacy I/H invert/half prefix grammar must
        /// NOT strip its first letter. Found on first Wii IR hardware contact
        /// (2026-07-01): the legacy-to-set migration read "IR Pointer X" as
        /// Invert + "R Pointer X" and persisted the mangled descriptor, so the
        /// pointer row evaluated to 0 forever. Every prefix-strip site guards
        /// on this before interpreting 'I'/'H'.
        /// SIBLING: InputManager.Step3.TryGetEngineOwnedSource is a SEPARATE,
        /// remainder-gated grammar for the same I/H collision on the per-key
        /// output path. A new 'I'- or 'H'-leading engine-owned family must be
        /// reflected in BOTH (this allow-list AND IsEngineOwnedDescriptor) or
        /// the two paths disagree.</summary>
        public static bool IsPrefixExemptDescriptor(string s) =>
            !string.IsNullOrEmpty(s)
            && (s.StartsWith("IR Pointer ", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("IR Brightness", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("IR Offscreen", StringComparison.OrdinalIgnoreCase));

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

        /// <summary>True for touchpad-gesture descriptors:
        /// <c>"Touchpad N <GestureName>"</c> where GestureName is
        /// neither <c>Click</c>, <c>Finger ...</c>, nor
        /// <c>Pointer ...</c> (#9 B-15). Distinguishes
        /// gesture sources from the legacy touchpad-button, per-
        /// finger axis, and absolute-pointer descriptors that share the
        /// same <c>Touchpad </c> prefix.</summary>
        public static bool IsTouchpadGestureDescriptor(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return false;
            if (!descriptor.StartsWith("Touchpad ", StringComparison.Ordinal)) return false;
            var parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3
                && !parts[2].Equals("Click", StringComparison.Ordinal)
                && !parts[2].Equals("Finger", StringComparison.Ordinal)
                && !parts[2].Equals("Pointer", StringComparison.Ordinal);
        }

        /// <summary>True for the mouse-gesture family
        /// <c>"Mouse Gesture Left/Right/Up/Down/Click"</c> (issue #200).</summary>
        public static bool IsMouseGestureDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && descriptor.StartsWith("Mouse Gesture ", StringComparison.Ordinal);

        /// <summary>True for the menu-item family
        /// <c>"Menu {menuId} Item {k}"</c> (#9 B-17): exactly four tokens
        /// with integer id / index. Strict so nothing else beginning with
        /// "Menu" ever misclassifies.</summary>
        public static bool IsMenuItemDescriptor(string descriptor)
            => TryParseMenuItem(descriptor, out _, out _);

        /// <summary>Parses <c>"Menu {menuId} Item {k}"</c> into its menu id
        /// and item index. Returns false for anything else.</summary>
        public static bool TryParseMenuItem(string descriptor, out int menuId, out int itemIndex)
        {
            menuId = -1;
            itemIndex = -1;
            if (string.IsNullOrEmpty(descriptor)) return false;
            if (!descriptor.StartsWith("Menu ", StringComparison.Ordinal)) return false;
            var parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 4
                && parts[2].Equals("Item", StringComparison.Ordinal)
                && int.TryParse(parts[1], out menuId)
                && int.TryParse(parts[3], out itemIndex)
                && menuId >= 0 && itemIndex >= 0;
        }

        /// <summary>Cached twin of <see cref="TryParseMenuItem"/> for the
        /// per-tick readers: keys the parse on the source's Descriptor
        /// REFERENCE so the hot path skips the Split. Poll thread only. A
        /// UI-thread descriptor swap misses the key and reparses next tick
        /// (one-tick staleness accepted). A cached menu id of -1 records a
        /// non-menu descriptor, so those also skip the Split.</summary>
        internal static bool TryParseMenuItemCached(MappingSource src, string canonical,
            out int menuId, out int itemIndex)
        {
            string key = src.Descriptor;
            if (ReferenceEquals(src.MenuParseKey, key))
            {
                menuId = src.MenuParseMenuId;
                itemIndex = src.MenuParseItemIndex;
                return menuId >= 0;
            }
            bool ok = TryParseMenuItem(canonical, out menuId, out itemIndex);
            src.MenuParseMenuId = ok ? menuId : -1;
            src.MenuParseItemIndex = ok ? itemIndex : -1;
            src.MenuParseKey = key;
            return ok;
        }

        /// <summary>Extracts the gesture name from a mouse-gesture
        /// descriptor ("Mouse Gesture Left" becomes "Left"). Empty when the
        /// descriptor is not of the family.</summary>
        public static string ParseMouseGestureName(string descriptor)
            => IsMouseGestureDescriptor(descriptor)
                ? descriptor.Substring("Mouse Gesture ".Length).Trim()
                : "";

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
            if (parts[2].Equals("Pointer", StringComparison.Ordinal)) return false; // #9 B-15 absolute pointer
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

        /// <summary>Returns true if the named mouse gesture ("Left" /
        /// "Right" / "Up" / "Down" / "Click") fired on the given
        /// <c>(slotIndex, deviceGuid)</c> within the current cooldown
        /// pulse (issue #200). Slot-keyed like the touchpad twin so each
        /// slot's Mouse-tab settings govern only that slot's rows.
        /// Returns false when unwired.</summary>
        public static Func<int, string, string, bool> MouseGestureFiredProvider { get; set; }

        /// <summary>Returns true if menu <c>menuId</c>'s item <c>k</c> is
        /// fired on the given <c>(slotIndex, deviceGuid, menuId, itemIndex)</c>
        /// this polling tick (#9 B-17): asserted by a hold-shaped fire type
        /// (Click / Always) or inside a one-shot commit's pulse window
        /// (ClickRelease / TouchRelease). Slot-keyed like the gesture
        /// providers. Returns false when unwired.</summary>
        public static Func<int, string, int, int, bool> MenuItemFiredProvider { get; set; }

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

        /// <summary>Reads the per-source Wii IR pointer axis (issue #146): pulls the
        /// normalized pointer from the device's own <see cref="CustomInputState.Ir"/>
        /// (so two remotes never share a pointer), selects X or Y from the
        /// descriptor, applies the per-source
        /// <see cref="MappingSource.IrPointerSensitivity"/>, then clamps to
        /// [-1..+1]. Returns 0 when no dot is seen this frame (Detected false), which
        /// the callers read as "centered", so a brief loss of sight relaxes the
        /// stick rather than snapping it. Invert is applied by the public Evaluate*
        /// wrappers, matching the cursor and gyro paths.</summary>
        /// <summary>The lineage's default aim-range normalization. All four
        /// Touchmote variants on disk ship pointer_marginsLeftRight = 0.4 and
        /// pointer_marginsTopBottom = 0.5 as DEFAULTS (each repo's
        /// WiiTUIO/Properties/Settings.cs), mapping camera position 0..1
        /// onto [-margin .. 1+margin] (ScreenPositionCalculator.cs:71-77,
        /// applied at :194-195), which in centered aim units is exactly a
        /// stretch of 1 + 2*margin. Both sensor-bar LEDs must stay inside
        /// the camera view for a pair midpoint, so tracked aim physically
        /// cannot reach +/-1; without this stretch the cursor walls off
        /// inside the screen in EVERY pointer mode (the border transform is
        /// identity inside its region, so the border modes hit the same
        /// wall; owner bench, 2026-07-11). The old single-dot fallback
        /// masked this by accident: the raw surviving dot extended the
        /// apparent range past the pair bound.</summary>
        internal const float IrMarginStretchX = 1.8f;
        internal const float IrMarginStretchY = 2.0f;

        private static float ReadTunedIrPointer(CustomInputState state, MappingSource src, int slotIndex, string deviceGuid)
        {
            if (src == null || state == null) return 0f;

            string s = src.Descriptor ?? "";
            char axis;
            if (s.EndsWith(" X", StringComparison.Ordinal)) axis = 'X';
            else if (s.EndsWith(" Y", StringComparison.Ordinal)) axis = 'Y';
            else return 0f;

            string dev = deviceGuid ?? "";
            if (!state.Ir.Detected)
            {
                // Sight lost: relax to center and drop the smoothing state so a
                // re-acquire snaps instead of sliding in from stale.
                _irEmaPrev.TryRemove((dev, slotIndex, axis), out _);
                return 0f;
            }

            // Lineage margin stretch first, then the bar offset in its
            // post-stretch (screen-space) meaning, matching Touchmote's
            // pixel-space offsetY applied after the margin scale
            // (ScreenPositionCalculator.cs:194-195).
            float baseVal = (axis == 'X' ? state.Ir.X : state.Ir.Y)
                * (axis == 'X' ? IrMarginStretchX : IrMarginStretchY);

            // Per-(device, slot) Pointer-tab tuning: sensor-bar vertical offset
            // (Y only, Touchmote offsetY) then EMA smoothing, applied HERE at
            // the slot-scoped read (not the per-device wrapper) so each virtual
            // controller keeps its own pointer feel. Same order the wrapper
            // used: offset -> smoothing -> sensitivity -> clamp.
            var tuning = IrTuningProvider?.Invoke(dev, slotIndex);
            if (tuning.HasValue)
            {
                if (axis == 'Y') baseVal += tuning.Value.barOffset;
                float sm = Math.Clamp(tuning.Value.smoothing, 0f, 0.95f);
                if (sm > 0f)
                {
                    var key = (dev, slotIndex, axis);
                    if (_irEmaPrev.TryGetValue(key, out var prev))
                    {
                        if (prev.Seq == _pollFrameSeq)
                        {
                            baseVal = prev.Value; // second row, same poll
                        }
                        else
                        {
                            baseVal = prev.Value + (baseVal - prev.Value) * (1f - sm);
                            _irEmaPrev[key] = (baseVal, _pollFrameSeq);
                        }
                    }
                    else
                    {
                        _irEmaPrev[key] = (baseVal, _pollFrameSeq); // seed unsmoothed
                    }
                }
            }

            float v = baseVal * (float)src.IrPointerSensitivity;
            if (v < -1f) v = -1f;
            else if (v > 1f) v = 1f;
            return v;
        }

        /// <summary>Per-poll sensor counts that map to full deflection for the
        /// Joy-Con 2 mouse sources (issue #154). Chosen for parity with a real
        /// mouse in PadForge: SdlMouseWrapper turns Raw Input deltas into axis
        /// values at MotionScale 2048 per count over the 0..65535 range, i.e.
        /// 16 counts in one poll = full scale. The sensor and a physical mouse
        /// therefore feel identical through the same mapping grid.</summary>
        private const float JoyCon2MouseCountsFullScale = 16f;

        /// <summary>Reads a Joy-Con 2 optical mouse motion source
        /// ("Mouse Motion X" / "Mouse Motion Y", issue #154) as a bipolar
        /// [-1..+1] per-poll velocity. The per-source
        /// <see cref="MappingSource.IrPointerSensitivity"/> scales it like the
        /// IR pointer (default 1.0), so a row can be made faster or slower
        /// without touching the shared constant. Invert rides the public
        /// Evaluate* wrappers, matching the other engine families.</summary>
        private static float ReadJoyCon2MouseMotion(CustomInputState state, MappingSource src)
        {
            if (state == null || src?.Descriptor == null) return 0f;
            string s = src.Descriptor;
            float counts;
            if (s.EndsWith(" X", StringComparison.Ordinal)) counts = state.JoyCon2MouseDX;
            else if (s.EndsWith(" Y", StringComparison.Ordinal)) counts = state.JoyCon2MouseDY;
            else return 0f;

            float v = counts / JoyCon2MouseCountsFullScale * (float)src.IrPointerSensitivity;
            if (v < -1f) v = -1f;
            else if (v > 1f) v = 1f;
            return v;
        }

        /// <summary>Reads a Wii Balance Board derived source (issue #146). The four
        /// corner load cells arrive on the gamepad stick axes
        /// (<see cref="CustomInputState.Axis"/> indices 0/1/3/4 = TopLeft /
        /// BottomLeft / TopRight / BottomRight, each raw int16 + 32768). Returns:
        ///   "Balance Lean X"  → center-of-gravity offset left↔right, bipolar [-1..+1]
        ///   "Balance Lean Y"  → CoG offset back↔front, bipolar [-1..+1]
        ///   "Balance Total Weight" → unipolar [0..1] of the kg sum over
        ///       <see cref="BalanceMaxKg"/>, after the per-board tare.
        /// Lean is a pure ratio and needs no calibration; Total Weight uses
        /// <see cref="BalanceCalibrationProvider"/> per-corner kg interpolation when
        /// available, else a raw-proportional fallback.</summary>
        private static float ReadTunedBalanceBoard(CustomInputState state, MappingSource src, string deviceGuid)
        {
            if (src == null || state == null || state.Axis == null) return 0f;

            // Raw int16 load cells (undo the +32768 unsigned shift). Clamp negatives
            // to 0: an unloaded cell can read slightly below zero, which would skew
            // the ratios and the sum.
            float tl = Math.Max(0, state.Axis[0] - 32768);
            float bl = Math.Max(0, state.Axis[1] - 32768);
            float tr = Math.Max(0, state.Axis[3] - 32768);
            float br = Math.Max(0, state.Axis[4] - 32768);

            string s = src.Descriptor ?? "";

            if (s.EndsWith("Lean X", StringComparison.Ordinal))
            {
                float left = tl + bl, right = tr + br;
                float total = left + right;
                if (total <= 1f) return 0f;
                return Math.Max(-1f, Math.Min(1f, (right - left) / total));
            }
            if (s.EndsWith("Lean Y", StringComparison.Ordinal))
            {
                float top = tl + tr, bottom = bl + br;
                float total = top + bottom;
                if (total <= 1f) return 0f;
                return Math.Max(-1f, Math.Min(1f, (top - bottom) / total));
            }
            if (s.EndsWith("Total Weight", StringComparison.Ordinal))
            {
                float kg;
                // Caller-resolved guid (EffectiveDeviceGuid), not the bare
                // src.DeviceGuid: an empty source guid is the documented
                // "the device on this slot" form and the provider returns
                // null (uncalibrated) for it, same trap as the gesture
                // providers above.
                var cal = BalanceCalibrationProvider?.Invoke(deviceGuid ?? "");
                if (cal != null && cal.Length >= 12)
                {
                    kg = RawCornerToKg(tl, cal, 0) + RawCornerToKg(bl, cal, 1)
                       + RawCornerToKg(tr, cal, 2) + RawCornerToKg(br, cal, 3);
                }
                else
                {
                    // No calibration yet: approximate kg from the raw sum so the
                    // source is still monotonic and usable (the 17 kg reference is
                    // ~roughly a few thousand raw units; this is intentionally a
                    // coarse fallback, replaced the moment calibration arrives).
                    kg = (tl + bl + tr + br) / 200f;
                }
                float tare = BalanceTareKgProvider?.Invoke(deviceGuid ?? "") ?? 0f;
                kg -= tare;
                if (kg < 0f) kg = 0f;
                return Math.Min(1f, kg / BalanceMaxKg);
            }
            return 0f;
        }

        /// <summary>Interpolates one raw corner load-cell reading to kilograms using
        /// the board's three reference points (Kg0, Kg17, Kg34), the documented Wii
        /// Balance Board piecewise-linear curve (WiimoteLib GetBalanceBoardSensorValue):
        /// below the 17 kg point it scales 0→17 kg across Kg0→Kg17, at or above it
        /// scales 17→34 kg across Kg17→Kg34 and extrapolates past 34 kg.</summary>
        private static float RawCornerToKg(float raw, float[] cal, int corner)
        {
            float kg0 = cal[corner * 3 + 0];
            float kg17 = cal[corner * 3 + 1];
            float kg34 = cal[corner * 3 + 2];
            if (raw < kg17)
            {
                float span = kg17 - kg0;
                return span > 0f ? 17f * (raw - kg0) / span : 0f;
            }
            float span2 = kg34 - kg17;
            return span2 > 0f ? 17f * (raw - kg17) / span2 + 17f : 17f;
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

        /// <summary>The auxiliary-accelerometer twin of
        /// <see cref="MotionLeanDescriptor"/> (issue #199): tilt derived from
        /// the Nunchuk / left Joy-Con gravity instead of the primary body's.
        /// Exact-match descriptor like the primary; the display label is
        /// contextual per device ("Nunchuk Lean" on Wii remotes) but this one
        /// internal string is what persists. Starts with "Motion " so it
        /// inherits the same benign IsMotionDescriptor classification the
        /// primary already has, and the leading 'M' keeps it clear of the
        /// I/H prefix grammar.</summary>
        public const string MotionLeanAuxDescriptor = "Motion Lean L";

        /// <summary>True when the descriptor is <see cref="MotionLeanAuxDescriptor"/>.</summary>
        public static bool IsMotionLeanAuxDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && string.Equals(descriptor.Trim(), MotionLeanAuxDescriptor, StringComparison.OrdinalIgnoreCase);

        /// <summary>Flick stick descriptors (#225): first-class inputs like
        /// <see cref="MotionLeanDescriptor"/>, picked from the input dropdown
        /// and mapped to the Mouse X (KbmMouseX) target, on any layer. The
        /// suffix names which of the device's sticks flicks; the axes resolve
        /// through <see cref="GamepadAliasTable"/> (Right → Axis 3/4, Left →
        /// Axis 0/1) so the read follows whatever pad feeds the slot. Tuning
        /// rides the source's ParamFlick* fields.</summary>
        public const string FlickStickRightDescriptor = "Flick Stick Right";
        public const string FlickStickLeftDescriptor = "Flick Stick Left";

        /// <summary>True for either flick stick descriptor.</summary>
        public static bool IsFlickStickDescriptor(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return false;
            string s = descriptor.Trim();
            return string.Equals(s, FlickStickRightDescriptor, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, FlickStickLeftDescriptor, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Resolves a flick stick descriptor to the canonical stick
        /// axis pair it reads ("Axis 3"/"Axis 4" for Right, "Axis 0"/"Axis 1"
        /// for Left, per <see cref="GamepadAliasTable"/>). False for
        /// non-flick descriptors.</summary>
        public static bool TryGetFlickStickAxes(string descriptor, out string xAxis, out string yAxis)
        {
            xAxis = yAxis = null;
            if (!IsFlickStickDescriptor(descriptor)) return false;
            bool left = descriptor.Trim().EndsWith("Left", StringComparison.OrdinalIgnoreCase);
            xAxis = ResolveGamepadAlias(left ? "Gamepad LeftStickX" : "Gamepad RightStickX");
            yAxis = ResolveGamepadAlias(left ? "Gamepad LeftStickY" : "Gamepad RightStickY");
            return xAxis != null && yAxis != null;
        }

        /// <summary>Stick deflection-ring descriptors (translator v17,
        /// Steam's joystick Outer Ring Binding). The value is the stick
        /// pair's deflection magnitude, sqrt(x*x + y*y) clamped to [0..1],
        /// the same pair read flick stick uses (Left = Axis 0/1, Right =
        /// Axis 3/4 via <see cref="GamepadAliasTable"/>). The bool read
        /// consumes the source flags as ring geometry: DeadZone percent is
        /// the ring RADIUS (Steam's edge_binding_radius on the 0..32767
        /// deflection scale, the v11 grounding, mapped to percent), and
        /// Invert selects the INNER ring ("the command will be sent when
        /// inside the radius instead of outside", Steam's shipped
        /// EdgeBindingInvert string) instead of the default outer ring.</summary>
        public const string LeftStickRingDescriptor = "Gamepad LeftStickRing";
        public const string RightStickRingDescriptor = "Gamepad RightStickRing";

        /// <summary>True for either stick-ring descriptor.</summary>
        public static bool IsStickRingDescriptor(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return false;
            string s = descriptor.Trim();
            return string.Equals(s, LeftStickRingDescriptor, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, RightStickRingDescriptor, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Resolves a stick-ring descriptor to the canonical axis
        /// pair it reads, the <see cref="TryGetFlickStickAxes"/> shape.</summary>
        public static bool TryGetStickRingAxes(string descriptor, out string xAxis, out string yAxis)
        {
            xAxis = yAxis = null;
            if (!IsStickRingDescriptor(descriptor)) return false;
            bool left = descriptor.Trim().StartsWith("Gamepad Left", StringComparison.OrdinalIgnoreCase);
            xAxis = ResolveGamepadAlias(left ? "Gamepad LeftStickX" : "Gamepad RightStickX");
            yAxis = ResolveGamepadAlias(left ? "Gamepad LeftStickY" : "Gamepad RightStickY");
            return xAxis != null && yAxis != null;
        }

        /// <summary>Rest floor for the INNER ring read, percent of full
        /// deflection. Steam gates ring commands on the stick actually
        /// being deflected (a centered stick sits inside every radius, and
        /// without a floor an inner-ring key would be held forever at
        /// rest). The only wild inner-ring authoring in the corpus
        /// (789818086, the walk-modifier config) pairs its ring with an
        /// authored stick deadzone of 1638/32767, 5 percent. This floor
        /// matches it. A detection-layer constant by design, not a lineage
        /// value.</summary>
        internal const int StickRingInnerFloorPercent = 5;

        /// <summary>Deflection magnitude of the ring's stick pair,
        /// [0..1]. Same normalization as the flick-stick pair read
        /// (SourceKindRuntime.ReadNormAxis: center 32768, span 32767).</summary>
        internal static float ReadStickRingMagnitude(CustomInputState state, string canonical)
        {
            if (state == null || !TryGetStickRingAxes(canonical, out string xDesc, out string yDesc))
                return 0f;
            float x = ReadRingNormAxis(state, xDesc);
            float y = ReadRingNormAxis(state, yDesc);
            float mag = (float)Math.Sqrt(x * x + y * y);
            return mag > 1f ? 1f : mag;
        }

        private static float ReadRingNormAxis(CustomInputState state, string axisDesc)
        {
            var parts = axisDesc.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int idx)
                || idx < 0 || idx >= CustomInputState.MaxAxis)
                return 0f;
            float v = (state.Axis[idx] - 32768) / 32767f;
            return v < -1f ? -1f : (v > 1f ? 1f : v);
        }

        /// <summary>The ring's bool read: outer = magnitude at or past the
        /// radius, inner (Invert) = deflected past the rest floor but
        /// inside the radius. The radius rides the per-source DeadZone
        /// percent (falling back to the caller's global threshold, the
        /// standard per-source-DeadZone contract).</summary>
        private static bool ReadStickRingBool(CustomInputState state, MappingSource src,
            string canonical, int globalThresholdPercent)
        {
            float mag = ReadStickRingMagnitude(state, canonical);
            int radiusPct = src.DeadZone > 0 ? src.DeadZone : globalThresholdPercent;
            float r01 = Math.Max(radiusPct, 1) / 100f;
            if (src.Invert)
                return mag > StickRingInnerFloorPercent / 100f && mag <= r01;
            return mag >= r01;
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
        private static float ReadTunedGyroRate(CustomInputState state, MappingSource src, int slotIndex, string srcDeviceGuid, out int gyroAxis, out GyroTuning tuning)
        {
            gyroAxis = -1;
            tuning = default;
            if (state == null || src == null) return 0f;

            tuning = GetGyroTuning(srcDeviceGuid, slotIndex);

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
                float defl = ResolveStickDeflection(slotIndex, tuning.EasyAimStickSide, tuning.EasyAimStickDirection);
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
            string deviceGuid = srcDeviceGuid;
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
                // v3.3 legacy EMA path, kept for back-compat when the
                // user has a non-zero SmoothingAlpha and both v3.4
                // thresholds at zero. A Roll source gets its own EMA lane
                // (2, matching the passthrough path below): in Local space
                // a Gyro Roll row and a Gyro Yaw row on the same
                // (device, slot) would otherwise both key lane 1, and the
                // per-frame seq gate would hand the second-evaluated row
                // the first's smoothed value. Horizontal stays on lane 1
                // deliberately: it is the yaw-equivalent blend, designed
                // to replace a yaw row, not coexist with one.
                yaw   = ApplyGyroSmoothing(deviceGuid, slotIndex, isRollSource ? 2 : 1, yaw, tuning.SmoothingAlpha);
                pitch = ApplyGyroSmoothing(deviceGuid, slotIndex, 0, pitch, tuning.SmoothingAlpha);
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
                float defl = ResolveStickDeflection(slotIndex, tuning.EasyAimStickSide, tuning.EasyAimStickDirection);
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
                // slotIndex in the key: the passthrough tuning is per-(device,
                // slot) like the mapping path's, and the bare "pt" key shared
                // (and double-advanced) one EMA across two slots running
                // passthrough on the same device.
                pYaw   = ApplyGyroSmoothing(smKey, slotIndex, 1, pYaw,   tuning.SmoothingAlpha);
                pPitch = ApplyGyroSmoothing(smKey, slotIndex, 0, pPitch, tuning.SmoothingAlpha);
                if (local)
                    pRoll = ApplyGyroSmoothing(smKey, slotIndex, 2, pRoll, tuning.SmoothingAlpha);
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
            CustomInputState state, MappingSource src, int globalThresholdPercent, int slotIndex = -1,
            string evaluatedDeviceGuid = null)
        {
            if (state == null || src == null) return false;

            bool raw = ReadAsBool(state, src, globalThresholdPercent, slotIndex,
                EffectiveDeviceGuid(src, evaluatedDeviceGuid));

            // Axis sources internalize Invert inside ReadAsBool — for
            // half-axis it picks which half to test, for full-axis it
            // flips the comparison. Applying Invert again here would
            // double-cancel, which is what broke the standard "two
            // opposing buttons on a centered axis" pattern (Left half
            // never fired because the inner branch returned true and
            // this outer flip turned it back to false).
            string desc = CanonicalDescriptor(src.Descriptor);
            if (desc.StartsWith("Axis", System.StringComparison.Ordinal)) return raw;
            // Mouse Motion internalizes Invert the same way (issue #154):
            // with HalfAxis it picks the direction (left/up vs right/down),
            // and without HalfAxis the any-direction test makes Invert
            // irrelevant. Flipping here would double-cancel the directional
            // rows exactly like the axis case above.
            if (desc.StartsWith("Mouse Motion ", System.StringComparison.Ordinal)) return raw;
            // A half-axis gyro read consumes Invert as its direction selector
            // (v15 gyro swipes), same shape as Mouse Motion. Non-half gyro
            // sources keep the legacy outer flip below.
            if (src.HalfAxis && desc.StartsWith("Gyro ", System.StringComparison.Ordinal)) return raw;
            // The stick-ring read consumes Invert as its inner/outer
            // selector (v17): flipping here would turn an inner ring into
            // NOT-inner, which fires at full deflection instead.
            if (IsStickRingDescriptor(desc)) return raw;

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
            bool relativeTouchpad = false, string evaluatedDeviceGuid = null)
        {
            if (state == null || src == null) return 0f;

            float raw = ReadAsBipolar(state, src, slotIndex, relativeTouchpad,
                EffectiveDeviceGuid(src, evaluatedDeviceGuid));
            // HalfAxis on a centered Axis source consumes Invert inside the
            // read as the half selector (lower half instead of upper),
            // mirroring the bool path, so the same row selects the same
            // physical motion whether it feeds a button or an axis target.
            // Negating on top would double-apply the flag. Such a source
            // carries its output flip in InvertOutput instead, which is how it
            // can select a half AND still invert.
            if (InvertConsumedByHalfAxisRead(src)) return src.InvertOutput ? -raw : raw;
            return src.Invert ? -raw : raw;
        }

        /// <summary>Evaluates a source for a unipolar trigger target.
        /// Returns a float in [0, +1]. Bipolar axes contribute their
        /// absolute value; buttons map to 0/1; HalfAxis still respects
        /// the active half.</summary>
        public static float EvaluateForTriggerTarget(
            CustomInputState state, MappingSource src, int slotIndex = -1,
            string evaluatedDeviceGuid = null)
        {
            if (state == null || src == null) return 0f;

            float raw = ReadAsUnipolar(state, src, slotIndex,
                EffectiveDeviceGuid(src, evaluatedDeviceGuid));
            // Mouse Motion internalizes Invert (issue #154): with HalfAxis it
            // picks which direction pulls the trigger; 1-v on a velocity would
            // read "full pull while still", which is never wanted.
            if ((src.Descriptor ?? "").StartsWith("Mouse Motion ", StringComparison.Ordinal)) return raw;
            // HalfAxis on a centered Axis source likewise internalizes
            // Invert as the half selector (lower half pulls the trigger).
            // 1-raw on top would read full-pressed at rest.
            if (InvertConsumedByHalfAxisRead(src)) return raw;
            return src.Invert ? 1f - raw : raw;
        }

        /// <summary>True when the analog reads consume the source's Invert
        /// flag internally as half-SELECTION (HalfAxis on a centered "Axis N"
        /// descriptor picks upper vs lower half, mirroring
        /// ReadButtonLikeBool), so the evaluators must not also apply their
        /// output-side Invert transform. Same internalized-Invert shape as
        /// the Mouse Motion family (issue #154).
        ///
        /// <para>Public because it is the ONE definition of "Invert is spoken
        /// for on this source". Any producer that wants an output flip must ask
        /// here first and write <see cref="MappingSource.InvertOutput"/> when
        /// this returns true, rather than assigning Invert and silently
        /// destroying the half selection. A second copy of this predicate
        /// living in a caller is how the two roles drift apart again.</para></summary>
        public static bool InvertConsumedByHalfAxisRead(MappingSource src)
        {
            if (!src.HalfAxis) return false;
            string s = CanonicalDescriptor(src.Descriptor);
            // Mouse Motion's bipolar read consumes Invert the same way when
            // HalfAxis is set (the read selects the direction); the trigger
            // evaluator already exempts the family unconditionally.
            // The stick ring (v17) consumes Invert as its inner/outer
            // selector. Its scalar reads return the unsigned deflection
            // magnitude either way, so no output flip may ride the flag.
            return s.StartsWith("Axis ", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("Mouse Motion ", StringComparison.Ordinal)
                || IsStickRingDescriptor(s);
        }

        /// <summary>Evaluates a source for a POV-direction target
        /// (DPadUp/Down/Left/Right). Same shape as button-target with
        /// PovDirection sources matching the descriptor's direction.</summary>
        public static bool EvaluateForPovDirectionTarget(
            CustomInputState state, MappingSource src, int globalThresholdPercent, int slotIndex = -1,
            string evaluatedDeviceGuid = null)
        {
            // POV-direction targets are bool; reuse the button path (which
            // already special-cases POV-direction sources via the parser).
            return EvaluateForButtonTarget(state, src, globalThresholdPercent, slotIndex, evaluatedDeviceGuid);
        }

        /// <summary>Device attribution for the per-(device, slot) readers.
        /// An empty <see cref="MappingSource.DeviceGuid"/> is a legal
        /// "any device" source (legacy hydration leaves it empty and the
        /// row eval matches every device), so keying tuning / debounce /
        /// EMA state off the bare src.DeviceGuid dropped per-device tuning
        /// and merged cache state across devices. Callers that know which
        /// device's state they are evaluating pass it as
        /// <c>evaluatedDeviceGuid</c>; non-empty source guids win so
        /// device-pinned rows behave exactly as before.</summary>
        internal static string EffectiveDeviceGuid(MappingSource src, string evaluatedDeviceGuid)
            => string.IsNullOrEmpty(src?.DeviceGuid) ? evaluatedDeviceGuid : src.DeviceGuid;

        // ─── Internal readers ──────────────────────────────────────────

        // NOTE (readers below): gesture / mouse-gesture provider lookups pass
        // the caller-resolved <c>deviceGuid</c> (EffectiveDeviceGuid: the
        // source's own guid when pinned, otherwise the device being
        // evaluated), NOT the bare src.DeviceGuid. An empty source guid is
        // the documented "the device on this slot" form (the Workshop
        // translator emits it), and the providers are keyed by a concrete
        // (slot, device, pad) triple, so the bare form always missed.
        private static bool ReadAsBool(CustomInputState state, MappingSource src, int globalThresholdPercent, int slotIndex, string deviceGuid)
        {
            string s = CanonicalDescriptor(src.Descriptor);
            if (string.IsNullOrEmpty(s)) return false;

            // Touchpad-gesture descriptors route through the per-tick
            // gesture engine's fire set; continuous-axis variants
            // (PinchAxis / RotateAxis) read as "fired" when their
            // magnitude exceeds the source's deadzone (engine-side
            // threshold semantics; one-shot variants ignore deadzone).
            // Mouse-gesture pulses (issue #200): pure one-shot bools from
            // the recognizer fired set, no axis variants.
            if (IsMouseGestureDescriptor(s))
            {
                return MouseGestureFiredProvider?.Invoke(
                    slotIndex, deviceGuid ?? "", ParseMouseGestureName(s)) ?? false;
            }

            // Menu items (#9 B-17): pure one-shot / hold bools from the
            // menu runtime's fired set, same shape as the gesture pulses.
            if (TryParseMenuItemCached(src, s, out int menuFireId, out int menuFireItem))
            {
                return MenuItemFiredProvider?.Invoke(
                    slotIndex, deviceGuid ?? "", menuFireId, menuFireItem) ?? false;
            }

            if (IsTouchpadGestureDescriptor(s))
            {
                if (!TryParseTouchpadGesture(s, out int gPad, out string gName)) return false;
                if (IsTouchpadGestureAxis(gName))
                {
                    float axisVal = TouchpadGestureAxisProvider?.Invoke(
                        slotIndex, deviceGuid ?? "", gPad, gName) ?? 0f;
                    float gThresh = src.DeadZone > 0 ? src.DeadZone / 100f : 0.5f;
                    return Math.Abs(axisVal) > gThresh;
                }
                return TouchpadGestureFiredProvider?.Invoke(
                    slotIndex, deviceGuid ?? "", gPad, gName) ?? false;
            }

            // Touchpad-as-button stays outside the generic Sensitivity
            // contract (#9 B-13 decision, per the 0c4e56cd precedent of
            // never leaving a visible knob inert): the bool read handles
            // Click and "Finger M Down" (whole-pad or half-windowed, #9
            // B-1) only, all unscaled booleans, and the finger X/Y
            // position axes have NO threshold read here (they fall
            // through ReadTouchpadBool and read false), so there is
            // nothing to scale. The VMs' deadzone gates exclude the
            // finger axes for the same reason.
            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // Absolute pointer as a button (#9 B-15): fires when the
                // tuned offset-from-center clears the per-source deadzone,
                // the same shape as the IR pointer's button coercion below.
                if (IsTouchpadPointerDescriptor(s))
                {
                    float pv = ReadTunedTouchpadPointer(state, src, slotIndex, deviceGuid);
                    int pdz = src.DeadZone > 0 ? src.DeadZone : globalThresholdPercent;
                    return Math.Abs(pv) > Math.Max(pdz, 1) / 100f;
                }
                return ReadTouchpadBool(state, s);
            }

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
                float tunedRate = ReadTunedGyroRate(state, src, slotIndex, deviceGuid, out int gyroAxis, out _);
                if (gyroAxis < 0) return false;
                // Per-source DeadZone (when set) overrides the default
                // 30°/s button threshold so users can dial in sensitivity.
                // Device-level deadzone has already been applied inside
                // ReadTunedGyroRate; this knob is the button-activation
                // threshold ON TOP of that.
                float gyroThresh = src.DeadZone > 0
                    ? src.DeadZone / 100f * GyroButtonThreshold * 3f  // DeadZone% × ~90°/s headroom
                    : GyroButtonThreshold;
                // The rate is a SIGNED bipolar axis, so HalfAxis selects one
                // rotation direction exactly like the Axis / Mouse Motion
                // grammar (v15 gyro swipes): Invert picks the half (false =
                // positive rate, true = negative) and Bidirectional restores
                // the any-direction test. Sign frame per SDL_sensor.h with
                // Dolphin's SDLGamepad.h SDL_AXES_GYRO as the proven consumer:
                // positive pitch = nose up, positive yaw = nose left.
                if (src.HalfAxis && !src.Bidirectional)
                    return src.Invert ? tunedRate < -gyroThresh : tunedRate > gyroThresh;
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

            if (s.Equals("IR Offscreen", StringComparison.Ordinal))
                return ReadIrOffscreen(state, src, deviceGuid);

            if (s.StartsWith("IR Pointer ", StringComparison.Ordinal))
            {
                float v = ReadTunedIrPointer(state, src, slotIndex, deviceGuid);
                int cdz = src.DeadZone > 0 ? src.DeadZone : globalThresholdPercent;
                return Math.Abs(v) > Math.Max(cdz, 1) / 100f;
            }

            if (s.StartsWith("Balance ", StringComparison.Ordinal))
            {
                float v = ReadTunedBalanceBoard(state, src, deviceGuid);
                int cdz = src.DeadZone > 0 ? src.DeadZone : globalThresholdPercent;
                return Math.Abs(v) > Math.Max(cdz, 1) / 100f;
            }

            if (s.Equals("IR Brightness", StringComparison.Ordinal))
            {
                // Cover-as-button (issue #151): pressed while the sensor reads
                // brighter than the threshold. Same per-row DeadZone override /
                // global-threshold fallback as the other derived sources.
                int cdz = src.DeadZone > 0 ? src.DeadZone : globalThresholdPercent;
                return state.JoyConIrIntensity > Math.Max(cdz, 1) / 100f;
            }

            if (s.StartsWith("Mouse Motion ", StringComparison.Ordinal))
            {
                // Motion-as-button (issue #154, the "invisible weapon wheel").
                // Default = ANY direction past the threshold (the issue's
                // "activates when cursor leaves small deadzone"). HalfAxis
                // selects ONE direction, Invert picks which (the grid's
                // direction grammar for bipolar sources): HalfAxis = right /
                // down, HalfAxis+Invert = left / up. Four rows give the full
                // up/down/left/right wheel the issue asks for.
                float v = ReadJoyCon2MouseMotion(state, src);
                int cdz = src.DeadZone > 0 ? src.DeadZone : globalThresholdPercent;
                float th = Math.Max(cdz, 1) / 100f;
                if (src.HalfAxis)
                {
                    // Bidirectional ("Either") restores the any-direction test,
                    // matching the generic Axis contract the UI documents
                    // ("either side of center counts; Invert has no effect").
                    if (src.Bidirectional) return Math.Abs(v) > th;
                    return src.Invert ? v < -th : v > th;
                }
                return Math.Abs(v) > th;
            }

            // Stick deflection ring (v17): ring geometry (radius on
            // DeadZone, inner on Invert) is consumed inside the read.
            if (IsStickRingDescriptor(s))
                return ReadStickRingBool(state, src, s, globalThresholdPercent);

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
                    // Generic per-source Sensitivity (issue #9) scales the
                    // value before the threshold test so the knob acts on
                    // button targets too, mirroring how the gyro / mouse /
                    // IR families consume theirs inside the read. Origin
                    // follows the matching analog leg: deviation-from-center
                    // for half-axis, magnitude-from-zero for full-axis
                    // (trigger semantics). sens == 1 leaves every comparison
                    // bit-identical to the unscaled path.
                    float boolSens = PerSourceSensitivity(src);
                    if (src.HalfAxis)
                    {
                        if (boolSens != 1f)
                        {
                            float scaled = 32768f + (av - 32768) * boolSens;
                            av = scaled < 0f ? 0 : scaled > 65535f ? 65535 : (int)scaled;
                        }
                        if (src.Bidirectional)
                        {
                            // Either side of center past deadzone counts:
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
                    if (boolSens != 1f)
                    {
                        float scaledFull = av * boolSens;
                        av = scaledFull > 65535f ? 65535 : (int)scaledFull;
                    }
                    int hi = (int)(thresh * 65535);
                    if (src.Invert)
                        return av < 65535 - hi;
                    return av > hi;

                case SourceType.Slider:
                    if (idx < 0 || idx >= CustomInputState.MaxSliders) return false;
                    int sv = state.Sliders[idx];
                    // Same sensitivity contract as the Axis leg above,
                    // magnitude-from-zero like the unipolar slider read.
                    float sliderSens = PerSourceSensitivity(src);
                    if (sliderSens != 1f)
                    {
                        float scaledSv = sv * sliderSens;
                        sv = scaledSv > 65535f ? 65535 : (int)scaledSv;
                    }
                    int shi = (int)(thresh * 65535);
                    return sv > shi;

                case SourceType.PovDirection:
                    if (idx < 0 || idx >= CustomInputState.MaxPovs) return false;
                    return PovMatches(state.Povs[idx], povDir);

                default:
                    return false;
            }
        }

        private static float ReadAsBipolar(CustomInputState state, MappingSource src, int slotIndex, bool relativeTouchpad, string deviceGuid)
        {
            string s = CanonicalDescriptor(src.Descriptor);
            if (string.IsNullOrEmpty(s)) return 0f;

            // Touchpad-gesture sources: continuous axes (PinchAxis,
            // RotateAxis) read their bipolar value from the gesture
            // engine's axis provider; one-shot gestures map to ±1
            // when fired (1 on the firing tick, 0 otherwise).
            // Mouse-gesture pulse as an axis contribution (issue #200):
            // 1 while the fired pulse is asserted, 0 otherwise, same as a
            // one-shot touchpad gesture.
            if (IsMouseGestureDescriptor(s))
            {
                bool mgFired = MouseGestureFiredProvider?.Invoke(
                    slotIndex, deviceGuid ?? "", ParseMouseGestureName(s)) ?? false;
                return mgFired ? 1f : 0f;
            }

            // Menu-item fire as an axis contribution (#9 B-17): 1 while
            // asserted / pulsed, 0 otherwise, same as a one-shot gesture.
            if (TryParseMenuItemCached(src, s, out int menuBiId, out int menuBiItem))
            {
                bool menuFired = MenuItemFiredProvider?.Invoke(
                    slotIndex, deviceGuid ?? "", menuBiId, menuBiItem) ?? false;
                return menuFired ? 1f : 0f;
            }

            if (IsTouchpadGestureDescriptor(s))
            {
                if (!TryParseTouchpadGesture(s, out int gPad, out string gName)) return 0f;
                if (IsTouchpadGestureAxis(gName))
                {
                    return TouchpadGestureAxisProvider?.Invoke(
                        slotIndex, deviceGuid ?? "", gPad, gName) ?? 0f;
                }
                bool fired = TouchpadGestureFiredProvider?.Invoke(
                    slotIndex, deviceGuid ?? "", gPad, gName) ?? false;
                return fired ? 1f : 0f;
            }

            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // Absolute pointer (#9 B-15): the tuned absolute read for
                // absolute consumers (stick targets, activators, and Step
                // 3's MouseAbs* routing, which evaluates with the default
                // relativeTouchpad = false). On the relative-delta lane the
                // family reads 0: a position is not a delta, and Step 3
                // falls through to this lane exactly when NO pointer source
                // on the row is engaged, so a mixed row (gyro + pointer,
                // corpus 3456927474) keeps its relative sources alive with
                // the pointer contributing nothing instead of leaking a
                // constant absolute offset into the delta sum every poll.
                if (IsTouchpadPointerDescriptor(s))
                    return relativeTouchpad ? 0f : ReadTunedTouchpadPointer(state, src, slotIndex, deviceGuid);

                // Two readings for touchpad sources:
                //   relative: per-frame delta scaled to mouse-style
                //     bipolar, used by KBM mouse / scroll targets.
                //   absolute: raw pad position [0..1] mapped to
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
                    if (TryReadTouchpadAxisAbsolute(state, src, s, out float bipolar)) return bipolar;
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
                float tunedRate = ReadTunedGyroRate(state, src, slotIndex, deviceGuid, out int gyroAxis, out var tuning);
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

            if (s.Equals("IR Offscreen", StringComparison.Ordinal))
                return ReadIrOffscreen(state, src, deviceGuid) ? 1f : 0f;

            if (s.StartsWith("IR Pointer ", StringComparison.Ordinal))
                return ReadTunedIrPointer(state, src, slotIndex, deviceGuid);

            if (s.StartsWith("Balance ", StringComparison.Ordinal))
                return ReadTunedBalanceBoard(state, src, deviceGuid);

            if (s.Equals("IR Brightness", StringComparison.Ordinal))
                return state.JoyConIrIntensity;

            if (s.StartsWith("Mouse Motion ", StringComparison.Ordinal))
            {
                float mv = ReadJoyCon2MouseMotion(state, src);
                if (src.HalfAxis)
                {
                    // Mirror the generic Axis bipolar+HalfAxis contract
                    // (#154 grammar): the selected direction ranges [0, +1],
                    // the other direction reads 0, Invert picks the half,
                    // Bidirectional folds both to magnitude. Without this the
                    // same persisted flags meant "up only" on a trigger
                    // target but "both directions, flipped" on a stick.
                    if (src.Bidirectional)
                        return Math.Min(1f, Math.Abs(mv));
                    if (src.Invert)
                        return mv < 0 ? Math.Min(1f, -mv) : 0f;
                    return mv > 0 ? Math.Min(1f, mv) : 0f;
                }
                return mv;
            }

            // Stick ring as an analog contribution (v17): the unsigned
            // deflection magnitude [0..1]. Inner/outer is a bool
            // construct, so Invert is consumed as that selector, never
            // as a sign (InvertConsumedByHalfAxisRead).
            if (IsStickRingDescriptor(s))
                return ReadStickRingMagnitude(state, s);

            if (!TryParseTypeIndex(s, out var t, out int idx, out string povDir))
                return 0f;

            float axisValue;
            switch (t)
            {
                case SourceType.Button:
                    return (idx >= 0 && idx < state.Buttons.Length && state.Buttons[idx]) ? 1f : 0f;

                case SourceType.Axis:
                    if (idx < 0 || idx >= CustomInputState.MaxAxis) return 0f;
                    int av = state.Axis[idx];
                    if (src.HalfAxis)
                    {
                        // Half selection mirrors ReadButtonLikeBool's contract
                        // for the same flags: default = upper half only,
                        // Invert = lower half only, Bidirectional = either
                        // side. The active half ranges to [0, +1]; the other
                        // half reads 0. (The old read folded BOTH halves to
                        // positive magnitude and left Invert to the evaluator,
                        // so the same row selected one half as a button but
                        // fired on both directions as an analog source.)
                        // Invert is consumed HERE as the half selector. See
                        // InvertConsumedByHalfAxisRead in the evaluators.
                        int delta = av - 32768;
                        if (src.Bidirectional)
                            axisValue = Math.Min(1f, Math.Abs(delta) / 32767f);
                        else if (src.Invert)
                            axisValue = delta < 0 ? Math.Min(1f, -delta / 32767f) : 0f;
                        else
                            axisValue = delta > 0 ? Math.Min(1f, delta / 32767f) : 0f;
                    }
                    else
                        axisValue = Math.Max(-1f, Math.Min(1f, (av - 32768) / 32767f));
                    break;

                case SourceType.Slider:
                    if (idx < 0 || idx >= CustomInputState.MaxSliders) return 0f;
                    axisValue = Math.Max(0f, Math.Min(1f, state.Sliders[idx] / 65535f)) * 2f - 1f;
                    break;

                case SourceType.PovDirection:
                    if (idx < 0 || idx >= CustomInputState.MaxPovs) return 0f;
                    return PovMatches(state.Povs[idx], povDir) ? 1f : 0f;

                default:
                    return 0f;
            }

            // Generic per-source Sensitivity (issue #9): scale the plain
            // analog generic sources (Axis / Slider, including the abstract
            // Gamepad sticks and triggers that canonicalize to them), then
            // re-clamp to the bipolar range. The specialized families (gyro /
            // mouse / IR / touchpad) applied their own sensitivity above and
            // return before reaching here, so there is no double-scaling.
            axisValue *= PerSourceSensitivity(src);
            if (axisValue < -1f) axisValue = -1f;
            else if (axisValue > 1f) axisValue = 1f;
            // Response curve / outer range channel (translator v11): the ONE
            // application seam, deliberately shared with the Sensitivity knob
            // so the specialized families that returned above are never
            // double-shaped and every evaluator lane (Step 3 mapping-set
            // eval, menus steer, the MouseAbs routing) inherits it through
            // EvaluateForBipolarAxisTarget.
            return ApplyCurveRangeShaping(axisValue, src);
        }

        private static float ReadAsUnipolar(CustomInputState state, MappingSource src, int slotIndex, string deviceGuid)
        {
            string s = CanonicalDescriptor(src.Descriptor);
            if (string.IsNullOrEmpty(s)) return 0f;

            // Touchpad-gesture sources: continuous-axis variants use
            // the absolute value of their bipolar reading (a trigger
            // target driven by PinchAxis fires harder as the pinch
            // gets more extreme in either direction); one-shot fires
            // return 0/1.
            // Mouse-gesture pulse, unipolar (issue #200): 0/1.
            if (IsMouseGestureDescriptor(s))
            {
                bool mgFired = MouseGestureFiredProvider?.Invoke(
                    slotIndex, deviceGuid ?? "", ParseMouseGestureName(s)) ?? false;
                return mgFired ? 1f : 0f;
            }

            // Menu-item fire, unipolar (#9 B-17): 0/1.
            if (TryParseMenuItemCached(src, s, out int menuUniId, out int menuUniItem))
            {
                bool menuFired = MenuItemFiredProvider?.Invoke(
                    slotIndex, deviceGuid ?? "", menuUniId, menuUniItem) ?? false;
                return menuFired ? 1f : 0f;
            }

            if (IsTouchpadGestureDescriptor(s))
            {
                if (!TryParseTouchpadGesture(s, out int gPad, out string gName)) return 0f;
                if (IsTouchpadGestureAxis(gName))
                {
                    float v = TouchpadGestureAxisProvider?.Invoke(
                        slotIndex, deviceGuid ?? "", gPad, gName) ?? 0f;
                    return Math.Abs(v);
                }
                bool fired = TouchpadGestureFiredProvider?.Invoke(
                    slotIndex, deviceGuid ?? "", gPad, gName) ?? false;
                return fired ? 1f : 0f;
            }

            if (s.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // Absolute pointer → trigger (#9 B-15): magnitude of the
                // tuned offset-from-center, the IR pointer's unipolar shape.
                if (IsTouchpadPointerDescriptor(s))
                    return Math.Abs(ReadTunedTouchpadPointer(state, src, slotIndex, deviceGuid));
                // Touchpad axis → unipolar: return [0..1] directly (raw finger
                // position; no bipolar centering).
                if (TryReadTouchpadAxisRaw(state, src, s, out float unipolar)) return unipolar;
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
                float tunedRate = ReadTunedGyroRate(state, src, slotIndex, deviceGuid, out int gyroAxis, out var tuning);
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

            if (s.Equals("IR Offscreen", StringComparison.Ordinal))
                return ReadIrOffscreen(state, src, deviceGuid) ? 1f : 0f;

            if (s.StartsWith("IR Pointer ", StringComparison.Ordinal))
                return Math.Abs(ReadTunedIrPointer(state, src, slotIndex, deviceGuid));

            if (s.StartsWith("Balance ", StringComparison.Ordinal))
                return Math.Abs(ReadTunedBalanceBoard(state, src, deviceGuid));

            if (s.Equals("IR Brightness", StringComparison.Ordinal))
                return state.JoyConIrIntensity; // already unipolar 0..1

            if (s.StartsWith("Mouse Motion ", StringComparison.Ordinal))
            {
                // Motion-as-trigger (issue #154): speed = pull, direction-blind
                // by default. HalfAxis selects ONE direction (Invert picks
                // which), so "up movement presses the trigger 0-100%" is a
                // HalfAxis+Invert row on Mouse Motion Y, per the issue's
                // driving use case.
                float mv = ReadJoyCon2MouseMotion(state, src);
                if (src.HalfAxis) return Math.Max(0f, src.Invert ? -mv : mv);
                return Math.Abs(mv);
            }

            // Stick ring as a trigger pull (v17): the unsigned deflection
            // magnitude [0..1], the bipolar read's twin.
            if (IsStickRingDescriptor(s))
                return ReadStickRingMagnitude(state, s);

            if (!TryParseTypeIndex(s, out var t, out int idx, out string povDir))
                return 0f;

            float axisValue;
            switch (t)
            {
                case SourceType.Button:
                    return (idx >= 0 && idx < state.Buttons.Length && state.Buttons[idx]) ? 1f : 0f;

                case SourceType.Axis:
                    if (idx < 0 || idx >= CustomInputState.MaxAxis) return 0f;
                    int av = state.Axis[idx];
                    if (src.HalfAxis)
                    {
                        // Half-axis trigger: one half of the centered axis
                        // drives the pull (rest = 0, full deflection that way
                        // = 1). Half selection mirrors ReadButtonLikeBool:
                        // default = upper half, Invert = lower half,
                        // Bidirectional = either side. (The old read folded
                        // both halves positive, so the trigger fired on both
                        // directions, and the evaluator's 1-raw Invert
                        // transform on top read full-pressed at rest.)
                        // Invert is consumed HERE as the half selector. See
                        // InvertConsumedByHalfAxisRead in the evaluators.
                        int delta = av - 32768;
                        if (src.Bidirectional)
                            axisValue = Math.Min(1f, Math.Abs(delta) / 32767f);
                        else if (src.Invert)
                            axisValue = delta < 0 ? Math.Min(1f, -delta / 32767f) : 0f;
                        else
                            axisValue = delta > 0 ? Math.Min(1f, delta / 32767f) : 0f;
                    }
                    else
                    {
                        // Trigger axes are unipolar 0..65535 with 0 = released
                        // (matches the legacy MapToTriggerSingle clamp). Stick
                        // axes mapped to triggers without HalfAxis sit at ~50 %
                        // at rest, same as legacy. Users who want a clean
                        // stick-to-trigger map opt in via HalfAxis.
                        axisValue = Math.Max(0f, Math.Min(1f, av / 65535f));
                    }
                    break;

                case SourceType.Slider:
                    if (idx < 0 || idx >= CustomInputState.MaxSliders) return 0f;
                    axisValue = Math.Max(0f, Math.Min(1f, state.Sliders[idx] / 65535f));
                    break;

                case SourceType.PovDirection:
                    if (idx < 0 || idx >= CustomInputState.MaxPovs) return 0f;
                    return PovMatches(state.Povs[idx], povDir) ? 1f : 0f;

                default:
                    return 0f;
            }

            // Generic per-source Sensitivity (issue #9): scale the plain
            // analog generic sources (Axis / Slider, including the abstract
            // Gamepad sticks and triggers that canonicalize to them), then
            // re-clamp to the unipolar range. Specialized families returned
            // above with their own sensitivity, so there is no double-scaling.
            axisValue *= PerSourceSensitivity(src);
            if (axisValue < 0f) axisValue = 0f;
            else if (axisValue > 1f) axisValue = 1f;
            return axisValue;
        }

        // ─── Descriptor helpers ────────────────────────────────────────

        // Parse results memoized by the descriptor STRING. Descriptors are
        // parsed on every source evaluation, and at the ~1 kHz poll rate the
        // Split + ToLowerInvariant + token strings amounted to well over a
        // hundred thousand allocations per second sustained (profiled cost
        // model, 2026-07-16). Keys are immutable strings, so no invalidation
        // exists: an edited mapping carries a different string and simply
        // adds an entry. Capped: descriptors arrive from imported profiles
        // too, and an uncapped cache would root every distinct key until
        // process exit; past the cap, descriptors still parse, they just
        // stop being remembered. Index parsing is invariant so the memoized
        // result cannot depend on whichever culture parsed it first.
        private const int TypeIndexCacheCap = 4096;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (SourceType T, int Index, string PovDir, bool Ok)>
            s_typeIndexCache = new(StringComparer.Ordinal);

        private static bool TryParseTypeIndex(string s, out SourceType t, out int index, out string povDir)
        {
            if (!s_typeIndexCache.TryGetValue(s, out var hit))
            {
                hit = ParseTypeIndexUncached(s);
                if (s_typeIndexCache.Count < TypeIndexCacheCap)
                    s_typeIndexCache[s] = hit;
            }
            t = hit.T;
            index = hit.Index;
            povDir = hit.PovDir;
            return hit.Ok;
        }

        private static (SourceType T, int Index, string PovDir, bool Ok) ParseTypeIndexUncached(string key)
        {
            string[] parts = key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return (SourceType.Unmapped, 0, null, false);

            var pt = parts[0].ToLowerInvariant() switch
            {
                "button" => SourceType.Button,
                "axis"   => SourceType.Axis,
                "slider" => SourceType.Slider,
                "pov"    => SourceType.PovDirection,
                _        => SourceType.Unmapped,
            };
            if (pt == SourceType.Unmapped) return (SourceType.Unmapped, 0, null, false);
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int idx))
                return (SourceType.Unmapped, 0, null, false);
            string dir = (pt == SourceType.PovDirection && parts.Length >= 3) ? parts[2] : null;
            return (pt, idx, dir, true);
        }

        private static bool PovMatches(int povCentidegrees, string direction)
        {
            // -1 (or any negative) signals POV centered.
            if (povCentidegrees < 0 || string.IsNullOrEmpty(direction)) return false;

            // Normalize to 0..35999.
            int v = ((povCentidegrees % 36000) + 36000) % 36000;
            // Case-insensitive compares instead of ToLowerInvariant: this runs
            // per POV source per tick and the lowercase copy was a per-call
            // allocation on the 1 kHz path.
            if (string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase))
                return v >= 31500 || v <= 4500;        // 315°..360°/0°..45°
            if (string.Equals(direction, "right", StringComparison.OrdinalIgnoreCase))
                return v >= 4500 && v <= 13500;        // 45°..135°
            if (string.Equals(direction, "down", StringComparison.OrdinalIgnoreCase))
                return v >= 13500 && v <= 22500;       // 135°..225°
            if (string.Equals(direction, "left", StringComparison.OrdinalIgnoreCase))
                return v >= 22500 && v <= 31500;       // 225°..315°
            return false;
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
                // Canonical pad-0 click rides Buttons[16] (the slot
                // SdlDeviceWrapper populates from SDL_GAMEPAD_BUTTON_TOUCHPAD).
                // Nonzero pads read their own per-pad Clicked, which the
                // wrapper fills from the fork's touchpad-click-as-button
                // recipe (pad 1 = MISC2 on Deck / SC 2026): Workshop imports
                // emit "Touchpad 1 Click" for right-pad clicks, and the old
                // padIdx!=0 bail made every such row permanently false.
                if (padIdx != 0)
                    return state.Touchpads != null
                        && padIdx < state.Touchpads.Length
                        && state.Touchpads[padIdx] != null
                        && state.Touchpads[padIdx].Clicked;
                if (state.Buttons == null || state.Buttons.Length <= 16) return false;
                return state.Buttons[16];
            }

            // "Touchpad N Finger M Down", plus the region-windowed
            // "... Down Left" / "Down Right" (#9 B-1): contact only while
            // the finger sits in that half of the pad. The windowed forms
            // carry the trackpad-half button groups a Steam config hosts on
            // one half of a single physical pad (B-19) and the mouse_region
            // engage triggers.
            if ((parts.Length == 5 || parts.Length == 6)
                && parts[2].Equals("Finger", StringComparison.Ordinal)
                && parts[4].Equals("Down", StringComparison.Ordinal))
            {
                int half = TouchpadHalfNone;
                if (parts.Length == 6)
                {
                    half = ParseTouchpadHalf(parts[5]);
                    if (half == TouchpadHalfNone) return false;
                }
                if (!int.TryParse(parts[3], out int fingerIdx)) return false;
                var pad = GetTouchpad(state, padIdx);
                if (pad == null) return false;
                if (fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return false;
                return pad.FingerDown[fingerIdx]
                    && FingerInTouchpadHalf(pad, fingerIdx, half);
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
            // Poll-frame gate: the delta is (current - previous) with
            // previous updated on compute, so the first reader consumed the
            // movement and a second row on the same (slot, finger, axis)
            // read a permanent ~0. Compute once per poll, re-serve within it.
            public ulong FrameSeq;
            public float FrameDelta;
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
            if (!TryParseTouchpadAxis(descriptor, out int padIdx, out int fingerIdx, out int axisOffset, out int half))
                return false;
            var pad = GetTouchpad(state, padIdx);
            if (pad == null) return false;
            if (fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return false;

            string deviceGuid = src?.DeviceGuid ?? string.Empty;
            // Key includes slotIndex so two slots sharing one physical touchpad
            // track their own previous-frame position, matching the slot-keyed
            // TouchpadMouseSettingsProvider lookup below. Without it the slot
            // evaluated first each frame overwrites PrevValue and the second
            // slot reads a zero delta. The half window joins the key so a
            // "X Left" row and a whole-pad "X" row on the same finger keep
            // independent previous-frame state (#9 B-1).
            string key = slotIndex + "|" + deviceGuid + "|" + padIdx + "|" + fingerIdx + "|" + axisOffset + "|" + half;

            // Lifted finger → reset delta tracker, return 0. A finger
            // outside the descriptor's half window gates the same way
            // (#9 B-1, "relative-delta reads gate per-sample"): no delta is
            // produced while outside, and dropping the tracker entry makes
            // re-entry seed fresh, so crossing back into the half never
            // manufactures a jump.
            if (!pad.FingerDown[fingerIdx] || !FingerInTouchpadHalf(pad, fingerIdx, half))
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

            // X / Y → delta from previous poll. Seed on first contact.
            var prev = _touchpadDeltas.GetOrAdd(key, _ => new TouchpadAxisDelta { PrevValue = raw, Seeded = false });
            if (!prev.Seeded)
            {
                _touchpadDeltas[key] = new TouchpadAxisDelta { PrevValue = raw, Seeded = true, FrameSeq = _pollFrameSeq };
                return true; // bipolar 0 on the seed frame
            }
            float delta;
            if (prev.FrameSeq == _pollFrameSeq)
            {
                // Second row on this (slot, finger, axis) in the same poll:
                // re-serve the delta instead of consuming it to ~0.
                delta = prev.FrameDelta;
            }
            else
            {
                delta = raw - prev.PrevValue;
                _touchpadDeltas[key] = new TouchpadAxisDelta
                {
                    PrevValue = raw,
                    Seeded = true,
                    FrameSeq = _pollFrameSeq,
                    FrameDelta = delta,
                };
            }

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
            // Generic per-source Sensitivity (#9 B-13) multiplies the delta
            // on top of the slot-level Touchpad-tab tuning, so per-row touch
            // sensitivity from a workshop config (or the row's slider) acts
            // exactly here, before the same clamp the base read had. A delta
            // under a positive multiplier is sign-neutral, and the != 1 guard
            // keeps the default bit-identical to the unscaled read.
            float rowSens = src != null ? PerSourceSensitivity(src) : 1f;
            if (rowSens != 1f) bipolar *= rowSens;
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
        /// [0..1] without recentering. Pressure isn't a signed axis.
        /// Returns 0 when the finger is not in contact (the caller's
        /// gating wrapper usually filters us out first, but this is
        /// the right defensive default).
        /// <para>Generic per-source Sensitivity (#9 B-13) scales the
        /// recentered X/Y position (deviation-from-center, the same
        /// origin the bipolar Axis leg uses) so the slider the widened
        /// predicate reveals is live on stick / passthrough targets
        /// too, not only on the mouse-delta path. Pressure is never
        /// scaled. 1.0 stays bit-identical.</para></summary>
        private static bool TryReadTouchpadAxisAbsolute(CustomInputState state, MappingSource src, string descriptor, out float bipolar)
        {
            bipolar = 0f;
            if (!TryParseTouchpadAxis(descriptor, out int padIdx, out int fingerIdx, out int axisOffset, out int half))
                return false;
            var pad = GetTouchpad(state, padIdx);
            if (pad == null) return false;
            if (fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return false;
            if (!pad.FingerDown[fingerIdx]) return true; // bipolar already 0
            // Half-windowed source with the finger outside its half:
            // neutral, exactly like a lifted finger (#9 B-1).
            if (!FingerInTouchpadHalf(pad, fingerIdx, half)) return true;
            float raw = axisOffset switch
            {
                0 => pad.FingerX[fingerIdx],
                1 => pad.FingerY[fingerIdx],
                2 => pad.FingerPressure[fingerIdx],
                _ => 0f
            };
            if (raw < 0f) raw = 0f; else if (raw > 1f) raw = 1f;
            if (axisOffset == 2)
            {
                bipolar = raw;
                return true;
            }
            // Windowed X re-normalizes its half to the full range so the
            // half behaves as a complete miniature pad (#9 B-1); Y and
            // whole-pad X pass through.
            raw = RenormalizeTouchpadHalf(raw, axisOffset, half);
            bipolar = raw * 2f - 1f;
            float rowSens = src != null ? PerSourceSensitivity(src) : 1f;
            if (rowSens != 1f)
            {
                bipolar *= rowSens;
                if (bipolar < -1f) bipolar = -1f;
                else if (bipolar > 1f) bipolar = 1f;
            }
            return true;
        }

        /// <summary>Returns finger position as unipolar [0..1]. Used by
        /// ReadAsUnipolar so a touchpad axis feeding a trigger target reads
        /// the raw position. Returns 0 when the finger is not in contact.
        /// <para>Generic per-source Sensitivity (#9 B-13) scales the X/Y
        /// position magnitude-from-zero, the same origin the unipolar
        /// Slider leg uses, then re-clamps. Pressure is never scaled.
        /// 1.0 stays bit-identical.</para></summary>
        private static bool TryReadTouchpadAxisRaw(CustomInputState state, MappingSource src, string descriptor, out float unipolar)
        {
            unipolar = 0f;
            if (!TryParseTouchpadAxis(descriptor, out int padIdx, out int fingerIdx, out int axisOffset, out int half))
                return false;
            var pad = GetTouchpad(state, padIdx);
            if (pad == null) return false;
            if (fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return false;
            if (!pad.FingerDown[fingerIdx]) return true; // unipolar already 0
            // Outside the descriptor's half window: neutral (#9 B-1).
            if (!FingerInTouchpadHalf(pad, fingerIdx, half)) return true;
            float raw = axisOffset switch
            {
                0 => pad.FingerX[fingerIdx],
                1 => pad.FingerY[fingerIdx],
                2 => pad.FingerPressure[fingerIdx],
                _ => 0f
            };
            if (raw < 0f) raw = 0f; else if (raw > 1f) raw = 1f;
            raw = RenormalizeTouchpadHalf(raw, axisOffset, half);
            unipolar = raw;
            if (axisOffset != 2)
            {
                float rowSens = src != null ? PerSourceSensitivity(src) : 1f;
                if (rowSens != 1f)
                {
                    unipolar *= rowSens;
                    if (unipolar < 0f) unipolar = 0f;
                    else if (unipolar > 1f) unipolar = 1f;
                }
            }
            return true;
        }

        /// <summary>Parses "Touchpad N Finger M X" / "...Y" / "...Pressure",
        /// plus the region-windowed half variants "... X Left" / "X Right" /
        /// "Y Left" / "Y Right" (#9 B-1: Steam splits a single physical
        /// trackpad into left/right halves; the windowed source reads the
        /// finger coordinate only while the finger is in that half).
        /// <paramref name="axisOffset"/> = 0 for X, 1 for Y, 2 for Pressure.
        /// <paramref name="half"/> = <see cref="TouchpadHalfNone"/> for the
        /// classic whole-pad form, <see cref="TouchpadHalfLeft"/> /
        /// <see cref="TouchpadHalfRight"/> for the windowed forms. Pressure
        /// has no windowed variant (the halves model Steam's surface split,
        /// and pressure is a physical magnitude, not a position).
        /// Returns false for "Click" / "Down" / unrecognized formats.</summary>
        private static bool TryParseTouchpadAxis(string descriptor,
            out int padIdx, out int fingerIdx, out int axisOffset, out int half)
        {
            padIdx = 0; fingerIdx = 0; axisOffset = -1; half = TouchpadHalfNone;
            string[] parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // Expected: "Touchpad N Finger M X|Y|Pressure" (5 parts) or
            // "Touchpad N Finger M X|Y Left|Right" (6 parts).
            if (parts.Length != 5 && parts.Length != 6) return false;
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
            if (axisOffset < 0) return false;
            if (parts.Length == 6)
            {
                if (axisOffset == 2) return false; // no windowed Pressure
                half = ParseTouchpadHalf(parts[5]);
                return half != TouchpadHalfNone;
            }
            return true;
        }

        // ─── Touchpad half windows (#9 B-1) ────────────────────────────
        //
        // Steam Input splits a single physical trackpad (DualShock 4 /
        // DualSense: SDL registers exactly ONE touchpad,
        // SDL_hidapi_ps4.c:732 / SDL_hidapi_ps5.c:846) into left_trackpad /
        // right_trackpad halves. The windowed descriptors mirror that: the
        // source is live only while the finger sits in its half (X < 0.5 =
        // Left, X >= 0.5 = Right), absolute X reads re-normalize the half to
        // the full range, and the relative-delta read gates per sample.

        internal const int TouchpadHalfNone = 0;
        internal const int TouchpadHalfLeft = 1;
        internal const int TouchpadHalfRight = 2;

        private static int ParseTouchpadHalf(string token) => token switch
        {
            "Left"  => TouchpadHalfLeft,
            "Right" => TouchpadHalfRight,
            _       => TouchpadHalfNone,
        };

        /// <summary>True when the finger sits inside the descriptor's half
        /// window (always true for the whole-pad form). The boundary finger
        /// X == 0.5 belongs to the Right half, matching the parse-time
        /// convention documented on <see cref="TryParseTouchpadAxis"/>.</summary>
        private static bool FingerInTouchpadHalf(TouchpadInputState pad, int fingerIdx, int half)
            => half switch
            {
                TouchpadHalfLeft  => pad.FingerX[fingerIdx] < 0.5f,
                TouchpadHalfRight => pad.FingerX[fingerIdx] >= 0.5f,
                _ => true,
            };

        /// <summary>Re-normalizes a raw finger X inside a half window to the
        /// full [0..1] range ("absolute reads clamp to the half
        /// re-normalized"): Left maps [0..0.5] onto [0..1], Right maps
        /// [0.5..1]. Y (and whole-pad X) pass through unchanged, because the
        /// halves split the pad horizontally, so a windowed Y spans the full
        /// pad height.</summary>
        private static float RenormalizeTouchpadHalf(float raw, int axisOffset, int half)
        {
            if (half == TouchpadHalfNone || axisOffset != 0) return raw;
            float v = half == TouchpadHalfLeft ? raw * 2f : (raw - 0.5f) * 2f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        // ─── Absolute touchpad pointer (#9 B-15) ───────────────────────
        //
        // "Touchpad N Pointer X|Y[ Left|Right]": finger 0's ABSOLUTE
        // position on the pad (or on a region-windowed half), the
        // touchpad twin of the Wii "IR Pointer X/Y" family. On a
        // KbmMouseX/Y row Step 3 routes it to KbmRawState.MouseAbs*
        // (SetCursorPos, the Touchmote idiom) instead of the delta lane,
        // so touching the pad warps the cursor to the matching screen
        // position. Steam's construct with the same semantics is the
        // mouse_region group mode ("treats the pad as a 1:1 map to
        // screen space, so touching a particular place on the pad will
        // always put the cursor in the same place on the screen",
        // Steamworks Input Source Modes doc). Finger 0 only: the first
        // contact owns the pointer, matching the finger the translator's
        // relative-mouse rows read.

        /// <summary>True for the absolute-pointer descriptors
        /// <c>"Touchpad N Pointer X/Y"</c> plus the region-windowed
        /// half forms <c>"... X Left"</c> etc. (#9 B-15).</summary>
        public static bool IsTouchpadPointerDescriptor(string descriptor)
            => TryParseTouchpadPointer(descriptor, out _, out _, out _);

        /// <summary>Parses <c>"Touchpad N Pointer X|Y"</c> (4 parts) or
        /// <c>"Touchpad N Pointer X|Y Left|Right"</c> (5 parts).
        /// <paramref name="axisOffset"/> = 0 for X, 1 for Y. The half
        /// window gates ENGAGEMENT on both axes (a "Y Right" source is
        /// live only while the finger sits in the right half) and
        /// re-normalizes X reads exactly like the Finger family
        /// (<see cref="RenormalizeTouchpadHalf"/>). No finger index in
        /// the grammar: the pointer always follows finger 0.</summary>
        public static bool TryParseTouchpadPointer(string descriptor,
            out int padIdx, out int axisOffset, out int half)
        {
            padIdx = 0; axisOffset = -1; half = TouchpadHalfNone;
            if (string.IsNullOrEmpty(descriptor)) return false;
            // Cheap reject before Split: this predicate runs per poll for every
            // source on a KbM mouse row (FindEngagedTouchpadPointerSource), and
            // the common gyro / stick -> mouse sources are not "Touchpad ..."
            // at all. Splitting them just to fail the parts[0] check below
            // allocated a string[] on the 1 kHz path for every such config.
            if (!descriptor.StartsWith("Touchpad ", StringComparison.Ordinal)) return false;
            string[] parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 && parts.Length != 5) return false;
            if (!parts[0].Equals("Touchpad", StringComparison.Ordinal)) return false;
            if (!int.TryParse(parts[1], out padIdx)) return false;
            if (!parts[2].Equals("Pointer", StringComparison.Ordinal)) return false;
            axisOffset = parts[3] switch
            {
                "X" => 0,
                "Y" => 1,
                _   => -1,
            };
            if (axisOffset < 0) return false;
            if (parts.Length == 5)
            {
                half = ParseTouchpadHalf(parts[4]);
                return half != TouchpadHalfNone;
            }
            return true;
        }

        /// <summary>True while the pointer descriptor's finger 0 is in
        /// contact inside the descriptor's half window. Step 3 gates
        /// <c>KbmRawState.MouseAbsValid</c> on this, so a lifted finger
        /// (or one outside the window) FREEZES the cursor at its last
        /// position instead of recentering, the same convention the Wii
        /// pointer applies on sight loss (and Steam's default mouse_region
        /// behavior with teleport_stop off).</summary>
        public static bool IsTouchpadPointerEngaged(CustomInputState state, string descriptor)
        {
            if (state == null) return false;
            if (!TryParseTouchpadPointer(descriptor, out int padIdx, out _, out int half)) return false;
            var pad = GetTouchpad(state, padIdx);
            if (pad == null || pad.MaxFingers <= 0) return false;
            return pad.FingerDown[0] && FingerInTouchpadHalf(pad, 0, half);
        }

        /// <summary>Reads the absolute touchpad pointer axis (#9 B-15) as
        /// bipolar [-1..+1]: window-renormalized finger-0 position, then the
        /// per-(slot, device, pad) margin stretch, then the per-source region
        /// window. Returns 0 (center) when no finger is engaged; the caller's
        /// validity gate keeps that value from driving the cursor.
        /// <para>Order of operations, each in its own space:</para>
        /// <list type="number">
        /// <item>Half renormalize: pad fraction [0..1] within the window
        /// (<see cref="RenormalizeTouchpadHalf"/>).</item>
        /// <item>Margin stretch (pad space, around the pad center):
        /// <c>0.5 + (v - 0.5) * stretch</c>, clamped. The Wii aim map ships
        /// the same concept as IrMarginStretchX/Y because tracked aim cannot
        /// reach the camera edges; on a touchpad the finger CAN reach the
        /// edges, so the default is 1.0 (Steam's 1:1 mouse_region map) and
        /// the Touchpad-tab knob raises it for thumbs that stop short of the
        /// bezel. Per (slot, device, pad) via
        /// <see cref="TouchpadMouseSettingsProvider"/>
        /// (<see cref="PadForge.Engine.Touchpad.TouchpadGestureSettings.PointerStretchX"/>/Y),
        /// looked up with the EFFECTIVE device guid (the IR pointer's
        /// convention) so translated empty-guid rows read the assigned
        /// device's tuning.</item>
        /// <item>Region window (screen space, per source):
        /// <c>(2*ParamPointerCenter - 1) + v * ParamPointerExtent</c>, the
        /// translator's channel for Steam mouse_region position_x/position_y
        /// (region center, percent of screen) and scale x
        /// sensitivity_horiz/vert_scale (region extent). Defaults 0.5 / 1.0
        /// are the identity full-screen map.</item>
        /// </list>
        /// Invert is applied by the public Evaluate* wrappers, matching the
        /// IR pointer path.</summary>
        private static float ReadTunedTouchpadPointer(CustomInputState state, MappingSource src,
            int slotIndex, string deviceGuid)
        {
            if (src == null || state == null) return 0f;
            if (!TryParseTouchpadPointer(src.Descriptor ?? "", out int padIdx, out int axisOffset, out int half))
                return 0f;
            var pad = GetTouchpad(state, padIdx);
            if (pad == null || pad.MaxFingers <= 0) return 0f;
            if (!pad.FingerDown[0] || !FingerInTouchpadHalf(pad, 0, half))
                return 0f; // not engaged; Step 3's validity gate freezes the cursor

            float raw = axisOffset == 0 ? pad.FingerX[0] : pad.FingerY[0];
            if (raw < 0f) raw = 0f; else if (raw > 1f) raw = 1f;
            raw = RenormalizeTouchpadHalf(raw, axisOffset, half);

            var tp = TouchpadMouseSettingsProvider?.Invoke(slotIndex, deviceGuid ?? "", padIdx);
            float stretch = axisOffset == 0
                ? (tp?.PointerStretchX ?? 1.0f)
                : (tp?.PointerStretchY ?? 1.0f);
            if (stretch != 1f)
            {
                raw = 0.5f + (raw - 0.5f) * stretch;
                if (raw < 0f) raw = 0f; else if (raw > 1f) raw = 1f;
            }

            float v = raw * 2f - 1f;
            float center = (float)src.ParamPointerCenter * 2f - 1f;
            float extent = (float)src.ParamPointerExtent;
            v = center + v * extent;
            if (v < -1f) v = -1f;
            else if (v > 1f) v = 1f;
            return v;
        }
    }
}
