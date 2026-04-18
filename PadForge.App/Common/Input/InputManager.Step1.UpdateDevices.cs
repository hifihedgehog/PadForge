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

            // After Step 5 updates the XInput hook mask, drop all open SDL
            // joystick handles so the next enumeration pass picks up the
            // masked state — SDL's XInput backend will skip hidden slots
            // and the virtual never enters PadForge's device list.
            if (_sdlJoysticksNeedReopen)
            {
                _sdlJoysticksNeedReopen = false;
                var sdlIds = _openedSdlInstanceIds.ToArray();
                foreach (uint sid in sdlIds)
                {
                    var u = FindOnlineDeviceBySdlInstanceId(sid);
                    if (u?.Device != null)
                    {
                        try { u.Device.Dispose(); } catch { }
                        u.Device = null;
                    }
                    _openedSdlInstanceIds.Remove(sid);
                }
            }

            // Revalidate the HIDMaestro-filtered SDL IDs every cycle: if the
            // underlying XInput slot no longer belongs to one of our virtual
            // controllers (e.g. the virtual was destroyed and the real moved
            // back into that slot), remove the ID from the filter cache so
            // the real device is re-opened on this pass. Without this the
            // filtered cache is sticky across virtual lifecycle and can
            // permanently mask the user's real input device.
            var cached = _filteredVigemInstanceIds.ToArray();
            foreach (uint cid in cached)
            {
                string cp = SDL_GetJoystickPathForID(cid) ?? string.Empty;
                if (cp.StartsWith("XInput#", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(cp.Substring(7), out int cslot)
                    && !IsHidMaestroXInputSlot(cslot))
                {
                    _filteredVigemInstanceIds.Remove(cid);
                }
            }

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

                    // ── Pre-open filtering ──
                    // CRITICAL: query the device path via SDL_GetJoystickPathForID
                    // WITHOUT opening the joystick, and skip HIDMaestro virtual
                    // devices before any HID handle is opened.
                    //
                    // Opening SDL joysticks for xinputhid profiles (e.g.
                    // xbox-series-xs-bt) disturbs the PnP settling process —
                    // even an open+immediate-close makes the HID collection
                    // invisible to DirectInput, so joy.cpl and games miss the
                    // device entirely. The test app works because it never
                    // enumerates with SDL. Filter pre-open, NOT post-open.
                    string prePath = SDL_GetJoystickPathForID(instanceId) ?? string.Empty;
                    bool hmMatch = !string.IsNullOrEmpty(prePath) && IsHidMaestroAncestor(prePath);
                    if (hmMatch)
                    {
                        Debug.WriteLine($"[Step1] Pre-open filtered HIDMaestro device: SDL#{instanceId} path={prePath}");
                        _filteredVigemInstanceIds.Add(instanceId);
                        continue;
                    }

                    // Open the device by instance ID.
                    var wrapper = new SdlDeviceWrapper();
                    if (!wrapper.Open(instanceId))
                    {
                        wrapper.Dispose();
                        continue;
                    }

                    // ── Post-open filtering (fallback) ──
                    // Still check post-open in case the pre-open path query
                    // returned an empty or unrecognized path — defence in depth.
                    if (IsViGEmVirtualDevice(wrapper))
                    {
                        Debug.WriteLine($"[Step1] Post-open filtered HIDMaestro device: SDL#{instanceId} VID={wrapper.VendorId:X4} PID={wrapper.ProductId:X4} path={wrapper.DevicePath} name={wrapper.Name}");
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
        //  HIDMaestro virtual device detection
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks whether an SDL device is a HIDMaestro virtual controller
        /// (our own output device that must not be opened as an input device,
        /// or SDL would feed our outputs back into themselves as inputs and
        /// create a feedback loop).
        ///
        /// Detection: HIDMaestro devices are enumerated under hardware ID
        /// `root\HIDMaestro` (see hidmaestro.inf). Their PnP instance paths
        /// contain `HIDMAESTRO` regardless of how the profile spoofs VID/PID
        /// for the host application's view. Matching the path is the only
        /// reliable way to filter our own outputs since HIDMaestro intentionally
        /// reports real Xbox / DualSense / wheel VID/PIDs to make virtual
        /// devices indistinguishable from real ones at the application layer.
        /// </summary>
        private bool IsViGEmVirtualDevice(SdlDeviceWrapper wrapper)
        {
            string path = wrapper.DevicePath;
            if (string.IsNullOrEmpty(path))
                return false;

            // Fast path: direct substring match catches the unspoofed cases
            // (root enumerator in the device interface symlink).
            string pathUpper = path.ToUpperInvariant();
            if (pathUpper.Contains("HIDMAESTRO") || pathUpper.Contains("HMXINPUT"))
                return true;

            // HIDMaestro profiles spoof real Xbox / Sony / wheel VID+PID for
            // the HID child collection, so the SDL symlink (e.g.
            // "\\?\HID#VID_045E&PID_028E#...") looks identical to a genuine
            // device. Walk the PnP parent chain to find the root enumerator —
            // HIDMaestro devices live under ROOT\HIDMAESTRO* (see driver INFs:
            // HIDMaestro, HIDMaestroGamepad, HIDMaestroUSB, HIDMaestroXna,
            // HIDMaestroXnaHid, HIDMaestroXUSB).
            return IsHidMaestroAncestor(path);
        }

        private bool IsHidMaestroAncestor(string symlinkPath)
        {
            // SDL uses a synthetic "XInput#N" path for its XInput backend —
            // there's no PnP tree to walk. Consult the live HMController list
            // directly: if any of our virtual controllers has claimed this
            // XInput slot, filter it. This is the ONLY case where real and
            // virtual Xbox devices share an identical SDL path, so without
            // this check both show up in the Devices list and a physical
            // controller mapping can race onto the wrong slot at startup.
            if (symlinkPath != null
                && symlinkPath.StartsWith("XInput#", StringComparison.OrdinalIgnoreCase)
                && symlinkPath.Length > 7
                && int.TryParse(symlinkPath.Substring(7), out int xiSlot)
                && IsHidMaestroXInputSlot(xiSlot))
            {
                return true;
            }

            // Convert HID device interface symlink to a PnP device instance ID.
            //   "\\?\HID#VID_045E&PID_028E#7&abc&0&0000#{4d1e55b2-...}"
            // → "HID\VID_045E&PID_028E\7&abc&0&0000"
            string s = symlinkPath;
            if (s.StartsWith(@"\\?\")) s = s.Substring(4);
            int brace = s.IndexOf('{');
            if (brace >= 0) s = s.Substring(0, brace).TrimEnd('#');
            string instanceId = s.Replace('#', '\\');

            uint devInst;
            int locateRc = CM_Locate_DevNodeW(out devInst, instanceId, 0);
            if (locateRc != 0)
            {
                return false;
            }

            // Depth-0 hardware ID check: every HIDMaestro HID child has
            // "HID\HIDMaestro" in its Hardware IDs (CM_DRP_HARDWAREID).
            // This is the most reliable single check — catches DS4,
            // DualSense, wheels, HOTAS, flight sticks, and all non-Xbox
            // profiles immediately with zero false positives.
            if (HasHidMaestroHardwareId(devInst))
                return true;

            // Walk the PnP parent chain. At each level check both the
            // instance ID (for legacy HIDMaestro root enumerator patterns)
            // and DEVPKEY_Device_Manufacturer (the canonical identifier —
            // set to "HIDMaestro" on every root device our SDK creates and
            // nowhere else on the system). Manufacturer string is the most
            // reliable signal: real Xbox BT controllers report "(Standard
            // system devices)", real Xbox wired USB devices report
            // "Microsoft", never "HIDMaestro". Matching on Manufacturer
            // means spoofed VID/PID profiles (xbox-series-xs-bt etc.) are
            // filtered correctly regardless of how Windows chooses to name
            // their enumerator path on a given machine.
            var idBuffer = new System.Text.StringBuilder(512);
            for (int depth = 0; depth < 16; depth++)
            {
                // --- Manufacturer property check ---
                var mfg = new char[128];
                int mfgLen = mfg.Length * 2;
                if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_MFG, out _, mfg, ref mfgLen, 0) == 0)
                {
                    int strLen = 0;
                    while (strLen < mfg.Length && mfg[strLen] != '\0') strLen++;
                    string mfgStr = new string(mfg, 0, strLen);
                    if (string.Equals(mfgStr, "HIDMaestro", StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // --- Instance ID string check (legacy patterns) ---
                idBuffer.Clear();
                idBuffer.EnsureCapacity(512);
                if (CM_Get_Device_IDW(devInst, idBuffer, idBuffer.Capacity, 0) == 0)
                {
                    string id = idBuffer.ToString();
                    if (id.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0
                        || id.IndexOf("HMCOMPANION", StringComparison.OrdinalIgnoreCase) >= 0
                        || id.IndexOf("HMXINPUT", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;

                    if (id.StartsWith(@"ROOT\VID_", StringComparison.OrdinalIgnoreCase)
                        && (id.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0
                            || id.IndexOf("&XI_", StringComparison.OrdinalIgnoreCase) >= 0))
                        return true;
                }

                uint parent;
                if (CM_Get_Parent(out parent, devInst, 0) != 0) break;
                if (parent == 0 || parent == devInst) break;
                devInst = parent;
            }
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XI_GAMEPAD_DBG
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XI_STATE_DBG
        {
            public uint dwPacketNumber;
            public XI_GAMEPAD_DBG Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetStateRaw(int dwUserIndex, out XI_STATE_DBG pState);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, System.Text.StringBuilder buffer, int len, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(
            uint devInst, uint property, out uint pulRegDataType,
            [Out] char[] buffer, ref int length, uint flags);

        // CM_DRP_HARDWAREID = 0x02 — REG_MULTI_SZ list of hardware IDs.
        // Every HIDMaestro HID child has "HID\HIDMaestro" in this list.
        private const uint CM_DRP_HARDWAREID = 0x02;

        // CM_DRP_MFG = 0x0D — the legacy "Manufacturer" property.
        private const uint CM_DRP_MFG = 0x0D;

        /// <summary>
        /// Checks whether a device node has "HIDMaestro" in any of its
        /// Hardware IDs (CM_DRP_HARDWAREID). Returns true if found.
        /// This is the most reliable single-call detection: every
        /// HIDMaestro profile's HID child gets "HID\HIDMaestro" written
        /// by the INF, and no real physical device ever has it.
        /// </summary>
        private static bool HasHidMaestroHardwareId(uint devInst)
        {
            var buffer = new char[1024];
            int length = buffer.Length * 2; // bytes
            if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_HARDWAREID,
                    out _, buffer, ref length, 0) != 0)
                return false;

            // REG_MULTI_SZ: null-separated strings, double-null terminated.
            int charCount = length / 2;
            int start = 0;
            for (int i = 0; i < charCount; i++)
            {
                if (buffer[i] == '\0')
                {
                    if (i == start) break; // double-null = end
                    var id = new string(buffer, start, i - start);
                    if (id.IndexOf("HIDMaestro", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    start = i + 1;
                }
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
