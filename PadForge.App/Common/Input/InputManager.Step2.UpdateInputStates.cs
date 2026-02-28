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
        /// state received from games via ViGEmBus.
        ///
        /// When a device is mapped to multiple slots, vibration from all slots
        /// is combined (max of each motor) so rumble from any game reaches the
        /// physical controller.
        /// </summary>
        private void ApplyForceFeedback(UserDevice ud)
        {
            if (ud == null || ud.ForceFeedbackState == null)
                return;

            // Only SDL devices with rumble or haptic FFB support.
            if (ud.Device == null || (!ud.Device.HasRumble && !ud.Device.HasHaptic))
                return;

            // Find ALL pad slots this device is mapped to (multi-slot assignment).
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            int slotCount = settings.FindByInstanceGuid(ud.InstanceGuid, _instanceGuidBuffer);
            if (slotCount == 0) return;

            // Combine vibration across all mapped slots (max of each motor).
            ushort combinedL = 0, combinedR = 0;
            PadSetting firstPadSetting = null;
            for (int i = 0; i < slotCount; i++)
            {
                var us = _instanceGuidBuffer[i];
                int padIndex = us.MapTo;
                if (padIndex < 0 || padIndex >= MaxPads) continue;

                // If a test rumble targets a specific device in this slot, skip others.
                Guid targetGuid = TestRumbleTargetGuid[padIndex];
                if (targetGuid != Guid.Empty && targetGuid != ud.InstanceGuid)
                    continue;

                var vib = VibrationStates[padIndex];
                if (vib == null) continue;

                if (vib.LeftMotorSpeed > combinedL)  combinedL = vib.LeftMotorSpeed;
                if (vib.RightMotorSpeed > combinedR) combinedR = vib.RightMotorSpeed;

                if (firstPadSetting == null)
                    firstPadSetting = us.GetPadSetting();
            }

            if (firstPadSetting == null) return;

            // Write combined vibration to a scratch Vibration and apply.
            if (_combinedVibration == null) _combinedVibration = new Vibration();
            _combinedVibration.LeftMotorSpeed = combinedL;
            _combinedVibration.RightMotorSpeed = combinedR;

            ud.ForceFeedbackState.SetDeviceForces(ud, ud.Device, firstPadSetting, _combinedVibration);
        }

        private Vibration _combinedVibration;

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
