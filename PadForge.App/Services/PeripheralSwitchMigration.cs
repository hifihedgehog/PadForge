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
    /// Chroma (#373) and Logitech LIGHTSYNC (#382) lightbar mirrors assign
    /// their lighting rows, and the Razer Sensa switch (#374) the Sensa row.
    /// A migrated lighting row starts at Player Number, so it shows the
    /// game's lightbar as the mirror did, and the controller's player color
    /// while no game writes one.
    ///
    /// <para>The switches had a global value and a nullable per-profile
    /// opinion (ProfileData's authored legs), and profiles own their device
    /// assignments (InputService.ApplyProfile applies each profile's entries
    /// whole). So a switch that was on becomes an assignment everywhere it
    /// was on: in the live settings when the active profile's value was on,
    /// and in each stored profile whose value was on. A profile's value is
    /// its own opinion, else the global one. The Sensa row goes to the
    /// virtual controller with the smallest displayed player number in that
    /// topology, the slot that rules a shared path. A lighting row goes to
    /// the first DualSense or DualShock 4 controller in display order, since
    /// the mirrors showed only a lightbar color a game wrote to one of those.
    /// A topology without the controller a row needs gets nothing, since the
    /// old lane had nothing to read there either.</para>
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
            yield return new LegacySwitch(PeripheralRowKind.RazerChroma, app?.EnableChromaLightbar ?? false,
                p => p.EnableChromaLightbar, p => p.EnableChromaLightbar = null);
            yield return new LegacySwitch(PeripheralRowKind.LogitechLightsync, app?.EnableLightsyncLightbar ?? false,
                p => p.EnableLightsyncLightbar, p => p.EnableLightsyncLightbar = null);
            // The Sensa row opens only where this build loads the engine.
            yield return new LegacySwitch(PeripheralRowKind.RazerSensa, app?.EnableSensaHaptics ?? false,
                p => p.EnableSensaHaptics, p => p.EnableSensaHaptics = null, PlatformSupport.SensaAvailable);
        }

        /// <summary>A profile file exported before #494, imported on its own:
        /// an opinion that turned a switch on assigns the row in the profile,
        /// and every opinion is cleared, as the settings load does.</summary>
        internal static void MigrateImported(ProfileData profile)
            => Run(Switches(null), new[] { profile }, null, null, _ => -1, null);

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
            => profile == null ? -1 : FirstDisplayedSlot(profile.SlotCreated, type => DisplayOrder(profile, type));

        /// <summary>A stored profile's display order for one group, rebuilt
        /// the way SettingsManager.SlotOrders.ReconcileLocked rebuilds the
        /// live one when the profile is applied: the saved order's created
        /// slots of that group, then the group's other created slots in
        /// ascending index. A profile saved before slot orders existed has
        /// none, and its order is ascending. A slot whose type is missing
        /// reads as Xbox, as <see cref="AddToProfile"/> assumes.</summary>
        internal static List<int> DisplayOrder(ProfileData profile, VirtualControllerType group)
        {
            var order = new List<int>();
            var created = profile?.SlotCreated;
            if (created == null) return order;
            int[] saved = group switch
            {
                VirtualControllerType.Xbox => profile.XboxSlotOrder,
                VirtualControllerType.PlayStation => profile.PlayStationSlotOrder,
                VirtualControllerType.Nintendo => profile.NintendoSlotOrder,
                VirtualControllerType.Extended => profile.ExtendedSlotOrder,
                VirtualControllerType.KeyboardMouse => profile.KeyboardMouseSlotOrder,
                VirtualControllerType.Midi => profile.MidiSlotOrder,
                VirtualControllerType.Vr => profile.VrSlotOrder,
                _ => null,
            };
            int count = Math.Min(created.Length, InputManager.MaxPads);
            bool Fits(int slot) => slot >= 0 && slot < count && created[slot] && TypeOf(profile, slot) == group;
            if (saved != null)
                foreach (int slot in saved)
                    if (Fits(slot) && !order.Contains(slot)) order.Add(slot);
            for (int slot = 0; slot < count; slot++)
                if (Fits(slot) && !order.Contains(slot)) order.Add(slot);
            return order;
        }

        private static VirtualControllerType TypeOf(ProfileData profile, int slot)
            => profile.SlotControllerTypes != null && slot < profile.SlotControllerTypes.Length
               && Enum.IsDefined(typeof(VirtualControllerType), profile.SlotControllerTypes[slot])
                ? (VirtualControllerType)profile.SlotControllerTypes[slot]
                : VirtualControllerType.Xbox;

        /// <summary>Whether a PlayStation controller of this profile carries
        /// the game's lightbar: a DualSense or a DualShock 4, the two whose
        /// output reports PadForge reads the lightbar from. No profile is the
        /// PlayStation default, a DualSense.</summary>
        internal static bool DecodesLightbar(string profileId)
        {
            string id = string.IsNullOrEmpty(profileId) ? InputManager.DefaultPlayStationProfileId : profileId;
            return id.StartsWith("dualsense", StringComparison.OrdinalIgnoreCase)
                   || id.StartsWith("dualshock-4", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The first created PlayStation slot in display order whose
        /// controller carries the game's lightbar. -1 when there is
        /// none.</summary>
        internal static int FirstLightbarSlot(IReadOnlyList<bool> created, IReadOnlyList<int> playStationOrder,
            Func<int, string> profileIdOf)
        {
            if (created == null || playStationOrder == null) return -1;
            foreach (int index in playStationOrder)
                if (index >= 0 && index < created.Count && created[index] && DecodesLightbar(profileIdOf(index)))
                    return index;
            return -1;
        }

        /// <summary>The slot a migrated row goes to: the first lightbar slot
        /// for a lighting row, the first displayed slot for the Sensa
        /// row.</summary>
        internal static int RulingSlot(PeripheralRowKind kind, IReadOnlyList<bool> created,
            Func<VirtualControllerType, IReadOnlyList<int>> orderOf, Func<int, string> profileIdOf)
            => PeripheralOutputRow.IsLightingRow(kind)
                ? FirstLightbarSlot(created, orderOf(VirtualControllerType.PlayStation), profileIdOf)
                : FirstDisplayedSlot(created, orderOf);

        internal static int RulingSlot(PeripheralRowKind kind, ProfileData profile)
        {
            if (profile == null) return -1;
            if (!PeripheralOutputRow.IsLightingRow(kind)) return FirstDisplayedSlot(profile);
            var ids = profile.SlotProfileIds;
            return FirstLightbarSlot(profile.SlotCreated, DisplayOrder(profile, VirtualControllerType.PlayStation),
                slot => ids != null && slot < ids.Length ? ids[slot] : null);
        }

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
        /// <paramref name="liveSlot"/> answers the live slot a row goes to, and
        /// <paramref name="slotPadSetting"/> a fresh default setting for a
        /// device on a slot of the live topology. True when anything changed,
        /// so the caller saves.</summary>
        public static bool Run(IEnumerable<LegacySwitch> switches, IList<ProfileData> profiles,
            ProfileData activeProfile, ProfileData defaultSnapshot, Func<PeripheralRowKind, int> liveSlot,
            Func<UserDevice, int, PadSetting> slotPadSetting)
        {
            bool changed = false;
            foreach (var s in switches)
            {
                bool live = (activeProfile != null ? s.Read(activeProfile) : null) ?? s.Global;
                if (s.Available && live) changed |= AssignLive(s.Kind, liveSlot(s.Kind), slotPadSetting);
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
        /// and its assignment to the live slot it goes to.</summary>
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

        /// <summary>An entry for the row on the profile's slot for it, with a
        /// default setting for that slot's controller type.</summary>
        internal static bool AddToProfile(ProfileData profile, PeripheralRowKind kind)
        {
            int slot = RulingSlot(kind, profile);
            if (slot < 0) return false;
            var guid = PeripheralOutputRow.IdentityFor(kind);
            var entries = profile.Entries?.ToList() ?? new List<ProfileEntry>();
            if (entries.Any(e => e.InstanceGuid == guid && e.MapTo == slot)) return false;

            var record = NewRowRecord(kind);
            var type = TypeOf(profile, slot);
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
