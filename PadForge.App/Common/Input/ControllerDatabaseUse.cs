using System;
using System.Collections.Generic;
using System.IO;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using PadForge.Services;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    /// <summary>
    /// How the stored controller database rows reach SDL (#600), as plain
    /// logic over SDL's answers, so each rule is tested without SDL.
    ///
    /// <para>SDL holds one mapping per controller ID and three priorities
    /// (SDL_gamepad.c): its built-in rows, the fork's community copy among
    /// them, are DEFAULT, a row added through SDL_AddGamepadMapping is API,
    /// and a row from the SDL_GAMECONTROLLERCONFIG hint is USER. A row
    /// replaces the one held for its ID only at the same or a higher
    /// priority (SDL_PrivateAddMappingForGUID). So the order rows are added
    /// in decides which one stands among PadForge's own additions, and no
    /// addition of PadForge's displaces a USER row. PadForge's process has
    /// the variable when the environment it started with holds it, and SDL
    /// then reads its rows at USER priority.</para>
    ///
    /// <para>A changed row is for an ID the fork's community copy holds, and
    /// replaces that copy. A new row is for an ID this release has no
    /// Windows row for, and a row added for it would be read for every
    /// controller of that ID. So it is added only when such a controller
    /// connects and all of <see cref="NewRowFor"/>'s conditions hold.</para>
    /// </summary>
    internal static class ControllerDatabaseUse
    {
        /// <summary>The embedded file of PadForge's own rows.</summary>
        internal const string OwnRowsResource = "PadForge.gamecontrollerdb_padforge.txt";

        /// <summary>The rows of a mapping file, comments and blank lines
        /// left out.</summary>
        internal static List<string> ReadRows(TextReader reader)
        {
            var rows = new List<string>();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                rows.Add(trimmed);
            }
            return rows;
        }

        private static readonly Lazy<IReadOnlyList<string>> LazyOwnRows = new(() =>
        {
            using var stream = typeof(ControllerDatabaseUse).Assembly.GetManifestResourceStream(OwnRowsResource);
            if (stream == null) return Array.Empty<string>();
            using var reader = new StreamReader(stream);
            return ReadRows(reader);
        });

        /// <summary>PadForge's own rows, as the exe carries them. Empty when
        /// the resource is missing, which a build with the file left out of
        /// the project would be.</summary>
        internal static IReadOnlyList<string> OwnRows => LazyOwnRows.Value;

        /// <summary>The controller IDs of PadForge's own rows, each as SDL's
        /// lookup reaches it from a joystick (<see cref="ControllerDatabaseRow.LookupId"/>).
        /// A row of PadForge's that names a name hash maps one variant of a
        /// controller on purpose, and no downloaded row is added for the
        /// others.</summary>
        internal static HashSet<string> OwnIds(IEnumerable<string> ownRows)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string row in ownRows)
            {
                int comma = row.IndexOf(',');
                string id = comma < 0 ? null : ControllerDatabaseRow.LookupId(row.Substring(0, comma));
                if (id != null) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// What SDL is given right after it starts, in the order it is
        /// given: the changed rows, then PadForge's own. Both are added at
        /// the API priority, where a later row for the same ID replaces an
        /// earlier one, so PadForge's own row stands wherever the two
        /// meet. A new row is never among them.
        ///
        /// <para>A changed row is given only while this release's baseline
        /// still says the fork carries a community row for its ID. The
        /// stored kind alone is not taken: a row added at startup for an ID
        /// of SDL's own Windows section would replace SDL's row.</para>
        /// </summary>
        internal static List<string> StartupRows(ControllerDatabaseSection section, ControllerDatabaseBaseline baseline,
            IEnumerable<string> ownRows)
        {
            var rows = new List<string>();
            if (section != null && baseline != null)
            {
                foreach (var stored in section.Rows)
                {
                    if (stored.Kind == ControllerDatabaseRowKind.Changed
                        && baseline.TryFind(stored.Row.Guid, out var entry) && entry.Verdict == ControllerDatabaseVerdict.Kept)
                        rows.Add(stored.Row.Text);
                }
            }
            rows.AddRange(ownRows);
            return rows;
        }

        /// <summary>The changed rows the row's reset notes as SDL starts
        /// (ControllerDatabaseEngine.NoteDownloaded): each but one whose ID
        /// PadForge's own rows map (<paramref name="ownIds"/>, <see cref="OwnIds"/>).
        /// PadForge's own row goes to SDL after it and stands there
        /// (<see cref="StartupRows"/>), so the downloaded row never reads a
        /// device, and what SDL holds before it is not what the reset is to
        /// give back.</summary>
        internal static List<string> StartupNoted(IReadOnlyList<string> changed, ISet<string> ownIds)
        {
            var noted = new List<string>(changed?.Count ?? 0);
            foreach (string row in changed ?? Array.Empty<string>())
            {
                int comma = row?.IndexOf(',') ?? -1;
                string id = comma < 0 ? null : ControllerDatabaseRow.LookupId(row.Substring(0, comma));
                if (id != null && ownIds != null && ownIds.Contains(id)) continue;
                noted.Add(row);
            }
            return noted;
        }

        /// <summary>
        /// The row of the user's that SDL is to be given under a device's own
        /// ID (#600 items 47 and 49), or null. SDL tries a device's exact
        /// version and checksum first (SDL_PrivateGetGamepadMappingForGUID),
        /// so a row of SDL's that names them stands over a row of the model
        /// that names neither, as Add Rows keeps rows. Where SDL reads the
        /// device through a mapping with other bindings than the user's row
        /// for it (<paramref name="sdlMapping"/>, null for none), the row goes
        /// to SDL once more under the device's own ID, as Paste gives one,
        /// which SDL finds at that first try. A device with a row of the
        /// user's under its own ID needs none.
        /// </summary>
        internal static string CopyFor(string deviceGuid, ControllerDatabaseMine mine, string sdlMapping)
        {
            if (string.IsNullOrEmpty(deviceGuid) || mine == null) return null;
            var row = mine.ForSdlDevice(deviceGuid);
            if (row == null || row.ForPadForgeDevice || string.Equals(row.Id, deviceGuid, StringComparison.OrdinalIgnoreCase)) return null;
            if (ControllerDatabaseSource.ReadsThrough(sdlMapping, row)) return null;
            return row.Row.TextUnder(deviceGuid.ToLowerInvariant());
        }

        /// <summary>
        /// What a row of the user's under a device's own ID takes the place
        /// of, for Paste to keep: the mapping SDL read the device through
        /// before any row of the user's. <paramref name="sdlMapping"/> is the
        /// one SDL reads it through now, empty for none. Under a copy of the
        /// user's row for its model (<paramref name="copied"/>), it is what SDL
        /// held under the device's ID before the first copy
        /// (<paramref name="beforeCopy"/>, null for none). Through the user's
        /// row for its model (<paramref name="model"/>, in the same bindings),
        /// it is what SDL held for that row's ID before it, null while that
        /// is not known. Where SDL had nothing for the device before a row of
        /// the user's that went (<paramref name="hadNone"/>), SDL still answers
        /// that row, and there was none. Otherwise no row of the user's is
        /// read, and SDL's mapping is the one. So Remove Mine gives back the
        /// row SDL had, whichever of the user's rows goes first.
        /// </summary>
        internal static string ReadBefore(string sdlMapping, ControllerDatabaseMineRow model, bool copied, string beforeCopy, bool hadNone)
        {
            if (copied) return beforeCopy ?? string.Empty;
            if (model != null && ControllerDatabaseSource.ReadsThrough(sdlMapping, model)) return model.OldRow;
            if (hadNone) return string.Empty;
            return sdlMapping ?? string.Empty;
        }

        /// <summary>A mapping SDL held, to give it back under the ID of the
        /// row or device it takes the place of, so SDL puts it on the same
        /// entry. Under an ID with no name checksum it keeps no checksum
        /// field: SDL reads a field's checksum into such an ID, which would
        /// put the mapping on another entry, and an ID with a checksum has SDL
        /// replace the field with its own (SDL_PrivateAddMappingForGUID). SDL
        /// takes the text through its turn to positional buttons again, so it
        /// goes as <see cref="ControllerDatabaseSource.ForSdlAgain"/> makes
        /// it. A text with no comma is no row and stays as it is.</summary>
        internal static string Under(string id, string mapping)
        {
            int comma = mapping.IndexOf(',');
            if (comma < 0) return mapping;
            if (SdlMappingRow.ChecksumOf(id) != 0) return ControllerDatabaseSource.ForSdlAgain(id + mapping.Substring(comma));
            var parts = new List<string>(mapping.Split(','));
            parts[0] = id;
            for (int i = parts.Count - 1; i >= 2; i--)
                if (parts[i].StartsWith("crc:", StringComparison.Ordinal)) parts.RemoveAt(i);
            return ControllerDatabaseSource.ForSdlAgain(string.Join(",", parts));
        }

        /// <summary>What SDL said of a joystick it lists, before PadForge
        /// opens it. <see cref="ReadRaw"/> is PadForge's own: it reads a
        /// Bliss-Box port raw while Read Bliss-Box Adapters is on, whatever
        /// SDL maps (SdlDeviceWrapper.OpensAsGamepad).</summary>
        internal readonly record struct SdlAnswers(string Guid, bool IsGamepad, SDL_JoystickType Type, bool ReadRaw);

        /// <summary>
        /// The new row to add for a joystick Step 1 is about to open, or
        /// null. A row is added only when all of these hold:
        /// <list type="number">
        /// <item>SDL does not read the joystick as a gamepad already. A row
        /// of SDL's or of the fork's, the XInput mapping and a HIDAPI
        /// driver's mapping all make SDL_IsGamepad true, so none of them is
        /// displaced.</item>
        /// <item>SDL types the joystick as unknown or as a gamepad. A wheel,
        /// a flight stick, an arcade stick, a throttle, a guitar or a drum
        /// kit has no gamepad's shape.</item>
        /// <item>PadForge's own file has no row for the controller, and
        /// PadForge does not read it raw whatever SDL maps.</item>
        /// <item>No saved assignment names a controller of this vendor and
        /// product. A controller read raw until now and assigned that way
        /// has mappings by its raw buttons and axes, which a gamepad row
        /// would give another meaning.</item>
        /// <item>Force Raw Joystick Mode is off for every saved controller
        /// of this vendor and product.</item>
        /// </list>
        /// A row already in use was added under all five once, and the
        /// assignment made since was made through it. For such a row the
        /// last two are not asked again, so the controller keeps the reading
        /// its mappings were made for, and Force Raw Joystick Mode switches
        /// between the two readings as it does for any gamepad.
        /// <paramref name="firstUse"/> says the row is to be marked in use.
        /// </summary>
        internal static ControllerDatabaseRow NewRowFor(ControllerDatabaseSection section, SdlAnswers sdl,
            IReadOnlySet<string> ownIds, Func<ushort, ushort, bool> hasSavedAssignment, Func<ushort, ushort, bool> forcedRaw,
            out bool firstUse)
        {
            firstUse = false;
            if (section == null || section.NewRows == 0) return null;
            string id = ControllerDatabaseRow.LookupId(sdl.Guid, i => section.TryFindNew(i, out _));
            if (!section.TryFindNew(id, out var stored)) return null;
            if (sdl.IsGamepad) return null;
            if (sdl.Type != SDL_JoystickType.SDL_JOYSTICK_TYPE_UNKNOWN && sdl.Type != SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD)
                return null;
            if (ownIds != null && ownIds.Contains(id)) return null;
            if (sdl.ReadRaw) return null;
            if (stored.InUse) return stored.Row;
            if (hasSavedAssignment(stored.Row.VendorId, stored.Row.ProductId)) return null;
            if (forcedRaw(stored.Row.VendorId, stored.Row.ProductId)) return null;
            firstUse = true;
            return stored.Row;
        }

        /// <summary>
        /// Whether a saved assignment names a controller of a vendor and
        /// product: a device assigned to a virtual controller now, or parked
        /// with its mappings while another profile is active, an entry of a
        /// stored profile, or a profile shortcut's trigger. A row maps every
        /// controller of its ID, so the question is asked of the model, not
        /// of the one unit that just connected.
        /// </summary>
        internal static bool HasSavedAssignment(ushort vendorId, ushort productId, IEnumerable<UserDevice> devices,
            IEnumerable<UserSetting> assignments, IEnumerable<ProfileData> profiles, IEnumerable<GlobalMacroData> shortcuts)
        {
            Guid productGuid = SdlDeviceWrapper.BuildProductGuid(vendorId, productId);
            var units = new HashSet<Guid>();
            foreach (var device in devices ?? Array.Empty<UserDevice>())
                if (device != null && device.VendorId == vendorId && device.ProdId == productId) units.Add(device.InstanceGuid);

            bool Names(Guid instance, Guid product) =>
                (instance != Guid.Empty && units.Contains(instance)) || product == productGuid;

            foreach (var assignment in assignments ?? Array.Empty<UserSetting>())
                if (assignment != null && Names(assignment.InstanceGuid, assignment.ProductGuid)) return true;
            foreach (var profile in profiles ?? Array.Empty<ProfileData>())
            {
                if (profile?.Entries == null) continue;
                foreach (var entry in profile.Entries)
                    if (entry != null && Names(entry.InstanceGuid, entry.ProductGuid)) return true;
            }
            foreach (var shortcut in shortcuts ?? Array.Empty<GlobalMacroData>())
            {
                if (shortcut == null) continue;
                if (Names(shortcut.TriggerDeviceGuid, Guid.Empty)) return true;
                if (shortcut.TriggerEntries == null) continue;
                foreach (var entry in shortcut.TriggerEntries)
                    if (entry != null && Names(entry.DeviceInstanceGuid, entry.DeviceProductGuid)) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a controller that SDL maps, and so types a gamepad, still
        /// opens raw: every saved entry of its vendor and product was read raw,
        /// as a joystick with Force Raw Joystick Mode off, and a saved
        /// assignment names the model. A new community row (hifihedgehog/SDL#53),
        /// a new row of SDL's own table or a HIDAPI driver that now claims the
        /// controller makes SDL map it, and opened as a gamepad its buttons and
        /// axes would come back renumbered under the user's mapping. A row of
        /// the user's, or a downloaded row the user took, is the user's choice.
        /// A downloaded row is withheld from the same controllers
        /// (<see cref="OfferRow"/>). A Bliss-Box port is left to Read Bliss-Box
        /// Adapters (#469). A controller SDL types otherwise, as an arcade
        /// stick, is saved as a joystick while SDL reads it as a gamepad.
        /// </summary>
        internal static bool KeepsRawRead(SdlMappingSource source, SDL_JoystickType type, ushort vendorId, ushort productId,
            IEnumerable<UserDevice> devices, Func<ushort, ushort, bool> hasSavedAssignment)
        {
            if (source is SdlMappingSource.None or SdlMappingSource.Yours or SdlMappingSource.Downloaded) return false;
            if (type != SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD || BlissBoxProtocol.IsPort(vendorId, productId)) return false;
            bool readRaw = false;
            foreach (var device in devices ?? Array.Empty<UserDevice>())
            {
                if (device == null || device.VendorId != vendorId || device.ProdId != productId) continue;
                if (device.ForceRawJoystickMode || device.CapType != InputDeviceType.Joystick) return false;
                readRaw = true;
            }
            return readRaw && hasSavedAssignment(vendorId, productId);
        }

        /// <summary>Whether Force Raw Joystick Mode is on for a saved
        /// controller of a vendor and product.</summary>
        internal static bool IsForcedRaw(ushort vendorId, ushort productId, IEnumerable<UserDevice> devices)
        {
            foreach (var device in devices ?? Array.Empty<UserDevice>())
                if (device != null && device.VendorId == vendorId && device.ProdId == productId && device.ForceRawJoystickMode)
                    return true;
            return false;
        }
    }
}
