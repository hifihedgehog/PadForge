using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using PadForge.Services;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Controller database rows (#597)
        //  The changed rows go to SDL as it starts
        //  (LoadEmbeddedGamepadMappings). A new row goes to SDL here, when
        //  Step 1 meets a joystick the row is for, before it opens it. The
        //  rules are ControllerDatabaseUse's.
        // ─────────────────────────────────────────────

        /// <summary>Raised on the poll thread when a new row was used for a
        /// controller for the first time. The row carries a mark from then
        /// on, and the settings are saved with it.</summary>
        internal event Action ControllerDatabaseRowFirstUsed;

        private static readonly Lazy<HashSet<string>> OwnMappingIds =
            new(() => ControllerDatabaseUse.OwnIds(ControllerDatabaseUse.OwnRows));

        /// <summary>
        /// Gives SDL the stored new row for a joystick it lists, when there
        /// is one and its conditions hold. Called before the joystick is
        /// opened, because the open asks SDL whether the joystick is a
        /// gamepad (SdlDeviceWrapper.Open).
        ///
        /// <para>SDL_AddGamepadMapping looks every listed joystick's mapping
        /// up again and records this one as a gamepad
        /// (PopMappingChangeTracking in SDL_gamepad.c), so SDL_IsGamepad
        /// answers true right after it. Where it does not, the row did not
        /// take.</para>
        ///
        /// <para>Returns the controller ID of a row used for the first time,
        /// or null. Step 1 marks the row in use
        /// (<see cref="MarkControllerDatabaseRowInUse"/>) only once it keeps
        /// the device, so an open that fails, or a device the self-readback
        /// guard passes over, leaves no mark.</para>
        /// </summary>
        private string OfferControllerDatabaseRow(uint instanceId)
        {
            var section = SettingsManager.ControllerDatabase;
            if (section == null || section.NewRows == 0) return null;
            try
            {
                string guid = GetJoystickGUIDStringForID(instanceId);
                // Nearly every joystick has no row, and costs no more than this.
                if (!section.TryFindNew(ControllerDatabaseRow.LookupId(guid), out _)) return null;

                var answers = new ControllerDatabaseUse.SdlAnswers(guid, SDL_IsGamepad(instanceId), SDL_GetJoystickTypeForID(instanceId),
                    BlissBoxApi.ReadsRaw(SDL_GetJoystickVendorForID(instanceId), SDL_GetJoystickProductForID(instanceId)));
                var row = ControllerDatabaseUse.NewRowFor(section, answers, OwnMappingIds.Value,
                    ModelHasSavedAssignment, ModelIsForcedRaw, out bool firstUse);
                if (row == null) return null;

                int added = SDL_AddGamepadMapping(row.Text);
                bool reads = added >= 0 && SDL_IsGamepad(instanceId);
                SdlDiagLog.WriteLine($"MAPPINGS database row {row.Guid} for SDL#{instanceId}: added={added} gamepad={reads} firstUse={firstUse}");
                return reads && firstUse ? row.Guid : null;
            }
            catch (Exception ex)
            {
                // The joystick opens as SDL reads it without the row. A
                // failure here must never keep a controller from opening.
                SdlDiagLog.WriteLine($"MAPPINGS database row for SDL#{instanceId} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Marks the new row a kept device was first read through,
        /// and has the settings saved with the mark. Null does nothing.</summary>
        private void MarkControllerDatabaseRowInUse(string id)
        {
            if (id == null) return;
            if (SettingsManager.ChangeControllerDatabase(current => current?.WithRowInUse(id)))
                ControllerDatabaseRowFirstUsed?.Invoke();
        }

        private static UserDevice[] SavedDevices()
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return Array.Empty<UserDevice>();
            lock (devices.SyncRoot) return devices.Items.ToArray();
        }

        private static bool ModelHasSavedAssignment(ushort vendorId, ushort productId)
        {
            UserSetting[] assignments = Array.Empty<UserSetting>();
            var settings = SettingsManager.UserSettings;
            if (settings != null)
                lock (settings.SyncRoot) assignments = settings.Items.ToArray();
            var profiles = new List<ProfileData>(ProfilesSnapshot());
            if (SettingsManager.PendingDefaultSnapshot is { } pending) profiles.Add(pending);
            return ControllerDatabaseUse.HasSavedAssignment(vendorId, productId, SavedDevices(), assignments, profiles,
                SettingsManager.GlobalMacros);
        }

        private static bool ModelIsForcedRaw(ushort vendorId, ushort productId) =>
            ControllerDatabaseUse.IsForcedRaw(vendorId, productId, SavedDevices());
    }
}
