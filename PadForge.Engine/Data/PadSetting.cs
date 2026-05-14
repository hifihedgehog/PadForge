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

            // Force feedback
            sb.Append(ForceType); sb.Append('|');
            sb.Append(ForceOverall); sb.Append('|');
            sb.Append(ForceSwapMotor); sb.Append('|');
            sb.Append(LeftMotorStrength); sb.Append('|');
            sb.Append(RightMotorStrength); sb.Append('|');

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

            // Inversion overrides
            sb.Append(LeftThumbAxisXInvert); sb.Append('|');
            sb.Append(LeftThumbAxisYInvert); sb.Append('|');
            sb.Append(RightThumbAxisXInvert); sb.Append('|');
            sb.Append(RightThumbAxisYInvert); sb.Append('|');

            sb.Append(AxisToButtonThreshold); sb.Append('|');

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
            DPad = DPadUp = DPadDown = DPadLeft = DPadRight = "";
            LeftTrigger = RightTrigger = "";
            LeftThumbAxisX = LeftThumbAxisY = "";
            RightThumbAxisX = RightThumbAxisY = "";
            LeftThumbAxisXNeg = LeftThumbAxisYNeg = "";
            RightThumbAxisXNeg = RightThumbAxisYNeg = "";
            TouchpadX1 = TouchpadY1 = TouchpadX2 = TouchpadY2 = "";
            TouchpadContact1 = TouchpadContact2 = TouchpadClick = "";

            // Extended/MIDI/KBM mapping dictionaries and arrays.
            ExtendedMappingEntries = null;
            _extendedMappingDict = null;
            MidiMappingEntries = null;
            _midiMappingDict = null;
            KbmMappingEntries = null;
            _kbmMappingDict = null;
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
            // Force feedback
            nameof(ForceType), nameof(ForceOverall), nameof(ForceSwapMotor),
            nameof(LeftMotorStrength), nameof(RightMotorStrength),
            // Audio bass rumble
            nameof(AudioRumbleEnabled), nameof(AudioRumbleSensitivity),
            nameof(AudioRumbleCutoffHz), nameof(AudioRumbleLeftMotor), nameof(AudioRumbleRightMotor),
            // Constant force
            nameof(ConstantForceEnabled), nameof(ConstantForceX), nameof(ConstantForceY),
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
                        else if (kvp.Key == "__SlotPlayStationConfigs")
                            ps.SlotPlayStationConfigsJson = kvp.Value;
                        else if (kvp.Key == "__SlotExtendedConfig")
                            ps.SlotExtendedConfigJson = kvp.Value;
                        else if (kvp.Key == "__SlotMidiConfig")
                            ps.SlotMidiConfigJson = kvp.Value;
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
