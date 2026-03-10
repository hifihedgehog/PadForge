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
                    if (ud == null || !ud.IsOnline || ud.InputState == null)
                    {
                        us.OutputState = default;
                        continue;
                    }

                    // Get the PadSetting with mapping rules.
                    PadSetting ps = us.GetPadSetting();
                    if (ps == null)
                    {
                        us.OutputState = default;
                        continue;
                    }

                    // Map the input state to a gamepad.
                    us.OutputState = MapInputToGamepad(ud.InputState, ps, out var rawMapped);
                    us.RawMappedState = rawMapped;

                    // For custom vJoy slots, also produce the raw vJoy output state.
                    int slot = us.MapTo;
                    if (slot >= 0 && slot < MaxPads &&
                        SlotControllerTypes[slot] == VirtualControllerType.VJoy &&
                        SlotVJoyIsCustom[slot])
                    {
                        var cfg = SlotVJoyConfigs[slot];
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
                }
                catch (Exception ex)
                {
                    RaiseError($"Error mapping device {us.InstanceGuid}", ex);
                    us.OutputState = default;
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

            // ── Buttons ──
            if (MapToButtonPressed(state, ps.ButtonA))
                gp.SetButton(Gamepad.A, true);
            if (MapToButtonPressed(state, ps.ButtonB))
                gp.SetButton(Gamepad.B, true);
            if (MapToButtonPressed(state, ps.ButtonX))
                gp.SetButton(Gamepad.X, true);
            if (MapToButtonPressed(state, ps.ButtonY))
                gp.SetButton(Gamepad.Y, true);

            if (MapToButtonPressed(state, ps.LeftShoulder))
                gp.SetButton(Gamepad.LEFT_SHOULDER, true);
            if (MapToButtonPressed(state, ps.RightShoulder))
                gp.SetButton(Gamepad.RIGHT_SHOULDER, true);

            if (MapToButtonPressed(state, ps.ButtonBack))
                gp.SetButton(Gamepad.BACK, true);
            if (MapToButtonPressed(state, ps.ButtonStart))
                gp.SetButton(Gamepad.START, true);

            if (MapToButtonPressed(state, ps.LeftThumbButton))
                gp.SetButton(Gamepad.LEFT_THUMB, true);
            if (MapToButtonPressed(state, ps.RightThumbButton))
                gp.SetButton(Gamepad.RIGHT_THUMB, true);

            if (MapToButtonPressed(state, ps.ButtonGuide))
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
                if (MapToButtonPressed(state, ps.DPadUp))
                    gp.SetButton(Gamepad.DPAD_UP, true);
                if (MapToButtonPressed(state, ps.DPadDown))
                    gp.SetButton(Gamepad.DPAD_DOWN, true);
                if (MapToButtonPressed(state, ps.DPadLeft))
                    gp.SetButton(Gamepad.DPAD_LEFT, true);
                if (MapToButtonPressed(state, ps.DPadRight))
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

            // ── Trigger dead zones ──
            gp.LeftTrigger = ApplyTriggerDeadZone(gp.LeftTrigger,
                TryParseDoubleStatic(ps.LeftTriggerDeadZone, 0),
                TryParseDoubleStatic(ps.LeftTriggerAntiDeadZone, 0),
                TryParseDoubleStatic(ps.LeftTriggerMaxRange, 100));
            gp.RightTrigger = ApplyTriggerDeadZone(gp.RightTrigger,
                TryParseDoubleStatic(ps.RightTriggerDeadZone, 0),
                TryParseDoubleStatic(ps.RightTriggerAntiDeadZone, 0),
                TryParseDoubleStatic(ps.RightTriggerMaxRange, 100));

            // ── Thumbsticks ──
            gp.ThumbLX = MapToThumbAxisWithNeg(state, ps.LeftThumbAxisX, ps.LeftThumbAxisXNeg);
            gp.ThumbLY = NegateAxis(MapToThumbAxisWithNeg(state, ps.LeftThumbAxisY, ps.LeftThumbAxisYNeg));
            gp.ThumbRX = MapToThumbAxisWithNeg(state, ps.RightThumbAxisX, ps.RightThumbAxisXNeg);
            gp.ThumbRY = NegateAxis(MapToThumbAxisWithNeg(state, ps.RightThumbAxisY, ps.RightThumbAxisYNeg));

            // Snapshot raw mapped state (after axis selection, before offset/DZ processing)
            // for the UI preview so it can apply its own pipeline without double-processing.
            rawMapped = gp;

            // ── Center offsets (applied before dead zone) ──
            gp.ThumbLX = ApplyCenterOffset(gp.ThumbLX, TryParseDoubleStatic(ps.LeftThumbCenterOffsetX, 0));
            gp.ThumbLY = ApplyCenterOffset(gp.ThumbLY, TryParseDoubleStatic(ps.LeftThumbCenterOffsetY, 0));
            gp.ThumbRX = ApplyCenterOffset(gp.ThumbRX, TryParseDoubleStatic(ps.RightThumbCenterOffsetX, 0));
            gp.ThumbRY = ApplyCenterOffset(gp.ThumbRY, TryParseDoubleStatic(ps.RightThumbCenterOffsetY, 0));

            // ── Dead zones ──
            ApplyDeadZone(ref gp.ThumbLX, ref gp.ThumbLY,
                TryParseDoubleStatic(ps.LeftThumbDeadZoneX, 0),
                TryParseDoubleStatic(ps.LeftThumbDeadZoneY, 0),
                ps.LeftThumbAntiDeadZoneX,
                ps.LeftThumbAntiDeadZoneY,
                ps.LeftThumbLinear,
                TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100),
                TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100));

            ApplyDeadZone(ref gp.ThumbRX, ref gp.ThumbRY,
                TryParseDoubleStatic(ps.RightThumbDeadZoneX, 0),
                TryParseDoubleStatic(ps.RightThumbDeadZoneY, 0),
                ps.RightThumbAntiDeadZoneX,
                ps.RightThumbAntiDeadZoneY,
                ps.RightThumbLinear,
                TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100),
                TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100));

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

            if (string.IsNullOrWhiteSpace(descriptor) || descriptor == "0" || descriptor == "")
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
        private static bool MapToButtonPressed(CustomInputState state, string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return false;

            // Support multiple descriptors separated by '|' (OR logic).
            if (descriptor.Contains('|'))
            {
                foreach (string part in descriptor.Split('|'))
                {
                    if (MapToButtonPressedSingle(state, part.Trim()))
                        return true;
                }
                return false;
            }

            return MapToButtonPressedSingle(state, descriptor);
        }

        /// <summary>
        /// Maps a single descriptor to a boolean button press.
        /// </summary>
        private static bool MapToButtonPressedSingle(CustomInputState state, string descriptor)
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
                        // Axis as button: threshold at 75% of range.
                        if (desc.Inverted)
                            return value < 16384;  // Below 25%
                        else
                            return value > 49151;  // Above 75%
                    }
                    return false;

                case MapType.Slider:
                    if (desc.Index >= 0 && desc.Index < CustomInputState.MaxSliders)
                    {
                        int value = state.Sliders[desc.Index];
                        if (desc.Inverted)
                            return value < 16384;
                        else
                            return value > 49151;
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
        /// Applies dead zone, anti-dead zone, and linear scaling to a pair
        /// of thumbstick axes (X and Y).
        /// 
        /// Dead zone: values within ±deadZone are forced to 0.
        /// Anti-dead zone: remaps the output range to start above the game's
        ///   expected dead zone (so small physical movements register in-game).
        /// Linear: adjusts the response curve (0 = default, positive = more linear).
        /// </summary>
        private static void ApplyDeadZone(ref short axisX, ref short axisY,
            double deadZoneX, double deadZoneY,
            string antiDeadZoneXStr, string antiDeadZoneYStr, string linearStr,
            double maxRangeX = 100, double maxRangeY = 100)
        {
            double antiDeadZoneX = TryParseDoubleStatic(antiDeadZoneXStr, 0);
            double antiDeadZoneY = TryParseDoubleStatic(antiDeadZoneYStr, 0);
            double linear = TryParseDoubleStatic(linearStr, 0);

            // Apply dead zone independently to each axis.
            axisX = ApplySingleDeadZone(axisX, deadZoneX, antiDeadZoneX, linear, maxRangeX);
            axisY = ApplySingleDeadZone(axisY, deadZoneY, antiDeadZoneY, linear, maxRangeY);
        }

        /// <summary>
        /// Applies a center offset correction to a single axis. The offset is a percentage
        /// of the full axis range (-100 to 100). Applied before dead zone processing.
        /// </summary>
        private static short ApplyCenterOffset(short value, double offsetPercent)
        {
            if (offsetPercent == 0) return value;
            int offsetRaw = (int)(offsetPercent / 100.0 * 32768);
            return (short)Math.Clamp(value + offsetRaw, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Applies dead zone processing to a single axis.
        /// </summary>
        private static short ApplySingleDeadZone(short value, double deadZone, double antiDeadZone, double linear, double maxRange = 100)
        {
            if (deadZone <= 0 && antiDeadZone <= 0 && maxRange >= 100)
                return value;

            // Normalize to float (-1.0 to 1.0).
            double norm = value / 32768.0;
            double sign = Math.Sign(norm);
            double magnitude = Math.Abs(norm);

            // Dead zone: values within the dead zone are zeroed.
            double dzNorm = deadZone / 100.0;
            if (magnitude < dzNorm)
                return 0;

            // Max range: cap the input ceiling so full output is reached at this %.
            double maxNorm = maxRange / 100.0;
            if (maxNorm <= dzNorm)
                maxNorm = Math.Min(dzNorm + 0.01, 1.0);

            // Remap from [dzNorm, maxNorm] to [0, 1].
            double remapped = Math.Min((magnitude - dzNorm) / (maxNorm - dzNorm), 1.0);

            // Anti-dead zone: offset the output minimum.
            double adzNorm = antiDeadZone / 100.0;
            double output = adzNorm + remapped * (1.0 - adzNorm);

            // Linear adjustment (simplified: 0 = default curve, 100 = fully linear).
            // A full implementation would use a more complex curve; this is a simplified version.
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
        /// Applies dead zone, anti-dead zone, and max range processing to a trigger value (0–65535).
        /// Dead zone: values below the threshold percentage are zeroed.
        /// Max range: caps the input so full physical press maps to this percentage ceiling.
        /// Anti-dead zone: remaps the output so small presses register past the game's dead zone.
        /// </summary>
        private static ushort ApplyTriggerDeadZone(ushort value, double deadZone, double antiDeadZone, double maxRange)
        {
            if (deadZone <= 0 && antiDeadZone <= 0 && maxRange >= 100)
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

            // Anti-dead zone: offset the output minimum.
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

        // ─────────────────────────────────────────────
        //  vJoy Custom mapping engine
        // ─────────────────────────────────────────────

        /// <summary>
        /// Maps a CustomInputState to a VJoyRawState using the PadSetting's vJoy
        /// dictionary-based mappings. Used for custom vJoy configurations with
        /// arbitrary numbers of axes, buttons, and POVs.
        /// </summary>
        private static VJoyRawState MapInputToVJoyRaw(CustomInputState state, PadSetting ps,
            VJoyVirtualController.VJoyDeviceConfig cfg)
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
            for (int i = 0; i < cfg.Buttons; i++)
            {
                string desc = ps.GetVJoyMapping($"VJoyBtn{i}");
                if (MapToButtonPressed(state, desc))
                    raw.SetButton(i, true);
            }

            // ── POVs ──
            for (int p = 0; p < cfg.Povs && p < raw.Povs.Length; p++)
            {
                // Individual direction buttons → continuous POV value
                bool up = MapToButtonPressed(state, ps.GetVJoyMapping($"VJoyPov{p}Up"));
                bool down = MapToButtonPressed(state, ps.GetVJoyMapping($"VJoyPov{p}Down"));
                bool left = MapToButtonPressed(state, ps.GetVJoyMapping($"VJoyPov{p}Left"));
                bool right = MapToButtonPressed(state, ps.GetVJoyMapping($"VJoyPov{p}Right"));

                raw.Povs[p] = DirectionToContinuousPov(up, down, left, right);
            }

            // ── Dead zones ──
            // Apply stick/trigger dead zones using the same axis layout as
            // VJoySlotConfig.ComputeAxisLayout (interleaved groups of X,Y,T).
            int interleave = Math.Min(cfg.Sticks, cfg.Triggers);
            for (int g = 0; g < cfg.Sticks; g++)
            {
                int xi = g < interleave ? g * 3 : interleave * 3 + (g - interleave) * 2;
                int yi = xi + 1;
                if (xi >= raw.Axes.Length || yi >= raw.Axes.Length) break;

                double dzX, dzY, adzX, adzY, lin, cofX = 0, cofY = 0, mrX = 100, mrY = 100;
                switch (g)
                {
                    case 0:
                        dzX = TryParseDoubleStatic(ps.LeftThumbDeadZoneX, 0);
                        dzY = TryParseDoubleStatic(ps.LeftThumbDeadZoneY, 0);
                        adzX = TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneX, 0);
                        adzY = TryParseDoubleStatic(ps.LeftThumbAntiDeadZoneY, 0);
                        lin = TryParseDoubleStatic(ps.LeftThumbLinear, 0);
                        cofX = TryParseDoubleStatic(ps.LeftThumbCenterOffsetX, 0);
                        cofY = TryParseDoubleStatic(ps.LeftThumbCenterOffsetY, 0);
                        mrX = TryParseDoubleStatic(ps.LeftThumbMaxRangeX, 100);
                        mrY = TryParseDoubleStatic(ps.LeftThumbMaxRangeY, 100);
                        break;
                    case 1:
                        dzX = TryParseDoubleStatic(ps.RightThumbDeadZoneX, 0);
                        dzY = TryParseDoubleStatic(ps.RightThumbDeadZoneY, 0);
                        adzX = TryParseDoubleStatic(ps.RightThumbAntiDeadZoneX, 0);
                        adzY = TryParseDoubleStatic(ps.RightThumbAntiDeadZoneY, 0);
                        lin = TryParseDoubleStatic(ps.RightThumbLinear, 0);
                        cofX = TryParseDoubleStatic(ps.RightThumbCenterOffsetX, 0);
                        cofY = TryParseDoubleStatic(ps.RightThumbCenterOffsetY, 0);
                        mrX = TryParseDoubleStatic(ps.RightThumbMaxRangeX, 100);
                        mrY = TryParseDoubleStatic(ps.RightThumbMaxRangeY, 100);
                        break;
                    default:
                        continue; // No dead zone properties for sticks 2+ yet
                }
                raw.Axes[xi] = ApplyCenterOffset(raw.Axes[xi], cofX);
                raw.Axes[yi] = ApplyCenterOffset(raw.Axes[yi], cofY);
                raw.Axes[xi] = ApplySingleDeadZone(raw.Axes[xi], dzX, adzX, lin, mrX);
                raw.Axes[yi] = ApplySingleDeadZone(raw.Axes[yi], dzY, adzY, lin, mrY);
            }

            for (int g = 0; g < cfg.Triggers; g++)
            {
                int ti = g < interleave ? g * 3 + 2
                       : interleave * 3 + Math.Max(0, cfg.Sticks - interleave) * 2 + (g - interleave);
                if (ti >= raw.Axes.Length) break;

                double dz, adz, maxR;
                switch (g)
                {
                    case 0:
                        dz = TryParseDoubleStatic(ps.LeftTriggerDeadZone, 0);
                        adz = TryParseDoubleStatic(ps.LeftTriggerAntiDeadZone, 0);
                        maxR = TryParseDoubleStatic(ps.LeftTriggerMaxRange, 100);
                        break;
                    case 1:
                        dz = TryParseDoubleStatic(ps.RightTriggerDeadZone, 0);
                        adz = TryParseDoubleStatic(ps.RightTriggerAntiDeadZone, 0);
                        maxR = TryParseDoubleStatic(ps.RightTriggerMaxRange, 100);
                        break;
                    default:
                        continue; // No dead zone properties for triggers 2+ yet
                }
                // Triggers use signed short in raw path; convert to unsigned 16-bit range,
                // apply trigger dead zone, then convert back.
                ushort asUshort = (ushort)(raw.Axes[ti] - short.MinValue);
                asUshort = ApplyTriggerDeadZone(asUshort, dz, adz, maxR);
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
            for (int i = 0; i < noteCount; i++)
            {
                string desc = ps.GetMidiMapping($"MidiNote{i}");
                raw.Notes[i] = MapToButtonPressed(state, desc);
            }

            return raw;
        }
    }
}
