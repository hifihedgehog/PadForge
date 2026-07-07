using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Contains the complete mapping configuration for a device-to-slot assignment.
    /// All mapping properties are string-typed descriptors in the format used by
    /// the InputManager Step 3 mapping engine:
    ///   "Button N", "Axis N", "IHAxis N", "POV N Dir", "Slider N", or "" (unmapped).
    /// 
    /// PadSettings are stored separately from UserSettings and linked via
    /// <see cref="PadSettingChecksum"/>. Multiple UserSettings can share the same
    /// PadSetting when devices use identical mappings.
    /// 
    /// Numeric settings (deadzones, gains) are stored as strings for XML
    /// serialization consistency with the original format.
    /// </summary>
    public partial class PadSetting
    {
        // ─────────────────────────────────────────────
        //  Identity
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checksum computed from all mapping/setting properties.
        /// Used to link UserSettings to PadSettings and to detect duplicates.
        /// </summary>
        [XmlElement]
        public string PadSettingChecksum { get; set; } = string.Empty;

        // ─────────────────────────────────────────────
        //  Button mappings
        // ─────────────────────────────────────────────

        [XmlElement] public string ButtonA { get; set; } = "";
        [XmlElement] public string ButtonB { get; set; } = "";
        [XmlElement] public string ButtonX { get; set; } = "";
        [XmlElement] public string ButtonY { get; set; } = "";

        [XmlElement] public string LeftShoulder { get; set; } = "";
        [XmlElement] public string RightShoulder { get; set; } = "";

        [XmlElement] public string ButtonBack { get; set; } = "";
        [XmlElement] public string ButtonStart { get; set; } = "";
        [XmlElement] public string ButtonGuide { get; set; } = "";

        /// <summary>Xbox Series Share button. Only surfaced on Xbox Series
        /// virtual-controller profiles; HM drops the bit on profiles whose
        /// descriptor doesn't declare button 13.</summary>
        [XmlElement] public string ButtonShare { get; set; } = "";

        [XmlElement] public string LeftThumbButton { get; set; } = "";
        [XmlElement] public string RightThumbButton { get; set; } = "";

        // ─────────────────────────────────────────────
        //  D-Pad mappings
        // ─────────────────────────────────────────────

        /// <summary>
        /// Combined D-Pad mapping. If set to a POV descriptor (e.g., "POV 0"),
        /// all four directions are automatically extracted. Individual DPadUp/Down/
        /// Left/Right override this when set.
        /// </summary>
        [XmlElement] public string DPad { get; set; } = "";

        [XmlElement] public string DPadUp { get; set; } = "";
        [XmlElement] public string DPadDown { get; set; } = "";
        [XmlElement] public string DPadLeft { get; set; } = "";
        [XmlElement] public string DPadRight { get; set; } = "";

        // ─────────────────────────────────────────────
        //  Trigger mappings
        // ─────────────────────────────────────────────

        [XmlElement] public string LeftTrigger { get; set; } = "";
        [XmlElement] public string RightTrigger { get; set; } = "";

        /// <summary>
        /// Dead zone for the left trigger (0–100). Values below this
        /// percentage of the trigger range are treated as zero.
        /// </summary>
        [XmlElement] public string LeftTriggerDeadZone { get; set; } = "0";

        /// <summary>
        /// Dead zone for the right trigger (0–100).
        /// </summary>
        [XmlElement] public string RightTriggerDeadZone { get; set; } = "0";

        /// <summary>
        /// Anti-deadzone for the left trigger (0–100%). Offsets the output range minimum
        /// so small physical presses register past the game's built-in trigger deadzone.
        /// </summary>
        [XmlElement] public string LeftTriggerAntiDeadZone { get; set; } = "0";

        /// <summary>Anti-deadzone for the right trigger (0–100%).</summary>
        [XmlElement] public string RightTriggerAntiDeadZone { get; set; } = "0";

        /// <summary>
        /// Max range for the left trigger (1–100%). Caps the output ceiling so full
        /// physical press maps to this percentage of the output range.
        /// </summary>
        [XmlElement] public string LeftTriggerMaxRange { get; set; } = "100";

        /// <summary>Max range for the right trigger (1–100%).</summary>
        [XmlElement] public string RightTriggerMaxRange { get; set; } = "100";

        // ─────────────────────────────────────────────
        //  Thumbstick axis mappings
        // ─────────────────────────────────────────────

        [XmlElement] public string LeftThumbAxisX { get; set; } = "";
        [XmlElement] public string LeftThumbAxisY { get; set; } = "";
        [XmlElement] public string RightThumbAxisX { get; set; } = "";
        [XmlElement] public string RightThumbAxisY { get; set; } = "";

        /// <summary>Negative-direction descriptor for stick axes (used when buttons map to bidirectional axes).</summary>
        [XmlElement] public string LeftThumbAxisXNeg { get; set; } = "";
        [XmlElement] public string LeftThumbAxisYNeg { get; set; } = "";
        [XmlElement] public string RightThumbAxisXNeg { get; set; } = "";
        [XmlElement] public string RightThumbAxisYNeg { get; set; } = "";

        // ─────────────────────────────────────────────
        //  Dead zone settings
        // ─────────────────────────────────────────────

        /// <summary>Left stick deadzone X (0–100%).</summary>
        [XmlElement] public string LeftThumbDeadZoneX { get; set; } = "0";

        /// <summary>Left stick deadzone Y (0–100%).</summary>
        [XmlElement] public string LeftThumbDeadZoneY { get; set; } = "0";

        /// <summary>Right stick deadzone X (0–100%).</summary>
        [XmlElement] public string RightThumbDeadZoneX { get; set; } = "0";

        /// <summary>Right stick deadzone Y (0–100%).</summary>
        [XmlElement] public string RightThumbDeadZoneY { get; set; } = "0";

        /// <summary>Left stick deadzone shape (DeadZoneShape enum). Default 2 = ScaledRadial.</summary>
        [XmlElement] public string LeftThumbDeadZoneShape { get; set; } = "2";

        /// <summary>Right stick deadzone shape (DeadZoneShape enum). Default 2 = ScaledRadial.</summary>
        [XmlElement] public string RightThumbDeadZoneShape { get; set; } = "2";

        /// <summary>
        /// Left stick anti-deadzone (0–100%). Offsets the output range minimum
        /// so small physical movements register past the game's built-in deadzone.
        /// </summary>
        [XmlElement] public string LeftThumbAntiDeadZone { get; set; } = "0";

        /// <summary>Right stick anti-deadzone (0–100%). Legacy unified property — use per-axis X/Y instead.</summary>
        [XmlElement] public string RightThumbAntiDeadZone { get; set; } = "0";

        /// <summary>Left stick anti-deadzone X axis (0–100%).</summary>
        [XmlElement] public string LeftThumbAntiDeadZoneX { get; set; } = "0";

        /// <summary>Left stick anti-deadzone Y axis (0–100%).</summary>
        [XmlElement] public string LeftThumbAntiDeadZoneY { get; set; } = "0";

        /// <summary>Right stick anti-deadzone X axis (0–100%).</summary>
        [XmlElement] public string RightThumbAntiDeadZoneX { get; set; } = "0";

        /// <summary>Right stick anti-deadzone Y axis (0–100%).</summary>
        [XmlElement] public string RightThumbAntiDeadZoneY { get; set; } = "0";

        /// <summary>
        /// Left stick linear response curve (0–100%). 0 = default, 100 = fully linear.
        /// </summary>
        [XmlElement] public string LeftThumbLinear { get; set; } = "0";

        /// <summary>Right stick linear response curve (0–100%).</summary>
        [XmlElement] public string RightThumbLinear { get; set; } = "0";

        /// <summary>Left stick X-axis sensitivity curve (-100 to 100). 0 = linear, +100 = exponential, -100 = logarithmic.</summary>
        [XmlElement] public string LeftThumbSensitivityCurveX { get; set; } = "0";
        /// <summary>Left stick Y-axis sensitivity curve (-100 to 100).</summary>
        [XmlElement] public string LeftThumbSensitivityCurveY { get; set; } = "0";

        /// <summary>Right stick X-axis sensitivity curve (-100 to 100).</summary>
        [XmlElement] public string RightThumbSensitivityCurveX { get; set; } = "0";
        /// <summary>Right stick Y-axis sensitivity curve (-100 to 100).</summary>
        [XmlElement] public string RightThumbSensitivityCurveY { get; set; } = "0";

        /// <summary>Left trigger sensitivity curve (-100 to 100).</summary>
        [XmlElement] public string LeftTriggerSensitivityCurve { get; set; } = "0";

        /// <summary>Right trigger sensitivity curve (-100 to 100).</summary>
        [XmlElement] public string RightTriggerSensitivityCurve { get; set; } = "0";

        /// <summary>Left stick X max range (1–100%). Full physical deflection maps to this ceiling.</summary>
        [XmlElement] public string LeftThumbMaxRangeX { get; set; } = "100";

        /// <summary>Left stick Y max range (1–100%).</summary>
        [XmlElement] public string LeftThumbMaxRangeY { get; set; } = "100";

        /// <summary>Right stick X max range (1–100%).</summary>
        [XmlElement] public string RightThumbMaxRangeX { get; set; } = "100";

        /// <summary>Right stick Y max range (1–100%).</summary>
        [XmlElement] public string RightThumbMaxRangeY { get; set; } = "100";

        // Per-direction (negative) max range. Null = inherit from symmetric property above.
        /// <summary>Left stick X negative direction (left) max range (1–100%).</summary>
        [XmlElement] public string LeftThumbMaxRangeXNeg { get; set; }

        /// <summary>Left stick Y negative direction (down) max range (1–100%).</summary>
        [XmlElement] public string LeftThumbMaxRangeYNeg { get; set; }

        /// <summary>Right stick X negative direction (left) max range (1–100%).</summary>
        [XmlElement] public string RightThumbMaxRangeXNeg { get; set; }

        /// <summary>Right stick Y negative direction (down) max range (1–100%).</summary>
        [XmlElement] public string RightThumbMaxRangeYNeg { get; set; }

        // ─────────────────────────────────────────────
        //  Stick center offset calibration
        // ─────────────────────────────────────────────

        /// <summary>Left stick X center offset (-100 to 100%). Corrects stick drift before deadzone.</summary>
        [XmlElement] public string LeftThumbCenterOffsetX { get; set; } = "0";

        /// <summary>Left stick Y center offset (-100 to 100%).</summary>
        [XmlElement] public string LeftThumbCenterOffsetY { get; set; } = "0";

        /// <summary>Right stick X center offset (-100 to 100%).</summary>
        [XmlElement] public string RightThumbCenterOffsetX { get; set; } = "0";

        /// <summary>Right stick Y center offset (-100 to 100%).</summary>
        [XmlElement] public string RightThumbCenterOffsetY { get; set; } = "0";

        // ─────────────────────────────────────────────
        //  Stick boundary calibration (#174)
        // ─────────────────────────────────────────────

        /// <summary>Left stick measured boundary map: space-separated radii
        /// (per angle) scaled by 100. Empty = uncalibrated, no reshaping.</summary>
        [XmlElement] public string LeftThumbBoundaryMap { get; set; } = "";

        /// <summary>Right stick measured boundary map. Empty = uncalibrated.</summary>
        [XmlElement] public string RightThumbBoundaryMap { get; set; } = "";

        // ─────────────────────────────────────────────
        //  Force feedback settings
        // ─────────────────────────────────────────────

        /// <summary>
        /// Force feedback type.
        /// 0 = Off, 1 = SDL Rumble (default for most controllers).
        /// </summary>
        [XmlElement] public string ForceType { get; set; } = "1";

        /// <summary>
        /// Overall force feedback strength (0–100%).
        /// Applied as a multiplier to both motors.
        /// </summary>
        [XmlElement] public string ForceOverall { get; set; } = "100";

        /// <summary>Wheel hardware rotation range in degrees (40–1080). Native
        /// wheel FFB only (Logitech/Fanatec/Thrustmaster), applied via the vendor
        /// HID writer. Default 900.</summary>
        [XmlElement] public string RotationRange { get; set; } = "900";

        /// <summary>Wheel auto-center spring strength (0–100%; 0 = off). Native
        /// wheel FFB only (Logitech/Thrustmaster). Default 0.</summary>
        [XmlElement] public string AutoCenterStrength { get; set; } = "0";

        /// <summary>Drive the wheel's RPM / shift LEDs from the running racing
        /// game's telemetry ("0" = off, "1" = on). Logitech / Fanatec only. The
        /// telemetry source (Forza Data Out, Assetto Corsa shared memory) is
        /// auto-detected. Default off.</summary>
        [XmlElement] public string WheelRpmLeds { get; set; } = "0";

        // ─── Steering at-lock feedback (v3.4 #94) ───
        // Per-slot, opt-in haptic feedback when a steering source (winding / 2D
        // angle-to-axis / motion-lean) saturates at full lock. All off by default.
        /// <summary>Rumble pulse on steering lock entry. "0"/"1".</summary>
        [XmlElement] public string SteeringLockRumbleEnabled       { get; set; } = "0";
        /// <summary>Impulse-trigger pulse on steering lock entry. "0"/"1".</summary>
        [XmlElement] public string SteeringLockTriggerVibEnabled   { get; set; } = "0";
        /// <summary>Lightbar pulse on steering lock entry (DualSense / DS4). "0"/"1".</summary>
        [XmlElement] public string SteeringLockLightbarEnabled     { get; set; } = "0";
        /// <summary>Adaptive-trigger resistance ramp toward lock (DualSense). "0"/"1".</summary>
        [XmlElement] public string SteeringLockATResistanceEnabled { get; set; } = "0";
        /// <summary>Lock-entry rumble / trigger pulse length in ms.</summary>
        [XmlElement] public string SteeringLockPulseMs             { get; set; } = "80";
        /// <summary>Lock lightbar pulse color (hex #RRGGBB), used when the color
        /// source is Fixed.</summary>
        [XmlElement] public string SteeringLockLightbarColor       { get; set; } = "#FF0000";
        /// <summary>Lock lightbar color source: "Fixed" (the color above), "RandomHue"
        /// (a fresh random hue each lock), or "PaletteStep" (advance through the dedicated
        /// steering palette below). Mirrors the macro lightbar's color modes.</summary>
        [XmlElement] public string SteeringLockLightbarColorSource { get; set; } = "Fixed";
        /// <summary>Dedicated palette for the PaletteStep color source — CSV of "RRGGBB"
        /// hex triplets. Used only by the steering lock, never shared with another section.</summary>
        [XmlElement] public string SteeringLockLightbarPaletteCsv  { get; set; } = "";
        /// <summary>Lock lightbar hold length in ms — how long the color holds at full before
        /// the decay fade begins (the lightbar's own hold, separate from the rumble/trigger
        /// pulse length).</summary>
        [XmlElement] public string SteeringLockLightbarHoldMs      { get; set; } = "80";
        /// <summary>Lock lightbar decay (fade-back) length in ms after the hold.</summary>
        [XmlElement] public string SteeringLockLightbarFadeMs      { get; set; } = "250";

        /// <summary>
        /// Whether to swap left and right rumble motors.
        /// "0" = no swap, "1" = swap.
        /// </summary>
        [XmlElement] public string ForceSwapMotor { get; set; } = "0";

        /// <summary>
        /// Left (low-frequency) motor strength (0–100%).
        /// </summary>
        [XmlElement] public string LeftMotorStrength { get; set; } = "100";

        /// <summary>
        /// Right (high-frequency) motor strength (0–100%).
        /// </summary>
        [XmlElement] public string RightMotorStrength { get; set; } = "100";

        /// <summary>
        /// Overall impulse-trigger gain (0–100%, Xbox One+ controllers).
        /// Multiplied with per-trigger strength before reaching the
        /// physical motor — mirrors <see cref="ForceOverall"/>.
        /// </summary>
        [XmlElement] public string ImpulseOverallGain { get; set; } = "100";

        /// <summary>
        /// Left impulse trigger motor strength (0–100%, Xbox One+ controllers).
        /// </summary>
        [XmlElement] public string ImpulseLeftStrength { get; set; } = "100";

        /// <summary>
        /// Right impulse trigger motor strength (0–100%, Xbox One+ controllers).
        /// </summary>
        [XmlElement] public string ImpulseRightStrength { get; set; } = "100";

        /// <summary>
        /// Whether to swap left and right impulse trigger motors.
        /// "0" = no swap, "1" = swap.
        /// </summary>
        [XmlElement] public string ImpulseSwapTriggers { get; set; } = "0";

        /// <summary>Enable constant-trigger-force override (Xbox One+).
        /// "0" = off (default), "1" = on. Mirrors
        /// <see cref="ConstantForceEnabled"/> for impulse-trigger
        /// motors with the same override-with-resume semantics: when
        /// game/macro trigger rumble is silent the user-set
        /// <see cref="ConstantTriggerForceLeft"/> /
        /// <see cref="ConstantTriggerForceRight"/> values drive the
        /// motors; on the next non-zero game-trigger tick the
        /// constant force pauses.</summary>
        [XmlElement] public string ConstantTriggerForceEnabled { get; set; } = "0";

        /// <summary>Constant left-trigger motor magnitude (0..1 as
        /// InvariantCulture string).</summary>
        [XmlElement] public string ConstantTriggerForceLeft { get; set; } = "0";

        /// <summary>Constant right-trigger motor magnitude (0..1 as
        /// InvariantCulture string).</summary>
        [XmlElement] public string ConstantTriggerForceRight { get; set; } = "0";

        /// <summary>Enable audio-driven impulse-trigger rumble
        /// (Xbox One+). "0" = off (default), "1" = on. Runs a parallel
        /// filter chain inside <see cref="Common.Input.AudioBassDetector"/>
        /// with its own sensitivity / cutoff so it stays independent of
        /// the Force Feedback tab's Audio Bass Rumble.</summary>
        [XmlElement] public string AudioRumbleTriggersEnabled { get; set; } = "0";

        /// <summary>Audio-trigger rumble sensitivity (1–20, default 4).</summary>
        [XmlElement] public string AudioRumbleTriggersSensitivity { get; set; } = "4";

        /// <summary>Audio-trigger rumble bass cutoff frequency in Hz
        /// (40–200, default 80).</summary>
        [XmlElement] public string AudioRumbleTriggersCutoffHz { get; set; } = "80";

        /// <summary>Audio-trigger rumble: left trigger motor scale
        /// (0..100%).</summary>
        [XmlElement] public string AudioRumbleLeftTrigger { get; set; } = "100";

        /// <summary>Audio-trigger rumble: right trigger motor scale
        /// (0..100%).</summary>
        [XmlElement] public string AudioRumbleRightTrigger { get; set; } = "100";

        // ─────────────────────────────────────────────
        //  Trigger rumble routing (issue #102)
        //  Routes the main rumble-motor amplitude (XInput rumble + the scalar
        //  magnitude FFB games write through XINPUT_VIBRATION) into the trigger
        //  channel (Xbox impulse triggers and DualSense AT Vibration), gated by
        //  a per-trigger activator. Source None keeps impulse-only behavior.
        // ─────────────────────────────────────────────

        /// <summary>Left-trigger rumble route source: None / MainLeft /
        /// MainRight / MaxOfBoth / SumOfBoth. None routes nothing.</summary>
        [XmlElement] public string LeftTriggerRouteSource { get; set; } = "None";

        /// <summary>Right-trigger rumble route source. See LeftTriggerRouteSource.</summary>
        [XmlElement] public string RightTriggerRouteSource { get; set; } = "None";

        /// <summary>Left-trigger route mode: Duplicate (keep the main motor
        /// spinning while feeding the trigger) or Redirect (silence the main
        /// motor on the physical device). Ignored when source is None.</summary>
        [XmlElement] public string LeftTriggerRouteMode { get; set; } = "Duplicate";

        /// <summary>Right-trigger route mode. See LeftTriggerRouteMode.</summary>
        [XmlElement] public string RightTriggerRouteMode { get; set; } = "Duplicate";

        /// <summary>Left-trigger routed-amplitude scale, 0..200% of the source
        /// main-motor amplitude.</summary>
        [XmlElement] public string LeftTriggerRouteScale { get; set; } = "100";

        /// <summary>Right-trigger routed-amplitude scale, 0..200%.</summary>
        [XmlElement] public string RightTriggerRouteScale { get; set; } = "100";

        /// <summary>Left-trigger route activator descriptor (cross-device, the
        /// same picker as Gyro Aim Engage). Empty = always engaged.</summary>
        [XmlElement] public string LeftTriggerRouteActivator { get; set; } = "";

        /// <summary>Right-trigger route activator descriptor.</summary>
        [XmlElement] public string RightTriggerRouteActivator { get; set; } = "";

        /// <summary>Device GUID the left-trigger route activator reads from
        /// (cross-device, same shape as GyroAimEngageDeviceGuid).</summary>
        [XmlElement] public string LeftTriggerRouteActivatorDeviceGuid { get; set; } = "";

        /// <summary>Device GUID the right-trigger route activator reads from.</summary>
        [XmlElement] public string RightTriggerRouteActivatorDeviceGuid { get; set; } = "";

        /// <summary>Left-trigger route activator mode: Hold / Toggle / AlwaysOn.
        /// AlwaysOn (and an empty activator) ignore the descriptor.</summary>
        [XmlElement] public string LeftTriggerRouteActivatorMode { get; set; } = "Hold";

        /// <summary>Right-trigger route activator mode. See above.</summary>
        [XmlElement] public string RightTriggerRouteActivatorMode { get; set; } = "Hold";

        /// <summary>Enable audio bass rumble for this device. "0" = off (default), "1" = on.</summary>
        [XmlElement] public string AudioRumbleEnabled { get; set; } = "0";

        /// <summary>Audio rumble sensitivity (1–20, default 4).</summary>
        [XmlElement] public string AudioRumbleSensitivity { get; set; } = "4";

        /// <summary>Audio rumble bass cutoff frequency in Hz (40–200, default 80).</summary>
        [XmlElement] public string AudioRumbleCutoffHz { get; set; } = "80";

        /// <summary>Audio rumble left motor scale (0–100%, default 100).</summary>
        [XmlElement] public string AudioRumbleLeftMotor { get; set; } = "100";

        /// <summary>Audio rumble right motor scale (0–100%, default 100).</summary>
        [XmlElement] public string AudioRumbleRightMotor { get; set; } = "100";

        /// <summary>Constant force toggle. "0" = off (default), "1" = on.
        /// When on, PadForge applies a continuous force to the physical
        /// device at the angle/magnitude defined by ConstantForceX/Y. Game-
        /// driven force from any source overrides while non-zero, then
        /// the constant force resumes.</summary>
        [XmlElement] public string ConstantForceEnabled { get; set; } = "0";

        /// <summary>Constant force X component, signed (-1.0..+1.0).
        /// Default 0. Combined with ConstantForceY into a polar
        /// magnitude+direction; magnitude clamps to 1.0.</summary>
        [XmlElement] public string ConstantForceX { get; set; } = "0";

        /// <summary>Constant force Y component, signed (-1.0..+1.0). Y+
        /// is "up" in the UI grid; the engine converts to HID polar
        /// (0=N CW). Default 0.</summary>
        [XmlElement] public string ConstantForceY { get; set; } = "0";

        // ─────────────────────────────────────────────
        //  v3.3 Gyro tuning — per-(device, slot).
        //  Lives on PadSetting so each binding config (game profile +
        //  slot assignment) gets its own gyro feel AND its own bias
        //  calibration, matching SteamInput's slot-scoped feel and
        //  letting the user re-zero per slot if the IMU drifts in a
        //  particular orientation.
        //  Defaults preserve v3.2 baseline (1× scale, no deadzone /
        //  smoothing / acceleration, Linear curve, always-on Easy Aim,
        //  zero bias / uncalibrated → InputService auto-calibrates on
        //  first (device, slot) sighting).
        // ─────────────────────────────────────────────

        /// <summary>Horizontal sensitivity multiplier — applied to gyro
        /// Yaw and Roll source contributions. Stored as string for the
        /// XML schema's "everything is a string" convention.</summary>
        [XmlElement] public string GyroSensitivityH { get; set; } = "1.0";

        /// <summary>Vertical sensitivity multiplier — applied to gyro
        /// Pitch source contributions.</summary>
        [XmlElement] public string GyroSensitivityV { get; set; } = "1.0";

        /// <summary>Gyro deadzone in degrees per second. Subtract-style:
        /// rates inside the threshold zero out, rates past pass through
        /// with the threshold subtracted. Default 3°/s.</summary>
        [XmlElement] public string GyroDeadZoneDegPerSec { get; set; } = "3.0";

        /// <summary>Single-pole EMA smoothing alpha (0 = off, 0.95 = max).
        /// Applied to the bias-subtracted rate before deadzone.</summary>
        [XmlElement] public string GyroSmoothingAlpha { get; set; } = "0";

        /// <summary>Rate-dependent gain (0 = off, 2 = max). output =
        /// input × (1 + accel × |input|). Composes with the output curve.</summary>
        [XmlElement] public string GyroAcceleration { get; set; } = "0";

        /// <summary>Output curve preset name: Linear, Aggressive, Relaxed,
        /// Wide, ExtraWide. Reshapes the normalized [-1..+1] output.</summary>
        [XmlElement] public string GyroOutputCurve { get; set; } = "Linear";

        /// <summary>Sensitivity unit display mode: Multiplier (× scale,
        /// default) or DegPerScreenTurn (Steam-style — degrees of
        /// physical rotation per one full screen turn). Underlying
        /// stored values remain multipliers; the dropdown only changes
        /// how the slider value is presented + entered.</summary>
        [XmlElement] public string GyroSensitivityUnits { get; set; } = "Multiplier";

        /// <summary>Easy-Aim right-stick deflection threshold (0–100%).
        /// 0 = always-on (default). When > 0, gyro output is zeroed when
        /// the slot's right stick is deflected less than this threshold
        /// — matches Steam's "gyro engaged while aiming" feel without
        /// requiring a manual Shift Activator setup.</summary>
        [XmlElement] public string GyroEasyAimStickThreshold { get; set; } = "0";

        /// <summary>Which stick's deflection drives the Easy-Aim gate
        /// (<see cref="GyroEasyAimStickThreshold"/>): "Right" (default,
        /// preserves existing profiles), "Left", or "Either" (the larger
        /// of the two deflections). Only consulted when the threshold &gt; 0.
        /// Issue #120.</summary>
        [XmlElement] public string GyroEngageStickSide { get; set; } = "Right";

        /// <summary>Which component of the engage stick(s) the Easy-Aim
        /// gate compares against the threshold (issue #120): "Full"
        /// (default, radial max(|x|,|y|), preserves existing profiles),
        /// "X"/"Y" (full horizontal/vertical), or "XNeg"/"XPos"/"YNeg"/
        /// "YPos" (single direction: left/right/down/up). Composes with
        /// GyroEngageStickSide. Only consulted when the threshold &gt; 0.</summary>
        [XmlElement] public string GyroEngageStickDirection { get; set; } = "Full";

        /// <summary>Wii IR pointer: where the sensor bar sits relative to the
        /// screen (0 = centered, 1 = above, 2 = below). Per (device, slot) like
        /// every other pad-page tunable. Moved here from UserDevice so two
        /// virtual controllers sharing one remote keep independent pointer
        /// feel (issue #146, same move the gyro tuning made in v3.3).</summary>
        [XmlElement] public string IrSensorBarPos { get; set; } = "0";

        /// <summary>Wii IR pointer: sensor-bar vertical compensation magnitude,
        /// 0..0.5 of the pointer range (Touchmote pointer_sensorBarPosCompensation).
        /// Applied above/below per <see cref="IrSensorBarPos"/>.</summary>
        [XmlElement] public string IrSensorBarComp { get; set; } = "0";

        /// <summary>Wii IR pointer smoothing, 0..1. 0 = raw (no lag), higher =
        /// heavier low-pass on the jittery camera (Touchmote position
        /// smoothing).</summary>
        [XmlElement] public string IrSmoothing { get; set; } = "0";

        /// <summary>At-rest bias for Pitch axis (rad/s), subtracted from
        /// the raw SDL3 gyro reading at the source-coercion read point.
        /// Per-(device, slot) — re-running calibration on slot A doesn't
        /// disturb slot B's bias for the same physical pad. "0" = no
        /// correction; <see cref="GyroCalibratedAtUtc"/> default means
        /// InputService auto-calibrates this (device, slot) on first
        /// sight.</summary>
        [XmlElement] public string GyroBiasPitch { get; set; } = "0";

        /// <summary>At-rest bias for Yaw axis (rad/s).</summary>
        [XmlElement] public string GyroBiasYaw { get; set; } = "0";

        /// <summary>At-rest bias for Roll axis (rad/s).</summary>
        [XmlElement] public string GyroBiasRoll { get; set; } = "0";

        /// <summary>UTC timestamp of the most recent successful
        /// calibration for this (device, slot). Default
        /// (DateTime.MinValue) flags "uncalibrated; auto-calibrate on
        /// first poll". Stored as an ISO-8601 round-trip string for
        /// schema consistency; reset to empty by the Reset Calibration
        /// button.</summary>
        [XmlElement] public string GyroCalibratedAtUtc { get; set; } = "";

        // ─────────────────────────────────────────────
        //  JoyShockMapper-canon gyro extensions: Player
        //  Space + dual-threshold smoothing + real-world
        //  calibration + button-gated aim engage +
        //  per-axis invert toggles. Closes the gap
        //  between the SteamInput-parity baseline and
        //  the JoyShockMapper / GamepadMotion canon.
        // ─────────────────────────────────────────────

        /// <summary>Gyro coordinate space: "Local" (default, raw
        /// controller axes), "Player" (yaw projected onto real-world
        /// vertical via gravity; pitch stays local — Jibb's recommended
        /// default), "World" (both yaw and pitch projected onto world
        /// axes). Player is the popular sweet spot.</summary>
        [XmlElement] public string GyroSpace { get; set; } = "Local";

        /// <summary>Player Space yaw relaxation factor. Default 1.41
        /// (~√2) per GamepadMotion.hpp. Lets the projected yaw range
        /// slightly past the raw magnitude so feel doesn't get muted
        /// at extreme tilts.</summary>
        [XmlElement] public string GyroPlayerSpaceYawRelaxFactor { get; set; } = "1.41";

        /// <summary>World Space side-reduction threshold (0–1, default
        /// 0.125 per GamepadMotion.hpp). Smoothly fades the pitch
        /// contribution as the controller is rolled onto its side,
        /// avoiding feel cliffs.</summary>
        [XmlElement] public string GyroWorldSpaceSideReductionThreshold { get; set; } = "0.125";

        /// <summary>Gyro tightening (lower) threshold in deg/s. Below
        /// this rate, the input is fully replaced by the smoothing
        /// buffer's average — hand tremor and microscopic drift get
        /// attenuated. Default 3°/s.</summary>
        [XmlElement] public string GyroTighteningThresholdDegPerSec { get; set; } = "3.0";

        /// <summary>Gyro smoothing (upper) threshold in deg/s. Above
        /// this rate, the input passes through raw — fast turns retain
        /// precision. Between tightening and smoothing, a linear ramp
        /// blends. Default 8°/s.</summary>
        [XmlElement] public string GyroSmoothingThresholdDegPerSec { get; set; } = "8.0";

        /// <summary>Length of the smoothing-buffer time window in
        /// milliseconds. At 60-fps poll, 50ms ≈ 3 samples. Larger =
        /// heavier smoothing below tightening; smaller = snappier.
        /// Default 50ms.</summary>
        [XmlElement] public string GyroSmoothingWindowMs { get; set; } = "50";

        /// <summary>Real-world calibration: in-game degrees per physical
        /// degree of controller rotation. 0 (default) = disabled. The
        /// user calibrates this once per game profile via the JSM-style
        /// "rotate the pad 360°, look at the in-game rotation, divide"
        /// recipe.</summary>
        [XmlElement] public string GyroRealWorldCalibration { get; set; } = "0";

        /// <summary>Cross-device button descriptor that gates gyro
        /// output: gyro fires only while this button is pressed. Empty
        /// = always-on (gyro is gated only by Easy Aim, if configured).
        /// Pairs naturally with Easy Aim; both are AND-composed (both
        /// must be active).</summary>
        [XmlElement] public string GyroAimEngageButton { get; set; } = "";

        /// <summary>Device GUID owning the
        /// <see cref="GyroAimEngageButton"/> descriptor.</summary>
        [XmlElement] public string GyroAimEngageDeviceGuid { get; set; } = "";

        /// <summary>Activation semantics for <see cref="GyroAimEngageButton"/>.
        /// <c>"Hold"</c> (default): gyro fires while the engage button is held;
        /// empty descriptor = always-on.
        /// <c>"Toggle"</c>: each rising edge of the engage button flips a
        /// sticky per-slot bit; release does nothing; empty descriptor =
        /// never engages from the button (the macro path can still engage).
        /// The button-bit is OR-combined with the
        /// <c>SetGyroEngaged</c> macro action's per-slot bit at read time, so
        /// either source can engage and neither can disengage what the other
        /// engaged. Both bits reset on profile switch and app restart
        /// (volatile per-slot state, not persisted).</summary>
        [XmlElement] public string GyroAimEngageMode { get; set; } = "Hold";

        /// <summary>Top-level invert toggle for the projected pitch
        /// axis. Applies post-tuning, after Player/World projection,
        /// so its effect is consistent across gyro spaces.</summary>
        [XmlElement] public string GyroInvertPitch { get; set; } = "0";

        /// <summary>Top-level invert toggle for the projected yaw axis
        /// (includes Roll for Local space and Horizontal blend).</summary>
        [XmlElement("GyroInvertYaw")] public string GyroInvertYawRoll { get; set; } = "0";

        /// <summary>When "1" (default), the Gyro tab tuning chain is
        /// applied to this device's motion passthrough on this slot —
        /// the virtual controller's gyro report and the DSU broadcast.
        /// When "0", the passthrough relays the raw sensor reading and
        /// the Gyro tab affects only gyro-as-mapping-source reads.
        /// Stored per-(device, slot) like the rest of the gyro tuning.
        /// Defaults to "0" so a fresh profile (or an absent element on
        /// an upgraded one) hands the game a clean passthrough; the
        /// user opts in to having tuning reach the virtual controller.</summary>
        [XmlElement] public string GyroApplyTuningToPassthrough { get; set; } = "0";

        // ─────────────────────────────────────────────
        //  Axis-to-button threshold
        // ─────────────────────────────────────────────

        /// <summary>
        /// Threshold (0–100%) for treating an axis as a button press.
        /// Used when mapping an axis to a digital button.
        /// Default 50 = axis must exceed 50% to register as pressed.
        /// </summary>
        [XmlElement] public string AxisToButtonThreshold { get; set; } = "50";

        // ─────────────────────────────────────────────
        //  Axis inversion overrides
        // ─────────────────────────────────────────────

        /// <summary>Invert left stick X axis. "0" or "1".</summary>
        [XmlElement] public string LeftThumbAxisXInvert { get; set; } = "0";

        /// <summary>Invert left stick Y axis.</summary>
        [XmlElement] public string LeftThumbAxisYInvert { get; set; } = "0";

        /// <summary>Invert right stick X axis.</summary>
        [XmlElement] public string RightThumbAxisXInvert { get; set; } = "0";

        /// <summary>Invert right stick Y axis.</summary>
        [XmlElement] public string RightThumbAxisYInvert { get; set; } = "0";

        // ─────────────────────────────────────────────
        //  PlayStation touchpad mappings
        // ─────────────────────────────────────────────

        [XmlElement] public string TouchpadX1 { get; set; } = "";
        [XmlElement] public string TouchpadY1 { get; set; } = "";
        [XmlElement] public string TouchpadX2 { get; set; } = "";
        [XmlElement] public string TouchpadY2 { get; set; } = "";
        [XmlElement] public string TouchpadContact1 { get; set; } = "";
        [XmlElement] public string TouchpadContact2 { get; set; } = "";
        [XmlElement] public string TouchpadClick { get; set; } = "";

        // ─────────────────────────────────────────────
        //  Motion passthrough sources (Sony-class slots only)
        //
        //  These two descriptors mark whether this device contributes
        //  its bundled gyro / accel stream to the virtual controller's
        //  motion channel on this slot. Value is the bundled-source
        //  descriptor literal "Motion Gyro" / "Motion Accel" when the
        //  device contributes, or empty when the user has opted out
        //  by deleting the row. The MappingSetMigrator and the
        //  EnsureMotionRows backfill keep these in sync with the
        //  per-slot MappingSet rows that the engine reads.
        // ─────────────────────────────────────────────

        [XmlElement] public string MotionGyro  { get; set; } = "";
        [XmlElement] public string MotionAccel { get; set; } = "";

        // ─────────────────────────────────────────────
        //  Touchpad gesture detection settings
        //
        //  One entry per (assigned device, touchpad-index) pair on
        //  this slot. Multi-pad devices like the Steam Controller have
        //  one entry per pad so the left and right pads can have
        //  independent gesture catalogs and thresholds. Empty / null
        //  collection means every touchpad uses
        //  TouchpadGestureSettings.Default() at runtime, which the
        //  InputManager's provider returns when no entry is found.
        // ─────────────────────────────────────────────

        [XmlArray("TouchpadSettings")]
        [XmlArrayItem("Settings")]
        public PadForge.Engine.Touchpad.TouchpadSettingsEntry[] TouchpadSettings { get; set; }

        // ─────────────────────────────────────────────
        //  Extended custom mappings (dictionary-based)
        //  Used for custom Extended configurations with arbitrary axis/button/POV counts.
        //  Keys use target names like "ExtendedAxis0", "ExtendedAxis0Neg", "ExtendedBtn0",
        //  "ExtendedPov0Up", etc. Values are mapping descriptors (same format as above).
        // ─────────────────────────────────────────────

        /// <summary>Serializable array for XML persistence of Extended mappings.</summary>
        [XmlArray("ExtendedMappings")]
        [XmlArrayItem("Map")]
        public ExtendedMappingEntry[] ExtendedMappingEntries { get; set; }

        [XmlIgnore]
        private Dictionary<string, string> _extendedMappingDict;

        /// <summary>Gets an Extended mapping value by key (e.g., "ExtendedAxis0", "ExtendedBtn5").</summary>
        public string GetExtendedMapping(string key)
        {
            EnsureExtendedDict();
            return _extendedMappingDict.TryGetValue(key, out var val) ? val : "";
        }

        /// <summary>Sets an Extended mapping value by key.</summary>
        public void SetExtendedMapping(string key, string value)
        {
            EnsureExtendedDict();
            if (string.IsNullOrEmpty(value))
                _extendedMappingDict.Remove(key);
            else
                _extendedMappingDict[key] = value;
        }

        /// <summary>Flushes the Extended mapping dict back to the serializable array.</summary>
        public void FlushExtendedMappings()
        {
            if (_extendedMappingDict == null) return; // Not initialized — array is canonical.
            if (_extendedMappingDict.Count == 0)
            {
                ExtendedMappingEntries = null;
                return;
            }
            var entries = new ExtendedMappingEntry[_extendedMappingDict.Count];
            int i = 0;
            foreach (var kvp in _extendedMappingDict)
                entries[i++] = new ExtendedMappingEntry { Key = kvp.Key, Value = kvp.Value };
            ExtendedMappingEntries = entries;
        }

        private readonly object _extendedDictLock = new();

        private void EnsureExtendedDict()
        {
            if (_extendedMappingDict != null) return;
            lock (_extendedDictLock)
            {
                if (_extendedMappingDict != null) return;
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                if (ExtendedMappingEntries != null)
                {
                    foreach (var e in ExtendedMappingEntries)
                    {
                        if (!string.IsNullOrEmpty(e.Key) && !string.IsNullOrEmpty(e.Value))
                            dict[e.Key] = e.Value;
                    }
                }
                _extendedMappingDict = dict;
            }
        }

        // ─────────────────────────────────────────────
        //  MIDI mappings (dictionary-based)
        //  Used for MIDI output with arbitrary CC/note counts.
        //  Keys: "MidiCC0", "MidiCC0Neg", "MidiNote0", etc.
        //  Values: mapping descriptors (same format as above).
        // ─────────────────────────────────────────────

        [XmlArray("MidiMappings")]
        [XmlArrayItem("Map")]
        public ExtendedMappingEntry[] MidiMappingEntries { get; set; }

        [XmlIgnore]
        private Dictionary<string, string> _midiMappingDict;

        public string GetMidiMapping(string key)
        {
            EnsureMidiDict();
            return _midiMappingDict.TryGetValue(key, out var val) ? val : "";
        }

        public void SetMidiMapping(string key, string value)
        {
            EnsureMidiDict();
            if (string.IsNullOrEmpty(value))
                _midiMappingDict.Remove(key);
            else
                _midiMappingDict[key] = value;
        }

        public void FlushMidiMappings()
        {
            if (_midiMappingDict == null) return; // Not initialized — array is canonical.
            if (_midiMappingDict.Count == 0)
            {
                MidiMappingEntries = null;
                return;
            }
            var entries = new ExtendedMappingEntry[_midiMappingDict.Count];
            int i = 0;
            foreach (var kvp in _midiMappingDict)
                entries[i++] = new ExtendedMappingEntry { Key = kvp.Key, Value = kvp.Value };
            MidiMappingEntries = entries;
        }

        private readonly object _midiDictLock = new();

        private void EnsureMidiDict()
        {
            if (_midiMappingDict != null) return;
            lock (_midiDictLock)
            {
                if (_midiMappingDict != null) return;
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                if (MidiMappingEntries != null)
                {
                    foreach (var e in MidiMappingEntries)
                    {
                        if (!string.IsNullOrEmpty(e.Key) && !string.IsNullOrEmpty(e.Value))
                            dict[e.Key] = e.Value;
                    }
                }
                _midiMappingDict = dict;
            }
        }

        // ─────────────────────────────────────────────
        //  KBM mappings (dictionary-based)
        //  Used for KeyboardMouse output with keyboard key + mouse targets.
        //  Keys: "KbmKey41" (VK_A), "KbmMouseX", "KbmMouseXNeg", "KbmMBtn0", etc.
        //  Values: mapping descriptors (same format as above).
        // ─────────────────────────────────────────────

        [XmlArray("KbmMappings")]
        [XmlArrayItem("Map")]
        public ExtendedMappingEntry[] KbmMappingEntries { get; set; }

        [XmlIgnore]
        private Dictionary<string, string> _kbmMappingDict;

        public string GetKbmMapping(string key)
        {
            EnsureKbmDict();
            return _kbmMappingDict.TryGetValue(key, out var val) ? val : "";
        }

        public void SetKbmMapping(string key, string value)
        {
            EnsureKbmDict();
            if (string.IsNullOrEmpty(value))
                _kbmMappingDict.Remove(key);
            else
                _kbmMappingDict[key] = value;
        }

        public void FlushKbmMappings()
        {
            if (_kbmMappingDict == null) return;
            if (_kbmMappingDict.Count == 0)
            {
                KbmMappingEntries = null;
                return;
            }
            var entries = new ExtendedMappingEntry[_kbmMappingDict.Count];
            int i = 0;
            foreach (var kvp in _kbmMappingDict)
                entries[i++] = new ExtendedMappingEntry { Key = kvp.Key, Value = kvp.Value };
            KbmMappingEntries = entries;
        }

        private readonly object _kbmDictLock = new();

        private void EnsureKbmDict()
        {
            if (_kbmMappingDict != null) return;
            lock (_kbmDictLock)
            {
                if (_kbmMappingDict != null) return;
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                if (KbmMappingEntries != null)
                {
                    foreach (var e in KbmMappingEntries)
                    {
                        if (!string.IsNullOrEmpty(e.Key) && !string.IsNullOrEmpty(e.Value))
                            dict[e.Key] = e.Value;
                    }
                }
                _kbmMappingDict = dict;
            }
        }

        // ─────────────────────────────────────────────
        //  Per-mapping deadzones (axis activation threshold)
        // ─────────────────────────────────────────────

        [XmlArray("MappingDeadZones")]
        [XmlArrayItem("Map")]
        public ExtendedMappingEntry[] MappingDeadZoneEntries { get; set; }

        [XmlIgnore]
        private Dictionary<string, string> _mappingDeadZoneDict;
        private readonly object _mappingDeadZoneDictLock = new();

        public string GetMappingDeadZone(string key)
        {
            EnsureMappingDeadZoneDict();
            return _mappingDeadZoneDict.TryGetValue(key, out var val) ? val : "";
        }

        public void SetMappingDeadZone(string key, string value)
        {
            EnsureMappingDeadZoneDict();
            if (string.IsNullOrEmpty(value) || value == "0" || value == "50")
                _mappingDeadZoneDict.Remove(key);
            else
                _mappingDeadZoneDict[key] = value;
        }

        public void FlushMappingDeadZones()
        {
            if (_mappingDeadZoneDict == null) return;
            if (_mappingDeadZoneDict.Count == 0)
            {
                MappingDeadZoneEntries = null;
                return;
            }
            var entries = new ExtendedMappingEntry[_mappingDeadZoneDict.Count];
            int i = 0;
            foreach (var kvp in _mappingDeadZoneDict)
                entries[i++] = new ExtendedMappingEntry { Key = kvp.Key, Value = kvp.Value };
            MappingDeadZoneEntries = entries;
        }

        private void EnsureMappingDeadZoneDict()
        {
            if (_mappingDeadZoneDict != null) return;
            lock (_mappingDeadZoneDictLock)
            {
                if (_mappingDeadZoneDict != null) return;
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                if (MappingDeadZoneEntries != null)
                {
                    foreach (var e in MappingDeadZoneEntries)
                    {
                        if (!string.IsNullOrEmpty(e.Key) && !string.IsNullOrEmpty(e.Value))
                            dict[e.Key] = e.Value;
                    }
                }
                _mappingDeadZoneDict = dict;
            }
        }

        // ─────────────────────────────────────────────
        //  Per-mapping Bidirectional flag
        //  Parallel storage to MappingDeadZone — the legacy single-source
        //  descriptor format encodes Invert / HalfAxis as I / H prefixes;
        //  Bidirectional is the third independent axis-to-button flag and
        //  lives in its own dictionary so the descriptor string stays
        //  unchanged. Stored as "1" / "0" / "" (missing = false default).
        // ─────────────────────────────────────────────

        [XmlArray("MappingBidirectional")]
        [XmlArrayItem("Map")]
        public ExtendedMappingEntry[] MappingBidirectionalEntries { get; set; }

        [XmlIgnore]
        private Dictionary<string, string> _mappingBidirectionalDict;
        private readonly object _mappingBidirectionalDictLock = new();

        public string GetMappingBidirectional(string key)
        {
            EnsureMappingBidirectionalDict();
            return _mappingBidirectionalDict.TryGetValue(key, out var val) ? val : "";
        }

        public void SetMappingBidirectional(string key, string value)
        {
            EnsureMappingBidirectionalDict();
            if (string.IsNullOrEmpty(value) || value == "0")
                _mappingBidirectionalDict.Remove(key);
            else
                _mappingBidirectionalDict[key] = value;
        }

        public void FlushMappingBidirectional()
        {
            if (_mappingBidirectionalDict == null) return;
            if (_mappingBidirectionalDict.Count == 0)
            {
                MappingBidirectionalEntries = null;
                return;
            }
            var entries = new ExtendedMappingEntry[_mappingBidirectionalDict.Count];
            int i = 0;
            foreach (var kvp in _mappingBidirectionalDict)
                entries[i++] = new ExtendedMappingEntry { Key = kvp.Key, Value = kvp.Value };
            MappingBidirectionalEntries = entries;
        }

        private void EnsureMappingBidirectionalDict()
        {
            if (_mappingBidirectionalDict != null) return;
            lock (_mappingBidirectionalDictLock)
            {
                if (_mappingBidirectionalDict != null) return;
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                if (MappingBidirectionalEntries != null)
                {
                    foreach (var e in MappingBidirectionalEntries)
                    {
                        if (!string.IsNullOrEmpty(e.Key) && !string.IsNullOrEmpty(e.Value))
                            dict[e.Key] = e.Value;
                    }
                }
                _mappingBidirectionalDict = dict;
            }
        }

        // ─────────────────────────────────────────────
        //  Migration
        // ─────────────────────────────────────────────

        /// <summary>
        /// Migrates legacy unified anti-deadzone values to per-axis properties.
        /// Call after deserialization when loading old settings files.
        /// </summary>
        public void MigrateAntiDeadZones()
        {
            if (IsEmptyOrZero(LeftThumbAntiDeadZoneX) && IsEmptyOrZero(LeftThumbAntiDeadZoneY)
                && !IsEmptyOrZero(LeftThumbAntiDeadZone))
            {
                LeftThumbAntiDeadZoneX = LeftThumbAntiDeadZone;
                LeftThumbAntiDeadZoneY = LeftThumbAntiDeadZone;
            }
            if (IsEmptyOrZero(RightThumbAntiDeadZoneX) && IsEmptyOrZero(RightThumbAntiDeadZoneY)
                && !IsEmptyOrZero(RightThumbAntiDeadZone))
            {
                RightThumbAntiDeadZoneX = RightThumbAntiDeadZone;
                RightThumbAntiDeadZoneY = RightThumbAntiDeadZone;
            }
        }

        /// <summary>
        /// Migrates symmetric max range values to per-direction properties.
        /// If negative-direction property is null/empty, copies the symmetric value.
        /// </summary>
        public void MigrateMaxRangeDirections()
        {
            if (string.IsNullOrEmpty(LeftThumbMaxRangeXNeg))
                LeftThumbMaxRangeXNeg = LeftThumbMaxRangeX;
            if (string.IsNullOrEmpty(LeftThumbMaxRangeYNeg))
                LeftThumbMaxRangeYNeg = LeftThumbMaxRangeY;
            if (string.IsNullOrEmpty(RightThumbMaxRangeXNeg))
                RightThumbMaxRangeXNeg = RightThumbMaxRangeX;
            if (string.IsNullOrEmpty(RightThumbMaxRangeYNeg))
                RightThumbMaxRangeYNeg = RightThumbMaxRangeY;
        }

        private static bool IsEmptyOrZero(string v) =>
            string.IsNullOrEmpty(v) || v == "0";

        // ─────────────────────────────────────────────
        //  Checksum computation
        // ─────────────────────────────────────────────

        /// <summary>
        /// Computes a checksum from all mapping and setting properties.
        /// Used to detect identical configurations and link UserSettings to PadSettings.
        /// </summary>
        /// <returns>An 8-character hex checksum string.</returns>
        public string ComputeChecksum()
        {
            var sb = new StringBuilder(1024);

            // Buttons
            sb.Append(ButtonA); sb.Append('|');
            sb.Append(ButtonB); sb.Append('|');
            sb.Append(ButtonX); sb.Append('|');
            sb.Append(ButtonY); sb.Append('|');
            sb.Append(LeftShoulder); sb.Append('|');
            sb.Append(RightShoulder); sb.Append('|');
            sb.Append(ButtonBack); sb.Append('|');
            sb.Append(ButtonStart); sb.Append('|');
            sb.Append(ButtonGuide); sb.Append('|');
            sb.Append(LeftThumbButton); sb.Append('|');
            sb.Append(RightThumbButton); sb.Append('|');
            sb.Append(ButtonShare); sb.Append('|');

            // D-Pad
            sb.Append(DPad); sb.Append('|');
            sb.Append(DPadUp); sb.Append('|');
            sb.Append(DPadDown); sb.Append('|');
            sb.Append(DPadLeft); sb.Append('|');
            sb.Append(DPadRight); sb.Append('|');

            // Triggers
            sb.Append(LeftTrigger); sb.Append('|');
            sb.Append(RightTrigger); sb.Append('|');
            sb.Append(LeftTriggerDeadZone); sb.Append('|');
            sb.Append(RightTriggerDeadZone); sb.Append('|');
            sb.Append(LeftTriggerAntiDeadZone); sb.Append('|');
            sb.Append(RightTriggerAntiDeadZone); sb.Append('|');
            sb.Append(LeftTriggerMaxRange); sb.Append('|');
            sb.Append(RightTriggerMaxRange); sb.Append('|');

            // Thumbstick axes
            sb.Append(LeftThumbAxisX); sb.Append('|');
            sb.Append(LeftThumbAxisY); sb.Append('|');
            sb.Append(RightThumbAxisX); sb.Append('|');
            sb.Append(RightThumbAxisY); sb.Append('|');
            sb.Append(LeftThumbAxisXNeg); sb.Append('|');
            sb.Append(LeftThumbAxisYNeg); sb.Append('|');
            sb.Append(RightThumbAxisXNeg); sb.Append('|');
            sb.Append(RightThumbAxisYNeg); sb.Append('|');

            // Touchpad
            sb.Append(TouchpadX1); sb.Append('|');
            sb.Append(TouchpadY1); sb.Append('|');
            sb.Append(TouchpadX2); sb.Append('|');
            sb.Append(TouchpadY2); sb.Append('|');
            sb.Append(TouchpadContact1); sb.Append('|');
            sb.Append(TouchpadContact2); sb.Append('|');
            sb.Append(TouchpadClick); sb.Append('|');

            // Dead zones
            sb.Append(LeftThumbDeadZoneX); sb.Append('|');
            sb.Append(LeftThumbDeadZoneY); sb.Append('|');
            sb.Append(RightThumbDeadZoneX); sb.Append('|');
            sb.Append(RightThumbDeadZoneY); sb.Append('|');
            sb.Append(LeftThumbDeadZoneShape); sb.Append('|');
            sb.Append(RightThumbDeadZoneShape); sb.Append('|');
            sb.Append(LeftThumbAntiDeadZone); sb.Append('|');
            sb.Append(RightThumbAntiDeadZone); sb.Append('|');
            sb.Append(LeftThumbAntiDeadZoneX); sb.Append('|');
            sb.Append(LeftThumbAntiDeadZoneY); sb.Append('|');
            sb.Append(RightThumbAntiDeadZoneX); sb.Append('|');
            sb.Append(RightThumbAntiDeadZoneY); sb.Append('|');
            sb.Append(LeftThumbLinear); sb.Append('|');
            sb.Append(RightThumbLinear); sb.Append('|');
            sb.Append(LeftThumbSensitivityCurveX); sb.Append('|');
            sb.Append(LeftThumbSensitivityCurveY); sb.Append('|');
            sb.Append(RightThumbSensitivityCurveX); sb.Append('|');
            sb.Append(RightThumbSensitivityCurveY); sb.Append('|');
            sb.Append(LeftTriggerSensitivityCurve); sb.Append('|');
            sb.Append(RightTriggerSensitivityCurve); sb.Append('|');
            sb.Append(LeftThumbMaxRangeX); sb.Append('|');
            sb.Append(LeftThumbMaxRangeY); sb.Append('|');
            sb.Append(RightThumbMaxRangeX); sb.Append('|');
            sb.Append(RightThumbMaxRangeY); sb.Append('|');
            sb.Append(LeftThumbMaxRangeXNeg); sb.Append('|');
            sb.Append(LeftThumbMaxRangeYNeg); sb.Append('|');
            sb.Append(RightThumbMaxRangeXNeg); sb.Append('|');
            sb.Append(RightThumbMaxRangeYNeg); sb.Append('|');
            sb.Append(LeftThumbCenterOffsetX); sb.Append('|');
            sb.Append(LeftThumbCenterOffsetY); sb.Append('|');
            sb.Append(RightThumbCenterOffsetX); sb.Append('|');
            sb.Append(RightThumbCenterOffsetY); sb.Append('|');
            sb.Append(LeftThumbBoundaryMap); sb.Append('|');
            sb.Append(RightThumbBoundaryMap); sb.Append('|');

            // Force feedback
            sb.Append(ForceType); sb.Append('|');
            sb.Append(ForceOverall); sb.Append('|');
            sb.Append(RotationRange); sb.Append('|');
            sb.Append(AutoCenterStrength); sb.Append('|');
            sb.Append(WheelRpmLeds); sb.Append('|');
            sb.Append(SteeringLockRumbleEnabled); sb.Append('|');
            sb.Append(SteeringLockTriggerVibEnabled); sb.Append('|');
            sb.Append(SteeringLockLightbarEnabled); sb.Append('|');
            sb.Append(SteeringLockATResistanceEnabled); sb.Append('|');
            sb.Append(SteeringLockPulseMs); sb.Append('|');
            sb.Append(SteeringLockLightbarColor); sb.Append('|');
            sb.Append(SteeringLockLightbarColorSource); sb.Append('|');
            sb.Append(SteeringLockLightbarPaletteCsv); sb.Append('|');
            sb.Append(SteeringLockLightbarHoldMs); sb.Append('|');
            sb.Append(SteeringLockLightbarFadeMs); sb.Append('|');
            sb.Append(ForceSwapMotor); sb.Append('|');
            sb.Append(LeftMotorStrength); sb.Append('|');
            sb.Append(RightMotorStrength); sb.Append('|');
            sb.Append(ImpulseOverallGain).Append('|');
            sb.Append(ImpulseLeftStrength).Append('|');
            sb.Append(ImpulseRightStrength).Append('|');
            sb.Append(ImpulseSwapTriggers).Append('|');
            sb.Append(ConstantTriggerForceEnabled).Append('|');
            sb.Append(ConstantTriggerForceLeft).Append('|');
            sb.Append(ConstantTriggerForceRight).Append('|');
            sb.Append(AudioRumbleTriggersEnabled).Append('|');
            sb.Append(AudioRumbleTriggersSensitivity).Append('|');
            sb.Append(AudioRumbleTriggersCutoffHz).Append('|');
            sb.Append(AudioRumbleLeftTrigger).Append('|');
            sb.Append(AudioRumbleRightTrigger).Append('|');

            // Audio bass rumble
            sb.Append(AudioRumbleEnabled); sb.Append('|');
            sb.Append(AudioRumbleSensitivity); sb.Append('|');
            sb.Append(AudioRumbleCutoffHz); sb.Append('|');
            sb.Append(AudioRumbleLeftMotor); sb.Append('|');
            sb.Append(AudioRumbleRightMotor); sb.Append('|');

            // Constant force
            sb.Append(ConstantForceEnabled); sb.Append('|');
            sb.Append(ConstantForceX); sb.Append('|');
            sb.Append(ConstantForceY); sb.Append('|');

            // Gyro tuning (per-(device, slot)). Gyro fields not being in
            // the checksum used to let SaveToFile's dedup-by-checksum drop
            // a PadSetting when two devices had identical mapping
            // descriptors but only the gyro tuning differed.
            sb.Append(GyroSensitivityH); sb.Append('|');
            sb.Append(GyroSensitivityV); sb.Append('|');
            sb.Append(GyroDeadZoneDegPerSec); sb.Append('|');
            sb.Append(GyroSmoothingAlpha); sb.Append('|');
            sb.Append(GyroAcceleration); sb.Append('|');
            sb.Append(GyroOutputCurve); sb.Append('|');
            sb.Append(GyroSensitivityUnits); sb.Append('|');
            sb.Append(GyroEasyAimStickThreshold); sb.Append('|');
            sb.Append(GyroEngageStickSide); sb.Append('|');
            sb.Append(GyroEngageStickDirection); sb.Append('|');
            sb.Append(IrSensorBarPos); sb.Append('|');
            sb.Append(IrSensorBarComp); sb.Append('|');
            sb.Append(IrSmoothing); sb.Append('|');
            sb.Append(GyroBiasPitch); sb.Append('|');
            sb.Append(GyroBiasYaw); sb.Append('|');
            sb.Append(GyroBiasRoll); sb.Append('|');
            sb.Append(GyroCalibratedAtUtc); sb.Append('|');
            sb.Append(GyroSpace); sb.Append('|');
            sb.Append(GyroPlayerSpaceYawRelaxFactor); sb.Append('|');
            sb.Append(GyroWorldSpaceSideReductionThreshold); sb.Append('|');
            sb.Append(GyroTighteningThresholdDegPerSec); sb.Append('|');
            sb.Append(GyroSmoothingThresholdDegPerSec); sb.Append('|');
            sb.Append(GyroSmoothingWindowMs); sb.Append('|');
            sb.Append(GyroRealWorldCalibration); sb.Append('|');
            sb.Append(GyroAimEngageButton); sb.Append('|');
            sb.Append(GyroAimEngageDeviceGuid); sb.Append('|');
            sb.Append(GyroAimEngageMode); sb.Append('|');
            // Trigger rumble routing (#102)
            sb.Append(LeftTriggerRouteSource); sb.Append('|');
            sb.Append(RightTriggerRouteSource); sb.Append('|');
            sb.Append(LeftTriggerRouteMode); sb.Append('|');
            sb.Append(RightTriggerRouteMode); sb.Append('|');
            sb.Append(LeftTriggerRouteScale); sb.Append('|');
            sb.Append(RightTriggerRouteScale); sb.Append('|');
            sb.Append(LeftTriggerRouteActivator); sb.Append('|');
            sb.Append(RightTriggerRouteActivator); sb.Append('|');
            sb.Append(LeftTriggerRouteActivatorDeviceGuid); sb.Append('|');
            sb.Append(RightTriggerRouteActivatorDeviceGuid); sb.Append('|');
            sb.Append(LeftTriggerRouteActivatorMode); sb.Append('|');
            sb.Append(RightTriggerRouteActivatorMode); sb.Append('|');
            sb.Append(GyroInvertPitch); sb.Append('|');
            sb.Append(GyroInvertYawRoll); sb.Append('|');
            sb.Append(GyroApplyTuningToPassthrough); sb.Append('|');

            // Inversion overrides
            sb.Append(LeftThumbAxisXInvert); sb.Append('|');
            sb.Append(LeftThumbAxisYInvert); sb.Append('|');
            sb.Append(RightThumbAxisXInvert); sb.Append('|');
            sb.Append(RightThumbAxisYInvert); sb.Append('|');

            sb.Append(AxisToButtonThreshold); sb.Append('|');

            // Motion passthrough source markers
            sb.Append(MotionGyro); sb.Append('|');
            sb.Append(MotionAccel); sb.Append('|');

            // Extended custom mappings (sorted for deterministic checksum)
            EnsureExtendedDict();
            if (_extendedMappingDict.Count > 0)
            {
                var keys = new List<string>(_extendedMappingDict.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    sb.Append(key); sb.Append('='); sb.Append(_extendedMappingDict[key]); sb.Append('|');
                }
            }

            // MIDI custom mappings (sorted for deterministic checksum)
            EnsureMidiDict();
            if (_midiMappingDict.Count > 0)
            {
                var midiKeys = new List<string>(_midiMappingDict.Keys);
                midiKeys.Sort(StringComparer.Ordinal);
                foreach (var key in midiKeys)
                {
                    sb.Append(key); sb.Append('='); sb.Append(_midiMappingDict[key]); sb.Append('|');
                }
            }

            // KBM custom mappings (sorted for deterministic checksum)
            EnsureKbmDict();
            if (_kbmMappingDict.Count > 0)
            {
                var kbmKeys = new List<string>(_kbmMappingDict.Keys);
                kbmKeys.Sort(StringComparer.Ordinal);
                foreach (var key in kbmKeys)
                {
                    sb.Append(key); sb.Append('='); sb.Append(_kbmMappingDict[key]); sb.Append('|');
                }
            }

            // Per-mapping deadzones (sorted for deterministic checksum)
            EnsureMappingDeadZoneDict();
            if (_mappingDeadZoneDict.Count > 0)
            {
                sb.Append("MDZ:");
                var mdzKeys = new List<string>(_mappingDeadZoneDict.Keys);
                mdzKeys.Sort(StringComparer.Ordinal);
                foreach (var key in mdzKeys)
                {
                    sb.Append(key); sb.Append('='); sb.Append(_mappingDeadZoneDict[key]); sb.Append('|');
                }
            }

            // Per-mapping bidirectional flags (parallel to the deadzone dict).
            // Without these in the checksum, two devices identical except for a
            // per-mapping Bidirectional flag collide on SaveToFile's dedup and
            // the dropped device inherits the survivor's flag.
            EnsureMappingBidirectionalDict();
            if (_mappingBidirectionalDict.Count > 0)
            {
                sb.Append("MBD:");
                var mbdKeys = new List<string>(_mappingBidirectionalDict.Keys);
                mbdKeys.Sort(StringComparer.Ordinal);
                foreach (var key in mbdKeys)
                {
                    sb.Append(key); sb.Append('='); sb.Append(_mappingBidirectionalDict[key]); sb.Append('|');
                }
            }

            // Per-(device, pad) touchpad gesture-detection settings.
            // Same shape as the gyro fix above — without these in the
            // checksum, two devices with otherwise-identical mappings
            // collide on SaveToFile's dedup-by-checksum and the second
            // device's per-pad toggles (EnableRadialZones, EnableTaps,
            // GestureMatchThreshold, the lot) get dropped silently.
            // Symptom: user toggles a touchpad-tab checkbox, autosave
            // fires, on next launch the checkbox is back to its prior
            // value because the PadSetting that actually carried the
            // mutation lost the dedup race.
            if (TouchpadSettings != null && TouchpadSettings.Length > 0)
            {
                sb.Append("TPS:");
                // Sort by (DeviceGuid, TouchpadIndex) so two PadSettings
                // with the same set of entries in different array order
                // still hash identically — checksum is content-defined,
                // not order-defined.
                var sortedEntries = new List<PadForge.Engine.Touchpad.TouchpadSettingsEntry>(TouchpadSettings);
                sortedEntries.Sort((a, b) =>
                {
                    int c = StringComparer.OrdinalIgnoreCase.Compare(a?.DeviceGuid ?? "", b?.DeviceGuid ?? "");
                    if (c != 0) return c;
                    return (a?.TouchpadIndex ?? 0).CompareTo(b?.TouchpadIndex ?? 0);
                });
                foreach (var entry in sortedEntries)
                {
                    if (entry?.Settings == null) continue;
                    var s = entry.Settings;
                    sb.Append(entry.DeviceGuid ?? ""); sb.Append('@');
                    sb.Append(entry.TouchpadIndex); sb.Append(':');
                    sb.Append(s.Enabled).Append(',').Append(s.Mode).Append(',').Append(s.CooldownMs).Append(',');
                    sb.Append(s.SwipeDistanceThreshold).Append(',').Append(s.SwipeTimeWindowMs).Append(',');
                    sb.Append(s.EnableFourWaySwipes).Append(',').Append(s.EnableEightWaySwipes).Append(',');
                    sb.Append(s.EnableRadialZones).Append(',').Append(s.RadialZoneCount).Append(',').Append(s.RadialCenterDeadzone).Append(',');
                    sb.Append(s.EnableTouchSpots).Append(',');
                    sb.Append(s.EnableTaps).Append(',').Append(s.TapTimeWindowMs).Append(',').Append(s.TapMaxMotion).Append(',').Append(s.MultiTapGapMs).Append(',');
                    sb.Append(s.EnableLongPress).Append(',').Append(s.LongPressTimeWindowMs).Append(',').Append(s.LongPressMaxMotion).Append(',');
                    sb.Append(s.EnableTwoFingerSwipes).Append(',').Append(s.TwoFingerSwipeAngularTolerance).Append(',');
                    sb.Append(s.EnablePinchSpread).Append(',').Append(s.PinchThreshold).Append(',');
                    sb.Append(s.EnableRotate).Append(',').Append(s.RotateThresholdDegrees).Append(',');
                    sb.Append(s.EnableThreeFingerGestures).Append(',').Append(s.EnableFourFingerGestures).Append(',').Append(s.EnableFiveFingerGestures).Append(',');
                    sb.Append(s.EnableShapeGestures).Append(',').Append(s.GestureMatchThreshold).Append(',');
                    sb.Append(s.EnableJoystickOutput).Append(',').Append(s.JoystickMaxRadius).Append(',').Append(s.JoystickInnerDeadzone).Append(',');
                    sb.Append(s.JoystickDPadMode).Append(',').Append(s.JoystickDPadActivationThreshold).Append(',');
                    sb.Append(s.MouseSensitivityX).Append(',').Append(s.MouseSensitivityY).Append(',');
                    sb.Append(s.MouseInvertX).Append(',').Append(s.MouseInvertY);
                    sb.Append('|');
                }
            }

            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return BitConverter.ToString(hash, 0, 4).Replace("-", "").ToUpperInvariant();
        }

        /// <summary>
        /// Updates the <see cref="PadSettingChecksum"/> property from the current values.
        /// Call this after modifying any mapping properties.
        /// </summary>
        public void UpdateChecksum()
        {
            PadSettingChecksum = ComputeChecksum();
        }

        // ─────────────────────────────────────────────
        //  Convenience: Check if anything is mapped
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns true if at least one mapping property has a non-empty descriptor.
        /// </summary>
        [XmlIgnore]
        public bool HasAnyMapping =>
            !string.IsNullOrEmpty(ButtonA) ||
            !string.IsNullOrEmpty(ButtonB) ||
            !string.IsNullOrEmpty(ButtonX) ||
            !string.IsNullOrEmpty(ButtonY) ||
            !string.IsNullOrEmpty(LeftShoulder) ||
            !string.IsNullOrEmpty(RightShoulder) ||
            !string.IsNullOrEmpty(ButtonBack) ||
            !string.IsNullOrEmpty(ButtonStart) ||
            !string.IsNullOrEmpty(ButtonGuide) ||
            !string.IsNullOrEmpty(LeftThumbButton) ||
            !string.IsNullOrEmpty(RightThumbButton) ||
            !string.IsNullOrEmpty(ButtonShare) ||
            !string.IsNullOrEmpty(DPad) ||
            !string.IsNullOrEmpty(DPadUp) ||
            !string.IsNullOrEmpty(DPadDown) ||
            !string.IsNullOrEmpty(DPadLeft) ||
            !string.IsNullOrEmpty(DPadRight) ||
            !string.IsNullOrEmpty(LeftTrigger) ||
            !string.IsNullOrEmpty(RightTrigger) ||
            !string.IsNullOrEmpty(LeftThumbAxisX) ||
            !string.IsNullOrEmpty(LeftThumbAxisY) ||
            !string.IsNullOrEmpty(RightThumbAxisX) ||
            !string.IsNullOrEmpty(RightThumbAxisY) ||
            !string.IsNullOrEmpty(LeftThumbAxisXNeg) ||
            !string.IsNullOrEmpty(LeftThumbAxisYNeg) ||
            !string.IsNullOrEmpty(RightThumbAxisXNeg) ||
            !string.IsNullOrEmpty(RightThumbAxisYNeg) ||
            !string.IsNullOrEmpty(TouchpadX1) ||
            !string.IsNullOrEmpty(TouchpadY1) ||
            !string.IsNullOrEmpty(TouchpadX2) ||
            !string.IsNullOrEmpty(TouchpadY2) ||
            !string.IsNullOrEmpty(TouchpadContact1) ||
            !string.IsNullOrEmpty(TouchpadContact2) ||
            !string.IsNullOrEmpty(TouchpadClick) ||
            (ExtendedMappingEntries != null && ExtendedMappingEntries.Length > 0) ||
            (_extendedMappingDict != null && _extendedMappingDict.Count > 0) ||
            (MidiMappingEntries != null && MidiMappingEntries.Length > 0) ||
            (_midiMappingDict != null && _midiMappingDict.Count > 0) ||
            (KbmMappingEntries != null && KbmMappingEntries.Length > 0) ||
            (_kbmMappingDict != null && _kbmMappingDict.Count > 0);

        /// <summary>
        /// Clears all mapping descriptors (standard, Extended, and MIDI) while preserving
        /// deadzone, force feedback, and other non-mapping configuration.
        /// Call before writing a new set of mappings to prevent stale leftovers
        /// from a previous mapping layout (e.g., switching Xbox preset → custom Extended).
        /// </summary>
        public void ClearMappingDescriptors()
        {
            // Standard mapping properties.
            ButtonA = ButtonB = ButtonX = ButtonY = "";
            LeftShoulder = RightShoulder = "";
            ButtonBack = ButtonStart = ButtonGuide = "";
            LeftThumbButton = RightThumbButton = "";
            ButtonShare = "";
            DPad = DPadUp = DPadDown = DPadLeft = DPadRight = "";
            LeftTrigger = RightTrigger = "";
            LeftThumbAxisX = LeftThumbAxisY = "";
            RightThumbAxisX = RightThumbAxisY = "";
            LeftThumbAxisXNeg = LeftThumbAxisYNeg = "";
            RightThumbAxisXNeg = RightThumbAxisYNeg = "";
            TouchpadX1 = TouchpadY1 = TouchpadX2 = TouchpadY2 = "";
            TouchpadContact1 = TouchpadContact2 = TouchpadClick = "";

            // Extended mapping dict: clear only the input-routing descriptors and PRESERVE
            // per-device tuning that shares this dict (steering Stick{g}Steer*, Extended
            // stick/trigger deadzone/range/curve). Nulling the whole dict here destroyed a
            // device's steering on every save that ran the descriptor-bleed cleanup (a
            // device-switch flush): the steering keys vanished and read back as Direct on the
            // next load. Stick deadzone/range live in named properties, which is why only
            // steering (and Extended tuning) hit this.
            if (_extendedMappingDict != null
                || (ExtendedMappingEntries != null && ExtendedMappingEntries.Length > 0))
            {
                EnsureExtendedDict();
                var preserved = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in _extendedMappingDict)
                    if (IsPerDeviceTuningKey(kvp.Key))
                        preserved[kvp.Key] = kvp.Value;
                _extendedMappingDict = preserved;
            }
            ExtendedMappingEntries = null; // re-flushed from the dict on save

            // MIDI/KBM mapping dictionaries and arrays (no tuning shares these).
            MidiMappingEntries = null;
            _midiMappingDict = null;
            KbmMappingEntries = null;
            _kbmMappingDict = null;
        }

        /// <summary>True for Extended-dict keys that are per-device TUNING (steering mode +
        /// tunables, Extended stick/trigger deadzone/range/curve) rather than input-routing
        /// descriptors. These survive <see cref="ClearMappingDescriptors"/> so a descriptor
        /// rewrite can't wipe a device's tuning.</summary>
        private static bool IsPerDeviceTuningKey(string k)
        {
            if (string.IsNullOrEmpty(k)) return false;
            if (k.StartsWith("Stick", StringComparison.Ordinal) && k.Contains("Steer", StringComparison.Ordinal))
                return true;
            // Motion Lean input tuning (gyro tab's Motion Steering card):
            // MotionSteerInner / MotionSteerOuter / MotionSteerOrient. Per-device
            // tuning, not an input-routing descriptor — without this, the
            // descriptor-bleed cleanup reset a non-selected device's tilt setup
            // to defaults on every device-switch save.
            if (k.StartsWith("MotionSteer", StringComparison.Ordinal))
                return true;
            if (k.StartsWith("ExtendedStick", StringComparison.Ordinal))
                return true;
            if (k.StartsWith("ExtendedTrigger", StringComparison.Ordinal)
                && (k.EndsWith("Dz", StringComparison.Ordinal) || k.EndsWith("Adz", StringComparison.Ordinal)
                    || k.EndsWith("Mr", StringComparison.Ordinal) || k.EndsWith("Curve", StringComparison.Ordinal)))
                return true;
            return false;
        }

        /// <summary>
        /// Returns all non-empty mapping descriptor strings from this PadSetting.
        /// Includes standard button/axis/dpad/trigger mappings, Extended, and MIDI custom entries.
        /// </summary>
        public List<string> GetAllMappingDescriptors()
        {
            var result = new List<string>();
            void Add(string d) { if (!string.IsNullOrEmpty(d)) result.Add(d); }

            // Buttons
            Add(ButtonA); Add(ButtonB); Add(ButtonX); Add(ButtonY);
            Add(LeftShoulder); Add(RightShoulder);
            Add(ButtonBack); Add(ButtonStart); Add(ButtonGuide);
            Add(LeftThumbButton); Add(RightThumbButton);
            Add(ButtonShare);

            // D-Pad
            Add(DPad); Add(DPadUp); Add(DPadDown); Add(DPadLeft); Add(DPadRight);

            // Triggers
            Add(LeftTrigger); Add(RightTrigger);

            // Thumbstick axes
            Add(LeftThumbAxisX); Add(LeftThumbAxisY);
            Add(RightThumbAxisX); Add(RightThumbAxisY);
            Add(LeftThumbAxisXNeg); Add(LeftThumbAxisYNeg);
            Add(RightThumbAxisXNeg); Add(RightThumbAxisYNeg);

            // Touchpad
            Add(TouchpadX1); Add(TouchpadY1);
            Add(TouchpadX2); Add(TouchpadY2);
            Add(TouchpadContact1); Add(TouchpadContact2);
            Add(TouchpadClick);

            // Extended custom mappings
            if (ExtendedMappingEntries != null)
            {
                foreach (var e in ExtendedMappingEntries)
                    Add(e.Value);
            }

            // MIDI custom mappings
            if (MidiMappingEntries != null)
            {
                foreach (var e in MidiMappingEntries)
                    Add(e.Value);
            }

            // KBM custom mappings
            if (KbmMappingEntries != null)
            {
                foreach (var e in KbmMappingEntries)
                    Add(e.Value);
            }

            return result;
        }

        // ─────────────────────────────────────────────
        //  Display
        // ─────────────────────────────────────────────

        public override string ToString()
        {
            int count = 0;
            if (!string.IsNullOrEmpty(ButtonA)) count++;
            if (!string.IsNullOrEmpty(ButtonB)) count++;
            if (!string.IsNullOrEmpty(ButtonX)) count++;
            if (!string.IsNullOrEmpty(ButtonY)) count++;
            if (!string.IsNullOrEmpty(LeftThumbAxisX)) count++;
            if (!string.IsNullOrEmpty(LeftThumbAxisY)) count++;
            if (!string.IsNullOrEmpty(RightThumbAxisX)) count++;
            if (!string.IsNullOrEmpty(RightThumbAxisY)) count++;
            if (!string.IsNullOrEmpty(LeftTrigger)) count++;
            if (!string.IsNullOrEmpty(RightTrigger)) count++;

            return $"PadSetting [{PadSettingChecksum}] ({count} mapped)";
        }

        // ─────────────────────────────────────────────
        //  JSON serialization for copy/paste
        // ─────────────────────────────────────────────

        /// <summary>Names of all copyable properties (excludes identity and game-specific fields).</summary>
        private static readonly string[] CopyablePropertyNames = new[]
        {
            // Buttons
            nameof(ButtonA), nameof(ButtonB), nameof(ButtonX), nameof(ButtonY),
            nameof(LeftShoulder), nameof(RightShoulder),
            nameof(ButtonBack), nameof(ButtonStart), nameof(ButtonGuide),
            nameof(ButtonShare),
            nameof(LeftThumbButton), nameof(RightThumbButton),
            // D-Pad
            nameof(DPad), nameof(DPadUp), nameof(DPadDown), nameof(DPadLeft), nameof(DPadRight),
            // Triggers
            nameof(LeftTrigger), nameof(RightTrigger),
            nameof(LeftTriggerDeadZone), nameof(RightTriggerDeadZone),
            nameof(LeftTriggerAntiDeadZone), nameof(RightTriggerAntiDeadZone),
            nameof(LeftTriggerMaxRange), nameof(RightTriggerMaxRange),
            // Sticks
            nameof(LeftThumbAxisX), nameof(LeftThumbAxisY),
            nameof(RightThumbAxisX), nameof(RightThumbAxisY),
            nameof(LeftThumbAxisXNeg), nameof(LeftThumbAxisYNeg),
            nameof(RightThumbAxisXNeg), nameof(RightThumbAxisYNeg),
            // Dead zones
            nameof(LeftThumbDeadZoneX), nameof(LeftThumbDeadZoneY),
            nameof(RightThumbDeadZoneX), nameof(RightThumbDeadZoneY),
            nameof(LeftThumbDeadZoneShape), nameof(RightThumbDeadZoneShape),
            nameof(LeftThumbAntiDeadZone), nameof(RightThumbAntiDeadZone),
            nameof(LeftThumbAntiDeadZoneX), nameof(LeftThumbAntiDeadZoneY),
            nameof(RightThumbAntiDeadZoneX), nameof(RightThumbAntiDeadZoneY),
            nameof(LeftThumbLinear), nameof(RightThumbLinear),
            nameof(LeftThumbSensitivityCurveX), nameof(LeftThumbSensitivityCurveY),
            nameof(RightThumbSensitivityCurveX), nameof(RightThumbSensitivityCurveY),
            nameof(LeftTriggerSensitivityCurve), nameof(RightTriggerSensitivityCurve),
            nameof(LeftThumbMaxRangeX), nameof(LeftThumbMaxRangeY),
            nameof(RightThumbMaxRangeX), nameof(RightThumbMaxRangeY),
            nameof(LeftThumbMaxRangeXNeg), nameof(LeftThumbMaxRangeYNeg),
            nameof(RightThumbMaxRangeXNeg), nameof(RightThumbMaxRangeYNeg),
            nameof(LeftThumbCenterOffsetX), nameof(LeftThumbCenterOffsetY),
            nameof(RightThumbCenterOffsetX), nameof(RightThumbCenterOffsetY),
            nameof(LeftThumbBoundaryMap), nameof(RightThumbBoundaryMap),
            // Force feedback
            nameof(ForceType), nameof(ForceOverall), nameof(ForceSwapMotor),
            nameof(LeftMotorStrength), nameof(RightMotorStrength),
            nameof(RotationRange), nameof(AutoCenterStrength), nameof(WheelRpmLeds),
            // Steering at-lock feedback (#94)
            nameof(SteeringLockRumbleEnabled), nameof(SteeringLockTriggerVibEnabled),
            nameof(SteeringLockLightbarEnabled), nameof(SteeringLockATResistanceEnabled),
            nameof(SteeringLockPulseMs), nameof(SteeringLockLightbarColor), nameof(SteeringLockLightbarFadeMs),
            nameof(SteeringLockLightbarColorSource), nameof(SteeringLockLightbarPaletteCsv),
            nameof(SteeringLockLightbarHoldMs),
            // Impulse trigger motors (Xbox One+)
            nameof(ImpulseOverallGain),
            nameof(ImpulseLeftStrength), nameof(ImpulseRightStrength),
            nameof(ImpulseSwapTriggers),
            nameof(ConstantTriggerForceEnabled),
            nameof(ConstantTriggerForceLeft), nameof(ConstantTriggerForceRight),
            nameof(AudioRumbleTriggersEnabled),
            nameof(AudioRumbleTriggersSensitivity), nameof(AudioRumbleTriggersCutoffHz),
            nameof(AudioRumbleLeftTrigger), nameof(AudioRumbleRightTrigger),
            // Trigger rumble routing (#102). Omitting these from the clone list
            // makes the Trigger Routing card revert to defaults on restart and
            // breaks copy/paste, the same failure the Gyro tuning note below warns about.
            nameof(LeftTriggerRouteSource), nameof(RightTriggerRouteSource),
            nameof(LeftTriggerRouteMode), nameof(RightTriggerRouteMode),
            nameof(LeftTriggerRouteScale), nameof(RightTriggerRouteScale),
            nameof(LeftTriggerRouteActivator), nameof(RightTriggerRouteActivator),
            nameof(LeftTriggerRouteActivatorDeviceGuid), nameof(RightTriggerRouteActivatorDeviceGuid),
            nameof(LeftTriggerRouteActivatorMode), nameof(RightTriggerRouteActivatorMode),
            // Audio bass rumble
            nameof(AudioRumbleEnabled), nameof(AudioRumbleSensitivity),
            nameof(AudioRumbleCutoffHz), nameof(AudioRumbleLeftMotor), nameof(AudioRumbleRightMotor),
            // Constant force
            nameof(ConstantForceEnabled), nameof(ConstantForceX), nameof(ConstantForceY),
            // Gyro tuning — per-(device, slot). Omitting these from the
            // clone list is the bug that makes Gyro tab sliders revert on
            // restart: LoadFromFile's CloneDeep would drop them, leaving
            // every in-memory PadSetting at constructor defaults regardless
            // of what the XML round-tripped through the XmlSerializer.
            nameof(GyroSensitivityH), nameof(GyroSensitivityV),
            nameof(GyroDeadZoneDegPerSec), nameof(GyroSmoothingAlpha),
            nameof(GyroAcceleration), nameof(GyroOutputCurve),
            nameof(GyroSensitivityUnits), nameof(GyroEasyAimStickThreshold),
            nameof(GyroEngageStickSide), nameof(GyroEngageStickDirection),
            nameof(IrSensorBarPos), nameof(IrSensorBarComp), nameof(IrSmoothing),
            nameof(GyroBiasPitch), nameof(GyroBiasYaw), nameof(GyroBiasRoll),
            nameof(GyroCalibratedAtUtc),
            nameof(GyroSpace), nameof(GyroPlayerSpaceYawRelaxFactor),
            nameof(GyroWorldSpaceSideReductionThreshold),
            nameof(GyroTighteningThresholdDegPerSec),
            nameof(GyroSmoothingThresholdDegPerSec),
            nameof(GyroSmoothingWindowMs), nameof(GyroRealWorldCalibration),
            nameof(GyroAimEngageButton), nameof(GyroAimEngageDeviceGuid),
            nameof(GyroAimEngageMode),
            nameof(GyroInvertPitch), nameof(GyroInvertYawRoll),
            nameof(GyroApplyTuningToPassthrough),
            // Axis inversion
            nameof(LeftThumbAxisXInvert), nameof(LeftThumbAxisYInvert),
            nameof(RightThumbAxisXInvert), nameof(RightThumbAxisYInvert),
            // Threshold
            nameof(AxisToButtonThreshold),
            // Touchpad
            nameof(TouchpadX1), nameof(TouchpadY1),
            nameof(TouchpadX2), nameof(TouchpadY2),
            nameof(TouchpadContact1), nameof(TouchpadContact2),
            nameof(TouchpadClick),
            // Motion passthrough
            nameof(MotionGyro), nameof(MotionAccel),
        };

        /// <summary>Optional payload populated by the clipboard copy
        /// path with this device's slice of the slot's MappingSet
        /// rows — i.e. every Base/Shift row where this device's
        /// GUID appears in Sources, with only those device-owned
        /// Sources retained. Round-trips multi-source ExtraSources,
        /// CombineMode and CombineExpression across Copy / Paste /
        /// Copy From. Not serialised by the on-disk XML path.</summary>
        [System.Xml.Serialization.XmlIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Collections.Generic.List<MappingRow> DeviceScopedMultiSourceRows { get; set; }

        /// <summary>Whole-slot snapshot of every row in the source
        /// slot's MappingSet, with source DeviceGuids preserved as-is.
        /// Used by Copy / Paste so a multi-device slot round-trips ALL
        /// devices' contributions instead of just the source slot's
        /// currently-selected device. Paste replaces the target slot's
        /// MappingSet wholesale from this list; sources whose device
        /// isn't on the target slot stay in the table but are inert
        /// until that device is assigned. Not serialised to the on-disk
        /// XML — this only travels through clipboard JSON.</summary>
        [System.Xml.Serialization.XmlIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Collections.Generic.List<MappingRow> SlotMultiSourceRows { get; set; }

        /// <summary>Opaque JSON payload carrying the PlayStation slot
        /// configs (Lighting / Adaptive Triggers / Mic LED / Player LED
        /// / audio-reactive / palette) — one entry per (slot, device)
        /// plus the slot anchor. Set by the App-side Copy path; consumed
        /// by the App-side Paste path. PadSetting just round-trips the
        /// string verbatim so the Engine stays free of App-ViewModel
        /// references.</summary>
        [System.Xml.Serialization.XmlIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public string SlotPlayStationConfigsJson { get; set; }

        /// <summary>Opaque JSON payload for the Extended custom layout
        /// snapshot (thumbstick / trigger / POV / button counts, OEM /
        /// Product strings, FFB toggle).</summary>
        [System.Xml.Serialization.XmlIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public string SlotExtendedConfigJson { get; set; }

        /// <summary>Opaque JSON payload for the MIDI slot layout
        /// snapshot (channel, velocity, CC + note ranges).</summary>
        [System.Xml.Serialization.XmlIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public string SlotMidiConfigJson { get; set; }

        /// <summary>Opaque JSON payload for the slot's shift authoring
        /// (ShiftActivators + Base flyout appearance), so Copy / Paste carries
        /// shift layers like Copy From does (#119).</summary>
        [System.Xml.Serialization.XmlIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public string SlotShiftActivatorsJson { get; set; }

        /// <summary>Opaque JSON payload carrying every device's PadSetting
        /// on the source slot. The outer PadSetting that wraps this field
        /// still carries the originally-selected device's tuning (legacy
        /// shape); this array carries the FULL set so all devices'
        /// per-device tuning (deadzones, sensitivity curves, FFB, Gyro,
        /// TouchpadSettings) round-trips through Copy / Paste and Copy
        /// From. Format: <see cref="PerDeviceSettingsEntry"/>[] serialized
        /// via <c>System.Text.Json</c>. Each entry's PadSettingJson is a
        /// nested PadSetting.ToJson() string with slot-level fields
        /// (this one included) zeroed so the nesting doesn't recurse.
        /// Set by the App-side Copy path; consumed by the App-side Paste
        /// path. PadSetting just round-trips the string verbatim so the
        /// Engine stays free of App-ViewModel references.</summary>
        [System.Xml.Serialization.XmlIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public string SlotPerDeviceSettingsJson { get; set; }

        /// <summary>
        /// Serializes all copyable mapping/deadzone/FF properties to a JSON string.
        /// Used for clipboard copy/paste of controller settings.
        /// </summary>
        public string ToJson(VirtualControllerType outputType = VirtualControllerType.Xbox, bool isExtended = false)
        {
            // Flush live dicts to arrays before serializing.
            FlushExtendedMappings();
            FlushMidiMappings();
            FlushKbmMappings();
            FlushMappingDeadZones();
            FlushMappingBidirectional();

            var dict = new Dictionary<string, string>();
            var type = GetType();

            // Embed layout metadata for cross-layout paste support.
            dict["__OutputType"] = ((int)outputType).ToString();
            dict["__IsExtended"] = isExtended ? "1" : "0";

            foreach (string name in CopyablePropertyNames)
            {
                var prop = type.GetProperty(name);
                if (prop != null)
                    dict[name] = prop.GetValue(this) as string ?? "";
            }

            // Include Extended/MIDI/KBM mapping arrays if present.
            if (ExtendedMappingEntries != null && ExtendedMappingEntries.Length > 0)
            {
                var extendedList = new List<Dictionary<string, string>>();
                foreach (var e in ExtendedMappingEntries)
                    extendedList.Add(new Dictionary<string, string> { ["Key"] = e.Key, ["Value"] = e.Value });
                dict["__ExtendedMappings"] = JsonSerializer.Serialize(extendedList);
            }
            if (MidiMappingEntries != null && MidiMappingEntries.Length > 0)
            {
                var midiList = new List<Dictionary<string, string>>();
                foreach (var e in MidiMappingEntries)
                    midiList.Add(new Dictionary<string, string> { ["Key"] = e.Key, ["Value"] = e.Value });
                dict["__MidiMappings"] = JsonSerializer.Serialize(midiList);
            }
            if (KbmMappingEntries != null && KbmMappingEntries.Length > 0)
            {
                var kbmList = new List<Dictionary<string, string>>();
                foreach (var e in KbmMappingEntries)
                    kbmList.Add(new Dictionary<string, string> { ["Key"] = e.Key, ["Value"] = e.Value });
                dict["__KbmMappings"] = JsonSerializer.Serialize(kbmList);
            }
            if (MappingDeadZoneEntries != null && MappingDeadZoneEntries.Length > 0)
            {
                var mdzList = new List<Dictionary<string, string>>();
                foreach (var e in MappingDeadZoneEntries)
                    mdzList.Add(new Dictionary<string, string> { ["Key"] = e.Key, ["Value"] = e.Value });
                dict["__MappingDeadZones"] = JsonSerializer.Serialize(mdzList);
            }
            if (MappingBidirectionalEntries != null && MappingBidirectionalEntries.Length > 0)
            {
                var bidirList = new List<Dictionary<string, string>>();
                foreach (var e in MappingBidirectionalEntries)
                    bidirList.Add(new Dictionary<string, string> { ["Key"] = e.Key, ["Value"] = e.Value });
                dict["__MappingBidirectional"] = JsonSerializer.Serialize(bidirList);
            }

            // v3.3 — per-(device, pad) Touchpad-tab settings (gesture
            // toggles + thresholds + Stick/D-Pad output + Mouse output).
            // Without this, Copy / Paste of a slot's settings silently
            // wipes the target's Touchpad-tab state because CopyFrom calls
            // DeepCopyTouchpadSettings with a null source, which returns
            // null and overwrites the target's existing array.
            if (TouchpadSettings != null && TouchpadSettings.Length > 0)
            {
                dict["__TouchpadSettings"] = JsonSerializer.Serialize(TouchpadSettings);
            }

            // Opaque per-slot config snapshots (Lighting / Adaptive Triggers
            // / Mic LED / Player LED / audio-reactive / palette for
            // PlayStation, custom layout for Extended, CC + note layout
            // for MIDI). The caller serialises the App-side DTOs into
            // these strings; PadSetting just round-trips them. Keeps the
            // Engine assembly free of dependencies on App ViewModels.
            if (!string.IsNullOrEmpty(SlotPlayStationConfigsJson))
                dict["__SlotPlayStationConfigs"] = SlotPlayStationConfigsJson;
            if (!string.IsNullOrEmpty(SlotExtendedConfigJson))
                dict["__SlotExtendedConfig"] = SlotExtendedConfigJson;
            if (!string.IsNullOrEmpty(SlotMidiConfigJson))
                dict["__SlotMidiConfig"] = SlotMidiConfigJson;
            if (!string.IsNullOrEmpty(SlotShiftActivatorsJson))
                dict["__SlotShiftActivators"] = SlotShiftActivatorsJson;
            if (!string.IsNullOrEmpty(SlotPerDeviceSettingsJson))
                dict["__SlotPerDeviceSettings"] = SlotPerDeviceSettingsJson;

            // Issue #61 — round-trip the slot's multi-source row data
            // for this device. Each row snapshot carries Target,
            // LayerMask, CombineMode, CombineExpression, and only
            // the Sources owned by this device. On paste, the apply
            // path substitutes the target device's GUID into those
            // Sources before merging into the target slot's MappingSet.
            if (DeviceScopedMultiSourceRows != null && DeviceScopedMultiSourceRows.Count > 0)
            {
                dict["__MultiSourceRows"] = JsonSerializer.Serialize(DeviceScopedMultiSourceRows);
            }

            // Whole-slot snapshot — preserves source DeviceGuids so multi-
            // device slots survive Copy / Paste.
            if (SlotMultiSourceRows != null && SlotMultiSourceRows.Count > 0)
            {
                dict["__SlotRows"] = JsonSerializer.Serialize(SlotMultiSourceRows);
            }

            return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Deserializes a JSON string into a new PadSetting.
        /// Returns null if the JSON is invalid or not a PadSetting export.
        /// Also extracts embedded layout metadata (OutputType, IsExtended) if present.
        /// </summary>
        public static PadSetting FromJson(string json)
            => FromJson(json, out _, out _);

        /// <summary>
        /// Deserializes a JSON string into a new PadSetting, also returning the
        /// source layout type embedded in the JSON (if any).
        /// </summary>
        public static PadSetting FromJson(string json,
            out VirtualControllerType sourceOutputType, out bool sourceIsExtended)
        {
            sourceOutputType = VirtualControllerType.Xbox;
            sourceIsExtended = false;

            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict == null || dict.Count == 0)
                    return null;

                // Extract layout metadata.
                if (dict.TryGetValue("__OutputType", out var otStr) && int.TryParse(otStr, out int otVal)
                    && Enum.IsDefined(typeof(VirtualControllerType), otVal))
                    sourceOutputType = (VirtualControllerType)otVal;
                if (dict.TryGetValue("__IsExtended", out var cvStr))
                    sourceIsExtended = cvStr == "1";

                var ps = new PadSetting();
                var type = typeof(PadSetting);

                foreach (var kvp in dict)
                {
                    if (kvp.Key.StartsWith("__"))
                    {
                        if (kvp.Key == "__ExtendedMappings")
                            ps.ExtendedMappingEntries = DeserializeMappingArray(kvp.Value);
                        else if (kvp.Key == "__MidiMappings")
                            ps.MidiMappingEntries = DeserializeMappingArray(kvp.Value);
                        else if (kvp.Key == "__KbmMappings")
                            ps.KbmMappingEntries = DeserializeMappingArray(kvp.Value);
                        else if (kvp.Key == "__MappingDeadZones")
                            ps.MappingDeadZoneEntries = DeserializeMappingArray(kvp.Value);
                        else if (kvp.Key == "__MultiSourceRows")
                        {
                            try
                            {
                                ps.DeviceScopedMultiSourceRows =
                                    JsonSerializer.Deserialize<System.Collections.Generic.List<MappingRow>>(kvp.Value)
                                    ?? new System.Collections.Generic.List<MappingRow>();
                            }
                            catch { /* malformed payload — paste degrades to single-source */ }
                        }
                        else if (kvp.Key == "__SlotRows")
                        {
                            try
                            {
                                ps.SlotMultiSourceRows =
                                    JsonSerializer.Deserialize<System.Collections.Generic.List<MappingRow>>(kvp.Value)
                                    ?? new System.Collections.Generic.List<MappingRow>();
                            }
                            catch { /* malformed — paste falls back to device-scoped or single-source */ }
                        }
                        else if (kvp.Key == "__MappingBidirectional")
                            ps.MappingBidirectionalEntries = DeserializeMappingArray(kvp.Value);
                        else if (kvp.Key == "__TouchpadSettings")
                        {
                            try
                            {
                                ps.TouchpadSettings = JsonSerializer.Deserialize<
                                    PadForge.Engine.Touchpad.TouchpadSettingsEntry[]>(kvp.Value);
                            }
                            catch { /* malformed payload — leave TouchpadSettings null */ }
                        }
                        else if (kvp.Key == "__SlotPlayStationConfigs")
                            ps.SlotPlayStationConfigsJson = kvp.Value;
                        else if (kvp.Key == "__SlotExtendedConfig")
                            ps.SlotExtendedConfigJson = kvp.Value;
                        else if (kvp.Key == "__SlotMidiConfig")
                            ps.SlotMidiConfigJson = kvp.Value;
                        else if (kvp.Key == "__SlotShiftActivators")
                            ps.SlotShiftActivatorsJson = kvp.Value;
                        else if (kvp.Key == "__SlotPerDeviceSettings")
                            ps.SlotPerDeviceSettingsJson = kvp.Value;
                        continue;
                    }
                    var prop = type.GetProperty(kvp.Key);
                    if (prop != null && prop.PropertyType == typeof(string) && prop.CanWrite)
                        prop.SetValue(ps, kvp.Value ?? "");
                }

                return ps;
            }
            catch
            {
                return null;
            }
        }

        private static ExtendedMappingEntry[] DeserializeMappingArray(string json)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
                if (list == null) return null;
                var arr = new ExtendedMappingEntry[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    arr[i] = new ExtendedMappingEntry
                    {
                        Key = list[i].TryGetValue("Key", out var k) ? k : "",
                        Value = list[i].TryGetValue("Value", out var v) ? v : ""
                    };
                }
                return arr;
            }
            catch { return null; }
        }

        /// <summary>
        /// Names of mapping properties that identify input sources (buttons, axes, d-pad).
        /// These need positional translation when copying between different layouts.
        /// </summary>
        private static readonly HashSet<string> MappingPropertyNames = new()
        {
            nameof(ButtonA), nameof(ButtonB), nameof(ButtonX), nameof(ButtonY),
            nameof(LeftShoulder), nameof(RightShoulder),
            nameof(ButtonBack), nameof(ButtonStart), nameof(ButtonGuide),
            nameof(ButtonShare),
            nameof(LeftThumbButton), nameof(RightThumbButton),
            nameof(DPad), nameof(DPadUp), nameof(DPadDown), nameof(DPadLeft), nameof(DPadRight),
            nameof(LeftTrigger), nameof(RightTrigger),
            nameof(LeftThumbAxisX), nameof(LeftThumbAxisY),
            nameof(RightThumbAxisX), nameof(RightThumbAxisY),
            nameof(LeftThumbAxisXNeg), nameof(LeftThumbAxisYNeg),
            nameof(RightThumbAxisXNeg), nameof(RightThumbAxisYNeg),
        };

        /// <summary>
        /// Copies mappings from another PadSetting with cross-layout translation.
        /// When source and target use the same layout, delegates to <see cref="CopyFrom"/>.
        /// When layouts differ, translates mapping positions (e.g., ButtonA → ExtendedBtn0)
        /// and copies non-mapping settings (deadzones, sensitivity, FFB) directly.
        /// </summary>
        public void CopyFromTranslated(PadSetting source,
            VirtualControllerType sourceType, bool sourceIsExtended,
            VirtualControllerType targetType, bool targetIsExtended)
        {
            if (source == null) return;

            // Same layout? Use direct copy.
            if (MappingTranslation.IsSameLayout(sourceType, sourceIsExtended, targetType, targetIsExtended))
            {
                CopyFrom(source);
                return;
            }

            source.FlushExtendedMappings();
            source.FlushMidiMappings();
            source.FlushKbmMappings();

            // Step 1: Copy non-mapping settings directly (deadzones, sensitivity, FFB, etc.)
            // These use the same property names regardless of output layout.
            var type = GetType();
            foreach (string name in CopyablePropertyNames)
            {
                if (MappingPropertyNames.Contains(name))
                    continue; // Skip mapping properties — they need translation.
                var prop = type.GetProperty(name);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(this, prop.GetValue(source) ?? "");
            }

            // Step 2: Collect all source mappings as (position → descriptor value).
            var translated = new Dictionary<MappingSlot, string>();

            // Read from gamepad properties (Xbox / PlayStation / Extended gamepad preset source)
            if (sourceType != VirtualControllerType.Midi &&
                sourceType != VirtualControllerType.KeyboardMouse &&
                !(sourceType == VirtualControllerType.Extended && sourceIsExtended))
            {
                foreach (string propName in MappingPropertyNames)
                {
                    var prop = type.GetProperty(propName);
                    if (prop == null) continue;
                    string val = prop.GetValue(source) as string ?? "";
                    if (string.IsNullOrEmpty(val)) continue;

                    var slot = MappingTranslation.GetPosition(propName, sourceType, false);
                    if (slot != null)
                        translated[slot] = val;
                }
            }

            // Read from Extended dictionary (Extended custom source)
            if (sourceType == VirtualControllerType.Extended && sourceIsExtended
                && source.ExtendedMappingEntries != null)
            {
                foreach (var e in source.ExtendedMappingEntries)
                {
                    if (string.IsNullOrEmpty(e.Key) || string.IsNullOrEmpty(e.Value)) continue;
                    var slot = MappingTranslation.GetPosition(e.Key, sourceType, true);
                    if (slot != null)
                        translated[slot] = e.Value;
                }
            }

            // Read from MIDI dictionary (MIDI source)
            if (sourceType == VirtualControllerType.Midi && source.MidiMappingEntries != null)
            {
                foreach (var e in source.MidiMappingEntries)
                {
                    if (string.IsNullOrEmpty(e.Key) || string.IsNullOrEmpty(e.Value)) continue;
                    var slot = MappingTranslation.GetPosition(e.Key, sourceType, false);
                    if (slot != null)
                        translated[slot] = e.Value;
                }
            }

            // Read from KBM dictionary (KeyboardMouse source)
            if (sourceType == VirtualControllerType.KeyboardMouse && source.KbmMappingEntries != null)
            {
                foreach (var e in source.KbmMappingEntries)
                {
                    if (string.IsNullOrEmpty(e.Key) || string.IsNullOrEmpty(e.Value)) continue;
                    var slot = MappingTranslation.GetPosition(e.Key, sourceType, false);
                    if (slot != null)
                        translated[slot] = e.Value;
                }
            }

            // Step 3: Write translated positions to target layout.

            // Clear existing target mappings first.
            if (targetType == VirtualControllerType.Extended && targetIsExtended)
            {
                ExtendedMappingEntries = null;
                _extendedMappingDict = null;
            }
            else if (targetType == VirtualControllerType.Midi)
            {
                MidiMappingEntries = null;
                _midiMappingDict = null;
            }
            else if (targetType == VirtualControllerType.KeyboardMouse)
            {
                KbmMappingEntries = null;
                _kbmMappingDict = null;
            }
            else
            {
                // Gamepad target: clear standard mapping properties.
                foreach (string propName in MappingPropertyNames)
                {
                    var prop = type.GetProperty(propName);
                    if (prop != null && prop.CanWrite)
                        prop.SetValue(this, "");
                }
            }

            // Write translated values.
            foreach (var kvp in translated)
            {
                string targetKey = MappingTranslation.GetPropertyName(kvp.Key, targetType, targetIsExtended);
                if (targetKey == null) continue; // No equivalent in target layout — silently dropped.

                if (targetType == VirtualControllerType.Extended && targetIsExtended)
                    SetExtendedMapping(targetKey, kvp.Value);
                else if (targetType == VirtualControllerType.Midi)
                    SetMidiMapping(targetKey, kvp.Value);
                else if (targetType == VirtualControllerType.KeyboardMouse)
                    SetKbmMapping(targetKey, kvp.Value);
                else
                {
                    // Gamepad target: write to standard property.
                    var prop = type.GetProperty(targetKey);
                    if (prop != null && prop.CanWrite)
                        prop.SetValue(this, kvp.Value);
                }
            }

            // Flush dictionaries to arrays for persistence.
            FlushExtendedMappings();
            FlushMidiMappings();
            FlushKbmMappings();
            FlushMappingDeadZones();
            FlushMappingBidirectional();
        }

        /// <summary>
        /// Copies all copyable properties from another PadSetting into this one.
        /// </summary>
        public void CopyFrom(PadSetting source)
        {
            if (source == null) return;

            var type = GetType();
            foreach (string name in CopyablePropertyNames)
            {
                var prop = type.GetProperty(name);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(this, prop.GetValue(source) ?? "");
            }

            // Flush source dicts to arrays so we copy the latest live data
            // (SetExtendedMapping/SetMidiMapping update the dict, not the array).
            source.FlushExtendedMappings();
            source.FlushMidiMappings();
            source.FlushKbmMappings();
            source.FlushMappingDeadZones();
            source.FlushMappingBidirectional();

            // Deep-copy arrays and invalidate our cached dictionaries.
            ExtendedMappingEntries = DeepCopyMappings(source.ExtendedMappingEntries);
            _extendedMappingDict = null;
            MidiMappingEntries = DeepCopyMappings(source.MidiMappingEntries);
            _midiMappingDict = null;
            KbmMappingEntries = DeepCopyMappings(source.KbmMappingEntries);
            _kbmMappingDict = null;
            MappingDeadZoneEntries = DeepCopyMappings(source.MappingDeadZoneEntries);
            _mappingDeadZoneDict = null;
            MappingBidirectionalEntries = DeepCopyMappings(source.MappingBidirectionalEntries);
            _mappingBidirectionalDict = null;

            // Touchpad gesture settings — typed per-(device, pad) entries.
            // Reflection-driven CopyablePropertyNames can't touch typed
            // arrays (it would coerce null to "" via the ?? "" guard and
            // throw on SetValue), so they need a dedicated deep-copy
            // parallel to the mapping arrays above. Without this clone,
            // SnapshotCurrentProfile drops the entries entirely and any
            // named-profile save loses every per-pad gesture setting.
            TouchpadSettings = DeepCopyTouchpadSettings(source.TouchpadSettings);
        }

        private static PadForge.Engine.Touchpad.TouchpadSettingsEntry[] DeepCopyTouchpadSettings(
            PadForge.Engine.Touchpad.TouchpadSettingsEntry[] src)
        {
            if (src == null || src.Length == 0) return null;
            var arr = new PadForge.Engine.Touchpad.TouchpadSettingsEntry[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var s = src[i];
                if (s == null) continue;
                arr[i] = new PadForge.Engine.Touchpad.TouchpadSettingsEntry
                {
                    DeviceGuid = s.DeviceGuid,
                    TouchpadIndex = s.TouchpadIndex,
                    Settings = s.Settings?.Clone(),
                };
            }
            return arr;
        }

        private static ExtendedMappingEntry[] DeepCopyMappings(ExtendedMappingEntry[] src)
        {
            if (src == null || src.Length == 0) return null;
            var arr = new ExtendedMappingEntry[src.Length];
            for (int i = 0; i < src.Length; i++)
                arr[i] = new ExtendedMappingEntry { Key = src[i].Key, Value = src[i].Value };
            return arr;
        }

        /// <summary>
        /// Creates a deep copy of this PadSetting (copies all properties + checksum).
        /// </summary>
        public PadSetting CloneDeep()
        {
            var clone = new PadSetting();
            clone.CopyFrom(this);
            clone.PadSettingChecksum = PadSettingChecksum;
            return clone;
        }
    }

    /// <summary>
    /// Key-value entry for Extended/MIDI mapping persistence in XML.
    /// </summary>
    public class ExtendedMappingEntry
    {
        [XmlAttribute] public string Key { get; set; } = "";
        [XmlAttribute] public string Value { get; set; } = "";
    }
}
