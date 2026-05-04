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
        ///   1. Save the current state as OldInputState (preserved for any consumer
        ///      that needs change detection on the next cycle).
        ///   2. Read a new state snapshot from SDL.
        ///   3. Apply force feedback if the device supports rumble and a game
        ///      is sending vibration data via ViGEmBus.
        /// </summary>
        private void UpdateInputStates()
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return;

            // Refresh per-slot post-mix-post-gain rumble before the per-device
            // FFB loop reads it. One pass per polling tick — every consumer
            // (SDL physical rumble, DS5/DS4 effect packet, FFB-tab meter)
            // reads the same FinalVibrationStates instance.
            ComputeFinalVibrationStates();

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

                    CustomInputState newState;

                    if (ud.IsTouchpad && ud.Device == null && _ptpReader != null && _ptpReader.IsAvailable)
                    {
                        // Precision Touchpad (no SDL wrapper).
                        newState = new CustomInputState();
                        if (ud.InstanceGuid == PtpMergedGuid)
                            _ptpReader.ReadInto(newState); // merged: first device
                        else
                        {
                            IntPtr ptpHandle = FindPtpHandle(ud.InstanceGuid);
                            if (ptpHandle != IntPtr.Zero)
                                _ptpReader.ReadInto(ptpHandle, newState);
                        }
                    }
                    else if (ud.Device != null)
                    {
                        // SDL device — read via wrapper.
                        newState = ud.Device.GetCurrentState(ud.ForceRawJoystickMode);
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

                    // Apply force feedback (rumble) if applicable.
                    ApplyForceFeedback(ud);
                }
                catch (Exception ex)
                {
                    RaiseError($"Error reading state for device {ud.ResolvedName}", ex);
                }
            }
        }

        /// <summary>Finds the PTP device handle for a given InstanceGuid.</summary>
        private IntPtr FindPtpHandle(Guid instanceGuid)
        {
            foreach (var kvp in _ptpHandleToGuid)
            {
                if (kvp.Value == instanceGuid)
                    return kvp.Key;
            }
            return IntPtr.Zero;
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

            // Per-(device, slot) PadSetting drives the audio-rumble + FFB
            // gain applied to THIS device. Different physical devices on
            // the same slot can have different gain / audio rumble settings,
            // so each device pulls its own UserSetting's PadSetting rather
            // than reading slotSettings[0]'s like the per-slot meter pass
            // does. For directional FFB data, use the first slot that
            // has it (no sensible way to combine two polar directions).
            ushort combinedL = 0, combinedR = 0;
            Vibration directionalSource = null;
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

                var raw = VibrationStates[padIndex];
                if (raw == null) continue;

                var devicePs = us.GetPadSetting();
                ScaleRumbleForDevice(raw.LeftMotorSpeed, raw.RightMotorSpeed,
                    devicePs, out ushort scaledL, out ushort scaledR);

                if (scaledL > combinedL) combinedL = scaledL;
                if (scaledR > combinedR) combinedR = scaledR;

                if (directionalSource == null
                    && (raw.HasDirectionalData || raw.HasConditionData))
                    directionalSource = raw;

                if (firstPadSetting == null)
                    firstPadSetting = devicePs;
            }

            if (firstPadSetting == null) return;

            // Write combined vibration to a scratch Vibration and apply.
            if (_combinedVibration == null) _combinedVibration = new Vibration();
            _combinedVibration.LeftMotorSpeed = combinedL;
            _combinedVibration.RightMotorSpeed = combinedR;

            // Copy directional/condition FFB data from the first slot that has it.
            // Without this, HasDirectionalData is always false and the haptic path
            // in SetDeviceForces is never reached (all FFB falls through to scalar rumble).
            if (directionalSource != null)
            {
                _combinedVibration.HasDirectionalData = directionalSource.HasDirectionalData;
                _combinedVibration.EffectType = directionalSource.EffectType;
                _combinedVibration.SignedMagnitude = directionalSource.SignedMagnitude;
                _combinedVibration.Direction = directionalSource.Direction;
                _combinedVibration.Period = directionalSource.Period;
                _combinedVibration.DeviceGain = directionalSource.DeviceGain;
                _combinedVibration.HasConditionData = directionalSource.HasConditionData;
                _combinedVibration.ConditionAxisCount = directionalSource.ConditionAxisCount;
                _combinedVibration.ConditionAxes = directionalSource.ConditionAxes;
            }
            else
            {
                // Clear stale directional data from previous frame.
                _combinedVibration.HasDirectionalData = false;
                _combinedVibration.HasConditionData = false;
            }

            ud.ForceFeedbackState.SetDeviceForces(ud, ud.Device, firstPadSetting, _combinedVibration);
        }

        private Vibration _combinedVibration;

        private static float TryParseFloat(string value, float defaultValue)
        {
            return float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result) ? result : defaultValue;
        }

        private static int TryParseInt(string value, int defaultValue)
        {
            return int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int result) ? result : defaultValue;
        }

        private static bool TryParseBool(string value)
        {
            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Per-slot pre-pass that fills <see cref="FinalVibrationStates"/>
        /// using the slot's <see cref="SelectedDeviceGuids"/> device's
        /// PadSetting — drives the FFB-tab activity meter only. The SDL
        /// physical-rumble path and the DS5/DS4 dispatcher each compute
        /// their own per-device scaled rumble (different physical devices
        /// on the same slot can have different gain / audio rumble
        /// settings), so they do NOT read this array.
        /// </summary>
        public void ComputeFinalVibrationStates()
        {
            var settings = SettingsManager.UserSettings;
            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                var raw = VibrationStates[padIndex];
                var final = FinalVibrationStates[padIndex];
                if (raw == null || final == null) continue;

                // Resolve the slot's currently-selected device PadSetting.
                // Falls back to slotSettings[0] when no device is selected
                // (e.g. before the first 30 Hz UI sync) so the meter still
                // reads something reasonable.
                PadSetting ps = null;
                if (settings != null)
                {
                    Guid selected = SelectedDeviceGuids[padIndex];
                    if (selected != Guid.Empty)
                    {
                        var slotSettings = settings.FindByPadIndex(padIndex);
                        if (slotSettings != null)
                        {
                            for (int i = 0; i < slotSettings.Count; i++)
                            {
                                if (slotSettings[i].InstanceGuid == selected)
                                {
                                    ps = slotSettings[i].GetPadSetting();
                                    break;
                                }
                            }
                            if (ps == null && slotSettings.Count > 0)
                                ps = slotSettings[0].GetPadSetting();
                        }
                    }
                    else
                    {
                        var slotSettings = settings.FindByPadIndex(padIndex);
                        if (slotSettings != null && slotSettings.Count > 0)
                            ps = slotSettings[0].GetPadSetting();
                    }
                }

                ScaleRumbleForDevice(raw.LeftMotorSpeed, raw.RightMotorSpeed,
                    ps, out ushort finalL, out ushort finalR);
                final.LeftMotorSpeed = finalL;
                final.RightMotorSpeed = finalR;

                // Directional / condition data passes through unchanged —
                // ForceFeedbackState handles overallGain on those branches
                // via SignedMagnitude scaling, which we leave as-is.
                final.HasDirectionalData = raw.HasDirectionalData;
                final.HasConditionData = raw.HasConditionData;
                final.EffectType = raw.EffectType;
                final.SignedMagnitude = raw.SignedMagnitude;
                final.Direction = raw.Direction;
                final.Period = raw.Period;
                final.DeviceGain = raw.DeviceGain;
                final.ConditionAxisCount = raw.ConditionAxisCount;
                final.ConditionAxes = raw.ConditionAxes;
            }
        }

        /// <summary>
        /// Mixes audio bass rumble into the raw motor values (when the
        /// device's PadSetting has it enabled) and applies ForceOverall ×
        /// LeftMotorStrength / RightMotorStrength × ForceSwapMotor. The
        /// audio detector is shared across slots (one peak source) but
        /// the per-device sensitivity / cutoff / left-right scaling
        /// applied here are per-PadSetting. With <paramref name="ps"/>
        /// null all scaling falls back to identity (raw passthrough at
        /// 100 % gain) so transient pre-init frames still produce sane
        /// rumble.
        /// </summary>
        public void ScaleRumbleForDevice(
            ushort rawLeft, ushort rawRight, PadSetting ps,
            out ushort scaledLeft, out ushort scaledRight)
        {
            ushort baseL = rawLeft;
            ushort baseR = rawRight;

            var detector = AudioBassDetector;
            if (detector != null && ps != null && ps.AudioRumbleEnabled == "1")
            {
                detector.DecayIfSilent();
                detector.Sensitivity = TryParseFloat(ps.AudioRumbleSensitivity, 4f);
                detector.CutoffHz = TryParseFloat(ps.AudioRumbleCutoffHz, 80f);
                ushort motorVal = detector.MotorValue;
                float leftScale = TryParseFloat(ps.AudioRumbleLeftMotor, 100f) / 100f;
                float rightScale = TryParseFloat(ps.AudioRumbleRightMotor, 100f) / 100f;
                ushort audioL = (ushort)(motorVal * leftScale);
                ushort audioR = (ushort)(motorVal * rightScale);
                if (audioL > baseL) baseL = audioL;
                if (audioR > baseR) baseR = audioR;
            }

            int overallGain = 100;
            int leftGain = 100;
            int rightGain = 100;
            bool swap = false;
            if (ps != null)
            {
                overallGain = Math.Clamp(TryParseInt(ps.ForceOverall, 100), 0, 100);
                leftGain = Math.Clamp(TryParseInt(ps.LeftMotorStrength, 100), 0, 100);
                rightGain = Math.Clamp(TryParseInt(ps.RightMotorStrength, 100), 0, 100);
                swap = TryParseBool(ps.ForceSwapMotor);
            }
            double sL = baseL * (leftGain / 100.0) * (overallGain / 100.0);
            double sR = baseR * (rightGain / 100.0) * (overallGain / 100.0);
            ushort finalL = (ushort)Math.Clamp(sL, 0, 65535);
            ushort finalR = (ushort)Math.Clamp(sR, 0, 65535);
            if (swap) (finalL, finalR) = (finalR, finalL);
            scaledLeft = finalL;
            scaledRight = finalR;
        }
    }
}
