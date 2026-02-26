using System;
using System.ComponentModel;
using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Links a physical input device (identified by <see cref="InstanceGuid"/>)
    /// to a virtual controller slot (identified by <see cref="MapTo"/>)
    /// and a mapping configuration (identified by <see cref="PadSettingChecksum"/>).
    /// 
    /// One UserSetting per device-to-slot assignment. Multiple devices can map to the
    /// same slot (combined in Step 4), and a device can map to multiple slots
    /// (one UserSetting per slot, same InstanceGuid, different MapTo).
    /// </summary>
    public class UserSetting : INotifyPropertyChanged
    {
        // ─────────────────────────────────────────────
        //  Identity — links device to slot
        // ─────────────────────────────────────────────

        /// <summary>
        /// Instance GUID of the physical device this setting applies to.
        /// Must match <see cref="UserDevice.InstanceGuid"/>.
        /// </summary>
        [XmlElement]
        public Guid InstanceGuid { get; set; }

        /// <summary>
        /// Human-readable instance name at the time this setting was created.
        /// Used for display when the device is offline.
        /// </summary>
        [XmlElement]
        public string InstanceName { get; set; } = string.Empty;

        /// <summary>
        /// Product GUID of the device. Used for matching when instance GUIDs
        /// change (e.g., device plugged into a different USB port).
        /// </summary>
        [XmlElement]
        public Guid ProductGuid { get; set; }

        /// <summary>
        /// Human-readable product name.
        /// </summary>
        [XmlElement]
        public string ProductName { get; set; } = string.Empty;

        // ─────────────────────────────────────────────
        //  Slot assignment
        // ─────────────────────────────────────────────

        private int _mapTo = -1;

        /// <summary>
        /// Virtual controller slot index this device is mapped to (0–3).
        /// A value of -1 means the device is not mapped to any slot.
        /// </summary>
        [XmlElement]
        public int MapTo
        {
            get => _mapTo;
            set
            {
                if (_mapTo != value)
                {
                    _mapTo = value;
                    OnPropertyChanged(nameof(MapTo));
                    OnPropertyChanged(nameof(MapToLabel));
                }
            }
        }

        /// <summary>
        /// Display label for the slot assignment: "Player 1"–"Player 4" or "Unmapped".
        /// </summary>
        [XmlIgnore]
        public string MapToLabel =>
            _mapTo >= 0 && _mapTo < 4 ? $"Player {_mapTo + 1}" : "Unmapped";

        // ─────────────────────────────────────────────
        //  PadSetting linkage
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checksum string that links this UserSetting to a <see cref="PadSetting"/>
        /// record. PadSettings are stored separately and shared across settings that
        /// use the same mapping configuration.
        /// </summary>
        [XmlElement]
        public string PadSettingChecksum { get; set; } = string.Empty;

        // ─────────────────────────────────────────────
        //  Enabled / ordering
        // ─────────────────────────────────────────────

        /// <summary>
        /// Whether this device-to-slot mapping is enabled.
        /// Disabled mappings are skipped in the pipeline.
        /// </summary>
        [XmlElement]
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Sort order for devices mapped to the same slot.
        /// Lower values have higher priority when combining states in Step 4.
        /// </summary>
        [XmlElement]
        public int SortOrder { get; set; }

        // ─────────────────────────────────────────────
        //  Metadata
        // ─────────────────────────────────────────────

        /// <summary>Date when this setting was created.</summary>
        [XmlElement]
        public DateTime DateCreated { get; set; } = DateTime.Now;

        /// <summary>Date when this setting was last modified.</summary>
        [XmlElement]
        public DateTime DateUpdated { get; set; } = DateTime.Now;

        /// <summary>Optional comment/note for this setting.</summary>
        [XmlElement]
        public string Comment { get; set; } = string.Empty;

        // ─────────────────────────────────────────────
        //  Auto-mapping flags
        // ─────────────────────────────────────────────

        /// <summary>
        /// Whether the mapping was auto-generated rather than manually configured.
        /// </summary>
        [XmlElement]
        public bool IsAutoMapped { get; set; }

        // ─────────────────────────────────────────────
        //  Runtime-only fields (not serialized)
        //  Used by InputManager pipeline Steps 2–4.
        // ─────────────────────────────────────────────

        /// <summary>
        /// The mapped gamepad output state computed in Step 3.
        /// Written by the background thread, read by Step 4.
        /// </summary>
        [XmlIgnore]
        public Gamepad OutputState { get; set; }

        /// <summary>
        /// Cached PadSetting reference. Set by SettingsManager during settings load.
        /// </summary>
        [XmlIgnore]
        internal PadSetting _cachedPadSetting;

        /// <summary>
        /// Retrieves the PadSetting linked to this UserSetting.
        /// Returns the cached PadSetting set via <see cref="SetPadSetting"/>.
        /// </summary>
        public PadSetting GetPadSetting()
        {
            return _cachedPadSetting;
        }

        /// <summary>
        /// Sets the cached PadSetting for this user setting.
        /// Called by SettingsManager during settings load and sync.
        /// </summary>
        public void SetPadSetting(PadSetting ps)
        {
            _cachedPadSetting = ps;
        }

        // ─────────────────────────────────────────────
        //  INotifyPropertyChanged
        // ─────────────────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // ─────────────────────────────────────────────
        //  Display
        // ─────────────────────────────────────────────

        public override string ToString()
        {
            string name = !string.IsNullOrEmpty(InstanceName) ? InstanceName : ProductName;
            return $"{name} → {MapToLabel}";
        }
    }
}
