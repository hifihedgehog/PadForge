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
        //  Each UserSetting links a device (InstanceGuid) to a pad slot (MapTo 0–3)
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
                    us.OutputState = MapInputToGamepad(ud.InputState, ps);
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
        private static Gamepad MapInputToGamepad(CustomInputState state, PadSetting ps)
        {
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
            if (MapToButtonPressed(state, ps.DPad))
            {
                // If DPad is mapped to a single POV, use directional mapping.
                // This is handled below via DPadUp/Down/Left/Right instead.
            }

            if (MapToButtonPressed(state, ps.DPadUp))
                gp.SetButton(Gamepad.DPAD_UP, true);
            if (MapToButtonPressed(state, ps.DPadDown))
                gp.SetButton(Gamepad.DPAD_DOWN, true);
            if (MapToButtonPressed(state, ps.DPadLeft))
                gp.SetButton(Gamepad.DPAD_LEFT, true);
            if (MapToButtonPressed(state, ps.DPadRight))
                gp.SetButton(Gamepad.DPAD_RIGHT, true);

            // If DPad is mapped to a POV hat, extract directions.
            MapDPadFromPov(state, ps.DPad, ref gp);

            // ── Triggers ──
            gp.LeftTrigger = MapToTrigger(state, ps.LeftTrigger);
            gp.RightTrigger = MapToTrigger(state, ps.RightTrigger);

            // ── Trigger dead zones ──
            gp.LeftTrigger = ApplyTriggerDeadZone(gp.LeftTrigger,
                TryParseIntStatic(ps.LeftTriggerDeadZone, 0),
                TryParseIntStatic(ps.LeftTriggerAntiDeadZone, 0),
                TryParseIntStatic(ps.LeftTriggerMaxRange, 100));
            gp.RightTrigger = ApplyTriggerDeadZone(gp.RightTrigger,
                TryParseIntStatic(ps.RightTriggerDeadZone, 0),
                TryParseIntStatic(ps.RightTriggerAntiDeadZone, 0),
                TryParseIntStatic(ps.RightTriggerMaxRange, 100));

            // ── Thumbsticks ──
            gp.ThumbLX = MapToThumbAxis(state, ps.LeftThumbAxisX);
            gp.ThumbLY = NegateAxis(MapToThumbAxis(state, ps.LeftThumbAxisY));
            gp.ThumbRX = MapToThumbAxis(state, ps.RightThumbAxisX);
            gp.ThumbRY = NegateAxis(MapToThumbAxis(state, ps.RightThumbAxisY));

            // ── Dead zones ──
            ApplyDeadZone(ref gp.ThumbLX, ref gp.ThumbLY,
                TryParseIntStatic(ps.LeftThumbDeadZoneX, 0),
                TryParseIntStatic(ps.LeftThumbDeadZoneY, 0),
                ps.LeftThumbAntiDeadZoneX,
                ps.LeftThumbAntiDeadZoneY,
                ps.LeftThumbLinear);

            ApplyDeadZone(ref gp.ThumbRX, ref gp.ThumbRY,
                TryParseIntStatic(ps.RightThumbDeadZoneX, 0),
                TryParseIntStatic(ps.RightThumbDeadZoneY, 0),
                ps.RightThumbAntiDeadZoneX,
                ps.RightThumbAntiDeadZoneY,
                ps.RightThumbLinear);

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

            // Centidegrees: 0=Up, 9000=Right, 18000=Down, 27000=Left
            // Allow ±6750 tolerance for diagonal detection.
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
        /// Maps a descriptor (or pipe-separated list) to a trigger value (0–255).
        /// Multiple descriptors: the highest value wins.
        /// 
        /// Examples:
        ///   "Axis 4"               → single source
        ///   "Axis 4|Button 8"      → max of axis value or button (0 or 255)
        /// </summary>
        private static byte MapToTrigger(CustomInputState state, string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return 0;

            // Support multiple descriptors separated by '|' (max value wins).
            if (descriptor.Contains('|'))
            {
                byte best = 0;
                foreach (string part in descriptor.Split('|'))
                {
                    byte val = MapToTriggerSingle(state, part.Trim());
                    if (val > best)
                        best = val;
                }
                return best;
            }

            return MapToTriggerSingle(state, descriptor);
        }

        /// <summary>
        /// Maps a single descriptor to a trigger value (0–255).
        /// </summary>
        private static byte MapToTriggerSingle(CustomInputState state, string descriptor)
        {
            var desc = ParseDescriptor(descriptor);
            if (!desc.IsValid)
                return 0;

            int rawValue = GetRawValue(state, desc);

            // Convert unsigned 16-bit (0–65535) to trigger range (0–255).
            if (desc.Inverted)
                rawValue = 65535 - rawValue;

            if (desc.HalfAxis)
            {
                // Half-axis: only use the upper half (32768–65535 → 0–255).
                rawValue = Math.Max(0, rawValue - 32768);
                return (byte)Math.Clamp(rawValue * 255 / 32767, 0, 255);
            }

            // Full axis: 0–65535 → 0–255.
            return (byte)Math.Clamp(rawValue * 255 / 65535, 0, 255);
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
            int deadZoneX, int deadZoneY,
            string antiDeadZoneXStr, string antiDeadZoneYStr, string linearStr)
        {
            int antiDeadZoneX = TryParseIntStatic(antiDeadZoneXStr, 0);
            int antiDeadZoneY = TryParseIntStatic(antiDeadZoneYStr, 0);
            int linear = TryParseIntStatic(linearStr, 0);

            // Apply dead zone independently to each axis.
            axisX = ApplySingleDeadZone(axisX, deadZoneX, antiDeadZoneX, linear);
            axisY = ApplySingleDeadZone(axisY, deadZoneY, antiDeadZoneY, linear);
        }

        /// <summary>
        /// Applies dead zone processing to a single axis.
        /// </summary>
        private static short ApplySingleDeadZone(short value, int deadZone, int antiDeadZone, int linear)
        {
            if (deadZone <= 0 && antiDeadZone <= 0)
                return value;

            // Normalize to float (-1.0 to 1.0).
            double norm = value / 32768.0;
            double sign = Math.Sign(norm);
            double magnitude = Math.Abs(norm);

            // Dead zone: values within the dead zone are zeroed.
            double dzNorm = deadZone / 100.0;
            if (magnitude < dzNorm)
                return 0;

            // Remap from dead zone edge to 1.0.
            double remapped = (magnitude - dzNorm) / (1.0 - dzNorm);

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
        /// Applies dead zone, anti-dead zone, and max range processing to a trigger value (0–255).
        /// Dead zone: values below the threshold percentage are zeroed.
        /// Max range: caps the input so full physical press maps to this percentage ceiling.
        /// Anti-dead zone: remaps the output so small presses register past the game's dead zone.
        /// </summary>
        private static byte ApplyTriggerDeadZone(byte value, int deadZone, int antiDeadZone, int maxRange)
        {
            if (deadZone <= 0 && antiDeadZone <= 0 && maxRange >= 100)
                return value;

            // Normalize to 0.0–1.0.
            double norm = value / 255.0;

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

            return (byte)Math.Clamp((int)(output * 255.0), 0, 255);
        }

        private static int TryParseIntStatic(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }
    }
}
