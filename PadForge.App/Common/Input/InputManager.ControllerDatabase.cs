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
                // A row of the user's for the device stands (#600), and a
                // downloaded row under the same ID would replace it in SDL.
                if (ControllerDatabaseMine.Current.ForSdlDevice(guid) != null) return null;

                var answers = new ControllerDatabaseUse.SdlAnswers(guid, SDL_IsGamepad(instanceId), SDL_GetJoystickTypeForID(instanceId),
                    BlissBoxApi.ReadsRaw(SDL_GetJoystickVendorForID(instanceId), SDL_GetJoystickProductForID(instanceId))
                    || ControllerDatabaseEngine.ReadsRaw(guid));
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

            var reopened = new List<Guid>();
            foreach (var (ud, old, mappingBefore) in listed)
            {
                uint id = old.SdlInstanceId;
                bool wants = SdlDeviceWrapper.OpensAsGamepad(SDL_IsGamepad(id) && !ControllerDatabaseEngine.ReadsRaw(GetJoystickGUIDStringForID(id)),
                    old.VendorId, old.ProductId);
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
            // Only when SDL reads it through this row: a row of
            // SDL_GAMECONTROLLERCONFIG for the device stands above it.
            string layout = ControllerDatabaseSource.LayoutOf(ControllerDatabaseEngine.MappingForId(wrapper.SdlInstanceId));
            if (!string.Equals(layout, row.Row.Layout, StringComparison.Ordinal)) return;
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

        /// <summary>The joysticks read raw for the rest of the session, by
        /// the ID of the user's row that was removed: a device's own ID, or a
        /// row's for every device it maps (<see cref="SdlMappingRow.Maps"/>).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RawIds =
            new(StringComparer.OrdinalIgnoreCase);

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
        }

        /// <summary>Whether the engine runs, so posted work is done.</summary>
        internal static bool Running => Environment.TickCount64 - System.Threading.Interlocked.Read(ref _lastLook) < StaleMs;

        /// <summary>Whether PadForge reads the joystick of this ID raw for the
        /// rest of the session.</summary>
        internal static bool ReadsRaw(string deviceGuid)
        {
            if (deviceGuid == null || RawIds.IsEmpty) return false;
            foreach (string id in RawIds.Keys)
                if (string.Equals(id, deviceGuid, StringComparison.OrdinalIgnoreCase) || SdlMappingRow.Maps(id, deviceGuid)) return true;
            return false;
        }

        /// <summary>Reads the joysticks a removed row of the user's mapped
        /// raw for the rest of the session, or, for a row given again, lets
        /// SDL's mapping decide again for every device it maps.</summary>
        internal static void SetReadsRaw(string rowId, bool raw)
        {
            if (string.IsNullOrEmpty(rowId)) return;
            if (raw)
            {
                RawIds[rowId] = 0;
                return;
            }
            foreach (string id in RawIds.Keys)
                if (string.Equals(id, rowId, StringComparison.OrdinalIgnoreCase) || SdlMappingRow.Maps(rowId, id) || SdlMappingRow.Maps(id, rowId))
                    RawIds.TryRemove(id, out _);
        }

        /// <summary>Runs every posted piece of work. Poll thread only.</summary>
        internal static void Drain(InputManager engine)
        {
            System.Threading.Interlocked.Exchange(ref _lastLook, Environment.TickCount64);
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
