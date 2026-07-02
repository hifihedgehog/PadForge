using System;
using PadForge.Common.Telemetry;
using PadForge.Engine;
using PadForge.Engine.Common;
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

                    // Idle disconnect countdown (#162). Tracks last activity at
                    // poll rate, checks the countdown ~1 Hz, and hands the
                    // radio I/O to the threadpool. DS4Windows gates mirrored:
                    // BT only, never while charging (DS4Device.cs:1437-1491).
                    UpdateIdleDisconnect(ud, newState);

                    // Touchpad gesture engine — runs once per device per
                    // tick, across every touchpad surface this device
                    // exposes. Settings come from the per-(device, pad)
                    // provider wired by the App layer against the active
                    // profile's PadSetting; defaults apply when unwired.
                    UpdateGestureContexts(ud, newState);

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
                // Impulse trigger motors keep the dispatcher's timer
                // alive same as main motors. Without this, the Xbox-VC →
                // DualSense AT Vibration auto-route never fires: a game
                // writing only impulse triggers (no main rumble, no
                // animated lightbar, no audio rumble) parks the timer
                // and the dispatcher never polls VibrationStates.
                bool hasGameRumble = raw != null && (
                    raw.LeftMotorSpeed > 0 || raw.RightMotorSpeed > 0
                    || raw.LeftTriggerMotorSpeed > 0 || raw.RightTriggerMotorSpeed > 0);
                // An active macro rumble override on a Sony slot needs
                // the dispatcher's timer running so the override actually
                // reaches the motors. Treat it as game-rumble equivalent
                // for timer-keepalive purposes — the dispatcher's per-
                // device rumble pump merges them via max() at write time.
                if (!hasGameRumble && MacroRumbleOverrides[padIndex].IsActive)
                    hasGameRumble = true;
                // A steering at-lock trigger-vibration pulse (#94 ch.2) likewise needs the
                // dispatcher's timer alive so the momentary block reaches the trigger
                // actuators when nothing else is driving the slot.
                if (!hasGameRumble && SteeringTrigVibOverrides[padIndex].IsActive)
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
                            // Either main-motor audio rumble OR audio-trigger
                            // rumble keeps the dispatcher alive; both pull
                            // from the same WASAPI capture / detector and
                            // both need the per-tick dispatch to apply.
                            if (ps.AudioRumbleEnabled == "1"
                                || ps.AudioRumbleTriggersEnabled == "1")
                                hasAudioRumbleEnabled = true;
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
                            // Constant trigger force: mirror the main-motor
                            // keepalive — same shape, trigger-motor analogue.
                            if (!hasGameRumble && ps.ConstantTriggerForceEnabled == "1"
                                && (ParseConstantForceComponent(ps.ConstantTriggerForceLeft) != 0.0
                                    || ParseConstantForceComponent(ps.ConstantTriggerForceRight) != 0.0))
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

        /// <summary>Idle disconnect countdown for one device (#162), the
        /// DS4Windows shape (DS4Device.cs:1437-1491): refresh the activity
        /// stamp whenever the input is non-idle, and once the stamp ages past
        /// the per-device timeout, drop the Bluetooth link on a worker so the
        /// controller sleeps. Gamepad-typed devices use the absolute idle
        /// test; everything else uses change detection against the previous
        /// poll. Never fires on USB paths or while charging.</summary>
        private static void UpdateIdleDisconnect(UserDevice ud, CustomInputState state)
        {
            long now = Environment.TickCount64;

            if (ud.IdleDisconnectSeconds <= 0)
            {
                ud.LastActiveTick = now;
                return;
            }

            bool idle = ud.CapType == InputDeviceType.Gamepad
                ? IdleInputDetector.IsGamepadIdle(state)
                : IdleInputDetector.IsUnchanged(state, ud.OldInputState);

            if (!idle || ud.LastActiveTick == 0)
            {
                ud.LastActiveTick = now;
                return;
            }

            // The countdown itself only needs ~1 Hz resolution.
            if (now - ud.LastIdleCheckTick < 1000) return;
            ud.LastIdleCheckTick = now;

            if (now - ud.LastActiveTick < ud.IdleDisconnectSeconds * 1000L) return;
            // No charging gate: SDL's power state was measured unreliable (a
            // full on-battery DualSense reads CHARGED), and disconnecting a
            // wall-charging pad from Bluetooth is harmless. It keeps charging.
            if (!PadForge.Common.Input.BluetoothLinkHelper.IsDisconnectTarget(ud.DevicePath)) return;

            // Re-arm so a failed or ignored disconnect retries after a full
            // countdown instead of hammering the radio every second.
            ud.LastActiveTick = now;

            ushort vid = ud.VendorId, pid = ud.ProdId;
            string path = ud.DevicePath, serial = ud.SerialNumber ?? string.Empty;
            string[] bthIds = ud.HidHideInstanceIds?.ToArray();
            PadForge.Common.Input.BluetoothLinkHelper.Trace(
                $"idle countdown fired: {ud.ResolvedName} after {ud.IdleDisconnectSeconds}s");
            System.Threading.Tasks.Task.Run(() =>
                PadForge.Common.Input.BluetoothLinkHelper.TryDisconnectDevice(vid, pid, path, serial, bthIds));
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

            // Xbox One+ routing: those devices skip the SDL-rumble path
            // entirely and write through XboxImpulseHidWriter (raw HID,
            // 9-byte BT or 13-byte GIP report). SDL's HasRumble flag is
            // ignored — every Microsoft Xbox One+ controller has rumble
            // + impulse-trigger motors as a hardware fact. We still
            // require ud.Device != null so we know the controller is
            // currently connected.
            bool isXboxImpulse = XboxControllerIdentity.IsImpulseTriggerDevice(ud.VendorId, ud.ProdId);
            // Native vendor FFB (Logitech / Fanatec wheels + Fanatec pedals) is
            // written via a custom HID output report (RawHidOutput), bypassing
            // SDL. These devices don't necessarily advertise standard HID
            // rumble/haptic and are intercepted BEFORE the SDL path, so they skip
            // the HasRumble/HasHaptic gate. Dispatched below by ud.DevicePath, so
            // ud.Device may be null. Strictly VID/PID-gated — non-vendor devices
            // fall through to the unchanged scalar/haptic path.
            bool isLogitechWheel = LogitechRawHidWriter.IsLogitechWheel(ud.VendorId, ud.ProdId);
            bool isFanatecWheel  = FanatecRawHidWriter.IsFanatecWheel(ud.VendorId, ud.ProdId);
            bool isFanatecPedal  = FanatecRawHidWriter.IsFanatecPedal(ud.VendorId, ud.ProdId);
            bool isThrustmasterWheel = ThrustmasterRawHidWriter.IsThrustmasterWheel(ud.VendorId, ud.ProdId);
            bool isVendorFfb = isLogitechWheel || isFanatecWheel || isFanatecPedal || isThrustmasterWheel;
            if (!isXboxImpulse && !isVendorFfb)
            {
                if (ud.Device == null || (!ud.Device.HasRumble && !ud.Device.HasHaptic))
                    return;
            }
            else if (isXboxImpulse && ud.Device == null)
            {
                return;
            }

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
            ushort combinedLT = 0, combinedRT = 0;
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

                // Constant-trigger-force evaluator: trigger-motor analogue
                // of ConstantForceEvaluator. When game/macro trigger
                // rumble is silent AND the impulse-triggers tab has
                // ConstantTriggerForce enabled, fills the trigger fields
                // with the user-set values. Same override-with-resume
                // semantics. Composes onto the main-motor result.
                if (_constantTriggerForceScratch == null) _constantTriggerForceScratch = new Vibration();
                effective = ConstantTriggerForceEvaluator.Resolve(effective, devicePs, _constantTriggerForceScratch);

                ScaleRumbleForDevice(effective.LeftMotorSpeed, effective.RightMotorSpeed,
                    devicePs, out ushort scaledL, out ushort scaledR);

                // Trigger rumble routing (#102): route the slot's post-gain
                // main-motor amplitude into the trigger channel when the
                // per-trigger activator is engaged. Computed from the pre-redirect
                // main motor; Redirect then silences the main motor(s) the route
                // drew from on this physical write.
                ApplyTriggerRouting(padIndex, scaledL, scaledR,
                    out ushort routedLT, out ushort routedRT,
                    out bool zeroMainL, out bool zeroMainR);
                if (zeroMainL) scaledL = 0;
                if (zeroMainR) scaledR = 0;

                if (scaledL > combinedL) combinedL = scaledL;
                if (scaledR > combinedR) combinedR = scaledR;

                // Impulse triggers: parallel max-combine. Pulls from
                // `effective` (post-constant-trigger-force resolution)
                // so the user-set static trigger pressure layers in.
                ScaleTriggerRumbleForDevice(effective.LeftTriggerMotorSpeed, effective.RightTriggerMotorSpeed,
                    devicePs, out ushort scaledLT, out ushort scaledRT);

                if (scaledLT > combinedLT) combinedLT = scaledLT;
                if (scaledRT > combinedRT) combinedRT = scaledRT;

                // #102 routed contribution layers onto the impulse output via max().
                if (routedLT > combinedLT) combinedLT = routedLT;
                if (routedRT > combinedRT) combinedRT = routedRT;

                // Steering at-lock trigger-vibration pulse (#94 ch.2) for Xbox-style
                // impulse triggers. DualSense routes this through the AT-vibration block in
                // UserEffectsDispatcher (which returns early for Sony pads above); this is
                // the impulse-trigger equivalent, layered onto the slot's trigger output
                // via max(). Injected raw (the pulse already carries its own strength) so
                // the cue is felt at a consistent level rather than scaled by the
                // per-device impulse gain meant for game rumble.
                float steerTrigVib = GetSteeringTrigVib(padIndex);
                if (steerTrigVib > 0f)
                {
                    ushort stv = (ushort)System.Math.Clamp((int)System.Math.Round(steerTrigVib * 65535f), 0, 65535);
                    if (stv > combinedLT) combinedLT = stv;
                    if (stv > combinedRT) combinedRT = stv;
                }

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
            _combinedVibration.LeftTriggerMotorSpeed = combinedLT;
            _combinedVibration.RightTriggerMotorSpeed = combinedRT;

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

            // Reverse output relay (#138): a "peer://" device lives on another PC.
            // Every config-baked value (post gain / audio-rumble / macro / constant
            // force, plus directional / condition data) is now in _combinedVibration.
            // Ship it to the owner for non-Sony, non-vendor-wheel devices — Xbox impulse
            // pads, generic gamepads, FFB sticks, and Fanatec pedals. Vendor wheels fall
            // through to their branch below, which ships the semantic wheel frame.
            if (RemoteLinkOutputRouter.IsPeerPath(ud.DevicePath)
                && !(isVendorFfb && !isFanatecPedal))
            {
                // Bake the consumer's Overall Strength into the directional/condition fields
                // before shipping: the owner replays with ForceOverall=100, and unlike the
                // scalar motors (already pre-scaled above) these are copied raw, so the slider
                // would otherwise be lost. DeviceGain stays raw — the owner applies it once.
                // Scale a COPY of the condition axes so the shared source state isn't mutated (#138 F30).
                if (_combinedVibration.HasDirectionalData || _combinedVibration.HasConditionData)
                {
                    int og = int.TryParse(firstPadSetting?.ForceOverall, out int fg) ? System.Math.Clamp(fg, 0, 100) : 100;
                    if (og != 100)
                    {
                        double s = og / 100.0;
                        _combinedVibration.SignedMagnitude = (short)System.Math.Clamp(_combinedVibration.SignedMagnitude * s, -10000, 10000);
                        var axes = _combinedVibration.ConditionAxes;
                        if (_combinedVibration.HasConditionData && axes != null && _combinedVibration.ConditionAxisCount > 0)
                        {
                            var scaled = (ConditionAxisData[])axes.Clone();
                            for (int ci = 0; ci < scaled.Length; ci++)
                            {
                                scaled[ci].PositiveCoefficient = (short)System.Math.Clamp(scaled[ci].PositiveCoefficient * s, -10000, 10000);
                                scaled[ci].NegativeCoefficient = (short)System.Math.Clamp(scaled[ci].NegativeCoefficient * s, -10000, 10000);
                            }
                            _combinedVibration.ConditionAxes = scaled;
                        }
                    }
                }
                RemoteLinkOutputRouter.ShipVibration(ud.DevicePath, _combinedVibration);
                return;
            }

            // Sole-writer guard (#138): this LOCAL device is also shared out and a remote
            // game is actively driving it (a relayed frame holds the output lease). Skip
            // the owner's local write so the inbound relay is the sole hardware writer.
            // One guard covers every class below — Xbox impulse, generic SDL rumble,
            // vendor FFB, and wheels. Lapses ~3 s after the remote falls quiet.
            if (RemoteLinkOutputRouter.IsClaimedByPeer(ud.DevicePath)) return;

            if (isXboxImpulse)
            {
                // Xbox One+ sole-writer path. PadForge writes the HID
                // output report (9-byte BT or 13-byte GIP) directly to
                // the physical controller. SDL_RumbleGamepad /
                // SDL_RumbleGamepadTriggers are NOT called for these
                // devices. Change-detection on ForceFeedbackState avoids
                // CreateFile + WriteFile churn at polling cadence.
                if (ud.ForceFeedbackState.TryRecordXboxImpulseSnapshot(
                        combinedL, combinedR, combinedLT, combinedRT))
                {
                    XboxImpulseHidWriter.Write(
                        ud, combinedL, combinedR, combinedLT, combinedRT);
                }
                return;
            }

            // Native vendor FFB dispatch. Wheels: project the directional force
            // onto the steering axis (shared ForceFeedbackState helper — same math
            // as the SDL single-axis haptic path) and send a constant force.
            // Fanatec pedals: map the combined L/R motors to the pedal rumble
            // motors. ud.DevicePath is the openable HID interface path.
            if (isVendorFfb)
            {
                int overallGain = int.TryParse(firstPadSetting?.ForceOverall, out int g)
                    ? System.Math.Clamp(g, 0, 100) : 100;
                if (isFanatecPedal)
                {
                    byte brake    = (byte)(combinedL >> 8); // XInput left  -> brake
                    byte throttle = (byte)(combinedR >> 8); // XInput right -> throttle
                    FanatecRawHidWriter.WritePedalRumble(ud.DevicePath, throttle, brake);
                }
                else // a wheel — Logitech / Fanatec / Thrustmaster
                {
                    var cv = _combinedVibration;
                    // Spring / damper / friction (game-driven condition effect) when
                    // present, else a constant force from the projected steering level.
                    bool hasCond = cv.HasConditionData && cv.ConditionAxisCount > 0 && cv.ConditionAxes != null;
                    var ca = hasCond ? cv.ConditionAxes[0] : default; // axis 0 = steering
                    short level = ForceFeedbackState.ComputeWheelSteeringLevel(cv, overallGain);
                    // Condition coefficients + clip scale by device gain too, matching the
                    // constant-force helper (ComputeWheelSteeringLevel) and the SDL path.
                    int condGain = overallGain * cv.DeviceGain / 255;
                    // XInput/Xbox targets send rumble (two motor magnitudes, no
                    // direction). A wheel has no rumble motor, so translate rumble into
                    // an oscillating constant force on the steering axis (a buzz),
                    // mirroring the Sine haptic strategy joysticks get in SetHapticForces.
                    // Real directional FFB takes precedence; rumble fills in otherwise.
                    short wheelForce = level != 0 ? level : ForceFeedbackState.ComputeWheelRumbleLevel(cv, overallGain);
                    // Auto-center strength (Wheel-tab slider). Fanatec has no firmware
                    // autocenter and ftec_set_range's f5 disables its stock spring, so
                    // Fanatec centering is a per-frame software spring (slot 1); Logitech
                    // and Thrustmaster use their firmware spring in the one-shot below.
                    int desAc = int.TryParse(firstPadSetting.AutoCenterStrength, out int acp) ? System.Math.Clamp(acp, 0, 100) : 0;
                    int acMag = desAc * 0xffff / 100; // 0..100% -> 0..0xffff
                    // Skip the HID write when the force/condition is identical to last poll —
                    // the wheel holds it, so re-sending is pure per-poll churn (the 1000->500 Hz
                    // drop with a wheel connected). Active FFB changes the signature each tick
                    // and still writes; only a steady force (idle, held spring) is throttled.
                    int periodicPeak = (isThrustmasterWheel && !hasCond && cv.HasDirectionalData && cv.Period > 0
                        && ForceFeedbackState.IsPeriodicEffect(cv.EffectType))
                        ? ForceFeedbackState.ComputeWheelSteeringPeak(cv, overallGain) : 0;
                    var ffbSig = new WheelFfbSig(hasCond, cv.HasDirectionalData, wheelForce, periodicPeak, acMag,
                        (int)cv.EffectType, (int)cv.Period,
                        hasCond ? (int)ca.PositiveCoefficient : 0, hasCond ? (int)ca.NegativeCoefficient : 0,
                        hasCond ? (int)ca.Offset : 0, hasCond ? (int)ca.DeadBand : 0,
                        hasCond ? (int)ca.PositiveSaturation : 0, hasCond ? (int)ca.NegativeSaturation : 0, condGain);

                    // Reverse output relay (#138): a "peer://" wheel lives on another PC.
                    // Ship the semantic steering frame (force/condition/periodic + range +
                    // RPM LEDs) and let the owner re-encode for ITS wheel's vendor/PID; the
                    // vendor writers' stateful upload/play caches must stay on the owner.
                    if (RemoteLinkOutputRouter.IsPeerPath(ud.DevicePath))
                    {
                        int peerRange = int.TryParse(firstPadSetting.RotationRange, out int prg)
                            ? System.Math.Clamp(prg, 40, 2520) : 900;
                        bool ledsOn = firstPadSetting.WheelRpmLeds == "1";
                        int ledMask = 0;
                        if (ledsOn)
                        {
                            TelemetryHub.RequestActive();
                            if (TelemetryHub.TryGetCurrent(out var ptel))
                            {
                                bool blinkOn = (Environment.TickCount / 60) % 2 == 0;
                                float frac = ptel.RpmFraction;
                                ledMask = isLogitechWheel ? RpmLedMap.Logitech(frac, blinkOn)
                                    : isFanatecWheel ? RpmLedMap.Fanatec(frac, blinkOn)
                                    : RpmLedMap.Thrustmaster(frac, blinkOn);
                            }
                        }
                        RemoteLinkOutputRouter.ShipWheel(ud.DevicePath,
                            hasCond, cv.HasDirectionalData, wheelForce, (short)periodicPeak, acMag,
                            cv.EffectType, (int)cv.Period,
                            hasCond ? ca.PositiveCoefficient : (short)0, hasCond ? ca.NegativeCoefficient : (short)0,
                            hasCond ? ca.Offset : (short)0, hasCond ? (int)ca.DeadBand : 0,
                            hasCond ? (int)ca.PositiveSaturation : 0, hasCond ? (int)ca.NegativeSaturation : 0, condGain,
                            (ushort)peerRange, (ushort)ledMask, ledsOn);
                        return;
                    }

                    if (_appliedWheelFfb.TryGetValue(ud.DevicePath, out var prevFfb) && prevFfb.Equals(ffbSig))
                    {
                        // Unchanged — the wheel already holds this force; skip the HID write.
                    }
                    else if (isLogitechWheel)
                    {
                        if (hasCond)
                            LogitechRawHidWriter.WriteCondition(ud.DevicePath, 0, cv.EffectType,
                                ca.PositiveCoefficient, ca.NegativeCoefficient, ca.Offset,
                                (int)ca.DeadBand, (int)ca.PositiveSaturation, (int)ca.NegativeSaturation, condGain,
                                LogitechRawHidWriter.HasFrictionCap(ud.ProdId));
                        else if (wheelForce == 0) LogitechRawHidWriter.WriteStopEffect(ud.DevicePath, 0);
                        else LogitechRawHidWriter.WriteConstantForce(ud.DevicePath, 0, wheelForce);
                    }
                    else if (isFanatecWheel)
                    {
                        if (hasCond)
                            FanatecRawHidWriter.WriteWheelCondition(ud.DevicePath, cv.EffectType,
                                ca.PositiveCoefficient, ca.NegativeCoefficient, ca.Offset,
                                (int)ca.DeadBand, (int)ca.PositiveSaturation, (int)ca.NegativeSaturation, condGain);
                        else
                        {
                            FanatecRawHidWriter.WriteWheelConstantForce(ud.DevicePath, wheelForce, ud.ProdId);
                            // Re-assert the software centering spring each frame so it
                            // survives a game-driven condition overwriting slot 1.
                            if (acMag > 0) FanatecRawHidWriter.WriteAutocenter(ud.DevicePath, acMag);
                        }
                    }
                    else // Thrustmaster wheel
                    {
                        if (hasCond)
                            ThrustmasterRawHidWriter.WriteCondition(ud.DevicePath, cv.EffectType,
                                ca.PositiveCoefficient, ca.NegativeCoefficient, ca.Offset,
                                (int)ca.DeadBand, (int)ca.PositiveSaturation, (int)ca.NegativeSaturation, condGain);
                        else if (cv.HasDirectionalData && cv.Period > 0 && ForceFeedbackState.IsPeriodicEffect(cv.EffectType))
                            // T300 firmware runs the waveform onboard (higher fidelity than
                            // host sampling). Pass the un-sampled steering peak as the amplitude
                            // — not the sampled level, which crosses zero mid-waveform.
                            ThrustmasterRawHidWriter.WritePeriodic(ud.DevicePath, cv.EffectType,
                                ForceFeedbackState.ComputeWheelSteeringPeak(cv, overallGain), (int)cv.Period);
                        else ThrustmasterRawHidWriter.WriteConstantForce(ud.DevicePath, wheelForce);
                    }
                    _appliedWheelFfb[ud.DevicePath] = ffbSig;

                    // Wheel settings (rotation range + auto-center) — one-shot,
                    // re-sent only when the persisted value changes.
                    int desRange = int.TryParse(firstPadSetting.RotationRange, out int rg) ? System.Math.Clamp(rg, 40, 2520) : 900; // per-wheel max enforced in each writer's WriteRange
                    if (!_appliedWheelSettings.TryGetValue(ud.DevicePath, out var prevWs) || prevWs.range != desRange || prevWs.ac != desAc)
                    {
                        bool applied;
                        if (isLogitechWheel)
                        {
                            applied  = LogitechRawHidWriter.WriteRange(ud.DevicePath, desRange, ud.ProdId);
                            applied &= LogitechRawHidWriter.WriteAutocenter(ud.DevicePath, acMag, LogitechRawHidWriter.IsMomo(ud.ProdId));
                        }
                        else if (isFanatecWheel)
                        {
                            applied  = FanatecRawHidWriter.WriteRange(ud.DevicePath, desRange, ud.ProdId); // f5 in the range sequence disables the firmware centering spring
                            applied &= FanatecRawHidWriter.WriteAutocenter(ud.DevicePath, acMag);          // software centering spring replaces it (slot 1); disables at 0
                        }
                        else
                        {
                            applied  = ThrustmasterRawHidWriter.WriteRange(ud.DevicePath, desRange, ud.ProdId);
                            applied &= ThrustmasterRawHidWriter.WriteAutocenter(ud.DevicePath, acMag);
                        }
                        // Cache only once the wheel actually accepted the settings.
                        // Latching on a failed write (device not ready on the first
                        // dispatch frame) would never re-send the auto-center disable,
                        // leaving the wheel at its firmware-default centering spring.
                        if (applied) _appliedWheelSettings[ud.DevicePath] = (desRange, desAc);
                    }

                    // RPM / shift LEDs from the running game's telemetry (Logitech
                    // 5-LED, Fanatec 9-LED rim, Thrustmaster 15-LED rim). Demand-
                    // driven: requesting telemetry starts the hub; it stops itself
                    // when no wheel asks. Re-sent only when the bitmask changes — the
                    // redline blink flips the mask, so it animates without per-tick
                    // HID churn. No telemetry (game closed / not racing) resolves to
                    // mask 0, so the strip clears instead of freezing on last frame.
                    if (firstPadSetting.WheelRpmLeds == "1" && (isLogitechWheel || isFanatecWheel || isThrustmasterWheel))
                    {
                        TelemetryHub.RequestActive();
                        int mask = 0;
                        if (TelemetryHub.TryGetCurrent(out var tel))
                        {
                            bool blinkOn = (Environment.TickCount / 60) % 2 == 0;
                            float frac = tel.RpmFraction;
                            if (isLogitechWheel) mask = RpmLedMap.Logitech(frac, blinkOn);
                            else if (isFanatecWheel) mask = RpmLedMap.Fanatec(frac, blinkOn);
                            else mask = RpmLedMap.Thrustmaster(frac, blinkOn);
                        }
                        if (!_appliedLeds.TryGetValue(ud.DevicePath, out int prevMask) || prevMask != mask)
                        {
                            if (isLogitechWheel) LogitechRawHidWriter.WriteRpmLeds(ud.DevicePath, (byte)mask);
                            else if (isFanatecWheel) FanatecRawHidWriter.WriteRpmLeds(ud.DevicePath, mask);
                            else ThrustmasterRawHidWriter.WriteRpmLeds(ud.DevicePath, mask);
                            _appliedLeds[ud.DevicePath] = mask;
                        }
                    }
                    else if (_appliedLeds.TryGetValue(ud.DevicePath, out int litMask) && litMask != 0)
                    {
                        // Feature turned off — clear the strip once.
                        if (isLogitechWheel) LogitechRawHidWriter.WriteRpmLeds(ud.DevicePath, 0);
                        else if (isFanatecWheel) FanatecRawHidWriter.WriteRpmLeds(ud.DevicePath, 0);
                        else if (isThrustmasterWheel) ThrustmasterRawHidWriter.WriteRpmLeds(ud.DevicePath, 0);
                        _appliedLeds[ud.DevicePath] = 0;
                    }
                }
                return;
            }

            ud.ForceFeedbackState.SetDeviceForces(ud, ud.Device, firstPadSetting, _combinedVibration);
        }

        private Vibration _combinedVibration;

        // Per-device last-applied wheel rotation range + auto-center, so those
        // one-shot settings are only re-sent to the wheel when they change.
        // ConcurrentDictionary: the polling thread writes these every tick a
        // wheel is assigned, while MarkDeviceOffline removes entries from a
        // ThreadPool thread (web/overlay disconnect); a plain Dictionary
        // resizing on the poll thread during that removal is undefined behavior.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int range, int ac)> _appliedWheelSettings = new();

        // Per-device last-applied RPM LED bitmask, so the strip is only re-sent
        // when it changes (steady RPM = no write; blink/step = write on change).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _appliedLeds = new();

        // Per-device last-applied wheel FFB force/condition. FFB is stateful — the wheel
        // firmware holds the last force until changed — so re-sending an unchanged force
        // every poll is a blocking HID write that halves the poll rate while a wheel is
        // connected (worst at idle: a steady stop/zero force re-sent every tick). Write the
        // force only when this signature changes.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WheelFfbSig> _appliedWheelFfb = new();
        private readonly struct WheelFfbSig : System.IEquatable<WheelFfbSig>
        {
            public readonly bool HasCond, Dir; public readonly short Force;
            public readonly int Peak, Ac, Effect, Period, Pc, Nc, Off, Db, Ps, Ns, CondGain;
            public WheelFfbSig(bool hasCond, bool dir, short force, int peak, int ac, int effect, int period,
                int pc, int nc, int off, int db, int ps, int ns, int condGain)
            { HasCond = hasCond; Dir = dir; Force = force; Peak = peak; Ac = ac; Effect = effect; Period = period;
              Pc = pc; Nc = nc; Off = off; Db = db; Ps = ps; Ns = ns; CondGain = condGain; }
            public bool Equals(WheelFfbSig o) => HasCond == o.HasCond && Dir == o.Dir && Force == o.Force && Peak == o.Peak
                && Ac == o.Ac && Effect == o.Effect && Period == o.Period && Pc == o.Pc && Nc == o.Nc && Off == o.Off
                && Db == o.Db && Ps == o.Ps && Ns == o.Ns && CondGain == o.CondGain;
            public override bool Equals(object o) => o is WheelFfbSig w && Equals(w);
            public override int GetHashCode() => System.HashCode.Combine(Force, Effect, Period, Pc, Nc, Off, CondGain, Ac);
        }

        // Per-slot scratch buffer reused across iterations of the
        // ApplyForceFeedback per-slot loop — the evaluator only writes
        // when the override fires, otherwise it returns the raw input
        // unchanged. Populating a fresh Vibration per tick would allocate
        // on every device with multi-slot mappings.
        private Vibration _constantForceScratch;

        // Constant-trigger-force evaluator's scratch — same shape as
        // _constantForceScratch but composes onto the post-main-constant-
        // force result so both layers can be active simultaneously.
        private Vibration _constantTriggerForceScratch;

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

            // Two independent walks: first slot with main-motor audio
            // rumble enabled drives the detector's main filter chain;
            // first slot with audio-trigger rumble enabled drives the
            // detector's parallel trigger filter chain. The two paths
            // are decoupled so the Impulse Triggers tab does not
            // inherit Force Feedback's bass cutoff / sensitivity.
            bool mainSet = false;
            bool triggerSet = false;
            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                if (mainSet && triggerSet) break;
                Guid selected = SelectedDeviceGuids[padIndex];
                var slotSettings = settings.FindByPadIndex(padIndex);
                if (slotSettings == null || slotSettings.Count == 0) continue;
                // Prefer SelectedMappedDevice's PadSetting (matches the
                // tab the user is editing); fall back to the first
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

                if (!mainSet && ps.AudioRumbleEnabled == "1")
                {
                    detector.Sensitivity = TryParseFloat(ps.AudioRumbleSensitivity, 4f);
                    detector.CutoffHz = TryParseFloat(ps.AudioRumbleCutoffHz, 80f);
                    mainSet = true;
                }
                if (!triggerSet && ps.AudioRumbleTriggersEnabled == "1")
                {
                    detector.TriggerSensitivity = TryParseFloat(ps.AudioRumbleTriggersSensitivity, 4f);
                    detector.TriggerCutoffHz = TryParseFloat(ps.AudioRumbleTriggersCutoffHz, 80f);
                    triggerSet = true;
                }
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
                    final.LeftTriggerMotorSpeed = raw.LeftTriggerMotorSpeed;
                    final.RightTriggerMotorSpeed = raw.RightTriggerMotorSpeed;
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
                    selected.LeftTriggerMotorSpeed = 0;
                    selected.RightTriggerMotorSpeed = 0;
                    selected.HasDirectionalData = false;
                    selected.HasConditionData = false;
                    continue;
                }

                ushort bestLT = 0, bestRT = 0;
                ushort selLT = 0, selRT = 0;

                for (int i = 0; i < slotCount; i++)
                {
                    var us = _instanceGuidBuffer[i];
                    if (us == null) continue;
                    var devicePs = us.GetPadSetting();

                    if (_constantForceScratch == null) _constantForceScratch = new Vibration();
                    var effective = ConstantForceEvaluator.Resolve(withMacro, devicePs, _constantForceScratch);

                    ScaleRumbleForDevice(effective.LeftMotorSpeed, effective.RightMotorSpeed,
                        devicePs, out ushort scaledL, out ushort scaledR);

                    // #102 trigger routing for the motor meters: mirror the
                    // hardware path so the FFB-tab meter reflects what the user is
                    // tuning the route Scale against.
                    ApplyTriggerRouting(padIndex, scaledL, scaledR,
                        out ushort routedLT, out ushort routedRT,
                        out bool zeroMainL, out bool zeroMainR);
                    if (zeroMainL) scaledL = 0;
                    if (zeroMainR) scaledR = 0;

                    if (scaledL > bestL) bestL = scaledL;
                    if (scaledR > bestR) bestR = scaledR;

                    ScaleTriggerRumbleForDevice(raw.LeftTriggerMotorSpeed, raw.RightTriggerMotorSpeed,
                        devicePs, out ushort scaledLT, out ushort scaledRT);

                    if (scaledLT > bestLT) bestLT = scaledLT;
                    if (scaledRT > bestRT) bestRT = scaledRT;
                    if (routedLT > bestLT) bestLT = routedLT;
                    if (routedRT > bestRT) bestRT = routedRT;

                    if (directionalSource == null
                        && (effective.HasDirectionalData || effective.HasConditionData))
                        directionalSource = effective;

                    // Capture the selected device's own scaled output for
                    // the FFB-tab meter.
                    if (selectedGuid != Guid.Empty && us.InstanceGuid == selectedGuid)
                    {
                        selL = scaledL;
                        selR = scaledR;
                        selLT = (ushort)System.Math.Max(scaledLT, routedLT);
                        selRT = (ushort)System.Math.Max(scaledRT, routedRT);
                        if (effective.HasDirectionalData || effective.HasConditionData)
                            selectedDirectional = effective;
                    }
                }

                final.LeftMotorSpeed = bestL;
                final.RightMotorSpeed = bestR;
                final.LeftTriggerMotorSpeed = bestLT;
                final.RightTriggerMotorSpeed = bestRT;
                selected.LeftMotorSpeed = selL;
                selected.RightMotorSpeed = selR;
                selected.LeftTriggerMotorSpeed = selLT;
                selected.RightTriggerMotorSpeed = selRT;

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
        /// <summary>
        /// Applies per-device impulse-trigger scaling: per-trigger
        /// strength (<c>ImpulseLeftStrength</c> / <c>ImpulseRightStrength</c>)
        /// × impulse-tab overall gain (<c>ImpulseOverallGain</c>) +
        /// <c>ImpulseSwapTriggers</c>. The impulse-triggers tab now owns
        /// its own overall-gain slider so it doesn't share the main-motor
        /// gain (<c>ForceOverall</c>). No audio-rumble mix on the trigger
        /// path — impulse triggers carry game-driven content only.
        /// </summary>
        public void ScaleTriggerRumbleForDevice(
            ushort rawLeft, ushort rawRight, PadSetting ps,
            out ushort scaledLeft, out ushort scaledRight)
        {
            ushort baseL = rawLeft;
            ushort baseR = rawRight;

            // Audio-trigger rumble: same MotorValue from the shared
            // AudioBassDetector that the main-motor path consumes,
            // scaled by the per-trigger AudioRumbleLeftTrigger /
            // AudioRumbleRightTrigger. Detector params (sensitivity,
            // cutoff) are set once per tick by ApplyDetectorSettingsForTick
            // — no work here, just read MotorValue.
            var detector = AudioBassDetector;
            if (detector != null && ps != null && ps.AudioRumbleTriggersEnabled == "1")
            {
                // Trigger path reads the detector's parallel filter chain
                // (TriggerMotorValue), driven by AudioRumbleTriggers*
                // settings — independent of the main-motor path.
                ushort motorVal = detector.TriggerMotorValue;
                float leftScale = TryParseFloat(ps.AudioRumbleLeftTrigger, 100f) / 100f;
                float rightScale = TryParseFloat(ps.AudioRumbleRightTrigger, 100f) / 100f;
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
                overallGain = Math.Clamp(TryParseInt(ps.ImpulseOverallGain, 100), 0, 100);
                leftGain = Math.Clamp(TryParseInt(ps.ImpulseLeftStrength, 100), 0, 100);
                rightGain = Math.Clamp(TryParseInt(ps.ImpulseRightStrength, 100), 0, 100);
                swap = TryParseBool(ps.ImpulseSwapTriggers);
            }
            double sL = baseL * (leftGain / 100.0) * (overallGain / 100.0);
            double sR = baseR * (rightGain / 100.0) * (overallGain / 100.0);
            ushort finalL = (ushort)Math.Clamp(sL, 0, 65535);
            ushort finalR = (ushort)Math.Clamp(sR, 0, 65535);
            if (swap) (finalL, finalR) = (finalR, finalL);
            scaledLeft = finalL;
            scaledRight = finalR;
        }

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
