using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using SDL3;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 1: UpdateDevices
        //  Enumerates SDL joystick, keyboard, and mouse devices,
        //  opens newly connected devices, marks disconnected devices as offline.
        //
        //  All controllers (including Xbox/XInput) are handled via SDL3.
        //  PadForge's own virtual controllers (HIDMaestro in v3, plus any
        //  v2 ViGEm residue) are detected and filtered out.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Set of SDL instance IDs that we have already opened (joysticks).
        /// Used to detect new vs. already-known devices.
        /// SDL3: instance IDs are uint (0 = invalid).
        /// </summary>
        private readonly HashSet<uint> _openedSdlInstanceIds = new HashSet<uint>();

        /// <summary>
        /// First-observed tick (UTC) per SDL instance ID for which the
        /// device has either vanished from SDL_GetJoysticks or reported
        /// IsAttached=false. Used by Phase 2 to debounce transient drops —
        /// xinputhid's slot-assignment pass during virtual creation can
        /// briefly make a physical controller look disconnected on one
        /// poll cycle. Calling MarkDeviceOffline on that first cycle nulls
        /// out ud.Device and freezes the Devices-page preview (S2 violation).
        /// We only mark offline after the device has been missing for the
        /// full debounce window.
        /// </summary>
        private readonly Dictionary<uint, DateTime> _sdlDisconnectCandidateSince = new();

        /// <summary>Debounce window in ms before a transient SDL drop is treated
        /// as a real disconnect. Chosen to be longer than the worst-case
        /// xinputhid reshuffle that a HIDMaestro virtual creation can induce
        /// on a coexisting physical Xbox (observed up to a few hundred ms on
        /// a BT-paired Series controller), short enough that a real unplug
        /// / pair-disconnect still surfaces to the UI quickly.</summary>
        private const int SdlDisconnectDebounceMs = 2000;

        // Keyboard/mouse tracking moved to _openedKeyboardHandles / _openedMouseHandles
        // (Raw Input IntPtr handles instead of SDL uint IDs).

        // ── Async Raw Input enumeration ──
        // Raw Input keyboard/mouse enumeration is expensive (CreateFile +
        // HidD_GetAttributes + registry per device). Running it off the
        // polling thread eliminates the ~2-5ms spike every 2 seconds.
        private volatile bool _rawInputEnumPending;
        private volatile bool _rawInputEnumRunning;
        private RawInputListener.DeviceInfo[] _cachedKeyboards;
        private RawInputListener.DeviceInfo[] _cachedMice;
        private RawInputListener.DeviceInfo[] _cachedConsumerControls;
        private readonly object _rawInputCacheLock = new object();

        /// <summary>
        /// Step 1: Enumerate all connected SDL joystick devices.
        ///
        /// SDL3 change: uses SDL_GetJoysticks() returning an array of instance IDs
        /// instead of SDL_NumJoysticks() + device-index-based enumeration.
        ///
        /// For each device found by SDL:
        ///   - If not yet opened: open it, create/update a UserDevice record, mark online
        ///   - If already opened: verify it's still attached
        ///
        /// For each previously opened device not found in current enumeration:
        ///   - Mark offline, close SDL handle
        ///
        /// Fires <see cref="DevicesUpdated"/> if the device list changed.
        /// </summary>
        private void UpdateDevices()
        {
            if (!_sdlInitialized)
                return;

            // No wait for App.OrphanSweepTask — the SDL3 fork filters
            // HIDMaestro HIDs out of enumeration whether or not the prior
            // session's kernel cleanup has finished, so we can enumerate
            // immediately. Blocking the polling thread on the sweep was
            // what pinned startup at 90+ seconds when stale virtuals
            // lingered from a force-killed previous session.

            bool changed = false;

            // SDL3: Get array of instance IDs for all connected joysticks.
            uint[] joystickIds = SDL_GetJoysticks();

            // Build a set of instance IDs currently visible to SDL.
            var currentInstanceIds = new HashSet<uint>(joystickIds);

            // --- Phase 1: Open newly connected devices ---
            foreach (uint instanceId in joystickIds)
            {
                try
                {
                    // Skip devices we already have open (by SDL instance ID).
                    // This is more reliable than GUID matching because serial-based
                    // GUIDs aren't available until after the device is opened.
                    if (_openedSdlInstanceIds.Contains(instanceId))
                        continue;

                    // Open the device by instance ID. The SDL3 fork already
                    // dropped HIDMaestro HIDs from hid_enumerate and any HM-
                    // only XInput slot from SDL_XINPUT_JoystickDetect, so
                    // every instance ID that reaches here is a real device.
                    var wrapper = new SdlDeviceWrapper();
                    if (!wrapper.Open(instanceId))
                    {
                        wrapper.Dispose();
                        continue;
                    }

                    Debug.WriteLine($"[Step1] Accepted device: SDL#{instanceId} VID={wrapper.VendorId:X4} PID={wrapper.ProductId:X4} path={wrapper.DevicePath} name={wrapper.Name}");
                    Engine.SdlDiagLog.WriteLine($"DEV + SDL#{instanceId} {wrapper.VendorId:X4}:{wrapper.ProductId:X4} {wrapper.Name}");

                    UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid, wrapper.ProductGuid);

                    // Populate from the SDL device.
                    ud.LoadFromSdlDevice(wrapper);
                    ud.IsOnline = true;

                    // Track the SDL instance ID.
                    _openedSdlInstanceIds.Add(wrapper.SdlInstanceId);

                    changed = true;
                }
                catch (Exception ex)
                {
                    RaiseError($"Error opening device (instance {instanceId})", ex);
                }
            }

            // --- Phase 1b/1c: Consume cached keyboard/mouse results ---
            // Raw Input enumeration runs on a background thread to avoid
            // blocking the polling loop with expensive CreateFile/HID I/O.
            // On the first cycle, run synchronously so devices are available
            // immediately at startup.
            if (_cachedKeyboards == null)
            {
                // First call runs synchronous so devices are ready before Step 2.
                _cachedKeyboards = RawInputListener.EnumerateKeyboards();
                _cachedMice = RawInputListener.EnumerateMice();
                _cachedConsumerControls = RawInputListener.EnumerateConsumerControls();
                _rawInputEnumPending = true;
            }

            if (_rawInputEnumPending)
            {
                RawInputListener.DeviceInfo[] keyboards, mice, consumers;
                lock (_rawInputCacheLock)
                {
                    keyboards = _cachedKeyboards;
                    mice = _cachedMice;
                    consumers = _cachedConsumerControls;
                    _rawInputEnumPending = false;
                }

                changed |= EnumerateKeyboards(keyboards);
                changed |= EnumerateMice(mice);
                changed |= EnumerateConsumerControls(consumers);
                changed |= DetectDisconnectedHandles(_openedKeyboardHandles, keyboards);
                changed |= DetectDisconnectedHandles(_openedMouseHandles, mice);
                changed |= DetectDisconnectedHandles(_openedConsumerHandles, consumers);
            }

            // Kick off the next async enumeration so results are ready
            // by the time the next 2-second UpdateDevices cycle runs.
            if (!_rawInputEnumRunning)
            {
                _rawInputEnumRunning = true;
                Task.Run(() =>
                {
                    try
                    {
                        var kb = RawInputListener.EnumerateKeyboards();
                        var ms = RawInputListener.EnumerateMice();
                        var cc = RawInputListener.EnumerateConsumerControls();
                        lock (_rawInputCacheLock)
                        {
                            _cachedKeyboards = kb;
                            _cachedMice = ms;
                            _cachedConsumerControls = cc;
                            _rawInputEnumPending = true;
                        }
                    }
                    catch { /* best effort — next cycle will retry */ }
                    finally { _rawInputEnumRunning = false; }
                });
            }

            // --- Phase 1d: Precision Touchpads (per-hardware device) ---
            if (_ptpReader != null && _ptpReader.IsAvailable)
            {
                var ptpDevices = _ptpReader.GetDevices();
                var currentPtpHandles = new HashSet<IntPtr>();

                foreach (var (handle, name, path, vid, pid) in ptpDevices)
                {
                    currentPtpHandles.Add(handle);
                    var guid = SdlDeviceWrapper.BuildInstanceGuid(path, vid, pid, 0);

                    // If the user removed this device from the Devices page,
                    // the handle is still tracked but the UserDevice is gone.
                    // Reset tracking so it gets recreated.
                    if (_openedPtpHandles.Contains(handle) &&
                        FindOnlineDeviceByInstanceGuid(guid) == null)
                    {
                        _openedPtpHandles.Remove(handle);
                    }

                    if (!_openedPtpHandles.Contains(handle))
                    {
                        UserDevice ud = FindOrCreateUserDevice(guid);
                        ud.LoadInstance(guid, name, guid, name);
                        ud.LoadCapabilities(0, 0, 0, InputDeviceType.Touchpad);
                        ud.DevicePath = path;
                        ud.VendorId = vid;
                        ud.ProdId = pid;
                        ud.IsOnline = true;
                        ud.HasTouchpad = true;
                        _openedPtpHandles.Add(handle);
                        _ptpHandleToGuid[handle] = guid;
                        changed = true;
                    }
                }

                // Detect disconnected PTP devices.
                var disconnected = new List<IntPtr>();
                foreach (var h in _openedPtpHandles)
                {
                    if (!currentPtpHandles.Contains(h))
                    {
                        if (_ptpHandleToGuid.TryGetValue(h, out var guid))
                        {
                            var ud = FindOnlineDeviceByInstanceGuid(guid);
                            if (ud != null) ud.IsOnline = false;
                            _ptpHandleToGuid.Remove(h);
                        }
                        disconnected.Add(h);
                        changed = true;
                    }
                }
                foreach (var h in disconnected)
                    _openedPtpHandles.Remove(h);

                // "All Touchpads (Merged)" aggregate device — always present when PTP is available.
                // Reset flag if the user removed the merged device from the Devices page.
                if (_ptpMergedCreated && FindOnlineDeviceByInstanceGuid(PtpMergedGuid) == null)
                    _ptpMergedCreated = false;

                if (!_ptpMergedCreated)
                {
                    UserDevice mergedUd = FindOrCreateUserDevice(PtpMergedGuid);
                    mergedUd.LoadInstance(PtpMergedGuid,
                        Strings.Instance.Devices_AllTouchpadsMerged,
                        PtpMergedGuid,
                        Strings.Instance.Devices_AllTouchpadsMerged);
                    mergedUd.LoadCapabilities(0, 0, 0, InputDeviceType.Touchpad);
                    mergedUd.DevicePath = "aggregate://touchpads";
                    mergedUd.IsOnline = true;
                    mergedUd.HasTouchpad = true;
                    _ptpMergedCreated = true;
                    changed = true;
                }
                // PTP claims the digitizer collection, which causes Windows to
                // send synthetic mouse WM_INPUT with hDevice=0 instead of the
                // original per-device handle. Redirect all mouse wrappers that
                // share hardware with a PTP device to IntPtr.Zero.
                // Only redirect mice that share hardware with a PTP device
                // (same VID/PID = same physical chip, different HID collection).
                // Retry each cycle until at least one redirect succeeds, since
                // PTP device VID/PID isn't known until first touchpad contact.
                if (!_ptpMouseRedirected && ptpDevices.Length > 0)
                {
                    var ptpVidPids = new HashSet<(ushort, ushort)>();
                    foreach (var (_, _, _, vid, pid) in ptpDevices)
                    {
                        if (vid != 0 || pid != 0)
                            ptpVidPids.Add((vid, pid));
                    }

                    if (ptpVidPids.Count > 0)
                    {
                        var devices = SettingsManager.UserDevices;
                        if (devices != null)
                        {
                            lock (devices.SyncRoot)
                            {
                                foreach (var ud in devices.Items)
                                {
                                    if (ud.IsOnline && ud.Device is SdlMouseWrapper mw &&
                                        mw.RawInputHandle != IntPtr.Zero &&
                                        mw.RawInputHandle != RawInputListener.AggregateMouseHandle &&
                                        ptpVidPids.Contains((ud.VendorId, ud.ProdId)))
                                    {
                                        mw.UpdateHandle(IntPtr.Zero);
                                        _ptpMouseRedirected = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else if (_ptpMergedCreated)
            {
                var mergedUd = FindOnlineDeviceByInstanceGuid(PtpMergedGuid);
                if (mergedUd != null) mergedUd.IsOnline = false;
                _ptpMergedCreated = false;
                changed = true;
            }

            // --- Phase 1e: MIDI input endpoints (issue #128) ---
            changed |= UpdateMidiInputDevices();

            // --- Phase 1f: NFC PC/SC readers (issue #150) ---
            changed |= UpdateNfcReaderDevices();

            // --- Phase 2: Detect disconnected SDL devices (debounced) ---
            //
            // Signals that indicate the device might be gone:
            //   (a) The SdlDeviceWrapper handle is null.
            //   (b) ud.Device.IsAttached returns false.
            //   (c) sdlId is no longer in SDL_GetJoysticks().
            //
            // (c) is the belt-and-suspenders for "SDL keeps a stale
            // JoystickID after the kernel device is gone" (HIDMaestro#11).
            //
            // S2 debounce: any one of these signals starts a countdown
            // (SdlDisconnectDebounceMs). The device is only marked offline
            // if the condition persists for the full window. This rides out
            // the xinputhid transients that occur during a HIDMaestro
            // virtual's kernel creation — those typically resolve within
            // tens to low hundreds of ms, far under the debounce window —
            // so a coexisting physical Xbox's SDL handle is preserved and
            // its Devices-page preview keeps moving. A real disconnect
            // (unplug, BT pair-drop) stays missing past the window and
            // surfaces as an offline event with only the debounce latency
            // of delay.
            var disconnectedIds = new List<uint>();
            var nowUtc = DateTime.UtcNow;

            foreach (uint sdlId in _openedSdlInstanceIds)
            {
                UserDevice ud = FindOnlineDeviceBySdlInstanceId(sdlId);
                if (ud == null)
                {
                    // Not found ONLINE. Two cases:
                    //  - Step 2 already flipped the device offline (its
                    //    GetCurrentState returned null when SDL reported the
                    //    handle detached). The UserDevice still holds the dead
                    //    SDL handle and none of the disconnect cleanup has run.
                    //    Without this, MarkDeviceOffline became unreachable for
                    //    real SDL unplugs the moment the detached-read guard
                    //    shipped: the handle leaked, the wheel-replug writer
                    //    resets never ran, and the per-slot output
                    //    neutralization never happened. Finish the disconnect
                    //    here — detachment is permanent for a handle, so no
                    //    debounce applies.
                    //  - The UserDevice itself is gone — nothing to clean.
                    var offlineUd = FindDeviceBySdlInstanceIdAnyState(sdlId);
                    if (offlineUd != null && offlineUd.Device != null)
                    {
                        MarkDeviceOffline(offlineUd);
                        changed = true;
                    }
                    disconnectedIds.Add(sdlId);
                    _sdlDisconnectCandidateSince.Remove(sdlId);
                    continue;
                }

                bool inCurrentEnum = currentInstanceIds.Contains(sdlId);
                bool looksDisconnected =
                    ud.Device == null
                    || !ud.Device.IsAttached
                    || !inCurrentEnum;

                if (!looksDisconnected)
                {
                    // Healthy. Clear any pending debounce for this SDL ID.
                    _sdlDisconnectCandidateSince.Remove(sdlId);
                    continue;
                }

                // Start / continue the debounce window.
                if (!_sdlDisconnectCandidateSince.TryGetValue(sdlId, out var firstSeen))
                {
                    _sdlDisconnectCandidateSince[sdlId] = nowUtc;
                    continue;
                }

                if ((nowUtc - firstSeen).TotalMilliseconds < SdlDisconnectDebounceMs)
                {
                    continue;
                }

                // Debounce window elapsed. Real disconnect.
                MarkDeviceOffline(ud);
                disconnectedIds.Add(sdlId);
                _sdlDisconnectCandidateSince.Remove(sdlId);
                changed = true;
            }

            // Clean up tracking for disconnected devices.
            foreach (uint sdlId in disconnectedIds)
            {
                _openedSdlInstanceIds.Remove(sdlId);
            }

            // --- Notify if anything changed ---
            if (changed)
            {
                DevicesUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        // ─────────────────────────────────────────────
        //  UserDevice lookup helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds a UserDevice by its instance GUID.
        /// Uses a manual loop to avoid LINQ closure allocations in the hot path.
        /// </summary>
        private UserDevice FindOnlineDeviceByInstanceGuid(Guid instanceGuid)
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return null;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].InstanceGuid == instanceGuid)
                        return devices[i];
                }
                return null;
            }
        }

        /// <summary>
        /// Finds an online UserDevice by its SDL instance ID.
        /// Uses a manual loop to avoid LINQ closure allocations.
        /// </summary>
        private UserDevice FindOnlineDeviceBySdlInstanceId(uint sdlInstanceId)
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return null;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    if (d.IsOnline && d.Device != null && d.Device.SdlInstanceId == sdlInstanceId)
                        return d;
                }
                return null;
            }
        }

        /// <summary>
        /// Like <see cref="FindOnlineDeviceBySdlInstanceId"/> but without the
        /// IsOnline filter. Used by the disconnect sweep to finish cleanup for
        /// a device Step 2 already flipped offline (detached-handle read): the
        /// UserDevice still holds the dead SDL wrapper that MarkDeviceOffline
        /// must dispose.
        /// </summary>
        private UserDevice FindDeviceBySdlInstanceIdAnyState(uint sdlInstanceId)
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return null;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    if (d.Device != null && d.Device.SdlInstanceId == sdlInstanceId)
                        return d;
                }
                return null;
            }
        }

        /// <summary>
        /// Finds an existing UserDevice by instance GUID, with fallback matching
        /// by ProductGuid for devices whose InstanceGuid changed (e.g. Bluetooth
        /// controllers that get a different device path after reboot).
        /// When a fallback match is found, migrates the old device and its
        /// UserSetting to the new InstanceGuid.
        /// </summary>
        private UserDevice FindOrCreateUserDevice(Guid instanceGuid, Guid productGuid = default)
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return new UserDevice();

            lock (devices.SyncRoot)
            {
                // 1. Exact match by InstanceGuid.
                for (int i = 0; i < devices.Items.Count; i++)
                {
                    if (devices.Items[i].InstanceGuid == instanceGuid)
                        return devices.Items[i];
                }

                // 2. Fallback: find an offline device with the same ProductGuid.
                //    This handles BT controllers that reconnect with a new device path.
                if (productGuid != Guid.Empty)
                {
                    UserDevice fallback = null;
                    for (int i = 0; i < devices.Items.Count; i++)
                    {
                        var d = devices.Items[i];
                        if (!d.IsOnline && d.ProductGuid == productGuid)
                        {
                            fallback = d;
                            break;
                        }
                    }

                    if (fallback != null)
                    {
                        // Migrate the device to its new InstanceGuid.
                        Guid oldGuid = fallback.InstanceGuid;
                        fallback.InstanceGuid = instanceGuid;

                        // Also migrate the linked UserSetting so slot assignment
                        // and PadSetting are preserved.
                        MigrateUserSettingGuid(oldGuid, instanceGuid);

                        return fallback;
                    }
                }

                // 3. No match — create a new device.
                var ud = new UserDevice { InstanceGuid = instanceGuid };
                devices.Items.Add(ud);
                return ud;
            }
        }

        /// <summary>
        /// Updates a UserSetting's InstanceGuid when the physical device's
        /// identity changes (e.g. Bluetooth reconnect with different path).
        /// </summary>
        private static void MigrateUserSettingGuid(Guid oldGuid, Guid newGuid)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            lock (settings.SyncRoot)
            {
                for (int i = 0; i < settings.Items.Count; i++)
                {
                    if (settings.Items[i].InstanceGuid == oldGuid)
                    {
                        settings.Items[i].InstanceGuid = newGuid;
                        break; // One UserSetting per device.
                    }
                }
            }
        }

        /// <summary>
        /// Marks a device as offline, disposes its SDL handle, and clears runtime state.
        /// </summary>
        private void MarkDeviceOffline(UserDevice ud)
        {
            if (ud == null) return;

            Engine.SdlDiagLog.WriteLine($"DEV - {ud.InstanceName}");

            // Stop rumble before closing.
            if (ud.ForceFeedbackState != null && ud.Device != null)
            {
                try { ud.ForceFeedbackState.StopDeviceForces(ud.Device); }
                catch { /* best effort */ }
            }

            // Dispose SDL handle.
            if (ud.Device != null)
            {
                try { ud.Device.Dispose(); }
                catch { /* best effort */ }
            }

            // Clear native-wheel per-device state so a same-path replug re-applies the
            // rotation range + auto-center disable (the wheel firmware power-cycles to its
            // default centering spring on unplug) and the FFB state machines re-arm instead
            // of refreshing/updating a slot the firmware reset to empty.
            if (!string.IsNullOrEmpty(ud.DevicePath))
            {
                _appliedWheelSettings.TryRemove(ud.DevicePath, out _);
                _appliedLeds.TryRemove(ud.DevicePath, out _);
                _appliedWheelFfb.TryRemove(ud.DevicePath, out _);
                LogitechRawHidWriter.ResetDevice(ud.DevicePath);
                ThrustmasterRawHidWriter.ResetDevice(ud.DevicePath);
                RawHidOutput.ResetDevice(ud.DevicePath);
            }

            ud.ClearRuntimeState();

            // Neutralize the device's per-slot mapped outputs. Step 3 skips
            // offline devices and "keeps the last OutputState" (a guard against
            // transient read glitches), so whatever was stamped on the final
            // frames before this confirmed disconnect would otherwise persist
            // for as long as the slot stays active: a detached pedal's
            // recentered read (inverted trigger -> ~32767 = 50% engaged), or a
            // button/pedal the user was holding at unplug. Step 4 copies
            // OutputState into the slot's combined output and the per-device
            // Triggers/Sticks preview reads RawMappedState, so both must go
            // neutral (Gamepad default: triggers released, sticks centered).
            var allSettings = SettingsManager.UserSettings;
            if (allSettings != null)
            {
                lock (allSettings.SyncRoot)
                {
                    foreach (var us in allSettings.Items)
                    {
                        if (us == null || us.InstanceGuid != ud.InstanceGuid) continue;
                        us.OutputState = default;
                        us.RawMappedState = default;
                    }
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Keyboard / Mouse enumeration
        // ─────────────────────────────────────────────

        /// <summary>
        /// Tracked Raw Input keyboard device handles.
        /// </summary>
        private readonly HashSet<IntPtr> _openedKeyboardHandles = new HashSet<IntPtr>();

        /// <summary>Tracked PTP device handles.</summary>
        private readonly HashSet<IntPtr> _openedPtpHandles = new();
        private readonly Dictionary<IntPtr, Guid> _ptpHandleToGuid = new();

        /// <summary>Fixed GUID for the merged touchpad aggregate device.</summary>
        private static readonly Guid PtpMergedGuid = new("50545000-ffff-ffff-5054-505450505450");
        private bool _ptpMergedCreated;
        private bool _ptpMouseRedirected;

        /// <summary>
        /// Tracked Raw Input mouse device handles.
        /// </summary>
        private readonly HashSet<IntPtr> _openedMouseHandles = new HashSet<IntPtr>();

        /// <summary>
        /// Processes pre-fetched keyboard device info and creates UserDevice
        /// records for any new keyboards. Returns true if a new keyboard was found.
        /// </summary>
        private bool EnumerateKeyboards(RawInputListener.DeviceInfo[] keyboards)
        {
            // Prune tracked handles whose UserDevice was removed (e.g. via UI "Remove").
            PruneOrphanedHandles(_openedKeyboardHandles);

            bool changed = false;

            foreach (var kb in keyboards)
            {
                if (_openedKeyboardHandles.Contains(kb.Handle))
                    continue;

                SdlKeyboardWrapper wrapper = null;
                try
                {
                    wrapper = new SdlKeyboardWrapper();
                    if (!wrapper.Open(kb))
                        continue;

                    UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid);
                    ud.LoadFromKeyboardDevice(wrapper);
                    ud.IsOnline = true;

                    _openedKeyboardHandles.Add(kb.Handle);
                    wrapper = null; // ownership transferred to UserDevice
                    changed = true;
                }
                catch (Exception ex)
                {
                    RaiseError($"Error opening keyboard ({kb.Name})", ex);
                }
                finally
                {
                    wrapper?.Dispose();
                }
            }

            return changed;
        }

        private readonly HashSet<IntPtr> _openedConsumerHandles = new HashSet<IntPtr>();

        /// <summary>
        /// Processes pre-fetched Consumer Control device info (issue #168) and
        /// creates UserDevice records for any new collections. Mirrors
        /// <see cref="EnumerateKeyboards"/>. Returns true if a new device was found.
        /// </summary>
        private bool EnumerateConsumerControls(RawInputListener.DeviceInfo[] consumers)
        {
            // Prune tracked handles whose UserDevice was removed (e.g. via UI "Remove").
            PruneOrphanedHandles(_openedConsumerHandles);

            bool changed = false;

            foreach (var cc in consumers)
            {
                if (_openedConsumerHandles.Contains(cc.Handle))
                    continue;

                ConsumerControlWrapper wrapper = null;
                try
                {
                    wrapper = new ConsumerControlWrapper();
                    if (!wrapper.Open(cc))
                        continue;

                    UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid);
                    ud.LoadFromConsumerDevice(wrapper);
                    ud.IsOnline = true;

                    _openedConsumerHandles.Add(cc.Handle);
                    wrapper = null; // ownership transferred to UserDevice
                    changed = true;
                }
                catch (Exception ex)
                {
                    RaiseError($"Error opening consumer control ({cc.Name})", ex);
                }
                finally
                {
                    wrapper?.Dispose();
                }
            }

            return changed;
        }

        /// <summary>
        /// Processes pre-fetched mouse device info and creates UserDevice
        /// records for any new mice. Returns true if a new mouse was found.
        /// </summary>
        private bool EnumerateMice(RawInputListener.DeviceInfo[] mice)
        {
            // Prune tracked handles whose UserDevice was removed (e.g. via UI "Remove").
            PruneOrphanedHandles(_openedMouseHandles);

            bool changed = false;

            foreach (var mouse in mice)
            {
                if (_openedMouseHandles.Contains(mouse.Handle))
                    continue;

                // Skip if an existing device with the same path is already tracked
                // (possibly redirected to IntPtr.Zero by PTP). Don't re-create it.
                if (!string.IsNullOrEmpty(mouse.DevicePath))
                {
                    var existingUd = FindOnlineDeviceByDevicePath(mouse.DevicePath);
                    if (existingUd != null)
                        continue;
                }

                SdlMouseWrapper wrapper = null;
                try
                {
                    wrapper = new SdlMouseWrapper();
                    if (!wrapper.Open(mouse))
                        continue;

                    UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid);
                    ud.LoadFromMouseDevice(wrapper);
                    ud.IsOnline = true;

                    _openedMouseHandles.Add(mouse.Handle);
                    wrapper = null; // ownership transferred to UserDevice
                    changed = true;
                }
                catch (Exception ex)
                {
                    RaiseError($"Error opening mouse ({mouse.Name})", ex);
                }
                finally
                {
                    wrapper?.Dispose();
                }
            }

            return changed;
        }

        // MIDI input endpoints (Phase 1e, issue #128). The WinRT device
        // query runs on a background task; the polling thread consumes the
        // latest cached snapshot, mirroring the Raw Input keyboard/mouse
        // enumeration above.
        private readonly Dictionary<string, MidiInputDevice> _openedMidiInputs =
            new Dictionary<string, MidiInputDevice>(StringComparer.OrdinalIgnoreCase);
        private readonly object _midiInputsLock = new object();
        private volatile List<(string Id, string Name)> _cachedMidiEndpoints;
        private volatile bool _midiEnumRunning;
        private volatile bool _midiInputsSuppressed;

        // NFC PC/SC readers (Phase 1f, issue #150). The monitor service owns
        // the PC/SC context + its own event thread; this sweep just mirrors
        // the visible reader set into UserDevices, like the MIDI sweep above.
        private readonly Dictionary<string, NfcReaderDevice> _openedNfcReaders =
            new Dictionary<string, NfcReaderDevice>(StringComparer.OrdinalIgnoreCase);
        private readonly object _nfcReadersLock = new object();
        // Retry throttle for starting the NFC monitor. Not a permanent latch:
        // if the Smart Card service is down at launch, Start() fails, and we
        // retry on a WALL-CLOCK interval so a service/reader that appears later
        // in the session is still picked up (self-healing like the MIDI sweep).
        // Wall-clock, not a poll counter: the device sweep runs at the
        // enumeration cadence (~2 s), not the pipeline rate, so a poll-count
        // throttle sized for 60 Hz would stretch a "5 s" retry to ~10 minutes.
        private long _nfcNextStartTicks; // Environment.TickCount64 of the next allowed Start attempt
        // Set true by ShutdownNfcReaders so a poll thread still inside Phase 1f
        // after the engine stop (the InputManager.Stop join can time out) cannot
        // re-Start a fresh monitor + context after teardown. Mirrors the MIDI
        // _midiInputsSuppressed latch.
        private volatile bool _nfcInputsSuppressed;
        private const int _nfcStartRetryMs = 5000;

        /// <summary>
        /// Tears down every open MIDI input connection and the shared input
        /// session, and suppresses Phase 1e until app restart. Called before
        /// uninstalling Windows MIDI Services so no in-process runtime
        /// objects are alive during the uninstall.
        /// </summary>
        public void ShutdownMidiInputs()
        {
            _midiInputsSuppressed = true;
            _cachedMidiEndpoints = null;
            lock (_midiInputsLock)
            {
                foreach (var kvp in _openedMidiInputs)
                {
                    var ud = FindOnlineDeviceByInstanceGuid(kvp.Value.InstanceGuid);
                    if (ud != null)
                    {
                        ud.IsOnline = false;
                        ud.Device = null;
                    }
                    kvp.Value.Dispose();
                }
                _openedMidiInputs.Clear();
            }
            MidiInputRuntime.Shutdown();
        }

        /// <summary>
        /// Phase 1e: registers MIDI input endpoints as input devices and
        /// marks vanished ones offline. PadForge's own MIDI virtual
        /// controller endpoints are deliberately included — assigning one
        /// as an input to another slot is the no-hardware loopback path.
        /// </summary>
        private bool UpdateMidiInputDevices()
        {
            if (_midiInputsSuppressed)
                return false;

            if (!_midiEnumRunning)
            {
                _midiEnumRunning = true;
                Task.Run(() =>
                {
                    try { _cachedMidiEndpoints = MidiInputRuntime.EnumerateEndpoints(); }
                    catch { }
                    finally { _midiEnumRunning = false; }
                });
            }

            var endpoints = _cachedMidiEndpoints;
            if (endpoints == null)
                return false;

            bool changed = false;
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The lock guards against ShutdownMidiInputs (UI thread, MIDI
            // services uninstall) racing this polling-thread sweep.
            lock (_midiInputsLock)
            {
                // Re-check under the lock: ShutdownMidiInputs may have set
                // the flag (and disposed the session) between the unlocked
                // check above and acquiring the lock. Without this, a stale
                // endpoint snapshot would dev.Open() and lazily recreate the
                // MidiSession that uninstall is about to remove.
                if (_midiInputsSuppressed)
                    return false;

                foreach (var (id, name) in endpoints)
                {
                    current.Add(id);

                    if (_openedMidiInputs.TryGetValue(id, out var existing))
                    {
                        // If the user removed this device from the Devices page,
                        // the connection is still tracked but the UserDevice is
                        // gone. Reset tracking so it gets recreated. (Same
                        // pattern as the PTP phase above.)
                        if (FindOnlineDeviceByInstanceGuid(existing.InstanceGuid) != null)
                            continue;
                        existing.Dispose();
                        _openedMidiInputs.Remove(id);
                    }

                    try
                    {
                        var dev = new MidiInputDevice(id, name);
                        if (!dev.Open())
                        {
                            dev.Dispose();
                            continue;
                        }

                        UserDevice ud = FindOrCreateUserDevice(dev.InstanceGuid, dev.ProductGuid);
                        ud.LoadFromExternalDevice(dev);
                        ud.IsOnline = true;
                        _openedMidiInputs[id] = dev;
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        RaiseError($"Error opening MIDI endpoint '{name}'", ex);
                    }
                }

                // Endpoints that vanished since the last snapshot.
                List<string> gone = null;
                foreach (var kvp in _openedMidiInputs)
                    if (!current.Contains(kvp.Key))
                        (gone ??= new List<string>()).Add(kvp.Key);

                if (gone != null)
                {
                    foreach (var id in gone)
                    {
                        var dev = _openedMidiInputs[id];
                        var ud = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid);
                        if (ud != null)
                        {
                            ud.IsOnline = false;
                            ud.Device = null;
                        }
                        dev.Dispose();
                        _openedMidiInputs.Remove(id);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// Tears down every open NFC reader device and the shared monitor
        /// service. Called on app shutdown alongside ShutdownMidiInputs.
        /// </summary>
        public void ShutdownNfcReaders()
        {
            _nfcInputsSuppressed = true;
            lock (_nfcReadersLock)
            {
                foreach (var kvp in _openedNfcReaders)
                {
                    var ud = FindOnlineDeviceByInstanceGuid(kvp.Value.InstanceGuid);
                    if (ud != null)
                    {
                        ud.IsOnline = false;
                        ud.Device = null;
                    }
                    kvp.Value.Dispose();
                }
                _openedNfcReaders.Clear();
            }
            try { PadForge.Services.NfcReaderService.Active?.Dispose(); } catch { }
        }

        /// <summary>
        /// Phase 1f: registers each visible PC/SC reader as an input device
        /// and marks vanished ones offline, mirroring UpdateMidiInputDevices.
        /// The shared monitor (NfcReaderService) is started once, lazily; when
        /// the Smart Card service is unavailable Start() fails and the monitor
        /// stays absent, but the start is retried periodically so a service or
        /// reader that appears later in the session is still picked up (the MIDI
        /// sweep self-heals the same way).
        /// </summary>
        private bool UpdateNfcReaderDevices()
        {
            // Suppressed after teardown so a late poll cannot resurrect the
            // monitor + context once ShutdownNfcReaders has disposed Active.
            if (_nfcInputsSuppressed)
                return false;

            var svc = PadForge.Services.NfcReaderService.Active;
            if (svc == null)
            {
                // Throttle start attempts (wall-clock) so a missing Smart Card
                // service does not trigger an SCardEstablishContext every sweep,
                // while still retrying every ~5 s regardless of the sweep rate.
                long now = Environment.TickCount64;
                if (now < _nfcNextStartTicks)
                    return false;
                _nfcNextStartTicks = now + _nfcStartRetryMs;
                svc = PadForge.Services.NfcReaderService.Start();
                if (svc == null) return false; // no Smart Card service yet; retry later
            }

            var readers = svc.GetReaders();

            bool changed = false;
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_nfcReadersLock)
            {
                foreach (var reader in readers)
                {
                    current.Add(reader);

                    if (_openedNfcReaders.TryGetValue(reader, out var existing))
                    {
                        // Recreate if the user removed it from the Devices page.
                        if (FindOnlineDeviceByInstanceGuid(existing.InstanceGuid) != null)
                            continue;
                        existing.Dispose();
                        _openedNfcReaders.Remove(reader);
                    }

                    try
                    {
                        var dev = new NfcReaderDevice(reader);
                        if (!dev.Open())
                        {
                            dev.Dispose();
                            continue;
                        }
                        UserDevice ud = FindOrCreateUserDevice(dev.InstanceGuid, dev.ProductGuid);
                        ud.LoadFromExternalDevice(dev);
                        ud.IsOnline = true;
                        _openedNfcReaders[reader] = dev;
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        RaiseError($"Error opening NFC reader '{reader}'", ex);
                    }
                }

                List<string> gone = null;
                foreach (var kvp in _openedNfcReaders)
                    if (!current.Contains(kvp.Key))
                        (gone ??= new List<string>()).Add(kvp.Key);

                if (gone != null)
                {
                    foreach (var reader in gone)
                    {
                        var dev = _openedNfcReaders[reader];
                        var ud = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid);
                        if (ud != null)
                        {
                            ud.IsOnline = false;
                            ud.Device = null;
                        }
                        dev.Dispose();
                        _openedNfcReaders.Remove(reader);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// Detects disconnected keyboards or mice by comparing tracked handles
        /// to current Raw Input device handles. Marks disconnected devices offline
        /// and removes their tracking entries so they can be re-opened on reconnect.
        /// </summary>
        private bool DetectDisconnectedHandles(
            HashSet<IntPtr> trackedHandles, RawInputListener.DeviceInfo[] currentDevices)
        {
            if (trackedHandles.Count == 0)
                return false;

            var currentSet = new HashSet<IntPtr>();
            for (int i = 0; i < currentDevices.Length; i++)
                currentSet.Add(currentDevices[i].Handle);

            var disconnected = new List<IntPtr>();
            var redirected = new List<IntPtr>();
            bool changed = false;

            foreach (IntPtr handle in trackedHandles)
            {
                if (!currentSet.Contains(handle))
                {
                    UserDevice ud = FindOnlineDeviceByHandle(handle);
                    if (ud != null)
                    {
                        // When PTP is active, the trackpad's mouse collection
                        // disappears from GetRawInputDeviceList but synthetic
                        // mouse WM_INPUT still arrives at hDevice=0. Keep the
                        // device online and redirect its wrapper to IntPtr.Zero.
                        if (_ptpReader != null && _ptpReader.IsAvailable &&
                            ud.Device is SdlMouseWrapper mouseWrapper)
                        {
                            mouseWrapper.UpdateHandle(IntPtr.Zero);
                            redirected.Add(handle);
                        }
                        else
                        {
                            MarkDeviceOffline(ud);
                            changed = true;
                            disconnected.Add(handle);
                        }
                    }
                    else
                    {
                        disconnected.Add(handle);
                    }
                }
            }

            foreach (IntPtr handle in disconnected)
                trackedHandles.Remove(handle);

            // Redirected devices: swap old handle for IntPtr.Zero in tracking.
            foreach (IntPtr handle in redirected)
            {
                trackedHandles.Remove(handle);
                trackedHandles.Add(IntPtr.Zero);
            }

            return changed;
        }

        /// <summary>
        /// Removes tracked handles that no longer have a corresponding UserDevice.
        /// This handles the case where the user removes a device via the UI while
        /// it's still physically connected — the tracking must be cleared so the
        /// device can be re-detected on the next enumeration cycle.
        /// </summary>
        private void PruneOrphanedHandles(HashSet<IntPtr> trackedHandles)
        {
            if (trackedHandles.Count == 0)
                return;

            var toRemove = new List<IntPtr>();
            foreach (IntPtr handle in trackedHandles)
            {
                if (FindOnlineDeviceByHandle(handle) == null)
                    toRemove.Add(handle);
            }

            for (int i = 0; i < toRemove.Count; i++)
                trackedHandles.Remove(toRemove[i]);
        }

        // ─────────────────────────────────────────────
        //  External device registration (web controllers)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Registers the touchpad overlay as a virtual device in the device list.
        /// </summary>
        public void RegisterOverlayDevice(TouchpadOverlayDevice device)
        {
            if (device == null) return;

            UserDevice ud = FindOrCreateUserDevice(device.InstanceGuid, device.ProductGuid);
            ud.LoadFromOverlayDevice(device);
            ud.IsOnline = true;

            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Registers an external (non-SDL) input device into the device list.
        /// Called by WebControllerServer when a browser client connects.
        /// Thread-safe via UserDevices.SyncRoot.
        /// </summary>
        public void RegisterExternalDevice(WebControllerDevice device)
        {
            if (device == null) return;

            UserDevice ud = FindOrCreateUserDevice(device.InstanceGuid, device.ProductGuid);
            ud.LoadFromWebDevice(device);
            ud.IsOnline = true;

            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Registers a remote peer's device (issue #138) into the device list.
        /// Called by LinkServer when a paired peer exposes a device. Same shape as
        /// RegisterExternalDevice but uses the generic LoadFromExternalDevice, since
        /// a RemotePeerDevice is just another ISdlInputDevice. Disconnect reuses
        /// UnregisterExternalDevice(Guid) below.
        /// </summary>
        public void RegisterPeerDevice(PadForge.Engine.RemoteLink.RemotePeerDevice device)
        {
            if (device == null) return;

            UserDevice ud = FindOrCreateUserDevice(device.InstanceGuid, device.ProductGuid);
            ud.LoadFromExternalDevice(device);
            ud.IsOnline = true;

            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        // ── Gamepad-only restriction (issue #138) ───────────────────────────
        // A peer paired with the "gamepad only" option may drive gamepad output
        // but never keyboard/mouse/scroll — neither via a KBM virtual controller
        // nor via a macro. The set holds the InstanceGuids of restricted peer
        // devices; the SendInput chokepoints consult IsSlotRestricted.
        private readonly HashSet<Guid> _restrictedDevices = new();
        private readonly object _restrictedLock = new();

        /// <summary>Mark (or clear) a device as gamepad-only restricted.</summary>
        public void SetDeviceRestricted(Guid instanceGuid, bool restricted)
        {
            lock (_restrictedLock)
            {
                if (restricted) _restrictedDevices.Add(instanceGuid);
                else _restrictedDevices.Remove(instanceGuid);
            }
        }

        /// <summary>Snapshot of restricted device GUIDs, or null when none (early-out).</summary>
        private Guid[] RestrictedSnapshot()
        {
            lock (_restrictedLock)
            {
                if (_restrictedDevices.Count == 0) return null;
                var a = new Guid[_restrictedDevices.Count];
                _restrictedDevices.CopyTo(a);
                return a;
            }
        }

        /// <summary>True if any of these macros is triggered by a restricted device,
        /// even when it lives on a slot the restricted device isn't mapped to.</summary>
        internal bool AnyMacroTriggerRestricted(PadForge.ViewModels.MacroItem[] macros)
        {
            if (macros == null) return false;
            var restricted = RestrictedSnapshot();
            if (restricted == null) return false;
            foreach (var m in macros)
            {
                if (m == null) continue;
                if (Array.IndexOf(restricted, m.TriggerDeviceGuid) >= 0) return true;
                var entries = m.GetTriggerInputEntries();
                if (entries != null)
                    foreach (var e in entries)
                        if (Array.IndexOf(restricted, e.DeviceGuid) >= 0) return true;
            }
            return false;
        }

        /// <summary>True if any online restricted device is a source for this slot.
        /// Free when no peer is restricted (the common case early-outs).</summary>
        internal bool IsSlotRestricted(int slot)
        {
            Guid[] restricted = RestrictedSnapshot();
            if (restricted == null) return false;
            var settings = SettingsManager.UserSettings;
            if (settings == null) return false;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us.MapTo == slot && Array.IndexOf(restricted, us.InstanceGuid) >= 0)
                        return true;
            }
            return false;
        }

        /// <summary>
        /// Marks an external device as offline when its connection is lost.
        /// Called by WebControllerServer when a browser client disconnects.
        /// </summary>
        public void UnregisterExternalDevice(Guid instanceGuid)
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return;

            // Resolve under the devices lock, but mark offline OUTSIDE it.
            // MarkDeviceOffline takes the UserSettings lock to neutralize the
            // device's per-slot outputs; holding UserDevices while acquiring
            // UserSettings here (a ThreadPool websocket-disconnect thread)
            // would form an ABBA pair with the UI-thread sites that nest the
            // same locks Settings-first. The lock only guards the scan; the
            // marking itself needs no collection lock.
            UserDevice target = null;
            lock (devices.SyncRoot)
            {
                for (int i = 0; i < devices.Items.Count; i++)
                {
                    var d = devices.Items[i];
                    if (d.IsOnline && d.InstanceGuid == instanceGuid)
                    {
                        target = d;
                        break;
                    }
                }
            }

            if (target != null)
                MarkDeviceOffline(target);

            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Finds an online device that was opened from the given Raw Input handle.
        /// Checks the RawInputHandle property on keyboard/mouse wrappers.
        /// </summary>
        private UserDevice FindOnlineDeviceByHandle(IntPtr handle)
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return null;

            // The keyboard/mouse wrappers store _sdlId = (uint)devicePath.GetHashCode().
            // We need to match on the device reference since we can't recover the path
            // from just the handle. Check Device.RawInputHandle for keyboard/mouse wrappers.
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    if (!d.IsOnline || d.Device == null)
                        continue;

                    if (d.Device is SdlKeyboardWrapper kb && kb.RawInputHandle == handle)
                        return d;
                    if (d.Device is SdlMouseWrapper mouse && mouse.RawInputHandle == handle)
                        return d;
                }
                return null;
            }
        }

        private UserDevice FindOnlineDeviceByDevicePath(string path)
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null || string.IsNullOrEmpty(path)) return null;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    if (d.IsOnline && d.DevicePath == path)
                        return d;
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Placeholder for the SettingsManager's UserDevices collection.
    /// </summary>
    public static partial class SettingsManager
    {
        public static DeviceCollection UserDevices { get; set; }
        public static SettingsCollection UserSettings { get; set; }
    }

    /// <summary>
    /// Thread-safe collection of UserDevice records with a sync root for locking.
    /// </summary>
    public class DeviceCollection
    {
        public List<UserDevice> Items { get; } = new List<UserDevice>();
        public object SyncRoot { get; } = new object();
    }

    /// <summary>
    /// Thread-safe collection of UserSetting records.
    /// </summary>
    public class SettingsCollection
    {
        public List<UserSetting> Items { get; } = new List<UserSetting>();
        public object SyncRoot { get; } = new object();

        /// <summary>Live record count under the lock. Used by hot-path callers
        /// to size a reusable buffer for the non-allocating FindByPadIndex
        /// overload: any single slot's settings are a subset of all records,
        /// so a buffer this size can never truncate a per-slot query.</summary>
        public int Count { get { lock (SyncRoot) return Items.Count; } }

        /// <summary>
        /// Finds the UserSetting that links a device (by InstanceGuid) to a pad slot.
        /// Uses a manual loop to avoid LINQ closure allocations.
        /// </summary>
        public UserSetting FindByInstanceGuid(Guid instanceGuid)
        {
            lock (SyncRoot)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i].InstanceGuid == instanceGuid)
                        return Items[i];
                }
                return null;
            }
        }

        /// <summary>
        /// Returns all UserSettings assigned to a specific pad slot (0–15).
        /// Allocates a new List — use <see cref="FindByPadIndex(int, UserSetting[], out int)"/>
        /// in the hot path to avoid allocations.
        /// </summary>
        public List<UserSetting> FindByPadIndex(int padIndex)
        {
            var results = new List<UserSetting>();
            lock (SyncRoot)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i].MapTo == padIndex)
                        results.Add(Items[i]);
                }
            }
            return results;
        }

        /// <summary>
        /// Non-allocating overload: fills a pre-allocated buffer with all UserSettings
        /// for a given device (by InstanceGuid) that have a valid MapTo (>= 0).
        /// Returns the count of matches. Skips orphaned entries (MapTo == -1).
        /// </summary>
        public int FindByInstanceGuid(Guid instanceGuid, UserSetting[] buffer)
        {
            int count = 0;
            lock (SyncRoot)
            {
                for (int i = 0; i < Items.Count && count < buffer.Length; i++)
                {
                    if (Items[i].InstanceGuid == instanceGuid && Items[i].MapTo >= 0)
                        buffer[count++] = Items[i];
                }
            }
            return count;
        }

        /// <summary>
        /// Non-allocating overload: fills a pre-allocated buffer with UserSettings
        /// assigned to the specified pad slot. Returns the count of matches.
        /// </summary>
        public int FindByPadIndex(int padIndex, UserSetting[] buffer)
        {
            int count = 0;
            lock (SyncRoot)
            {
                for (int i = 0; i < Items.Count && count < buffer.Length; i++)
                {
                    if (Items[i].MapTo == padIndex)
                        buffer[count++] = Items[i];
                }
            }
            return count;
        }
    }
}
