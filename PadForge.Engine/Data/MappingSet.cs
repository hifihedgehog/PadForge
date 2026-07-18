using System.Collections.Generic;
using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Per-virtual-controller mapping table. Replaces the per-(VC, Device)
    /// mapping fields on <see cref="PadSetting"/>. Cross-device sources
    /// live naturally inside a single <see cref="MappingRow"/>.
    ///
    /// <para>
    /// Shift-layer authoring (Issue #61 Phase 6) lives entirely inside
    /// this object: <see cref="ShiftActivators"/> declares the activator
    /// configuration for every layer on this slot, and <see cref="Rows"/>
    /// carries every layer's rows tagged by <see cref="MappingRow.LayerMask"/>.
    /// This guarantees shift state is per-profile by construction — the
    /// whole MappingSet is what <see cref="ProfileData.SlotMappingSets"/>
    /// stores per slot.
    /// </para>
    /// </summary>
    public class MappingSet
    {
        /// <summary>Mapping rows. Each row has a <see cref="MappingRow.Target"/>
        /// and <see cref="MappingRow.LayerMask"/>; a single Target can have
        /// multiple rows when more than one layer is configured.</summary>
        [XmlElement("Row")]
        public List<MappingRow> Rows { get; set; } = new();

        /// <summary>
        /// Shift activators authored for this slot. Each activator names
        /// the layer it engages via <see cref="ShiftActivator.LayerMask"/>.
        /// Empty list = no shift layers; only Base rows fire. Multi-activator
        /// resolution uses last-engaged-wins (the most recently engaged
        /// activator's layer is active).
        /// </summary>
        [XmlElement("ShiftActivator")]
        public List<ShiftActivator> ShiftActivators { get; set; } = new();

        /// <summary>
        /// Radial / touch menus authored for (or imported onto) this slot
        /// (#9 B-17). Same placement rationale as
        /// <see cref="ShiftActivators"/>: menus are slot-scoped structures
        /// whose entries carry a per-entry DeviceGuid ("" = any device on
        /// the slot, the Workshop-import form), and riding the MappingSet
        /// gives them profile capture / apply and Workshop materialization
        /// through the existing per-slot plumbing. Empty list = no menus.
        /// </summary>
        [XmlElement("Menu")]
        public List<PadForge.Engine.Menus.MenuDefinitionEntry> Menus { get; set; } = new();

        // ── Base layer flyout appearance (#119) ──
        // Base has no activator, so its display name / dot color / glyph for
        // the engaged-layer flyout and the Base tab live here. Empty falls
        // back to the localized "Base" label, a gray dot, and the Shift glyph.

        /// <summary>Display name shown on the Base tab and the Base flyout.
        /// Empty falls back to the localized "Base" label.</summary>
        [XmlAttribute] public string BaseLayerName { get; set; } = "";

        /// <summary>Base flyout / tab dot color, <c>"#RRGGBB"</c>. Empty = gray.</summary>
        [XmlAttribute] public string BaseColor { get; set; } = "";

        /// <summary>Base flyout glyph (emoji or single grapheme). Empty falls
        /// back to the universal Shift glyph.</summary>
        [XmlAttribute] public string BaseIcon { get; set; } = "";

        /// <summary>
        /// Rumble-to-audio (bass shaker / LFE) configuration for this slot,
        /// issue #236. Null = feature disabled, nothing persisted. Rides the
        /// MappingSet for the same reason <see cref="Menus"/> does: one
        /// lifetime with the slot's rows across profile capture / apply,
        /// copy, compaction, reset, and .pfprofile export. NOT PadSetting
        /// (per-device scope; game feedback reaches the virtual controller
        /// whether or not any physical device has motors) and NOT
        /// DeviceSlotConfig (also device-scoped).
        /// </summary>
        [XmlElement("RumbleAudio")]
        public RumbleAudioConfig RumbleAudio { get; set; }

        /// <summary>
        /// True when this set carries anything worth persisting or loading:
        /// rows, shift activators (layers), menus, authoritative ownership,
        /// or a rumble-audio config. The ONE content gate for cold load and
        /// slot-has-content checks. Gating on a hand-list of collections
        /// silently discarded a set whose only content was the newest
        /// structure (a menus-only slot lost its menu on every launch,
        /// Codex audit 2026-07-16; a config-only rumble-audio set would
        /// have died the same way).
        /// </summary>
        [XmlIgnore]
        public bool HasAuthoredContent
            => (Rows != null && Rows.Count > 0)
            || (ShiftActivators != null && ShiftActivators.Count > 0)
            || (Menus != null && Menus.Count > 0)
            || Authoritative
            || RumbleAudio != null;

        /// <summary>
        /// An authoritative set owns its slot's mappings completely: the
        /// legacy-automap merge must not add rows or inject sources into it.
        /// Stamped true on Steam Workshop imports, whose rows spell out every
        /// binding (including automap-identical ones) explicitly, so a
        /// device's auto-mapped legacy descriptors would double every input.
        /// Old XML has no attribute and deserializes false, keeping every
        /// hand-authored set on the normal merge path.
        /// </summary>
        [XmlAttribute] public bool Authoritative { get; set; } = false;

        // ── Workshop slot-level stamps (v18). Imported profiles carry no
        // devices, so per-device PadSetting channels (thumb deadzone
        // shape, gyro aim engage) cannot be stamped at import. These
        // attributes carry the authored values on the authoritative set
        // instead. Two overlay contracts, both only while
        // Authoritative:
        //   1. Deadzone-shape stamps OVERLAY the resolved per-(slot,
        //      device) PadSetting: a non-empty stamp wins over the
        //      device value, the touchpad-gesture auto-arm shape
        //      (Step3.ResolveThumbDeadZoneShape).
        //   2. The gyro-engage stamp is a USER-FIRST FALLBACK: it is
        //      consulted only when no device PadSetting on the slot
        //      configures an engage button, so a user-authored engage
        //      always wins (InputManager.UpdateGyroEngageStates).
        // Empty strings (the deserialized default for old XML) mean
        // "no stamp". Every container-copy helper must carry the whole
        // stamp family via CopyWorkshopStampsTo. ──

        /// <summary>Steam deadzone_shape for the left thumb pair, as a
        /// <see cref="DeadZoneShape"/> ordinal string ("0" = Axial for
        /// Steam's Cross / Square, "2" = ScaledRadial for Circle). Only
        /// read when <see cref="Authoritative"/>.</summary>
        [XmlAttribute] public string WorkshopLeftStickDeadZoneShape { get; set; } = "";

        /// <summary>Right-thumb twin of
        /// <see cref="WorkshopLeftStickDeadZoneShape"/>.</summary>
        [XmlAttribute] public string WorkshopRightStickDeadZoneShape { get; set; } = "";

        /// <summary>Steam gyro_button as a device-free engage descriptor
        /// (empty DeviceGuid contract): gyro rows fire only while it is
        /// held. Only read when <see cref="Authoritative"/>.</summary>
        [XmlAttribute] public string WorkshopGyroEngageDescriptor { get; set; } = "";

        /// <summary>Steam gyro_button_invert: engage while the button is
        /// NOT held.</summary>
        [XmlAttribute] public bool WorkshopGyroEngageInvert { get; set; } = false;

        /// <summary>Steam gyro_button_invert value 2 (translator v25): the
        /// three-state "Gyro Button Behavior" enum's Toggle arm. Each
        /// press of <see cref="WorkshopGyroEngageDescriptor"/> flips the
        /// engage state (the slot engage machinery's own Toggle mode)
        /// instead of holding it. Only meaningful beside a non-empty
        /// engage descriptor; ignored otherwise.</summary>
        [XmlAttribute] public bool WorkshopGyroEngageToggle { get; set; } = false;

        /// <summary>Steam gyro_ratchet_button_mask (translator v22) as
        /// pipe-joined device-free descriptors: while ANY of them is held
        /// on any slot device, the slot's gyro reads are clutched
        /// (disengaged), Steam's ratchet ("lift the mouse" re-centering).
        /// A separate AND-NOT lane beside the engage stamp, never a
        /// replacement for it, so it composes with an authored engage
        /// button, the user's own engage PadSetting, and the
        /// SetGyroEngaged macro bit alike. Only read while
        /// <see cref="Authoritative"/>. Empty = no ratchet.</summary>
        [XmlAttribute]
        public string WorkshopGyroRatchetDescriptors
        {
            get => _workshopGyroRatchetDescriptors;
            set { _workshopGyroRatchetDescriptors = value ?? ""; _workshopGyroRatchetList = null; }
        }
        private string _workshopGyroRatchetDescriptors = "";
        private string[] _workshopGyroRatchetList;

        /// <summary>Split, cached view of
        /// <see cref="WorkshopGyroRatchetDescriptors"/> for the per-tick
        /// engage settle (no per-poll string split).</summary>
        [XmlIgnore]
        public string[] WorkshopGyroRatchetList
            => _workshopGyroRatchetList ??= _workshopGyroRatchetDescriptors.Length == 0
                ? System.Array.Empty<string>()
                : _workshopGyroRatchetDescriptors.Split('|', System.StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Copies the Workshop slot-level stamps (v18) onto
        /// <paramref name="dst"/>. The ONE seam for every container-copy
        /// helper (profile snapshot deep clone, Copy From Slot, the legacy
        /// merge), so a new stamp cannot silently drop from one of them
        /// the way the hand-lists dropped all four. <see cref="Authoritative"/>
        /// itself stays a per-caller decision (the clipboard paste
        /// deliberately does not carry ownership, and stamps are only read
        /// while Authoritative, so uncarried stamps there are inert).</summary>
        public void CopyWorkshopStampsTo(MappingSet dst)
        {
            if (dst == null) return;
            dst.WorkshopLeftStickDeadZoneShape = WorkshopLeftStickDeadZoneShape ?? "";
            dst.WorkshopRightStickDeadZoneShape = WorkshopRightStickDeadZoneShape ?? "";
            dst.WorkshopGyroEngageDescriptor = WorkshopGyroEngageDescriptor ?? "";
            dst.WorkshopGyroEngageInvert = WorkshopGyroEngageInvert;
            dst.WorkshopGyroEngageToggle = WorkshopGyroEngageToggle;
            dst.WorkshopGyroRatchetDescriptors = WorkshopGyroRatchetDescriptors ?? "";
        }
    }
}
