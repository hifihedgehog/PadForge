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

            // "Motion Lean L" (#199) resolves contextually by device (Nunchuk /
            // left Joy-Con), in BOTH naming modes, so it sits above the
            // raw-numbered early return: Wii remotes are raw-numbered class and
            // they are exactly where this label matters.
            if (PadForge.Engine.Common.Mapping.SourceCoercion.IsMotionLeanAuxDescriptor(mapping.SourceDescriptor))
            {
                mapping.SetResolvedSourceText(ResolveMotionLeanAuxName(ud));
                return;
            }
            if (PadForge.Engine.Data.MappingSetMigrator.IsMotionAccelAuxDescriptor(mapping.SourceDescriptor))
            {
                mapping.SetResolvedSourceText(ResolveMotionAccelAuxName(ud));
                return;
            }

            // Abstract "Gamepad ..." family (issue #9): device-agnostic
            // semantic names that resolve without device-object metadata, so
            // they sit above the raw-numbered / DeviceObjects paths.
            {
                string gamepadText = ResolveGamepadText(mapping.SourceDescriptor);
                if (gamepadText != null)
                {
                    mapping.SetResolvedSourceText(gamepadText);
                    return;
                }
            }

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
                    if (sub.Equals("Lean",  System.StringComparison.OrdinalIgnoreCase))
                        { mapping.SetResolvedSourceText(si.Mapping_MotionLean); return; }
                    return;
                }
            }

            // Device-independent touchpad / gyro families: resolve without
            // device-object metadata so imported empty-guid rows (#9, owner
            // report 2026-07-13) don't fall back to the raw 0-based
            // descriptor while their own dropdown shows the localized picker
            // entry. With no device context there is no single-pad case to
            // shorten for, so the any-device naming carries the 1-based pad
            // prefix everywhere, mirroring BuildDeviceAgnosticChoices;
            // concrete-device rows keep the per-device shortening (bare
            // pad-0 click).
            {
                string t = mapping.SourceDescriptor;
                string tPrefix = "";
                if (t.StartsWith("IH", System.StringComparison.OrdinalIgnoreCase))
                { tPrefix = t.Substring(0, 2); t = t.Substring(2); }
                else if (t.StartsWith("I", System.StringComparison.OrdinalIgnoreCase) && t.Length > 1 && !char.IsDigit(t[1])
                         && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(t))
                { tPrefix = t.Substring(0, 1); t = t.Substring(1); }
                else if (t.StartsWith("H", System.StringComparison.OrdinalIgnoreCase) && t.Length > 1 && !char.IsDigit(t[1]))
                { tPrefix = t.Substring(0, 1); t = t.Substring(1); }
                if (t.StartsWith("Touchpad", System.StringComparison.Ordinal)
                    || t.StartsWith("Gyro ", System.StringComparison.Ordinal)
                    || t.StartsWith("Menu ", System.StringComparison.Ordinal)
                    || t.StartsWith("Mouse Gesture ", System.StringComparison.Ordinal)
                    // #241: route NFC to the friendly resolver; the numeric
                    // token-2 path below would leave the raw descriptor on the
                    // chip (Codex #8).
                    || PadForge.Engine.Common.Mapping.SourceCoercion.IsNfcTagDescriptor(t)
                    || PadForge.Engine.Common.Mapping.SourceCoercion.IsFlickStickDescriptor(t))
                {
                    string fam = ResolveDescriptorText(t, null, padPrefixAlways: ud == null);
                    if (fam != null)
                    {
                        if (!string.IsNullOrEmpty(tPrefix))
                        {
                            string prefixLabel = ResolvePrefixLabel(tPrefix);
                            if (!string.IsNullOrEmpty(prefixLabel))
                                fam = $"{prefixLabel} {fam}";
                        }
                        mapping.SetResolvedSourceText(fam);
                    }
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
            else if (s.StartsWith("I", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(s))
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

            // Device-independent touchpad / gyro families: same delegation
            // the primary-side resolver applies, so an empty-guid neg source
            // renders the picker's naming instead of the raw descriptor.
            {
                string t = mapping.NegSourceDescriptor;
                string tPrefix = "";
                if (t.StartsWith("IH", System.StringComparison.OrdinalIgnoreCase))
                { tPrefix = t.Substring(0, 2); t = t.Substring(2); }
                else if (t.StartsWith("I", System.StringComparison.OrdinalIgnoreCase) && t.Length > 1 && !char.IsDigit(t[1])
                         && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(t))
                { tPrefix = t.Substring(0, 1); t = t.Substring(1); }
                else if (t.StartsWith("H", System.StringComparison.OrdinalIgnoreCase) && t.Length > 1 && !char.IsDigit(t[1]))
                { tPrefix = t.Substring(0, 1); t = t.Substring(1); }
                if (t.StartsWith("Touchpad", System.StringComparison.Ordinal)
                    || t.StartsWith("Gyro ", System.StringComparison.Ordinal)
                    || t.StartsWith("Menu ", System.StringComparison.Ordinal)
                    || t.StartsWith("Mouse Gesture ", System.StringComparison.Ordinal)
                    // #241: route NFC to the friendly resolver; the numeric
                    // token-2 path below would leave the raw descriptor on the
                    // chip (Codex #8).
                    || PadForge.Engine.Common.Mapping.SourceCoercion.IsNfcTagDescriptor(t)
                    || PadForge.Engine.Common.Mapping.SourceCoercion.IsFlickStickDescriptor(t))
                {
                    string fam = ResolveDescriptorText(t, null, padPrefixAlways: ud == null);
                    if (fam != null)
                    {
                        if (!string.IsNullOrEmpty(tPrefix))
                        {
                            string prefixLabel = ResolvePrefixLabel(tPrefix);
                            if (!string.IsNullOrEmpty(prefixLabel))
                                fam = $"{prefixLabel} {fam}";
                        }
                        mapping.SetResolvedNegText(fam);
                    }
                    return;
                }
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
        /// Returns null if no match found. <paramref name="padPrefixAlways"/>
        /// selects the any-device naming for the touchpad families (every
        /// label carries the 1-based pad prefix, matching
        /// BuildDeviceAgnosticChoices); false keeps the per-device
        /// shortening (bare pad-0 click, noun-wrapped pad-0 stick channels).
        /// </summary>
        internal static string ResolveDescriptorText(string descriptor, DeviceObjectItem[] objects, bool padPrefixAlways = false)
        {
            string s = descriptor;
            string prefix = "";
            if (s.StartsWith("IH", System.StringComparison.OrdinalIgnoreCase))
            { prefix = s.Substring(0, 2); s = s.Substring(2); }
            else if (s.StartsWith("I", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(s))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }
            else if (s.StartsWith("H", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            { prefix = s.Substring(0, 1); s = s.Substring(1); }

            // "Menu {id} Item {k}" (#9 B-17): a radial / touch menu cell's
            // hover-commit fire. The label keeps the RAW cell index (the
            // serialized touch_menu_button_{k} identity, matching the
            // Menus-tab editor); renumbering for display would re-create
            // the #196 off-by-one trap.
            if (PadForge.Engine.Common.Mapping.SourceCoercion.TryParseMenuItem(
                    s, out int chipMenuId, out int chipMenuItem))
            {
                return prefix + string.Format(
                    Strings.Instance.Mapping_MenuItem_Format, chipMenuId, chipMenuItem);
            }

            // Touchpad descriptors → localized display names. Mirrors the
            // picker (AddTouchpadRawChoices): per-finger axes spell out pad
            // and finger explicitly ("Touchpad 1 Finger 1 X", 1-based for
            // display, 0-based in the descriptor); the click is a single
            // SDL button with no numbering.
            if (s.StartsWith("Touchpad", System.StringComparison.Ordinal))
            {
                var si = Strings.Instance;
                var tp = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                // "Touchpad {pad} Click {window}" (v18): the windowed click
                // MUST render its window. This form used to fall into the
                // plain-click branch below, so a Left-half-only click chip
                // read as the whole-pad click (audit 2026-07-17 G2).
                if (tp.Length == 4 && tp[2].Equals("Click", System.StringComparison.OrdinalIgnoreCase))
                {
                    string w = TouchpadWindowPhrase(tp[3]);
                    if (w == null) return null;
                    string clickLabel = si.Mapping_TouchpadClick + " (" + w + ")";
                    if (int.TryParse(tp[1], out int cwPad) && (padPrefixAlways || cwPad > 0))
                        clickLabel = string.Format(si.Mapping_TouchpadGesture_PadPrefix_Format, cwPad + 1, clickLabel);
                    return prefix + clickLabel;
                }
                // "Touchpad {pad} Click" → the pad-0 click is the single SDL
                // click button and stays unnumbered on a concrete device; a
                // pad-1+ click (Steam Controller era imports) and any click
                // in the any-device context carry the 1-based pad prefix so
                // the chip reads exactly like its picker entry. Exactly 3
                // tokens: the 4-token windowed form resolved above.
                if (tp.Length == 3 && tp[2].Equals("Click", System.StringComparison.OrdinalIgnoreCase))
                {
                    string clickLabel = si.Mapping_TouchpadClick;
                    if (int.TryParse(tp[1], out int cPad) && (padPrefixAlways || cPad > 0))
                        clickLabel = string.Format(si.Mapping_TouchpadGesture_PadPrefix_Format, cPad + 1, clickLabel);
                    return prefix + clickLabel;
                }
                // "Touchpad {pad} Finger {finger} {X|Y|Down}" → explicit axis.
                // Six-part forms are the region-windowed variants: the #9
                // B-1 Left/Right halves keep their dedicated half-marked
                // formats, and the v18 tokens (Upper / Lower, the diamond
                // quadrants) render as the whole-pad label plus a window
                // parenthetical. Seven-part forms are the v18 composed
                // "Down {quadrant} {Left|Right}" windows. Both used to
                // resolve null and fall back to the raw 0-based chip
                // (audit 2026-07-17 G2).
                if (tp.Length >= 5 && tp.Length <= 7 && int.TryParse(tp[1], out int padIdx)
                    && tp[2].Equals("Finger", System.StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(tp[3], out int fingerIdx))
                {
                    // Finger ring (v26): the edge-ring pair read, whole-pad
                    // or windowed to a half; windows render as the standard
                    // parenthetical.
                    if (tp[4].Equals("Ring", System.StringComparison.OrdinalIgnoreCase))
                    {
                        if (tp.Length == 7) return null;
                        string ringWindow = "";
                        if (tp.Length == 6)
                        {
                            string rw = TouchpadWindowPhrase(tp[5]);
                            if (rw == null) return null;
                            ringWindow = " (" + rw + ")";
                        }
                        return prefix + string.Format(si.Mapping_TouchpadFingerRing_Format,
                            padIdx + 1, fingerIdx + 1) + ringWindow;
                    }
                    string fmt;
                    string windowSuffix = "";
                    if (tp.Length == 7)
                    {
                        // Composed quadrant-in-half, "Down North Left":
                        // valid on Down only, quadrant token then a
                        // horizontal half (SourceCoercion.ComposeTouchpadWindow).
                        if (!tp[4].Equals("Down", System.StringComparison.OrdinalIgnoreCase)
                            || !IsTouchpadQuadrantToken(tp[5])
                            || (!tp[6].Equals("Left", System.StringComparison.Ordinal)
                                && !tp[6].Equals("Right", System.StringComparison.Ordinal)))
                            return null;
                        fmt = si.Mapping_TouchpadFingerTouch_Format;
                        windowSuffix = " (" + TouchpadWindowPhrase(tp[5]) + ", " + TouchpadWindowPhrase(tp[6]) + ")";
                    }
                    else if (tp.Length == 6)
                    {
                        bool left = tp[5].Equals("Left", System.StringComparison.OrdinalIgnoreCase);
                        bool right = tp[5].Equals("Right", System.StringComparison.OrdinalIgnoreCase);
                        bool pressure = tp[4].Equals("Pressure", System.StringComparison.OrdinalIgnoreCase);
                        if ((left || right) && !pressure)
                        {
                            fmt =
                                  tp[4].Equals("X",    System.StringComparison.OrdinalIgnoreCase) ? (left ? si.Mapping_TouchpadFingerXLeft_Format : si.Mapping_TouchpadFingerXRight_Format)
                                : tp[4].Equals("Y",    System.StringComparison.OrdinalIgnoreCase) ? (left ? si.Mapping_TouchpadFingerYLeft_Format : si.Mapping_TouchpadFingerYRight_Format)
                                : tp[4].Equals("Down", System.StringComparison.OrdinalIgnoreCase) ? (left ? si.Mapping_TouchpadFingerTouchLeft_Format : si.Mapping_TouchpadFingerTouchRight_Format)
                                : null;
                        }
                        else
                        {
                            // Windowed Pressure (#239) renders EVERY zone token
                            // as the parenthetical, the horizontal halves
                            // included (no dedicated half-marked Pressure
                            // formats exist), and additionally accepts the
                            // pressure-only Center zone of the exclusive
                            // five-zone DS3-sim layout. X / Y / Down keep the
                            // v18 window vocabulary.
                            string w = pressure ? TouchpadPressureZonePhrase(tp[5]) : TouchpadWindowPhrase(tp[5]);
                            if (w == null) return null;
                            fmt =
                                  tp[4].Equals("X",    System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadFingerX_Format
                                : tp[4].Equals("Y",    System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadFingerY_Format
                                : tp[4].Equals("Down", System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadFingerTouch_Format
                                : pressure ? si.Mapping_TouchpadFingerPressure_Format
                                : null;
                            windowSuffix = " (" + w + ")";
                        }
                    }
                    else
                    {
                        fmt =
                              tp[4].Equals("X",        System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadFingerX_Format
                            : tp[4].Equals("Y",        System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadFingerY_Format
                            : tp[4].Equals("Down",     System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadFingerTouch_Format
                            : tp[4].Equals("Pressure", System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadFingerPressure_Format
                            : null;
                    }
                    if (fmt == null) return null;
                    return prefix + string.Format(fmt, padIdx + 1, fingerIdx + 1) + windowSuffix;
                }
                // "Touchpad {pad} Pointer {X|Y}[ Left|Right]" → the absolute
                // pointer (#9 B-15). Same 1-based pad numbering and half-
                // window formats as the Finger family; MUST run before the
                // gesture fallback below or "Pointer" would parse as a
                // gesture name and resolve to null.
                if (tp.Length >= 4 && tp.Length <= 5 && int.TryParse(tp[1], out int ptrPad)
                    && tp[2].Equals("Pointer", System.StringComparison.OrdinalIgnoreCase))
                {
                    string fmt;
                    string ptrWindowSuffix = "";
                    if (tp.Length == 5)
                    {
                        bool left = tp[4].Equals("Left", System.StringComparison.OrdinalIgnoreCase);
                        bool right = tp[4].Equals("Right", System.StringComparison.OrdinalIgnoreCase);
                        if (left || right)
                        {
                            fmt =
                                  tp[3].Equals("X", System.StringComparison.OrdinalIgnoreCase) ? (left ? si.Mapping_TouchpadPointerXLeft_Format : si.Mapping_TouchpadPointerXRight_Format)
                                : tp[3].Equals("Y", System.StringComparison.OrdinalIgnoreCase) ? (left ? si.Mapping_TouchpadPointerYLeft_Format : si.Mapping_TouchpadPointerYRight_Format)
                                : null;
                        }
                        else
                        {
                            // v18 window tokens on the pointer render as the
                            // whole-pad label plus the window parenthetical,
                            // the Finger family's rule.
                            string w = TouchpadWindowPhrase(tp[4]);
                            if (w == null) return null;
                            fmt =
                                  tp[3].Equals("X", System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadPointerX_Format
                                : tp[3].Equals("Y", System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadPointerY_Format
                                : null;
                            ptrWindowSuffix = " (" + w + ")";
                        }
                    }
                    else
                    {
                        fmt =
                              tp[3].Equals("X", System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadPointerX_Format
                            : tp[3].Equals("Y", System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_TouchpadPointerY_Format
                            : null;
                    }
                    if (fmt == null) return null;
                    return prefix + string.Format(fmt, ptrPad + 1) + ptrWindowSuffix;
                }
                // "Touchpad {pad} {GestureName}" → localized gesture label.
                // Same naming the picker builds via AddTouchpadGestureChoices.
                // padPrefixAlways (the any-device context) wraps pad 0 too,
                // matching BuildDeviceAgnosticChoices; the per-device context
                // has no pad count on this reverse path, so pad 0 stays
                // unwrapped and pads past the first always carry the prefix.
                if (tp.Length >= 3 && int.TryParse(tp[1], out int gPadIdx))
                {
                    string gestureName = string.Join(" ", tp, 2, tp.Length - 2);
                    string label = ResolveTouchpadGestureLabel(si, gestureName);
                    if (label == null) return null;
                    // Stick / D-pad channel names always carry the
                    // "Touchpad" noun so they can't be confused with the
                    // gamepad's physical sticks, mirroring the picker's
                    // StickWrap policy.
                    bool stickChannel = gestureName is "StickX" or "StickY"
                        or "DPadUp" or "DPadRight" or "DPadDown" or "DPadLeft";
                    if (padPrefixAlways || gPadIdx > 0)
                        label = string.Format(si.Mapping_TouchpadGesture_PadPrefix_Format, gPadIdx + 1, label);
                    else if (stickChannel)
                        label = string.Format(si.Mapping_TouchpadGesture_SinglePadNoun_Format, label);
                    return prefix + label;
                }
                return null;
            }

            // Gyro descriptors → localized display names. The gravity-lean
            // pair (v26) shares the prefix but is its own family.
            if (s.StartsWith("Gyro ", System.StringComparison.Ordinal))
            {
                var si = Strings.Instance;
                string axis = s.Substring(5).Trim();
                // Aux rate family (#252) before the primary axis names: the
                // left Joy-Con of a pair. Labelled with its own strings so
                // the picker never shows two identical "Gyro Pitch" rows.
                if (axis.StartsWith("L ", System.StringComparison.OrdinalIgnoreCase))
                {
                    string auxAxis = axis.Substring(2).Trim();
                    if (auxAxis.Equals("Pitch", System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroAuxPitch;
                    if (auxAxis.Equals("Yaw",   System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroAuxYaw;
                    if (auxAxis.Equals("Roll",  System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroAuxRoll;
                    return null;
                }
                if (axis.Equals("Pitch",      System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroPitch;
                if (axis.Equals("Yaw",        System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroYaw;
                if (axis.Equals("Roll",       System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroRoll;
                if (axis.Equals("Horizontal", System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroHorizontal;
                if (axis.Equals("Lean X",     System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroLeanX;
                if (axis.Equals("Lean Y",     System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_GyroLeanY;
                return null;
            }

            // Flick stick descriptors (#225) → localized display names. The
            // leading 'F' never enters the I/H prefix grammar, so the prefix
            // here is always empty; kept for shape consistency. Touch-surface
            // forms (v26) MUST resolve before the stick-name tail test: their
            // half suffix also ends with "Left".
            if (PadForge.Engine.Common.Mapping.SourceCoercion.IsFlickStickDescriptor(s))
            {
                var si = Strings.Instance;
                if (PadForge.Engine.Common.Mapping.SourceCoercion.TryGetFlickStickTouchpad(
                        s, out int fsPad, out int fsHalf))
                {
                    string label = string.Format(si.Mapping_FlickStickTouchpad_Format, fsPad + 1);
                    // 1 / 2 = SourceCoercion.TouchpadHalfLeft / Right (the
                    // MenuDefinitionEntry.HostHalf encoding).
                    if (fsHalf == 1)
                        label += " (" + si.Menu_Half_Left + ")";
                    else if (fsHalf == 2)
                        label += " (" + si.Menu_Half_Right + ")";
                    return prefix + label;
                }
                bool leftStick = s.Trim().EndsWith("Left", System.StringComparison.OrdinalIgnoreCase);
                return prefix + (leftStick ? si.Mapping_FlickStickLeft : si.Mapping_FlickStickRight);
            }

            // NFC tag descriptors (#241): "Any NFC Tag" and "NFC Tag N".
            // The numbered form resolves its registry button back to the
            // user's tag name; an unregistered/removed button falls back to
            // the generic label so a stale binding still reads sensibly.
            if (PadForge.Engine.Common.Mapping.SourceCoercion.TryGetNfcTagButton(s, out int nfcButton))
            {
                var si = Strings.Instance;
                if (nfcButton == 0) return prefix + si.Mapping_AnyNfcTag;
                foreach (var tag in PadForge.Common.Input.NfcTagRegistry.Tags)
                    if (tag.Button == nfcButton)
                        return prefix + string.Format(si.Mapping_NfcTagNamed, tag.Name);
                return prefix + string.Format(si.Mapping_NfcTagNamed, "#" + nfcButton);
            }

            // Bundled motion-passthrough descriptors → localized display names.
            if (s.StartsWith("Motion ", System.StringComparison.Ordinal))
            {
                var si = Strings.Instance;
                string sub = s.Substring(7).Trim();
                if (sub.Equals("Gyro",  System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_MotionGyro;
                if (sub.Equals("Accel", System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_MotionAccel;
                if (sub.Equals("Lean",  System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_MotionLean;
                // Aux lean (#199): this reverse path has no device context, so
                // the neutral label stands in for the contextual one.
                if (sub.Equals("Lean L", System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_AuxMotionLean;
                if (sub.Equals("Accel L", System.StringComparison.OrdinalIgnoreCase)) return prefix + si.Mapping_AuxMotionAccel;
                return null;
            }

            // Mouse-gesture pulses (issue #200) -> localized labels.
            // Grammar: "Mouse Gesture {buttonIndex} {Gesture}".
            if (s.StartsWith("Mouse Gesture ", System.StringComparison.Ordinal))
            {
                var si = Strings.Instance;
                var mg = s.Split(new[] { ' ' }, 4, System.StringSplitOptions.RemoveEmptyEntries);
                if (mg.Length < 4 || !int.TryParse(mg[2], out int mgBtn)
                    || mgBtn < 0
                    || mgBtn >= PadForge.Engine.Mouse.MouseGestureContext.ButtonCount) return null;
                string g = mg[3].Trim();
                string word =
                      g.Equals("Left",  System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_MouseGestureLeft
                    : g.Equals("Right", System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_MouseGestureRight
                    : g.Equals("Up",    System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_MouseGestureUp
                    : g.Equals("Down",  System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_MouseGestureDown
                    : g.Equals("Click", System.StringComparison.OrdinalIgnoreCase) ? si.Mapping_MouseGestureClick
                    : null;
                if (word == null) return null;
                // X1/X2 are proper button names with no locale variants.
                // Index 5 = the Custom activation (discussion #216).
                string[] mgNames = { si.Mouse_LeftClick, si.Mouse_MiddleClick, si.Mouse_RightClick, "X1", "X2", si.Mapping_MouseGestureCustom };
                return prefix + string.Format(si.Mapping_MouseGesture_Format, mgNames[mgBtn], word);
            }

            // Abstract "Gamepad ..." family (issue #9) → localized display.
            // Device-agnostic, so it resolves without device-object metadata.
            if (s.StartsWith("Gamepad ", System.StringComparison.Ordinal))
            {
                string gp = ResolveGamepadText(s);
                return gp == null ? null : prefix + gp;
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

        /// <summary>Localized display phrase for a touchpad window token
        /// (v18 grammar: SourceCoercion.ParseTouchpadHalf). Left / Right
        /// reuse the Menus tab's half atoms. The vertical halves and the
        /// diamond quadrants carry their own Mapping_TouchpadWindow_*
        /// keys (added with #239, replacing the verbatim-token stopgap
        /// the v18 batch shipped). Returns null outside the grammar.</summary>
        private static string TouchpadWindowPhrase(string token) => token switch
        {
            "Left" => Strings.Instance.Menu_Half_Left,
            "Right" => Strings.Instance.Menu_Half_Right,
            "Upper" => Strings.Instance.Mapping_TouchpadWindow_Upper,
            "Lower" => Strings.Instance.Mapping_TouchpadWindow_Lower,
            "North" => Strings.Instance.Mapping_TouchpadWindow_North,
            "South" => Strings.Instance.Mapping_TouchpadWindow_South,
            "East"  => Strings.Instance.Mapping_TouchpadWindow_East,
            "West"  => Strings.Instance.Mapping_TouchpadWindow_West,
            _ => null,
        };

        /// <summary>Localized display phrase for a PRESSURE window token
        /// (#239 grammar): the v18 window vocabulary plus the pressure-only
        /// Center zone of the exclusive five-zone DS3-sim layout. Center
        /// stays OUT of <see cref="TouchpadWindowPhrase"/> because no other
        /// family accepts it (SourceCoercion.TryParseTouchpadAxis parses
        /// Center for the Pressure axis only), so Click / Down / Ring
        /// windows keep resolving null on it. Returns null outside the
        /// grammar.</summary>
        private static string TouchpadPressureZonePhrase(string token)
            => token == "Center"
                ? Strings.Instance.Mapping_TouchpadWindow_Center
                : TouchpadWindowPhrase(token);

        /// <summary>The nine window tokens the Pressure axis accepts
        /// (#239), picker-iteration order. The four halves and four
        /// diamond quadrants come from the v18 vocabulary, Center is the
        /// pressure-only fifth zone.</summary>
        private static readonly string[] TouchpadPressureZoneTokens =
            { "Left", "Right", "Upper", "Lower", "North", "South", "East", "West", "Center" };

        /// <summary>True for the four diamond-quadrant window tokens (v18),
        /// the only tokens the 7-token composed form may lead with.</summary>
        private static bool IsTouchpadQuadrantToken(string token)
            => token is "North" or "South" or "East" or "West";

        /// <summary>Friendly member label for the abstract gamepad family
        /// (the token after <c>"Gamepad "</c>). Reuses the existing DevObj_*
        /// labels so the family reads the same as the raw per-device entries,
        /// only prefixed by "Gamepad". Paddle members carry SDL's physical
        /// name (Right/Left Paddle 1/2) so the user sees which paddle a
        /// family index means. Returns null for an unrecognized member.</summary>
        private static string GamepadMemberDisplay(string member)
        {
            var si = Strings.Instance;
            switch (member)
            {
                case "ButtonA": return "A";
                case "ButtonB": return "B";
                case "ButtonX": return "X";
                case "ButtonY": return "Y";
                case "LeftShoulder":  return si.DevObj_LeftShoulder;
                case "RightShoulder": return si.DevObj_RightShoulder;
                case "ButtonBack":    return si.DevObj_Back;
                case "ButtonStart":   return si.DevObj_Start;
                case "LeftStick":     return si.DevObj_LeftStickButton;
                case "RightStick":    return si.DevObj_RightStickButton;
                case "ButtonGuide":   return si.DevObj_Guide;
                case "Paddle1": return si.DevObj_RightPaddle1;
                case "Paddle2": return si.DevObj_LeftPaddle1;
                case "Paddle3": return si.DevObj_RightPaddle2;
                case "Paddle4": return si.DevObj_LeftPaddle2;
                case "DPadUp":    return $"{si.DevObj_DPad} {ResolvePovDirection("Up")}";
                case "DPadDown":  return $"{si.DevObj_DPad} {ResolvePovDirection("Down")}";
                case "DPadLeft":  return $"{si.DevObj_DPad} {ResolvePovDirection("Left")}";
                case "DPadRight": return $"{si.DevObj_DPad} {ResolvePovDirection("Right")}";
                case "LeftStickX":   return si.DevObj_LeftStickX;
                case "LeftStickY":   return si.DevObj_LeftStickY;
                case "RightStickX":  return si.DevObj_RightStickX;
                case "RightStickY":  return si.DevObj_RightStickY;
                case "LeftTrigger":  return si.DevObj_LeftTrigger;
                case "RightTrigger": return si.DevObj_RightTrigger;
                // Stick deflection rings (translator v17): not alias-table
                // members (they read the axis PAIR, not one canonical
                // input), but they live in the same "Gamepad " namespace so
                // the reverse resolver and the pickers name them here.
                case "LeftStickRing":  return si.Mapping_LeftStickRing;
                case "RightStickRing": return si.Mapping_RightStickRing;
                // Capsense touch channels (translator v26): the fork's
                // SDL_GetGamepadCapSense family, same non-alias namespace
                // rule as the rings.
                case "LeftStickTouch":  return si.Mapping_CapSenseLeftStickTouch;
                case "RightStickTouch": return si.Mapping_CapSenseRightStickTouch;
                case "LeftGripTouch":   return si.Mapping_CapSenseLeftGripTouch;
                case "RightGripTouch":  return si.Mapping_CapSenseRightGripTouch;
                default: return null;
            }
        }

        /// <summary>The picker's device-independent "(Any device)" group
        /// (#9): every descriptor namespace that resolves per-device at
        /// evaluation time rather than naming a concrete controller. An
        /// empty-guid source ("first device on the slot", which is what the
        /// Workshop translator emits on every row) selects OUT of this group,
        /// so the picker never has to borrow a concrete device's entry for a
        /// device-agnostic pick, and a slot with no devices at all still
        /// offers a working namespace. The set mirrors what the translator
        /// can emit with an empty guid, plus the Pressure member the #9 plan
        /// includes in the abstract family:
        /// the 25 "Gamepad ..." alias members (SourceCoercion
        /// .GamepadAliasTable), the "Gyro ..." quartet, and per touchpad
        /// surface 0/1 the "Touchpad {p} ..." finger axes, Click, touch
        /// spots, anchor D-pad, and stick output. Callers tag each choice
        /// with the empty device guid so the GroupStyle header renders the
        /// "(Any device)" label. Touchpad display names always carry the
        /// 1-based pad prefix: with no device bound there is no single-pad
        /// case to shorten for.</summary>
        internal static InputChoice[] BuildDeviceAgnosticChoices()
        {
            var si = Strings.Instance;
            var list = new System.Collections.Generic.List<InputChoice>();
            foreach (var (member, _) in PadForge.Engine.Common.Mapping.SourceCoercion.GamepadAliasTable)
            {
                string memberDisplay = GamepadMemberDisplay(member);
                if (memberDisplay == null) continue;
                list.Add(new InputChoice
                {
                    Descriptor = "Gamepad " + member,
                    DisplayName = string.Format(si.Mapping_Gamepad_Format, memberDisplay)
                });
            }

            list.Add(new InputChoice { Descriptor = "Gyro Pitch",      DisplayName = si.Mapping_GyroPitch });
            list.Add(new InputChoice { Descriptor = "Gyro Yaw",        DisplayName = si.Mapping_GyroYaw });
            list.Add(new InputChoice { Descriptor = "Gyro Roll",       DisplayName = si.Mapping_GyroRoll });
            list.Add(new InputChoice { Descriptor = "Gyro Horizontal", DisplayName = si.Mapping_GyroHorizontal });

            // Gravity-lean pair (translator v26): sustained tilt from the
            // low-passed accelerometer, the gyro-hosted dpad / deflection
            // channel. Emitted with the empty guid, so the abstract
            // namespace offers it (the flick-stick rule).
            list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.GyroLeanXDescriptor, DisplayName = si.Mapping_GyroLeanX });
            list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.GyroLeanYDescriptor, DisplayName = si.Mapping_GyroLeanY });

            // Flick stick (#225): the translator emits these with the empty
            // guid, so the abstract namespace must offer them too.
            list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.FlickStickRightDescriptor, DisplayName = si.Mapping_FlickStickRight });
            list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.FlickStickLeftDescriptor,  DisplayName = si.Mapping_FlickStickLeft });

            // Stick deflection rings (translator v17): emitted with the
            // empty guid for stick-hosted Outer Ring bindings, so the
            // abstract namespace offers them too (the flick-stick rule).
            list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.LeftStickRingDescriptor,  DisplayName = string.Format(si.Mapping_Gamepad_Format, si.Mapping_LeftStickRing) });
            list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.RightStickRingDescriptor, DisplayName = string.Format(si.Mapping_Gamepad_Format, si.Mapping_RightStickRing) });

            // Capsense touch channels (translator v26): the fork's
            // SDL_GetGamepadCapSense family (stick tops, grip handles),
            // same non-alias "Gamepad " namespace rule as the rings.
            foreach (var (capDesc, _) in PadForge.Engine.Common.Mapping.SourceCoercion.CapSenseTable)
            {
                string capLabel = ResolveGamepadText(capDesc);
                if (capLabel != null)
                    list.Add(new InputChoice { Descriptor = capDesc, DisplayName = capLabel });
            }

            // Two touchpad surfaces: the translator's trackpad resolvers
            // emit pad indices 0 (LEFT) and 1 (RIGHT, Steam Controller /
            // Deck era configs). Descriptor spellings match the resolver
            // output exactly; display strings reuse the per-device picker's
            // keys so the two lists read the same.
            for (int p = 0; p < 2; p++)
            {
                string PadWrap(string label) =>
                    string.Format(si.Mapping_TouchpadGesture_PadPrefix_Format, p + 1, label);

                list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 X",        DisplayName = string.Format(si.Mapping_TouchpadFingerX_Format,        p + 1, 1) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 Y",        DisplayName = string.Format(si.Mapping_TouchpadFingerY_Format,        p + 1, 1) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 Down",     DisplayName = string.Format(si.Mapping_TouchpadFingerTouch_Format,    p + 1, 1) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 Pressure", DisplayName = string.Format(si.Mapping_TouchpadFingerPressure_Format, p + 1, 1) });
                // Windowed Pressure (#239): the nine zone reads, offered on
                // EVERY pad (unlike the v18 halves below) because the
                // five-zone DS3-sim lives per physical pad: a Steam
                // Controller simulates the full DualShock 3 with left-pad
                // zones as the D-pad and right-pad zones as the face
                // buttons. Display names match ResolveDescriptorText
                // exactly (the mirror-closure test).
                foreach (var w in TouchpadPressureZoneTokens)
                    list.Add(new InputChoice
                    {
                        Descriptor = $"Touchpad {p} Finger 0 Pressure {w}",
                        DisplayName = string.Format(si.Mapping_TouchpadFingerPressure_Format, p + 1, 1)
                            + " (" + TouchpadPressureZonePhrase(w) + ")",
                    });
                // Finger ring (v26): the edge-ring pair read the translator
                // emits for Steam's edge_binding_radius / _invert geometry.
                // Display name matches ResolveDescriptorText exactly (the
                // mirror-closure test).
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 Ring",     DisplayName = string.Format(si.Mapping_TouchpadFingerRing_Format,     p + 1, 1) });
                // Touch-surface flick stick (v26), the flick-stick rule.
                list.Add(new InputChoice
                {
                    Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.FlickStickTouchpadPrefix + p,
                    DisplayName = string.Format(si.Mapping_FlickStickTouchpad_Format, p + 1),
                });
                // Region-windowed halves (#9 B-1) live on pad 0 only: the
                // halves model Steam's split of a SINGLE physical pad
                // (DS4 / DualSense), which is always pad 0. Multi-pad
                // devices have a real pad per half and don't need them.
                if (p == 0)
                {
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 X Left",     DisplayName = string.Format(si.Mapping_TouchpadFingerXLeft_Format,      p + 1, 1) });
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 X Right",    DisplayName = string.Format(si.Mapping_TouchpadFingerXRight_Format,     p + 1, 1) });
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 Y Left",     DisplayName = string.Format(si.Mapping_TouchpadFingerYLeft_Format,      p + 1, 1) });
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 Y Right",    DisplayName = string.Format(si.Mapping_TouchpadFingerYRight_Format,     p + 1, 1) });
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 Down Left",  DisplayName = string.Format(si.Mapping_TouchpadFingerTouchLeft_Format,  p + 1, 1) });
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger 0 Down Right", DisplayName = string.Format(si.Mapping_TouchpadFingerTouchRight_Format, p + 1, 1) });
                    // v18 windows (audit 2026-07-17 G2): vertical halves and
                    // the diamond quadrants join the pad-0 window family so
                    // the forms the engine reads (and the translator emits)
                    // are pickable, not import-only. Display names match
                    // ResolveDescriptorText exactly (the mirror-closure test).
                    foreach (var w in new[] { "Upper", "Lower", "North", "South", "East", "West" })
                        list.Add(new InputChoice
                        {
                            Descriptor = $"Touchpad {p} Finger 0 Down {w}",
                            DisplayName = string.Format(si.Mapping_TouchpadFingerTouch_Format, p + 1, 1)
                                + " (" + TouchpadWindowPhrase(w) + ")",
                        });
                    // Windowed clicks (v18): click composed with the finger-0
                    // window, Steam's requires_click on a half / zone.
                    foreach (var w in new[] { "Left", "Right", "Upper", "Lower" })
                        list.Add(new InputChoice
                        {
                            Descriptor = $"Touchpad {p} Click {w}",
                            DisplayName = string.Format(si.Mapping_TouchpadGesture_PadPrefix_Format, p + 1,
                                si.Mapping_TouchpadClick + " (" + TouchpadWindowPhrase(w) + ")"),
                        });
                    // Half-windowed finger ring + touch-surface flick (v26):
                    // the single-pad left_/right_trackpad split, pad-0 rule.
                    foreach (var w in new[] { "Left", "Right" })
                    {
                        list.Add(new InputChoice
                        {
                            Descriptor = $"Touchpad {p} Finger 0 Ring {w}",
                            DisplayName = string.Format(si.Mapping_TouchpadFingerRing_Format, p + 1, 1)
                                + " (" + TouchpadWindowPhrase(w) + ")",
                        });
                        list.Add(new InputChoice
                        {
                            Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.FlickStickTouchpadPrefix + p + " " + w,
                            DisplayName = string.Format(si.Mapping_FlickStickTouchpad_Format, p + 1)
                                + " (" + TouchpadWindowPhrase(w) + ")",
                        });
                    }
                }
                // Absolute pointer (#9 B-15): the translator emits these
                // with the empty guid for trackpad mouse_region groups, so
                // the abstract namespace must offer them (the flick-stick
                // precedent). Halves follow the Finger family's pad-0 rule.
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer X", DisplayName = string.Format(si.Mapping_TouchpadPointerX_Format, p + 1) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer Y", DisplayName = string.Format(si.Mapping_TouchpadPointerY_Format, p + 1) });
                if (p == 0)
                {
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer X Left",  DisplayName = string.Format(si.Mapping_TouchpadPointerXLeft_Format,  p + 1) });
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer X Right", DisplayName = string.Format(si.Mapping_TouchpadPointerXRight_Format, p + 1) });
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer Y Left",  DisplayName = string.Format(si.Mapping_TouchpadPointerYLeft_Format,  p + 1) });
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer Y Right", DisplayName = string.Format(si.Mapping_TouchpadPointerYRight_Format, p + 1) });
                }
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} Click",             DisplayName = PadWrap(si.Mapping_TouchpadClick) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} TouchLeft",         DisplayName = PadWrap(si.Mapping_TouchpadGesture_TouchLeft) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} TouchRight",        DisplayName = PadWrap(si.Mapping_TouchpadGesture_TouchRight) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} DPadUp",            DisplayName = PadWrap(si.Mapping_TouchpadGesture_DPadUp) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} DPadRight",         DisplayName = PadWrap(si.Mapping_TouchpadGesture_DPadRight) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} DPadDown",          DisplayName = PadWrap(si.Mapping_TouchpadGesture_DPadDown) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} DPadLeft",          DisplayName = PadWrap(si.Mapping_TouchpadGesture_DPadLeft) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} StickX",            DisplayName = PadWrap(si.Mapping_TouchpadGesture_StickX) });
                list.Add(new InputChoice { Descriptor = $"Touchpad {p} StickY",            DisplayName = PadWrap(si.Mapping_TouchpadGesture_StickY) });
            }

            return list.ToArray();
        }

        /// <summary>Reverse-resolves a <c>"Gamepad ..."</c> descriptor to its
        /// localized display (<c>"Gamepad Left Stick X"</c>). Returns null when
        /// the descriptor is not a recognized gamepad-family member. Shared by
        /// the row resolver, the neg/extra resolver, and the picker builder so
        /// all three stay in lockstep with SourceCoercion.GamepadAliasTable.</summary>
        internal static string ResolveGamepadText(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)
                || !descriptor.StartsWith("Gamepad ", System.StringComparison.Ordinal))
                return null;
            string member = descriptor.Substring("Gamepad ".Length).Trim();
            string memberDisplay = GamepadMemberDisplay(member);
            return memberDisplay == null
                ? null
                : string.Format(Strings.Instance.Mapping_Gamepad_Format, memberDisplay);
        }

        /// <summary>
        /// Maps an Engine-level object name (invariant English) to its localized display string.
        /// Falls back to the original name if no localization is defined.
        /// </summary>
        internal static string LocalizeObjectName(string name)
        {
            var s = Strings.Instance;
            // Dynamic consumer usages outside the fixed ConsumerUsageTable
            // arrive as "Consumer 0xNNNN" (issue #168). Localize the leading
            // word so the fallback chip/picker label isn't half-English.
            if (name != null && name.StartsWith("Consumer 0x", System.StringComparison.Ordinal))
                return string.Format(s.DevObj_ConsumerDynamic_Format, name.Substring("Consumer ".Length));
            var localized = name switch
            {
                "Any NFC Tag" => s.Mapping_NfcAnyTag,
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
                // Mouse axes (issue #107 rename: Speed = relative delta, Position
                // = absolute cursor). The position descriptors are localized at the
                // picker, not here, since they are not device objects.
                "Mouse Speed X" => s.Mapping_MouseSpeedX,
                "Mouse Speed Y" => s.Mapping_MouseSpeedY,
                "Mouse Scroll" => s.Mapping_MouseScroll,
                // Consumer Control buttons (issue #168), invariant names from
                // ConsumerUsageTable. Volume/track names deliberately shadow
                // the same-named keyboard VK objects: same physical button,
                // same translation (the documented double-visibility case).
                "Power" => s.DevObj_ConsumerPower,
                "Menu" => s.DevObj_ConsumerMenu,
                "OK" => s.DevObj_ConsumerOk,
                "Menu Up" => s.DevObj_ConsumerMenuUp,
                "Menu Down" => s.DevObj_ConsumerMenuDown,
                "Menu Left" => s.DevObj_ConsumerMenuLeft,
                "Menu Right" => s.DevObj_ConsumerMenuRight,
                "Menu Escape" => s.DevObj_ConsumerMenuEscape,
                "Media Play" => s.DevObj_ConsumerPlay,
                "Media Pause" => s.DevObj_ConsumerPause,
                "Record" => s.DevObj_ConsumerRecord,
                "Fast Forward" => s.DevObj_ConsumerFastForward,
                "Rewind" => s.DevObj_ConsumerRewind,
                "Next Track" => s.DevObj_ConsumerNextTrack,
                "Previous Track" => s.DevObj_ConsumerPreviousTrack,
                "Media Stop" => s.DevObj_ConsumerMediaStop,
                "Eject" => s.DevObj_ConsumerEject,
                "Play/Pause" => s.DevObj_ConsumerPlayPause,
                "Voice Command" => s.DevObj_ConsumerVoiceCommand,
                "Mute" => s.DevObj_ConsumerMute,
                "Volume Up" => s.DevObj_ConsumerVolumeUp,
                "Volume Down" => s.DevObj_ConsumerVolumeDown,
                "Quit" => s.DevObj_ConsumerQuit,
                "Channel Up" => s.DevObj_ConsumerChannelUp,
                "Channel Down" => s.DevObj_ConsumerChannelDown,
                "Media Player" => s.DevObj_ConsumerMediaPlayer,
                "Email" => s.DevObj_ConsumerEmail,
                "Calculator" => s.DevObj_ConsumerCalculator,
                "File Browser" => s.DevObj_ConsumerFileBrowser,
                "Browser Search" => s.DevObj_ConsumerBrowserSearch,
                "Browser Home" => s.DevObj_ConsumerBrowserHome,
                "Browser Back" => s.DevObj_ConsumerBrowserBack,
                "Browser Forward" => s.DevObj_ConsumerBrowserForward,
                "Browser Stop" => s.DevObj_ConsumerBrowserStop,
                "Browser Refresh" => s.DevObj_ConsumerBrowserRefresh,
                "Browser Bookmarks" => s.DevObj_ConsumerBrowserBookmarks,
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

            // Keyboard keys the engine's invariant table leaves as hex
            // ("Key 0xNN"): resolve through the macro editor's VirtualKey
            // vocabulary, which names and localizes the whole VK space.
            // The one seam covers both twin surfaces (the picker and the
            // row chips localize through this method). Undefined VK values
            // keep the hex fallback.
            if (name.StartsWith("Key 0x", System.StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(6), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int vkCode)
                && System.Enum.IsDefined(typeof(PadForge.Common.VirtualKey), vkCode))
                return ViewModels.MacroAction.VirtualKeyDisplayName(
                    (PadForge.Common.VirtualKey)vkCode);

            // Parametric patterns: "Axis 6", "Slider 0", "POV 2", "Button 5".
            // "Axis N" is the generic extra-axis family (issue #193): the named
            // standard axes ("X Axis", "Left Stick X", ...) are matched by the
            // switch above and never reach this prefix test.
            if (name.StartsWith("Axis ", System.StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(5), out int axisIdx))
                return string.Format(s.DevObj_AxisN, axisIdx);

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

        /// <summary>Contextual display label for the "Motion Lean L" aux-accel
        /// descriptor (#199). The sensor is the Nunchuk on Wii remotes
        /// (RVL-CNT-01 0x0306 / -TR 0x0330) and the left Joy-Con on combined
        /// Nintendo pairs; anything else (or no device context) gets the
        /// neutral label.</summary>
        internal static string ResolveMotionLeanAuxName(PadForge.Engine.Data.UserDevice ud)
        {
            var si = Strings.Instance;
            if (ud != null && ud.VendorId == 0x057E)
            {
                if (ud.ProdId == 0x0306 || ud.ProdId == 0x0330) return si.Mapping_NunchukLean;
                return si.Mapping_LeftJoyConLean;
            }
            return si.Mapping_AuxMotionLean;
        }

        /// <summary>Contextual display label for the "Motion Accel L"
        /// aux-accelerometer passthrough descriptor (#199 follow-up). Same
        /// device resolution as <see cref="ResolveMotionLeanAuxName"/>.</summary>
        internal static string ResolveMotionAccelAuxName(PadForge.Engine.Data.UserDevice ud)
        {
            var si = Strings.Instance;
            if (ud != null && ud.VendorId == 0x057E)
            {
                if (ud.ProdId == 0x0306 || ud.ProdId == 0x0330) return si.Mapping_NunchukAccel;
                return si.Mapping_LeftJoyConAccel;
            }
            return si.Mapping_AuxMotionAccel;
        }

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
            // Any direction held (v26): the dpad edge / click read.
            "Any" => Strings.Instance.POV_Any,
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
            else if (s.StartsWith("I", System.StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(s))
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
        private static readonly string[] MidiNoteLetters =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        private static readonly System.Collections.Generic.Dictionary<int, string> MidiCcNames = new()
        {
            [1] = "Mod Wheel", [2] = "Breath", [4] = "Foot Pedal",
            [5] = "Portamento Time", [7] = "Volume", [8] = "Balance",
            [10] = "Pan", [11] = "Expression", [64] = "Sustain",
            [65] = "Portamento", [66] = "Sostenuto", [67] = "Soft Pedal",
            [71] = "Resonance", [74] = "Brightness", [91] = "Reverb", [93] = "Chorus",
        };

        /// <summary>Emits the full MIDI namespace as input choices: notes as
        /// buttons ("Midi Note N"), CCs ("Midi CC N"), and pitch bend.</summary>
        private static void AddMidiChoices(System.Collections.Generic.List<InputChoice> list, Strings si)
        {
            for (int n = 0; n < PadForge.Engine.MidiInputState.NoteCount; n++)
            {
                string noteName = $"{MidiNoteLetters[n % 12]}{n / 12 - 1}";
                list.Add(new InputChoice
                {
                    Descriptor = $"Midi Note {n}",
                    DisplayName = string.Format(si.Mapping_MidiNote_Format, n, noteName),
                });
            }
            for (int c = 0; c < PadForge.Engine.MidiInputState.CcCount; c++)
            {
                string display = MidiCcNames.TryGetValue(c, out string nm)
                    ? string.Format(si.Mapping_MidiCcNamed_Format, c, nm)
                    : string.Format(si.Mapping_MidiCc_Format, c);
                // Absolute value (fader/knob), then the two relative-encoder
                // pulse buttons (endless rotary → up/down).
                list.Add(new InputChoice { Descriptor = $"Midi CC {c}", DisplayName = display });
                list.Add(new InputChoice { Descriptor = $"Midi CC {c} Up", DisplayName = string.Format(si.Mapping_MidiCcUp_Format, c) });
                list.Add(new InputChoice { Descriptor = $"Midi CC {c} Down", DisplayName = string.Format(si.Mapping_MidiCcDown_Format, c) });
            }
            list.Add(new InputChoice { Descriptor = "Midi Pitch Bend", DisplayName = si.Mapping_MidiPitchBend });
        }

        internal static InputChoice[] BuildInputChoices(UserDevice ud,
            System.Func<int, PadForge.Engine.Touchpad.TouchpadGestureSettings> touchpadSettingsForPad = null,
            System.Func<PadForge.Engine.Mouse.MouseGestureSettings> mouseGestureSettings = null)
        {
            var list = new System.Collections.Generic.List<InputChoice>();

            if (ud == null)
                return list.ToArray();

            var si = Strings.Instance;

            // MIDI input devices expose the whole MIDI namespace, listed
            // here (no DeviceObjects, no config): all 128 notes, all 128
            // CCs, and pitch bend. The descriptors resolve through the
            // "Midi ..." family in SourceCoercion against CustomInputState.Midi.
            if (ud.CapType == PadForge.Engine.InputDeviceType.Midi)
            {
                AddMidiChoices(list, si);
                return list.ToArray();
            }

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

            // Abstract "Gamepad ..." family (issue #9). Device-agnostic
            // semantic names for the standardized gamepad inputs, gated on
            // the device being a gamepad read through SDL's normalized
            // mapping (not a force-raw device, where "Gamepad ButtonA" would
            // read the raw joystick button instead of A). Each descriptor
            // canonicalizes in SourceCoercion to the per-device Button/Axis/
            // POV read. Gyro and touchpad members of the family reuse the
            // existing "Gyro ..." / "Touchpad ..." entries below, so they are
            // not duplicated here.
            if (ud.CapType == PadForge.Engine.InputDeviceType.Gamepad && !UseRawNumberedNaming(ud))
            {
                foreach (var (member, _) in PadForge.Engine.Common.Mapping.SourceCoercion.GamepadAliasTable)
                {
                    string memberDisplay = GamepadMemberDisplay(member);
                    if (memberDisplay == null) continue;
                    list.Add(new InputChoice
                    {
                        Descriptor = "Gamepad " + member,
                        DisplayName = string.Format(si.Mapping_Gamepad_Format, memberDisplay)
                    });
                }

                // Flick stick (#225): whole-stick mouse-turn inputs. Map one
                // to Mouse X on a keyboard/mouse slot; the engine resolves
                // the stick axes per device through the Gamepad alias table
                // and the tuning rides the Flick Stick card on the Sticks
                // tab. Same gamepad gate as the alias family: the read is
                // the canonical stick pair.
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.FlickStickRightDescriptor, DisplayName = si.Mapping_FlickStickRight });
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.FlickStickLeftDescriptor,  DisplayName = si.Mapping_FlickStickLeft });

                // Stick deflection rings (translator v17): whole-stick
                // magnitude reads, same gamepad gate and pair resolution
                // as flick stick.
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.LeftStickRingDescriptor,  DisplayName = string.Format(si.Mapping_Gamepad_Format, si.Mapping_LeftStickRing) });
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.RightStickRingDescriptor, DisplayName = string.Format(si.Mapping_Gamepad_Format, si.Mapping_RightStickRing) });
            }

            // Touchpad raw sources (per-finger axes + click) for devices
            // with HasTouchpad or Touchpad type. Distinct from the
            // higher-level gesture entries below — these are direct
            // hardware reads (X / Y / Down per finger, Click). Gesture
            // entries are HARDWARE ABSTRACTIONS that live after the
            // gyro/motion block at the bottom of the picker, since
            // their semantics + per-pad enable toggles put them in
            // a different conceptual layer than the raw axes.
            //
            // Click is dropped only for PTP system touchpads (laptop
            // trackpads enumerated via Raw Input) which have no click
            // button. They're uniquely identified by IsTouchpad &&
            // Device == null — PrecisionTouchpadReader handles them
            // directly without attaching an ISdlInputDevice wrapper.
            // Every other touchpad-capable device (DualSense, DS4,
            // web touchpad, overlay) has a wrapper and a click.
            if (ud.HasTouchpad || ud.IsTouchpad)
            {
                // One raw-axis block per touchpad surface the device exposes.
                // Descriptors stay 0-based internally ("Touchpad 0 Finger 0 X",
                // "Touchpad 1 Finger 0 X" for a second pad); the display names
                // built below are 1-based. Multi-touchpad devices (Steam
                // Controller 2026 / Steam Deck / original Steam Controller) get
                // a block per pad. Pad count comes from the live device
                // snapshot, mirroring AddTouchpadGestureChoices; absent a
                // wrapper it stays a single pad.
                // Pad + finger counts come from the live snapshot when the
                // device is online (authoritative), else from the persisted
                // Cap* values so a powered-off controller keeps the right shape
                // instead of collapsing to one pad / two fingers. SDL enumerates
                // the real per-pad finger count (SDL_GetNumGamepadTouchpadFingers):
                // the Steam Controller 2026 reports 1 finger per pad, DualSense 2.
                // Emitting a fixed two-finger block produced a dead "finger 2" on
                // single-finger pads, so gate each finger on the actual count.
                // Published snapshot (see the sole-reader pooling contract);
                // persisted Cap* counts remain the fallback.
                CustomInputState tpState = ud.InputState;

                int numPads = (tpState?.Touchpads != null && tpState.Touchpads.Length > 0)
                    ? tpState.Touchpads.Length
                    : (ud.CapTouchpadCount > 0 ? ud.CapTouchpadCount : 1);

                int FingerCount(int p)
                {
                    if (tpState?.Touchpads != null && p < tpState.Touchpads.Length && tpState.Touchpads[p] != null)
                        return tpState.Touchpads[p].MaxFingers;
                    if (ud.CapTouchpadFingerCounts != null && p < ud.CapTouchpadFingerCounts.Length)
                        return ud.CapTouchpadFingerCounts[p];
                    return 2; // legacy fallback for configs predating per-pad finger persistence
                }

                // Display names spell out both pad and finger explicitly
                // ("Touchpad 1 Finger 1 X"), 1-based for humans while the
                // descriptor stays 0-based internally. Uniform for single-
                // and multi-pad devices, so a DualSense reads
                // "Touchpad 1 Finger 1 X / Touchpad 1 Finger 2 X" and the
                // Steam Controller 2026 reads "Touchpad 1 Finger 1 X /
                // Touchpad 2 Finger 1 X". One row per finger the pad
                // actually reports (FingerCount), so single-finger pads
                // don't list a dead second finger.
                for (int p = 0; p < numPads; p++)
                {
                    int fingers = FingerCount(p);
                    for (int f = 0; f < fingers; f++)
                    {
                        list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} X",        DisplayName = string.Format(si.Mapping_TouchpadFingerX_Format,        p + 1, f + 1) });
                        list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} Y",        DisplayName = string.Format(si.Mapping_TouchpadFingerY_Format,        p + 1, f + 1) });
                        list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} Down",     DisplayName = string.Format(si.Mapping_TouchpadFingerTouch_Format,    p + 1, f + 1) });
                        list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} Pressure", DisplayName = string.Format(si.Mapping_TouchpadFingerPressure_Format, p + 1, f + 1) });
                        // Windowed Pressure (#239): the nine zone reads,
                        // offered per pad and finger with NO single-pad
                        // gate (unlike the v18 halves below): the
                        // five-zone DS3-sim lives per physical pad, so a
                        // Steam Controller needs the zones on both pads
                        // (left pad = D-pad, right pad = face buttons).
                        // Display names match ResolveDescriptorText
                        // exactly (the mirror-closure convention).
                        foreach (var w in TouchpadPressureZoneTokens)
                            list.Add(new InputChoice
                            {
                                Descriptor = $"Touchpad {p} Finger {f} Pressure {w}",
                                DisplayName = string.Format(si.Mapping_TouchpadFingerPressure_Format, p + 1, f + 1)
                                    + " (" + TouchpadPressureZonePhrase(w) + ")",
                            });
                        // Region-windowed halves (#9 B-1): only single-pad
                        // devices (DS4 / DualSense) offer them. Their one
                        // physical pad is what Steam splits into left/right
                        // halves; a multi-pad device has a real pad per
                        // half, so the windowed variants would be noise.
                        if (numPads == 1)
                        {
                            list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} X Left",     DisplayName = string.Format(si.Mapping_TouchpadFingerXLeft_Format,      p + 1, f + 1) });
                            list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} X Right",    DisplayName = string.Format(si.Mapping_TouchpadFingerXRight_Format,     p + 1, f + 1) });
                            list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} Y Left",     DisplayName = string.Format(si.Mapping_TouchpadFingerYLeft_Format,      p + 1, f + 1) });
                            list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} Y Right",    DisplayName = string.Format(si.Mapping_TouchpadFingerYRight_Format,     p + 1, f + 1) });
                            list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} Down Left",  DisplayName = string.Format(si.Mapping_TouchpadFingerTouchLeft_Format,  p + 1, f + 1) });
                            list.Add(new InputChoice { Descriptor = $"Touchpad {p} Finger {f} Down Right", DisplayName = string.Format(si.Mapping_TouchpadFingerTouchRight_Format, p + 1, f + 1) });
                            // v18 windows (G2): vertical halves + diamond
                            // quadrants, same single-pad rule as the halves
                            // above. Display matches ResolveDescriptorText.
                            foreach (var w in new[] { "Upper", "Lower", "North", "South", "East", "West" })
                                list.Add(new InputChoice
                                {
                                    Descriptor = $"Touchpad {p} Finger {f} Down {w}",
                                    DisplayName = string.Format(si.Mapping_TouchpadFingerTouch_Format, p + 1, f + 1)
                                        + " (" + TouchpadWindowPhrase(w) + ")",
                                });
                        }
                    }

                    // Absolute pointer (#9 B-15): one pair per pad (finger 0
                    // owns the pointer, so no per-finger rows), plus the
                    // single-pad half windows, mirroring the Finger family's
                    // halves rule above.
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer X", DisplayName = string.Format(si.Mapping_TouchpadPointerX_Format, p + 1) });
                    list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer Y", DisplayName = string.Format(si.Mapping_TouchpadPointerY_Format, p + 1) });
                    if (numPads == 1)
                    {
                        list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer X Left",  DisplayName = string.Format(si.Mapping_TouchpadPointerXLeft_Format,  p + 1) });
                        list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer X Right", DisplayName = string.Format(si.Mapping_TouchpadPointerXRight_Format, p + 1) });
                        list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer Y Left",  DisplayName = string.Format(si.Mapping_TouchpadPointerYLeft_Format,  p + 1) });
                        list.Add(new InputChoice { Descriptor = $"Touchpad {p} Pointer Y Right", DisplayName = string.Format(si.Mapping_TouchpadPointerYRight_Format, p + 1) });
                    }
                }

                // Touchpad click is a SINGLE SDL button (SDL_GAMEPAD_BUTTON_TOUCHPAD
                // -> Buttons[16], surfaced as "Touchpad Click" in the button list).
                // SDL defines it once with no per-pad numbering, so emit exactly one
                // click descriptor, never "Touchpad 1 Click". A multi-pad device's
                // second physical click surfaces as its own gamepad button (MISC2).
                bool isPtpSystemTouchpad = ud.IsTouchpad && ud.Device == null;
                if (!isPtpSystemTouchpad)
                {
                    list.Add(new InputChoice { Descriptor = "Touchpad 0 Click", DisplayName = si.Mapping_TouchpadClick });
                    // Windowed clicks (v18, G2): click AND finger 0 inside
                    // the window, single-pad devices only (the halves rule).
                    // Pad 0 in the per-device context stays unnumbered, the
                    // plain click's convention. ResolveDescriptorText emits
                    // the same shape.
                    if (numPads == 1)
                        foreach (var w in new[] { "Left", "Right", "Upper", "Lower" })
                            list.Add(new InputChoice
                            {
                                Descriptor = $"Touchpad 0 Click {w}",
                                DisplayName = si.Mapping_TouchpadClick + " (" + TouchpadWindowPhrase(w) + ")",
                            });
                }
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

            // Aux gyro (#252): the LEFT half of a combined Joy-Con pair,
            // whose rates are a second physical sensor (the primary gyro
            // above is the right half). Gated on its own capability, so it
            // appears only for a paired device that actually reports
            // SDL_SENSOR_GYRO_L.
            if (ud.HasGyroAux)
            {
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.GyroAuxPitchDescriptor, DisplayName = si.Mapping_GyroAuxPitch });
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.GyroAuxYawDescriptor,   DisplayName = si.Mapping_GyroAuxYaw });
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.GyroAuxRollDescriptor,  DisplayName = si.Mapping_GyroAuxRoll });
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
            // Aux accelerometer passthrough (#199 follow-up): sources the
            // slot's IMU stream from the Nunchuk / left Joy-Con instead of
            // the body. One internal descriptor, contextual display label.
            if (ud.HasAccelAux)
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Data.MappingSetMigrator.MotionAccelAuxSourceDescriptor, DisplayName = ResolveMotionAccelAuxName(ud) });
            // Aux GYRO passthrough (#252): the gyro twin of the row above,
            // so a slot can stream the left Joy-Con's full rate vector to
            // DSU / a virtual DualSense instead of the right half's.
            if (ud.HasGyroAux)
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Data.MappingSetMigrator.MotionGyroAuxSourceDescriptor, DisplayName = Strings.Instance.Mapping_MotionGyroAux });

            // Absolute cursor-position sources (#107). The cursor is system-wide,
            // read from SourceCoercion.MouseCursorProvider regardless of device, but
            // we list it under the mouse (always online) so the source's device
            // binding stays stable instead of riding a pad that can disconnect.
            if (ud.CapType == PadForge.Engine.InputDeviceType.Mouse)
            {
                list.Add(new InputChoice { Descriptor = "Mouse Position X", DisplayName = si.Mapping_MousePositionX });
                list.Add(new InputChoice { Descriptor = "Mouse Position Y", DisplayName = si.Mapping_MousePositionY });
            }

            // Wii Remote IR-camera pointer (#146). Absolute aim from the two
            // sensor-bar dots, per device (CustomInputState.Ir), so two remotes
            // keep separate pointers. Map "IR Pointer X/Y" to Mouse X/Y on a KBM
            // virtual controller to drive the cursor, or to a stick for aiming.
            if (ud.HasIrCamera)
            {
                list.Add(new InputChoice { Descriptor = "IR Pointer X", DisplayName = si.Mapping_IrPointerX });
                list.Add(new InputChoice { Descriptor = "IR Pointer Y", DisplayName = si.Mapping_IrPointerY });
                // #203: debounced "camera cannot see the sensor bar", the
                // lightgun-reload input. Button-class, so the shift-layer
                // activator dialog offers it automatically.
                list.Add(new InputChoice { Descriptor = "IR Offscreen", DisplayName = si.Mapping_IrOffscreen });
            }

            // Wii Balance Board derived sources (#146). The four corner load cells
            // also arrive raw on the stick axes; these are the friendly derived
            // channels (total weight + center-of-gravity lean).
            if (ud.IsBalanceBoard)
            {
                list.Add(new InputChoice { Descriptor = "Balance Total Weight", DisplayName = si.Mapping_BalanceTotalWeight });
                list.Add(new InputChoice { Descriptor = "Balance Lean X",       DisplayName = si.Mapping_BalanceLeanX });
                list.Add(new InputChoice { Descriptor = "Balance Lean Y",       DisplayName = si.Mapping_BalanceLeanY });
            }

            // Right Joy-Con NIR camera cover/proximity scalar (#151). Covered =
            // bright = 1, uncovered = dark = 0, per device
            // (CustomInputState.JoyConIrIntensity). Map it to a button for
            // cover-to-press, or to a trigger for analog proximity.
            if (ud.HasJoyConIr)
                list.Add(new InputChoice { Descriptor = "IR Brightness", DisplayName = si.Mapping_JoyConIrBrightness });

            // NFC tag reader (#241): the right Joy-Con / Pro Controller reads
            // NFC tags (amiibo UIDs). "Any NFC Tag" fires on any tag; each
            // registered tag (NfcTagRegistry) is its own bindable source,
            // stable button, display name from the registry. Map to a macro
            // trigger for "tap tag -> action", or to a virtual button. The
            // reader powers only while armed (a registered tag + this pad
            // online, or a capture in progress).
            // Remote rows are INCLUDED and work end to end (#241, audit
            // 2026-07-24). The owner announces the reader on the device-list
            // v3 capability tail, and a live binding here ships a
            // SourceDemand datagram that arms the owner's MCU on its own
            // demand cadence, so a tap on the remote pad reaches this
            // mapping. UserDevice.HasNfcReader is VID/PID-computed and a
            // relayed Switch pad carries its real VID/PID, so this gate
            // answers for both local and remote rows.
            if (ud.HasNfcReader)
            {
                list.Add(new InputChoice
                {
                    Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.AnyNfcTagDescriptor,
                    DisplayName = si.Mapping_AnyNfcTag,
                });
                foreach (var tag in PadForge.Common.Input.NfcTagRegistry.Tags)
                    list.Add(new InputChoice
                    {
                        Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.NfcTagDescriptorForButton(tag.Button),
                        DisplayName = string.Format(si.Mapping_NfcTagNamed, tag.Name),
                    });
            }

            // Joy-Con 2 optical mouse sensor (#154). Per-poll motion velocity in
            // mouse counts, per device (CustomInputState.JoyCon2MouseDX/DY),
            // scaled to feel like a real mouse's motion axes. Map to sticks,
            // scroll, buttons (threshold = the per-row deadzone), or a trigger.
            if (ud.HasJoyCon2Mouse)
            {
                list.Add(new InputChoice { Descriptor = "Mouse Motion X", DisplayName = si.Mapping_MouseMotionX });
                list.Add(new InputChoice { Descriptor = "Mouse Motion Y", DisplayName = si.Mapping_MouseMotionY });
            }

            // Gravity-lean input: tilt the controller like a wheel and the lean
            // angle drives whatever axis the user maps it to. A normal input
            // descriptor — it does NOT override the target's other sources.
            // Tuning (tilt deadzones / grip orientation) lives on the Gyro
            // tab's Motion Steering card, per assigned device.
            if (ud.HasAccel)
                list.Add(new InputChoice { Descriptor = "Motion Lean", DisplayName = si.Mapping_MotionLean });

            // Aux (left-side) accelerometer lean (#199): the Nunchuk's own
            // sensor on a Nunchuk-attached Wii Remote, the left half of a
            // combined Joy-Con pair. One internal descriptor, contextual
            // display label per device.
            if (ud.HasAccelAux)
                list.Add(new InputChoice { Descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.MotionLeanAuxDescriptor, DisplayName = ResolveMotionLeanAuxName(ud) });

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

            // Mouse gestures (issue #200): each SELECTED gesture button
            // carries its own five one-shot pulses, so different buttons can
            // drive different mapping combos. Buttons come from the slot's
            // Mouse-tab selection (default X1 when the engine is stopped);
            // the Enabled toggle governs firing, not visibility, so a
            // disabled setup stays discoverable.
            if (ud.IsMouse)
            {
                var mgs = mouseGestureSettings?.Invoke()
                    ?? PadForge.Engine.Mouse.MouseGestureSettings.Default();
                // X1/X2 are proper button names with no locale variants.
                // Index 5 = the Custom activation (discussion #216): its
                // five pulses list whenever the Custom button is selected,
                // same selected-governs-visibility rule as the mouse rows.
                string[] mgButtonNames = { si.Mouse_LeftClick, si.Mouse_MiddleClick, si.Mouse_RightClick, "X1", "X2", si.Mapping_MouseGestureCustom };
                string[] mgWords =
                {
                    si.Mapping_MouseGestureLeft, si.Mapping_MouseGestureRight,
                    si.Mapping_MouseGestureUp, si.Mapping_MouseGestureDown,
                    si.Mapping_MouseGestureClick,
                };
                for (int b = 0; b < mgButtonNames.Length; b++)
                {
                    if ((mgs.GestureButtons & (1 << b)) == 0) continue;
                    for (int g = 0; g < mgWords.Length; g++)
                    {
                        list.Add(new InputChoice
                        {
                            Descriptor = "Mouse Gesture " + PadForge.Engine.Mouse.MouseGestureRecognizer.Keys[b][g],
                            DisplayName = string.Format(si.Mapping_MouseGesture_Format, mgButtonNames[b], mgWords[g]),
                        });
                    }
                }
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
             ud.CapType != InputDeviceType.Keyboard &&
             // NFC readers carry one named button ("Any NFC Tag"); show the
             // friendly name from GetDeviceObjects, not "Button 0" (#150).
             ud.CapType != InputDeviceType.Nfc &&
             // Consumer Control buttons are named from the canonical usage
             // table ("Play/Pause", "Voice Command"), not "Button 0" (#168).
             ud.CapType != InputDeviceType.ConsumerControl);

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
            // per device type. PTP system touchpads (ud.IsTouchpad with
            // ud.Device == null — data flows through PrecisionTouchpadReader
            // rather than an ISdlInputDevice wrapper) always support
            // PtpMaxFingers (5) per the HID PTP spec, so the fallback
            // must reflect that or 3/4/5-finger gestures never surface
            // in the picker.
            // Persisted CapTouchpadCount keeps both pads' gesture descriptors
            // available when the device is offline (no live wrapper); the live
            // snapshot overrides with authoritative pad + finger counts.
            int numPads = ud.CapTouchpadCount > 0 ? ud.CapTouchpadCount : 1;
            int fallbackFingers = ud.IsTouchpad
                ? PadForge.Engine.PrecisionTouchpadReader.PtpMaxFingers
                : 2;
            int[] perPadFingers = new int[numPads];
            for (int i = 0; i < numPads; i++) perPadFingers[i] = fallbackFingers;
            {
                var state = ud.InputState;
                if (state?.Touchpads != null && state.Touchpads.Length > 0)
                {
                    numPads = state.Touchpads.Length;
                    perPadFingers = new int[numPads];
                    for (int p = 0; p < numPads; p++)
                        perPadFingers[p] = state.Touchpads[p]?.MaxFingers ?? 0;
                }
            }

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
                    ? string.Format(si.Mapping_TouchpadGesture_PadPrefix_Format, p + 1, label)
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

                // Stick / D-pad output is independent of the gesture
                // master toggle and the In-box / Custom mode picker —
                // it's a separate channel the user opts into via its
                // own EnableJoystickOutput. Surface its descriptors
                // first so a user who only wants stick/D-pad output (and
                // has gestures fully disabled) still sees these in the
                // picker.
                //
                // Display names ALWAYS include the word "Touchpad" so
                // the user can tell these apart from a gamepad's own
                // physical sticks and D-pad — picking "Stick X" out of
                // a flat list when your DualSense is also on the slot
                // would be ambiguous otherwise. For single-pad devices
                // the wrap is plain "Touchpad Stick X"; multi-pad uses
                // the pad-prefix format "Touchpad 1: Stick X" (1-based,
                // matching the per-finger axes and Devices previews).
                if (s?.EnableJoystickOutput == true)
                {
                    string StickWrap(string label) => multiPad
                        ? string.Format(si.Mapping_TouchpadGesture_PadPrefix_Format, p + 1, label)
                        : string.Format(si.Mapping_TouchpadGesture_SinglePadNoun_Format, label);
                    AddGesture(list, p, "StickX", StickWrap(si.Mapping_TouchpadGesture_StickX));
                    AddGesture(list, p, "StickY", StickWrap(si.Mapping_TouchpadGesture_StickY));
                    string dpadMode = s.JoystickDPadMode ?? "FourWay";
                    if (!string.Equals(dpadMode, "Off", System.StringComparison.OrdinalIgnoreCase))
                    {
                        AddGesture(list, p, "DPadUp",    StickWrap(si.Mapping_TouchpadGesture_DPadUp));
                        AddGesture(list, p, "DPadRight", StickWrap(si.Mapping_TouchpadGesture_DPadRight));
                        AddGesture(list, p, "DPadDown",  StickWrap(si.Mapping_TouchpadGesture_DPadDown));
                        AddGesture(list, p, "DPadLeft",  StickWrap(si.Mapping_TouchpadGesture_DPadLeft));
                    }
                }

                if (s != null && !s.Enabled) continue;
                bool showInBox = s == null
                    || string.Equals(s.Mode, "Both", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Mode, "InBoxOnly", System.StringComparison.OrdinalIgnoreCase);
                if (!showInBox) continue;
                bool gateSpots     = s?.EnableTouchSpots           ?? true;
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

                // Touch spots (#178): held-while-touched zone buttons.
                // TouchMulti needs a pad that can report 2+ fingers.
                if (gateSpots)
                {
                    AddGesture(list, p, "TouchLeft",  PadWrap(si.Mapping_TouchpadGesture_TouchLeft));
                    AddGesture(list, p, "TouchRight", PadWrap(si.Mapping_TouchpadGesture_TouchRight));
                    AddGesture(list, p, "TouchTop",   PadWrap(si.Mapping_TouchpadGesture_TouchTop));
                    if (max >= 2)
                        AddGesture(list, p, "TouchMulti", PadWrap(si.Mapping_TouchpadGesture_TouchMulti));
                }

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
                // Append the degree-from-top angle so the user can tell
                // which direction a zone covers without counting wedges.
                // Engine math anchors zone 0 to 0° (top) and increases
                // clockwise: 90° = right, 180° = down, 270° = left.
                if (gateRadial)
                {
                    int zc = radialCount;
                    for (int z = 0; z < zc; z++)
                        list.Add(new InputChoice
                        {
                            Descriptor = $"Touchpad {p} RadialZone{zc}_{z}",
                            DisplayName = PadWrap(string.Format(
                                si.Mapping_TouchpadGesture_RadialZone_Format, zc, z)
                                + " (" + RadialZoneAngleLabel(zc, z) + ")"),
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

        /// <summary>Resolves a bare touchpad gesture name (the descriptor
        /// with "Touchpad {pad} " stripped) to its localized label. Used
        /// by the reverse descriptor path (mapping-row neg sources, the
        /// macro trigger chip) so gesture descriptors stop rendering as
        /// raw internal names. In-box names look up their
        /// Mapping_TouchpadGesture_* key; custom gestures show their
        /// user-given name; radial zones reuse the picker's count/index
        /// + angle format. Returns null for names that resolve to no
        /// known gesture so callers can fall back to raw text.</summary>
        internal static string ResolveTouchpadGestureLabel(Strings si, string gestureName)
        {
            if (string.IsNullOrEmpty(gestureName)) return null;
            if (gestureName.StartsWith("Custom_", System.StringComparison.Ordinal))
                return gestureName.Substring("Custom_".Length);
            if (gestureName.StartsWith("RadialZone", System.StringComparison.Ordinal))
            {
                var rz = gestureName.Substring("RadialZone".Length).Split('_');
                if (rz.Length == 2 && int.TryParse(rz[0], out int zc) && int.TryParse(rz[1], out int z))
                    return string.Format(si.Mapping_TouchpadGesture_RadialZone_Format, zc, z)
                        + " (" + RadialZoneAngleLabel(zc, z) + ")";
                return null;
            }
            // Single-token in-box names map 1:1 onto their string keys
            // (TouchLeft, SwipeUp, Pinch, StickX, DPadUp, Circle, ...).
            // Strings.Get returns the key itself when no resource
            // exists, which doubles as the unknown-name signal.
            if (gestureName.IndexOf(' ') >= 0) return null;
            string key = "Mapping_TouchpadGesture_" + gestureName;
            string label = Strings.Get(key);
            return string.Equals(label, key, System.StringComparison.Ordinal) ? null : label;
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

        /// <summary>Returns the radial-zone direction as a degree-from-top
        /// label ("0°" = up, "90°" = right, "180°" = down, "270°" = left).
        /// Matches the engine math: zone 0 anchors at 0° (top), zones
        /// increase clockwise in 360/N steps. Degrees-from-top is the
        /// most culture-neutral notation for compass-style directions —
        /// the analog-clock convention (e.g. "3 o'clock" = right)
        /// doesn't read the same everywhere, but mathematics degrees do.</summary>
        private static string RadialZoneAngleLabel(int zoneCount, int zoneIdx)
        {
            if (zoneCount <= 0) return zoneIdx.ToString();
            int degrees = (360 * zoneIdx) / zoneCount;
            return degrees.ToString() + "°";
        }
    }
}
