using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>A type of virtual controller offered on a tile (#574): Add a
    /// Controller's seven and Home's three on its first page. Its values are
    /// fixed when it is made.</summary>
    public sealed class ControllerTypeChoice : ObservableObject
    {
        public VirtualControllerType Type { get; init; }

        /// <summary>The tile's AutomationId: the popup button's for Add a
        /// Controller (AddXbox360Btn, AddDS4Btn, AddNintendoBtn, AddRawBtn,
        /// AddKeyboardMouseBtn, AddMidiBtn, AddVrBtn), Home_FirstRun_ and the
        /// type for Home.</summary>
        public string AutomationId { get; init; } = string.Empty;

        /// <summary>The type's name, ControllerType_*.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>The tile's line: what the type is for, or why it cannot be
        /// made now.</summary>
        public string Line { get; init; } = string.Empty;

        /// <summary>The tooltip the Add Controller popup gave the type, word
        /// for word: its name, or why it cannot be made.</summary>
        public string ToolTip { get; init; } = string.Empty;

        public bool IsAvailable { get; init; } = true;

        public System.Windows.Media.Geometry Geometry => SlotTypeIconMap.GeometryFor(Type);

        public string Glyph => SlotTypeIconMap.GlyphFor(Type);

        /// <summary>The AutomationId each type's tile keeps from the popup's
        /// button, which the capture and slot scripts find it by.</summary>
        internal static string AddButtonId(VirtualControllerType type) => type switch
        {
            VirtualControllerType.PlayStation => "AddDS4Btn",
            VirtualControllerType.Nintendo => "AddNintendoBtn",
            VirtualControllerType.Extended => "AddRawBtn",
            VirtualControllerType.KeyboardMouse => "AddKeyboardMouseBtn",
            VirtualControllerType.Midi => "AddMidiBtn",
            VirtualControllerType.Vr => "AddVrBtn",
            _ => "AddXbox360Btn",
        };

        internal static string NameOf(VirtualControllerType type) => type switch
        {
            VirtualControllerType.PlayStation => Strings.Instance.ControllerType_PlayStation,
            VirtualControllerType.Nintendo => Strings.Instance.ControllerType_Nintendo,
            VirtualControllerType.Extended => Strings.Instance.ControllerType_Extended,
            VirtualControllerType.KeyboardMouse => Strings.Instance.ControllerType_KeyboardMouse,
            VirtualControllerType.Midi => Strings.Instance.ControllerType_MIDI,
            VirtualControllerType.Vr => Strings.Instance.ControllerType_VR,
            _ => Strings.Instance.ControllerType_Xbox,
        };

        /// <summary>What the type is for, in one line on its tile.</summary>
        internal static string LineOf(VirtualControllerType type) => type switch
        {
            VirtualControllerType.PlayStation => Strings.Instance.AddController_PlayStation_Line,
            VirtualControllerType.Nintendo => Strings.Instance.AddController_Nintendo_Line,
            VirtualControllerType.Extended => Strings.Instance.AddController_Extended_Line,
            VirtualControllerType.KeyboardMouse => Strings.Instance.AddController_Kbm_Line,
            VirtualControllerType.Midi => Strings.Instance.AddController_Midi_Line,
            VirtualControllerType.Vr => Strings.Instance.AddController_Vr_Line,
            _ => Strings.Instance.AddController_Xbox_Line,
        };

        /// <summary>The most controllers of the type, and the tooltip the
        /// popup gave a type at that number.</summary>
        private static (int Cap, string MaxFormat) CapOf(VirtualControllerType type) => type switch
        {
            VirtualControllerType.PlayStation => (SettingsManager.MaxPlayStationSlots, Strings.Instance.Main_PlayStation_Max_Format),
            VirtualControllerType.Nintendo => (SettingsManager.MaxNintendoSlots, Strings.Instance.Main_Nintendo_Max_Format),
            VirtualControllerType.Extended => (SettingsManager.MaxExtendedSlots, Strings.Instance.Main_Extended_Max_Format),
            VirtualControllerType.KeyboardMouse => (SettingsManager.MaxKeyboardMouseSlots, Strings.Instance.Main_KBM_Max_Format),
            VirtualControllerType.Midi => (SettingsManager.MaxMidiSlots, Strings.Instance.Main_MIDI_Max_Format),
            VirtualControllerType.Vr => (SettingsManager.MaxVrSlots, Strings.Instance.Main_VR_Max_Format),
            _ => (SettingsManager.MaxXbox360Slots, Strings.Instance.Main_Xbox_Max_Format),
        };

        /// <summary>A type for Add a Controller, by the popup's rules: none
        /// past 16 controllers in all or past the type's own cap, no MIDI
        /// without a MIDI API, no VR without SteamVR. The tooltip is the
        /// popup's, and the line says why in plain words.</summary>
        internal static ControllerTypeChoice For(VirtualControllerType type, int ofType, int total,
            bool midiAvailable, bool steamVrAvailable)
        {
            var (cap, maxFormat) = CapOf(type);
            bool full = total >= InputManager.MaxPads;
            bool atCap = ofType >= cap;
            bool midiMissing = type == VirtualControllerType.Midi && !midiAvailable;
            bool vrMissing = type == VirtualControllerType.Vr && !steamVrAvailable;
            string name = NameOf(type);
            return new ControllerTypeChoice
            {
                Type = type,
                AutomationId = AddButtonId(type),
                Name = name,
                ToolTip = midiMissing ? Strings.Instance.Main_MIDI_NotRunning
                    : vrMissing ? Strings.Instance.Main_VR_RequiresSteamVR
                    : atCap ? string.Format(maxFormat, cap)
                    : name,
                Line = midiMissing ? Strings.Instance.AddController_MidiNotRunning
                    : vrMissing ? Strings.Instance.AddController_VrNeedsSteamVr
                    : atCap && type == VirtualControllerType.Vr ? Strings.Instance.AddController_VrOnlyOne
                    : LineOf(type),
                IsAvailable = !(full || atCap || midiMissing || vrMissing),
            };
        }

        /// <summary>Home's three on its first page, in the board's order:
        /// Xbox, PlayStation, Keyboard + Mouse. Each can be made, since no
        /// controller exists yet.</summary>
        internal static IReadOnlyList<ControllerTypeChoice> FirstRunSet()
        {
            ControllerTypeChoice Tile(VirtualControllerType type, string id, string line)
            {
                string name = NameOf(type);
                return new ControllerTypeChoice { Type = type, AutomationId = id, Name = name, Line = line, ToolTip = name };
            }
            return new[]
            {
                Tile(VirtualControllerType.Xbox, "Home_FirstRun_Xbox", Strings.Instance.AddController_Xbox_Line),
                Tile(VirtualControllerType.PlayStation, "Home_FirstRun_PlayStation", Strings.Instance.AddController_PlayStation_Line),
                Tile(VirtualControllerType.KeyboardMouse, "Home_FirstRun_KeyboardMouse", Strings.Instance.Home_FirstRun_Kbm_Line),
            };
        }
    }

    /// <summary>A choice under Driven By in Add a Controller (#574): a device
    /// that can drive the new controller, or Choose Later (Device Empty).
    /// Its values are fixed when it is made.</summary>
    public sealed class DrivenByChoice : ObservableObject
    {
        public Guid Device { get; init; }

        public string Name { get; init; } = string.Empty;

        /// <summary>The words between the name and the battery, with a space
        /// before them when the device has a line at all.</summary>
        public string LineLead { get; init; } = string.Empty;

        /// <summary>The battery level, drawn in the telemetry face.</summary>
        public string LineBattery { get; init; } = string.Empty;

        /// <summary>A picture of that level, drawn before it
        /// (BatteryGlyphs), or nothing.</summary>
        public string BatteryGlyph { get; init; } = string.Empty;

        /// <summary>The space between the picture and the level.</summary>
        public string BatteryGap => BatteryGlyph.Length > 0 ? " " : string.Empty;

        public string LineTail { get; init; } = string.Empty;

        /// <summary>What a screen reader hears: the name, then the line after
        /// a comma when there is one.</summary>
        public string AccessibleName { get; init; } = string.Empty;

        /// <summary>AddController_Device_ and the device's place in the
        /// list, or AddController_ChooseLater.</summary>
        public string AutomationId { get; init; } = string.Empty;

        public bool IsChecked { get; init; }

        /// <summary>A device, checked when it is the first.</summary>
        internal static DrivenByChoice For(Guid device, string name, bool bluetooth, string battery, int index,
            string batteryGlyph = null)
        {
            var (lead, level, tail) = DeviceLine.Parts(bluetooth, battery, Strings.Instance.AddController_DeviceLine_Format, "{0}");
            string line = lead + level + tail;
            bool hasLine = line.Length > 0;
            name ??= string.Empty;
            return new DrivenByChoice
            {
                Device = device,
                Name = name,
                LineLead = hasLine ? " " + lead : string.Empty,
                LineBattery = level,
                BatteryGlyph = level.Length > 0 ? batteryGlyph ?? string.Empty : string.Empty,
                LineTail = tail,
                AccessibleName = hasLine ? name + ", " + line : name,
                AutomationId = "AddController_Device_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsChecked = index == 0,
            };
        }

        /// <summary>The last choice: the controller is made with no
        /// device.</summary>
        internal static DrivenByChoice ChooseLater()
        {
            string name = Strings.Instance.AddController_ChooseLater;
            return new DrivenByChoice
            {
                Device = Guid.Empty,
                Name = name,
                AccessibleName = name,
                AutomationId = "AddController_ChooseLater",
            };
        }
    }

    /// <summary>The words after a controller's name in Add a Controller and on
    /// Home's first page (#574): whether it is on Bluetooth, and its
    /// battery.</summary>
    internal static class DeviceLine
    {
        /// <summary>Lead, battery and tail. Bluetooth with a battery takes
        /// <paramref name="bothFormat"/>, a battery alone
        /// <paramref name="batteryFormat"/>, Bluetooth alone
        /// Pad_Transport_Bluetooth, neither nothing. A format is split at
        /// {0}, the battery's place, and a format without {0} is all lead.
        /// No transport is claimed off Bluetooth, as the device pill and the
        /// dossier claim none (DeviceRowViewModel.IsBluetoothLink).</summary>
        internal static (string Lead, string Battery, string Tail) Parts(bool bluetooth, string battery,
            string bothFormat, string batteryFormat)
        {
            bool hasBattery = !string.IsNullOrEmpty(battery);
            if (!hasBattery)
                return bluetooth ? (Strings.Instance.Pad_Transport_Bluetooth, string.Empty, string.Empty)
                    : (string.Empty, string.Empty, string.Empty);
            string format = (bluetooth ? bothFormat : batteryFormat) ?? string.Empty;
            int at = format.IndexOf("{0}", StringComparison.Ordinal);
            if (at < 0) return (format, string.Empty, string.Empty);
            return (format.Substring(0, at), battery, format.Substring(at + 3));
        }
    }

    /// <summary>What Add a Controller shows (#574), made each time it
    /// opens.</summary>
    public sealed class AddControllerViewModel : ObservableObject
    {
        /// <summary>Driven By: the devices, then Choose Later. Empty when no
        /// device can be offered.</summary>
        public IReadOnlyList<DrivenByChoice> Devices { get; init; } = Array.Empty<DrivenByChoice>();

        public bool ShowsDrivenBy => Devices.Count > 0;

        /// <summary>The device the new controller takes: the checked choice's,
        /// Empty for Choose Later or no device.</summary>
        public Guid SelectedDevice { get; set; }

        /// <summary>Xbox, PlayStation and Nintendo.</summary>
        public IReadOnlyList<ControllerTypeChoice> Common { get; init; } = Array.Empty<ControllerTypeChoice>();

        /// <summary>Keyboard + Mouse, Extended, MIDI and VR.</summary>
        public IReadOnlyList<ControllerTypeChoice> More { get; init; } = Array.Empty<ControllerTypeChoice>();

        /// <summary>Why no tile can be used while every slot is taken, or
        /// empty.</summary>
        public string FullNotice { get; init; } = string.Empty;

        public bool IsFull => FullNotice.Length > 0;

        /// <summary>The dialog for these devices (each already a choice, in
        /// order) and these controllers: <paramref name="countOf"/> gives how
        /// many of a type exist and <paramref name="total"/> how many in
        /// all.</summary>
        internal static AddControllerViewModel Build(IReadOnlyList<DrivenByChoice> devices,
            Func<VirtualControllerType, int> countOf, int total, bool midiAvailable, bool steamVrAvailable)
        {
            var choices = new List<DrivenByChoice>();
            if (devices != null) choices.AddRange(devices);
            Guid selected = choices.Count > 0 ? choices[0].Device : Guid.Empty;
            if (choices.Count > 0) choices.Add(DrivenByChoice.ChooseLater());
            ControllerTypeChoice Tile(VirtualControllerType type)
                => ControllerTypeChoice.For(type, countOf?.Invoke(type) ?? 0, total, midiAvailable, steamVrAvailable);
            return new AddControllerViewModel
            {
                Devices = choices,
                SelectedDevice = selected,
                Common = new[] { Tile(VirtualControllerType.Xbox), Tile(VirtualControllerType.PlayStation), Tile(VirtualControllerType.Nintendo) },
                More = new[]
                {
                    Tile(VirtualControllerType.KeyboardMouse), Tile(VirtualControllerType.Extended),
                    Tile(VirtualControllerType.Midi), Tile(VirtualControllerType.Vr),
                },
                FullNotice = total >= InputManager.MaxPads
                    ? string.Format(Strings.Instance.AddController_Full_Format, InputManager.MaxPads)
                    : string.Empty,
            };
        }
    }
}
