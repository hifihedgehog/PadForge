using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        //  ViGEm virtual controllers are detected and filtered out.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Set of SDL instance IDs that we have already opened (joysticks).
        /// Used to detect new vs. already-known devices.
        /// SDL3: instance IDs are uint (0 = invalid).
        /// </summary>
        private readonly HashSet<uint> _openedSdlInstanceIds = new HashSet<uint>();

        // Keyboard/mouse tracking moved to _openedKeyboardHandles / _openedMouseHandles
        // (Raw Input IntPtr handles instead of SDL uint IDs).

        /// <summary>
        /// SDL instance IDs identified as ViGEm virtual controllers.
        /// These are skipped entirely on subsequent enumeration cycles to avoid
        /// the open/close cycle that resets XInput rumble state — SDL3's close
        /// internally calls XInputSetState(0,0) on the device's XInput slot,
        /// which triggers ViGEm's FeedbackReceived(0,0) and kills active vibration.
        /// </summary>
        private readonly HashSet<uint> _filteredVigemInstanceIds = new HashSet<uint>();

        // ── Async Raw Input enumeration ──
        // Raw Input keyboard/mouse enumeration is expensive (CreateFile +
        // HidD_GetAttributes + registry per device). Running it off the
        // polling thread eliminates the ~2-5ms spike every 2 seconds.
        private volatile bool _rawInputEnumPending;
        private volatile bool _rawInputEnumRunning;
        private RawInputListener.DeviceInfo[] _cachedKeyboards;
        private RawInputListener.DeviceInfo[] _cachedMice;
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
                    // Skip devices already identified as ViGEm virtual controllers.
                    if (_filteredVigemInstanceIds.Contains(instanceId))
                        continue;

                    // Skip devices we already have open (by SDL instance ID).
                    // This is more reliable than GUID matching because serial-based
                    // GUIDs aren't available until after the device is opened.
                    if (_openedSdlInstanceIds.Contains(instanceId))
                        continue;

                    // Open the device by instance ID.
                    var wrapper = new SdlDeviceWrapper();
                    if (!wrapper.Open(instanceId))
                    {
                        wrapper.Dispose();
                        continue;
                    }

                    // ── Post-open filtering ──
                    // Skip ViGEm virtual controllers (our own output devices).
                    if (IsViGEmVirtualDevice(wrapper))
                    {
                        Debug.WriteLine($"[Step1] Filtered ViGEm device: SDL#{instanceId} VID={wrapper.VendorId:X4} PID={wrapper.ProductId:X4} path={wrapper.DevicePath} name={wrapper.Name}");
                        _filteredVigemInstanceIds.Add(instanceId);
                        wrapper.Dispose();
                        continue;
                    }

                    Debug.WriteLine($"[Step1] Accepted device: SDL#{instanceId} VID={wrapper.VendorId:X4} PID={wrapper.ProductId:X4} path={wrapper.DevicePath} name={wrapper.Name}");

                    // Find or create the UserDevice record.
                    // Passes ProductGuid for fallback matching when InstanceGuid changes
                    // (e.g. Bluetooth device reconnects with a different device path).
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
                // First call — synchronous so devices are ready before Step 2.
                _cachedKeyboards = RawInputListener.EnumerateKeyboards();
                _cachedMice = RawInputListener.EnumerateMice();
                _rawInputEnumPending = true;
            }

            if (_rawInputEnumPending)
            {
                RawInputListener.DeviceInfo[] keyboards, mice;
                lock (_rawInputCacheLock)
                {
                    keyboards = _cachedKeyboards;
                    mice = _cachedMice;
                    _rawInputEnumPending = false;
                }

                changed |= EnumerateKeyboards(keyboards);
                changed |= EnumerateMice(mice);
                changed |= DetectDisconnectedHandles(_openedKeyboardHandles, keyboards);
                changed |= DetectDisconnectedHandles(_openedMouseHandles, mice);
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
                        lock (_rawInputCacheLock)
                        {
                            _cachedKeyboards = kb;
                            _cachedMice = ms;
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

            // --- Phase 2: Detect disconnected SDL devices ---
            var disconnectedIds = new List<uint>();

            foreach (uint sdlId in _openedSdlInstanceIds)
            {
                // Find the UserDevice with this SDL instance ID.
                UserDevice ud = FindOnlineDeviceBySdlInstanceId(sdlId);
                if (ud == null)
                {
                    disconnectedIds.Add(sdlId);
                    continue;
                }

                // Check if the device is still attached.
                if (ud.Device == null || !ud.Device.IsAttached)
                {
                    MarkDeviceOffline(ud);
                    disconnectedIds.Add(sdlId);
                    changed = true;
                }
            }

            // Clean up tracking for disconnected devices.
            foreach (uint sdlId in disconnectedIds)
            {
                _openedSdlInstanceIds.Remove(sdlId);
            }

            // Clean up ViGEm IDs that are no longer present (virtual controller destroyed).
            _filteredVigemInstanceIds.IntersectWith(currentInstanceIds);

            // --- Notify if anything changed ---
            if (changed)
            {
                DevicesUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        // ─────────────────────────────────────────────
        //  ViGEm virtual device detection
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks whether an SDL device is a ViGEm virtual controller
        /// (our own output device that must not be opened as an input device).
        ///
        /// Detection methods:
        ///   1. Device path containing ViGEm signatures ("vigem", "virtual")
        ///   2. Zero VID/PID + SDL game controller + active ViGEm count > 0
        ///   3. Xbox 360 VID/PID (045E:028E) — filter up to _activeXbox360Count
        ///   4. DS4 VID/PID (054C:05C4) — filter up to _activeDs4Count
        ///
        /// For Xbox 360 and DS4, we use our own count of virtual controllers
        /// created by Step 5 rather than walking the PnP device tree. ViGEm DS4
        /// devices don't register under USB\VID_054C&amp;PID_05C4 in the registry
        /// (they use ViGEmBus's own bus enumerator), making PnP tree walks unreliable.
        /// </summary>
        private bool IsViGEmVirtualDevice(SdlDeviceWrapper wrapper)
        {
            // ── Check device path for ViGEm signatures ──
            string path = wrapper.DevicePath;
            if (!string.IsNullOrEmpty(path))
            {
                string pathLower = path.ToLowerInvariant();
                if (pathLower.Contains("vigem") || pathLower.Contains("virtual"))
                    return true;
            }

            // ── Zero VID/PID + recognized as game controller ──
            // ViGEm devices may report VID/PID as 0 through SDL's
            // pre-open enumeration. If a device has no VID/PID but SDL
            // recognizes it as a standard game controller, AND we currently
            // have ViGEm virtual controllers active (or expect to), it's very likely virtual.
            if (wrapper.VendorId == 0 && wrapper.ProductId == 0)
            {
                if ((_activeVigemCount > 0 || _expectedXbox360Count > 0 || _expectedDs4Count > 0)
                    && wrapper.IsGameController)
                    return true;
            }

            // ── vJoy VID/PID — our own virtual joystick output device ──
            // vJoy devices (VID=1234 PID=BEAD) must not be opened as input devices.
            // SDL opening the HID device can interfere with vJoyInterface.dll's
            // write path (the driver's HID report mechanism).
            if (wrapper.VendorId == 0x1234 && wrapper.ProductId == 0xBEAD)
                return true;

            // ── Xbox 360 VID/PID — ViGEm emulates exactly this ──
            // ViGEm Xbox 360 virtual controllers report VID=045E PID=028E.
            // Modern real Xbox controllers use different PIDs (0B12, 0B13, 0B20, etc.)
            // — only the original Xbox 360 controller from 2005 uses 028E.
            // When we have any active/expected Xbox VCs, filter ALL 045E:028E devices
            // to prevent feedback loops where stale ViGEm nodes slip through a
            // counter-based filter and cause exponential virtual controller creation.
            if (wrapper.VendorId == 0x045E && wrapper.ProductId == 0x028E)
            {
                int filterCount = Math.Max(_activeXbox360Count, _expectedXbox360Count);
                if (filterCount > 0)
                    return true;
            }

            // ── DS4 VID/PID — ViGEm emulates Sony DS4 ──
            // Same logic: filter ALL 054C:05C4 devices when we have active/expected DS4 VCs.
            // Real DS4 controllers with this exact PID are rare (original DualShock 4 v1);
            // newer DS4s and DualSense use different PIDs.
            if (wrapper.VendorId == 0x054C && wrapper.ProductId == 0x05C4)
            {
                int filterCount = Math.Max(_activeDs4Count, _expectedDs4Count);
                if (filterCount > 0)
                    return true;
            }

            return false;
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

            ud.ClearRuntimeState();
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

                try
                {
                    var wrapper = new SdlKeyboardWrapper();
                    if (!wrapper.Open(kb))
                    {
                        wrapper.Dispose();
                        continue;
                    }

                    UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid);
                    ud.LoadFromKeyboardDevice(wrapper);
                    ud.IsOnline = true;

                    _openedKeyboardHandles.Add(kb.Handle);
                    changed = true;
                }
                catch (Exception ex)
                {
                    RaiseError($"Error opening keyboard ({kb.Name})", ex);
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

                try
                {
                    var wrapper = new SdlMouseWrapper();
                    if (!wrapper.Open(mouse))
                    {
                        wrapper.Dispose();
                        continue;
                    }

                    UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid);
                    ud.LoadFromMouseDevice(wrapper);
                    ud.IsOnline = true;

                    _openedMouseHandles.Add(mouse.Handle);
                    changed = true;
                }
                catch (Exception ex)
                {
                    RaiseError($"Error opening mouse ({mouse.Name})", ex);
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
        /// Marks an external device as offline when its connection is lost.
        /// Called by WebControllerServer when a browser client disconnects.
        /// </summary>
        public void UnregisterExternalDevice(Guid instanceGuid)
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return;

            lock (devices.SyncRoot)
            {
                for (int i = 0; i < devices.Items.Count; i++)
                {
                    var d = devices.Items[i];
                    if (d.IsOnline && d.InstanceGuid == instanceGuid)
                    {
                        MarkDeviceOffline(d);
                        break;
                    }
                }
            }

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
