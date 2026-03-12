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
    /// Numeric settings (dead zones, gains) are stored as strings for XML
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
        /// Anti-dead zone for the left trigger (0–100%). Offsets the output range minimum
        /// so small physical presses register past the game's built-in trigger dead zone.
        /// </summary>
        [XmlElement] public string LeftTriggerAntiDeadZone { get; set; } = "0";

        /// <summary>Anti-dead zone for the right trigger (0–100%).</summary>
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

        /// <summary>Left stick dead zone X (0–100%).</summary>
        [XmlElement] public string LeftThumbDeadZoneX { get; set; } = "0";

        /// <summary>Left stick dead zone Y (0–100%).</summary>
        [XmlElement] public string LeftThumbDeadZoneY { get; set; } = "0";

        /// <summary>Right stick dead zone X (0–100%).</summary>
        [XmlElement] public string RightThumbDeadZoneX { get; set; } = "0";

        /// <summary>Right stick dead zone Y (0–100%).</summary>
        [XmlElement] public string RightThumbDeadZoneY { get; set; } = "0";

        /// <summary>Left stick dead zone shape (DeadZoneShape enum). Default 2 = ScaledRadial.</summary>
        [XmlElement] public string LeftThumbDeadZoneShape { get; set; } = "2";

        /// <summary>Right stick dead zone shape (DeadZoneShape enum). Default 2 = ScaledRadial.</summary>
        [XmlElement] public string RightThumbDeadZoneShape { get; set; } = "2";

        /// <summary>
        /// Left stick anti-dead zone (0–100%). Offsets the output range minimum
        /// so small physical movements register past the game's built-in dead zone.
        /// </summary>
        [XmlElement] public string LeftThumbAntiDeadZone { get; set; } = "0";

        /// <summary>Right stick anti-dead zone (0–100%). Legacy unified property — use per-axis X/Y instead.</summary>
        [XmlElement] public string RightThumbAntiDeadZone { get; set; } = "0";

        /// <summary>Left stick anti-dead zone X axis (0–100%).</summary>
        [XmlElement] public string LeftThumbAntiDeadZoneX { get; set; } = "0";

        /// <summary>Left stick anti-dead zone Y axis (0–100%).</summary>
        [XmlElement] public string LeftThumbAntiDeadZoneY { get; set; } = "0";

        /// <summary>Right stick anti-dead zone X axis (0–100%).</summary>
        [XmlElement] public string RightThumbAntiDeadZoneX { get; set; } = "0";

        /// <summary>Right stick anti-dead zone Y axis (0–100%).</summary>
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

        // ─────────────────────────────────────────────
        //  Stick center offset calibration
        // ─────────────────────────────────────────────

        /// <summary>Left stick X center offset (-100 to 100%). Corrects stick drift before dead zone.</summary>
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
        //  vJoy custom mappings (dictionary-based)
        //  Used for custom vJoy configurations with arbitrary axis/button/POV counts.
        //  Keys use target names like "VJoyAxis0", "VJoyAxis0Neg", "VJoyBtn0",
        //  "VJoyPov0Up", etc. Values are mapping descriptors (same format as above).
        // ─────────────────────────────────────────────

        /// <summary>Serializable array for XML persistence of vJoy mappings.</summary>
        [XmlArray("VJoyMappings")]
        [XmlArrayItem("Map")]
        public VJoyMappingEntry[] VJoyMappingEntries { get; set; }

        [XmlIgnore]
        private Dictionary<string, string> _vjoyMappingDict;

        /// <summary>Gets a vJoy mapping value by key (e.g., "VJoyAxis0", "VJoyBtn5").</summary>
        public string GetVJoyMapping(string key)
        {
            EnsureVJoyDict();
            return _vjoyMappingDict.TryGetValue(key, out var val) ? val : "";
        }

        /// <summary>Sets a vJoy mapping value by key.</summary>
        public void SetVJoyMapping(string key, string value)
        {
            EnsureVJoyDict();
            if (string.IsNullOrEmpty(value))
                _vjoyMappingDict.Remove(key);
            else
                _vjoyMappingDict[key] = value;
        }

        /// <summary>Flushes the in-memory dictionary back to the serializable array.</summary>
        public void FlushVJoyMappings()
        {
            if (_vjoyMappingDict == null) return; // Not initialized — array is canonical.
            if (_vjoyMappingDict.Count == 0)
            {
                VJoyMappingEntries = null;
                return;
            }
            var entries = new VJoyMappingEntry[_vjoyMappingDict.Count];
            int i = 0;
            foreach (var kvp in _vjoyMappingDict)
                entries[i++] = new VJoyMappingEntry { Key = kvp.Key, Value = kvp.Value };
            VJoyMappingEntries = entries;
        }

        private readonly object _vjoyDictLock = new();

        private void EnsureVJoyDict()
        {
            if (_vjoyMappingDict != null) return;
            lock (_vjoyDictLock)
            {
                if (_vjoyMappingDict != null) return;
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                if (VJoyMappingEntries != null)
                {
                    foreach (var e in VJoyMappingEntries)
                    {
                        if (!string.IsNullOrEmpty(e.Key) && !string.IsNullOrEmpty(e.Value))
                            dict[e.Key] = e.Value;
                    }
                }
                _vjoyMappingDict = dict;
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
        public VJoyMappingEntry[] MidiMappingEntries { get; set; }

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
            var entries = new VJoyMappingEntry[_midiMappingDict.Count];
            int i = 0;
            foreach (var kvp in _midiMappingDict)
                entries[i++] = new VJoyMappingEntry { Key = kvp.Key, Value = kvp.Value };
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
        public VJoyMappingEntry[] KbmMappingEntries { get; set; }

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
            var entries = new VJoyMappingEntry[_kbmMappingDict.Count];
            int i = 0;
            foreach (var kvp in _kbmMappingDict)
                entries[i++] = new VJoyMappingEntry { Key = kvp.Key, Value = kvp.Value };
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
        //  Game-specific overrides
        // ─────────────────────────────────────────────

        /// <summary>
        /// Optional game executable name this PadSetting is associated with.
        /// When empty, the setting is global (applies to all games).
        /// </summary>
        [XmlElement] public string GameFileName { get; set; } = "";

        // ─────────────────────────────────────────────
        //  Migration
        // ─────────────────────────────────────────────

        /// <summary>
        /// Migrates legacy unified anti-dead zone values to per-axis properties.
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

            // Inversion overrides
            sb.Append(LeftThumbAxisXInvert); sb.Append('|');
            sb.Append(LeftThumbAxisYInvert); sb.Append('|');
            sb.Append(RightThumbAxisXInvert); sb.Append('|');
            sb.Append(RightThumbAxisYInvert); sb.Append('|');

            sb.Append(AxisToButtonThreshold); sb.Append('|');

            // vJoy custom mappings (sorted for deterministic checksum)
            EnsureVJoyDict();
            if (_vjoyMappingDict.Count > 0)
            {
                var keys = new List<string>(_vjoyMappingDict.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    sb.Append(key); sb.Append('='); sb.Append(_vjoyMappingDict[key]); sb.Append('|');
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
            (VJoyMappingEntries != null && VJoyMappingEntries.Length > 0) ||
            (_vjoyMappingDict != null && _vjoyMappingDict.Count > 0) ||
            (MidiMappingEntries != null && MidiMappingEntries.Length > 0) ||
            (_midiMappingDict != null && _midiMappingDict.Count > 0);

        /// <summary>
        /// Clears all mapping descriptors (standard, vJoy, and MIDI) while preserving
        /// dead zone, force feedback, and other non-mapping configuration.
        /// Call before writing a new set of mappings to prevent stale leftovers
        /// from a previous mapping layout (e.g., switching Xbox 360 preset → custom vJoy).
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

            // vJoy/MIDI mapping dictionaries and arrays.
            VJoyMappingEntries = null;
            _vjoyMappingDict = null;
            MidiMappingEntries = null;
            _midiMappingDict = null;
        }

        /// <summary>
        /// Returns all non-empty mapping descriptor strings from this PadSetting.
        /// Includes standard button/axis/dpad/trigger mappings, vJoy, and MIDI custom entries.
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

            // vJoy custom mappings
            if (VJoyMappingEntries != null)
            {
                foreach (var e in VJoyMappingEntries)
                    Add(e.Value);
            }

            // MIDI custom mappings
            if (MidiMappingEntries != null)
            {
                foreach (var e in MidiMappingEntries)
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
            nameof(LeftThumbCenterOffsetX), nameof(LeftThumbCenterOffsetY),
            nameof(RightThumbCenterOffsetX), nameof(RightThumbCenterOffsetY),
            // Force feedback
            nameof(ForceType), nameof(ForceOverall), nameof(ForceSwapMotor),
            nameof(LeftMotorStrength), nameof(RightMotorStrength),
            // Axis inversion
            nameof(LeftThumbAxisXInvert), nameof(LeftThumbAxisYInvert),
            nameof(RightThumbAxisXInvert), nameof(RightThumbAxisYInvert),
            // Threshold
            nameof(AxisToButtonThreshold),
        };

        /// <summary>
        /// Serializes all copyable mapping/deadzone/FF properties to a JSON string.
        /// Used for clipboard copy/paste of controller settings.
        /// </summary>
        public string ToJson()
        {
            // Flush live dicts to arrays before serializing.
            FlushVJoyMappings();
            FlushMidiMappings();
            FlushKbmMappings();

            var dict = new Dictionary<string, string>();
            var type = GetType();

            foreach (string name in CopyablePropertyNames)
            {
                var prop = type.GetProperty(name);
                if (prop != null)
                    dict[name] = prop.GetValue(this) as string ?? "";
            }

            // Include vJoy/MIDI mapping arrays if present.
            if (VJoyMappingEntries != null && VJoyMappingEntries.Length > 0)
            {
                var vjoyList = new List<Dictionary<string, string>>();
                foreach (var e in VJoyMappingEntries)
                    vjoyList.Add(new Dictionary<string, string> { ["Key"] = e.Key, ["Value"] = e.Value });
                dict["__VJoyMappings"] = JsonSerializer.Serialize(vjoyList);
            }
            if (MidiMappingEntries != null && MidiMappingEntries.Length > 0)
            {
                var midiList = new List<Dictionary<string, string>>();
                foreach (var e in MidiMappingEntries)
                    midiList.Add(new Dictionary<string, string> { ["Key"] = e.Key, ["Value"] = e.Value });
                dict["__MidiMappings"] = JsonSerializer.Serialize(midiList);
            }

            return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Deserializes a JSON string into a new PadSetting.
        /// Returns null if the JSON is invalid or not a PadSetting export.
        /// </summary>
        public static PadSetting FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict == null || dict.Count == 0)
                    return null;

                var ps = new PadSetting();
                var type = typeof(PadSetting);

                foreach (var kvp in dict)
                {
                    if (kvp.Key == "__VJoyMappings")
                    {
                        ps.VJoyMappingEntries = DeserializeMappingArray(kvp.Value);
                        continue;
                    }
                    if (kvp.Key == "__MidiMappings")
                    {
                        ps.MidiMappingEntries = DeserializeMappingArray(kvp.Value);
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

        private static VJoyMappingEntry[] DeserializeMappingArray(string json)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
                if (list == null) return null;
                var arr = new VJoyMappingEntry[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    arr[i] = new VJoyMappingEntry
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
            // (SetVJoyMapping/SetMidiMapping update the dict, not the array).
            source.FlushVJoyMappings();
            source.FlushMidiMappings();
            source.FlushKbmMappings();

            // Deep-copy arrays and invalidate our cached dictionaries.
            VJoyMappingEntries = DeepCopyMappings(source.VJoyMappingEntries);
            _vjoyMappingDict = null;
            MidiMappingEntries = DeepCopyMappings(source.MidiMappingEntries);
            _midiMappingDict = null;
            KbmMappingEntries = DeepCopyMappings(source.KbmMappingEntries);
            _kbmMappingDict = null;
        }

        private static VJoyMappingEntry[] DeepCopyMappings(VJoyMappingEntry[] src)
        {
            if (src == null || src.Length == 0) return null;
            var arr = new VJoyMappingEntry[src.Length];
            for (int i = 0; i < src.Length; i++)
                arr[i] = new VJoyMappingEntry { Key = src[i].Key, Value = src[i].Value };
            return arr;
        }

        /// <summary>
        /// Creates a deep copy of this PadSetting (copies all properties + checksum + GameFileName).
        /// </summary>
        public PadSetting CloneDeep()
        {
            var clone = new PadSetting();
            clone.CopyFrom(this);
            clone.PadSettingChecksum = PadSettingChecksum;
            clone.GameFileName = GameFileName;
            return clone;
        }
    }

    /// <summary>
    /// Key-value entry for vJoy/MIDI mapping persistence in XML.
    /// </summary>
    public class VJoyMappingEntry
    {
        [XmlAttribute] public string Key { get; set; } = "";
        [XmlAttribute] public string Value { get; set; } = "";
    }
}
