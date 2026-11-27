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
        //  Controller database rows (#600)
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
            // A row of the user's goes to SDL under the joystick's own ID
            // first, where SDL would read it through another row (#600).
            GiveCopy(instanceId);
            var section = SettingsManager.ControllerDatabase;
            if (section == null || section.NewRows == 0) return null;
            try
            {
                string guid = GetJoystickGUIDStringForID(instanceId);
                // Nearly every joystick has no row, and costs no more than this.
                if (!section.TryFindNew(ControllerDatabaseRow.LookupId(guid, id => section.TryFindNew(id, out _)), out _)) return null;
                // A row of the user's for the device stands (#600), and a
                // downloaded row under the same ID would replace it in SDL.
                if (ControllerDatabaseMine.Current.ForSdlDevice(guid) != null) return null;

                var answers = new ControllerDatabaseUse.SdlAnswers(guid, SDL_IsGamepad(instanceId), SDL_GetJoystickTypeForID(instanceId),
                    BlissBoxApi.ReadsRaw(SDL_GetJoystickVendorForID(instanceId), SDL_GetJoystickProductForID(instanceId))
                    || ControllerDatabaseEngine.ReadsRaw(guid));
                var row = ControllerDatabaseUse.NewRowFor(section, answers, OwnMappingIds.Value,
                    ModelHasSavedAssignment, ModelIsForcedRaw, out bool firstUse);
                if (row == null) return null;

                // What SDL held under the ID goes back with the row's reset.
                ControllerDatabaseEngine.NoteDownloaded(row.Guid, ControllerDatabaseEngine.MappingForGuid(row.Guid), row.Text);
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

        /// <summary>
        /// Gives SDL a row of the user's under a joystick's own ID when SDL
        /// would read the joystick through a mapping with other bindings
        /// (<see cref="ControllerDatabaseUse.CopyFor"/>), and keeps the mapping
        /// SDL held under that ID before, for the row's removal
        /// (<see cref="ControllerDatabaseEngine.TakeCopies"/>). Poll thread, or
        /// as Step 1 opens a joystick. A failure leaves the joystick as SDL
        /// reads it.
        /// </summary>
        private static void GiveCopy(uint instanceId)
        {
            var mine = ControllerDatabaseMine.Current;
            if (mine.Count == 0) return;
            try
            {
                string guid = GetJoystickGUIDStringForID(instanceId);
                string mapping = ControllerDatabaseEngine.MappingForId(instanceId);
                string copy = ControllerDatabaseUse.CopyFor(guid, mine, mapping);
                if (copy == null) return;
                int added = SDL_AddGamepadMapping(copy);
                if (added >= 0) ControllerDatabaseEngine.NoteCopy(guid, mine.ForSdlDevice(guid).Id, mapping);
                SdlDiagLog.WriteLine($"MAPPINGS your row under SDL#{instanceId}'s own ID: added={added}");
            }
            catch (Exception ex)
            {
                SdlDiagLog.WriteLine($"MAPPINGS your row under SDL#{instanceId}'s own ID failed: {ex.Message}");
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

        /// <summary>Whether a joystick SDL maps keeps the raw read its saved
        /// assignment was made with
        /// (<see cref="ControllerDatabaseUse.KeepsRawRead"/>). Asked as the
        /// joystick opens, never per pass, and the cheap check of the saved
        /// entries comes before SDL is asked for the mapping.</summary>
        internal static bool KeepsSavedRawRead(uint instanceId)
        {
            try
            {
                ushort vendor = SDL_GetJoystickVendorForID(instanceId), product = SDL_GetJoystickProductForID(instanceId);
                var devices = SavedDevices();
                if (!ControllerDatabaseUse.KeepsRawRead(SdlMappingSource.Community, SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD,
                        vendor, product, devices, (_, _) => true))
                    return false;
                string guid = GetJoystickGUIDStringForID(instanceId);
                var source = ControllerDatabaseSource.Of(guid, ControllerDatabaseEngine.MappingForId(instanceId),
                    ControllerDatabaseMine.Current, SettingsManager.ControllerDatabase, ControllerDatabaseUse.OwnRows,
                    ControllerDatabaseBaseline.Embedded, PadForge.Services.ControllerDatabaseProfileRows.Applied?.Invoke());
                return ControllerDatabaseUse.KeepsRawRead(source, SDL_GetJoystickTypeForID(instanceId), vendor, product, devices,
                    ModelHasSavedAssignment);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ModelIsForcedRaw(ushort vendorId, ushort productId) =>
            ControllerDatabaseUse.IsForcedRaw(vendorId, productId, SavedDevices());

        // ─────────────────────────────────────────────
        //  Rows given at run time (#600)
        //  The interface thread posts work (ControllerDatabaseEngine.Post),
        //  and the poll thread runs it here once a cycle, so every SDL call
        //  and every reopen happens where Step 1 makes them.
        // ─────────────────────────────────────────────

        /// <summary>Runs the controller database work the interface thread
        /// posted. Called once a poll cycle, beside the Bliss-Box retries.
        /// Nothing posted costs one read.</summary>
        private void RunControllerDatabaseWork() => ControllerDatabaseEngine.Drain(this);

        /// <summary>What SDL says of a device now, or null when the device is
        /// not one SDL lists.</summary>
        internal SdlDeviceFacts ReadSdlFacts(Guid instance)
        {
            var ud = FindOnlineDeviceByInstanceGuid(instance);
            if (ud?.Device is not SdlDeviceWrapper wrapper) return null;
            uint id = wrapper.SdlInstanceId;
            string guid = GetJoystickGUIDStringForID(id);
            if (string.IsNullOrEmpty(guid) || guid.Trim('0').Length == 0) return null;
            string mapping = ControllerDatabaseEngine.MappingForId(id);
            return new SdlDeviceFacts(guid, mapping, wrapper.GameController != IntPtr.Zero, SDL_IsGamepad(id));
        }

        /// <summary>
        /// Gives SDL rows and has every listed joystick read the way SDL now
        /// maps it (#600 items 47 and 49). A joystick open the other way from
        /// SDL's answer, or from the session's raw list, gets a fresh wrapper
        /// for the same SDL instance, as the Bliss-Box switch does
        /// (<see cref="ReopenBlissBoxRows"/>), and so does a gamepad whose
        /// mapping changed, since the wrapper reads its controls from the
        /// mapping as it opens. With <paramref name="offerNew"/>, the stored
        /// new rows are offered to every joystick SDL reads as a joystick,
        /// under <see cref="ControllerDatabaseUse.NewRowFor"/>'s conditions,
        /// as a newly listed joystick is offered one.
        /// </summary>
        internal ControllerDatabaseApplied ApplySdlRows(IReadOnlyList<string> rows, bool offerNew)
        {
            var devices = SettingsManager.UserDevices;
            var listed = new List<(UserDevice Device, SdlDeviceWrapper Wrapper, string Mapping)>();
            if (devices != null)
                lock (devices.SyncRoot)
                    foreach (var ud in devices.Items)
                        if (ud != null && ud.IsOnline && ud.Device is SdlDeviceWrapper w) listed.Add((ud, w, null));
            for (int i = 0; i < listed.Count; i++)
                listed[i] = (listed[i].Device, listed[i].Wrapper, ControllerDatabaseEngine.MappingForId(listed[i].Wrapper.SdlInstanceId));

            int added = 0, refused = 0;
            foreach (string row in rows ?? Array.Empty<string>())
            {
                int result = SDL_AddGamepadMapping(row);
                if (result >= 0) added++;
                else refused++;
                SdlDiagLog.WriteLine($"MAPPINGS user or database row at run time: result={result}");
            }

            var firstUsed = new List<string>();
            if (offerNew)
                foreach (var (_, w, _) in listed)
                    if (w.GameController == IntPtr.Zero && OfferControllerDatabaseRow(w.SdlInstanceId) is { } used)
                        firstUsed.Add(used);
            // A row of the user's that SDL reads a device past goes to SDL
            // again under the device's own ID (#600), before it is opened again.
            foreach (var (_, w, _) in listed)
                GiveCopy(w.SdlInstanceId);

            var reopened = new List<Guid>();
            foreach (var (ud, old, mappingBefore) in listed)
            {
                uint id = old.SdlInstanceId;
                bool wants = SdlDeviceWrapper.OpensAsGamepad(SDL_IsGamepad(id) && !ControllerDatabaseEngine.ReadsRaw(GetJoystickGUIDStringForID(id))
                    && !InputManager.KeepsSavedRawRead(id), old.VendorId, old.ProductId);
                bool isGamepad = old.GameController != IntPtr.Zero;
                bool sameMapping = !isGamepad
                    || string.Equals(mappingBefore, ControllerDatabaseEngine.MappingForId(id), StringComparison.Ordinal);
                if (wants == isGamepad && sameMapping) continue;
                var fresh = new SdlDeviceWrapper();
                bool loaded = false;
                try
                {
                    if (fresh.Open(id))
                    {
                        if (ud.InstanceGuid != fresh.InstanceGuid) fresh.OverrideInstanceGuid(ud.InstanceGuid);
                        ud.LoadFromSdlDevice(fresh);
                        loaded = true;
                    }
                }
                catch (Exception ex)
                {
                    RaiseDeviceError($"Error reopening device (instance {id})", ex);
                }
                finally
                {
                    if (!loaded) try { fresh.Dispose(); } catch { }
                }
                if (!loaded) continue;
                _openedSdlInstanceIds[fresh.SdlInstanceId] = fresh;
                reopened.Add(ud.InstanceGuid);
                SdlDiagLog.WriteLine($"DEV reopen SDL#{id} {fresh.VendorId:X4}:{fresh.ProductId:X4} gamepad={fresh.GameController != IntPtr.Zero} (controller database row)");
                NoteUserRowOpened(ud, fresh);
            }
            foreach (string used in firstUsed) MarkControllerDatabaseRowInUse(used);
            return new ControllerDatabaseApplied(true, added, refused, reopened);
        }
    }

    public partial class InputManager
    {
        /// <summary>
        /// A device opened as a gamepad through a row of the user's whose
        /// remap it still owes, because it was away or the engine stopped
        /// when the row was added, is announced
        /// (<see cref="ControllerDatabaseEngine.UserRowOpened"/>), and the
        /// interface maps its rows again from the new layout. Called by Step 1
        /// once it keeps a device, and after a reopen. Poll thread only.
        /// </summary>
        private static void NoteUserRowOpened(UserDevice ud, SdlDeviceWrapper wrapper)
        {
            if (ud == null || wrapper == null || wrapper.GameController == IntPtr.Zero) return;
            var mine = ControllerDatabaseMine.Current;
            if (mine.Count == 0) return;
            var row = mine.ForSdlDevice(ud.SdlGuid);
            if (row == null || row.HasMapped(ud.InstanceGuid)) return;
            // Only when SDL reads it through this row.
            if (!ControllerDatabaseSource.ReadsThrough(ControllerDatabaseEngine.MappingForId(wrapper.SdlInstanceId), row)) return;
            ControllerDatabaseEngine.RaiseUserRowOpened(ud.InstanceGuid);
        }
    }

    /// <summary>What SDL says of a device (#600 item 45): its ID, the
    /// mapping SDL holds for it (null for none), whether PadForge opened it
    /// through that mapping, and whether SDL maps it at all.</summary>
    internal sealed record SdlDeviceFacts(string Guid, string Mapping, bool OpenedAsGamepad, bool SdlMaps);

    /// <summary>What giving SDL rows came to: whether the engine ran the
    /// work, how many rows SDL took and refused, and the devices that were
    /// opened again.</summary>
    internal sealed record ControllerDatabaseApplied(bool Ran, int Added, int Refused, IReadOnlyList<Guid> Reopened)
    {
        internal static readonly ControllerDatabaseApplied NotRunning = new(false, 0, 0, Array.Empty<Guid>());
    }

    /// <summary>
    /// The door between the interface and the poll thread for the controller
    /// database (#600). The interface posts work, and the poll thread runs
    /// it once a cycle (<see cref="InputManager.RunControllerDatabaseWork"/>),
    /// so SDL is called only where Step 1 calls it. Work posted while the
    /// engine is stopped is not run: the stored rows reach SDL as it starts
    /// (InputManager.LoadEmbeddedGamepadMappings).
    /// </summary>
    internal static class ControllerDatabaseEngine
    {
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action<InputManager>> Queue = new();

        /// <summary>The joysticks read raw until SDL starts again, by the ID
        /// of the user's row that was removed: a device's own ID, or a row's
        /// for every device it maps (<see cref="SdlMappingRow.Maps"/>). SDL
        /// keeps a mapping once given until it quits, and starts again with
        /// only the rows PadForge has then (<see cref="SdlStarting"/>).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RawIds =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The rows of the user's SDL was given again under a
        /// device's own ID this start, by the device's ID: the row's ID and
        /// the mapping SDL held under the device's ID before the first copy,
        /// null for none (InputManager.GiveCopy).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string RowId, string Old)> Copies =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The IDs SDL was given a mapping back under this start, as
        /// a row of the user's or its copy went: SDL reads a device of such an
        /// ID through that entry, whatever a removed row of its model
        /// left.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> GivenBack =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The IDs of the user's rows that went with no mapping of
        /// SDL's before them, as <see cref="RawIds"/> takes them, kept for the
        /// rest of the start after a new row lifts the raw read: SDL still
        /// answers the removed row's bindings for them (<see cref="HadNone"/>).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> NoneIds =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The IDs SDL was given a downloaded row under this start,
        /// a changed one as SDL started or after Update Now, or a new one as a
        /// joystick of its ID was listed, each with what SDL held under the ID
        /// before the first, empty for none. The row's reset gives that back
        /// (<see cref="TakeDownloaded"/>), since SDL keeps a mapping once
        /// given until it quits.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Before, string Text)> Downloaded =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The IDs the row's reset read raw because SDL held nothing
        /// under them before a downloaded row, each with that row's text:
        /// lifted when a download is applied again
        /// (<see cref="LiftDownloadedRaw"/>), so its new rows are offered.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> DownloadedRaw =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Notes what SDL holds under a row's ID before the downloaded
        /// row <paramref name="text"/> is given to it. The first note for an
        /// ID stands. Poll thread, or as SDL starts.</summary>
        internal static void NoteDownloaded(string id, string before, string text)
        {
            if (string.IsNullOrEmpty(id)) return;
            Downloaded.TryAdd(id, (before ?? string.Empty, text ?? string.Empty));
        }

        /// <summary><see cref="NoteDownloaded(string, string, string)"/> for
        /// each row text, by its ID, with <paramref name="held"/> asked what
        /// SDL holds under it.</summary>
        internal static void NoteDownloaded(IEnumerable<string> rows, Func<string, string> held)
        {
            foreach (string row in rows ?? Array.Empty<string>())
            {
                if (row == null || row.Length < 33 || row[32] != ',') continue;
                string id = row.Substring(0, 32).ToLowerInvariant();
                if (!Downloaded.ContainsKey(id)) NoteDownloaded(id, held(id), row);
            }
        }

        /// <summary>Lifts the raw read the row's reset set for IDs SDL held
        /// nothing under (<see cref="TakeDownloaded"/>), and no other: a
        /// download applied again offers its new rows. SDL still holds the
        /// downloaded row under each such ID, and reads its device through it
        /// again without offering it once more, so the ID is noted again with
        /// nothing before it, for the next reset. Poll thread.</summary>
        internal static void LiftDownloadedRaw()
        {
            foreach (string id in DownloadedRaw.Keys)
            {
                if (!DownloadedRaw.TryRemove(id, out string text)) continue;
                RawIds.TryRemove(id, out _);
                NoteDownloaded(id, string.Empty, text);
            }
        }

        /// <summary>
        /// What SDL is given back as the downloaded rows go (the row's reset):
        /// for each ID it was given one under, the mapping it held before,
        /// under that ID, or, where it held none, the devices of the ID read
        /// raw until SDL starts again. An ID a row of the user's stands under
        /// in <paramref name="mine"/> keeps that row. Poll thread.
        /// </summary>
        internal static List<string> TakeDownloaded(ControllerDatabaseMine mine,
            List<(string Id, string Text, string Before)> taken = null)
        {
            var back = new List<string>();
            foreach (var pair in Downloaded)
            {
                if (!Downloaded.TryRemove(pair.Key, out var held)) continue;
                taken?.Add((pair.Key, held.Text, held.Before));
                bool yours = false;
                foreach (var row in mine?.Rows ?? Array.Empty<ControllerDatabaseMineRow>())
                    if (!row.ForPadForgeDevice && string.Equals(row.Id, pair.Key, StringComparison.OrdinalIgnoreCase)) yours = true;
                if (yours) continue;
                if (held.Before.Length > 0)
                {
                    back.Add(ControllerDatabaseUse.Under(pair.Key, held.Before));
                    GivenBack[pair.Key] = 0;
                }
                else
                {
                    if (!RawIds.ContainsKey(pair.Key)) DownloadedRaw[pair.Key] = held.Text;
                    SetReadsRaw(pair.Key, true);
                }
            }
            return back;
        }

        /// <summary>As the engine starts, before SDL does: the joysticks read
        /// raw since a row of the user's went are read as SDL maps them
        /// again, and no copy of a row or mapping given back is held, since
        /// SDL starts with no row PadForge has not given it this
        /// start.</summary>
        internal static void SdlStarting()
        {
            RawIds.Clear();
            Copies.Clear();
            GivenBack.Clear();
            NoneIds.Clear();
            Downloaded.Clear();
            DownloadedRaw.Clear();
        }

        /// <summary>
        /// Whether SDL had no mapping of its own for this ID before a row of
        /// the user's that went this start, under the ID itself, or under its
        /// model's ID while SDL was given no mapping back under the ID. SDL
        /// keeps a removed row until it quits, so what it answers for such an
        /// ID is that row, and a row kept for the ID, or a copy given for it,
        /// replaces nothing.
        /// </summary>
        internal static bool HadNone(string deviceGuid)
            => !string.IsNullOrEmpty(deviceGuid) && !NoneIds.IsEmpty && Marked(NoneIds, deviceGuid);

        /// <summary>Whether a removed row's ID in <paramref name="ids"/> is the
        /// device's own, or its model's while SDL was given no mapping back
        /// under the device's ID.</summary>
        private static bool Marked(System.Collections.Concurrent.ConcurrentDictionary<string, byte> ids, string deviceGuid)
        {
            foreach (string id in ids.Keys)
            {
                if (string.Equals(id, deviceGuid, StringComparison.OrdinalIgnoreCase)) return true;
                if (SdlMappingRow.Maps(id, deviceGuid) && !GivenBack.ContainsKey(deviceGuid)) return true;
            }
            return false;
        }

        /// <summary>Whether SDL was given a copy of a row of the user's under
        /// a device's own ID this start, and the mapping SDL held under that
        /// ID before the first copy (<paramref name="old"/>, null for
        /// none).</summary>
        internal static bool HeldBeforeCopy(string deviceGuid, out string old)
        {
            old = null;
            if (string.IsNullOrEmpty(deviceGuid) || !Copies.TryGetValue(deviceGuid, out var copy)) return false;
            old = copy.Old;
            return true;
        }

        /// <summary>
        /// What SDL is given back for a row of the user's that went, besides
        /// its copies (<see cref="TakeCopies"/>): the mapping SDL held for the
        /// row's ID before it, under that ID. Where SDL held none, the devices
        /// the row mapped are read raw until SDL starts again. A row whose
        /// earlier mapping is not known, added while the engine was stopped
        /// and removed before it ran, gives nothing back, and a row for a
        /// device SDL does not read has nothing to give.
        /// </summary>
        internal static List<string> GiveBack(ControllerDatabaseMineRow row)
        {
            var texts = new List<string>();
            if (row == null || row.ForPadForgeDevice || row.OldRow == null) return texts;
            if (row.OldRow.Length == 0)
            {
                SetReadsRaw(row.Id, true);
                return texts;
            }
            texts.Add(ControllerDatabaseUse.Under(row.Id, row.OldRow));
            GivenBack[row.Id] = 0;
            return texts;
        }

        /// <summary>Notes that SDL was given a row of the user's under a
        /// device's own ID. A device given a copy before keeps the mapping
        /// SDL held before the first one, and one SDL had nothing for before
        /// a removed row (<see cref="HadNone"/>) keeps none.</summary>
        internal static void NoteCopy(string deviceGuid, string rowId, string old)
        {
            if (string.IsNullOrEmpty(deviceGuid) || rowId == null) return;
            if (HadNone(deviceGuid)) old = null;
            Copies.AddOrUpdate(deviceGuid, (rowId, string.IsNullOrEmpty(old) ? null : old), (_, held) => (rowId, held.Old));
        }

        /// <summary>
        /// The copies of a removed row of the user's, taken back: for each
        /// device SDL was given one for, the mapping SDL held under its ID
        /// before, to give SDL again, or, when SDL held none, the device read
        /// raw until SDL starts again. A device with a row of the user's under
        /// its own ID in <paramref name="mine"/> keeps that row's entry. Poll
        /// thread.
        /// </summary>
        internal static List<string> TakeCopies(string rowId, ControllerDatabaseMine mine)
        {
            var back = new List<string>();
            foreach (var pair in Copies)
            {
                if (!string.Equals(pair.Value.RowId, rowId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Copies.TryRemove(pair.Key, out var copy)) continue;
                var own = mine?.ForSdlDevice(pair.Key);
                if (own != null && string.Equals(own.Id, pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (copy.Old != null)
                {
                    back.Add(ControllerDatabaseUse.Under(pair.Key, copy.Old));
                    GivenBack[pair.Key] = 0;
                }
                else SetReadsRaw(pair.Key, true);
            }
            return back;
        }

        /// <summary>Raised on the poll thread when a device opens through a
        /// row of the user's whose remap it owes (#600), with its instance
        /// ID.</summary>
        internal static event Action<Guid> UserRowOpened;

        internal static void RaiseUserRowOpened(Guid instance) => UserRowOpened?.Invoke(instance);

        /// <summary>When the poll thread last looked, in
        /// <see cref="Environment.TickCount64"/> time.</summary>
        private static long _lastLook;

        /// <summary>How long the poll thread may not look before work is not
        /// posted: its slowest cycle, the idle one, runs at least every
        /// 100 ms.</summary>
        private const int StaleMs = 1500;

        /// <summary>How long a post waits for the poll thread.</summary>
        internal static TimeSpan Wait = TimeSpan.FromSeconds(3);

        static ControllerDatabaseEngine()
        {
            SdlDeviceWrapper.ReadsRawThisSession = id => !RawIds.IsEmpty && ReadsRaw(GetJoystickGUIDStringForID(id));
            SdlDeviceWrapper.KeepsSavedRawRead = InputManager.KeepsSavedRawRead;
        }

        /// <summary>Whether the engine runs, so posted work is done.</summary>
        internal static bool Running => Environment.TickCount64 - System.Threading.Interlocked.Read(ref _lastLook) < StaleMs;

        /// <summary>
        /// Whether PadForge reads the joystick of this ID raw until SDL starts
        /// again: a removed row of the user's, under the joystick's own ID or
        /// its model's, had no mapping of SDL's to give back. A joystick that
        /// a row of the user's maps now is read through that row, as it was
        /// kept or as its copy, and one SDL was given its own mapping back
        /// under its ID is read through that, whatever a removed row of its
        /// model left.
        /// </summary>
        internal static bool ReadsRaw(string deviceGuid)
        {
            if (deviceGuid == null || RawIds.IsEmpty) return false;
            if (ControllerDatabaseMine.Current?.ForSdlDevice(deviceGuid) != null) return false;
            return Marked(RawIds, deviceGuid);
        }

        /// <summary>Reads the joysticks a removed row of the user's mapped
        /// raw until SDL starts again, or, for a row given again, lets SDL's
        /// mapping decide again for every device it maps. A row that went
        /// with nothing before it is also noted for <see cref="HadNone"/>,
        /// which a row given again does not lift.</summary>
        internal static void SetReadsRaw(string rowId, bool raw)
        {
            if (string.IsNullOrEmpty(rowId)) return;
            if (raw)
            {
                RawIds[rowId] = 0;
                NoneIds[rowId] = 0;
                return;
            }
            foreach (string id in RawIds.Keys)
                if (string.Equals(id, rowId, StringComparison.OrdinalIgnoreCase) || SdlMappingRow.Maps(rowId, id) || SdlMappingRow.Maps(id, rowId))
                    RawIds.TryRemove(id, out _);
        }

        /// <summary><see cref="SetReadsRaw"/> lifting the raw read for a row
        /// given again, which answers the IDs it lifted, for
        /// <see cref="ReadRawAgain"/>.</summary>
        internal static List<string> LiftReadsRaw(string rowId)
        {
            var lifted = new List<string>();
            if (string.IsNullOrEmpty(rowId)) return lifted;
            foreach (string id in RawIds.Keys)
                if (string.Equals(id, rowId, StringComparison.OrdinalIgnoreCase) || SdlMappingRow.Maps(rowId, id) || SdlMappingRow.Maps(id, rowId))
                    if (RawIds.TryRemove(id, out _)) lifted.Add(id);
            return lifted;
        }

        /// <summary>Reads raw again the IDs a row SDL did not keep lifted
        /// (<see cref="LiftReadsRaw"/>).</summary>
        internal static void ReadRawAgain(IEnumerable<string> ids)
        {
            foreach (string id in ids ?? Array.Empty<string>()) RawIds[id] = 0;
        }

        /// <summary>Runs every posted piece of work. Poll thread only.</summary>
        internal static void Drain(InputManager engine)
        {
            // A plain write: Running reads it whole (Interlocked.Read), so
            // the pass needs no locked exchange for it.
            System.Threading.Volatile.Write(ref _lastLook, Environment.TickCount64);
            while (Queue.TryDequeue(out var work)) work(engine);
        }

        /// <summary>Has the poll thread run <paramref name="work"/>, and
        /// gives its answer, or <paramref name="none"/> when the engine is
        /// stopped or does not answer in time.</summary>
        internal static async System.Threading.Tasks.Task<T> Post<T>(Func<InputManager, T> work, T none)
        {
            if (!Running) return none;
            var done = new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            // Work the poll thread reaches only after the wait, say after the
            // engine stopped and started again, is dropped, since its caller
            // already took it as not done.
            long deadline = Environment.TickCount64 + (long)Wait.TotalMilliseconds;
            Queue.Enqueue(engine =>
            {
                if (Environment.TickCount64 > deadline)
                {
                    done.TrySetResult(none);
                    return;
                }
                try { done.TrySetResult(work(engine)); }
                catch (Exception ex)
                {
                    SdlDiagLog.WriteLine("MAPPINGS controller database work failed: " + ex.Message);
                    done.TrySetResult(none);
                }
            });
            var first = await System.Threading.Tasks.Task.WhenAny(done.Task, System.Threading.Tasks.Task.Delay(Wait)).ConfigureAwait(true);
            return first == done.Task ? done.Task.Result : none;
        }

        // ─────────────────────────────────────────────
        //  SDL's mapping text, which SDL3Minimal does not carry
        // ─────────────────────────────────────────────

        [System.Runtime.InteropServices.DllImport("SDL3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl,
            EntryPoint = "SDL_GetGamepadMappingForID")]
        private static extern IntPtr SDL_GetGamepadMappingForID(uint instanceId);

        [System.Runtime.InteropServices.DllImport("SDL3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl,
            EntryPoint = "SDL_GetGamepadMappings")]
        private static extern IntPtr SDL_GetGamepadMappings(out int count);

        /// <summary>The mapping SDL holds for a joystick it lists, as
        /// "ID,name,bindings", or null for none. Poll thread only.</summary>
        internal static string MappingForId(uint instanceId)
        {
            IntPtr text = SDL_GetGamepadMappingForID(instanceId);
            if (text == IntPtr.Zero) return null;
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text); }
            finally { SDL_free(text); }
        }

        [System.Runtime.InteropServices.DllImport("SDL3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl,
            EntryPoint = "SDL_StringToGUID")]
        private static extern SDL_GUID SDL_StringToGUID([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string text);

        [System.Runtime.InteropServices.DllImport("SDL3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl,
            EntryPoint = "SDL_GetGamepadMappingForGUID")]
        private static extern IntPtr SDL_GetGamepadMappingForGUID(SDL_GUID guid);

        /// <summary>The mapping SDL holds for an ID, with no device needed, as
        /// "ID,name,bindings", or an empty text when SDL has none. Poll
        /// thread, or as SDL starts.</summary>
        internal static string MappingForGuid(string guid)
        {
            IntPtr text = SDL_GetGamepadMappingForGUID(SDL_StringToGUID(guid));
            if (text == IntPtr.Zero) return string.Empty;
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text) ?? string.Empty; }
            finally { SDL_free(text); }
        }

        /// <summary>Raised when SDL told what it held for the IDs of rows of
        /// the user's added while the engine was stopped, so the settings are
        /// saved with it.</summary>
        internal static event Action OldRowsLearned;

        /// <summary>
        /// As SDL starts, before the user's rows are given to it: what SDL
        /// holds for the ID of each row of the user's whose earlier mapping is
        /// not known, because the row was added while the engine was stopped.
        /// Remove Mine gives that mapping back, or reads the device raw when
        /// there was none.
        /// </summary>
        internal static void LearnOldRows()
        {
            bool learned = false;
            ControllerDatabaseMine.Change(mine =>
            {
                ControllerDatabaseMine next = mine;
                foreach (var row in mine.Rows)
                {
                    if (row.ForPadForgeDevice || row.OldRow != null) continue;
                    next = next.With(row with { OldRow = MappingForGuid(row.Id) });
                    learned = true;
                }
                return next;
            });
            if (learned) OldRowsLearned?.Invoke();
        }

        /// <summary>Every mapping SDL holds, each as "ID,name,bindings".
        /// SDL returns one block, freed once. Poll thread only.</summary>
        internal static List<string> AllMappings()
        {
            var list = new List<string>();
            IntPtr block = SDL_GetGamepadMappings(out int count);
            if (block == IntPtr.Zero) return list;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr text = System.Runtime.InteropServices.Marshal.ReadIntPtr(block, i * IntPtr.Size);
                    if (text == IntPtr.Zero) break;
                    list.Add(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text));
                }
            }
            finally { SDL_free(block); }
            return list;
        }
    }
}
