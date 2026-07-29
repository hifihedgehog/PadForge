using System;
using PadForge.Common.Input;
using PadForge.Engine.Data;

namespace PadForge.Services
{
    /// <summary>
    /// <para>Moves a Workshop import's device tuning into the device's OWN
    /// settings, once a device is actually assigned to the slot.</para>
    ///
    /// <para>A Steam config assumes one controller, so its tuning is per
    /// physical input: "the right stick uses this deadzone shape", "gyro
    /// engages on this button". PadForge already has settings for exactly
    /// those things, with cards the user can edit. The import could not write
    /// them because it runs before any device is assigned and those settings
    /// are keyed by device guid, so it parked them on the slot as
    /// <c>MappingSet.Workshop*</c> stamps and the engine consulted the stamps
    /// at runtime instead.</para>
    ///
    /// <para>That parking spot became a second, invisible settings system.
    /// The stick deadzone shape was the worst of it: the runtime read
    /// returned the stamp unconditionally for an Authoritative slot, so the
    /// user's own Dead Zone Shape control was overridden and editing it did
    /// nothing, with nothing on screen to say why.</para>
    ///
    /// <para>So the stamps are applied HERE, at assignment, and cleared. From
    /// then on the values live in the user's settings, the existing cards show
    /// and edit them, and the engine has one place to read.</para>
    ///
    /// <para>Applied only where the user has not already chosen something, so
    /// re-assigning a device cannot silently overwrite tuning the user set by
    /// hand. Cleared unconditionally, because a stamp that has been offered
    /// once has done its job: leaving it would let it re-apply after the user
    /// deliberately changed the value back.</para>
    /// </summary>
    public static class WorkshopTuningApplier
    {
        /// <summary><para>Folds the slot's import stamps into
        /// <paramref name="ps"/>. Returns true when anything changed, so the
        /// caller can mark dirty.</para>
        /// <para>Call this from EVERY path that assigns a device to a slot.
        /// The runtime overlays this replaced applied on every path by
        /// construction, so wiring it into one assignment entry point and not
        /// its sibling silently dropped the tuning for the other. There are
        /// two today, DeviceService.OnAssignToSlot (the device list's assign
        /// command) and DeviceService.AssignDeviceToSlot (drag-drop and
        /// programmatic), and a third added later must not have to know this
        /// exists. It is idempotent and cheap, so calling it too often is
        /// free and calling it too seldom is a silent regression.</para>
        /// <para>N/A by design: WorkshopGyroRatchetDescriptors is the one
        /// Workshop stamp NOT folded here. There is no ratchet field on
        /// PadSetting and no ratchet control in any view, so there is no
        /// user-facing setting to fold it into. It stays a runtime overlay
        /// (InputManager's gyro engage config) until a card exists.</para></summary>
        public static bool ApplyToAssignedDevice(int slotIndex, PadSetting ps)
        {
            if (ps == null) return false;
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || slotIndex < 0 || slotIndex >= sets.Length) return false;
            var set = sets[slotIndex];
            if (set == null) return false;

            bool changed = false;

            // ── stick deadzone shape ──────────────────────────────────────
            if (!string.IsNullOrEmpty(set.WorkshopLeftStickDeadZoneShape))
            {
                if (IsDefaultShape(ps.LeftThumbDeadZoneShape))
                {
                    ps.LeftThumbDeadZoneShape = set.WorkshopLeftStickDeadZoneShape;
                    changed = true;
                }
                set.WorkshopLeftStickDeadZoneShape = "";
            }
            if (!string.IsNullOrEmpty(set.WorkshopRightStickDeadZoneShape))
            {
                if (IsDefaultShape(ps.RightThumbDeadZoneShape))
                {
                    ps.RightThumbDeadZoneShape = set.WorkshopRightStickDeadZoneShape;
                    changed = true;
                }
                set.WorkshopRightStickDeadZoneShape = "";
            }

            // ── gyro engage button ────────────────────────────────────────
            if (!string.IsNullOrEmpty(set.WorkshopGyroEngageDescriptor))
            {
                if (string.IsNullOrEmpty(ps.GyroAimEngageButton))
                {
                    ps.GyroAimEngageButton = set.WorkshopGyroEngageDescriptor;
                    // The import's descriptor is device-free by construction:
                    // it names a control on whatever device drives the slot.
                    ps.GyroAimEngageDeviceGuid = "";
                    ps.GyroAimEngageMode =
                        set.WorkshopGyroEngageToggle ? "Toggle"
                        : set.WorkshopGyroEngageInvert ? "ReleaseToEngage"
                        : "Hold";
                    changed = true;
                }
                set.WorkshopGyroEngageDescriptor = "";
                set.WorkshopGyroEngageToggle = false;
                set.WorkshopGyroEngageInvert = false;
            }

            return changed;
        }

        /// <summary>True when the stored shape is absent or the serialized
        /// default, i.e. the user has not chosen one.</summary>
        private static bool IsDefaultShape(string shape) =>
            string.IsNullOrEmpty(shape) || shape == "2";
    }
}
