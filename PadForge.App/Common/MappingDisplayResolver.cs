using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Common
{
    /// <summary>
    /// Resolves mapping descriptors (e.g., "Axis 0", "Button 65", "POV 0 Up")
    /// to human-readable display text using device object metadata and localization.
    /// Also builds the available input choices list for the mapping dropdown.
    ///
    /// Extracted from InputService to separate presentation logic from engine state management.
    /// </summary>
    internal static class MappingDisplayResolver
    {
        /// <summary>
        /// Resolves the source descriptor of a mapping to a human-friendly display name
        /// using the device's object metadata.
        /// </summary>
        internal static void ResolveDisplayText(MappingItem mapping, UserDevice ud)
        {
            if (mapping == null || string.IsNullOrEmpty(mapping.SourceDescriptor))
                return;

            if (ud != null && UseRawNumberedNaming(ud))
            {
                string resolved = ResolveRawNumberedText(mapping.SourceDescriptor);
                if (resolved != null)
                    mapping.SetResolvedSourceText(resolved);
                return;
            }

            // Bundled motion-passthrough descriptors don't depend on
            // device-objects metadata — they are protocol-level markers
            // that always resolve to a fixed localized name.
            {
                string md = mapping.SourceDescriptor;
                if (md.StartsWith("Motion ", System.StringComparison.Ordinal))
                {
                    var si = Strings.Instance;
                    string sub = md.Substring(7).Trim();
                    if (sub.Equals("Gyro",  System.StringComparison.OrdinalIgnoreCase))
                        { mapping.SetResolvedSourceText(si.Mapping_MotionGyro); return; }
                    if (sub.Equals("Accel", System.StringComparison.OrdinalIgnoreCase))
                        { mapping.SetResolvedSourceText(si.Mapping_MotionAccel); return; }
                    return;
                }
            }

            var objects = ud?.DeviceObjects;
            if (objects == null || objects.Length == 0)
                return;

            string s = mapping.SourceDescriptor;
            string prefix = "";
            if (s.StartsWith("IH", System.StringComparison.OrdinalIgnoreCase))
            { prefix = s.Substring(0, 2); s = s.Substring(2); }
            else if (s.StartsWith("I", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }
            else if (s.StartsWith("H", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }

            string[] parts = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
                return;

            string typeName = parts[0].ToLowerInvariant();

            for (int i = 0; i < objects.Length; i++)
            {
                var obj = objects[i];
                if (obj.InputIndex != index)
                    continue;

                bool match = typeName switch
                {
                    "button" => obj.IsButton,
                    "axis" => obj.IsAxis && !obj.IsSlider,
                    "slider" => obj.IsSlider,
                    "pov" => obj.IsPov,
                    _ => false
                };

                if (match && !string.IsNullOrEmpty(obj.Name))
                {
                    string display = LocalizeObjectName(obj.Name);

                    if (typeName == "pov" && parts.Length >= 3)
                    {
                        string dir = ResolvePovDirection(parts[2]);
                        display = obj.Name == "D-Pad"
                            ? $"{display} {dir}"
                            : string.Format(Strings.Instance.Mapping_POV_Format, index, dir);
                    }

                    if (!string.IsNullOrEmpty(prefix))
                    {
                        string prefixLabel = ResolvePrefixLabel(prefix);
                        if (!string.IsNullOrEmpty(prefixLabel))
                            display = $"{prefixLabel} {display}";
                    }
                    mapping.SetResolvedSourceText(display);
                    return;
                }
            }
        }

        /// <summary>
        /// Resolves the negative-direction descriptor to a human-friendly display name.
        /// </summary>
        internal static void ResolveNegDisplayText(MappingItem mapping, UserDevice ud)
        {
            if (mapping == null || string.IsNullOrEmpty(mapping.NegSourceDescriptor))
                return;

            if (ud != null && UseRawNumberedNaming(ud))
            {
                string resolved = ResolveRawNumberedText(mapping.NegSourceDescriptor);
                if (resolved != null)
                    mapping.SetResolvedNegText(resolved);
                return;
            }

            var objects = ud?.DeviceObjects;
            if (objects == null || objects.Length == 0)
                return;

            string resolved2 = ResolveDescriptorText(mapping.NegSourceDescriptor, objects);
            if (resolved2 != null)
                mapping.SetResolvedNegText(resolved2);
        }

        /// <summary>
        /// Resolves a descriptor string to a human-readable name using device object metadata.
        /// Returns null if no match found.
        /// </summary>
        internal static string ResolveDescriptorText(string descriptor, DeviceObjectItem[] objects)
        {
            string s = descriptor;
            string prefix = "";
            if (s.StartsWith("IH", System.StringComparison.OrdinalIgnoreCase))
            { prefix = s.Substring(0, 2); s = s.Substring(2); }
            else if (s.StartsWith("I", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }
            else if (s.StartsWith("H", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }

            // Touchpad descriptors → localized display names.
            if (s.StartsWith("Touchpad", System.StringComparison.Ordinal))
            {
                var si = Strings.Instance;
                if (s.Contains("Finger 0 X")) return prefix + si.Mapping_TouchpadX1;
                if (s.Contains("Finger 0 Y")) return prefix + si.Mapping_TouchpadY1;
                if (s.Contains("Finger 0 Down")) return prefix + si.Mapping_TouchpadContact1;
                if (s.Contains("Finger 1 X")) return prefix + si.Mapping_TouchpadX2;
                if (s.Contains("Finger 1 Y")) return prefix + si.Mapping_TouchpadY2;
                if (s.Contains("Finger 1 Down")) return prefix + si.Mapping_TouchpadContact2;
                return null;
            }

            // Gyro descriptors → localized display names.
            if (s.StartsWith("Gyro ", System.StringComparison.Ordinal))
            {
                var si = Strings.Instance;
                string axis = s.Substring(5).Trim();
                if (axis.Equals("Pitch",      System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroPitch;
                if (axis.Equals("Yaw",        System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroYaw;
                if (axis.Equals("Roll",       System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroRoll;
                if (axis.Equals("Horizontal", System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroHorizontal;
                return null;
            }

            // Bundled motion-passthrough descriptors → localized display names.
            if (s.StartsWith("Motion ", System.StringComparison.Ordinal))
            {
                var si = Strings.Instance;
                string sub = s.Substring(7).Trim();
                if (sub.Equals("Gyro",  System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_MotionGyro;
                if (sub.Equals("Accel", System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_MotionAccel;
                return null;
            }

            string[] parts = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
                return null;

            string typeName = parts[0].ToLowerInvariant();

            for (int i = 0; i < objects.Length; i++)
            {
                var obj = objects[i];
                if (obj.InputIndex != index)
                    continue;

                bool match = typeName switch
                {
                    "button" => obj.IsButton,
                    "axis" => obj.IsAxis && !obj.IsSlider,
                    "slider" => obj.IsSlider,
                    "pov" => obj.IsPov,
                    _ => false
                };

                if (match && !string.IsNullOrEmpty(obj.Name))
                {
                    string display = LocalizeObjectName(obj.Name);

                    if (typeName == "pov" && parts.Length >= 3)
                    {
                        string dir = ResolvePovDirection(parts[2]);
                        display = obj.Name == "D-Pad"
                            ? $"{display} {dir}"
                            : string.Format(Strings.Instance.Mapping_POV_Format, index, dir);
                    }

                    if (!string.IsNullOrEmpty(prefix))
                    {
                        string prefixLabel = ResolvePrefixLabel(prefix);
                        if (!string.IsNullOrEmpty(prefixLabel))
                            display = $"{prefixLabel} {display}";
                    }
                    return display;
                }
            }
            return null;
        }

        /// <summary>
        /// Maps an Engine-level object name (invariant English) to its localized display string.
        /// Falls back to the original name if no localization is defined.
        /// </summary>
        internal static string LocalizeObjectName(string name)
        {
            var s = Strings.Instance;
            var localized = name switch
            {
                "Left Stick X" => s.DevObj_LeftStickX,
                "Left Stick Y" => s.DevObj_LeftStickY,
                "Left Trigger" => s.DevObj_LeftTrigger,
                "Right Stick X" => s.DevObj_RightStickX,
                "Right Stick Y" => s.DevObj_RightStickY,
                "Right Trigger" => s.DevObj_RightTrigger,
                "D-Pad" => s.DevObj_DPad,
                "Left Shoulder" => s.DevObj_LeftShoulder,
                "Right Shoulder" => s.DevObj_RightShoulder,
                "Left Stick Button" => s.DevObj_LeftStickButton,
                "Right Stick Button" => s.DevObj_RightStickButton,
                "Back" => s.DevObj_Back,
                "Start" => s.DevObj_Start,
                "Guide" => s.DevObj_Guide,
                "X Axis" => s.DevObj_XAxis,
                "Y Axis" => s.DevObj_YAxis,
                "Z Axis" => s.DevObj_ZAxis,
                "X Rotation" => s.DevObj_XRotation,
                "Y Rotation" => s.DevObj_YRotation,
                "Z Rotation" => s.DevObj_ZRotation,
                "POV" => s.DevObj_POV,
                "Misc 1" => s.DevObj_Misc1,
                "Right Paddle 1" => s.DevObj_RightPaddle1,
                "Right Paddle 2" => s.DevObj_RightPaddle2,
                "Left Paddle 1" => s.DevObj_LeftPaddle1,
                "Left Paddle 2" => s.DevObj_LeftPaddle2,
                "Misc 2" => s.DevObj_Misc2,
                "Misc 3" => s.DevObj_Misc3,
                "Misc 4" => s.DevObj_Misc4,
                "Misc 5" => s.DevObj_Misc5,
                "Misc 6" => s.DevObj_Misc6,
                _ => null
            };
            if (localized != null) return localized;

            // Keyboard key names (invariant Engine names → localized display).
            var keyLocalized = name switch
            {
                "Backspace" => s.Key_Backspace,
                "Tab" => s.Key_Tab,
                "Enter" => s.Key_Enter,
                "Shift" => s.Key_Shift,
                "Ctrl" => s.Key_Control,
                "Alt" => s.Key_Alt,
                "Pause" => s.Key_Pause,
                "CapsLock" => s.Key_CapsLock,
                "Escape" => s.Key_Escape,
                "Space" => s.Key_Space,
                "PageUp" => s.Key_PageUp,
                "PageDown" => s.Key_PageDown,
                "End" => s.Key_End,
                "Home" => s.Key_Home,
                "Left" => s.Key_Left,
                "Up" => s.Key_Up,
                "Right" => s.Key_Right,
                "Down" => s.Key_Down,
                "PrintScreen" => s.Key_PrintScreen,
                "Insert" => s.Key_Insert,
                "Delete" => s.Key_Delete,
                "LWin" => s.Key_LWin,
                "RWin" => s.Key_RWin,
                "Apps" => s.Key_Apps,
                "Numpad *" => s.Key_NumpadMultiply,
                "Numpad +" => s.Key_NumpadAdd,
                "Numpad -" => s.Key_NumpadSubtract,
                "Numpad ." => s.Key_NumpadDecimal,
                "Numpad /" => s.Key_NumpadDivide,
                "NumLock" => s.Key_NumLock,
                "ScrollLock" => s.Key_ScrollLock,
                "LShift" => s.Key_LeftShift,
                "RShift" => s.Key_RightShift,
                "LCtrl" => s.Key_LeftCtrl,
                "RCtrl" => s.Key_RightCtrl,
                "LAlt" => s.Key_LeftAlt,
                "RAlt" => s.Key_RightAlt,
                "Semicolon" => s.Key_Semicolon,
                "Equals" => s.Key_Equals,
                "Comma" => s.Key_Comma,
                "Minus" => s.Key_Minus,
                "Period" => s.Key_Period,
                "Slash" => s.Key_Slash,
                "Grave" => s.Key_Grave,
                "LeftBracket" => s.Key_LeftBracket,
                "Backslash" => s.Key_Backslash,
                "RightBracket" => s.Key_RightBracket,
                "Apostrophe" => s.Key_Apostrophe,
                _ => null
            };
            if (keyLocalized != null) return keyLocalized;

            // Numpad digits: "Numpad 0" through "Numpad 9"
            if (name.StartsWith("Numpad ", System.StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(7), out int numpadIdx))
                return string.Format(s.Key_Numpad, numpadIdx);

            // Parametric patterns: "Slider 0", "POV 2", "Button 5"
            if (name.StartsWith("Slider ", System.StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(7), out int sliderIdx))
                return string.Format(s.DevObj_Slider, sliderIdx);

            if (name.StartsWith("POV ", System.StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(4), out int hatIdx))
                return string.Format(s.DevObj_POVN, hatIdx);

            if (name.StartsWith("Button ", System.StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(7), out int btnIdx))
                return string.Format(s.DevObj_Button, btnIdx);

            return name;
        }

        internal static string ResolvePrefixLabel(string prefix) => prefix.ToUpperInvariant() switch
        {
            "I" => Strings.Instance.Mapping_Inv,
            "H" => Strings.Instance.Mapping_Half,
            "IH" => Strings.Instance.Mapping_InvHalf,
            _ => ""
        };

        internal static string ResolvePovDirection(string dir) => dir switch
        {
            "Up" => Strings.Instance.POV_Up,
            "UpRight" => Strings.Instance.POV_UpRight,
            "Right" => Strings.Instance.POV_Right,
            "DownRight" => Strings.Instance.POV_DownRight,
            "Down" => Strings.Instance.POV_Down,
            "DownLeft" => Strings.Instance.POV_DownLeft,
            "Left" => Strings.Instance.POV_Left,
            "UpLeft" => Strings.Instance.POV_UpLeft,
            _ => dir
        };

        /// <summary>
        /// Builds a numbered display string from a raw descriptor (e.g., "Button 0", "Axis 1",
        /// "POV 0 Up") with I/H/IH prefix support. Used when Force Raw Joystick Mode is active.
        /// </summary>
        internal static string ResolveRawNumberedText(string descriptor)
        {
            string s = descriptor;
            string prefix = "";
            if (s.StartsWith("IH", System.StringComparison.OrdinalIgnoreCase))
            { prefix = s.Substring(0, 2); s = s.Substring(2); }
            else if (s.StartsWith("I", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }
            else if (s.StartsWith("H", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }

            // Bundled motion-passthrough descriptors carry no integer index.
            if (s.StartsWith("Motion ", System.StringComparison.Ordinal))
            {
                var siM = Strings.Instance;
                string sub = s.Substring(7).Trim();
                if (sub.Equals("Gyro",  System.StringComparison.OrdinalIgnoreCase)) return prefix + siM.Mapping_MotionGyro;
                if (sub.Equals("Accel", System.StringComparison.OrdinalIgnoreCase)) return prefix + siM.Mapping_MotionAccel;
                return null;
            }

            string[] parts = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
                return null;

            string typeName = parts[0].ToLowerInvariant();
            var si = Strings.Instance;
            string display = typeName switch
            {
                "button" => string.Format(si.DevObj_Button, index),
                "axis" => string.Format(si.DevObj_AxisN, index),
                "slider" => string.Format(si.DevObj_Slider, index),
                "pov" when parts.Length >= 3 => string.Format(si.Mapping_POV_Format,
                    index, ResolvePovDirection(parts[2])),
                "pov" => string.Format(si.DevObj_POVN, index),
                _ => s
            };

            if (!string.IsNullOrEmpty(prefix))
            {
                string prefixLabel = ResolvePrefixLabel(prefix);
                if (!string.IsNullOrEmpty(prefixLabel))
                    display = $"{prefixLabel} {display}";
            }

            return display;
        }

        /// <summary>
        /// Builds the list of available input choices from a device.
        /// Returns axes, buttons, POVs (with directions), sliders,
        /// touchpad raw sources, gyro / motion, and touchpad gesture
        /// abstractions (last, after the raw layer).
        ///
        /// <para>When <paramref name="touchpadSettingsForPad"/> is
        /// supplied, gesture entries are gated by the per-pad
        /// <see cref="PadForge.Engine.Touchpad.TouchpadGestureSettings"/>
        /// — disabled pads + disabled gesture categories + Mode
        /// (InBoxOnly / CustomOnly / Both) all hide the matching
        /// dropdown entries. Null = no gating, shows everything the
        /// device's hardware could support (the legacy behavior).</para>
        /// </summary>
        internal static InputChoice[] BuildInputChoices(UserDevice ud,
            System.Func<int, PadForge.Engine.Touchpad.TouchpadGestureSettings> touchpadSettingsForPad = null)
        {
            var list = new System.Collections.Generic.List<InputChoice>();

            if (ud == null)
                return list.ToArray();

            var si = Strings.Instance;

            if (ud.DeviceObjects != null && ud.DeviceObjects.Length > 0)
            {
                bool useRaw = UseRawNumberedNaming(ud);

                foreach (var obj in ud.DeviceObjects)
                {
                    if (!obj.IsAxis || obj.IsSlider) continue;
                    string descriptor = $"Axis {obj.InputIndex}";
                    string display = useRaw
                        ? string.Format(si.DevObj_AxisN, obj.InputIndex)
                        : LocalizeObjectName(obj.Name);
                    list.Add(new InputChoice { Descriptor = descriptor, DisplayName = display });
                }

                foreach (var obj in ud.DeviceObjects)
                {
                    if (!obj.IsSlider) continue;
                    string descriptor = $"Slider {obj.InputIndex}";
                    string display = useRaw
                        ? string.Format(si.DevObj_Slider, obj.InputIndex)
                        : LocalizeObjectName(obj.Name);
                    list.Add(new InputChoice { Descriptor = descriptor, DisplayName = display });
                }

                foreach (var obj in ud.DeviceObjects)
                {
                    if (!obj.IsButton) continue;
                    string descriptor = $"Button {obj.InputIndex}";
                    string display = useRaw
                        ? string.Format(si.DevObj_Button, obj.InputIndex)
                        : LocalizeObjectName(obj.Name);
                    list.Add(new InputChoice { Descriptor = descriptor, DisplayName = display });
                }

                string[] povDirs = { "Up", "Right", "Down", "Left" };
                foreach (var obj in ud.DeviceObjects)
                {
                    if (!obj.IsPov) continue;
                    foreach (string dir in povDirs)
                    {
                        string descriptor = $"POV {obj.InputIndex} {dir}";
                        string dirDisplay = ResolvePovDirection(dir);
                        string display = useRaw || obj.Name != "D-Pad"
                            ? string.Format(si.Mapping_POV_Format, obj.InputIndex, dirDisplay)
                            : $"{LocalizeObjectName(obj.Name)} {dirDisplay}";
                        list.Add(new InputChoice { Descriptor = descriptor, DisplayName = display });
                    }
                }
            }
            else
            {
                bool isGamepad = !UseRawNumberedNaming(ud);

                string[] gpAxisNames = isGamepad
                    ? new[] { si.DevObj_LeftStickX, si.DevObj_LeftStickY, si.DevObj_LeftTrigger,
                              si.DevObj_RightStickX, si.DevObj_RightStickY, si.DevObj_RightTrigger }
                    : null;

                for (int i = 0; i < ud.CapAxeCount; i++)
                {
                    string display = (gpAxisNames != null && i < gpAxisNames.Length)
                        ? gpAxisNames[i]
                        : string.Format(si.DevObj_AxisN, i);
                    list.Add(new InputChoice { Descriptor = $"Axis {i}", DisplayName = display });
                }

                string[] gpBtnNames = isGamepad
                    ? new[] { "A", "B", "X", "Y",
                              si.DevObj_LeftShoulder, si.DevObj_RightShoulder,
                              si.DevObj_Back, si.DevObj_Start,
                              si.DevObj_LeftStickButton, si.DevObj_RightStickButton,
                              si.DevObj_Guide }
                    : null;

                // Prefer the live device's sparse SupportedButtonIndices so
                // devices that populate only specific slots (e.g.,
                // TouchpadOverlayDevice with just slot 16, or the touchpad-only
                // WebControllerDevice) don't surface phantom raw "Button N"
                // entries for every slot between 0 and the highest populated
                // index. Falls back to the dense range when no live wrapper
                // is available (offline device).
                var sparse = ud.Device?.SupportedButtonIndices;
                if (sparse != null && sparse.Length > 0)
                {
                    foreach (int i in sparse)
                    {
                        string display = (gpBtnNames != null && i < gpBtnNames.Length)
                            ? gpBtnNames[i]
                            : string.Format(si.DevObj_Button, i);
                        list.Add(new InputChoice { Descriptor = $"Button {i}", DisplayName = display });
                    }
                }
                else
                {
                    int btnCount = System.Math.Max(ud.CapButtonCount, ud.RawButtonCount);
                    for (int i = 0; i < btnCount; i++)
                    {
                        string display = (gpBtnNames != null && i < gpBtnNames.Length)
                            ? gpBtnNames[i]
                            : string.Format(si.DevObj_Button, i);
                        list.Add(new InputChoice { Descriptor = $"Button {i}", DisplayName = display });
                    }
                }

                for (int i = 0; i < ud.CapPovCount; i++)
                {
                    foreach (string dir in new[] { "Up", "Right", "Down", "Left" })
                    {
                        string dirDisplay = ResolvePovDirection(dir);
                        string display = isGamepad && i == 0
                            ? $"{si.DevObj_DPad} {dirDisplay}"
                            : string.Format(si.Mapping_POV_Format, i, dirDisplay);
                        list.Add(new InputChoice
                        {
                            Descriptor = $"POV {i} {dir}",
                            DisplayName = display
                        });
                    }
                }
            }

            // Touchpad raw sources (per-finger axes + click) for devices
            // with HasTouchpad or Touchpad type. Distinct from the
            // higher-level gesture entries below — these are direct
            // hardware reads (X / Y / Down per finger, Click). Gesture
            // entries are HARDWARE ABSTRACTIONS that live after the
            // gyro/motion block at the bottom of the picker, since
            // their semantics + per-pad enable toggles put them in
            // a different conceptual layer than the raw axes.
            if (ud.HasTouchpad || ud.IsTouchpad)
            {
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 0 X", DisplayName = si.Mapping_TouchpadX1 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 0 Y", DisplayName = si.Mapping_TouchpadY1 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 0 Down", DisplayName = si.Mapping_TouchpadContact1 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 1 X", DisplayName = si.Mapping_TouchpadX2 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 1 Y", DisplayName = si.Mapping_TouchpadY2 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 1 Down", DisplayName = si.Mapping_TouchpadContact2 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Click", DisplayName = si.Mapping_TouchpadClick });
            }

            // Gyro sources (for devices with a gyroscope sensor). SDL3
            // surfaces gyro uniformly across DS4 / DualSense / Switch Pro /
            // Switch 2 Pro / Joy-Con / Steam Controller / Steam Deck / any
            // third-party pad whose driver exposes SDL_SENSOR_GYRO.
            if (ud.HasGyro)
            {
                list.Add(new InputChoice { Descriptor = "Gyro Pitch",      DisplayName = si.Mapping_GyroPitch });
                list.Add(new InputChoice { Descriptor = "Gyro Yaw",        DisplayName = si.Mapping_GyroYaw });
                list.Add(new InputChoice { Descriptor = "Gyro Roll",       DisplayName = si.Mapping_GyroRoll });
                list.Add(new InputChoice { Descriptor = "Gyro Horizontal", DisplayName = si.Mapping_GyroHorizontal });
            }

            // Bundled motion-passthrough sources. Marker descriptors that
            // bind the device's full 3-axis sensor stream to a virtual
            // controller's MotionGyro / MotionAccel target. Lets users
            // re-add a deleted Motion row from the picker, and is what
            // CreateDefaultPadSetting + EnsureMotionRows write at auto-
            // map time.
            if (ud.HasGyro)
                list.Add(new InputChoice { Descriptor = "Motion Gyro",  DisplayName = si.Mapping_MotionGyro });
            if (ud.HasAccel)
                list.Add(new InputChoice { Descriptor = "Motion Accel", DisplayName = si.Mapping_MotionAccel });

            // Touchpad gesture descriptors come LAST in the per-device
            // section so they appear after raw hardware (touchpad axes,
            // gyro, motion-passthrough) — they're abstractions that
            // sit on top of the raw input. Surfacing is per actual pad
            // index (multi-pad devices get per-pad listings); per-pad
            // enable + category gating runs in
            // InputService.PopulateAvailableInputs against
            // _inputManager.TouchpadGestureSettingsProvider so disabled
            // categories don't show up in the dropdown.
            if (ud.HasTouchpad || ud.IsTouchpad)
                AddTouchpadGestureChoices(list, ud, si, touchpadSettingsForPad);

            return list.ToArray();
        }

        /// <summary>
        /// Returns true when the device should use raw numbered naming (Button 0, Axis 1, etc.)
        /// on the Mappings tab.
        /// </summary>
        internal static bool UseRawNumberedNaming(UserDevice ud) =>
            ud.ForceRawJoystickMode ||
            (ud.CapType != InputDeviceType.Gamepad &&
             ud.CapType != InputDeviceType.Mouse &&
             ud.CapType != InputDeviceType.Keyboard);

        /// <summary>Surfaces touchpad gesture descriptors in the input
        /// picker, one block per touchpad surface the device exposes.
        /// Finger-count gating: 2-finger gestures only on pads with
        /// ≥2 fingers, 3-finger only on ≥3, etc. Shape gestures
        /// (Circle / Square / ...) are always available since they're
        /// single-finger. Custom user-recorded gestures intentionally
        /// don't surface here — they appear in the picker only after
        /// the user records them through the Touchpad tab, gated by
        /// the per-gesture DeviceClass / TouchpadIndex filter.</summary>
        private static void AddTouchpadGestureChoices(
            System.Collections.Generic.List<InputChoice> list,
            UserDevice ud,
            Strings si,
            System.Func<int, PadForge.Engine.Touchpad.TouchpadGestureSettings> settingsForPad = null)
        {
            // Best-effort pad / finger counts. Live device snapshot
            // gives the authoritative numbers; absent that, fall back
            // to the historical single-pad, two-finger assumption.
            int numPads = 1;
            int[] perPadFingers = new[] { 2 };
            try
            {
                var state = ud.Device?.GetCurrentState();
                if (state?.Touchpads != null && state.Touchpads.Length > 0)
                {
                    numPads = state.Touchpads.Length;
                    perPadFingers = new int[numPads];
                    for (int p = 0; p < numPads; p++)
                        perPadFingers[p] = state.Touchpads[p]?.MaxFingers ?? 0;
                }
            }
            catch { /* defensive: pad-discovery failures fall back to defaults */ }

            // Multi-pad devices (Steam Controller 2026 / Steam Deck /
            // original Steam Controller) need a per-pad disambiguator
            // in the display name so the picker doesn't show two
            // identical "Swipe Up" entries the user can't tell apart.
            // Single-pad devices (DualSense / DS4 / etc.) skip the
            // wrapping so the labels stay terse.
            bool multiPad = numPads > 1;

            for (int p = 0; p < numPads; p++)
            {
                int max = perPadFingers[p];
                string PadWrap(string label) => multiPad
                    ? string.Format(si.Mapping_TouchpadGesture_PadPrefix_Format, p, label)
                    : label;

                // Gating — when the App layer passes a per-pad settings
                // provider, surface only the gesture categories the
                // user has enabled. Disabled pads contribute nothing;
                // "InBoxOnly" suppresses custom (custom is surfaced by
                // a different code path in InputService); each category
                // toggle hides its descriptors when off. Provider==null
                // defaults to "show everything" so callers without
                // profile context (legacy / future device-only picker)
                // still get a functional list.
                var s = settingsForPad?.Invoke(p);
                if (s != null && !s.Enabled) continue;
                bool showInBox = s == null
                    || string.Equals(s.Mode, "Both", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Mode, "InBoxOnly", System.StringComparison.OrdinalIgnoreCase);
                if (!showInBox) continue;
                bool gate4Way      = s?.EnableFourWaySwipes        ?? true;
                bool gate8Way      = s?.EnableEightWaySwipes       ?? true;
                bool gateRadial    = s?.EnableRadialZones          ?? true;
                int  radialCount   = s?.RadialZoneCount             ?? 8;
                bool gateTaps      = s?.EnableTaps                 ?? true;
                bool gateLongPress = s?.EnableLongPress            ?? true;
                bool gateTwoSwipe  = s?.EnableTwoFingerSwipes      ?? true;
                bool gatePinch     = s?.EnablePinchSpread          ?? true;
                bool gateRotate    = s?.EnableRotate               ?? true;
                bool gateThree     = s?.EnableThreeFingerGestures  ?? true;
                bool gateFour      = s?.EnableFourFingerGestures   ?? true;
                bool gateFive      = s?.EnableFiveFingerGestures   ?? true;
                bool gateShape     = s?.EnableShapeGestures        ?? true;

                // Single-finger 4-way swipes
                if (gate4Way)
                {
                    AddGesture(list, p, "SwipeUp",    PadWrap(si.Mapping_TouchpadGesture_SwipeUp));
                    AddGesture(list, p, "SwipeDown",  PadWrap(si.Mapping_TouchpadGesture_SwipeDown));
                    AddGesture(list, p, "SwipeLeft",  PadWrap(si.Mapping_TouchpadGesture_SwipeLeft));
                    AddGesture(list, p, "SwipeRight", PadWrap(si.Mapping_TouchpadGesture_SwipeRight));
                }
                // 8-way diagonals layer on top of 4-way axial
                if (gate8Way)
                {
                    AddGesture(list, p, "SwipeNE", PadWrap(si.Mapping_TouchpadGesture_SwipeNE));
                    AddGesture(list, p, "SwipeNW", PadWrap(si.Mapping_TouchpadGesture_SwipeNW));
                    AddGesture(list, p, "SwipeSE", PadWrap(si.Mapping_TouchpadGesture_SwipeSE));
                    AddGesture(list, p, "SwipeSW", PadWrap(si.Mapping_TouchpadGesture_SwipeSW));
                }
                if (gateTaps)
                {
                    AddGesture(list, p, "Tap",       PadWrap(si.Mapping_TouchpadGesture_Tap));
                    AddGesture(list, p, "DoubleTap", PadWrap(si.Mapping_TouchpadGesture_DoubleTap));
                    AddGesture(list, p, "TripleTap", PadWrap(si.Mapping_TouchpadGesture_TripleTap));
                }
                if (gateLongPress)
                    AddGesture(list, p, "LongPress", PadWrap(si.Mapping_TouchpadGesture_LongPress));
                // Radial zones — only the currently-active count
                // appears in the picker (matching the recipe semantics:
                // "Settings_side toggle gates which count fires").
                // Append the clock-position label (12, 1:30, 3, ...) so
                // the user can tell which direction a zone covers without
                // counting wedges from the top. Engine math anchors zone
                // 0 to 12 o'clock and increases clockwise.
                if (gateRadial)
                {
                    int zc = radialCount;
                    for (int z = 0; z < zc; z++)
                        list.Add(new InputChoice
                        {
                            Descriptor = $"Touchpad {p} RadialZone{zc}_{z}",
                            DisplayName = PadWrap(string.Format(
                                si.Mapping_TouchpadGesture_RadialZone_Format, zc, z)
                                + " (" + ClockPositionLabel(zc, z) + ")"),
                        });
                }
                // Single-finger shapes
                if (gateShape)
                    foreach (var name in Engine.Touchpad.InBoxShapeTemplates.Names)
                        AddGesture(list, p, name, PadWrap(ResolveShapeName(si, name)));

                if (max >= 2)
                {
                    if (gateTwoSwipe)
                    {
                        AddGesture(list, p, "TwoFingerSwipeUp",    PadWrap(si.Mapping_TouchpadGesture_TwoFingerSwipeUp));
                        AddGesture(list, p, "TwoFingerSwipeDown",  PadWrap(si.Mapping_TouchpadGesture_TwoFingerSwipeDown));
                        AddGesture(list, p, "TwoFingerSwipeLeft",  PadWrap(si.Mapping_TouchpadGesture_TwoFingerSwipeLeft));
                        AddGesture(list, p, "TwoFingerSwipeRight", PadWrap(si.Mapping_TouchpadGesture_TwoFingerSwipeRight));
                    }
                    if (gateTaps)
                        AddGesture(list, p, "TwoFingerTap", PadWrap(si.Mapping_TouchpadGesture_TwoFingerTap));
                    if (gatePinch)
                    {
                        AddGesture(list, p, "Pinch",     PadWrap(si.Mapping_TouchpadGesture_Pinch));
                        AddGesture(list, p, "Spread",    PadWrap(si.Mapping_TouchpadGesture_Spread));
                        AddGesture(list, p, "PinchAxis", PadWrap(si.Mapping_TouchpadGesture_PinchAxis));
                    }
                    if (gateRotate)
                    {
                        AddGesture(list, p, "RotateCW",   PadWrap(si.Mapping_TouchpadGesture_RotateCW));
                        AddGesture(list, p, "RotateCCW",  PadWrap(si.Mapping_TouchpadGesture_RotateCCW));
                        AddGesture(list, p, "RotateAxis", PadWrap(si.Mapping_TouchpadGesture_RotateAxis));
                    }
                }
                if (max >= 3 && gateThree)
                {
                    AddGesture(list, p, "ThreeFingerSwipeUp",    PadWrap(si.Mapping_TouchpadGesture_ThreeFingerSwipeUp));
                    AddGesture(list, p, "ThreeFingerSwipeDown",  PadWrap(si.Mapping_TouchpadGesture_ThreeFingerSwipeDown));
                    AddGesture(list, p, "ThreeFingerSwipeLeft",  PadWrap(si.Mapping_TouchpadGesture_ThreeFingerSwipeLeft));
                    AddGesture(list, p, "ThreeFingerSwipeRight", PadWrap(si.Mapping_TouchpadGesture_ThreeFingerSwipeRight));
                    AddGesture(list, p, "ThreeFingerTap",        PadWrap(si.Mapping_TouchpadGesture_ThreeFingerTap));
                }
                if (max >= 4 && gateFour)
                {
                    AddGesture(list, p, "FourFingerSwipeUp",    PadWrap(si.Mapping_TouchpadGesture_FourFingerSwipeUp));
                    AddGesture(list, p, "FourFingerSwipeDown",  PadWrap(si.Mapping_TouchpadGesture_FourFingerSwipeDown));
                    AddGesture(list, p, "FourFingerSwipeLeft",  PadWrap(si.Mapping_TouchpadGesture_FourFingerSwipeLeft));
                    AddGesture(list, p, "FourFingerSwipeRight", PadWrap(si.Mapping_TouchpadGesture_FourFingerSwipeRight));
                    AddGesture(list, p, "FourFingerTap",        PadWrap(si.Mapping_TouchpadGesture_FourFingerTap));
                }
                if (max >= 5 && gateFive)
                {
                    AddGesture(list, p, "FiveFingerTap", PadWrap(si.Mapping_TouchpadGesture_FiveFingerTap));
                }
            }
        }

        private static void AddGesture(System.Collections.Generic.List<InputChoice> list,
            int padIdx, string name, string display)
        {
            list.Add(new InputChoice
            {
                Descriptor = $"Touchpad {padIdx} {name}",
                DisplayName = string.IsNullOrEmpty(display) ? name : display,
            });
        }

        private static string ResolveShapeName(Strings si, string shape) => shape switch
        {
            "Circle"     => si.Mapping_TouchpadGesture_Circle,
            "CircleCCW"  => si.Mapping_TouchpadGesture_CircleCCW,
            "Square"     => si.Mapping_TouchpadGesture_Square,
            "Triangle"   => si.Mapping_TouchpadGesture_Triangle,
            "Z"          => si.Mapping_TouchpadGesture_Z,
            "Checkmark"  => si.Mapping_TouchpadGesture_Checkmark,
            _            => shape,
        };

        /// <summary>Returns the analog-clock-face position for a radial
        /// zone. Zone 0 anchors at 12 o'clock (top) and zones increase
        /// clockwise, matching the engine math. Output is the raw
        /// clock-face number ("12", "3", "6", "9") or, for zone counts
        /// that don't land on whole hours (8 zones at 45° steps), the
        /// half-hour notation ("1:30", "4:30", etc.). Locale-agnostic
        /// — analog clock numbers read the same in every language.</summary>
        private static string ClockPositionLabel(int zoneCount, int zoneIdx)
        {
            if (zoneCount <= 0) return zoneIdx.ToString();
            // 12 hours × 2 half-hours = 24 half-hour ticks around the dial.
            // Zone i sits at (24 * i / zoneCount) half-hour ticks from 12.
            int halfHourTicks = (24 * zoneIdx) / zoneCount;
            int wholeHours = halfHourTicks / 2;
            bool isHalfHour = (halfHourTicks % 2) != 0;
            int displayHours = wholeHours == 0 ? 12 : wholeHours;
            return isHalfHour ? $"{displayHours}:30" : displayHours.ToString();
        }
    }
}
