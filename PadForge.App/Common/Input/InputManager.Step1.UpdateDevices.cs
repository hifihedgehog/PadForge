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

        /// <summary>Cycle counter for throttled XInput slot snapshots written
        /// to the diagnostic log. At ~1 kHz polling, 1000 cycles ≈ 1 second.</summary>
        private int _xiSnapshotCycles;
        private const int XiSnapshotIntervalCycles = 2000;

        // Keyboard/mouse tracking moved to _openedKeyboardHandles / _openedMouseHandles
        // (Raw Input IntPtr handles instead of SDL uint IDs).

        /// <summary>
        /// SDL instance IDs identified as virtual controllers (HIDMaestro
        /// today; v2-era ViGEm paths also match as defense-in-depth for
        /// upgrading users pre-cleanup). These are skipped entirely on
        /// subsequent enumeration cycles to avoid the open/close cycle that
        /// resets XInput rumble state — SDL3's close internally calls
        /// XInputSetState(0,0) on the device's XInput slot, which would
        /// trigger the virtual's feedback callback with zero motors and
        /// kill active vibration.
        /// </summary>
        private readonly HashSet<uint> _filteredVirtualInstanceIds = new HashSet<uint>();

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
        private bool _orphanSweepAwaited;

        private void UpdateDevices()
        {
            if (!_sdlInitialized)
                return;

            // Ensure the startup orphan-sweep task has finished before we
            // enumerate — the sweep runs in App.OnStartup on a background
            // thread so the main window can present immediately, but we
            // must not call SDL_GetJoysticks before kernel-side orphans
            // are gone (otherwise they show up in the Devices list and
            // xinputhid-backed orphans leak through SDL's XInput backend).
            // Wait runs here on the polling thread, never on the UI
            // thread, so any kernel-cleanup latency no longer freezes
            // window rendering.
            if (!_orphanSweepAwaited)
            {
                _orphanSweepAwaited = true;
                try { App.OrphanSweepTask?.Wait(); }
                catch { /* sweep failures already swallowed inside the task */ }
            }

            bool changed = false;

            // Targeted close for masked-slot leaks only (S2 invariant:
            // physical controller input must never be interrupted by virtual
            // lifecycle events). With pre-mask in Step 5 the virtual slot is
            // hidden from SDL BEFORE the virtual's kernel device exists, so
            // SDL cannot have the virtual in its joystick list. If an open
            // handle is nevertheless sitting at a currently-masked XInput
            // slot, that's either a pre-mask prediction miss or a driver
            // race — close it so stale virtual state cannot deliver input.
            // Physical handles at unmasked slots are left untouched.
            if (_sdlJoysticksNeedReopen)
            {
                _sdlJoysticksNeedReopen = false;
                int currentMask = XInputHook.IsInstalled ? XInputHook.IgnoreSlotMask : 0;
                if (currentMask != 0)
                {
                    var sdlIds = _openedSdlInstanceIds.ToArray();
                    foreach (uint sid in sdlIds)
                    {
                        string p = SDL_GetJoystickPathForID(sid) ?? string.Empty;
                        if (!p.StartsWith("XInput#", StringComparison.OrdinalIgnoreCase)
                            || p.Length <= 7
                            || !int.TryParse(p.Substring(7), out int slot)
                            || (currentMask & (1 << slot)) == 0)
                        {
                            continue;
                        }
                        var u = FindOnlineDeviceBySdlInstanceId(sid);
                        if (u?.Device != null)
                        {
                            try { u.Device.Dispose(); } catch { }
                            u.Device = null;
                        }
                        _openedSdlInstanceIds.Remove(sid);
                    }
                }
            }

            // Revalidate the HIDMaestro-filtered SDL IDs every cycle: if the
            // underlying XInput slot is no longer masked by the hook (e.g.
            // the virtual was destroyed or xinputhid reshuffled a physical
            // into that slot), remove the ID from the filter cache so the
            // real device is re-opened on this pass. Authoritative mask is
            // consulted directly — _hiddenXInputSlot can go stale when
            // xinputhid reshuffles without our create/destroy triggering,
            // which would otherwise leave a legitimate physical wedged in
            // the filter cache forever.
            int curHookMask = XInputHook.IsInstalled ? XInputHook.IgnoreSlotMask : 0;
            var cached = _filteredVirtualInstanceIds.ToArray();
            foreach (uint cid in cached)
            {
                string cp = SDL_GetJoystickPathForID(cid) ?? string.Empty;
                if (cp.StartsWith("XInput#", StringComparison.OrdinalIgnoreCase)
                    && cp.Length > 7
                    && int.TryParse(cp.Substring(7), out int cslot)
                    && cslot >= 0 && cslot < 4
                    && (curHookMask & (1 << cslot)) == 0)
                {
                    _filteredVirtualInstanceIds.Remove(cid);
                }
            }

            // Authoritative hook-mask recompute — runs every Step 1 pass.
            //
            // Instead of letting the mask accumulate incremental updates from
            // Step 5 (create/destroy), RESET it from scratch here against
            // current kernel state. For each XInput slot 0-3:
            //   - Unoccupied: bit clear.
            //   - HIDMaestro HID child exists AND no real shares VID/PID:
            //     bit set. Catches orphans from prior force-killed processes,
            //     our own virtuals wherever xinputhid placed them, and
            //     reshuffled virtuals that our create-time tracking lost
            //     track of.
            //   - Ambiguous (both HM and real HID children match VID/PID,
            //     e.g. xbox-series-xs-bt virtual coexisting with a real
            //     Xbox Series BT): consult Step 5's _hiddenXInputSlot map.
            //     Only mask when a pad explicitly claims this slot; otherwise
            //     leave clear so the physical stays visible.
            //   - Real-only (no HM HID child): bit clear.
            //
            // Net result: mask can never strand a bit across swaps. A stale
            // slot that no longer has a HM virtual simply doesn't set a bit
            // on this pass.
            if (XInputHook.IsInstalled)
            {
                int prevMask = XInputHook.IgnoreSlotMask;
                int desiredMask = 0;
                System.Text.StringBuilder snapSb = null;

                // Snapshot every slot's CapsEx identity + packet count once
                // up-front so the group-based ambiguous logic below can pick
                // by pkt ranking.
                var slotVid = new ushort[4];
                var slotPid = new ushort[4];
                var slotPkt = new uint[4];
                var slotOccupied = new bool[4];
                for (int s = 0; s < 4; s++)
                {
                    int rc = XInputHook.GetStateOriginal(s, out var st);
                    slotOccupied[s] = rc == 0;
                    slotPkt[s] = st.dwPacketNumber;
                    if (rc == 0)
                    {
                        PadForge.Engine.StableXInputInstance.TryGetXInputSlotVidPid(
                            (uint)s, out slotVid[s], out slotPid[s]);
                    }
                }

                // Count how many Microsoft-type HM virtual controllers
                // currently have each profile VID/PID alive in-process. This
                // tells us how many slots in each VID/PID group SHOULD be
                // masked as virtuals. The rest of matching slots are either
                // physicals or stale — distinguished by packet count below.
                //
                // Pending-clear slots are NOT folded into this count —
                // they represent specific slots, not a pooled count of
                // "how many dying virtuals exist with this VID/PID."
                // Inflating the count would cause the group-ranking loop
                // to mask the physical (the only remaining slot in the
                // group) once the pending slot has been torn down by
                // xinputhid. Pending masking is handled per-slot below.
                var expectedVirtualsPerVidPid = new System.Collections.Generic.Dictionary<(ushort, ushort), int>();
                for (int p = 0; p < MaxPads; p++)
                {
                    var vc = _virtualControllers[p];
                    if (vc is HMaestroVirtualController hmp
                        && vc.Type == VirtualControllerType.Microsoft)
                    {
                        var key = (hmp.ProfileVendorId, hmp.ProfileProductId);
                        if (!expectedVirtualsPerVidPid.TryGetValue(key, out int cnt)) cnt = 0;
                        expectedVirtualsPerVidPid[key] = cnt + 1;
                    }
                }

                // Walk VID/PID groups. Within each group, the N lowest-pkt
                // slots are the virtuals (pkt is a reliable signal: fresh
                // virtual pkt is near 0; seasoned physical pkt is ≫100).
                var processed = new System.Collections.Generic.HashSet<(ushort, ushort)>();
                for (int s = 0; s < 4; s++)
                {
                    if (!slotOccupied[s]) continue;
                    var key = (slotVid[s], slotPid[s]);
                    if (key == ((ushort)0, (ushort)0)) continue;
                    if (!processed.Add(key)) continue;

                    // Collect all slots in this group.
                    var groupSlots = new System.Collections.Generic.List<int>(4);
                    for (int t = 0; t < 4; t++)
                    {
                        if (slotOccupied[t] && slotVid[t] == key.Item1 && slotPid[t] == key.Item2)
                            groupSlots.Add(t);
                    }

                    string hm = null, real = null;
                    try { hm = PadForge.Engine.StableXInputInstance.FindHidMaestroChild(key.Item1, key.Item2); } catch { }
                    try { real = PadForge.Engine.StableXInputInstance.Find(key.Item1, key.Item2); } catch { }
                    bool knownHmVidPid = PadForge.Engine.StableXInputInstance
                        .IsKnownHidMaestroVidPid(key.Item1, key.Item2);
                    bool hasHm = !string.IsNullOrEmpty(hm) || knownHmVidPid;
                    bool hasReal = !string.IsNullOrEmpty(real);

                    if (!hasHm)
                    {
                        // No HM signal at all. None of these slots are ours.
                        continue;
                    }

                    if (!hasReal)
                    {
                        // Pure HM group — every slot in this group is a
                        // virtual. Mask all.
                        foreach (int t in groupSlots) desiredMask |= (1 << t);
                        continue;
                    }

                    // Ambiguous: at least one real shares VID/PID. Count
                    // how many slots should be masked (bounded by expected
                    // virtuals and actual slot count), and pick the lowest-
                    // packet-count slots — those are our virtuals. The
                    // leftover high-pkt slots are physicals and stay visible.
                    if (!expectedVirtualsPerVidPid.TryGetValue(key, out int expected))
                        expected = 0;
                    // Clamp: can't mask more slots than exist in this group,
                    // and can't mask more than our in-process tracking says
                    // we have virtuals.
                    if (expected > groupSlots.Count) expected = groupSlots.Count;
                    if (expected <= 0) continue;

                    groupSlots.Sort((a, b) => slotPkt[a].CompareTo(slotPkt[b]));
                    for (int i = 0; i < expected; i++)
                    {
                        desiredMask |= (1 << groupSlots[i]);
                    }
                }

                // Pending-clear pass (per-slot).
                //
                // For each slot with a pending-clear bit armed (meaning we
                // recently destroyed a Microsoft virtual that owned this
                // slot), decide mask inclusion based on CURRENT slot state:
                //   - Slot is kernel-empty: xinputhid teardown flicker —
                //     keep the bit set so a brief re-occupancy can't leak.
                //   - Slot is occupied with the destroyed virtual's
                //     profile VID/PID: dying virtual is still kernel-bound
                //     here — keep masked.
                //   - Slot is occupied with a DIFFERENT VID/PID:
                //     something else (physical, different virtual) now
                //     owns this slot. Don't mask via pending; the group-
                //     ranking pass above has already made the correct
                //     decision for whatever is now there.
                for (int s = 0; s < 4; s++)
                {
                    if (!_pendingXInputMaskClearSlots[s]) continue;
                    if (!slotOccupied[s])
                    {
                        desiredMask |= (1 << s);
                        continue;
                    }
                    var prof = _pendingXInputMaskClearProfile[s];
                    if ((prof.vid == slotVid[s] && prof.pid == slotPid[s])
                        || (prof.vid == 0 && prof.pid == 0))
                    {
                        desiredMask |= (1 << s);
                    }
                }

                if (desiredMask != prevMask)
                {
                    XInputHook.SetIgnoreSlotMask(desiredMask);
                    if (snapSb == null) snapSb = new System.Text.StringBuilder();
                    snapSb.AppendLine($"[AuthMask @ {DateTime.Now:HH:mm:ss.fff}] 0x{prevMask:X} -> 0x{desiredMask:X}");
                    try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge-sort-diag.log"), snapSb.ToString()); } catch { }
                }
            }

            // Throttled XInput slot snapshot for visibility (every N cycles).
            if (++_xiSnapshotCycles >= XiSnapshotIntervalCycles)
            {
                _xiSnapshotCycles = 0;
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[XI-snapshot @ {DateTime.Now:HH:mm:ss.fff}] mask=0x{(XInputHook.IsInstalled ? XInputHook.IgnoreSlotMask : 0):X} ");
                    for (int s = 0; s < 4; s++)
                    {
                        int rc = XInputHook.IsInstalled ? XInputHook.GetStateOriginal(s, out _) : 0x048F;
                        bool masked = XInputHook.IsInstalled && (XInputHook.IgnoreSlotMask & (1 << s)) != 0;
                        sb.Append($"s{s}={(rc == 0 ? "conn" : "none")}{(masked ? "M" : "")} ");
                    }
                    sb.AppendLine();
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge-sort-diag.log"),
                        sb.ToString());
                }
                catch { }
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
                    if (_filteredVirtualInstanceIds.Contains(instanceId))
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

                    // XInput#N escape hatch for orphans and startup races:
                    // IsHidMaestroAncestor's XInput# branch only matches slots
                    // currently in _hiddenXInputSlot. A virtual left over from
                    // a prior PadForge session (force-killed, pre-sweep, etc.)
                    // does NOT appear in _hiddenXInputSlot on this launch, so
                    // the ancestor check falls through. Catch that case by
                    // asking whether a HIDMaestro HID child with the SDL
                    // device's VID/PID exists in the PnP tree. If yes AND no
                    // real (non-HM) HID child shares the VID/PID, the SDL
                    // device at this XInput slot MUST be one of our virtuals
                    // that slipped the mask. Filter it. When both HM and
                    // non-HM children exist (user has a real sharing VID/PID
                    // with a HIDMaestro profile, e.g. real Xbox 360 alongside
                    // xbox-360-wired virtual), the hook mask remains the
                    // authoritative signal — don't guess here.
                    if (!hmMatch
                        && !string.IsNullOrEmpty(prePath)
                        && prePath.StartsWith("XInput#", StringComparison.OrdinalIgnoreCase))
                    {
                        ushort vid = SDL_GetJoystickVendorForID(instanceId);
                        ushort pid = SDL_GetJoystickProductForID(instanceId);
                        if (vid != 0 || pid != 0)
                        {
                            string hmChild = null;
                            string realChild = null;
                            try { hmChild = PadForge.Engine.StableXInputInstance.FindHidMaestroChild(vid, pid); } catch { }
                            try { realChild = PadForge.Engine.StableXInputInstance.Find(vid, pid); } catch { }
                            if (!string.IsNullOrEmpty(hmChild) && string.IsNullOrEmpty(realChild))
                            {
                                Debug.WriteLine($"[Step1] Pre-open filtered orphan HIDMaestro XInput device: SDL#{instanceId} path={prePath} VID={vid:X4} PID={pid:X4} hmChild={hmChild}");
                                hmMatch = true;
                            }
                        }
                    }

                    if (hmMatch)
                    {
                        Debug.WriteLine($"[Step1] Pre-open filtered HIDMaestro device: SDL#{instanceId} path={prePath}");
                        _filteredVirtualInstanceIds.Add(instanceId);
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
                    if (IsHidMaestroVirtualDevice(wrapper))
                    {
                        Debug.WriteLine($"[Step1] Post-open filtered HIDMaestro device: SDL#{instanceId} VID={wrapper.VendorId:X4} PID={wrapper.ProductId:X4} path={wrapper.DevicePath} name={wrapper.Name}");
                        _filteredVirtualInstanceIds.Add(instanceId);
                        wrapper.Dispose();
                        continue;
                    }

                    Debug.WriteLine($"[Step1] Accepted device: SDL#{instanceId} VID={wrapper.VendorId:X4} PID={wrapper.ProductId:X4} path={wrapper.DevicePath} name={wrapper.Name}");

                    try
                    {
                        int hookMaskAtAccept = XInputHook.IsInstalled ? XInputHook.IgnoreSlotMask : -1;
                        string hookState = XInputHook.IsInstalled ? $"mask=0x{hookMaskAtAccept:X}" : "NOT_INSTALLED";
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge-sort-diag.log"),
                            $"[Step1-accepted @ {DateTime.Now:HH:mm:ss.fff}] SDL#{instanceId} VID={wrapper.VendorId:X4} PID={wrapper.ProductId:X4} path='{wrapper.DevicePath}' name='{wrapper.Name}' InstanceGuid={wrapper.InstanceGuid} Serial='{wrapper.SerialNumber}' hook[{hookState}]\n");
                    }
                    catch { }

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
                    // UserDevice itself is gone — no handle to preserve.
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

            // Clean up ViGEm IDs that are no longer present (virtual controller destroyed).
            _filteredVirtualInstanceIds.IntersectWith(currentInstanceIds);

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
        private bool IsHidMaestroVirtualDevice(SdlDeviceWrapper wrapper)
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
            // there's no PnP tree to walk. Consult the authoritative hook
            // mask: whatever bits are currently set IS the definition of
            // "this slot is PadForge's virtual." The Step 1 AuthMask pass
            // keeps those bits in sync with current kernel state (including
            // pkt-based ranking for shared-VID/PID groups), so checking it
            // here matches what SDL sees at its own XInput read — no stale
            // _hiddenXInputSlot to trip over when xinputhid reshuffles.
            if (symlinkPath != null
                && symlinkPath.StartsWith("XInput#", StringComparison.OrdinalIgnoreCase)
                && symlinkPath.Length > 7
                && int.TryParse(symlinkPath.Substring(7), out int xiSlot)
                && xiSlot >= 0 && xiSlot < 4
                && XInputHook.IsInstalled
                && (XInputHook.IgnoreSlotMask & (1 << xiSlot)) != 0)
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
