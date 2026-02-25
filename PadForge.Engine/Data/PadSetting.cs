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

        // ─────────────────────────────────────────────
        //  Thumbstick axis mappings
        // ─────────────────────────────────────────────

        [XmlElement] public string LeftThumbAxisX { get; set; } = "";
        [XmlElement] public string LeftThumbAxisY { get; set; } = "";
        [XmlElement] public string RightThumbAxisX { get; set; } = "";
        [XmlElement] public string RightThumbAxisY { get; set; } = "";

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

        /// <summary>
        /// Motor period in milliseconds. Used by some force feedback implementations
        /// to control the update frequency. Default 0 = automatic.
        /// </summary>
        [XmlElement] public string LeftMotorPeriod { get; set; } = "0";

        /// <summary>Right motor period in milliseconds.</summary>
        [XmlElement] public string RightMotorPeriod { get; set; } = "0";

        /// <summary>
        /// Left motor direction. 0 = normal, 1 = inverted.
        /// </summary>
        [XmlElement] public string LeftMotorDirection { get; set; } = "0";

        /// <summary>Right motor direction.</summary>
        [XmlElement] public string RightMotorDirection { get; set; } = "0";

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

            // Thumbstick axes
            sb.Append(LeftThumbAxisX); sb.Append('|');
            sb.Append(LeftThumbAxisY); sb.Append('|');
            sb.Append(RightThumbAxisX); sb.Append('|');
            sb.Append(RightThumbAxisY); sb.Append('|');

            // Dead zones
            sb.Append(LeftThumbDeadZoneX); sb.Append('|');
            sb.Append(LeftThumbDeadZoneY); sb.Append('|');
            sb.Append(RightThumbDeadZoneX); sb.Append('|');
            sb.Append(RightThumbDeadZoneY); sb.Append('|');
            sb.Append(LeftThumbAntiDeadZone); sb.Append('|');
            sb.Append(RightThumbAntiDeadZone); sb.Append('|');
            sb.Append(LeftThumbAntiDeadZoneX); sb.Append('|');
            sb.Append(LeftThumbAntiDeadZoneY); sb.Append('|');
            sb.Append(RightThumbAntiDeadZoneX); sb.Append('|');
            sb.Append(RightThumbAntiDeadZoneY); sb.Append('|');
            sb.Append(LeftThumbLinear); sb.Append('|');
            sb.Append(RightThumbLinear); sb.Append('|');

            // Force feedback
            sb.Append(ForceType); sb.Append('|');
            sb.Append(ForceOverall); sb.Append('|');
            sb.Append(ForceSwapMotor); sb.Append('|');
            sb.Append(LeftMotorStrength); sb.Append('|');
            sb.Append(RightMotorStrength); sb.Append('|');
            sb.Append(LeftMotorPeriod); sb.Append('|');
            sb.Append(RightMotorPeriod); sb.Append('|');
            sb.Append(LeftMotorDirection); sb.Append('|');
            sb.Append(RightMotorDirection); sb.Append('|');

            // Inversion overrides
            sb.Append(LeftThumbAxisXInvert); sb.Append('|');
            sb.Append(LeftThumbAxisYInvert); sb.Append('|');
            sb.Append(RightThumbAxisXInvert); sb.Append('|');
            sb.Append(RightThumbAxisYInvert); sb.Append('|');

            sb.Append(AxisToButtonThreshold);

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
            !string.IsNullOrEmpty(RightThumbAxisY);

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
            // Sticks
            nameof(LeftThumbAxisX), nameof(LeftThumbAxisY),
            nameof(RightThumbAxisX), nameof(RightThumbAxisY),
            // Dead zones
            nameof(LeftThumbDeadZoneX), nameof(LeftThumbDeadZoneY),
            nameof(RightThumbDeadZoneX), nameof(RightThumbDeadZoneY),
            nameof(LeftThumbAntiDeadZone), nameof(RightThumbAntiDeadZone),
            nameof(LeftThumbAntiDeadZoneX), nameof(LeftThumbAntiDeadZoneY),
            nameof(RightThumbAntiDeadZoneX), nameof(RightThumbAntiDeadZoneY),
            nameof(LeftThumbLinear), nameof(RightThumbLinear),
            // Force feedback
            nameof(ForceType), nameof(ForceOverall), nameof(ForceSwapMotor),
            nameof(LeftMotorStrength), nameof(RightMotorStrength),
            nameof(LeftMotorPeriod), nameof(RightMotorPeriod),
            nameof(LeftMotorDirection), nameof(RightMotorDirection),
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
            var dict = new Dictionary<string, string>();
            var type = GetType();

            foreach (string name in CopyablePropertyNames)
            {
                var prop = type.GetProperty(name);
                if (prop != null)
                    dict[name] = prop.GetValue(this) as string ?? "";
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
}
