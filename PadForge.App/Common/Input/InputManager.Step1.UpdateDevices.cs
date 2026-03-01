using System;
using System.Collections.Generic;
using System.Diagnostics;
using PadForge.Engine;
using PadForge.Engine.Data;
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

            // --- Phase 1b: Enumerate keyboards ---
            changed |= EnumerateKeyboards();

            // --- Phase 1c: Enumerate mice ---
            changed |= EnumerateMice();

            // --- Phase 2: Detect disconnected devices ---
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

            // Detect disconnected keyboards.
            changed |= DetectDisconnectedHandles(_openedKeyboardHandles, RawInputListener.EnumerateKeyboards());

            // Detect disconnected mice.
            changed |= DetectDisconnectedHandles(_openedMouseHandles, RawInputListener.EnumerateMice());

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

        /// <summary>
        /// Tracked Raw Input mouse device handles.
        /// </summary>
        private readonly HashSet<IntPtr> _openedMouseHandles = new HashSet<IntPtr>();

        /// <summary>
        /// Enumerates connected keyboards via Raw Input and creates UserDevice
        /// records for any new keyboards. Returns true if a new keyboard was found.
        /// </summary>
        private bool EnumerateKeyboards()
        {
            // Prune tracked handles whose UserDevice was removed (e.g. via UI "Remove").
            PruneOrphanedHandles(_openedKeyboardHandles);

            var keyboards = RawInputListener.EnumerateKeyboards();
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
        /// Enumerates connected mice via Raw Input and creates UserDevice
        /// records for any new mice. Returns true if a new mouse was found.
        /// </summary>
        private bool EnumerateMice()
        {
            // Prune tracked handles whose UserDevice was removed (e.g. via UI "Remove").
            PruneOrphanedHandles(_openedMouseHandles);

            var mice = RawInputListener.EnumerateMice();
            bool changed = false;

            foreach (var mouse in mice)
            {
                if (_openedMouseHandles.Contains(mouse.Handle))
                    continue;

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
            bool changed = false;

            foreach (IntPtr handle in trackedHandles)
            {
                if (!currentSet.Contains(handle))
                {
                    // Find by InstanceGuid (built from device path, same as wrapper).
                    UserDevice ud = FindOnlineDeviceByHandle(handle);
                    if (ud != null)
                    {
                        MarkDeviceOffline(ud);
                        changed = true;
                    }
                    disconnected.Add(handle);
                }
            }

            foreach (IntPtr handle in disconnected)
                trackedHandles.Remove(handle);

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
        /// Returns all UserSettings assigned to a specific pad slot (0–3).
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
