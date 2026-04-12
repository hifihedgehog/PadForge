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

                    // Map the input state to a gamepad.
                    us.OutputState = MapInputToGamepad(ud.InputState, ps, out var rawMapped);
                    us.RawMappedState = rawMapped;

                    // For custom vJoy slots, also produce the raw vJoy output state.
                    int slot = us.MapTo;
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.Extended &&
                        SlotExtendedIsCustom[slot])
                    {
                        var cfg = SlotCustomLayouts[slot];
                        us.VJoyRawOutputState = MapInputToVJoyRaw(ud.InputState, ps, cfg);
                    }

                    // For MIDI slots, produce the raw MIDI output state.
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.Midi)
                    {
                        var mc = _midiConfigs[slot];
                        if (mc != null)
                            us.MidiRawOutputState = MapInputToMidiRaw(ud.InputState, ps, mc.CcCount, mc.NoteCount);
                    }

                    // For KeyboardMouse slots, produce the raw KBM output state.
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.KeyboardMouse)
                    {
                        us.KbmRawOutputState = MapInputToKbmRaw(ud.InputState, ps);
                    }

                    // For DS4 slots, produce touchpad state from input device.
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.Sony)
                    {
                        us.TouchpadOutputState = MapInputToTouchpad(ud.InputState, ps, us.TouchpadOutputState);
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
            if (MapToButtonPressed(state, ps.ButtonA, TryParseIntStatic(ps.GetMappingDeadZone("ButtonA"), 0), gt))
                gp.SetButton(Gamepad.A, true);
            if (MapToButtonPressed(state, ps.ButtonB, TryParseIntStatic(ps.GetMappingDeadZone("ButtonB"), 0), gt))
                gp.SetButton(Gamepad.B, true);
            if (MapToButtonPressed(state, ps.ButtonX, TryParseIntStatic(ps.GetMappingDeadZone("ButtonX"), 0), gt))
                gp.SetButton(Gamepad.X, true);
            if (MapToButtonPressed(state, ps.ButtonY, TryParseIntStatic(ps.GetMappingDeadZone("ButtonY"), 0), gt))
                gp.SetButton(Gamepad.Y, true);

            if (MapToButtonPressed(state, ps.LeftShoulder, TryParseIntStatic(ps.GetMappingDeadZone("LeftShoulder"), 0), gt))
                gp.SetButton(Gamepad.LEFT_SHOULDER, true);
            if (MapToButtonPressed(state, ps.RightShoulder, TryParseIntStatic(ps.GetMappingDeadZone("RightShoulder"), 0), gt))
                gp.SetButton(Gamepad.RIGHT_SHOULDER, true);

            if (MapToButtonPressed(state, ps.ButtonBack, TryParseIntStatic(ps.GetMappingDeadZone("ButtonBack"), 0), gt))
                gp.SetButton(Gamepad.BACK, true);
            if (MapToButtonPressed(state, ps.ButtonStart, TryParseIntStatic(ps.GetMappingDeadZone("ButtonStart"), 0), gt))
                gp.SetButton(Gamepad.START, true);

            if (MapToButtonPressed(state, ps.LeftThumbButton, TryParseIntStatic(ps.GetMappingDeadZone("LeftThumbButton"), 0), gt))
                gp.SetButton(Gamepad.LEFT_THUMB, true);
            if (MapToButtonPressed(state, ps.RightThumbButton, TryParseIntStatic(ps.GetMappingDeadZone("RightThumbButton"), 0), gt))
                gp.SetButton(Gamepad.RIGHT_THUMB, true);

            if (MapToButtonPressed(state, ps.ButtonGuide, TryParseIntStatic(ps.GetMappingDeadZone("ButtonGuide"), 0), gt))
                gp.SetButton(Gamepad.GUIDE, true);

            // ── D-Pad ──
            // Individual direction mappings take priority. Only fall back to
            // the combined DPad descriptor if no individual directions are set.
            bool hasIndividualDPad = !string.IsNullOrEmpty(ps.DPadUp)
                                 || !string.IsNullOrEmpty(ps.DPadDown)
                                 || !string.IsNullOrEmpty(ps.DPadLeft)
                                 || !string.IsNullOrEmpty(ps.DPadRight);

            if (hasIndividualDPad)
            {
                if (MapToButtonPressed(state, ps.DPadUp, TryParseIntStatic(ps.GetMappingDeadZone("DPadUp"), 0), gt))
                    gp.SetButton(Gamepad.DPAD_UP, true);
                if (MapToButtonPressed(state, ps.DPadDown, TryParseIntStatic(ps.GetMappingDeadZone("DPadDown"), 0), gt))
                    gp.SetButton(Gamepad.DPAD_DOWN, true);
                if (MapToButtonPressed(state, ps.DPadLeft, TryParseIntStatic(ps.GetMappingDeadZone("DPadLeft"), 0), gt))
                    gp.SetButton(Gamepad.DPAD_LEFT, true);
                if (MapToButtonPressed(state, ps.DPadRight, TryParseIntStatic(ps.GetMappingDeadZone("DPadRight"), 0), gt))
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

            return gp;
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
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
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
            int deadZonePercent = 0, int globalThresholdPercent = 50)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return false;

            // Support multiple descriptors separated by '|' (OR logic).
            if (descriptor.Contains('|'))
            {
                foreach (string part in descriptor.Split('|'))
                {
                    if (MapToButtonPressedSingle(state, part.Trim(), deadZonePercent, globalThresholdPercent))
                        return true;
                }
                return false;
            }

            return MapToButtonPressedSingle(state, descriptor, deadZonePercent, globalThresholdPercent);
        }

        /// <summary>
        /// Maps a single descriptor to a boolean button press.
        /// </summary>
        private static bool MapToButtonPressedSingle(CustomInputState state, string descriptor,
            int deadZonePercent = 0, int globalThresholdPercent = 50)
        {
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
                            // Half-axis: threshold applies within the active half (center-to-edge).
                            // Non-inverted half: active range 32768–65535, threshold = 32768 + 32767*t
                            // Inverted half: active range 32767–0, threshold = 32767 - 32767*t
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
        //  vJoy Custom mapping engine
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a CustomInputState to a VJoyRawState using the PadSetting's vJoy
        /// dictionary-based mappings. Used for custom vJoy configurations with
        /// arbitrary numbers of axes, buttons, and POVs.
        /// </summary>
        private static VJoyRawState MapInputToVJoyRaw(CustomInputState state, PadSetting ps,
            CustomControllerLayout cfg)
        {
            var raw = VJoyRawState.Create(cfg.Axes, cfg.Buttons, cfg.Povs);
            raw.Clear(); // POVs need to start centered

            // ── Axes ──
            // Raw vJoy axes use signed short internally. SubmitRawState converts to unsigned
            // HID range via (signed + 32768) / 2, preserving the natural direction:
            //   signed negative → HID low (0 = up/left)
            //   signed positive → HID high (32767 = down/right)
            // No NegateAxis needed here — unlike the gamepad path (which applies NegateAxis
            // + HID Y inversion in SubmitGamepadState), the raw path has no second inversion.
            // The display layer (UpdateFromVJoyRawState) applies its own 1.0-Y for LiveY.
            for (int i = 0; i < cfg.Axes && i < raw.Axes.Length; i++)
            {
                string posDesc = ps.GetVJoyMapping($"VJoyAxis{i}");
                string negDesc = ps.GetVJoyMapping($"VJoyAxis{i}Neg");
                raw.Axes[i] = MapToThumbAxisWithNeg(state, posDesc, negDesc);
            }

            // ── Buttons ──
            int vgt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);
            for (int i = 0; i < cfg.Buttons; i++)
            {
                string key = $"VJoyBtn{i}";
                string desc = ps.GetVJoyMapping(key);
                if (MapToButtonPressed(state, desc, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), vgt))
                    raw.SetButton(i, true);
            }

            // ── POVs ──
            for (int p = 0; p < cfg.Povs && p < raw.Povs.Length; p++)
            {
                string upKey = $"VJoyPov{p}Up", downKey = $"VJoyPov{p}Down";
                string leftKey = $"VJoyPov{p}Left", rightKey = $"VJoyPov{p}Right";
                bool up = MapToButtonPressed(state, ps.GetVJoyMapping(upKey), TryParseIntStatic(ps.GetMappingDeadZone(upKey), 0), vgt);
                bool down = MapToButtonPressed(state, ps.GetVJoyMapping(downKey), TryParseIntStatic(ps.GetMappingDeadZone(downKey), 0), vgt);
                bool left = MapToButtonPressed(state, ps.GetVJoyMapping(leftKey), TryParseIntStatic(ps.GetMappingDeadZone(leftKey), 0), vgt);
                bool right = MapToButtonPressed(state, ps.GetVJoyMapping(rightKey), TryParseIntStatic(ps.GetMappingDeadZone(rightKey), 0), vgt);

                raw.Povs[p] = DirectionToContinuousPov(up, down, left, right);
            }

            // ── Deadzones ──
            // Apply stick/trigger deadzones using the same axis layout as
            // VJoySlotConfig.ComputeAxisLayout (interleaved groups of X,Y,T).
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
                        // Custom vJoy sticks 2+: read all settings from vJoy dictionary.
                        dzShape = ParseDeadZoneShape(ps.GetVJoyMapping($"VJoyStick{g}DzShape"));
                        dzX = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}DzX"), 0);
                        dzY = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}DzY"), 0);
                        adzX = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}AdzX"), 0);
                        adzY = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}AdzY"), 0);
                        lin = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}Linear"), 0);
                        lutX = Common.CurveLut.GetOrBuild(ps.GetVJoyMapping($"VJoyStick{g}CurveX"));
                        lutY = Common.CurveLut.GetOrBuild(ps.GetVJoyMapping($"VJoyStick{g}CurveY"));
                        cofX = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}CofX"), 0);
                        cofY = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}CofY"), 0);
                        mrX = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}MrX"), 100);
                        mrY = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}MrY"), 100);
                        mrXN = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}MrXN"), mrX);
                        mrYN = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyStick{g}MrYN"), mrY);
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
                        // Custom vJoy triggers 2+: read from vJoy dictionary.
                        dz = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyTrigger{g}Dz"), 0);
                        adz = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyTrigger{g}Adz"), 0);
                        maxR = TryParseDoubleStatic(ps.GetVJoyMapping($"VJoyTrigger{g}Mr"), 100);
                        tlut = Common.CurveLut.GetOrBuild(ps.GetVJoyMapping($"VJoyTrigger{g}Curve"));
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
            int ccCount, int noteCount)
        {
            var raw = MidiRawState.Create(ccCount, noteCount);
            raw.Clear();

            // CCs — map each from input axis to 0-127
            for (int i = 0; i < ccCount; i++)
            {
                string posDesc = ps.GetMidiMapping($"MidiCC{i}");
                string negDesc = ps.GetMidiMapping($"MidiCC{i}Neg");
                short axisValue = MapToThumbAxisWithNeg(state, posDesc, negDesc);
                // Convert signed short (-32768..32767) to MIDI range (0..127)
                raw.CcValues[i] = (byte)((axisValue + 32768) * 127 / 65535);
            }

            // Notes — map each as boolean
            int mgt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);
            for (int i = 0; i < noteCount; i++)
            {
                string key = $"MidiNote{i}";
                string desc = ps.GetMidiMapping(key);
                raw.Notes[i] = MapToButtonPressed(state, desc, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), mgt);
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
        private static KbmRawState MapInputToKbmRaw(CustomInputState state, PadSetting ps)
        {
            var raw = new KbmRawState();
            int kgt = TryParseIntStatic(ps.AxisToButtonThreshold, 50);

            // Map keyboard keys
            for (int i = 0; i < KbmKeyCount; i++)
            {
                byte vk = KbmKeyVkCodes[i];
                string key = $"KbmKey{vk:X2}";
                string desc = ps.GetKbmMapping(key);
                if (!string.IsNullOrEmpty(desc) && MapToButtonPressed(state, desc, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), kgt))
                    raw.SetKey(vk, true);
            }

            // Map mouse buttons (0=LMB, 1=RMB, 2=MMB, 3=X1, 4=X2)
            for (int i = 0; i < 5; i++)
            {
                string key = $"KbmMBtn{i}";
                string desc = ps.GetKbmMapping(key);
                if (!string.IsNullOrEmpty(desc) && MapToButtonPressed(state, desc, TryParseIntStatic(ps.GetMappingDeadZone(key), 0), kgt))
                    raw.SetMouseButton(i, true);
            }

            // Map mouse X axis (bidirectional)
            {
                string posDesc = ps.GetKbmMapping("KbmMouseX");
                string negDesc = ps.GetKbmMapping("KbmMouseXNeg");
                if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
                    raw.MouseDeltaX = MapToThumbAxisWithNeg(state, posDesc, negDesc);
            }

            // Map mouse Y axis (bidirectional)
            {
                string posDesc = ps.GetKbmMapping("KbmMouseY");
                string negDesc = ps.GetKbmMapping("KbmMouseYNeg");
                if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
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
                if (!string.IsNullOrEmpty(posDesc) || !string.IsNullOrEmpty(negDesc))
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
        ///
        /// Two modes per finger:
        /// 1. SDL touchpad passthrough — descriptor starts with "Touchpad" → direct from
        ///    CustomInputState.TouchpadFingers[]/TouchpadDown[] (DS4/DualSense/Steam Deck)
        /// 2. Stick-to-touchpad — descriptor is a standard axis (e.g. "Axis 0") → stick
        ///    deflection drives a virtual cursor via velocity accumulation
        ///
        /// TouchpadClick and TouchpadContact always use the standard button pipeline.
        /// </summary>
        private static TouchpadState MapInputToTouchpad(CustomInputState state, PadSetting ps, TouchpadState prev)
        {
            var tp = new TouchpadState { PacketCounter = prev.PacketCounter };

            // ── Finger 0 ──
            bool isTouchpadSource0 = IsTouchpadDescriptor(ps.TouchpadX1);
            if (isTouchpadSource0)
            {
                // Direct passthrough from SDL touchpad data
                tp.X0 = state.TouchpadFingers[0];
                tp.Y0 = state.TouchpadFingers[1];
                tp.Down0 = state.TouchpadDown[0];
            }
            else if (!string.IsNullOrEmpty(ps.TouchpadX1))
            {
                // Stick-to-touchpad: read axis value and accumulate as cursor velocity
                float stickX = MapToThumbAxisWithNeg(state, ps.TouchpadX1, null) / 32768f;
                float stickY = MapToThumbAxisWithNeg(state, ps.TouchpadY1, null) / 32768f;
                const float sensitivity = 0.015f;
                tp.X0 = Math.Clamp(prev.X0 + stickX * sensitivity, 0f, 1f);
                tp.Y0 = Math.Clamp(prev.Y0 + stickY * sensitivity, 0f, 1f);
                // Contact driven by mapping descriptor or auto-true when stick is deflected
                tp.Down0 = !string.IsNullOrEmpty(ps.TouchpadContact1)
                    ? MapToButtonPressed(state, ps.TouchpadContact1)
                    : (Math.Abs(stickX) > 0.1f || Math.Abs(stickY) > 0.1f);
            }

            // ── Finger 1 ──
            bool isTouchpadSource1 = IsTouchpadDescriptor(ps.TouchpadX2);
            if (isTouchpadSource1)
            {
                tp.X1 = state.TouchpadFingers[3];
                tp.Y1 = state.TouchpadFingers[4];
                tp.Down1 = state.TouchpadDown[1];
            }
            else if (!string.IsNullOrEmpty(ps.TouchpadX2))
            {
                float stickX = MapToThumbAxisWithNeg(state, ps.TouchpadX2, null) / 32768f;
                float stickY = MapToThumbAxisWithNeg(state, ps.TouchpadY2, null) / 32768f;
                const float sensitivity = 0.015f;
                tp.X1 = Math.Clamp(prev.X1 + stickX * sensitivity, 0f, 1f);
                tp.Y1 = Math.Clamp(prev.Y1 + stickY * sensitivity, 0f, 1f);
                tp.Down1 = !string.IsNullOrEmpty(ps.TouchpadContact2)
                    ? MapToButtonPressed(state, ps.TouchpadContact2)
                    : (Math.Abs(stickX) > 0.1f || Math.Abs(stickY) > 0.1f);
            }

            // ── Touchpad click ──
            tp.Click = MapToButtonPressed(state, ps.TouchpadClick);

            // Increment packet counter on finger state transitions.
            if (tp.Down0 != prev.Down0 || tp.Down1 != prev.Down1)
                tp.PacketCounter++;

            return tp;
        }

        /// <summary>Returns true if the descriptor is a touchpad-specific source (not a generic axis).</summary>
        private static bool IsTouchpadDescriptor(string descriptor) =>
            !string.IsNullOrEmpty(descriptor) &&
            descriptor.StartsWith("Touchpad", StringComparison.Ordinal);
    }
}
