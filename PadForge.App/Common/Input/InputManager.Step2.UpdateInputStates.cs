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

            // Decay the audio bass detector exactly ONCE per polling tick.
            // ScaleRumbleForDevice is called many times per tick (slot
            // pass + per-device pass + per-device dispatcher pass), and
            // each DecayIfSilent invocation multiplies the decay rate
            // when audio is silent — collapsing bass energy between hits
            // and weakening audio rumble. The detector docstring spells
            // out "once per frame"; this is that one frame call.
            //
            // Same reasoning for Sensitivity / CutoffHz: the detector
            // applies these IN the WASAPI callback (rms × _sensitivity).
            // Setting them many times per polling tick from per-device
            // PadSettings creates a race with the audio thread and
            // (when devices have different sensitivities) lets the
            // last-call-wins value bleed into the next callback. Set
            // them once per tick from the slot's primary audio-enabled
            // device — matches the 3.1.0 path exactly.
            var det = AudioBassDetector;
            if (det != null)
            {
                det.DecayIfSilent();
                ApplyDetectorSettingsForTick(det);
            }

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

            // ══════════════════════════════════════════════════════════════
            // Per-slot Sony-rumble poke for UserEffectsDispatcher.
            // ══════════════════════════════════════════════════════════════
            // DO NOT REMOVE THIS LOOP without also reverting the Sony
            // VID/PID skip above + the synthesizers' unconditional rumble
            // writes. The three pieces form the sole-writer rumble
            // architecture for DS5/DS4:
            //
            //   1. ApplyForceFeedback skips Sony pads (above) → SDL
            //      never writes rumble for them.
            //   2. UserEffectsDispatcher writes the entire effect packet
            //      every tick (rumble + lightbar + AT + mic LED).
            //   3. THIS poke keeps the dispatcher's 33 ms timer alive
            //      during audio-rumble or game-rumble periods even
            //      when the lightbar mode is static / off — because
            //      the timer was originally gated only on lightbar
            //      animation, and an idle-lightbar slot would otherwise
            //      have NO writer at all.
            //
            // The architecture exists because two writers (PadForge
            // dispatcher + SDL3 PS5/PS4 driver) racing on an
            // asynchronously-sampled audio peak produced the v3.1.x
            // audio-rumble + animated-lightbar regression — see memory:
            // sony-rumble-sole-writer-architecture.md.
            //
            // Inputs:
            //   - hasGameRumble: raw VibrationStates non-zero (game or
            //     test rumble in flight)
            //   - hasAudioRumbleEnabled: any per-device PadSetting on
            //     the slot has AudioRumbleEnabled=="1" (audio peaks
            //     should be flowing into rumble bytes)
            // The dispatcher merges these with its lightbar-animation
            // logic in UpdateAnimTimer to decide whether to keep its
            // 33 ms timer running.
            //
            // Cost: one walk of UserSettings.Items per slot per polling
            // tick under the SyncRoot lock. ~16 slots × ~16 user
            // settings worst case, well under a microsecond on warm
            // cache. The lock is held briefly enough that UI-thread
            // mutations (device assignment, profile load) don't see
            // measurable contention.
            var settingsForPoke = SettingsManager.UserSettings;
            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                // Empty pad — no VC means no dispatcher to poke. Skip the
                // lock acquire + UserSettings scan that would otherwise
                // run 14× per cycle on a typical 2-active-slot setup.
                if (!SettingsManager.SlotCreated[padIndex]) continue;

                var raw = VibrationStates[padIndex];
                bool hasGameRumble = raw != null && (raw.LeftMotorSpeed > 0 || raw.RightMotorSpeed > 0);
                // An active macro rumble override on a Sony slot needs
                // the dispatcher's timer running so the override actually
                // reaches the motors. Treat it as game-rumble equivalent
                // for timer-keepalive purposes — the dispatcher's per-
                // device rumble pump merges them via max() at write time.
                if (!hasGameRumble && MacroRumbleOverrides[padIndex].IsActive)
                    hasGameRumble = true;

                bool hasAudioRumbleEnabled = false;
                if (settingsForPoke != null)
                {
                    lock (settingsForPoke.SyncRoot)
                    {
                        for (int i = 0; i < settingsForPoke.Items.Count; i++)
                        {
                            var us = settingsForPoke.Items[i];
                            if (us == null || us.MapTo != padIndex) continue;
                            var ps = us.GetPadSetting();
                            if (ps == null) continue;
                            if (ps.AudioRumbleEnabled == "1") hasAudioRumbleEnabled = true;
                            // Constant force: when any per-device PadSetting on
                            // this slot has it enabled with nonzero X or Y,
                            // treat as game-rumble-equivalent so the Sony
                            // dispatcher's effect-packet timer runs and the
                            // synthesized motor bytes from
                            // ConstantForceEvaluator.Resolve actually reach
                            // the wire. Without this poke, a slot that's
                            // game-silent and lightbar-static parks the
                            // dispatcher and constant force never fires on
                            // DualSense / DS4.
                            if (!hasGameRumble && ps.ConstantForceEnabled == "1"
                                && (ParseConstantForceComponent(ps.ConstantForceX) != 0.0
                                    || ParseConstantForceComponent(ps.ConstantForceY) != 0.0))
                            {
                                hasGameRumble = true;
                            }
                            if (hasAudioRumbleEnabled && hasGameRumble) break;
                        }
                    }
                }

                UserEffectsDispatcher.OnPollingTick(padIndex, hasGameRumble, hasAudioRumbleEnabled);
            }
        }

        // PadSetting stores ConstantForceX/Y as InvariantCulture strings
        // (XmlElement-serialized). Parse defensively: anything we can't
        // turn into a number reads as zero so the dispatcher-timer poke
        // logic above never trips on a malformed setting.
        private static double ParseConstantForceComponent(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0.0;
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? v : 0.0;
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

            // ══════════════════════════════════════════════════════════════
            // SONY DS5 / DS4 SKIP — DO NOT REMOVE.
            // ══════════════════════════════════════════════════════════════
            // UserEffectsDispatcher is the SOLE writer of effect packets
            // for Sony DualSense / DualShock 4 — rumble + lightbar +
            // adaptive triggers + mic LED, all in one HID write per
            // dispatcher tick. SDL_RumbleJoystick MUST NOT be called
            // for these devices.
            //
            // Calling SDL rumble here would have SDL3's PS5/PS4 driver
            // write its own effect packet through a separate HID handle
            // that races with the dispatcher's per-tick writes. The
            // firmware applies whichever WriteFile lands most recently;
            // when the two writers' rumble bytes disagree (which they
            // always do during audio rumble, because AudioBassDetector.
            // MotorValue is sampled asynchronously from the WASAPI
            // callback), motors stutter at 30 Hz and the user perceives
            // weak rumble. This was the v3.1.x audio-rumble +
            // animated-lightbar regression. The architectural fix is
            // sole-writer mode; do not undo it.
            //
            // The poke loop at the end of UpdateInputStates keeps the
            // dispatcher's 33 ms timer alive across audio-rumble and
            // game-rumble periods even with a static / off lightbar.
            // Game rumble, test rumble, and audio rumble all flow
            // through the dispatcher's effect packet path.
            //
            // See memory: sony-rumble-sole-writer-architecture.md.
            const ushort SonyVid = 0x054C;
            if (ud.VendorId == SonyVid &&
                (ud.ProdId == 0x0CE6   // DualSense
              || ud.ProdId == 0x0DF2   // DualSense Edge
              || ud.ProdId == 0x05C4   // DS4 v1
              || ud.ProdId == 0x09CC   // DS4 v1 alt
              || ud.ProdId == 0x0BA0)) // DS4 v2
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

                // Macro rumble layers on top of game force via max() so
                // user-driven feedback is always felt even mid-game-rumble.
                // Constant force then resolves over the merged result with
                // override-with-resume semantics: if game OR macro is
                // producing force this tick, the constant force stays
                // dormant; the moment both go silent it kicks back in.
                if (_macroRumbleScratch == null) _macroRumbleScratch = new Vibration();
                var withMacro = MacroRumbleOverride.Merge(raw, MacroRumbleOverrides[padIndex], _macroRumbleScratch);

                if (_constantForceScratch == null) _constantForceScratch = new Vibration();
                var effective = ConstantForceEvaluator.Resolve(withMacro, devicePs, _constantForceScratch);

                ScaleRumbleForDevice(effective.LeftMotorSpeed, effective.RightMotorSpeed,
                    devicePs, out ushort scaledL, out ushort scaledR);

                if (scaledL > combinedL) combinedL = scaledL;
                if (scaledR > combinedR) combinedR = scaledR;

                if (directionalSource == null
                    && (effective.HasDirectionalData || effective.HasConditionData))
                    directionalSource = effective;

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

        // Per-slot scratch buffer reused across iterations of the
        // ApplyForceFeedback per-slot loop — the evaluator only writes
        // when the override fires, otherwise it returns the raw input
        // unchanged. Populating a fresh Vibration per tick would allocate
        // on every device with multi-slot mappings.
        private Vibration _constantForceScratch;

        // Same shape as _constantForceScratch but for the macro rumble
        // merge layer that runs ahead of constant-force resolution.
        private Vibration _macroRumbleScratch;

        /// <summary>Pushes the audio detector's per-tick parameters
        /// (Sensitivity, CutoffHz) from the first audio-rumble-enabled
        /// PadSetting found across all slots. The detector is shared
        /// app-wide; ScaleRumbleForDevice's per-device call sites
        /// previously fought over these properties, racing the WASAPI
        /// callback's read of <c>_sensitivity</c>. One write per tick
        /// matches 3.1.0's contract.</summary>
        private void ApplyDetectorSettingsForTick(AudioBassDetector detector)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;
            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                Guid selected = SelectedDeviceGuids[padIndex];
                var slotSettings = settings.FindByPadIndex(padIndex);
                if (slotSettings == null || slotSettings.Count == 0) continue;
                // Prefer SelectedMappedDevice's PadSetting (matches the
                // FFB tab the user is editing); fall back to the first
                // mapped device on the slot.
                PadSetting ps = null;
                if (selected != Guid.Empty)
                {
                    for (int i = 0; i < slotSettings.Count; i++)
                    {
                        if (slotSettings[i].InstanceGuid == selected)
                        {
                            ps = slotSettings[i].GetPadSetting();
                            break;
                        }
                    }
                }
                if (ps == null) ps = slotSettings[0].GetPadSetting();
                if (ps == null) continue;
                if (ps.AudioRumbleEnabled != "1") continue;

                detector.Sensitivity = TryParseFloat(ps.AudioRumbleSensitivity, 4f);
                detector.CutoffHz = TryParseFloat(ps.AudioRumbleCutoffHz, 80f);
                return; // first audio-enabled slot wins
            }
        }

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
        /// Per-slot pre-pass that fills two parallel meter feeds:
        /// <list type="bullet">
        /// <item><see cref="FinalVibrationStates"/> — strongest per-motor
        ///   output across every device mapped to the slot, each scaled
        ///   by its OWN PadSetting (gain, motor strengths, audio rumble,
        ///   constant force). Drives the Controller-preview-tab motor
        ///   meter. Device-filter-independent so a force coming through
        ///   any device on the slot is visible regardless of which device
        ///   the user is editing.</item>
        /// <item><see cref="SelectedDeviceVibrationStates"/> — the
        ///   <see cref="SelectedDeviceGuids"/> device's own scaled output
        ///   (its own gain / audio rumble / constant force applied).
        ///   Drives the FFB-tab motor meter. Device-specific so the
        ///   user editing one device's FFB settings sees what's actually
        ///   reaching THAT device.</item>
        /// </list>
        ///
        /// <para>Macro rumble and constant force layering match Step 2's
        /// per-device ApplyForceFeedback path so both meters track what
        /// the firmware actually receives, not just the raw game-driven
        /// values.</para>
        /// </summary>
        public void ComputeFinalVibrationStates()
        {
            var settings = SettingsManager.UserSettings;
            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                var raw = VibrationStates[padIndex];
                var final = FinalVibrationStates[padIndex];
                var selected = SelectedDeviceVibrationStates[padIndex];
                if (raw == null || final == null || selected == null) continue;

                // Macro rumble override is slot-level — apply once before
                // the per-device loop so audio rumble / constant force
                // resolution sees the merged baseline.
                if (_macroRumbleScratch == null) _macroRumbleScratch = new Vibration();
                var withMacro = MacroRumbleOverride.Merge(raw, MacroRumbleOverrides[padIndex], _macroRumbleScratch);

                ushort bestL = 0, bestR = 0;
                ushort selL = 0, selR = 0;
                Vibration directionalSource = null;
                Vibration selectedDirectional = null;
                Guid selectedGuid = SelectedDeviceGuids[padIndex];
                int slotCount = settings != null
                    ? settings.FindByPadIndex(padIndex, _instanceGuidBuffer) : 0;

                if (slotCount == 0)
                {
                    // No devices mapped → preview meter mirrors raw (no
                    // scaling to apply) so the user still sees a test
                    // rumble in flight. FFB-tab meter shows zero (no
                    // device == no per-device output to display).
                    final.LeftMotorSpeed = withMacro.LeftMotorSpeed;
                    final.RightMotorSpeed = withMacro.RightMotorSpeed;
                    final.HasDirectionalData = withMacro.HasDirectionalData;
                    final.HasConditionData = withMacro.HasConditionData;
                    final.EffectType = withMacro.EffectType;
                    final.SignedMagnitude = withMacro.SignedMagnitude;
                    final.Direction = withMacro.Direction;
                    final.Period = withMacro.Period;
                    final.DeviceGain = withMacro.DeviceGain;
                    final.ConditionAxisCount = withMacro.ConditionAxisCount;
                    final.ConditionAxes = withMacro.ConditionAxes;
                    selected.LeftMotorSpeed = 0;
                    selected.RightMotorSpeed = 0;
                    selected.HasDirectionalData = false;
                    selected.HasConditionData = false;
                    continue;
                }

                for (int i = 0; i < slotCount; i++)
                {
                    var us = _instanceGuidBuffer[i];
                    if (us == null) continue;
                    var devicePs = us.GetPadSetting();

                    if (_constantForceScratch == null) _constantForceScratch = new Vibration();
                    var effective = ConstantForceEvaluator.Resolve(withMacro, devicePs, _constantForceScratch);

                    ScaleRumbleForDevice(effective.LeftMotorSpeed, effective.RightMotorSpeed,
                        devicePs, out ushort scaledL, out ushort scaledR);

                    if (scaledL > bestL) bestL = scaledL;
                    if (scaledR > bestR) bestR = scaledR;

                    if (directionalSource == null
                        && (effective.HasDirectionalData || effective.HasConditionData))
                        directionalSource = effective;

                    // Capture the selected device's own scaled output for
                    // the FFB-tab meter.
                    if (selectedGuid != Guid.Empty && us.InstanceGuid == selectedGuid)
                    {
                        selL = scaledL;
                        selR = scaledR;
                        if (effective.HasDirectionalData || effective.HasConditionData)
                            selectedDirectional = effective;
                    }
                }

                final.LeftMotorSpeed = bestL;
                final.RightMotorSpeed = bestR;
                selected.LeftMotorSpeed = selL;
                selected.RightMotorSpeed = selR;

                // Directional / condition data passes through unchanged
                // from the first contributing device.
                if (directionalSource != null)
                {
                    final.HasDirectionalData = directionalSource.HasDirectionalData;
                    final.HasConditionData = directionalSource.HasConditionData;
                    final.EffectType = directionalSource.EffectType;
                    final.SignedMagnitude = directionalSource.SignedMagnitude;
                    final.Direction = directionalSource.Direction;
                    final.Period = directionalSource.Period;
                    final.DeviceGain = directionalSource.DeviceGain;
                    final.ConditionAxisCount = directionalSource.ConditionAxisCount;
                    final.ConditionAxes = directionalSource.ConditionAxes;
                }
                else
                {
                    final.HasDirectionalData = false;
                    final.HasConditionData = false;
                }

                if (selectedDirectional != null)
                {
                    selected.HasDirectionalData = selectedDirectional.HasDirectionalData;
                    selected.HasConditionData = selectedDirectional.HasConditionData;
                    selected.EffectType = selectedDirectional.EffectType;
                    selected.SignedMagnitude = selectedDirectional.SignedMagnitude;
                    selected.Direction = selectedDirectional.Direction;
                    selected.Period = selectedDirectional.Period;
                    selected.DeviceGain = selectedDirectional.DeviceGain;
                    selected.ConditionAxisCount = selectedDirectional.ConditionAxisCount;
                    selected.ConditionAxes = selectedDirectional.ConditionAxes;
                }
                else
                {
                    selected.HasDirectionalData = false;
                    selected.HasConditionData = false;
                }
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
                // detector.DecayIfSilent / Sensitivity / CutoffHz are set
                // ONCE per polling tick by UpdateInputStates +
                // ApplyDetectorSettingsForTick. Calling them here would
                // multiply the decay rate and race the WASAPI callback's
                // read of _sensitivity, weakening audio rumble between
                // hits. ScaleRumbleForDevice just consumes MotorValue.
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
