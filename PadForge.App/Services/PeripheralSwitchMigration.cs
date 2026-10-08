using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Services
{
    /// <summary>
    /// Turns the Dashboard switches that drove vendor outputs globally into
    /// assignments of the vendor rows that replace them (#494): the Razer
    /// Sensa switch (#374) assigns the Razer Sensa row.
    ///
    /// <para>The switches had a global value and a nullable per-profile
    /// opinion (ProfileData's authored legs), and profiles own their device
    /// assignments (InputService.ApplyProfile applies each profile's entries
    /// whole). So a switch that was on becomes an assignment everywhere it
    /// was on: in the live settings when the active profile's value was on,
    /// and in each stored profile whose value was on. A profile's value is
    /// its own opinion, else the global one. The row goes to the virtual
    /// controller with the smallest displayed player number in that
    /// topology, the slot that rules a shared path. A topology with no
    /// controller gets nothing, since the old lane had nothing to read there
    /// either.</para>
    ///
    /// <para>Each profile's opinion is cleared once read, and the switches
    /// are no longer written, so the migration runs once per file.</para>
    /// </summary>
    internal static class PeripheralSwitchMigration
    {
        /// <summary>One shipped switch: its row, its global value, its
        /// profile leg, and whether this PC can open the row at all. A row it
        /// never can gets no assignment, and the switch only clears.</summary>
        internal readonly record struct LegacySwitch(PeripheralRowKind Kind, bool Global,
            Func<ProfileData, bool?> Read, Action<ProfileData> Clear, bool Available = true);

        /// <summary>The shipped switches, each with its global value from the
        /// file's app settings, or false for a profile imported on its own,
        /// which carries none.</summary>
        internal static IEnumerable<LegacySwitch> Switches(AppSettingsData app)
        {
            // The Sensa row opens only where this build loads the engine.
            yield return new LegacySwitch(PeripheralRowKind.RazerSensa, app?.EnableSensaHaptics ?? false,
                p => p.EnableSensaHaptics, p => p.EnableSensaHaptics = null, PlatformSupport.SensaAvailable);
        }

        /// <summary>A profile file exported before #494, imported on its own:
        /// an opinion that turned a switch on assigns the row in the profile,
        /// and every opinion is cleared, as the settings load does.</summary>
        internal static void MigrateImported(ProfileData profile)
            => Run(Switches(null), new[] { profile }, null, null, () => -1, null);

        /// <summary>The slot that shows the smallest player number: the
        /// first created slot in the group order the Dashboard, the sidebar
        /// and <see cref="SettingsManager.SlotOrders.GetGlobalSlotNumber"/>
        /// walk. -1 when no slot is created.</summary>
        internal static int FirstDisplayedSlot(IReadOnlyList<bool> created,
            Func<VirtualControllerType, IReadOnlyList<int>> orderOf)
        {
            if (created == null) return -1;
            foreach (var type in VirtualControllerGroups.InOrder)
            {
                var order = orderOf(type);
                if (order == null) continue;
                foreach (int index in order)
                    if (index >= 0 && index < created.Count && created[index]) return index;
            }
            return -1;
        }

        internal static int FirstDisplayedSlot(ProfileData profile)
            => profile == null ? -1 : FirstDisplayedSlot(profile.SlotCreated, type => type switch
            {
                VirtualControllerType.Xbox => profile.XboxSlotOrder,
                VirtualControllerType.PlayStation => profile.PlayStationSlotOrder,
                VirtualControllerType.Nintendo => profile.NintendoSlotOrder,
                VirtualControllerType.Extended => profile.ExtendedSlotOrder,
                VirtualControllerType.KeyboardMouse => profile.KeyboardMouseSlotOrder,
                VirtualControllerType.Midi => profile.MidiSlotOrder,
                VirtualControllerType.Vr => profile.VrSlotOrder,
                _ => null,
            });

        /// <summary>A device record for a vendor row, filled from the row the
        /// way Step 1 fills it, and offline until the row opens.</summary>
        internal static UserDevice NewRowRecord(PeripheralRowKind kind)
        {
            var row = new PeripheralOutputRow(kind);
            var ud = new UserDevice();
            ud.LoadFromExternalDevice(row);
            ud.ClearRuntimeState();
            row.Dispose();
            ud.PeripheralOutputs = (int)(PeripheralOutputRow.IsLightingRow(kind)
                ? PeripheralOutputKinds.Lighting : PeripheralOutputKinds.Haptics);
            return ud;
        }

        /// <summary>Applies every switch. <paramref name="activeProfile"/> is
        /// the named profile active at load, or null for the default, and
        /// <paramref name="defaultSnapshot"/> the default's stored state while
        /// a named profile is active. The live topology belongs to the active
        /// profile, and its stored entries are rebuilt from the live state on
        /// the next save, so the live switch value there was its own opinion,
        /// else the global one, as the load applied it.
        /// <paramref name="liveSlot"/> answers the live slot that rules, and
        /// <paramref name="slotPadSetting"/> a fresh default setting for a
        /// device on a slot of the live topology. True when anything changed,
        /// so the caller saves.</summary>
        public static bool Run(IEnumerable<LegacySwitch> switches, IList<ProfileData> profiles,
            ProfileData activeProfile, ProfileData defaultSnapshot, Func<int> liveSlot,
            Func<UserDevice, int, PadSetting> slotPadSetting)
        {
            bool changed = false;
            foreach (var s in switches)
            {
                bool live = (activeProfile != null ? s.Read(activeProfile) : null) ?? s.Global;
                if (s.Available && live) changed |= AssignLive(s.Kind, liveSlot(), slotPadSetting);
                if (defaultSnapshot != null)
                {
                    if (s.Available && (s.Read(defaultSnapshot) ?? s.Global))
                        changed |= AddToProfile(defaultSnapshot, s.Kind);
                    changed |= s.Read(defaultSnapshot) != null;
                    s.Clear(defaultSnapshot);
                }
                if (profiles == null) continue;
                foreach (var profile in profiles)
                {
                    if (profile == null) continue;
                    if (s.Available && (s.Read(profile) ?? s.Global)) changed |= AddToProfile(profile, s.Kind);
                    changed |= s.Read(profile) != null;
                    s.Clear(profile);
                }
            }
            return changed;
        }

        /// <summary>The row's record, created offline when the file has none,
        /// and its assignment to the live slot that rules.</summary>
        internal static bool AssignLive(PeripheralRowKind kind, int slot,
            Func<UserDevice, int, PadSetting> slotPadSetting)
        {
            if (slot < 0) return false;
            var guid = PeripheralOutputRow.IdentityFor(kind);
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            if (devices == null || settings == null) return false;

            UserDevice ud;
            lock (devices.SyncRoot)
            {
                ud = devices.Items.FirstOrDefault(d => d?.InstanceGuid == guid);
                if (ud == null)
                {
                    ud = NewRowRecord(kind);
                    devices.Items.Add(ud);
                }
            }
            lock (settings.SyncRoot)
            {
                if (settings.Items.Any(s => s.InstanceGuid == guid && s.MapTo == slot)) return false;
            }
            var us = SettingsManager.AssignDeviceToSlot(guid, slot);
            if (us == null) return false;
            us.ProductGuid = ud.ProductGuid;
            if (us.GetPadSetting() == null)
            {
                var ps = slotPadSetting(ud, slot);
                us.SetPadSetting(ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
            }
            PadForge.Engine.SdlDiagLog.WriteLine($"CFG #494 migrated a global switch: {kind} row assigned to slot {slot}");
            return true;
        }

        /// <summary>An entry for the row on the profile's ruling slot, with a
        /// default setting for that slot's controller type.</summary>
        internal static bool AddToProfile(ProfileData profile, PeripheralRowKind kind)
        {
            int slot = FirstDisplayedSlot(profile);
            if (slot < 0) return false;
            var guid = PeripheralOutputRow.IdentityFor(kind);
            var entries = profile.Entries?.ToList() ?? new List<ProfileEntry>();
            if (entries.Any(e => e.InstanceGuid == guid && e.MapTo == slot)) return false;

            var record = NewRowRecord(kind);
            var type = profile.SlotControllerTypes != null && slot < profile.SlotControllerTypes.Length
                       && Enum.IsDefined(typeof(VirtualControllerType), profile.SlotControllerTypes[slot])
                ? (VirtualControllerType)profile.SlotControllerTypes[slot]
                : VirtualControllerType.Xbox;
            string profileId = profile.SlotProfileIds != null && slot < profile.SlotProfileIds.Length
                ? profile.SlotProfileIds[slot] : null;
            var ps = SettingsManager.CreateDefaultPadSetting(record, type, profileId);

            var padSettings = profile.PadSettings?.ToList() ?? new List<PadSetting>();
            if (!padSettings.Any(p => p.PadSettingChecksum == ps.PadSettingChecksum))
                padSettings.Add(ps);
            entries.Add(new ProfileEntry
            {
                InstanceGuid = guid,
                ProductGuid = record.ProductGuid,
                MapTo = slot,
                PadSettingChecksum = ps.PadSettingChecksum,
            });
            profile.PadSettings = padSettings.ToArray();
            profile.Entries = entries.ToArray();
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"CFG #494 migrated a profile switch: {kind} row assigned to slot {slot} in '{profile.Name}'");
            return true;
        }
    }
}
