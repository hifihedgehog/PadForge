using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Central manager for device records and mapping settings.
    /// 
    /// This is a static class shared between the background engine thread
    /// and the UI thread. All access to <see cref="UserDevices"/> and
    /// <see cref="UserSettings"/> must be done inside a lock on the
    /// respective collection's SyncRoot.
    /// 
    /// Lifecycle:
    ///   1. SettingsService.Initialize() creates the collections and loads from XML.
    ///   2. InputManager.Step1 adds/updates UserDevice records as devices connect/disconnect.
    ///   3. InputService (UI thread) reads collections to sync ViewModels.
    ///   4. SettingsService.Save() serializes collections to XML on save.
    /// 
    /// This file is the canonical partial; additional partial declarations exist
    /// in InputManager.cs and InputManager.Step1.UpdateDevices.cs for the
    /// UserDevices/UserSettings property declarations and collection class definitions.
    /// </summary>
    public static partial class SettingsManager
    {
        // UserDevices and UserSettings properties are declared in
        // InputManager.Step1.UpdateDevices.cs (partial class).

        // ─────────────────────────────────────────────
        //  Profiles
        // ─────────────────────────────────────────────

        /// <summary>All saved profiles. Empty list = no profiles configured.</summary>
        public static List<ProfileData> Profiles { get; set; } = new();

        /// <summary>The ID of the currently active profile, or null for the default (root) profile.</summary>
        public static string ActiveProfileId { get; set; }

        /// <summary>Whether auto-switching profiles based on foreground application is enabled.</summary>
        public static bool EnableAutoProfileSwitching { get; set; }

        // ─────────────────────────────────────────────
        //  Initialization
        // ─────────────────────────────────────────────

        /// <summary>
        /// Ensures the manager's collections are initialized.
        /// Safe to call multiple times.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (UserDevices == null)
                UserDevices = new DeviceCollection();
            if (UserSettings == null)
                UserSettings = new SettingsCollection();
        }

        // ─────────────────────────────────────────────
        //  Device management
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds a UserDevice by instance GUID. Thread-safe.
        /// </summary>
        /// <returns>The device, or null if not found.</returns>
        public static UserDevice FindDeviceByInstanceGuid(Guid instanceGuid)
        {
            var devices = UserDevices;
            if (devices == null) return null;

            lock (devices.SyncRoot)
            {
                return devices.Items.FirstOrDefault(d => d.InstanceGuid == instanceGuid);
            }
        }

        /// <summary>
        /// Finds all online devices. Thread-safe.
        /// Returns a snapshot (safe to iterate outside the lock).
        /// </summary>
        public static List<UserDevice> GetOnlineDevices()
        {
            var devices = UserDevices;
            if (devices == null) return new List<UserDevice>();

            lock (devices.SyncRoot)
            {
                return devices.Items.Where(d => d.IsOnline).ToList();
            }
        }

        /// <summary>
        /// Adds a UserDevice if it doesn't already exist (by InstanceGuid).
        /// Returns the existing or newly added device. Thread-safe.
        /// </summary>
        public static UserDevice AddOrGetDevice(UserDevice device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));

            var devices = UserDevices;
            if (devices == null) return device;

            lock (devices.SyncRoot)
            {
                var existing = devices.Items.FirstOrDefault(
                    d => d.InstanceGuid == device.InstanceGuid);
                if (existing != null)
                    return existing;

                devices.Items.Add(device);
                return device;
            }
        }

        /// <summary>
        /// Removes a UserDevice by instance GUID. Thread-safe.
        /// Also removes any associated UserSettings.
        /// </summary>
        /// <returns>True if removed.</returns>
        public static bool RemoveDevice(Guid instanceGuid)
        {
            bool removed = false;
            var devices = UserDevices;
            if (devices != null)
            {
                lock (devices.SyncRoot)
                {
                    int idx = devices.Items.FindIndex(d => d.InstanceGuid == instanceGuid);
                    if (idx >= 0)
                    {
                        devices.Items.RemoveAt(idx);
                        removed = true;
                    }
                }
            }

            // Also remove associated settings.
            var settings = UserSettings;
            if (settings != null)
            {
                lock (settings.SyncRoot)
                {
                    settings.Items.RemoveAll(s => s.InstanceGuid == instanceGuid);
                }
            }

            return removed;
        }

        // ─────────────────────────────────────────────
        //  UserSetting management
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds the UserSetting for a device. Thread-safe.
        /// Shorthand for <c>UserSettings.FindByInstanceGuid(guid)</c>.
        /// </summary>
        public static UserSetting FindSettingByInstanceGuid(Guid instanceGuid)
        {
            var settings = UserSettings;
            if (settings == null) return null;
            return settings.FindByInstanceGuid(instanceGuid);
        }

        /// <summary>
        /// Creates or retrieves a UserSetting that links a device to a pad slot.
        /// If a UserSetting already exists for the device, its MapTo is updated.
        /// Thread-safe.
        /// </summary>
        /// <param name="instanceGuid">Device instance GUID.</param>
        /// <param name="padIndex">Target pad slot (0–3).</param>
        /// <returns>The UserSetting (existing or new).</returns>
        public static UserSetting AssignDeviceToSlot(Guid instanceGuid, int padIndex)
        {
            if (padIndex < 0 || padIndex > 3)
                throw new ArgumentOutOfRangeException(nameof(padIndex), "Must be 0–3.");

            var settings = UserSettings;
            if (settings == null) return null;

            lock (settings.SyncRoot)
            {
                var existing = settings.Items.FirstOrDefault(
                    s => s.InstanceGuid == instanceGuid);

                if (existing != null)
                {
                    existing.MapTo = padIndex;
                    return existing;
                }

                var us = new UserSetting
                {
                    InstanceGuid = instanceGuid,
                    MapTo = padIndex
                };

                // Don't create a PadSetting here — let the caller (DeviceService)
                // create proper defaults based on the device type via CreateDefaultPadSetting().

                settings.Items.Add(us);
                return us;
            }
        }

        /// <summary>
        /// Unassigns a device from its pad slot by removing its UserSetting.
        /// Thread-safe.
        /// </summary>
        public static bool UnassignDevice(Guid instanceGuid)
        {
            var settings = UserSettings;
            if (settings == null) return false;

            lock (settings.SyncRoot)
            {
                return settings.Items.RemoveAll(
                    s => s.InstanceGuid == instanceGuid) > 0;
            }
        }

        /// <summary>
        /// Returns a snapshot of all UserSettings assigned to a pad slot.
        /// Thread-safe.
        /// </summary>
        public static List<UserSetting> GetSettingsForSlot(int padIndex)
        {
            var settings = UserSettings;
            if (settings == null) return new List<UserSetting>();
            return settings.FindByPadIndex(padIndex);
        }

        // ─────────────────────────────────────────────
        //  PadSetting helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates a default PadSetting with standard Xbox controller mappings
        /// auto-detected from the device's capability count.
        /// </summary>
        /// <param name="ud">The device to create defaults for.</param>
        /// <returns>A PadSetting with sensible default mappings.</returns>
        public static PadSetting CreateDefaultPadSetting(UserDevice ud)
        {
            var ps = new PadSetting();

            if (ud == null)
            {
                ps.UpdateChecksum();
                return ps;
            }

            // SDL3 gamepads (CapType == Gamepad) get auto-mapped using the
            // standardized SDL3 gamepad axis/button order:
            //   Axes: LX(0), LY(1), LT(2), RX(3), RY(4), RT(5)
            //   Buttons: A(0), B(1), X(2), Y(3), LB(4), RB(5),
            //            Back(6), Start(7), LS(8), RS(9), Guide(10)
            //   Hats: 1 (D-pad)
            if (ud.CapType == InputDeviceType.Gamepad)
            {
                ps.LeftThumbAxisX = "Axis 0";
                ps.LeftThumbAxisY = "Axis 1";
                ps.LeftTrigger = "Axis 2";
                ps.RightThumbAxisX = "Axis 3";
                ps.RightThumbAxisY = "Axis 4";
                ps.RightTrigger = "Axis 5";

                // D-pad from hat switch.
                ps.DPad = "POV 0";

                // SDL3 XInput backend button indices.
                ps.ButtonA = "Button 0";
                ps.ButtonB = "Button 1";
                ps.ButtonX = "Button 2";
                ps.ButtonY = "Button 3";
                ps.LeftShoulder = "Button 4";
                ps.RightShoulder = "Button 5";
                ps.ButtonBack = "Button 6";
                ps.ButtonStart = "Button 7";
                ps.LeftThumbButton = "Button 8";
                ps.RightThumbButton = "Button 9";
                ps.ButtonGuide = "Button 10";

                // Default dead zones and gains.
                ps.LeftThumbDeadZoneX = "0";
                ps.LeftThumbDeadZoneY = "0";
                ps.RightThumbDeadZoneX = "0";
                ps.RightThumbDeadZoneY = "0";
                ps.LeftThumbAntiDeadZone = "0";
                ps.RightThumbAntiDeadZone = "0";
                ps.LeftThumbLinear = "0";
                ps.RightThumbLinear = "0";
                ps.ForceOverall = "100";
                ps.LeftMotorStrength = "100";
                ps.RightMotorStrength = "100";
                ps.ForceSwapMotor = "0";

                ps.UpdateChecksum();
                return ps;
            }

            // Non-gamepad devices are not auto-mapped.
            // The user must manually record mappings for these devices.

            ps.UpdateChecksum();
            return ps;
        }

        // ─────────────────────────────────────────────
        //  Diagnostics
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns a summary string for diagnostics.
        /// </summary>
        public static string GetSummary()
        {
            int deviceCount = 0, onlineCount = 0, settingCount = 0;

            var devices = UserDevices;
            if (devices != null)
            {
                lock (devices.SyncRoot)
                {
                    deviceCount = devices.Items.Count;
                    onlineCount = devices.Items.Count(d => d.IsOnline);
                }
            }

            var settings = UserSettings;
            if (settings != null)
            {
                lock (settings.SyncRoot)
                {
                    settingCount = settings.Items.Count;
                }
            }

            return $"Devices: {onlineCount}/{deviceCount} online, Settings: {settingCount}";
        }
    }
}
