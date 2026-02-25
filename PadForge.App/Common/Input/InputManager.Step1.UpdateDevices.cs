using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
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

        /// <summary>Tracked keyboard SDL instance IDs.</summary>
        private readonly HashSet<uint> _openedKeyboardIds = new HashSet<uint>();

        /// <summary>Tracked mouse SDL instance IDs.</summary>
        private readonly HashSet<uint> _openedMouseIds = new HashSet<uint>();

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

            // Reset per-cycle caches for ViGEm PnP detection.
            _vigemPnPCount = -1;
            _vigemDs4PnPCount = -1;

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
                        _filteredVigemInstanceIds.Add(instanceId);
                        wrapper.Dispose();
                        continue;
                    }

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
            changed |= DetectDisconnected(_openedKeyboardIds, SDL_GetKeyboards());

            // Detect disconnected mice.
            changed |= DetectDisconnected(_openedMouseIds, SDL_GetMice());

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
        /// Cached count of ViGEm Xbox 360 devices from PnP detection.
        /// Refreshed once per enumeration cycle (not per device).
        /// -1 = not yet computed this cycle.
        /// </summary>
        private int _vigemPnPCount = -1;
        private int _xbox360FilteredThisCycle;
        private int _vigemDs4PnPCount = -1;
        private int _ds4FilteredThisCycle;

        /// <summary>
        /// Checks whether an SDL device is a ViGEm virtual controller
        /// (our own output device that must not be opened as an input device).
        ///
        /// Detection methods:
        ///   1. Device path containing ViGEm signatures
        ///   2. Zero VID/PID + SDL game controller (likely virtual)
        ///   3. Xbox 360 VID/PID (045E:028E) — uses PnP device tree walk
        ///      to distinguish real Xbox 360 controllers from ViGEm emulation
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
            // have ViGEm virtual controllers active, it's very likely virtual.
            if (wrapper.VendorId == 0 && wrapper.ProductId == 0)
            {
                if (_activeVigemCount > 0 && wrapper.IsGameController)
                    return true;
            }

            // ── Xbox 360 VID/PID — ViGEm emulates exactly this ──
            // Real Xbox 360 controllers and ViGEm virtual controllers both
            // report VID=045E PID=028E. We distinguish them by walking the
            // Windows PnP device tree: ViGEm devices have ViGEmBus as an
            // ancestor. The PnP count is cached per enumeration cycle.
            if (wrapper.VendorId == 0x045E && wrapper.ProductId == 0x028E
                && _activeVigemCount > 0)
            {
                // Lazy-compute the PnP count once per cycle.
                if (_vigemPnPCount < 0)
                {
                    _vigemPnPCount = CountViGEmDevices(
                        @"SYSTEM\CurrentControlSet\Enum\USB\VID_045E&PID_028E",
                        @"USB\VID_045E&PID_028E\");
                    _xbox360FilteredThisCycle = 0;
                }

                // Filter up to _vigemPnPCount devices (keep the rest as real).
                if (_xbox360FilteredThisCycle < _vigemPnPCount)
                {
                    _xbox360FilteredThisCycle++;
                    return true;
                }
            }

            // ── DS4 VID/PID — ViGEm emulates Sony DS4 ──
            // ViGEm DS4 virtual controllers report VID=054C PID=05C4.
            // Same PnP tree walk as Xbox 360 to distinguish real vs virtual.
            if (wrapper.VendorId == 0x054C && wrapper.ProductId == 0x05C4
                && _activeVigemCount > 0)
            {
                if (_vigemDs4PnPCount < 0)
                {
                    _vigemDs4PnPCount = CountViGEmDevices(
                        @"SYSTEM\CurrentControlSet\Enum\USB\VID_054C&PID_05C4",
                        @"USB\VID_054C&PID_05C4\");
                    _ds4FilteredThisCycle = 0;
                }

                if (_ds4FilteredThisCycle < _vigemDs4PnPCount)
                {
                    _ds4FilteredThisCycle++;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Counts how many devices under the given registry key are ViGEm virtual
        /// controllers (have ViGEmBus as a PnP ancestor). Used for both Xbox 360
        /// (VID_045E/PID_028E) and DS4 (VID_054C/PID_05C4) filtering.
        /// </summary>
        /// <param name="registrySubKey">e.g. @"SYSTEM\CurrentControlSet\Enum\USB\VID_045E&amp;PID_028E"</param>
        /// <param name="instancePrefix">e.g. @"USB\VID_045E&amp;PID_028E\"</param>
        private static int CountViGEmDevices(string registrySubKey, string instancePrefix)
        {
            int count = 0;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(registrySubKey, false);
                if (key == null)
                    return 0;

                foreach (var instanceName in key.GetSubKeyNames())
                {
                    var instanceId = instancePrefix + instanceName;

                    // Skip devices that are not currently present.
                    if (!IsDevicePresent(instanceId))
                        continue;

                    if (IsUnderViGEmBus(instanceId))
                        count++;
                }
            }
            catch
            {
                return 0;
            }
            return count;
        }

        // ─────────────────────────────────────────────
        //  PnP helpers (cfgmgr32) for ViGEm detection
        // ─────────────────────────────────────────────

        private const int CR_SUCCESS = 0;
        private const uint DN_DEVICE_IS_PRESENT = 0x00000002;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(
            out uint pdnDevInst, string pDeviceID, int ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(
            out uint pdnDevInst, uint dnDevInst, int ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(
            uint dnDevInst, StringBuilder Buffer, int BufferLen, int ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(
            out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, int ulFlags);

        private static bool IsDevicePresent(string deviceInstanceId)
        {
            if (CM_Locate_DevNodeW(out var devInst, deviceInstanceId, 0) != CR_SUCCESS)
                return false;
            if (CM_Get_DevNode_Status(out var status, out _, devInst, 0) != CR_SUCCESS)
                return false;
            return (status & DN_DEVICE_IS_PRESENT) != 0;
        }

        private static bool IsUnderViGEmBus(string deviceInstanceId)
        {
            if (CM_Locate_DevNodeW(out var devInst, deviceInstanceId, 0) != CR_SUCCESS)
                return false;

            // Walk up the device tree (max 64 levels).
            for (int depth = 0; depth < 64; depth++)
            {
                var id = GetDeviceInstanceId(devInst);
                if (!string.IsNullOrEmpty(id))
                {
                    // Check registry for ViGEmBus service name.
                    try
                    {
                        using var regKey = Registry.LocalMachine.OpenSubKey(
                            @"SYSTEM\CurrentControlSet\Enum\" + id, false);
                        if (regKey != null)
                        {
                            var service = regKey.GetValue("Service") as string;
                            if (!string.IsNullOrEmpty(service) &&
                                service.Equals("ViGEmBus", StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                    catch { }
                }

                if (CM_Get_Parent(out var parent, devInst, 0) != CR_SUCCESS)
                    break;
                devInst = parent;
            }

            return false;
        }

        private static string GetDeviceInstanceId(uint devInst)
        {
            var sb = new StringBuilder(1024);
            return CM_Get_Device_IDW(devInst, sb, sb.Capacity, 0) == CR_SUCCESS
                ? sb.ToString()
                : null;
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
        /// Enumerates connected keyboards via SDL_GetKeyboards and creates UserDevice
        /// records for any new keyboards. Returns true if a new keyboard was found.
        /// </summary>
        private bool EnumerateKeyboards()
        {
            uint[] keyboardIds = SDL_GetKeyboards();
            bool changed = false;

            foreach (uint kbId in keyboardIds)
            {
                if (_openedKeyboardIds.Contains(kbId))
                    continue;

                try
                {
                    var wrapper = new SdlKeyboardWrapper();
                    if (!wrapper.Open(kbId))
                    {
                        wrapper.Dispose();
                        continue;
                    }

                    UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid);
                    ud.LoadFromKeyboardDevice(wrapper);
                    ud.IsOnline = true;

                    _openedKeyboardIds.Add(kbId);
                    changed = true;
                }
                catch (Exception ex)
                {
                    RaiseError($"Error opening keyboard (instance {kbId})", ex);
                }
            }

            return changed;
        }

        /// <summary>
        /// Enumerates connected mice via SDL_GetMice and creates UserDevice
        /// records for any new mice. Returns true if a new mouse was found.
        /// </summary>
        private bool EnumerateMice()
        {
            uint[] mouseIds = SDL_GetMice();
            bool changed = false;

            foreach (uint mouseId in mouseIds)
            {
                if (_openedMouseIds.Contains(mouseId))
                    continue;

                try
                {
                    var wrapper = new SdlMouseWrapper();
                    if (!wrapper.Open(mouseId))
                    {
                        wrapper.Dispose();
                        continue;
                    }

                    UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid);
                    ud.LoadFromMouseDevice(wrapper);
                    ud.IsOnline = true;

                    _openedMouseIds.Add(mouseId);
                    changed = true;
                }
                catch (Exception ex)
                {
                    RaiseError($"Error opening mouse (instance {mouseId})", ex);
                }
            }

            return changed;
        }

        /// <summary>
        /// Detects disconnected keyboards or mice by comparing tracked IDs to current SDL IDs.
        /// Marks disconnected devices offline and removes them from tracking.
        /// </summary>
        private bool DetectDisconnected(HashSet<uint> trackedIds, uint[] currentIds)
        {
            if (trackedIds.Count == 0)
                return false;

            var currentSet = new HashSet<uint>(currentIds);
            var disconnected = new List<uint>();
            bool changed = false;

            foreach (uint id in trackedIds)
            {
                if (!currentSet.Contains(id))
                {
                    // Find by checking all devices whose SdlInstanceId matches.
                    UserDevice ud = FindOnlineDeviceBySdlInstanceId(id);
                    if (ud != null)
                    {
                        MarkDeviceOffline(ud);
                        changed = true;
                    }
                    disconnected.Add(id);
                }
            }

            foreach (uint id in disconnected)
                trackedIds.Remove(id);

            return changed;
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
