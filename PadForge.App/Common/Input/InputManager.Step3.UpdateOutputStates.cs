using System;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 3: UpdateOutputStates
        //  Maps each device's CustomInputState to a Gamepad struct
        //  based on the PadSetting mapping rules configured for that device.
        //
        //  Each UserSetting links a device (InstanceGuid) to a pad slot (MapTo 0–15)
        //  and references a PadSetting that contains the mapping rules.
        //
        //  PadSetting string fields like "ButtonA", "LeftThumbAxisX", etc. contain
        //  mapping descriptors in the format: "{MapType} {Index}" or "IH{MapType} {Index}"
        //  (IH prefix = inverted/half-axis). Examples:
        //    "Button 0"  → Button index 0
        //    "Axis 1"    → Axis index 1
        //    "IHAxis 2"  → Axis index 2, inverted half
        //    "POV 0 Up"  → POV 0, up direction
        //    "Slider 0"  → Slider index 0
        // ─────────────────────────────────────────────

        /// <summary>
        /// Step 3: For each device with a valid UserSetting + PadSetting, map its
        /// <see cref="CustomInputState"/> to a <see cref="Gamepad"/> and store the
        /// result on the UserSetting for later combination in Step 4.
        /// </summary>
        private void UpdateOutputStates()
        {
            var settings = SettingsManager.UserSettings?.Items;
            if (settings == null) return;

            // Reset per-slot multi-source row evaluation tracking so
            // the new frame's first device pass triggers fresh cross-
            // device evaluation. Every multi-source row (Sum, Average,
            // AND, XOR, Custom, etc.) evaluates row.Sources once
            // across all devices; subsequent device passes in the
            // same frame skip rows already written.
            BeginFrameMultiSourceTracking();

            // Clear the per-slot raw touchpad-click flags; they're re-OR'd per device below.
            System.Array.Clear(SlotRawTouchpadClick, 0, SlotRawTouchpadClick.Length);

            // Snapshot settings into pre-allocated buffer (no LINQ allocation).
            int snapshotCount;
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                if (_settingSnapshotBuffer.Length < settings.Count)
                    _settingSnapshotBuffer = new UserSetting[settings.Count];

                snapshotCount = 0;
                for (int i = 0; i < settings.Count; i++)
                    _settingSnapshotBuffer[snapshotCount++] = settings[i];
            }

            for (int si = 0; si < snapshotCount; si++)
            {
                var us = _settingSnapshotBuffer[si];
                try
                {
                    // Find the device for this setting.
                    UserDevice ud = FindOnlineDeviceByInstanceGuid(us.InstanceGuid);
                    if (ud == null)
                    {
                        us.OutputState = default;
                        us.RawMappedState = default; // preview must not freeze on a removed device
                        continue;
                    }
                    // Device exists but input temporarily unavailable — keep
                    // last valid OutputState to prevent transient zero glitches
                    // (e.g. output controller reading during state refresh).
                    if (!ud.IsOnline || ud.InputState == null)
                        continue;

                    // Get the PadSetting with mapping rules.
                    PadSetting ps = us.GetPadSetting();
                    if (ps == null)
                        continue;

                    // Map the input state to a gamepad. Phase 1c-2 routes
                    // descriptor reading through MappingSet when one is
                    // available for this slot; tuning still comes from
                    // PadSetting until Phase 1c-3.
                    int slotIndex = us.MapTo;
                    MappingSet ms = (slotIndex >= 0 && slotIndex < SettingsManager.SlotMappingSets.Length)
                        ? SettingsManager.SlotMappingSets[slotIndex]
                        : null;
                    Gamepad rawMapped;
                    if (ms != null && ms.Rows != null && ms.Rows.Count > 0)
                    {
                        us.OutputState = MapInputToGamepadFromMappingSet(
                            ud.InputState, ms, us.InstanceGuid.ToString(), ps, slotIndex, out rawMapped);
                    }
                    else
                    {
                        us.OutputState = MapInputToGamepad(ud.InputState, ps, out rawMapped);
                    }
                    us.RawMappedState = rawMapped;

                    // Raw physical touchpad click (SDL_GAMEPAD_BUTTON_TOUCHPAD = Buttons[16]),
                    // OR'd into the per-slot flag for the InputReactive lightbar so the press
                    // flashes regardless of virtual-controller type or click mapping. Done here
                    // (not via TouchpadOutputState) because that's only computed for PlayStation
                    // slots and reflects the click's mapping, not the physical press.
                    if (slotIndex >= 0 && slotIndex < MaxPads)
                    {
                        var rawButtons = ud.InputState.Buttons;
                        if (rawButtons != null && rawButtons.Length > 16 && rawButtons[16])
                            SlotRawTouchpadClick[slotIndex] = true;
                    }

                    // Steering at-lock haptic feedback (#94). The MappingSet eval above
                    // updated this frame's lock state; fire the opt-in channels now.
                    if (ms != null && ms.Rows != null && ms.Rows.Count > 0)
                        ApplySteeringLockFeedback(ms, slotIndex, ps, ud);

                    // All non-gamepad output paths route per-target descriptor
                    // reads through the per-VC MappingSet when a Base-layer row
                    // exists (multi-source, combine-mode, Custom-formula aware).
                    // ps + the legacy single-source PadSetting fields stay live
                    // as the fallback for configs that haven't been resaved
                    // since the multi-source UI shipped.
                    string deviceGuidStr = us.InstanceGuid.ToString();

                    // For custom Extended slots, also produce the raw Extended output state.
                    int slot = slotIndex;
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.Extended &&
                        SlotExtendedIsCustom[slot])
                    {
                        var cfg = SlotCustomLayouts[slot];
                        us.ExtendedRawOutputState = MapInputToExtendedRaw(
                            ud.InputState, ps, cfg, ms, deviceGuidStr, slot);
                    }

                    // For MIDI slots, produce the raw MIDI output state.
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.Midi)
                    {
                        var mc = _midiConfigs[slot];
                        if (mc != null)
                            us.MidiRawOutputState = MapInputToMidiRaw(
                                ud.InputState, ps, mc.CcCount, mc.NoteCount,
                                ms, deviceGuidStr, slot);
                    }

                    // For KeyboardMouse slots, produce the raw KBM output state.
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.KeyboardMouse)
                    {
                        us.KbmRawOutputState = MapInputToKbmRaw(
                            ud.InputState, ps, ms, deviceGuidStr, slot);
                    }

                    // For PlayStation slots, produce touchpad state from input device.
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.PlayStation)
                    {
                        us.TouchpadOutputState = MapInputToTouchpad(
                            ud.InputState, ps, us.TouchpadOutputState,
                            ms, deviceGuidStr, slot);
                    }
                }
                catch (Exception ex)
                {
                    // Don't zero OutputState — keep last valid state to prevent
                    // transient glitches from propagating through the pipeline.
                    RaiseError($"Error mapping device {us.InstanceGuid}", ex);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Mapping engine
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a <see cref="CustomInputState"/> to a <see cref="Gamepad"/> using
        /// the mapping rules defined in a <see cref="PadSetting"/>.
        /// </summary>
        /// <param name="state">The device's current input state.</param>
        /// <param name="ps">The PadSetting containing mapping rules.</param>
        /// <returns>A populated Gamepad struct.</returns>
        private static Gamepad MapInputToGamepad(CustomInputState state, PadSetting ps, out Gamepad rawMapped)
        {
            rawMapped = default;
            var gp = new Gamepad();
            int gt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            // ── Buttons ──
            if (MapToButtonPressed(state, ps.ButtonA, TryParseIntStatic(ps.GetMappingDeadZone("ButtonA"), 0), gt, ps.GetMappingBidirectional("ButtonA") == "1"))
                gp.SetButton(Gamepad.A, true);
            if (MapToButtonPressed(state, ps.ButtonB, TryParseIntStatic(ps.GetMappingDeadZone("ButtonB"), 0), gt, ps.GetMappingBidirectional("ButtonB") == "1"))
                gp.SetButton(Gamepad.B, true);
            if (MapToButtonPressed(state, ps.ButtonX, TryParseIntStatic(ps.GetMappingDeadZone("ButtonX"), 0), gt, ps.GetMappingBidirectional("ButtonX") == "1"))
                gp.SetButton(Gamepad.X, true);
            if (MapToButtonPressed(state, ps.ButtonY, TryParseIntStatic(ps.GetMappingDeadZone("ButtonY"), 0), gt, ps.GetMappingBidirectional("ButtonY") == "1"))
                gp.SetButton(Gamepad.Y, true);

            if (MapToButtonPressed(state, ps.LeftShoulder, TryParseIntStatic(ps.GetMappingDeadZone("LeftShoulder"), 0), gt, ps.GetMappingBidirectional("LeftShoulder") == "1"))
                gp.SetButton(Gamepad.LEFT_SHOULDER, true);
            if (MapToButtonPressed(state, ps.RightShoulder, TryParseIntStatic(ps.GetMappingDeadZone("RightShoulder"), 0), gt, ps.GetMappingBidirectional("RightShoulder") == "1"))
                gp.SetButton(Gamepad.RIGHT_SHOULDER, true);

            if (MapToButtonPressed(state, ps.ButtonBack, TryParseIntStatic(ps.GetMappingDeadZone("ButtonBack"), 0), gt, ps.GetMappingBidirectional("ButtonBack") == "1"))
                gp.SetButton(Gamepad.BACK, true);
            if (MapToButtonPressed(state, ps.ButtonStart, TryParseIntStatic(ps.GetMappingDeadZone("ButtonStart"), 0), gt, ps.GetMappingBidirectional("ButtonStart") == "1"))
                gp.SetButton(Gamepad.START, true);

            if (MapToButtonPressed(state, ps.LeftThumbButton, TryParseIntStatic(ps.GetMappingDeadZone("LeftThumbButton"), 0), gt, ps.GetMappingBidirectional("LeftThumbButton") == "1"))
                gp.SetButton(Gamepad.LEFT_THUMB, true);
            if (MapToButtonPressed(state, ps.RightThumbButton, TryParseIntStatic(ps.GetMappingDeadZone("RightThumbButton"), 0), gt, ps.GetMappingBidirectional("RightThumbButton") == "1"))
                gp.SetButton(Gamepad.RIGHT_THUMB, true);

            if (MapToButtonPressed(state, ps.ButtonGuide, TryParseIntStatic(ps.GetMappingDeadZone("ButtonGuide"), 0), gt, ps.GetMappingBidirectional("ButtonGuide") == "1"))
                gp.SetButton(Gamepad.GUIDE, true);

            // Xbox Series Share button — sits outside the 16-bit Buttons
            // mask. HM drops the bit on profiles whose descriptor doesn't
            // declare button 13, so always-mapping is safe even if the
            // active profile isn't Xbox Series.
            if (MapToButtonPressed(state, ps.ButtonShare, TryParseIntStatic(ps.GetMappingDeadZone("ButtonShare"), 0), gt, ps.GetMappingBidirectional("ButtonShare") == "1"))
                gp.Share = true;

            // ── D-Pad ──
            // Individual direction mappings take priority. Only fall back to
            // the combined DPad descriptor if no individual directions are set.
            bool hasIndividualDPad = !string.IsNullOrEmpty(ps.DPadUp)
                                 || !string.IsNullOrEmpty(ps.DPadDown)
                                 || !string.IsNullOrEmpty(ps.DPadLeft)
                                 || !string.IsNullOrEmpty(ps.DPadRight);

            if (hasIndividualDPad)
            {
                if (MapToButtonPressed(state, ps.DPadUp, TryParseIntStatic(ps.GetMappingDeadZone("DPadUp"), 0), gt, ps.GetMappingBidirectional("DPadUp") == "1"))
                    gp.SetButton(Gamepad.DPAD_UP, true);
                if (MapToButtonPressed(state, ps.DPadDown, TryParseIntStatic(ps.GetMappingDeadZone("DPadDown"), 0), gt, ps.GetMappingBidirectional("DPadDown") == "1"))
                    gp.SetButton(Gamepad.DPAD_DOWN, true);
                if (MapToButtonPressed(state, ps.DPadLeft, TryParseIntStatic(ps.GetMappingDeadZone("DPadLeft"), 0), gt, ps.GetMappingBidirectional("DPadLeft") == "1"))
                    gp.SetButton(Gamepad.DPAD_LEFT, true);
                if (MapToButtonPressed(state, ps.DPadRight, TryParseIntStatic(ps.GetMappingDeadZone("DPadRight"), 0), gt, ps.GetMappingBidirectional("DPadRight") == "1"))
                    gp.SetButton(Gamepad.DPAD_RIGHT, true);
            }
            else
            {
                // Legacy/combined: extract all 4 directions from a single POV hat.
                MapDPadFromPov(state, ps.DPad, ref gp);
            }

            // ── Triggers ──
            gp.LeftTrigger = MapToTrigger(state, ps.LeftTrigger);
            gp.RightTrigger = MapToTrigger(state, ps.RightTrigger);

            // ── Thumbsticks ──
            gp.ThumbLX = MapToThumbAxisWithNeg(state, ps.LeftThumbAxisX, ps.LeftThumbAxisXNeg);
            gp.ThumbLY = NegateAxis(MapToThumbAxisWithNeg(state, ps.LeftThumbAxisY, ps.LeftThumbAxisYNeg));
            gp.ThumbRX = MapToThumbAxisWithNeg(state, ps.RightThumbAxisX, ps.RightThumbAxisXNeg);
            gp.ThumbRY = NegateAxis(MapToThumbAxisWithNeg(state, ps.RightThumbAxisY, ps.RightThumbAxisYNeg));

            // Snapshot raw mapped state (after axis selection, before DZ processing)
            // for the UI preview so it can apply its own pipeline without double-processing.
            rawMapped = gp;

            ApplyPadSettingTuning(ref gp, ps);
            return gp;
        }

        /// <summary>
        /// Issue #61 multi-source/shift Phase 1c-2 entry point. Reads
        /// descriptors from a per-VC <see cref="MappingSet"/> instead of
        /// the legacy per-(VC × Device) <see cref="PadSetting"/> mapping
        /// fields. Per-source <c>DeviceGuid</c> filters which row sources
        /// contribute on this pass; sources for other devices are ignored
        /// here and contribute via Step 4's cross-device combine. Tuning
        /// (deadzones, curves, center offsets) still comes from
        /// <paramref name="ps"/> until Phase 1c-3 moves them to
        /// <see cref="DeviceTuning"/>.
        /// </summary>
        private static Gamepad MapInputToGamepadFromMappingSet(
            CustomInputState state,
            MappingSet mappingSet,
            string thisDeviceGuid,
            PadSetting ps,
            int slotIndex,
            out Gamepad rawMapped)
        {
            rawMapped = default;
            var gp = new Gamepad();

            int gt = TryParseIntStatic(ps?.AxisToButtonThreshold, 50);
            ApplyMappingSetToGamepad(state, mappingSet, thisDeviceGuid, gt, slotIndex, ref gp);

            rawMapped = gp;
            ApplyPadSettingTuning(ref gp, ps);
            return gp;
        }

        /// <summary>
        /// Applies the per-(VC × Device) tuning shared by both Step 3
        /// paths: trigger deadzones / curves, stick center offsets,
        /// stick deadzones / curves / shape. Today these read from
        /// <see cref="PadSetting"/>; Phase 1c-3 will move them to
        /// <see cref="DeviceTuning"/> while keeping this signature.
        /// </summary>
        private static void ApplyPadSettingTuning(ref Gamepad gp, PadSetting ps)
        {
            if (ps == null) return;

            // ── Trigger deadzones ──
            gp.LeftTrigger = ApplyTriggerDeadZone(gp.LeftTrigger,
                TryParseDoubleStatic(ps.LeftTriggerDeadZone, 0),
                TryParseDoubleStatic(ps.LeftTriggerAntiDeadZone, 0),
                TryParseDoubleStatic(ps.LeftTriggerMaxRange, 100),
                Common.CurveLut.GetOrBuild(ps.LeftTriggerSensitivityCurve));
            gp.RightTrigger = ApplyTriggerDeadZone(gp.RightTrigger,
                TryParseDoubleStatic(ps.RightTriggerDeadZone, 0),
                TryParseDoubleStatic(ps.RightTriggerAntiDeadZone, 0),
                TryParseDoubleStatic(ps.RightTriggerMaxRange, 100),
                Common.CurveLut.GetOrBuild(ps.RightTriggerSensitivityCurve));

            // ── Center offsets (applied before deadzone) ──
            gp.ThumbLX = ApplyCenterOffset(gp.ThumbLX, TryParseDoubleStatic(ps.LeftThumbCenterOffsetX, 0));
            gp.ThumbLY = ApplyCenterOffset(gp.ThumbLY, TryParseDoubleStatic(ps.LeftThumbCenterOffsetY, 0));
            gp.ThumbRX = ApplyCenterOffset(gp.ThumbRX, TryParseDoubleStatic(ps.RightThumbCenterOffsetX, 0));
            gp.ThumbRY = ApplyCenterOffset(gp.ThumbRY, TryParseDoubleStatic(ps.RightThumbCenterOffsetY, 0));

            // ── Dead zones ──
            ApplyDeadZone(ref gp.ThumbLX, ref gp.ThumbLY,
                TryParseDoubleStatic(ps.LeftThumbDeadZoneX, 0),
                TryParseDoubleStatic(ps.LeftThumbDeadZoneY, 0),
                TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneX, 0),
                TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneY, 0),
                TryParseDoubleStatic(ps.LeftThumbLinear, 0),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeXNeg, TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100)),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeYNeg, TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100)),
                Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveX),
                Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveY),
                ParseDeadZoneShape(ps.LeftThumbDeadZoneShape));

            ApplyDeadZone(ref gp.ThumbRX, ref gp.ThumbRY,
                TryParseDoubleStatic(ps.RightThumbDeadZoneX, 0),
                TryParseDoubleStatic(ps.RightThumbDeadZoneY, 0),
                TryParseDoubleStatic(ps.RightThumbAntiDeadZoneX, 0),
                TryParseDoubleStatic(ps.RightThumbAntiDeadZoneY, 0),
                TryParseDoubleStatic(ps.RightThumbLinear, 0),
                TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100),
                TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100),
                TryParseDoubleStatic(ps.RightThumbMaxRangeXNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100)),
                TryParseDoubleStatic(ps.RightThumbMaxRangeYNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100)),
                Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveX),
                Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveY),
                ParseDeadZoneShape(ps.RightThumbDeadZoneShape));
        }

        /// <summary>
        /// Negates a signed short axis value. Clamps short.MinValue to short.MaxValue
        /// to avoid overflow (since -(-32768) overflows short).
        /// Used to correct Y-axis orientation: the unsigned pipeline produces 0=up
        /// which maps to negative signed values, but XInput convention is positive Y = up.
        /// </summary>
        private static short NegateAxis(short value)
            => value == short.MinValue ? short.MaxValue : (short)-value;

        // ─────────────────────────────────────────────
        //  Mapping descriptor parser
        // ─────────────────────────────────────────────

        /// <summary>
        /// Parsing result for a PadSetting mapping descriptor string.
        /// </summary>
        private struct MappingDescriptor
        {
            public MapType Type;
            public int Index;
            public bool Inverted;
            public bool HalfAxis;
            public string PovDirection; // "Up", "Down", "Left", "Right" (for POV)
            public bool IsValid;
        }

        /// <summary>
        /// Parses a mapping descriptor string like "Button 0", "Axis 1", "IHAxis 2",
        /// "POV 0 Up", "Slider 0" into its components.
        /// </summary>
        private static MappingDescriptor ParseDescriptor(string descriptor)
        {
            var result = new MappingDescriptor();

            if (string.IsNullOrWhiteSpace(descriptor) || descriptor == "0")
                return result;

            string s = descriptor.Trim();

            // Check for invert/half prefix.
            if (s.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
            {
                result.Inverted = true;
                result.HalfAxis = true;
                s = s.Substring(2);
            }
            else if (s.StartsWith("H", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            {
                result.HalfAxis = true;
                s = s.Substring(1);
            }
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(s))
            {
                result.Inverted = true;
                s = s.Substring(1);
            }

            // Split remaining into parts.
            string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return result;

            // Parse type.
            string typeName = parts[0].ToLowerInvariant();
            switch (typeName)
            {
                case "axis":
                    result.Type = MapType.Axis;
                    break;
                case "button":
                    result.Type = MapType.Button;
                    break;
                case "slider":
                    result.Type = MapType.Slider;
                    break;
                case "pov":
                    result.Type = MapType.POV;
                    break;
                default:
                    return result;
            }

            // Parse index.
            if (!int.TryParse(parts[1], out int index))
                return result;

            result.Index = index;

            // Parse POV direction if present.
            if (result.Type == MapType.POV && parts.Length >= 3)
            {
                result.PovDirection = parts[2];
            }

            result.IsValid = true;
            return result;
        }

        // ─────────────────────────────────────────────
        //  Button mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a descriptor (or pipe-separated list of descriptors) to a boolean button press.
        /// Multiple descriptors are OR'd: if ANY source is active, the button is pressed.
        /// 
        /// For buttons: returns true if the button is pressed.
        /// For axes: returns true if the axis exceeds a threshold (75%).
        /// For POV: returns true if the POV matches the specified direction.
        /// 
        /// Examples:
        ///   "Button 0"             → single source
        ///   "Button 0|Button 5"    → pressed if either Button 0 OR Button 5 is pressed
        ///   "Button 3|Axis 2"      → pressed if Button 3 is pressed OR Axis 2 exceeds threshold
        /// </summary>
        private static bool MapToButtonPressed(CustomInputState state, string descriptor,
            int deadZonePercent = 0, int globalThresholdPercent = 50, bool bidirectional = false)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return false;

            // Support multiple descriptors separated by '|' (OR logic).
            if (descriptor.Contains('|'))
            {
                foreach (string part in descriptor.Split('|'))
                {
                    if (MapToButtonPressedSingle(state, part.Trim(), deadZonePercent, globalThresholdPercent, bidirectional))
                        return true;
                }
                return false;
            }

            return MapToButtonPressedSingle(state, descriptor, deadZonePercent, globalThresholdPercent, bidirectional);
        }

        /// <summary>Finds the "IR Pointer" source feeding a KBM mouse target, so
        /// the Wii pointer can be routed as an ABSOLUTE cursor position
        /// (Touchmote-style) instead of a velocity delta (issue #146). Checks
        /// the mapping-set row's sources first (only ones owned by
        /// <paramref name="thisDeviceGuid"/>, since Step 3 runs per assigned
        /// device and state.Ir belongs to that device), then the legacy per-key
        /// descriptor. Returns null when the target is not IR-driven, which
        /// keeps the existing delta path untouched for every other source.</summary>
        private static PadForge.Engine.Data.MappingSource FindIrPointerSource(
            MappingSet mappingSet, string targetName, string legacyDesc, string thisDeviceGuid)
        {
            var row = FindBaseRowForTarget(mappingSet, targetName);
            if (row?.Sources != null)
            {
                foreach (var src in row.Sources)
                {
                    if (src?.Descriptor == null) continue;
                    if (!src.Descriptor.StartsWith("IR Pointer ", StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrEmpty(src.DeviceGuid)
                        && !string.Equals(src.DeviceGuid, thisDeviceGuid, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return src;
                }
            }
            if (!string.IsNullOrEmpty(legacyDesc)
                && legacyDesc.Trim().StartsWith("IR Pointer ", StringComparison.Ordinal))
                return new PadForge.Engine.Data.MappingSource { Descriptor = legacyDesc.Trim() };
            return null;
        }

        /// <summary>
        /// Engine-evaluated source families ("IR Pointer X/Y", "IR Brightness",
        /// "Balance ...", "Mouse Position X/Y", "Midi ...") are owned by
        /// SourceCoercion and are not part of the legacy Axis/Button/Slider/POV
        /// grammar, so ParseDescriptor silently drops them. Found on first Wii IR
        /// hardware contact (2026-07-01): "IR Pointer X" assigned to KbmMouseX
        /// tracked perfectly at the driver yet the mouse never moved, because the
        /// per-key KBM path fell through to ParseDescriptor. Same pre-parse
        /// delegation shape as the Touchpad branch in MapToButtonPressedSingle.
        /// Per-slot-tuned families (gyro, touchpad gestures) stay on the
        /// mapping-set path, which carries the slot context they need.
        /// </summary>
        private static bool IsEngineOwnedDescriptor(string s) =>
            s.StartsWith("IR Pointer ", StringComparison.Ordinal) ||
            s.Equals("IR Brightness", StringComparison.Ordinal) ||
            s.StartsWith("Balance ", StringComparison.Ordinal) ||
            s.StartsWith("Mouse Position ", StringComparison.Ordinal) ||
            s.StartsWith("Midi ", StringComparison.Ordinal);

        /// <summary>
        /// Maps a single descriptor to a boolean button press.
        /// </summary>
        private static bool MapToButtonPressedSingle(CustomInputState state, string descriptor,
            int deadZonePercent = 0, int globalThresholdPercent = 50, bool bidirectional = false)
        {
            // Touchpad-typed descriptors that resolve to a bool. Parallel to the
            // "Touchpad N Finger M X/Y/Down" descriptors consumed by Step 3's
            // touchpad output path; here we recognize:
            //   "Touchpad N Click"          → state.Buttons[16] (SDL_GAMEPAD_BUTTON_TOUCHPAD;
            //                                  N>0 returns false until a per-device
            //                                  multi-touchpad-click extension lands)
            //   "Touchpad N Finger M Down"  → state.TouchpadDown[M] for finger M
            // Resolved BEFORE ParseDescriptor because that parser only knows
            // Axis / Button / Slider / POV — adding a fifth MapType would touch
            // many call sites, but bool-yielding touchpad descriptors only
            // need to flow through MapToButtonPressed.
            if (!string.IsNullOrEmpty(descriptor)
                && descriptor.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                return MapTouchpadButton(state, descriptor.Trim());
            }

            // Engine-owned families (IR Pointer / IR Brightness / Balance /
            // Mouse Position / Midi): threshold like the mapping grid does.
            if (!string.IsNullOrEmpty(descriptor) && IsEngineOwnedDescriptor(descriptor.Trim()))
            {
                var engineSrc = new PadForge.Engine.Data.MappingSource
                {
                    Descriptor = descriptor.Trim(),
                    DeadZone = deadZonePercent,
                };
                return PadForge.Engine.Common.Mapping.SourceCoercion.EvaluateForButtonTarget(
                    state, engineSrc, globalThresholdPercent);
            }

            var desc = ParseDescriptor(descriptor);
            if (!desc.IsValid)
                return false;

            switch (desc.Type)
            {
                case MapType.Button:
                    if (desc.Index >= 0 && desc.Index < state.Buttons.Length)
                        return state.Buttons[desc.Index];
                    return false;

                case MapType.Axis:
                    if (desc.Index >= 0 && desc.Index < CustomInputState.MaxAxis)
                    {
                        int value = state.Axis[desc.Index];
                        double t = Math.Max(deadZonePercent > 0 ? deadZonePercent : globalThresholdPercent, 1) / 100.0;
                        if (desc.HalfAxis)
                        {
                            if (bidirectional)
                            {
                                // Either side of center past deadzone counts.
                                int delta = value - 32768;
                                if (delta < 0) delta = -delta;
                                return delta > (int)(32767 * t);
                            }
                            if (desc.Inverted)
                                return value < (int)(32767 * (1.0 - t));
                            else
                                return value > (int)(32768 + 32767 * t);
                        }
                        int hi = (int)(t * 65535);
                        if (desc.Inverted)
                            return value < 65535 - hi;
                        else
                            return value > hi;
                    }
                    return false;

                case MapType.Slider:
                    if (desc.Index >= 0 && desc.Index < CustomInputState.MaxSliders)
                    {
                        int value = state.Sliders[desc.Index];
                        double t = Math.Max(deadZonePercent > 0 ? deadZonePercent : globalThresholdPercent, 1) / 100.0;
                        if (desc.HalfAxis)
                        {
                            if (bidirectional)
                            {
                                int delta = value - 32768;
                                if (delta < 0) delta = -delta;
                                return delta > (int)(32767 * t);
                            }
                            if (desc.Inverted)
                                return value < (int)(32767 * (1.0 - t));
                            else
                                return value > (int)(32768 + 32767 * t);
                        }
                        int hi = (int)(t * 65535);
                        if (desc.Inverted)
                            return value < 65535 - hi;
                        else
                            return value > hi;
                    }
                    return false;

                case MapType.POV:
                    if (desc.Index >= 0 && desc.Index < state.Povs.Length)
                    {
                        return IsPovDirectionActive(state.Povs[desc.Index], desc.PovDirection);
                    }
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Checks if a POV value matches a specified direction string.
        /// </summary>
        private static bool IsPovDirectionActive(int povValue, string direction)
        {
            if (povValue < 0) return false; // Centered
            if (string.IsNullOrEmpty(direction)) return povValue >= 0; // Any direction

            // Centidegrees: 0=Up, 4500=UpRight, 9000=Right, 13500=DownRight,
            // 18000=Down, 22500=DownLeft, 27000=Left, 31500=UpLeft.
            // Cardinals use ±67.5° tolerance; diagonals use ±22.5° (exact sector).
            switch (direction.ToLowerInvariant())
            {
                case "up":
                    return povValue >= 29250 || povValue <= 6750;
                case "right":
                    return povValue >= 2250 && povValue <= 15750;
                case "down":
                    return povValue >= 11250 && povValue <= 24750;
                case "left":
                    return povValue >= 20250 && povValue <= 33750;
                case "upright":
                    return povValue >= 2250 && povValue <= 6750;
                case "downright":
                    return povValue >= 11250 && povValue <= 15750;
                case "downleft":
                    return povValue >= 20250 && povValue <= 24750;
                case "upleft":
                    return povValue >= 29250 && povValue <= 33750;
                default:
                    return false;
            }
        }

        // ─────────────────────────────────────────────
        //  D-Pad from POV mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// If the DPad mapping descriptor points to a POV hat (or pipe-separated list),
        /// extracts the directional components and sets the corresponding D-pad button flags.
        /// </summary>
        private static void MapDPadFromPov(CustomInputState state, string descriptor, ref Gamepad gp)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return;

            // Support multiple descriptors separated by '|'.
            if (descriptor.Contains('|'))
            {
                foreach (string part in descriptor.Split('|'))
                {
                    MapDPadFromPovSingle(state, part.Trim(), ref gp);
                }
                return;
            }

            MapDPadFromPovSingle(state, descriptor, ref gp);
        }

        /// <summary>
        /// Maps a single POV descriptor to D-pad button flags.
        /// </summary>
        private static void MapDPadFromPovSingle(CustomInputState state, string descriptor, ref Gamepad gp)
        {
            var desc = ParseDescriptor(descriptor);
            if (!desc.IsValid || desc.Type != MapType.POV)
                return;

            if (desc.Index < 0 || desc.Index >= state.Povs.Length)
                return;

            int pov = state.Povs[desc.Index];
            if (pov < 0) return; // Centered

            if (IsPovDirectionActive(pov, "Up"))
                gp.SetButton(Gamepad.DPAD_UP, true);
            if (IsPovDirectionActive(pov, "Down"))
                gp.SetButton(Gamepad.DPAD_DOWN, true);
            if (IsPovDirectionActive(pov, "Left"))
                gp.SetButton(Gamepad.DPAD_LEFT, true);
            if (IsPovDirectionActive(pov, "Right"))
                gp.SetButton(Gamepad.DPAD_RIGHT, true);
        }

        // ─────────────────────────────────────────────
        //  Trigger mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a descriptor (or pipe-separated list) to a trigger value (0–65535).
        /// Multiple descriptors: the highest value wins.
        ///
        /// Examples:
        ///   "Axis 4"               → single source
        ///   "Axis 4|Button 8"      → max of axis value or button (0 or 65535)
        /// </summary>
        private static ushort MapToTrigger(CustomInputState state, string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return 0;

            // Support multiple descriptors separated by '|' (max value wins).
            if (descriptor.Contains('|'))
            {
                ushort best = 0;
                foreach (string part in descriptor.Split('|'))
                {
                    ushort val = MapToTriggerSingle(state, part.Trim());
                    if (val > best)
                        best = val;
                }
                return best;
            }

            return MapToTriggerSingle(state, descriptor);
        }

        /// <summary>
        /// Maps a single descriptor to a trigger value (0–65535).
        /// </summary>
        private static ushort MapToTriggerSingle(CustomInputState state, string descriptor)
        {
            var desc = ParseDescriptor(descriptor);
            if (!desc.IsValid)
                return 0;

            // POV → trigger needs digital semantics, not the axis-style
            // 32767-at-rest baseline GetRawValue uses for stick mapping.
            // A POV mapped to a trigger should sit at 0 when the POV is
            // centered and at 65535 when the configured direction is
            // active; without this, a centered POV reports ~50% trigger
            // pull at rest.
            if (desc.Type == MapType.POV
                && desc.Index >= 0 && desc.Index < state.Povs.Length)
            {
                bool active = IsPovDirectionActive(state.Povs[desc.Index], desc.PovDirection);
                if (desc.Inverted) active = !active;
                return active ? (ushort)65535 : (ushort)0;
            }

            int rawValue = GetRawValue(state, desc);

            // Keep full unsigned 16-bit range (0–65535) for trigger precision.
            if (desc.Inverted)
                rawValue = 65535 - rawValue;

            if (desc.HalfAxis)
            {
                // Half-axis: only use the upper half (32768–65535 → 0–65535).
                rawValue = Math.Max(0, rawValue - 32768);
                return (ushort)Math.Clamp(rawValue * 65535 / 32767, 0, 65535);
            }

            // Full axis: already 0–65535.
            return (ushort)Math.Clamp(rawValue, 0, 65535);
        }

        // ─────────────────────────────────────────────
        //  Thumbstick axis mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a descriptor (or pipe-separated list) to a signed thumbstick axis value (-32768 to 32767).
        /// Multiple descriptors: the source with the largest absolute magnitude wins.
        /// 
        /// Examples:
        ///   "Axis 1"           → single source
        ///   "Axis 1|Axis 3"    → whichever axis has larger magnitude
        /// </summary>
        private static short MapToThumbAxis(CustomInputState state, string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return 0;

            // Support multiple descriptors separated by '|' (largest magnitude wins).
            if (descriptor.Contains('|'))
            {
                short best = 0;
                foreach (string part in descriptor.Split('|'))
                {
                    short val = MapToThumbAxisSingle(state, part.Trim());
                    if (Math.Abs(val) > Math.Abs(best))
                        best = val;
                }
                return best;
            }

            return MapToThumbAxisSingle(state, descriptor);
        }

        /// <summary>
        /// Maps a single descriptor to a signed thumbstick axis value.
        /// </summary>
        private static short MapToThumbAxisSingle(CustomInputState state, string descriptor)
        {
            // Engine-owned families (IR Pointer / IR Brightness / Balance /
            // Mouse Position / Midi): bipolar [-1..+1] scaled to the signed
            // axis range, same evaluator the mapping grid uses.
            if (!string.IsNullOrWhiteSpace(descriptor) && IsEngineOwnedDescriptor(descriptor.Trim()))
            {
                float v = PadForge.Engine.Common.Mapping.SourceCoercion.EvaluateForBipolarAxisTarget(
                    state, new PadForge.Engine.Data.MappingSource { Descriptor = descriptor.Trim() });
                return (short)Math.Clamp((int)(v * short.MaxValue), short.MinValue, short.MaxValue);
            }

            var desc = ParseDescriptor(descriptor);
            if (!desc.IsValid)
                return 0;

            int rawValue = GetRawValue(state, desc);

            // Convert unsigned (0–65535) to signed (-32768 to 32767).
            int signed = rawValue - 32768;

            if (desc.Inverted)
                signed = -signed;

            // Clamp to short range.
            return (short)Math.Clamp(signed, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Maps a thumbstick axis using both positive and negative descriptors.
        /// When negDescriptor is empty, delegates to MapToThumbAxis (existing behavior).
        /// When both are set (typically buttons), pos pressed → +32767, neg pressed → -32768,
        /// both pressed → 0 (cancel out).
        /// </summary>
        private static short MapToThumbAxisWithNeg(CustomInputState state, string posDescriptor, string negDescriptor)
        {
            if (string.IsNullOrWhiteSpace(negDescriptor))
                return MapToThumbAxis(state, posDescriptor);

            // Both descriptors exist — treat as digital directions.
            bool posActive = MapToButtonPressed(state, posDescriptor);
            bool negActive = MapToButtonPressed(state, negDescriptor);

            if (posActive && negActive) return 0;
            if (posActive) return short.MaxValue;
            if (negActive) return short.MinValue;
            return 0;
        }

        /// <summary>
        /// Maps a Custom Extended trigger-axis input descriptor pair to a
        /// signed short suitable for a trigger slot in
        /// <see cref="ExtendedRawState.Axes"/>. The companion to
        /// <see cref="MapToThumbAxisWithNeg"/> for the trigger half of the
        /// dispatch in <see cref="MapInputToExtendedRaw"/>.
        ///
        /// <para><b>Why a separate mapper.</b> The signed-short axis
        /// representation puts the unsigned 16-bit zero point at
        /// <c>short.MinValue</c> (-32768). A stick with no input rests at
        /// signed <c>0</c> (= wire 50%, centered) — that's the correct rest
        /// for a stick. A trigger with no input rests at signed
        /// <c>short.MinValue</c> (= wire 0%, released) — that's the correct
        /// rest for a trigger. The thumbstick mapper inherits "0 = rest"
        /// from <see cref="MapToThumbAxisSingle"/>'s
        /// <c>if (!desc.IsValid) return 0;</c> guard, which is wrong for
        /// trigger slots; an unmapped Custom Extended trigger axis routed
        /// through the stick mapper paints the wire at 50% (centered)
        /// instead of 0% (released). This mapper substitutes
        /// <see cref="short.MinValue"/> in every "no measurable input" path
        /// so a trigger slot rests at released regardless of how the user
        /// configured (or didn't configure) the descriptor pair.</para>
        ///
        /// <para><b>Valid-descriptor paths are unchanged.</b> A mapped
        /// physical trigger axis goes through the same
        /// <c>MapToThumbAxis</c> codepath the stick mapper uses (raw 0..65535
        /// shifted to signed -32768..+32767), which the trigger slot already
        /// expects: released physical trigger (raw 0) lands at
        /// short.MinValue, fully pressed (raw 65535) at short.MaxValue.
        /// Buttons mapped to a trigger axis behave identically — released
        /// button (raw 0) → short.MinValue, pressed (raw 65535) →
        /// short.MaxValue. Only the rest-when-empty fallback differs.</para>
        ///
        /// <para><b>Pos+neg digital pair.</b> Trigger Mappings don't expose
        /// a negative-direction descriptor (triggers are unidirectional),
        /// so the pair branch shouldn't fire in normal use. Handled defensively
        /// for users who set both via XML edit or a future feature: only
        /// posActive presses the trigger; negActive alone or neither active
        /// reads as released. There's no "negative trigger" to push the
        /// value below released, unlike a stick's left/right pair.</para>
        /// </summary>
        private static short MapToExtendedTriggerAxis(CustomInputState state, string posDescriptor, string negDescriptor)
        {
            if (string.IsNullOrWhiteSpace(negDescriptor))
            {
                // Single descriptor: empty → released; valid → analog read;
                // invalid → released (the stick mapper would have returned
                // 0 here, which is wrong for trigger rest).
                if (string.IsNullOrWhiteSpace(posDescriptor))
                    return short.MinValue;
                // Engine-owned families read unipolar [0..1] for a trigger
                // target and map to the wire range (rest = short.MinValue).
                if (IsEngineOwnedDescriptor(posDescriptor.Trim()))
                {
                    float t = PadForge.Engine.Common.Mapping.SourceCoercion.EvaluateForTriggerTarget(
                        state, new PadForge.Engine.Data.MappingSource { Descriptor = posDescriptor.Trim() });
                    return (short)Math.Clamp((int)(t * 65535f) - 32768, short.MinValue, short.MaxValue);
                }
                var desc = ParseDescriptor(posDescriptor);
                if (!desc.IsValid)
                    return short.MinValue;
                return MapToThumbAxis(state, posDescriptor);
            }

            // Pos+neg digital pair. Triggers are unidirectional, so neg
            // doesn't push below released; it only fails to press.
            bool posActive = MapToButtonPressed(state, posDescriptor);
            if (posActive) return short.MaxValue;
            return short.MinValue;
        }

        // ─────────────────────────────────────────────
        //  Raw value extraction
        // ─────────────────────────────────────────────

        /// <summary>
        /// Gets the raw unsigned value (0–65535) from the input state based
        /// on the mapping descriptor's type and index.
        /// For buttons, returns 0 or 65535.
        /// For POV, returns axis-equivalent based on direction.
        /// </summary>
        private static int GetRawValue(CustomInputState state, MappingDescriptor desc)
        {
            switch (desc.Type)
            {
                case MapType.Axis:
                    if (desc.Index >= 0 && desc.Index < CustomInputState.MaxAxis)
                        return state.Axis[desc.Index];
                    return 0;

                case MapType.Slider:
                    if (desc.Index >= 0 && desc.Index < CustomInputState.MaxSliders)
                        return state.Sliders[desc.Index];
                    return 0;

                case MapType.Button:
                    if (desc.Index >= 0 && desc.Index < state.Buttons.Length)
                        return state.Buttons[desc.Index] ? 65535 : 0;
                    return 0;

                case MapType.POV:
                    // Map POV direction to axis value.
                    if (desc.Index >= 0 && desc.Index < state.Povs.Length)
                    {
                        int pov = state.Povs[desc.Index];
                        return PovDirectionToAxisValue(pov, desc.PovDirection);
                    }
                    return 32767; // Center

                default:
                    return 0;
            }
        }

        /// <summary>
        /// Converts a POV direction to an axis-equivalent value (0–65535).
        /// For Up/Left directions: active → 0, inactive → 32767.
        /// For Down/Right directions: active → 65535, inactive → 32767.
        /// </summary>
        private static int PovDirectionToAxisValue(int povValue, string direction)
        {
            if (string.IsNullOrEmpty(direction))
                return 32767;

            bool active = IsPovDirectionActive(povValue, direction);

            switch (direction.ToLowerInvariant())
            {
                case "up":
                case "left":
                    return active ? 0 : 32767;

                case "down":
                case "right":
                    return active ? 65535 : 32767;

                default:
                    return 32767;
            }
        }

        // ─────────────────────────────────────────────
        //  Dead zone processing
        // ─────────────────────────────────────────────

        /// <summary>
        /// Applies deadzone, anti-deadzone, and linear scaling to a pair
        /// of thumbstick axes (X and Y) using the specified deadzone shape algorithm.
        /// </summary>
        private static void ApplyDeadZone(ref short axisX, ref short axisY,
            double deadZoneX, double deadZoneY,
            double antiDeadZoneX, double antiDeadZoneY, double linear,
            double maxRangeX, double maxRangeY,
            double maxRangeXNeg, double maxRangeYNeg,
            double[] lutX, double[] lutY,
            DeadZoneShape shape)
        {
            // Axial: existing independent per-axis behavior.
            if (shape == DeadZoneShape.Axial)
            {
                axisX = ApplySingleDeadZone(axisX, deadZoneX, antiDeadZoneX, linear, maxRangeX, maxRangeXNeg, lutX);
                axisY = ApplySingleDeadZone(axisY, deadZoneY, antiDeadZoneY, linear, maxRangeY, maxRangeYNeg, lutY);
                return;
            }

            // ── Common normalization to [-1, 1] ──
            double nx = axisX / 32768.0;
            double ny = axisY / 32768.0;
            double signX = Math.Sign(nx), signY = Math.Sign(ny);
            double magX = Math.Abs(nx), magY = Math.Abs(ny);
            double dzXn = deadZoneX / 100.0, dzYn = deadZoneY / 100.0;
            // Pick max range based on direction of input.
            double mrXn = (nx >= 0 ? maxRangeX : maxRangeXNeg) / 100.0;
            double mrYn = (ny >= 0 ? maxRangeY : maxRangeYNeg) / 100.0;
            if (mrXn <= dzXn) mrXn = Math.Min(dzXn + 0.01, 1.0);
            if (mrYn <= dzYn) mrYn = Math.Min(dzYn + 0.01, 1.0);

            double remX, remY;

            switch (shape)
            {
                case DeadZoneShape.Radial:
                    ComputeRadial(nx, ny, magX, magY, dzXn, dzYn, mrXn, mrYn,
                        rescale: false, out remX, out remY);
                    break;
                case DeadZoneShape.ScaledRadial:
                    ComputeRadial(nx, ny, magX, magY, dzXn, dzYn, mrXn, mrYn,
                        rescale: true, out remX, out remY);
                    break;
                case DeadZoneShape.SlopedAxial:
                    ComputeSloped(magX, magY, dzXn, dzYn, mrXn, mrYn,
                        rescale: false, out remX, out remY);
                    break;
                case DeadZoneShape.SlopedScaledAxial:
                    ComputeSloped(magX, magY, dzXn, dzYn, mrXn, mrYn,
                        rescale: true, out remX, out remY);
                    break;
                case DeadZoneShape.Hybrid:
                    ComputeHybrid(nx, ny, magX, magY, dzXn, dzYn, mrXn, mrYn,
                        out remX, out remY, out signX, out signY);
                    break;
                default:
                    remX = magX; remY = magY;
                    break;
            }

            // ── Post-DZ per-axis pipeline: curve → anti-DZ → linear → output ──
            axisX = ApplyPostDeadZone(remX, signX, antiDeadZoneX, linear, lutX);
            axisY = ApplyPostDeadZone(remY, signY, antiDeadZoneY, linear, lutY);
        }

        /// <summary>
        /// Post-deadzone per-axis processing: sensitivity curve, anti-deadzone, linear.
        /// Input remapped is [0,1], sign is ±1.
        /// </summary>
        private static short ApplyPostDeadZone(double remapped, double sign,
            double antiDeadZone, double linear, double[] lut)
        {
            if (remapped <= 0 && antiDeadZone <= 0)
                return 0;

            if (lut != null)
                remapped = Common.CurveLut.Lookup(lut, Math.Clamp(remapped, 0, 1));

            double adzNorm = antiDeadZone / 100.0;
            double output = adzNorm + remapped * (1.0 - adzNorm);

            if (linear > 0)
            {
                double linearFactor = linear / 100.0;
                output = remapped * linearFactor + output * (1.0 - linearFactor);
            }

            double result = sign * output * 32767.0;
            return (short)Math.Clamp(result, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Radial / Scaled Radial deadzone with elliptical support.
        /// </summary>
        internal static void ComputeRadial(double nx, double ny,
            double magX, double magY,
            double dzXn, double dzYn, double mrXn, double mrYn,
            bool rescale, out double remX, out double remY)
        {
            // If both DZs are zero, no deadzone gating needed.
            if (dzXn <= 0 && dzYn <= 0)
            {
                remX = Math.Min(magX / mrXn, 1.0);
                remY = Math.Min(magY / mrYn, 1.0);
                return;
            }

            // Elliptical distance: (nx/dzX)² + (ny/dzY)² < 1 means inside DZ.
            const double eps = 1e-10;
            double effDzX = Math.Max(dzXn, eps);
            double effDzY = Math.Max(dzYn, eps);
            double edx = nx / effDzX;
            double edy = ny / effDzY;
            double ellipDist = Math.Sqrt(edx * edx + edy * edy);

            if (ellipDist < 1.0)
            {
                remX = 0; remY = 0;
                return;
            }

            if (!rescale)
            {
                // Radial (no rescale): pass through raw magnitudes, clamped at max range.
                remX = Math.Min(magX / mrXn, 1.0);
                remY = Math.Min(magY / mrYn, 1.0);
                return;
            }

            // Scaled Radial: rescale magnitude from [dzR, mrR] to [0, 1].
            double rawMag = Math.Sqrt(nx * nx + ny * ny);
            if (rawMag < eps) { remX = 0; remY = 0; return; }

            double ux = nx / rawMag, uy = ny / rawMag; // unit direction

            // DZ ellipse radius in this direction.
            double dxu = ux / effDzX, dyu = uy / effDzY;
            double dzR = 1.0 / Math.Sqrt(dxu * dxu + dyu * dyu);

            // Max-range ellipse radius in this direction.
            double mxu = ux / mrXn, myu = uy / mrYn;
            double mrR = 1.0 / Math.Sqrt(mxu * mxu + myu * myu);
            if (mrR <= dzR) mrR = dzR + 0.01;

            double scaledMag = Math.Clamp((rawMag - dzR) / (mrR - dzR), 0, 1);

            // Project back to per-axis, maintaining direction.
            remX = scaledMag * Math.Abs(ux);
            remY = scaledMag * Math.Abs(uy);
        }

        /// <summary>
        /// Sloped Axial / Sloped Scaled Axial deadzone.
        /// DZ on each axis scales with the other axis magnitude.
        /// </summary>
        internal static void ComputeSloped(double magX, double magY,
            double dzXn, double dzYn, double mrXn, double mrYn,
            bool rescale, out double remX, out double remY)
        {
            // Effective DZ: when other axis is large, DZ grows → easier cardinal lock.
            // When both are small (near center), DZ shrinks → less center filtering.
            double effDzX = dzXn * magY;
            double effDzY = dzYn * magX;

            if (magX < effDzX)
                remX = 0;
            else if (rescale)
            {
                double range = mrXn - effDzX;
                remX = range > 0 ? Math.Min((magX - effDzX) / range, 1.0) : 0;
            }
            else
                remX = Math.Min(magX / mrXn, 1.0);

            if (magY < effDzY)
                remY = 0;
            else if (rescale)
            {
                double range = mrYn - effDzY;
                remY = range > 0 ? Math.Min((magY - effDzY) / range, 1.0) : 0;
            }
            else
                remY = Math.Min(magY / mrYn, 1.0);
        }

        /// <summary>
        /// Hybrid: Scaled Radial first (center noise), then Sloped Scaled Axial (cardinal precision).
        /// </summary>
        internal static void ComputeHybrid(double nx, double ny,
            double magX, double magY,
            double dzXn, double dzYn, double mrXn, double mrYn,
            out double remX, out double remY, out double signX, out double signY)
        {
            // Stage 1: Scaled Radial
            ComputeRadial(nx, ny, magX, magY, dzXn, dzYn, mrXn, mrYn,
                rescale: true, out double srX, out double srY);

            // Stage 2: Sloped Scaled Axial on the radial output
            // Signs are from the original input.
            signX = Math.Sign(nx);
            signY = Math.Sign(ny);
            ComputeSloped(srX, srY, dzXn, dzYn, 1.0, 1.0,
                rescale: true, out remX, out remY);
        }

        /// <summary>
        /// Parses a DeadZoneShape from a string. Returns ScaledRadial for null/empty/invalid.
        /// </summary>
        internal static DeadZoneShape ParseDeadZoneShape(string value)
        {
            if (string.IsNullOrEmpty(value)) return DeadZoneShape.ScaledRadial;
            if (int.TryParse(value, out int v) && Enum.IsDefined(typeof(DeadZoneShape), v))
                return (DeadZoneShape)v;
            return DeadZoneShape.ScaledRadial;
        }

        /// <summary>
        /// Applies a center offset correction to a single axis. The offset is a percentage
        /// of the full axis range (-100 to 100). Applied before deadzone processing.
        /// </summary>
        private static short ApplyCenterOffset(short value, double offsetPercent)
        {
            if (offsetPercent == 0) return value;
            int offsetRaw = (int)(offsetPercent / 100.0 * 32768);
            return (short)Math.Clamp(value + offsetRaw, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Applies deadzone processing to a single axis.
        /// </summary>
        private static short ApplySingleDeadZone(short value, double deadZone, double antiDeadZone, double linear, double maxRangePos = 100, double maxRangeNeg = 100, double[] lut = null)
        {
            if (deadZone <= 0 && antiDeadZone <= 0 && maxRangePos >= 100 && maxRangeNeg >= 100 && lut == null)
                return value;

            // Normalize to float (-1.0 to 1.0).
            double norm = value / 32768.0;
            double sign = Math.Sign(norm);
            double magnitude = Math.Abs(norm);

            // Deadzone: values within the deadzone are zeroed.
            double dzNorm = deadZone / 100.0;
            if (magnitude < dzNorm)
                return 0;

            // Max range: cap the input ceiling so full output is reached at this %.
            // Pick positive or negative direction max range based on input sign.
            double maxNorm = (norm >= 0 ? maxRangePos : maxRangeNeg) / 100.0;
            if (maxNorm <= dzNorm)
                maxNorm = Math.Min(dzNorm + 0.01, 1.0);

            // Remap from [dzNorm, maxNorm] to [0, 1].
            double remapped = Math.Min((magnitude - dzNorm) / (maxNorm - dzNorm), 1.0);

            // Sensitivity curve: spline LUT lookup.
            if (lut != null)
                remapped = Common.CurveLut.Lookup(lut, remapped);

            // Anti-deadzone: offset the output minimum.
            double adzNorm = antiDeadZone / 100.0;
            double output = adzNorm + remapped * (1.0 - adzNorm);

            // Linear adjustment (simplified: 0 = default curve, 100 = fully linear).
            if (linear > 0)
            {
                double linearFactor = linear / 100.0;
                output = remapped * linearFactor + output * (1.0 - linearFactor);
            }

            // Apply sign and clamp to short range.
            double result = sign * output * 32767.0;
            return (short)Math.Clamp(result, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Applies deadzone, anti-deadzone, and max range processing to a trigger value (0–65535).
        /// Deadzone: values below the threshold percentage are zeroed.
        /// Max range: caps the input so full physical press maps to this percentage ceiling.
        /// Anti-deadzone: remaps the output so small presses register past the game's deadzone.
        /// </summary>
        private static ushort ApplyTriggerDeadZone(ushort value, double deadZone, double antiDeadZone, double maxRange, double[] lut = null)
        {
            if (deadZone <= 0 && antiDeadZone <= 0 && maxRange >= 100 && lut == null)
                return value;

            // Normalize to 0.0–1.0.
            double norm = value / 65535.0;

            // Dead zone: values below threshold are zeroed.
            double dzNorm = deadZone / 100.0;
            if (norm < dzNorm)
                return 0;

            // Max range: cap the input ceiling.
            double maxNorm = maxRange / 100.0;
            if (maxNorm <= dzNorm)
                maxNorm = dzNorm + 0.01;

            // Remap from [dzNorm, maxNorm] to [0, 1].
            double remapped = Math.Clamp((norm - dzNorm) / (maxNorm - dzNorm), 0.0, 1.0);

            // Sensitivity curve: spline LUT lookup.
            if (lut != null)
                remapped = Common.CurveLut.Lookup(lut, remapped);

            // Anti-deadzone: offset the output minimum.
            double adzNorm = antiDeadZone / 100.0;
            double output = adzNorm + remapped * (1.0 - adzNorm);

            return (ushort)Math.Clamp((int)(output * 65535.0), 0, 65535);
        }

        private static double TryParseDoubleStatic(string value, double defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double result) ? result : defaultValue;
        }

        private static int TryParseIntStatic(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        // ─────────────────────────────────────────────
        //  Extended Custom mapping engine
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a CustomInputState to a ExtendedRawState using the PadSetting's Extended
        /// dictionary-based mappings. Used for custom Extended configurations with
        /// arbitrary numbers of axes, buttons, and POVs.
        /// </summary>
        private static ExtendedRawState MapInputToExtendedRaw(CustomInputState state, PadSetting ps,
            CustomControllerLayout cfg,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex)
        {
            var raw = ExtendedRawState.Create(cfg.Axes, cfg.Buttons, cfg.Povs);
            raw.Clear(); // POVs need to start centered
            int vgt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            // ── Axes ── (MappingSet-first; fall back to legacy single-source)
            // Raw Extended axes use signed short internally. SubmitRawState converts to unsigned
            // HID range via (signed + 32768) / 2, preserving the natural direction:
            //   signed negative → HID low (0 = up/left)
            //   signed positive → HID high (32767 = down/right)
            // Stick slots rest at signed 0 (= wire 50%); trigger slots rest at
            // short.MinValue (= wire 0%). Different MappingSet evaluator per slot
            // type so an unmapped trigger doesn't sit at 50% on the wire.
            for (int i = 0; i < cfg.Axes && i < raw.Axes.Length; i++)
            {
                string axisKey = $"ExtendedAxis{i}";
                bool isTrigger = cfg.IsTriggerSlot(i);
                short axisValue;
                if (isTrigger
                    ? TryEvaluateMappingSetExtendedTrigger(state, mappingSet, thisDeviceGuid, slotIndex, axisKey, out axisValue)
                    : TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid, slotIndex, axisKey, out axisValue))
                {
                    raw.Axes[i] = axisValue;
                }
                else
                {
                    string posDesc = ps.GetExtendedMapping(axisKey);
                    string negDesc = ps.GetExtendedMapping($"ExtendedAxis{i}Neg");
                    raw.Axes[i] = isTrigger
                        ? MapToExtendedTriggerAxis(state, posDesc, negDesc)
                        : MapToThumbAxisWithNeg(state, posDesc, negDesc);
                }
            }

            // ── Buttons ──
            for (int i = 0; i < cfg.Buttons; i++)
            {
                string key = $"ExtendedBtn{i}";
                bool pressed;
                if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                        slotIndex, key, vgt, out pressed))
                {
                    if (pressed) raw.SetButton(i, true);
                }
                else
                {
                    string desc = ps.GetExtendedMapping(key);
                    if (MapToButtonPressed(state, desc, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), vgt, ps.GetMappingBidirectional(key) == "1"))
                        raw.SetButton(i, true);
                }
            }

            // ── POVs ──
            for (int p = 0; p < cfg.Povs && p < raw.Povs.Length; p++)
            {
                string upKey = $"ExtendedPov{p}Up", downKey = $"ExtendedPov{p}Down";
                string leftKey = $"ExtendedPov{p}Left", rightKey = $"ExtendedPov{p}Right";
                bool up = EvalExtendedDirection(state, ps, mappingSet, thisDeviceGuid, slotIndex, upKey, vgt);
                bool down = EvalExtendedDirection(state, ps, mappingSet, thisDeviceGuid, slotIndex, downKey, vgt);
                bool left = EvalExtendedDirection(state, ps, mappingSet, thisDeviceGuid, slotIndex, leftKey, vgt);
                bool right = EvalExtendedDirection(state, ps, mappingSet, thisDeviceGuid, slotIndex, rightKey, vgt);

                raw.Povs[p] = DirectionToContinuousPov(up, down, left, right);
            }

            // ── Deadzones ──
            // Apply stick/trigger deadzones using the same axis layout as
            // ExtendedSlotConfig.ComputeAxisLayout (interleaved groups of X,Y,T).
            int interleave = Math.Min(cfg.Sticks, cfg.Triggers);
            for (int g = 0; g < cfg.Sticks; g++)
            {
                int xi = g < interleave ? g * 3 : interleave * 3 + (g - interleave) * 2;
                int yi = xi + 1;
                if (xi >= raw.Axes.Length || yi >= raw.Axes.Length) break;

                double dzX, dzY, adzX, adzY, lin, cofX = 0, cofY = 0, mrX = 100, mrY = 100, mrXN = 100, mrYN = 100;
                double[] lutX = null, lutY = null;
                DeadZoneShape dzShape;
                switch (g)
                {
                    case 0:
                        dzShape = ParseDeadZoneShape(ps.LeftThumbDeadZoneShape);
                        dzX = TryParseDoubleStatic(ps.LeftThumbDeadZoneX, 0);
                        dzY = TryParseDoubleStatic(ps.LeftThumbDeadZoneY, 0);
                        adzX = TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneX, 0);
                        adzY = TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneY, 0);
                        lin = TryParseDoubleStatic(ps.LeftThumbLinear, 0);
                        lutX = Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveX);
                        lutY = Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveY);
                        cofX = TryParseDoubleStatic(ps.LeftThumbCenterOffsetX, 0);
                        cofY = TryParseDoubleStatic(ps.LeftThumbCenterOffsetY, 0);
                        mrX = TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100);
                        mrY = TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100);
                        mrXN = TryParseDoubleStatic(ps.LeftThumbMaxRangeXNeg, mrX);
                        mrYN = TryParseDoubleStatic(ps.LeftThumbMaxRangeYNeg, mrY);
                        break;
                    case 1:
                        dzShape = ParseDeadZoneShape(ps.RightThumbDeadZoneShape);
                        dzX = TryParseDoubleStatic(ps.RightThumbDeadZoneX, 0);
                        dzY = TryParseDoubleStatic(ps.RightThumbDeadZoneY, 0);
                        adzX = TryParseDoubleStatic(ps.RightThumbAntiDeadZoneX, 0);
                        adzY = TryParseDoubleStatic(ps.RightThumbAntiDeadZoneY, 0);
                        lin = TryParseDoubleStatic(ps.RightThumbLinear, 0);
                        lutX = Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveX);
                        lutY = Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveY);
                        cofX = TryParseDoubleStatic(ps.RightThumbCenterOffsetX, 0);
                        cofY = TryParseDoubleStatic(ps.RightThumbCenterOffsetY, 0);
                        mrX = TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100);
                        mrY = TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100);
                        mrXN = TryParseDoubleStatic(ps.RightThumbMaxRangeXNeg, mrX);
                        mrYN = TryParseDoubleStatic(ps.RightThumbMaxRangeYNeg, mrY);
                        break;
                    default:
                        // Custom Extended sticks 2+: read all settings from Extended dictionary.
                        dzShape = ParseDeadZoneShape(ps.GetExtendedMapping($"ExtendedStick{g}DzShape"));
                        dzX = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}DzX"), 0);
                        dzY = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}DzY"), 0);
                        adzX = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}AdzX"), 0);
                        adzY = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}AdzY"), 0);
                        lin = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}Linear"), 0);
                        lutX = Common.CurveLut.GetOrBuild(ps.GetExtendedMapping($"ExtendedStick{g}CurveX"));
                        lutY = Common.CurveLut.GetOrBuild(ps.GetExtendedMapping($"ExtendedStick{g}CurveY"));
                        cofX = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}CofX"), 0);
                        cofY = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}CofY"), 0);
                        mrX = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}MrX"), 100);
                        mrY = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}MrY"), 100);
                        mrXN = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}MrXN"), mrX);
                        mrYN = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedStick{g}MrYN"), mrY);
                        break;
                }
                raw.Axes[xi] = ApplyCenterOffset(raw.Axes[xi], cofX);
                raw.Axes[yi] = ApplyCenterOffset(raw.Axes[yi], cofY);
                ApplyDeadZone(ref raw.Axes[xi], ref raw.Axes[yi],
                    dzX, dzY, adzX, adzY, lin, mrX, mrY, mrXN, mrYN, lutX, lutY, dzShape);
            }

            for (int g = 0; g < cfg.Triggers; g++)
            {
                int ti = g < interleave ? g * 3 + 2
                       : interleave * 3 + Math.Max(0, cfg.Sticks - interleave) * 2 + (g - interleave);
                if (ti >= raw.Axes.Length) break;

                double dz, adz, maxR;
                double[] tlut;
                switch (g)
                {
                    case 0:
                        dz = TryParseDoubleStatic(ps.LeftTriggerDeadZone, 0);
                        adz = TryParseDoubleStatic(ps.LeftTriggerAntiDeadZone, 0);
                        maxR = TryParseDoubleStatic(ps.LeftTriggerMaxRange, 100);
                        tlut = Common.CurveLut.GetOrBuild(ps.LeftTriggerSensitivityCurve);
                        break;
                    case 1:
                        dz = TryParseDoubleStatic(ps.RightTriggerDeadZone, 0);
                        adz = TryParseDoubleStatic(ps.RightTriggerAntiDeadZone, 0);
                        maxR = TryParseDoubleStatic(ps.RightTriggerMaxRange, 100);
                        tlut = Common.CurveLut.GetOrBuild(ps.RightTriggerSensitivityCurve);
                        break;
                    default:
                        // Custom Extended triggers 2+: read from Extended dictionary.
                        dz = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedTrigger{g}Dz"), 0);
                        adz = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedTrigger{g}Adz"), 0);
                        maxR = TryParseDoubleStatic(ps.GetExtendedMapping($"ExtendedTrigger{g}Mr"), 100);
                        tlut = Common.CurveLut.GetOrBuild(ps.GetExtendedMapping($"ExtendedTrigger{g}Curve"));
                        break;
                }
                // Triggers use signed short in raw path; convert to unsigned 16-bit range,
                // apply trigger deadzone, then convert back.
                ushort asUshort = (ushort)(raw.Axes[ti] - short.MinValue);
                asUshort = ApplyTriggerDeadZone(asUshort, dz, adz, maxR, tlut);
                // Back to signed short range
                raw.Axes[ti] = (short)(asUshort + short.MinValue);
            }

            return raw;
        }

        /// <summary>Evaluates one Extended POV-direction button, preferring
        /// the per-VC MappingSet row when present.</summary>
        private static bool EvalExtendedDirection(CustomInputState state, PadSetting ps,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex,
            string key, int globalThreshold)
        {
            if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                    slotIndex, key, globalThreshold, out bool pressed))
                return pressed;
            return MapToButtonPressed(state, ps.GetExtendedMapping(key),
                TryParseIntStatic(ps.GetMappingDeadZone(key), 0), globalThreshold,
                ps.GetMappingBidirectional(key) == "1");
        }

        /// <summary>
        /// Converts 4 direction booleans to a continuous POV value (0-35900, -1=centered).
        /// </summary>
        private static int DirectionToContinuousPov(bool up, bool down, bool left, bool right)
        {
            if (up && right) return 4500;
            if (right && down) return 13500;
            if (down && left) return 22500;
            if (left && up) return 31500;
            if (up) return 0;
            if (right) return 9000;
            if (down) return 18000;
            if (left) return 27000;
            return -1; // Centered
        }

        // ─────────────────────────────────────────────
        //  MIDI mapping engine
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a CustomInputState to a MidiRawState using Midi dictionary-based
        /// mappings. CC values are mapped from signed axis range to 0-127 MIDI range.
        /// Notes are mapped as boolean on/off.
        /// </summary>
        private static MidiRawState MapInputToMidiRaw(CustomInputState state, PadSetting ps,
            int ccCount, int noteCount,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex)
        {
            var raw = MidiRawState.Create(ccCount, noteCount);
            raw.Clear();
            int mgt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            // CCs — map each from input axis to 0-127. Prefer the per-VC
            // MappingSet (multi-source, combine-mode, Custom-formula aware)
            // when a row exists for the target; fall back to legacy
            // single-source PadSetting fields for un-resaved configs.
            for (int i = 0; i < ccCount; i++)
            {
                string ccKey = $"MidiCC{i}";
                short axisValue;
                if (!TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                        slotIndex, ccKey, out axisValue))
                {
                    string posDesc = ps.GetMidiMapping(ccKey);
                    string negDesc = ps.GetMidiMapping($"MidiCC{i}Neg");
                    axisValue = MapToThumbAxisWithNeg(state, posDesc, negDesc);
                }
                // Convert signed short (-32768..32767) to MIDI range (0..127)
                raw.CcValues[i] = (byte)((axisValue + 32768) * 127 / 65535);
            }

            // Notes — map each as boolean. Same MappingSet-first dispatch.
            for (int i = 0; i < noteCount; i++)
            {
                string key = $"MidiNote{i}";
                bool pressed;
                if (!TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                        slotIndex, key, mgt, out pressed))
                {
                    string desc = ps.GetMidiMapping(key);
                    pressed = MapToButtonPressed(state, desc, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), mgt, ps.GetMappingBidirectional(key) == "1");
                }
                raw.Notes[i] = pressed;
            }

            return raw;
        }

        // ─────────────────────────────────────────────
        //  KBM raw state mapping
        // ─────────────────────────────────────────────

        /// <summary>
        /// Virtual key codes used for KBM mapping targets.
        /// Order matches InitializeKeyboardMouseMappings() in PadViewModel.
        /// </summary>
        private static readonly byte[] KbmKeyVkCodes;
        private static readonly int KbmKeyCount;

        static InputManager()
        {
            // Build the full list of VK codes that KBM supports
            var vks = new System.Collections.Generic.List<byte>(128);

            // Letters A-Z (0x41-0x5A)
            for (int i = 0; i < 26; i++) vks.Add((byte)(0x41 + i));
            // Numbers 0-9 (0x30-0x39)
            for (int i = 0; i <= 9; i++) vks.Add((byte)(0x30 + i));
            // Function keys F1-F12 (0x70-0x7B)
            for (int i = 0; i < 12; i++) vks.Add((byte)(0x70 + i));
            // Modifiers
            vks.Add(0xA0); vks.Add(0xA1); // L/R Shift
            vks.Add(0xA2); vks.Add(0xA3); // L/R Ctrl
            vks.Add(0xA4); vks.Add(0xA5); // L/R Alt
            // Special keys
            vks.Add(0x20); vks.Add(0x0D); vks.Add(0x1B); vks.Add(0x09); vks.Add(0x08); vks.Add(0x14);
            // Navigation
            vks.Add(0x26); vks.Add(0x28); vks.Add(0x25); vks.Add(0x27); // arrows
            vks.Add(0x24); vks.Add(0x23); vks.Add(0x21); vks.Add(0x22); // home/end/pgup/pgdn
            vks.Add(0x2D); vks.Add(0x2E); // insert/delete
            // Punctuation
            vks.Add(0xBA); vks.Add(0xBB); vks.Add(0xBC); vks.Add(0xBD);
            vks.Add(0xBE); vks.Add(0xBF); vks.Add(0xC0); vks.Add(0xDB);
            vks.Add(0xDC); vks.Add(0xDD); vks.Add(0xDE);
            // Numpad 0-9
            for (int i = 0; i <= 9; i++) vks.Add((byte)(0x60 + i));
            // Numpad operators
            vks.Add(0x6A); vks.Add(0x6B); vks.Add(0x6D); vks.Add(0x6E); vks.Add(0x6F);

            KbmKeyVkCodes = vks.ToArray();
            KbmKeyCount = KbmKeyVkCodes.Length;
        }

        /// <summary>
        /// Maps a CustomInputState to a KbmRawState using KBM dictionary-based mappings.
        /// Keys are mapped as button presses, mouse axes as signed deltas.
        /// </summary>
        private static KbmRawState MapInputToKbmRaw(CustomInputState state, PadSetting ps,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex)
        {
            var raw = new KbmRawState();
            int kgt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            // Map keyboard keys — MappingSet-first, fall back to legacy
            // single-source for un-resaved configs.
            for (int i = 0; i < KbmKeyCount; i++)
            {
                byte vk = KbmKeyVkCodes[i];
                string key = $"KbmKey{vk:X2}";
                bool pressed;
                if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                        slotIndex, key, kgt, out pressed))
                {
                    if (pressed) raw.SetKey(vk, true);
                }
                else
                {
                    string desc = ps.GetKbmMapping(key);
                    if (!string.IsNullOrEmpty(desc) && MapToButtonPressed(state, desc, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), kgt, ps.GetMappingBidirectional(key) == "1"))
                        raw.SetKey(vk, true);
                }
            }

            // Map mouse buttons (0=LMB, 1=RMB, 2=MMB, 3=X1, 4=X2)
            for (int i = 0; i < 5; i++)
            {
                string key = $"KbmMBtn{i}";
                bool pressed;
                if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                        slotIndex, key, kgt, out pressed))
                {
                    if (pressed) raw.SetMouseButton(i, true);
                }
                else
                {
                    string desc = ps.GetKbmMapping(key);
                    if (!string.IsNullOrEmpty(desc) && MapToButtonPressed(state, desc, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), kgt, ps.GetMappingBidirectional(key) == "1"))
                        raw.SetMouseButton(i, true);
                }
            }

            // Map mouse X axis (bidirectional)
            {
                string posDesc = ps.GetKbmMapping("KbmMouseX");
                string negDesc = ps.GetKbmMapping("KbmMouseXNeg");
                var irSrcX = FindIrPointerSource(mappingSet, "KbmMouseX", posDesc, thisDeviceGuid);
                if (irSrcX != null)
                {
                    // Wii IR pointing is ABSOLUTE aim (Touchmote-style): the
                    // cursor goes where the remote points, not where a velocity
                    // integral drifts. Route the evaluated [-1..+1] aim to the
                    // absolute-pointer channel; the KBM VC positions the OS
                    // cursor (Touchmote MouseSimulator.cs:154 SetCursorPos).
                    raw.MouseAbsX = PadForge.Engine.Common.Mapping.SourceCoercion
                        .EvaluateForBipolarAxisTarget(state, irSrcX, slotIndex);
                    if (state.Ir.Detected) raw.MouseAbsValid = true;
                }
                else if (TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                        slotIndex, "KbmMouseX", out short msxValue))
                {
                    raw.MouseDeltaX = msxValue;
                }
                else if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
                    raw.MouseDeltaX = MapToThumbAxisWithNeg(state, posDesc, negDesc);
            }

            // Map mouse Y axis (bidirectional)
            {
                string posDesc = ps.GetKbmMapping("KbmMouseY");
                string negDesc = ps.GetKbmMapping("KbmMouseYNeg");
                var irSrcY = FindIrPointerSource(mappingSet, "KbmMouseY", posDesc, thisDeviceGuid);
                if (irSrcY != null)
                {
                    // Absolute aim, same as the X block. state.Ir.Y is already
                    // screen-aligned (+1 = bottom, Touchmote convention applied
                    // at the wrapper), so no velocity-convention negation here.
                    raw.MouseAbsY = PadForge.Engine.Common.Mapping.SourceCoercion
                        .EvaluateForBipolarAxisTarget(state, irSrcY, slotIndex);
                    if (state.Ir.Detected) raw.MouseAbsValid = true;
                }
                else if (TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                        slotIndex, "KbmMouseY", out short msyValue))
                {
                    // KbmMouseY convention is positive = UP (the VC negates it to
                    // screen-Y, which is positive = down). The MappingSet evaluator
                    // returns SDL convention (positive = down), so negate to default
                    // physical-up → cursor-up — matching the legacy single-descriptor
                    // path below and the gamepad ThumbLY path. A per-source Invert
                    // still flips direction from there.
                    raw.MouseDeltaY = NegateAxis(msyValue);
                }
                else if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
                {
                    raw.MouseDeltaY = MapToThumbAxisWithNeg(state, posDesc, negDesc);
                    // For a full analog axis (no neg descriptor), SDL Y convention has
                    // positive=down. KBM convention: KbmMouseY positive=UP (matching
                    // gamepad path's NegateAxis on ThumbLY). Negate so the VC's
                    // screen-Y negation produces correct cursor direction.
                    if (string.IsNullOrWhiteSpace(negDesc))
                        raw.MouseDeltaY = NegateAxis(raw.MouseDeltaY);
                }
            }

            // Snapshot pre-deadzone values for stick tab preview.
            raw.PreDzMouseDeltaX = raw.MouseDeltaX;
            raw.PreDzMouseDeltaY = raw.MouseDeltaY;

            // ── Center offsets (applied before deadzone, same as gamepad path) ──
            raw.MouseDeltaX = ApplyCenterOffset(raw.MouseDeltaX, TryParseDoubleStatic(ps.LeftThumbCenterOffsetX, 0));
            raw.MouseDeltaY = ApplyCenterOffset(raw.MouseDeltaY, TryParseDoubleStatic(ps.LeftThumbCenterOffsetY, 0));

            // ── Mouse movement deadzone + sensitivity (uses Left Thumb settings) ──
            ApplyDeadZone(ref raw.MouseDeltaX, ref raw.MouseDeltaY,
                TryParseDoubleStatic(ps.LeftThumbDeadZoneX, 0),
                TryParseDoubleStatic(ps.LeftThumbDeadZoneY, 0),
                TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneX, 0),
                TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneY, 0),
                TryParseDoubleStatic(ps.LeftThumbLinear, 0),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeXNeg, TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100)),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeYNeg, TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100)),
                Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveX),
                Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveY),
                ParseDeadZoneShape(ps.LeftThumbDeadZoneShape));

            // Map scroll axis (bidirectional)
            {
                string posDesc = ps.GetKbmMapping("KbmScroll");
                string negDesc = ps.GetKbmMapping("KbmScrollNeg");
                if (TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                        slotIndex, "KbmScroll", out short scrollValue))
                {
                    // KbmScroll convention is positive = UP; the MappingSet evaluator
                    // returns SDL positive = down, so negate to default physical-up →
                    // scroll-up (same fix as MouseDeltaY). Per-source Invert overrides.
                    raw.ScrollDelta = NegateAxis(scrollValue);
                }
                else if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
                {
                    raw.ScrollDelta = MapToThumbAxisWithNeg(state, posDesc, negDesc);
                    // Full analog axis: SDL Y positive=down, but KbmScroll positive=UP.
                    // Negate so physical up → scroll up (same fix as MouseDeltaY).
                    if (string.IsNullOrWhiteSpace(negDesc))
                        raw.ScrollDelta = NegateAxis(raw.ScrollDelta);
                }
            }

            // Snapshot pre-deadzone scroll for stick preview.
            raw.PreDzScrollDelta = raw.ScrollDelta;

            // ── Scroll deadzone + sensitivity (uses Right Thumb settings, scroll on Y axis) ──
            // Scroll is a signed bidirectional axis — use stick deadzone with X=0.
            {
                short scrollX = 0;
                ApplyDeadZone(ref scrollX, ref raw.ScrollDelta,
                    TryParseDoubleStatic(ps.RightThumbDeadZoneX, 0),
                    TryParseDoubleStatic(ps.RightThumbDeadZoneY, 0),
                    TryParseDoubleStatic(ps.RightThumbAntiDeadZoneX, 0),
                    TryParseDoubleStatic(ps.RightThumbAntiDeadZoneY, 0),
                    TryParseDoubleStatic(ps.RightThumbLinear, 0),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeXNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100)),
                    TryParseDoubleStatic(ps.RightThumbMaxRangeYNeg, TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100)),
                    Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveX),
                    Common.CurveLut.GetOrBuild(ps.RightThumbSensitivityCurveY),
                    ParseDeadZoneShape(ps.RightThumbDeadZoneShape));
            }

            return raw;
        }

        /// <summary>
        /// Maps touchpad input from CustomInputState to a TouchpadState.
        /// Multi-source / combine modes / Custom formulas apply to every
        /// touchpad target (X/Y, contact, click) — same as every other VC
        /// type. The primary source's descriptor pattern picks the OUTPUT
        /// mode for the X/Y position:
        ///
        ///   PASSTHROUGH — Sources[0] starts with "Touchpad" (a finger-X
        ///     or finger-Y descriptor). The combined per-frame value is
        ///     written as ABSOLUTE position; extra sources contribute via
        ///     the row's combine mode (e.g. Average two physical
        ///     touchpads, MaxAbs whichever finger is furthest from
        ///     center). Passthrough sources are routed through
        ///     SourceCoercion's touchpad-axis reader (added with this
        ///     pass) so they coexist with stick / button sources in the
        ///     same row.
        ///
        ///   STICK-TO-CURSOR — Sources[0] is anything else (axis, button,
        ///     POV). The combined value is integrated per frame as
        ///     cursor velocity, exactly like the legacy single-source
        ///     stick-to-cursor path.
        ///
        /// Mode detection looks at the FIRST source's descriptor (or the
        /// legacy ps.TouchpadX1/X2 string when no MappingSet row exists)
        /// so a user's choice of "physical touchpad" vs "stick-to-cursor"
        /// stays expressed in the source they pick first.
        /// </summary>
        private static TouchpadState MapInputToTouchpad(CustomInputState state, PadSetting ps, TouchpadState prev,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex)
        {
            var tp = new TouchpadState { PacketCounter = prev.PacketCounter };
            int gt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            EvalTouchpadFinger(state, ps, mappingSet, thisDeviceGuid, slotIndex, gt,
                xKey: "TouchpadX1", yKey: "TouchpadY1", contactKey: "TouchpadContact1",
                legacyXDesc: ps.TouchpadX1, legacyYDesc: ps.TouchpadY1,
                legacyContactDesc: ps.TouchpadContact1,
                prevX: prev.X0, prevY: prev.Y0,
                physicalFingerIdx: 0,
                out tp.X0, out tp.Y0, out tp.Down0);

            EvalTouchpadFinger(state, ps, mappingSet, thisDeviceGuid, slotIndex, gt,
                xKey: "TouchpadX2", yKey: "TouchpadY2", contactKey: "TouchpadContact2",
                legacyXDesc: ps.TouchpadX2, legacyYDesc: ps.TouchpadY2,
                legacyContactDesc: ps.TouchpadContact2,
                prevX: prev.X1, prevY: prev.Y1,
                physicalFingerIdx: 1,
                out tp.X1, out tp.Y1, out tp.Down1);

            // ── Touchpad click ──
            // Empty descriptor = no click output (matches finger
            // semantics above). CreateDefaultPadSetting fills this in
            // for Sony source devices so the default DualSense →
            // PlayStation flow still surfaces the physical click.
            tp.Click = EvalTouchpadButton(state, ps, mappingSet, thisDeviceGuid, slotIndex,
                "TouchpadClick", ps.TouchpadClick, gt, out _);

            // Increment packet counter on finger state transitions.
            if (tp.Down0 != prev.Down0 || tp.Down1 != prev.Down1)
                tp.PacketCounter++;

            return tp;
        }

        /// <summary>Evaluates a touchpad button-class target (Click /
        /// ContactN). MappingSet-first; falls back to the legacy
        /// per-device descriptor on PadSetting when no row is present.
        /// <paramref name="found"/> tells the caller whether ANY mapping
        /// exists (so the velocity-implicit-contact fallback in the
        /// stick-to-touchpad path knows whether to kick in).</summary>
        private static bool EvalTouchpadButton(CustomInputState state, PadSetting ps,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex,
            string targetName, string legacyDescriptor, int globalThreshold,
            out bool found)
        {
            if (TryEvaluateMappingSetButton(state, mappingSet, thisDeviceGuid,
                    slotIndex, targetName, globalThreshold, out bool pressed))
            {
                found = true;
                return pressed;
            }
            if (!string.IsNullOrEmpty(legacyDescriptor))
            {
                found = true;
                return MapToButtonPressed(state, legacyDescriptor);
            }
            found = false;
            return false;
        }

        /// <summary>Evaluates one virtual touchpad finger (X/Y/contact).
        /// Mode is determined by the primary source's descriptor pattern —
        /// a touchpad-passthrough descriptor produces absolute position,
        /// anything else produces stick-to-cursor velocity. Both modes are
        /// fully multi-source-capable through the MappingSet path; the
        /// legacy per-device PadSetting descriptors are kept as a fallback
        /// for configs that haven't been resaved since the per-VC
        /// MappingSet shipped.</summary>
        private static void EvalTouchpadFinger(CustomInputState state, PadSetting ps,
            MappingSet mappingSet, string thisDeviceGuid, int slotIndex, int globalThreshold,
            string xKey, string yKey, string contactKey,
            string legacyXDesc, string legacyYDesc, string legacyContactDesc,
            float prevX, float prevY,
            int physicalFingerIdx,
            out float outX, out float outY, out bool outDown)
        {
            outX = prevX;
            outY = prevY;
            outDown = false;

            // Resolve the primary source's descriptor — MappingSet first
            // (Sources[0]) for the X target, legacy ps.TouchpadX? as the
            // fallback. The mode (passthrough vs velocity) keys off this
            // ONE descriptor: extra sources on the row contribute via the
            // row's combine mode, but they don't switch the output mode
            // out from under the primary.
            string primaryXDesc = ResolvePrimaryDescriptor(mappingSet, xKey, legacyXDesc);
            bool isPassthroughMode = IsTouchpadDescriptor(primaryXDesc);

            // Does the row exist in the MappingSet? Drives whether we use
            // the multi-source/combine evaluator or the legacy single-
            // descriptor reader. In passthrough mode the X/Y eval is
            // GATED — touchpad-class sources contribute only while their
            // paired TouchpadDown is true. The gated evaluator returns
            // false (no active source) so we can hold the previous
            // position — see TryEvaluateMappingSetTouchpadAxis.
            bool xViaMappingSet, yViaMappingSet;
            short xCombined, yCombined;
            if (isPassthroughMode)
            {
                xViaMappingSet = TryEvaluateMappingSetTouchpadAxis(state, mappingSet, thisDeviceGuid,
                    slotIndex, xKey, physicalFingerIdx, out xCombined);
                yViaMappingSet = TryEvaluateMappingSetTouchpadAxis(state, mappingSet, thisDeviceGuid,
                    slotIndex, yKey, physicalFingerIdx, out yCombined);
            }
            else
            {
                // Velocity / stick-to-cursor: ungated. Sticks at rest
                // already read 0 so the "stale finger position" pollution
                // can't happen here.
                xViaMappingSet = TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                    slotIndex, xKey, out xCombined);
                yViaMappingSet = TryEvaluateMappingSetBipolarAxis(state, mappingSet, thisDeviceGuid,
                    slotIndex, yKey, out yCombined);
            }
            bool anyXMapping = xViaMappingSet || !string.IsNullOrEmpty(legacyXDesc);

            if (!anyXMapping)
                return;  // No mapping at all → no output for this finger.

            if (isPassthroughMode)
            {
                if (xViaMappingSet)
                {
                    // Combined bipolar [-1..+1] → absolute position [0..1].
                    float xBipolar = xCombined / 32767f;
                    outX = Math.Clamp((xBipolar + 1f) * 0.5f, 0f, 1f);
                }
                else
                {
                    // Gated evaluator returned false (no active source) AND
                    // legacy fallback path didn't yield a Touchpad descriptor
                    // either — hold the previous position. This is the
                    // sticky-touchpad semantic: when no finger is touching,
                    // the cursor stays where the last finger left it.
                    outX = prevX;
                }

                if (yViaMappingSet)
                {
                    float yBipolar = yCombined / 32767f;
                    outY = Math.Clamp((yBipolar + 1f) * 0.5f, 0f, 1f);
                }
                else
                {
                    outY = prevY;
                }

                // If the MappingSet had no row at all for X (xViaMappingSet
                // was false AND no row found), but a legacy descriptor was
                // present, fall through to the legacy passthrough reader.
                // The gated evaluator never returns false for "row exists
                // with active sources" so this branch is for legacy
                // pre-MappingSet configs only.
                if (!xViaMappingSet && !string.IsNullOrEmpty(legacyXDesc)
                    && IsTouchpadDescriptor(legacyXDesc))
                {
                    float xBipolar = MapPassthroughLegacyAxisToBipolar(state, legacyXDesc, physicalFingerIdx, isY: false);
                    outX = Math.Clamp((xBipolar + 1f) * 0.5f, 0f, 1f);
                }
                if (!yViaMappingSet && !string.IsNullOrEmpty(legacyYDesc)
                    && IsTouchpadDescriptor(legacyYDesc))
                {
                    float yBipolar = MapPassthroughLegacyAxisToBipolar(state, legacyYDesc, physicalFingerIdx, isY: true);
                    outY = Math.Clamp((yBipolar + 1f) * 0.5f, 0f, 1f);
                }

                // Contact: MappingSet-first; falls back to legacy descriptor;
                // falls back to the physical finger's Down bit so the
                // out-of-the-box DualSense→PlayStation passthrough lights up
                // without the user authoring a contact mapping explicitly.
                bool contactPressed = EvalTouchpadButton(state, ps, mappingSet, thisDeviceGuid, slotIndex,
                    contactKey, legacyContactDesc, globalThreshold, out bool contactFound);
                outDown = contactFound
                    ? contactPressed
                    : (state.Touchpads != null && state.Touchpads.Length > 0
                       && state.Touchpads[0] != null
                       && physicalFingerIdx >= 0
                       && physicalFingerIdx < state.Touchpads[0].MaxFingers
                       && state.Touchpads[0].FingerDown[physicalFingerIdx]);
            }
            else
            {
                // Stick-to-cursor: combined bipolar value is integrated as
                // per-frame cursor velocity. Same sensitivity as the legacy
                // path so existing configs feel identical after the upgrade.
                float stickX = xViaMappingSet
                    ? xCombined / 32768f
                    : MapToThumbAxisWithNeg(state, legacyXDesc, null) / 32768f;
                float stickY = yViaMappingSet
                    ? yCombined / 32768f
                    : MapToThumbAxisWithNeg(state, legacyYDesc, null) / 32768f;
                const float sensitivity = 0.015f;
                outX = Math.Clamp(prevX + stickX * sensitivity, 0f, 1f);
                outY = Math.Clamp(prevY + stickY * sensitivity, 0f, 1f);

                // Contact: explicit mapping wins; else implicit-on-deflection
                // (matches legacy stick-to-cursor behavior).
                bool contactPressed = EvalTouchpadButton(state, ps, mappingSet, thisDeviceGuid, slotIndex,
                    contactKey, legacyContactDesc, globalThreshold, out bool contactFound);
                outDown = contactFound
                    ? contactPressed
                    : (Math.Abs(stickX) > 0.1f || Math.Abs(stickY) > 0.1f);
            }
        }

        /// <summary>Returns the primary source's descriptor for a target.
        /// Reads the Base-layer MappingSet row's <c>Sources[0].Descriptor</c>
        /// when present; otherwise returns the legacy
        /// PadSetting-stored descriptor.</summary>
        private static string ResolvePrimaryDescriptor(MappingSet mappingSet, string targetName, string legacyDescriptor)
        {
            // Race-safe snapshot. The save path mutates Rows on the UI
            // thread; a raw indexed iteration here on the polling thread
            // could read past Count and surface as the "Error mapping
            // device" error in Step 3's outer try/catch.
            var rows = SnapshotRows(mappingSet, out int rowsCount);
            for (int i = 0; i < rowsCount; i++)
            {
                var r = rows[i];
                if (r == null) continue;
                if (!string.Equals(r.LayerMask ?? "Base", "Base", StringComparison.Ordinal)) continue;
                if (!string.Equals(r.Target, targetName, StringComparison.Ordinal)) continue;
                var srcs = SnapshotSources(r, out int srcsCount);
                if (srcsCount == 0) break;
                return srcs[0]?.Descriptor ?? legacyDescriptor;
            }
            return legacyDescriptor;
        }

        /// <summary>Legacy-fallback reader for passthrough mode when no
        /// MappingSet row exists yet. Reads state.TouchpadFingers for a
        /// "Touchpad N Finger M X|Y" descriptor and returns it as bipolar
        /// [-1..+1]; non-touchpad descriptors fall back to
        /// <see cref="MapToThumbAxisWithNeg"/>-style stick reading scaled
        /// to [-1..+1]. <paramref name="physicalFingerIdx"/> is the index
        /// used when the descriptor's finger / axis aren't explicit (e.g.
        /// a bare "Touchpad" prefix from older configs).</summary>
        private static float MapPassthroughLegacyAxisToBipolar(CustomInputState state,
            string descriptor, int physicalFingerIdx, bool isY)
        {
            if (string.IsNullOrEmpty(descriptor)) return 0f;
            if (descriptor.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                // Try a "Touchpad N Finger M X|Y" parse; default to the
                // expected slot (physicalFingerIdx, X or Y).
                var parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int padIdx = 0;
                int fingerIdx = physicalFingerIdx;
                int axisOffset = isY ? 1 : 0;
                if (parts.Length >= 2 && int.TryParse(parts[1], out int parsedPad))
                    padIdx = parsedPad;
                if (parts.Length == 5
                    && parts[2].Equals("Finger", StringComparison.Ordinal)
                    && int.TryParse(parts[3], out int parsedFinger))
                {
                    fingerIdx = parsedFinger;
                    axisOffset = parts[4] switch { "X" => 0, "Y" => 1, "Pressure" => 2, _ => axisOffset };
                }
                if (state.Touchpads == null || padIdx < 0 || padIdx >= state.Touchpads.Length) return 0f;
                var pad = state.Touchpads[padIdx];
                if (pad == null || fingerIdx < 0 || fingerIdx >= pad.MaxFingers) return 0f;
                float raw = axisOffset switch
                {
                    0 => pad.FingerX[fingerIdx],
                    1 => pad.FingerY[fingerIdx],
                    2 => pad.FingerPressure[fingerIdx],
                    _ => 0f
                };
                return Math.Clamp((raw - 0.5f) * 2f, -1f, 1f);
            }
            // Non-touchpad descriptor in passthrough mode (shouldn't happen
            // — IsTouchpadDescriptor gates this branch — but fall through
            // safely just in case).
            return MapToThumbAxisWithNeg(state, descriptor, null) / 32768f;
        }

        /// <summary>Returns true if the descriptor is a touchpad-specific source (not a generic axis).</summary>
        private static bool IsTouchpadDescriptor(string descriptor) =>
            !string.IsNullOrEmpty(descriptor) &&
            descriptor.StartsWith("Touchpad", StringComparison.Ordinal);

        /// <summary>
        /// Resolves bool-yielding touchpad descriptors against a CustomInputState.
        /// Recognized forms:
        ///   "Touchpad N Click"          — state.Buttons[16] (SDL_GAMEPAD_BUTTON_TOUCHPAD;
        ///                                  N is parsed but only N==0 currently
        ///                                  has a backing slot — multi-touchpad
        ///                                  devices route their extras through
        ///                                  the SDL3 fork patch into other
        ///                                  Buttons[] indices, not handled here)
        ///   "Touchpad N Finger M Down"  — state.TouchpadDown[M], finger M's
        ///                                  contact bool. N is parsed for
        ///                                  symmetry with the X/Y descriptors.
        /// Anything else returns false. The N==0 restriction matches the X/Y
        /// descriptors elsewhere in Step 3 — PadForge models a single logical
        /// touchpad with up to two fingers regardless of how many physical
        /// touchpads SDL reports (multi-touchpad devices like the Steam Deck
        /// fan their fingers into the same two slots).
        /// </summary>
        private static bool MapTouchpadButton(CustomInputState state, string descriptor)
        {
            // Format: "Touchpad N <suffix>", split on spaces.
            var parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!int.TryParse(parts[1], out int touchpadIndex)) return false;

            // Click: "Touchpad N Click" — 3 parts.
            if (parts.Length == 3 && string.Equals(parts[2], "Click", StringComparison.Ordinal))
            {
                if (touchpadIndex != 0) return false;
                if (state.Buttons == null || state.Buttons.Length <= 16) return false;
                return state.Buttons[16];
            }

            // Finger down: "Touchpad N Finger M Down" — 5 parts.
            if (parts.Length == 5
                && string.Equals(parts[2], "Finger", StringComparison.Ordinal)
                && string.Equals(parts[4], "Down", StringComparison.Ordinal)
                && int.TryParse(parts[3], out int fingerIndex))
            {
                if (state.Touchpads == null
                    || touchpadIndex < 0 || touchpadIndex >= state.Touchpads.Length) return false;
                var pad = state.Touchpads[touchpadIndex];
                if (pad == null || fingerIndex < 0 || fingerIndex >= pad.MaxFingers) return false;
                return pad.FingerDown[fingerIndex];
            }

            // X/Y descriptors are handled by the touchpad output path in
            // Step 3 directly (BuildTouchpadState reads state.Touchpads),
            // not via MapToButtonPressed. They don't have a meaningful bool
            // interpretation, so reject them here so the user can't quietly
            // assign a stick X to a button.
            return false;
        }
    }
}
