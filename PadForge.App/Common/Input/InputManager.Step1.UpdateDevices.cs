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
        /// SDL instance IDs that we have already opened (joysticks), each with
        /// the wrapper opened for it. Used to detect new vs. already-known
        /// devices; the wrapper reference lets the disconnect sweep dispose an
        /// orphan whose UserDevice no longer points at it (UI Remove, or a
        /// replug rebind that swapped ud.Device to a fresh wrapper) instead of
        /// leaving its SDL handles to the GC finalizer racing the poll loop.
        /// SDL3: instance IDs are uint (0 = invalid).
        /// </summary>
        private readonly Dictionary<uint, SdlDeviceWrapper> _openedSdlInstanceIds = new Dictionary<uint, SdlDeviceWrapper>();

        // SDL instance IDs identified as OUR OWN HM virtuals and rejected
        // by the self-readback guard; kept so each enumeration pass skips
        // them instead of re-opening and re-probing every 2 s. Cleared
        // implicitly on process restart; SDL instance IDs are unique per
        // connection so a REAL device never inherits a suppressed id.
        private readonly HashSet<uint> _suppressedSelfVirtualIds = new();

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
                    if (_openedSdlInstanceIds.ContainsKey(instanceId))
                        continue;

                    // Previously rejected self-virtual: don't reopen it on
                    // every enumeration pass.
                    if (_suppressedSelfVirtualIds.Contains(instanceId))
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

                    // Self-readback guard (2026-07-20): PadForge must NEVER
                    // open its own HM virtuals. The fork-side enumeration
                    // filter and the cloak both exist, but a driver upgrade
                    // recreates the virtual devnodes with fresh instance
                    // paths and can slip past both (observed live: the SDL
                    // switch driver then FIGHTS the virtual Switch Pro's
                    // protocol responder, cyclically resetting its inputs
                    // and interleaving rumble). Coverage is narrower than
                    // it looks: SDL's HIDAPI drivers overwrite the hid-level
                    // "HM-CTL-<n>" serial with the fabricated MAC during
                    // their identity handshake, and non-Xbox virtuals'
                    // interface paths carry no HIDMAESTRO marker (the fork
                    // filter reads DEVPKEY hardware IDs for that reason).
                    // So this guard catches failed-handshake and
                    // serial-preserving cases only; the fork enumeration
                    // filter remains the primary defense, and the XInput
                    // backend leak (no serial, no path marker) is out of
                    // scope here entirely.
                    bool selfVirtual =
                        (wrapper.SerialNumber != null
                         && wrapper.SerialNumber.StartsWith("HM-CTL-", StringComparison.Ordinal))
                        || (wrapper.DevicePath != null
                            && wrapper.DevicePath.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0)
                        // Composite personas (HM v1.4.0) ride the real USB
                        // stack and carry neither marker. Their one
                        // discriminator is usbip2_ude ancestry. Sony VID
                        // only, the sole composite vendor today.
                        || (wrapper.VendorId == 0x054C && IsOnUsbipVhci(wrapper.DevicePath));
                    if (selfVirtual)
                    {
                        Engine.SdlDiagLog.WriteLine(
                            $"DEV self-virtual suppressed SDL#{instanceId} {wrapper.VendorId:X4}:{wrapper.ProductId:X4} serial={wrapper.SerialNumber}");
                        _suppressedSelfVirtualIds.Add(instanceId);
                        wrapper.Dispose();
                        continue;
                    }

                    Debug.WriteLine($"[Step1] Accepted device: SDL#{instanceId} VID={wrapper.VendorId:X4} PID={wrapper.ProductId:X4} path={wrapper.DevicePath} name={wrapper.Name}");
                    Engine.SdlDiagLog.WriteLine($"DEV + SDL#{instanceId} {wrapper.VendorId:X4}:{wrapper.ProductId:X4} {wrapper.Name}");

                    UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid, wrapper.ProductGuid,
                        currentInstanceIds, wrapper.SerialNumber);

                    // Same-serial twin (owner-approved 2026-07-25): the
                    // resolver bound this connection to a row whose
                    // identity differs from the wrapper's serial-derived
                    // GUID (an adopted or rebound twin row keeping its
                    // persisted identity, or a first-ever minted one).
                    // The wrapper must carry that identity BEFORE
                    // LoadFromSdlDevice, which stamps the row's
                    // InstanceGuid from the wrapper.
                    if (ud.InstanceGuid != wrapper.InstanceGuid)
                        wrapper.OverrideInstanceGuid(ud.InstanceGuid);

                    // Populate from the SDL device.
                    ud.LoadFromSdlDevice(wrapper);
                    ud.IsOnline = true;

                    // Track the SDL instance ID with its wrapper.
                    _openedSdlInstanceIds[wrapper.SdlInstanceId] = wrapper;

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
                            if (ud != null)
                            {
                                ud.IsOnline = false;
                                // Same neutralize the MIDI and NFC removal
                                // paths perform. A click-bar press or a mapped
                                // contact asserted when the touchpad vanished
                                // stayed stamped on the slot's combined output,
                                // because Step 3 keeps the last OutputState for
                                // an offline device.
                                NeutralizeMappedOutputsFor(ud);
                            }
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
                if (mergedUd != null)
                {
                    mergedUd.IsOnline = false;
                    // This one fires on a live config change (merging turned
                    // off), not just at shutdown, so a held input on the merged
                    // surface would latch on the slot exactly as above.
                    NeutralizeMappedOutputsFor(mergedUd);
                }
                _ptpMergedCreated = false;
                changed = true;
            }

            // --- Phase 1e: MIDI input endpoints (issue #128) ---
            changed |= UpdateMidiInputDevices();

            // --- Phase 1f: NFC PC/SC readers (issue #150) ---
            changed |= UpdateNfcReaderDevices();

            // --- Phase 1g: Sony headset head trackers (issue #188) ---
            changed |= UpdateHeadsetMotionDevices();

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

            foreach (uint sdlId in _openedSdlInstanceIds.Keys)
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
                    //    here. Detachment is permanent for a handle, so no
                    //    debounce applies.
                    //  - The UserDevice itself is gone. Nothing to clean.
                    var offlineUd = FindDeviceBySdlInstanceIdAnyState(sdlId);
                    if (offlineUd != null && offlineUd.Device != null)
                    {
                        MarkDeviceOffline(offlineUd);
                        changed = true;
                    }
                    // Orphaned wrapper: no UserDevice references it anymore
                    // (UI Remove dropped the record, or a replug rebind
                    // swapped ud.Device to a fresh wrapper). Dispose it here
                    // on the poll thread, the same thread as MarkDeviceOffline's
                    // dispose. Dispose is idempotent, so the handled case above
                    // is safe to skip by reference.
                    //
                    // EXCEPT when the same physical device is live under a
                    // rebound wrapper: HIDAPI drivers keep ONE context per
                    // device, and closing a stale instance runs the driver's
                    // CloseJoystick against it (the fork's Wii CloseJoystick
                    // nulls ctx->joystick), clobbering the live instance's
                    // registration. Disposing here after a Wii re-identify
                    // put the driver into a 10 s re-identify churn (observed
                    // 2026-07-11, diag.log). For the rebind case the stale
                    // handle is deliberately left to the finalizer, the
                    // long-standing behavior before the 2026-07-11 audit.
                    if (_openedSdlInstanceIds.TryGetValue(sdlId, out var orphan)
                        && orphan != null && !ReferenceEquals(orphan, offlineUd?.Device)
                        && !DeviceLiveUnderNewWrapper(orphan))
                    {
                        try { orphan.Dispose(); } catch { }
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
        /// <summary>Capped memo for config Guid strings parsed on the
        /// 1 kHz path (motion-source resolution). Same policy as the
        /// tuning parse memos.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid>
            s_guidParseCache = new(System.StringComparer.Ordinal);

        internal static bool TryParseGuidCached(string text, out Guid guid)
        {
            if (string.IsNullOrEmpty(text)) { guid = Guid.Empty; return false; }
            if (s_guidParseCache.TryGetValue(text, out guid)) return guid != Guid.Empty;
            bool ok = Guid.TryParse(text, out guid);
            if (s_guidParseCache.Count < 4096)
                s_guidParseCache[text] = ok ? guid : Guid.Empty;
            return ok;
        }

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
        // Internal so the same-model adoption path is testable end to end.
        // Pinning only the serial predicate left the CALL SITE unguarded:
        // deleting the gate from the loop kept every predicate test green.
        //
        // livePresentSdlIds (same-serial twin gate, owner-approved
        // 2026-07-25): the SDL sweep passes the instance IDs SDL reports
        // present RIGHT NOW. Serial outranks device path in
        // BuildInstanceGuid, so two units reporting the identical serial
        // string (a real clone-pad shape) build the SAME InstanceGuid, and
        // without this gate the second unit stole the first one's row and
        // disposed its live wrapper, violating the process-time
        // distinctness rule. An exact-GUID row is treated as a LIVE TWIN's
        // row only when its claiming wrapper's SDL instance is still in
        // the present set: a same-unit reconnect always arrives under a
        // NEW instance id while the old one has left the set (one physical
        // device cannot be two present instances), so the rebind flow the
        // disconnect debounce relies on is untouched. Every non-SDL caller
        // passes null and keeps today's semantics exactly.
        internal UserDevice FindOrCreateUserDevice(Guid instanceGuid, Guid productGuid = default,
            HashSet<uint> livePresentSdlIds = null, string serialNumber = null)
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return new UserDevice { InstanceGuid = instanceGuid };

            lock (devices.SyncRoot)
            {
                // 1. Exact match by InstanceGuid.
                UserDevice exact = null;
                for (int i = 0; i < devices.Items.Count; i++)
                {
                    if (devices.Items[i].InstanceGuid == instanceGuid)
                    { exact = devices.Items[i]; break; }
                }
                // The flapped-unit rebind, HOISTED above every other
                // resolution (round eight, R11). A same-product,
                // SAME-SERIAL row still marked online while its claiming
                // wrapper's SDL instance has LEFT the present set is this
                // same physical unit re-identifying inside the disconnect
                // debounce: its old connection is gone, and one physical
                // device is never two present instances. It outranks the
                // exact-match return in BOTH directions: (1) when the
                // exact row is a live sibling (the twin collision), the
                // flapped twin must get its own row back; (2) when the
                // exact row is OFFLINE, adopting it would move a LIVE
                // unit onto different assignments mid-session, breaking
                // in-process identity stability. The serial constraint is
                // load-bearing (round eight): without it, a THIRD
                // same-model unit sitting inside its own disconnect
                // debounce was hijacked and the two units swapped
                // identities. Rows with no serial (path/sdlguid derived
                // identities) compare as empty == empty, which stays
                // inside the drawer policy for indistinguishable shells.
                //
                // Dispose truth (round eight, R12, correcting round
                // seven's claim): returning this row means
                // LoadFromSdlDevice swaps the wrapper IN PLACE and
                // disposes the stale one right there
                // (UserDevice.LoadFromDevice), the same flow every
                // exact-guid rebind has used since 54b572b9.
                // DeviceLiveUnderNewWrapper does NOT shield that dispose;
                // it is consulted only in the orphan sweep, which this
                // precedes. The two 2026-07-11 commits embody opposing
                // policies (dispose-at-rebind to keep handles off the
                // finalizer thread vs leave-to-finalizer to protect
                // shared fork HIDAPI contexts); dispose-at-rebind is the
                // long-established behavior, including the
                // hardware-validated Wii re-identify, so it stands.
                // WATCHED RESIDUAL: if fork-driver re-identify churn ever
                // recurs, the in-place dispose at rebind time is the
                // first suspect.
                // The scan runs ANCHOR-FREE (round nine, R7): it used to
                // require an exact-GUID row to read the incoming serial
                // from, so deleting a twin's offline sibling row (the
                // Devices page allows it, with no online gate) left a
                // flapped LIVE twin with no anchor, no zombie match, and
                // a freshly minted row that orphaned its own. The
                // wrapper's serial is the quantity the constraint always
                // meant; exact.SerialNumber was only ever a proxy for it.
                // Empty serials still compare equal to each other, which
                // keeps path-derived identities inside the drawer policy
                // for indistinguishable shells.
                string incomingSerial = serialNumber ?? exact?.SerialNumber ?? "";
                // A NON-EMPTY serial is required when there is no anchor
                // (round ten). Empty == empty carries zero identity
                // information, so anchor-free it matched ANY online
                // same-product row whose claimant had lapsed: a
                // genuinely new serialless pad, enumerated in the same
                // sweep as a momentarily-absent sibling, adopted that
                // sibling's row, mappings and calibration, and the two
                // pads swapped when the first returned. Round nine's
                // deleted-sibling fix is untouched by this: a twin
                // COLLISION only exists when the serial is non-empty,
                // because BuildInstanceGuid falls through to the
                // per-unit device path when it is empty, so two
                // serialless units never derive the same identity.
                // OPEN (round 37): the "exact != null" disjunct admits this
                // scan for SERIALLESS units, where the row test below
                // degenerates to "" == "". That is load-bearing for the case
                // FlappedTwin_InsideTheDebounce_RebindsToItsOwnRow pins (one
                // unit re-identifying inside the debounce must find its OWN
                // row, not mint a new one), and it is also what lets two
                // same-model serialless pads cross-bind when one is inside its
                // debounce while the other enumerates. Removing the disjunct
                // fixes the second and breaks the first, so the two cases need
                // separating on whether `exact` is a live-twin collision
                // rather than on the serial. Owner call, not a mechanical fix.
                // Is the row we would otherwise return already CLAIMED by a
                // live, present device? Computed here rather than after the
                // scan because the scan needs it: it is what separates the two
                // serialless cases, which the serial cannot.
                bool liveTwinCollision = exact != null
                    && livePresentSdlIds != null
                    && exact.IsOnline
                    && exact.Device != null
                    && livePresentSdlIds.Contains(exact.Device.SdlInstanceId);

                // With an EMPTY serial the row test below degenerates to
                // "" == "", so admitting this scan on "exact != null" alone let
                // any online same-product row whose SDL instance had lapsed
                // match. Two same-model serialless pads, one inside its 2 s
                // disconnect debounce while the other re-identified, cross-bound:
                // the returning unit was stamped with the absent unit's identity
                // and inherited its slot, mappings and calibration.
                //
                // The serial cannot separate that from the case round seven
                // pins (ONE unit re-identifying inside its own debounce must
                // find its own row), because both arrive with an empty serial.
                // liveTwinCollision can. Re-identifying, the exact row is held
                // by the live sibling, so returning it is impossible and the
                // scan is the only way home. Cross-binding, the exact row is
                // the arriving pad's OWN row with nobody on it, so there is
                // nothing to search for and the scan can only do harm.
                if (livePresentSdlIds != null && productGuid != Guid.Empty
                    && (liveTwinCollision || !string.IsNullOrEmpty(incomingSerial)))
                {
                    for (int i = 0; i < devices.Items.Count; i++)
                    {
                        var d = devices.Items[i];
                        if (d.IsOnline && d.ProductGuid == productGuid
                            && d.InstanceGuid != Guid.Empty
                            && d.InstanceGuid != instanceGuid
                            && d.Device != null
                            && !livePresentSdlIds.Contains(d.Device.SdlInstanceId)
                            && string.Equals(d.SerialNumber ?? "", incomingSerial,
                                StringComparison.Ordinal))
                            return d;
                    }
                }

                if (exact != null && !liveTwinCollision)
                    return exact;

                if (liveTwinCollision)
                {
                    // TWIN RESOLUTION (round seven R4/R5, zombie rebind
                    // hoisted above in round eight).
                    if (productGuid != Guid.Empty)
                    {
                        // (b) The drawer adoption, KEEPING the row's own
                        // identity (round seven, R5). The incoming
                        // serial-derived GUID is unusable here (it collides
                        // with the live sibling), and minting a fresh one
                        // per resolve re-keyed the twin EVERY LAUNCH: its
                        // per-device slot configs (lighting, triggers,
                        // audio) could never persist and grew a dead saved
                        // entry per launch, device-pinned mapping rows died
                        // on every reconnect, and Remote Link's
                        // PeerLocalDeviceId broke its documented stability
                        // contract. The adopted row's existing GUID is what
                        // its UserSettings and every other GUID-keyed store
                        // already reference, so identity is NOT restamped
                        // and nothing is migrated; the caller pushes the
                        // row's GUID onto the wrapper instead. Ordinary
                        // non-collision adoption keeps restamping, because
                        // there the incoming GUID is the device's true
                        // stable identity. The asymmetry is deliberate.
                        for (int i = 0; i < devices.Items.Count; i++)
                        {
                            var d = devices.Items[i];
                            // Empty-guid rows (corrupt persisted data) are
                            // never adopted as a twin identity (round
                            // eight, R11): the caller's wrapper override
                            // no-ops on Guid.Empty and the row would then
                            // be restamped with the COLLIDING serial GUID.
                            if (!d.IsOnline && d.ProductGuid == productGuid
                                && d.InstanceGuid != Guid.Empty)
                                return d;
                        }
                    }

                    // (c) First-ever twin: a session-minted identity,
                    // stable from the next launch on via (b). Same-serial
                    // hardware with NO usable product identity (VID/PID
                    // 0000) cannot take (a)/(b), because an Empty-product
                    // scan would match across device classes, so it
                    // re-mints per launch; accepted degenerate-hardware
                    // limitation. WATCHED RESIDUAL: if SDL ever listed a
                    // re-identifying device's old and new instance ids in
                    // ONE snapshot, the collision predicate above would
                    // read it as a live sibling and (a) would not match.
                    // No evidence SDL produces that shape; if
                    // single-device duplicate rows ever appear, this gate
                    // is the first suspect.
                    // ProductGuid stamped at creation (round eight, R11):
                    // LoadFromSdlDevice normally stamps it right after,
                    // but a failed load used to leave the row
                    // product-less and therefore invisible to every
                    // adoption scan forever.
                    var twin = new UserDevice
                    { InstanceGuid = Guid.NewGuid(), ProductGuid = productGuid };
                    devices.Items.Add(twin);
                    return twin;
                }

                // 2. Fallback: find an offline device with the same ProductGuid.
                //    This handles BT controllers that reconnect with a new device path.
                //    ProductGuid is VID+PID only, so it cannot tell two units of
                //    the same model apart, and that is DELIBERATE (owner decision,
                //    2026-07-25). Identical controllers are physically
                //    indistinguishable to their owner: you cannot tell which unit
                //    you pulled out of the drawer without labelling the shell. So
                //    identity here follows CONNECTION ORDER, not hardware. The
                //    first unit powered on claims the stored entry and its
                //    mappings, whichever unit it happens to be.
                //    A serial gate was tried here and REVERTED: it blocked the
                //    adoption when the serials differed, which is exactly the
                //    drawer case, and the second controller came up blank instead
                //    of inheriting the config the user had already built.
                //    Serial-pinned identity is right only where the user has a
                //    mental model of a specific pairing (DualShock 3, Wii), which
                //    is a separate, per-device-class decision and is NOT this
                //    generic path.
                if (productGuid != Guid.Empty)
                {
                    // No Guid.Empty exclusion here, unlike the twin lane
                    // above: on this ordinary path the row is RESTAMPED
                    // with the incoming identity, so adopting a corrupt
                    // empty-guid row repairs it instead of creating the
                    // duplicate the twin lane must avoid (where the
                    // wrapper override no-ops on Empty). The asymmetry is
                    // deliberate.
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

                        // Also migrate the linked UserSettings so slot
                        // assignments and PadSettings are preserved.
                        MigrateUserSettingGuid(oldGuid, instanceGuid);

                        // Device-PINNED references (mapping-row sources,
                        // activator legs, menu entries, per-pad slot
                        // configs) live in UI-owned structures this poll
                        // thread must not touch, so the re-key is queued
                        // and UpdatePadDeviceInfo drains it on the UI
                        // thread through the same remap helper
                        // ApplyProfile's rebind lane uses (round eight,
                        // R13: without this, a re-keyed device's pinned
                        // rows produced no output and its lighting reset).
                        lock (PendingDeviceGuidMigrationsLock)
                            PendingDeviceGuidMigrations.Add((oldGuid, instanceGuid));

                        return fallback;
                    }
                }

                // 3. No match: create a new device.
                var ud = new UserDevice { InstanceGuid = instanceGuid };
                devices.Items.Add(ud);
                return ud;
            }
        }

        /// <summary>Adoption re-keys queued by the poll thread for the UI
        /// thread to drain (round eight, R13). InputService's
        /// UpdatePadDeviceInfo rewrites the device-pinned mapping-row /
        /// activator / menu guids and moves the per-pad slot configs;
        /// those structures are UI-owned and must never be walked from
        /// here.</summary>
        internal static readonly System.Collections.Generic.List<(Guid Old, Guid New)>
            PendingDeviceGuidMigrations = new();
        internal static readonly object PendingDeviceGuidMigrationsLock = new();

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
                // A device may be assigned to several slots at once (one
                // UserSetting per slot, same InstanceGuid, different
                // MapTo), so EVERY matching row follows the device. The
                // old first-match break silently orphaned slots 2..N on
                // any adoption (round seven, R6; pre-existing since
                // v2.0.0-beta, its comment claimed one row per device).
                // When a (newGuid, MapTo) row ALREADY exists (an orphaned
                // setting colliding with the adoption target), the
                // existing destination row is the live truth and the old
                // row is dropped instead of rewritten, so the migration
                // can never manufacture duplicate (guid, slot) rows
                // (round eight, R5).
                var items = settings.Items;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i].InstanceGuid != oldGuid) continue;
                    int mapTo = items[i].MapTo;
                    bool twinExists = false;
                    for (int j = 0; j < items.Count; j++)
                    {
                        if (j != i && items[j].InstanceGuid == newGuid && items[j].MapTo == mapTo)
                        { twinExists = true; break; }
                    }
                    if (twinExists) items.RemoveAt(i);
                    else items[i].InstanceGuid = newGuid;
                }
            }
        }

        /// <summary>True when the physical device behind <paramref name="orphan"/>
        /// is still online through a DIFFERENT wrapper (the driver re-identify
        /// rebind: same InstanceGuid, new SDL instance id). Disposing the stale
        /// wrapper in that state closes SDL handles whose driver context is
        /// SHARED with the live instance.</summary>
        private static bool DeviceLiveUnderNewWrapper(SdlDeviceWrapper orphan)
        {
            var devs = SettingsManager.UserDevices?.Items;
            if (devs == null) return false;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                for (int i = 0; i < devs.Count; i++)
                {
                    var d = devs[i];
                    if (d == null || !d.IsOnline || d.Device == null) continue;
                    if (ReferenceEquals(d.Device, orphan)) continue;
                    if (d.InstanceGuid == orphan.InstanceGuid) return true;
                }
            }
            return false;
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

            NeutralizeMappedOutputsFor(ud);

            // Any confirmed disconnect can reshuffle the synthetic "XInput#N"
            // paths the impulse writer caches its handles under, and a stale
            // entry whose handle is still valid writes to the wrong pad
            // without ever failing into the self-heal path.
            XboxImpulseHidWriter.InvalidateCachedTargets();
        }

        /// <summary>Clears runtime state and neutralizes the device's per-slot
        /// mapped outputs. Step 3 skips offline devices and "keeps the last
        /// OutputState" (a guard against transient read glitches), so whatever
        /// was stamped on the final frames before a confirmed disconnect would
        /// otherwise persist for as long as the slot stays active: a detached
        /// pedal's recentered read (inverted trigger -> ~32767 = 50% engaged),
        /// or a button the user was holding at unplug. Step 4 copies
        /// OutputState into the slot's combined output and the per-device
        /// Triggers/Sticks preview reads RawMappedState, so both must go
        /// neutral (Gamepad default: triggers released, sticks centered).
        ///
        /// <para>Split out of MarkDeviceOffline so the MIDI and NFC teardown
        /// lanes can share it. Those two dispose their own endpoint object and
        /// so cannot call MarkDeviceOffline (it would double-dispose), which is
        /// exactly why they were silently skipping this step and freezing a
        /// held note or CC into the slot after the endpoint vanished.</para></summary>
        private static void NeutralizeMappedOutputsFor(UserDevice ud)
        {
            if (ud == null) return;
            ud.ClearRuntimeState();

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

        // Sony headset head trackers (Phase 1g, issue #188). Discovery,
        // qualification (Bluetooth feature-report reads) and the enable
        // sequence are all blocking device I/O, so the worker does the
        // whole open and the poll thread only registers finished devices
        // and retires vanished ones.
        private readonly Dictionary<string, SonyHeadsetMotionDevice> _openedHeadsets =
            new Dictionary<string, SonyHeadsetMotionDevice>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SonyHeadsetMotionDevice> _headsetPendingRegister =
            new List<SonyHeadsetMotionDevice>();
        private readonly object _headsetLock = new object();
        private volatile bool _headsetSweepRunning;
        private volatile bool _headsetInputsSuppressed;
        // Latest sweep's present qualified paths; null until the first
        // sweep completes so nothing is retired on a cold cache.
        private volatile HashSet<string> _headsetPresentPaths;
        private long _headsetNextSweepTicks;
        private readonly Dictionary<string, long> _headsetOpenFailedAt =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // Wall-clock cadence like the NFC retry throttle: the sweep opens
        // handles across the HID tree, so it runs well below the ~2 s
        // device-sweep rate.
        private const int _headsetSweepIntervalMs = 3000;
        private const int _headsetOpenRetryMs = 60000;

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
                        // Same neutralize the unplug path performs. This is not
                        // only an app-shutdown path: it also runs before a
                        // Windows MIDI Services uninstall, with the app still
                        // live, and Step 3 keeps the last OutputState for an
                        // offline device. A note or CC held at that moment
                        // stayed stamped on the slot's combined output.
                        NeutralizeMappedOutputsFor(ud);
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
        private readonly Dictionary<string, long> _midiOpenFailedAt = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Closes any open loopback input connections to the
        /// given PadForge MIDI endpoint. MUST run before that endpoint's
        /// device-side teardown: tearing down a virtual endpoint while
        /// this process still holds a client connection to it is the
        /// deterministic midisrv wedge (bench 2026-07-23: every switch
        /// away from a working MIDI slot with the loopback open left the
        /// service hung past SCM control). Callers demote the endpoint's
        /// registry claim first (MidiVirtualController.MarkClosing) so
        /// the scanner cannot reopen it in this window.</summary>
        internal void CloseMidiInputsForEndpoint(string uniqueEndpointId)
        {
            if (string.IsNullOrEmpty(uniqueEndpointId)) return;
            lock (_midiInputsLock)
            {
                List<string> matches = null;
                foreach (var kvp in _openedMidiInputs)
                    if (kvp.Key.IndexOf(uniqueEndpointId, StringComparison.OrdinalIgnoreCase) >= 0)
                        (matches ??= new List<string>()).Add(kvp.Key);
                if (matches == null) return;
                foreach (var id in matches)
                {
                    var dev = _openedMidiInputs[id];
                    var ud = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid);
                    if (ud != null)
                    {
                        ud.IsOnline = false;
                        ud.Device = null;
                        // Closing one endpoint while the app runs on. Same
                        // neutralize the unplug path performs, for the same
                        // reason: without it a held note or CC survives the
                        // close on the slot's combined output.
                        NeutralizeMappedOutputsFor(ud);
                    }
                    dev.Dispose();
                    _openedMidiInputs.Remove(id);
                }
            }
        }

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

                long midiNow = Environment.TickCount64;
                foreach (var (id, name) in endpoints)
                {
                    current.Add(id);

                    // PadForge's own virtual-controller endpoints (the
                    // documented no-hardware loopback path) open ONLY while
                    // the owning MidiVirtualController in this process
                    // reports its device side fully connected. That is the
                    // authoritative state, not a name-plus-settle-time
                    // heuristic: a PadForge-shaped endpoint with no ready
                    // owner is either mid-create (the owner will flag ready
                    // when the service finishes) or a corpse stranded by a
                    // failed service-side teardown, and opening a corpse
                    // re-animates it inside the service (see
                    // MidiEndpointJanitor, which removes them instead).
                    if (MidiEndpointJanitor.IsPadForgeEndpointId(id)
                        && !_openedMidiInputs.ContainsKey(id)
                        && !MidiVirtualController.IsReadyEndpointInstance(id))
                        continue;

                    // A recent failed open backs off instead of re-poking a
                    // sick service every sweep.
                    if (_midiOpenFailedAt.TryGetValue(id, out long failedAt)
                        && midiNow - failedAt < 60_000)
                        continue;

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
                            _midiOpenFailedAt[id] = midiNow;
                            continue;
                        }
                        _midiOpenFailedAt.Remove(id);

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

                // Forget cooldown tracking for vanished ids so a
                // re-created endpoint starts fresh.
                if (_midiOpenFailedAt.Count > 0)
                {
                    List<string> stale = null;
                    foreach (var key in _midiOpenFailedAt.Keys)
                        if (!current.Contains(key)) (stale ??= new List<string>()).Add(key);
                    if (stale != null) foreach (var key in stale) _midiOpenFailedAt.Remove(key);
                }

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
                            // Same neutralize MarkDeviceOffline performs. Without
                            // it a note or CC held when the endpoint vanished
                            // stayed stamped on the slot's combined output.
                            NeutralizeMappedOutputsFor(ud);
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
                        // Same neutralize the reader-removed path performs.
                        // Step 3 keeps the last OutputState for an offline
                        // device, so a tag-presence input asserted at teardown
                        // stayed stamped on the slot's combined output.
                        NeutralizeMappedOutputsFor(ud);
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
                            // Same neutralize MarkDeviceOffline performs. Without
                            // it a note or CC held when the endpoint vanished
                            // stayed stamped on the slot's combined output.
                            NeutralizeMappedOutputsFor(ud);
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
        /// Phase 1g: registers Sony headset head trackers (issue #188),
        /// mirroring the MIDI sweep shape. Discovery, the marker probe and
        /// the enable-sequence feature writes are Bluetooth I/O, so a
        /// worker performs the entire enumerate-and-open; this poll-thread
        /// phase only registers finished devices and retires ones whose
        /// HID node vanished or whose reader thread died.
        /// </summary>
        private bool UpdateHeadsetMotionDevices()
        {
            if (_headsetInputsSuppressed)
                return false;

            long now = Environment.TickCount64;
            if (!_headsetSweepRunning && now >= _headsetNextSweepTicks)
            {
                _headsetSweepRunning = true;
                _headsetNextSweepTicks = now + _headsetSweepIntervalMs;
                Task.Run(() =>
                {
                    try { HeadsetMotionSweep(); }
                    catch { }
                    finally { _headsetSweepRunning = false; }
                });
            }

            bool changed = false;
            var present = _headsetPresentPaths;
            lock (_headsetLock)
            {
                // Re-check under the lock: shutdown may have latched while
                // the worker was finishing (the MIDI suppress pattern).
                if (_headsetInputsSuppressed)
                    return false;

                // Register devices the worker finished opening.
                for (int i = 0; i < _headsetPendingRegister.Count; i++)
                {
                    var dev = _headsetPendingRegister[i];
                    try
                    {
                        UserDevice ud = FindOrCreateUserDevice(dev.InstanceGuid, dev.ProductGuid);
                        ud.LoadFromExternalDevice(dev);
                        ud.IsOnline = true;
                        _openedHeadsets[dev.DevicePath] = dev;
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        dev.Dispose();
                        RaiseError($"Error registering headset tracker '{dev.Name}'", ex);
                    }
                }
                _headsetPendingRegister.Clear();

                // Retire vanished nodes, dead readers, and rows the user
                // removed from the Devices page (the MIDI recreate pattern:
                // dropping the tracking entry lets the next sweep re-open).
                List<string> gone = null;
                foreach (var kvp in _openedHeadsets)
                {
                    bool vanished = present != null && !present.Contains(kvp.Key);
                    bool dead = !kvp.Value.IsAttached;
                    bool removedByUser = FindOnlineDeviceByInstanceGuid(kvp.Value.InstanceGuid) == null;
                    if (vanished || dead || removedByUser)
                        (gone ??= new List<string>()).Add(kvp.Key);
                }
                if (gone != null)
                {
                    foreach (var path in gone)
                    {
                        var dev = _openedHeadsets[path];
                        var ud = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid);
                        if (ud != null)
                        {
                            ud.IsOnline = false;
                            ud.Device = null;
                            // Same neutralize the MIDI/NFC retire paths
                            // perform: a gyro deflection held at vanish time
                            // would stay stamped on the slot's output.
                            NeutralizeMappedOutputsFor(ud);
                        }
                        dev.Dispose();
                        _openedHeadsets.Remove(path);
                        changed = true;
                    }
                }

                // Forget open-failure cooldowns for vanished paths so a
                // re-created node starts fresh.
                if (present != null && _headsetOpenFailedAt.Count > 0)
                {
                    List<string> stale = null;
                    foreach (var key in _headsetOpenFailedAt.Keys)
                        if (!present.Contains(key)) (stale ??= new List<string>()).Add(key);
                    if (stale != null) foreach (var key in stale) _headsetOpenFailedAt.Remove(key);
                }
            }
            return changed;
        }

        /// <summary>Worker half of Phase 1g: enumerate qualified trackers,
        /// open and configure the new ones, queue them for poll-thread
        /// registration. All blocking I/O lives here.</summary>
        private void HeadsetMotionSweep()
        {
            var candidates = SonyHeadsetMotionRuntime.Enumerate();
            if (candidates == null)
                return; // enumeration failed; keep the previous snapshot
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates) present.Add(c.Path);
            _headsetPresentPaths = present;

            long now = Environment.TickCount64;
            foreach (var candidate in candidates)
            {
                lock (_headsetLock)
                {
                    if (_headsetInputsSuppressed) return;
                    if (_openedHeadsets.ContainsKey(candidate.Path)) continue;
                    bool pending = false;
                    for (int i = 0; i < _headsetPendingRegister.Count; i++)
                        if (string.Equals(_headsetPendingRegister[i].DevicePath, candidate.Path,
                                StringComparison.OrdinalIgnoreCase)) { pending = true; break; }
                    if (pending) continue;
                    if (_headsetOpenFailedAt.TryGetValue(candidate.Path, out long failedAt)
                        && now - failedAt < _headsetOpenRetryMs)
                        continue;
                }

                var dev = new SonyHeadsetMotionDevice(candidate);
                bool ok = false;
                try { ok = dev.Open(); }
                catch { }
                lock (_headsetLock)
                {
                    if (_headsetInputsSuppressed) { dev.Dispose(); return; }
                    if (!ok)
                    {
                        dev.Dispose();
                        _headsetOpenFailedAt[candidate.Path] = now;
                        continue;
                    }
                    _headsetOpenFailedAt.Remove(candidate.Path);
                    _headsetPendingRegister.Add(dev);
                }
            }
        }

        /// <summary>
        /// Tears down every open headset tracker and suppresses Phase 1g.
        /// Called on app shutdown alongside ShutdownNfcReaders.
        /// </summary>
        public void ShutdownHeadsetMotionInputs()
        {
            _headsetInputsSuppressed = true;
            lock (_headsetLock)
            {
                foreach (var kvp in _openedHeadsets)
                {
                    var ud = FindOnlineDeviceByInstanceGuid(kvp.Value.InstanceGuid);
                    if (ud != null)
                    {
                        ud.IsOnline = false;
                        ud.Device = null;
                        NeutralizeMappedOutputsFor(ud);
                    }
                    kvp.Value.Dispose();
                }
                _openedHeadsets.Clear();
                foreach (var dev in _headsetPendingRegister)
                    dev.Dispose();
                _headsetPendingRegister.Clear();
            }
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
                bool changed = restricted
                    ? _restrictedDevices.Add(instanceGuid)
                    : _restrictedDevices.Remove(instanceGuid);
                if (changed) _restrictedSnapshotCache = null;
            }
        }

        // Rebuilt only when the set changes: while a Remote Link
        // gamepad-only restriction was active, the old shape allocated a
        // Guid[] per poll tick (menu walk + per-macro-slot checks).
        private Guid[] _restrictedSnapshotCache;

        /// <summary>Snapshot of restricted device GUIDs, or null when none (early-out).</summary>
        private Guid[] RestrictedSnapshot()
        {
            lock (_restrictedLock)
            {
                if (_restrictedDevices.Count == 0) return null;
                var cached = _restrictedSnapshotCache;
                if (cached != null) return cached;
                var a = new Guid[_restrictedDevices.Count];
                _restrictedDevices.CopyTo(a);
                _restrictedSnapshotCache = a;
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
            // MarkDeviceOffline takes the UserSettings lock to neutralize
            // the device's per-slot outputs. Nesting devices->settings here
            // would be legal under the lock canon (devices before settings,
            // never the reverse; no Settings-first nesting site exists in
            // the repo as of the 2026-07-20 audit sweep), but the marking
            // needs no collection lock, so keep it outside on principle.
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

            // Drop the device's menu runtime contexts NOW: a fired context
            // stays credible for the staleness window, and a restricted
            // peer's restriction is cleared at disconnect, so leaving the
            // context alive allowed one last key injection after the gate
            // was gone.
            PurgeMenuContextsForDevice(instanceGuid);

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
