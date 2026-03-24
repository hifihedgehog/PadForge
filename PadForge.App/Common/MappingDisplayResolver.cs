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
                _ => null
            };
            if (localized != null) return localized;

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
        /// Returns axes, buttons, POVs (with directions), sliders, and touchpad sources.
        /// </summary>
        internal static InputChoice[] BuildInputChoices(UserDevice ud)
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

                int btnCount = System.Math.Max(ud.CapButtonCount, ud.RawButtonCount);
                for (int i = 0; i < btnCount; i++)
                {
                    string display = (gpBtnNames != null && i < gpBtnNames.Length)
                        ? gpBtnNames[i]
                        : string.Format(si.DevObj_Button, i);
                    list.Add(new InputChoice { Descriptor = $"Button {i}", DisplayName = display });
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

            // Touchpad sources (for devices with HasTouchpad or Touchpad type).
            if (ud.HasTouchpad || ud.IsTouchpad)
            {
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 0 X", DisplayName = si.Mapping_TouchpadX1 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 0 Y", DisplayName = si.Mapping_TouchpadY1 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 0 Down", DisplayName = si.Mapping_TouchpadContact1 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 1 X", DisplayName = si.Mapping_TouchpadX2 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 1 Y", DisplayName = si.Mapping_TouchpadY2 });
                list.Add(new InputChoice { Descriptor = "Touchpad 0 Finger 1 Down", DisplayName = si.Mapping_TouchpadContact2 });
            }

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
    }
}
