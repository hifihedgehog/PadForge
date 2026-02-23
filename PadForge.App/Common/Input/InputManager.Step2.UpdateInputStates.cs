using System;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 2: UpdateInputStates
        //  Reads the current input state from each online device.
        //  SDL devices use SdlDeviceWrapper.GetCurrentState().
        //  Native XInput devices use XInputInterop.GetStateEx().
        //  Also applies force feedback (rumble) to devices that support it.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Step 2: Read current input states from all online devices and apply force feedback.
        /// 
        /// For each online device:
        ///   1. Save the current state as OldInputState.
        ///   2. Read a new state snapshot from SDL or XInput.
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

                    if (ud.IsXInput && ud.XInputUserIndex >= 0)
                    {
                        // Safety guard: skip ViGEm-owned XInput slots to prevent
                        // loopback. This is a defense-in-depth check — Step 1
                        // should already exclude ViGEm slots during enumeration,
                        // but if a slot becomes ViGEm-owned between Step 1 and
                        // Step 2, this catches it. Adapted from x360ce's
                        // IsViGEmOwnedSlot() guard.
                        if (IsViGEmOccupiedSlot(ud.XInputUserIndex))
                        {
                            ud.IsOnline = false;
                            continue;
                        }

                        // Native XInput controller — read via P/Invoke.
                        newState = ReadXInputState(ud.XInputUserIndex);
                    }
                    else if (ud.Device != null)
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
        //  XInput state reading
        // ─────────────────────────────────────────────

        /// <summary>
        /// Reads the current state of a native XInput controller and converts it
        /// to a <see cref="CustomInputState"/> using the same unsigned axis convention
        /// as SDL devices.
        /// </summary>
        /// <param name="userIndex">XInput user index (0–3).</param>
        /// <returns>A CustomInputState, or null if the controller is disconnected.</returns>
        private CustomInputState ReadXInputState(int userIndex)
        {
            if (!XInputInterop.GetStateEx(userIndex, out var xiState))
                return null;

            return XInputInterop.ConvertToInputState(xiState);
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

            // Only SDL devices with rumble support or XInput devices.
            bool canRumble = (ud.Device != null && ud.Device.HasRumble) || ud.IsXInput;
            if (!canRumble)
                return;

            // Find which pad slot this device is mapped to.
            var userSetting = SettingsManager.UserSettings?.FindByInstanceGuid(ud.InstanceGuid);
            if (userSetting == null)
                return;

            int padIndex = userSetting.MapTo;
            if (padIndex < 0 || padIndex >= MaxPads)
                return;

            // Get the vibration state for this pad slot.
            Vibration vibration = VibrationStates[padIndex];
            if (vibration == null)
                return;

            // Get the PadSetting for force feedback configuration.
            PadSetting ps = userSetting.GetPadSetting();
            if (ps == null)
                return;

            if (ud.IsXInput && ud.XInputUserIndex >= 0)
            {
                // Native XInput: send vibration directly via XInput API.
                ApplyXInputVibration(ud, ps, vibration);
            }
            else if (ud.Device != null)
            {
                // SDL device: use ForceFeedbackState to manage rumble.
                ud.ForceFeedbackState.SetDeviceForces(ud, ud.Device, ps, vibration);
            }
        }

        /// <summary>
        /// Applies vibration to a native XInput controller via XInput API.
        /// Applies the PadSetting's gain and motor swap settings.
        /// </summary>
        private void ApplyXInputVibration(UserDevice ud, PadSetting ps, Vibration vibration)
        {
            if (ud.XInputUserIndex < 0 || ud.XInputUserIndex >= MaxPads)
                return;

            // Parse gain settings.
            int overallGain = TryParseInt(ps.ForceOverall, 100);
            int leftGain = TryParseInt(ps.LeftMotorStrength, 100);
            int rightGain = TryParseInt(ps.RightMotorStrength, 100);
            bool swapMotors = TryParseBool(ps.ForceSwapMotor);

            overallGain = Math.Clamp(overallGain, 0, 100);
            leftGain = Math.Clamp(leftGain, 0, 100);
            rightGain = Math.Clamp(rightGain, 0, 100);

            double left = vibration.LeftMotorSpeed * (leftGain / 100.0) * (overallGain / 100.0);
            double right = vibration.RightMotorSpeed * (rightGain / 100.0) * (overallGain / 100.0);

            ushort finalLeft = (ushort)Math.Clamp(left, 0, 65535);
            ushort finalRight = (ushort)Math.Clamp(right, 0, 65535);

            if (swapMotors)
                (finalLeft, finalRight) = (finalRight, finalLeft);

            try
            {
                XInputInterop.SetVibration(ud.XInputUserIndex, finalLeft, finalRight);
            }
            catch { /* best effort */ }
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

    // ─────────────────────────────────────────────────────
    //  Additional XInputInterop partial declarations
    // ─────────────────────────────────────────────────────

    public static partial class XInputInterop
    {
        /// <summary>
        /// Reads the extended state of an XInput controller.
        /// Returns true if the controller is connected and state was read.
        /// </summary>
        public static partial bool GetStateEx(int userIndex, out XInputState state);

        /// <summary>
        /// Converts a raw XInput state to a <see cref="CustomInputState"/>.
        /// Maps XInput axes (signed) to unsigned convention and XInput buttons
        /// to the button array.
        /// </summary>
        public static partial CustomInputState ConvertToInputState(XInputState xiState);

        /// <summary>
        /// Sets vibration on a native XInput controller.
        /// </summary>
        public static partial void SetVibration(int userIndex, ushort leftMotor, ushort rightMotor);
    }

}
