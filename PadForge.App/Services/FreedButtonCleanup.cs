using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Common.Input.Twitch;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Clears what still names a button whose word, phrase, tag or learned
    /// button was removed. The registries give the lowest free button to the
    /// next one added, so anything left naming the old one would fire the new
    /// one with no warning: a removed "!home" mapped to Guide would hand Guide
    /// to the next word.
    ///
    /// <para>The walk covers everywhere <see cref="InputService.RemapDeviceGuidsInSlotMappingSets"/>,
    /// <see cref="InputService.RemapDeviceGuidsInMacros"/> and
    /// <see cref="InputService.RemapDeviceGuidsInStoredPadSettings"/> follow a
    /// device: the slots' mapping sets, the PadSettings they are rebuilt from,
    /// the macros, the engage pins, and the same again in every saved profile.
    /// A source with no device names no button of this one, so it stays.</para>
    /// </summary>
    internal static class FreedButtonCleanup
    {
        /// <summary>Whether a descriptor read on a device is the freed
        /// button.</summary>
        internal delegate bool NamesButton(string deviceGuid, string descriptor);

        private static readonly PropertyInfo[] s_padSettingText = typeof(PadSetting)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite)
            .ToArray();

        public static void ForChatWord(int button, IEnumerable<PadViewModel> pads)
            => Clear(ButtonOn(new[] { TwitchChatDevice.Identity }, button), pads);

        /// <summary>A phrase is a button on a microphone's own row, and
        /// "Voice Phrase N" on a pad that hears it. A variable speaks raw
        /// buttons, so on a pad it holds the place the pulse is stamped at
        /// (<see cref="SourceCoercion.VoicePhraseButtonBase"/> + N). No other
        /// button of a pad reaches that far. A keyboard's row does, with its
        /// keys, so a pad's row alone is read that way.</summary>
        public static void ForVoicePhrase(int button, IEnumerable<PadViewModel> pads)
        {
            var onRow = ButtonOn(DevicesOfType(InputDeviceType.Microphone), button);
            var onPad = ButtonOn(DevicesWhere(d => d.CapType == InputDeviceType.Gamepad || d.HasVoicePhrases),
                SourceCoercion.VoicePhraseButtonBase + button);
            string pulse = SourceCoercion.VoicePhraseDescriptorForButton(button);
            Clear((device, d) => onRow(device, d) || onPad(device, d) || IsDescriptor(d, pulse), pads);
        }

        /// <summary>A tag is a button on a reader's own row, and "NFC Tag N"
        /// on a pad that reads it.</summary>
        public static void ForNfcTag(int button, IEnumerable<PadViewModel> pads)
        {
            var onRow = ButtonOn(DevicesOfType(InputDeviceType.Nfc), button);
            string pulse = SourceCoercion.NfcTagDescriptorForButton(button);
            Clear((device, d) => onRow(device, d) || IsDescriptor(d, pulse), pads);
        }

        public static void ForHandheldButton(int button, IEnumerable<PadViewModel> pads)
            => Clear(ButtonOn(DevicesOfType(InputDeviceType.HandheldButtons), button), pads);

        private static HashSet<Guid> DevicesOfType(int type) => DevicesWhere(d => d.CapType == type);

        private static HashSet<Guid> DevicesWhere(Func<UserDevice, bool> test)
        {
            lock (SettingsManager.UserDevices.SyncRoot)
                return SettingsManager.UserDevices.Items.Where(d => d != null && test(d))
                    .Select(d => d.InstanceGuid).ToHashSet();
        }

        private static NamesButton ButtonOn(IEnumerable<Guid> devices, int button)
        {
            var set = devices as HashSet<Guid> ?? devices.ToHashSet();
            string name = "Button " + button;
            return (device, d) => Guid.TryParse(device, out var g) && set.Contains(g) && IsDescriptor(d, name);
        }

        // A descriptor can carry the I, H or IH prefix a read's inversion and
        // half axis are spelled with.
        private static bool IsDescriptor(string descriptor, string name)
            => !string.IsNullOrEmpty(descriptor)
                && string.Equals(SettingsService.StripSourcePrefix(descriptor), name, StringComparison.OrdinalIgnoreCase);

        /// <summary>The walk. Runs on the UI thread, which owns the view
        /// models and the settings lists.</summary>
        internal static void Clear(NamesButton names, IEnumerable<PadViewModel> pads)
        {
            var changedSlots = new HashSet<int>();
            var sets = SettingsManager.SlotMappingSets;
            if (sets != null)
            {
                for (int slot = 0; slot < sets.Length; slot++)
                    if (ClearSet(sets[slot], names)) changedSlots.Add(slot);
            }

            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                lock (settings.SyncRoot)
                {
                    foreach (var us in settings.Items)
                    {
                        if (us == null) continue;
                        var cleared = WithoutButton(us.GetPadSetting(), us.InstanceGuid.ToString(), names);
                        if (cleared == null) continue;
                        // A PadSetting can be shared by checksum, so the row
                        // takes its own copy.
                        us.SetPadSetting(cleared);
                        us.PadSettingChecksum = cleared.PadSettingChecksum;
                    }
                }
            }

            if (SettingsManager.Profiles != null)
                foreach (var profile in SettingsManager.Profiles)
                    ClearProfile(profile, names);
            ClearProfile(SettingsManager.PendingDefaultSnapshot, names);

            if (SettingsManager.GlobalMacros != null)
            {
                foreach (var g in SettingsManager.GlobalMacros)
                {
                    if (g?.TriggerEntries == null) continue;
                    g.TriggerEntries = g.TriggerEntries
                        .Where(e => e == null || e.IsAxis || !names(e.DeviceInstanceGuid.ToString(), "Button " + e.ButtonIndex))
                        .ToArray();
                }
            }

            if (pads == null) return;
            foreach (var pad in pads)
            {
                if (pad == null) continue;
                ClearPadView(pad, names);
                // The grid mirrors the set, and the next save writes the grid
                // back over it.
                if (changedSlots.Contains(pad.PadIndex) && !InputService.VmMappingsStale)
                    InputService.RefreshMappingsToViewModel(pad);
            }
        }

        /// <summary>A set without the button. True when anything left.</summary>
        internal static bool ClearSet(MappingSet ms, NamesButton names)
        {
            if (ms == null) return false;
            bool changed = false;
            SettingsService.RemoveSourcesFromSet(ms, s =>
            {
                bool remove = ClearSource(s, names, ref changed);
                changed |= remove;
                return remove;
            });
            if (ms.ShiftActivators != null)
            {
                foreach (var a in ms.ShiftActivators)
                {
                    if (a == null) continue;
                    if (names(a.DeviceGuid, a.Descriptor)) { a.Descriptor = ""; changed = true; }
                    if (names(a.ChordSecondDeviceGuid, a.ChordSecondDescriptor)) { a.ChordSecondDescriptor = ""; changed = true; }
                    if (names(a.CyclePrevDeviceGuid, a.CyclePrevDescriptor)) { a.CyclePrevDescriptor = ""; changed = true; }
                }
            }
            if (ms.Menus != null)
            {
                foreach (var m in ms.Menus)
                {
                    if (m == null) continue;
                    if (names(m.DeviceGuid, m.ClickDescriptor)) { m.ClickDescriptor = ""; changed = true; }
                    if (names(m.DeviceGuid, m.CustomXDescriptor)) { m.CustomXDescriptor = ""; changed = true; }
                    if (names(m.DeviceGuid, m.CustomYDescriptor)) { m.CustomYDescriptor = ""; changed = true; }
                    changed |= ClearKeyboardMenu(m, names);
                }
            }
            return changed;
        }

        /// <summary>A keyboard menu's inputs without the button (#566): Opens
        /// With and every control of every layout, steering and pointers
        /// among them. An input reads on its own device, else the menu's. A
        /// combination that names the button goes whole, as a chord left with
        /// one leg would fire on that leg alone.</summary>
        private static bool ClearKeyboardMenu(PadForge.Engine.Menus.MenuDefinitionEntry m, NamesButton names)
        {
            if (m.Kind != PadForge.Engine.Menus.MenuKind.Keyboard) return false;
            bool changed = false;
            string On(string device) => string.IsNullOrEmpty(device) ? m.DeviceGuid : device;
            bool Names(PadForge.Engine.Menus.KeyboardInput input)
                => !input.IsEmpty && (names(On(input.DeviceGuid), input.Descriptor)
                    || (input.HasAnd && names(On(input.AndDeviceGuid), input.AndDescriptor)));
            if (Names(PadForge.Engine.Menus.KeyboardMenuDefaults.OpenInput(m)))
            {
                PadForge.Engine.Menus.KeyboardMenuDefaults.SetOpenInput(m, PadForge.Engine.Menus.KeyboardInput.None);
                changed = true;
            }
            foreach (PadForge.Engine.Menus.KeyboardLayout layout in Enum.GetValues(typeof(PadForge.Engine.Menus.KeyboardLayout)))
                foreach (var a in PadForge.Engine.Menus.KeyboardMenuDefaults.Actions(layout))
                    if (Names(PadForge.Engine.Menus.KeyboardMenuDefaults.Effective(m, layout, a)))
                    {
                        PadForge.Engine.Menus.KeyboardMenuDefaults.Set(m, layout, a, PadForge.Engine.Menus.KeyboardInput.None);
                        changed = true;
                    }
            return changed;
        }

        // True when the source goes: one that reads the button, or a key kind
        // left with no key it reads (MappingSource.RetargetKeyDevices' rule).
        private static bool ClearSource(MappingSource s, NamesButton names, ref bool changed)
        {
            if (s == null) return false;
            if (names(s.DeviceGuid, s.GateDescriptor)) { s.GateDescriptor = ""; changed = true; }
            bool upDown = s.Kind is "Incremental" or "Ramped";
            bool modifier = s.Kind == "InvertOnHold";
            if (!upDown && !modifier) return names(s.DeviceGuid, s.Descriptor);

            if (names(s.DescriptorDeviceGuid ?? s.DeviceGuid, s.Descriptor)) { s.Descriptor = ""; changed = true; }
            bool upGone = names(SourceKindRuntime.KeyDevice(s.ParamUpDeviceGuid, s), s.ParamUp);
            bool downGone = names(SourceKindRuntime.KeyDevice(s.ParamDownDeviceGuid, s), s.ParamDown);
            bool modifierGone = names(SourceKindRuntime.KeyDevice(s.ParamModifierDeviceGuid, s), s.ParamModifier);
            if (upGone) { s.ParamUp = ""; changed = true; }
            if (downGone) { s.ParamDown = ""; changed = true; }
            if (modifierGone) { s.ParamModifier = ""; changed = true; }
            if (upDown) return (upGone || downGone) && string.IsNullOrEmpty(s.ParamUp) && string.IsNullOrEmpty(s.ParamDown);
            return modifierGone;
        }

        /// <summary>A copy of a PadSetting without the button, or null when
        /// it does not name it, its raw, MIDI, keyboard and mouse, and VR
        /// mappings included. A set is rebuilt from these on every device
        /// assignment (SettingsService.MergeMappingSetsFromLegacy), so a name
        /// left here brings its row back.</summary>
        internal static PadSetting WithoutButton(PadSetting ps, string device, NamesButton names)
        {
            if (ps == null) return null;
            ps.FlushRawMappings();
            var copy = ps.CloneDeep();
            bool changed = false;
            foreach (var p in s_padSettingText)
            {
                if (NamesItsOwnController(copy, p.Name)) continue;
                if (names(device, (string)p.GetValue(copy)))
                {
                    p.SetValue(copy, "");
                    changed = true;
                }
            }
            // These name a controller of their own, and the walk above left
            // them to this when they do.
            if (names(copy.GyroAimEngageDeviceGuid, copy.GyroAimEngageButton)) { copy.GyroAimEngageButton = ""; changed = true; }
            if (names(copy.LeftTriggerRouteActivatorDeviceGuid, copy.LeftTriggerRouteActivator)) { copy.LeftTriggerRouteActivator = ""; changed = true; }
            if (names(copy.RightTriggerRouteActivatorDeviceGuid, copy.RightTriggerRouteActivator)) { copy.RightTriggerRouteActivator = ""; changed = true; }
            if (copy.MouseGestureSettings != null)
            {
                foreach (var e in copy.MouseGestureSettings)
                {
                    if (e?.Settings != null && names(e.Settings.CustomEngageDeviceGuid, e.Settings.CustomEngageButton))
                    {
                        e.Settings.CustomEngageButton = "";
                        changed = true;
                    }
                }
            }
            changed |= ClearEntries(copy.RawMappingEntries, copy.SetRawMapping, device, names);
            changed |= ClearEntries(copy.MidiMappingEntries, copy.SetMidiMapping, device, names);
            changed |= ClearEntries(copy.KbmMappingEntries, copy.SetKbmMapping, device, names);
            changed |= ClearEntries(copy.VrMappingEntries, copy.SetVrMapping, device, names);
            if (!changed) return null;
            copy.FlushRawMappings();
            copy.FlushMidiMappings();
            copy.FlushKbmMappings();
            copy.FlushVrMappings();
            copy.UpdateChecksum();
            return copy;
        }

        private static bool ClearEntries(RawMappingEntry[] entries, Action<string, string> set, string device, NamesButton names)
        {
            if (entries == null) return false;
            bool changed = false;
            foreach (var e in entries)
            {
                if (e != null && names(device, e.Value))
                {
                    set(e.Key, null);
                    changed = true;
                }
            }
            return changed;
        }

        // A pin with its controller named reads that controller, whichever
        // row's PadSetting holds it. With none named it reads this row.
        private static bool NamesItsOwnController(PadSetting ps, string property) => property switch
        {
            nameof(PadSetting.GyroAimEngageButton) => !string.IsNullOrEmpty(ps.GyroAimEngageDeviceGuid),
            nameof(PadSetting.LeftTriggerRouteActivator) => !string.IsNullOrEmpty(ps.LeftTriggerRouteActivatorDeviceGuid),
            nameof(PadSetting.RightTriggerRouteActivator) => !string.IsNullOrEmpty(ps.RightTriggerRouteActivatorDeviceGuid),
            _ => false,
        };

        private static void ClearProfile(ProfileData profile, NamesButton names)
        {
            if (profile == null) return;
            if (profile.SlotMappingSets != null)
                foreach (var ms in profile.SlotMappingSets)
                    ClearSet(ms, names);

            if (profile.Entries != null && profile.PadSettings != null)
            {
                var added = new List<PadSetting>();
                foreach (var entry in profile.Entries)
                {
                    if (entry == null) continue;
                    var ps = profile.PadSettings.FirstOrDefault(p => p != null && p.PadSettingChecksum == entry.PadSettingChecksum);
                    var cleared = WithoutButton(ps, entry.InstanceGuid.ToString(), names);
                    if (cleared == null) continue;
                    entry.PadSettingChecksum = cleared.PadSettingChecksum;
                    if (!added.Any(p => p.PadSettingChecksum == cleared.PadSettingChecksum)) added.Add(cleared);
                }
                // Another row can still read the old one by its checksum.
                if (added.Count > 0) profile.PadSettings = profile.PadSettings.Concat(added).ToArray();
            }

            if (profile.Macros != null)
            {
                foreach (var md in profile.Macros)
                {
                    if (md == null) continue;
                    if (!string.IsNullOrEmpty(md.TriggerInputs))
                    {
                        var specs = md.TriggerInputs.Split('|');
                        var kept = specs.Where(spec =>
                        {
                            var e = MacroItem.TriggerInputEntry.Parse(spec);
                            return e == null || !EntryNames(e, names);
                        }).ToArray();
                        if (kept.Length != specs.Length)
                        {
                            md.TriggerInputs = string.Join("|", kept);
                            // An empty spec falls back to these on load.
                            md.TriggerRawButtons = null;
                            md.TriggerPovs = null;
                        }
                    }
                    if (!string.IsNullOrEmpty(md.TriggerExpressionVariables))
                    {
                        // Empty entries stay: a variable's place is its letter.
                        md.TriggerExpressionVariables = string.Join("|", md.TriggerExpressionVariables.Split('|').Select(spec =>
                        {
                            var v = new MacroExpressionVariable();
                            if (!string.IsNullOrEmpty(spec)) v.Spec = spec;
                            return VariableNames(v, names) ? "" : spec;
                        }));
                    }
                }
            }

            if (profile.DeviceSlotConfigs != null)
            {
                foreach (var c in profile.DeviceSlotConfigs)
                    if (c != null && names(c.AudioMirrorEngageDeviceGuid, c.AudioMirrorEngageButton))
                        c.AudioMirrorEngageButton = "";
            }
        }

        private static void ClearPadView(PadViewModel pad, NamesButton names)
        {
            // The view models write these back over the PadSetting on the next
            // save, as the re-key walk's notes record.
            if (names(pad.GyroAimEngageDeviceGuid, pad.GyroAimEngageButton)) pad.GyroAimEngageButton = "";
            if (names(pad.LeftTriggerRouteActivatorDeviceGuid, pad.LeftTriggerRouteActivator)) pad.LeftTriggerRouteActivator = "";
            if (names(pad.RightTriggerRouteActivatorDeviceGuid, pad.RightTriggerRouteActivator)) pad.RightTriggerRouteActivator = "";
            if (names(pad.MouseGestureCustomEngageDeviceGuid, pad.MouseGestureCustomEngageButton)) pad.MouseGestureCustomEngageButton = "";
            if (pad.PerDeviceSlotConfigs != null)
            {
                foreach (var cfg in pad.PerDeviceSlotConfigs.Values)
                    if (cfg != null && names(cfg.AudioMirrorEngageDeviceGuid, cfg.AudioMirrorEngageButton))
                        cfg.AudioMirrorEngageButton = "";
            }
            if (pad.Macros == null) return;
            foreach (var mac in pad.Macros)
            {
                if (mac == null) continue;
                var entries = mac.GetTriggerInputEntries();
                var kept = entries.Where(e => e == null || !EntryNames(e, names)).ToList();
                if (kept.Count != entries.Count) mac.SetTriggerInputEntries(kept);
                if (mac.TriggerExpressionVariables == null) continue;
                foreach (var v in mac.TriggerExpressionVariables)
                    if (v != null && VariableNames(v, names)) v.RawButton = -1;
            }
        }

        private static bool EntryNames(MacroItem.TriggerInputEntry e, NamesButton names)
            => (e.RawButton >= 0 && names(e.DeviceGuid.ToString(), "Button " + e.RawButton))
                || names(e.DeviceGuid.ToString(), e.SourceDescriptor);

        private static bool VariableNames(MacroExpressionVariable v, NamesButton names)
            => v.RawButton >= 0 && names(v.DeviceGuid.ToString(), "Button " + v.RawButton);
    }
}
