using System;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 2: UpdateInputStates
        //  Reads the current input state from each online device via SDL.
        //  Also applies force feedback (rumble) to devices that support it.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Step 2: Read current input states from all online devices and apply force feedback.
        ///
        /// For each online device:
        ///   1. Save the current state as OldInputState.
        ///   2. Read a new state snapshot from SDL.
        ///   3. Compute buffered updates (differences from old state).
        ///   4. Apply force feedback if the device supports rumble and a game
        ///      is sending vibration data via ViGEmBus.
        /// </summary>
        private void UpdateInputStates()
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return;

            // Snapshot online devices into pre-allocated buffer (no LINQ allocation).
            int snapshotCount;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                if (_deviceSnapshotBuffer.Length < devices.Count)
                    _deviceSnapshotBuffer = new UserDevice[devices.Count];

                snapshotCount = 0;
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].IsOnline)
                        _deviceSnapshotBuffer[snapshotCount++] = devices[i];
                }
            }

            for (int si = 0; si < snapshotCount; si++)
            {
                var ud = _deviceSnapshotBuffer[si];
                try
                {
                    // Save previous state for change detection.
                    ud.OldInputState = ud.InputState;
                    ud.OldInputUpdates = ud.InputUpdates;
                    ud.OldInputStateTime = ud.InputStateTime;

                    CustomInputState newState;

                    if (ud.Device != null)
                    {
                        // SDL device — read via wrapper.
                        newState = ud.Device.GetCurrentState();
                    }
                    else
                    {
                        // Device handle lost — mark offline.
                        ud.IsOnline = false;
                        continue;
                    }

                    if (newState == null)
                    {
                        // Read failed — device may have been disconnected.
                        ud.IsOnline = false;
                        continue;
                    }

                    // Atomic reference swap — safe for cross-thread reading.
                    ud.InputState = newState;
                    ud.InputStateTime = DateTime.UtcNow;

                    // Compute buffered updates for change detection / recording.
                    if (ud.OldInputState != null)
                    {
                        ud.InputUpdates = CustomInputHelper.GetUpdates(ud.OldInputState, newState);
                    }
                    else
                    {
                        ud.InputUpdates = Array.Empty<CustomInputUpdate>();
                    }

                    // Apply force feedback (rumble) if applicable.
                    ApplyForceFeedback(ud);
                }
                catch (Exception ex)
                {
                    RaiseError($"Error reading state for device {ud.ResolvedName}", ex);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Force feedback
        // ─────────────────────────────────────────────

        /// <summary>
        /// Applies force feedback (rumble) to a device based on the vibration
        /// state received from the game via ViGEmBus.
        ///
        /// The vibration state comes from the virtual controller slot that this
        /// device is mapped to. The PadSetting for the device controls gain,
        /// motor swap, and other force feedback parameters.
        /// </summary>
        private void ApplyForceFeedback(UserDevice ud)
        {
            if (ud == null || ud.ForceFeedbackState == null)
                return;

            // Only SDL devices with rumble or haptic FFB support.
            if (ud.Device == null || (!ud.Device.HasRumble && !ud.Device.HasHaptic))
                return;

            // Find which pad slot this device is mapped to.
            var userSetting = SettingsManager.UserSettings?.FindByInstanceGuid(ud.InstanceGuid);
            if (userSetting == null)
                return;

            int padIndex = userSetting.MapTo;
            if (padIndex < 0 || padIndex >= MaxPads)
                return;

            // If a test rumble targets a specific device in this slot, skip others.
            Guid targetGuid = TestRumbleTargetGuid[padIndex];
            if (targetGuid != Guid.Empty && targetGuid != ud.InstanceGuid)
                return;

            // Get the vibration state for this pad slot.
            Vibration vibration = VibrationStates[padIndex];
            if (vibration == null)
                return;

            // Get the PadSetting for force feedback configuration.
            PadSetting ps = userSetting.GetPadSetting();
            if (ps == null)
                return;

            // SDL device: use ForceFeedbackState to manage rumble.
            ud.ForceFeedbackState.SetDeviceForces(ud, ud.Device, ps, vibration);
        }

        // ─────────────────────────────────────────────
        //  Parse helpers
        // ─────────────────────────────────────────────

        private static int TryParseInt(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        private static bool TryParseBool(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
