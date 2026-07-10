namespace PadForge.Engine.Data
{
    /// <summary>
    /// Clipboard / Copy-From payload entry: one entry per device on the
    /// source slot, carrying that device's full PadSetting as a nested
    /// JSON string. Paste matches each entry to a target-slot device by
    /// <see cref="InstanceGuid"/> first (perfect round-trip on the same
    /// machine) and falls back to <see cref="ProductGuid"/> (same model,
    /// different physical instance) before skipping the entry.
    /// </summary>
    /// <remarks>
    /// Lives in Engine.Data so PadSetting.ToJson / FromJson can produce
    /// + consume it without depending on App-side ViewModels. The nested
    /// PadSettingJson string is a PadSetting.ToJson() output with the
    /// outer-only slot-level fields (SlotDeviceConfigsJson,
    /// SlotExtendedConfigJson, SlotMidiConfigJson, SlotMultiSourceRows,
    /// DeviceScopedMultiSourceRows, and SlotPerDeviceSettingsJson itself)
    /// cleared so the nesting doesn't recurse.
    /// </remarks>
    public sealed class PerDeviceSettingsEntry
    {
        /// <summary>Source device's instance GUID as a string. Used as
        /// the first-preference match key against the target slot's
        /// UserSetting.InstanceGuid values.</summary>
        public string InstanceGuid { get; set; } = "";

        /// <summary>Source device's product GUID as a string. Used as
        /// the fallback match key when no InstanceGuid match is found —
        /// covers the case where the target slot has the same controller
        /// model but a different physical unit (different USB instance).</summary>
        public string ProductGuid { get; set; } = "";

        /// <summary>Source device's product name (for diagnostics / logs).
        /// Not used for matching.</summary>
        public string ProductName { get; set; } = "";

        /// <summary>Nested PadSetting serialized via
        /// <see cref="PadSetting.ToJson(VirtualControllerType, bool)"/>.
        /// Carries deadzones, sensitivity curves, force feedback, impulse
        /// triggers, gyro tuning, TouchpadSettings, and mapping
        /// descriptors for this one device.</summary>
        public string PadSettingJson { get; set; } = "";
    }
}
