using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace PadForge.Engine.Menus
{
    /// <summary>How a keyboard menu lays out its keys (#566). Saved by name,
    /// so the names stay.</summary>
    public enum KeyboardLayout
    {
        /// <summary>One selection moved across the whole keyboard, the layout
        /// Windows' gamepad keyboard and Steam's keyboard use.</summary>
        Grid = 0,
        /// <summary>The keyboard split in halves, a steering input and a type
        /// input for each (Wilson and Agrawala, CHI 2006).</summary>
        Split = 1,
        /// <summary>The daisy wheel: eight groups of four characters
        /// (daisywheeljs).</summary>
        Wheel = 2,
    }

    /// <summary>The press of Opens With that acts (#566), a macro trigger's
    /// three press modes. Saved by name, so the names stay.</summary>
    public enum KeyboardOpenPress
    {
        Press = 0,
        /// <summary>Held for the hold time, Steam's Long_Press.</summary>
        Hold = 1,
        /// <summary>Pressed twice within the window, Steam's Double_Press.</summary>
        DoublePress = 2,
    }

    /// <summary>What one keyboard control does (#566). Saved by name, so the
    /// names stay. The steering inputs are the menu's hosts, not controls:
    /// HostDescriptor steers Grid's selection, Split's left half and the
    /// wheel's group, and Host2Descriptor steers Split's right half.</summary>
    public enum KeyboardControlAction
    {
        TypeKey = 0,
        Backspace = 1,
        Space = 2,
        Enter = 3,
        Close = 4,
        /// <summary>Shift for the next key. Split's left hand.</summary>
        ShiftNextKey = 5,
        /// <summary>Split's right-hand Shift.</summary>
        ShiftRightHand = 6,
        SymbolsPage = 7,
        CursorLeft = 8,
        CursorRight = 9,
        MoveUp = 10,
        MoveDown = 11,
        MoveLeft = 12,
        MoveRight = 13,
        Suggestion1 = 14,
        Suggestion2 = 15,
        Suggestion3 = 16,
        /// <summary>Held, it moves the keyboard with the main steering input.</summary>
        MoveKeyboard = 17,
        TypeLeftHalf = 18,
        TypeRightHalf = 19,
        ArrowUp = 20,
        ArrowDown = 21,
        ArrowLeft = 22,
        ArrowRight = 23,
        PickWest = 24,
        PickNorth = 25,
        PickEast = 26,
        PickSouth = 27,
        /// <summary>Held, the wheel shows capitals and symbols.</summary>
        Capitals = 28,
        /// <summary>Held, the wheel shows numbers and brackets.</summary>
        Numbers = 29,
        Home = 30,
        End = 31,
    }

    /// <summary>The input a keyboard control or Opens With reads (#566): one
    /// input, or two held together. A descriptor is any source descriptor, in
    /// the legacy I/H prefix form for the half of an axis ("HGamepad
    /// RightStickX" for the stick pushed right, "IHGamepad RightStickX" for
    /// left), which SourceCoercion.StripLegacyPrefix splits into the Invert
    /// and HalfAxis flags. A device guid names the controller an input is
    /// read on, and "" reads it on every controller that drives the menu, as
    /// the device-free "Gamepad ..." names are meant to be read.</summary>
    public readonly record struct KeyboardInput(string Descriptor, string DeviceGuid = "",
        string AndDescriptor = "", string AndDeviceGuid = "")
    {
        public static readonly KeyboardInput None = new("");

        public bool IsEmpty => string.IsNullOrWhiteSpace(Descriptor);

        public bool HasAnd => !string.IsNullOrWhiteSpace(AndDescriptor);

        /// <summary>The same inputs on the same devices, as text compares.</summary>
        public bool SameAs(in KeyboardInput o) =>
            Eq(Descriptor, o.Descriptor) && Eq(DeviceGuid, o.DeviceGuid)
            && Eq(AndDescriptor, o.AndDescriptor) && Eq(AndDeviceGuid, o.AndDeviceGuid);

        private static bool Eq(string a, string b) =>
            string.Equals((a ?? "").Trim(), (b ?? "").Trim(), System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One keyboard control of one layout and the input that drives
    /// it (#566). Each layout keeps its own, since the same action has other
    /// defaults in another layout. A control with no entry takes its
    /// layout's default (<see cref="KeyboardMenuDefaults.DefaultFor"/>), so
    /// an untouched keyboard saves none. An empty descriptor means the user
    /// cleared the control.</summary>
    public sealed class KeyboardControlEntry
    {
        [XmlAttribute] public KeyboardLayout Layout { get; set; }

        [XmlAttribute] public KeyboardControlAction Action { get; set; }

        [XmlAttribute] public string Descriptor { get; set; } = "";

        [XmlAttribute] public string DeviceGuid { get; set; } = "";
        public bool ShouldSerializeDeviceGuid() => !string.IsNullOrEmpty(DeviceGuid);

        /// <summary>A second input held with the first, which makes the
        /// control a combination.</summary>
        [XmlAttribute] public string AndDescriptor { get; set; } = "";
        public bool ShouldSerializeAndDescriptor() => !string.IsNullOrEmpty(AndDescriptor);

        [XmlAttribute] public string AndDeviceGuid { get; set; } = "";
        public bool ShouldSerializeAndDeviceGuid() => !string.IsNullOrEmpty(AndDeviceGuid);

        public KeyboardInput Input => new(Descriptor ?? "", DeviceGuid ?? "", AndDescriptor ?? "", AndDeviceGuid ?? "");

        public KeyboardControlEntry Clone() => new()
        {
            Layout = Layout,
            Action = Action,
            Descriptor = Descriptor,
            DeviceGuid = DeviceGuid,
            AndDescriptor = AndDescriptor,
            AndDeviceGuid = AndDeviceGuid,
        };
    }

    /// <summary>The controls each layout uses, in the order the editor lists
    /// them, and their defaults (#566). Defaults follow button position on a
    /// gamepad, through the device-free "Gamepad ..." names, and come from
    /// the keyboards people already use: Windows' gamepad keyboard (X
    /// Backspace, Y Space, Menu Enter), Steam's B to close, the CHI 2006
    /// dual-stick keyboard for Split, and daisywheeljs for the wheel.</summary>
    public static class KeyboardMenuDefaults
    {
        /// <summary>The main steering input's default per layout.</summary>
        public const string Host = "Gamepad LeftStick";

        /// <summary>Split's right half steering input's default.</summary>
        public const string Host2 = "Gamepad RightStick";

        /// <summary>A keyboard's Screen Position from the top, in percent of
        /// the work area: at the bottom, where the Windows touch keyboard and
        /// Steam's keyboard appear.</summary>
        public const int PosYPercent = 100;

        /// <summary>The macro trigger's defaults for a long press's hold time
        /// and a double press's window (MacroItem.TriggerHoldMs and
        /// TriggerDoublePressMs), and the longest either takes.</summary>
        public const int DefaultHoldMs = 500, DefaultDoublePressMs = 442, MaxOpenPressMs = 10000;

        /// <summary>Opens With's hold time or double-press window in a menu:
        /// the saved one, else the press's default. 0 for a plain press.</summary>
        public static int OpenPressMs(MenuDefinitionEntry menu)
        {
            if (menu == null) return 0;
            int ms = Math.Clamp(menu.OpenPressMs, 0, MaxOpenPressMs);
            return menu.OpenPress switch
            {
                KeyboardOpenPress.Hold => ms > 0 ? ms : DefaultHoldMs,
                KeyboardOpenPress.DoublePress => ms > 0 ? ms : DefaultDoublePressMs,
                _ => 0,
            };
        }

        /// <summary>A new keyboard menu with the keyboard's defaults: the left
        /// stick steers, it sits at the bottom of the screen, and every control
        /// takes its layout's default.</summary>
        public static MenuDefinitionEntry Create(int menuId, string name) => new()
        {
            MenuId = menuId,
            Name = name ?? "",
            Kind = MenuKind.Keyboard,
            HostDescriptor = Host,
            PosYPercent = PosYPercent,
        };

        private static readonly (KeyboardControlAction Action, string Descriptor)[] GridControls =
        {
            (KeyboardControlAction.MoveUp, "Gamepad DPadUp"),
            (KeyboardControlAction.MoveDown, "Gamepad DPadDown"),
            (KeyboardControlAction.MoveLeft, "Gamepad DPadLeft"),
            (KeyboardControlAction.MoveRight, "Gamepad DPadRight"),
            (KeyboardControlAction.TypeKey, "Gamepad ButtonA"),
            (KeyboardControlAction.Backspace, "Gamepad ButtonX"),
            (KeyboardControlAction.Space, "Gamepad ButtonY"),
            (KeyboardControlAction.Enter, "Gamepad ButtonStart"),
            (KeyboardControlAction.ShiftNextKey, "Gamepad LeftTrigger"),
            (KeyboardControlAction.SymbolsPage, "Gamepad RightTrigger"),
            (KeyboardControlAction.CursorLeft, "Gamepad LeftShoulder"),
            (KeyboardControlAction.CursorRight, "Gamepad RightShoulder"),
            (KeyboardControlAction.Suggestion1, "IHGamepad RightStickX"),
            (KeyboardControlAction.Suggestion2, "IHGamepad RightStickY"),
            (KeyboardControlAction.Suggestion3, "HGamepad RightStickX"),
            (KeyboardControlAction.Close, "Gamepad ButtonB"),
            (KeyboardControlAction.MoveKeyboard, "Gamepad ButtonBack"),
        };

        private static readonly (KeyboardControlAction Action, string Descriptor)[] SplitControls =
        {
            (KeyboardControlAction.TypeLeftHalf, "Gamepad LeftTrigger"),
            (KeyboardControlAction.TypeRightHalf, "Gamepad RightTrigger"),
            (KeyboardControlAction.Backspace, "Gamepad ButtonX"),
            (KeyboardControlAction.Space, "Gamepad ButtonY"),
            (KeyboardControlAction.ShiftNextKey, "Gamepad LeftStick"),
            (KeyboardControlAction.ShiftRightHand, "Gamepad RightStick"),
            (KeyboardControlAction.SymbolsPage, "Gamepad ButtonBack"),
            (KeyboardControlAction.ArrowUp, "Gamepad DPadUp"),
            (KeyboardControlAction.ArrowDown, "Gamepad DPadDown"),
            (KeyboardControlAction.ArrowLeft, "Gamepad DPadLeft"),
            (KeyboardControlAction.ArrowRight, "Gamepad DPadRight"),
            (KeyboardControlAction.CursorLeft, "Gamepad LeftShoulder"),
            (KeyboardControlAction.CursorRight, "Gamepad RightShoulder"),
            (KeyboardControlAction.Enter, "Gamepad ButtonStart"),
            (KeyboardControlAction.Close, "Gamepad ButtonB"),
            (KeyboardControlAction.MoveKeyboard, "Gamepad ButtonBack"),
        };

        private static readonly (KeyboardControlAction Action, string Descriptor)[] WheelControls =
        {
            (KeyboardControlAction.PickWest, "Gamepad ButtonX"),
            (KeyboardControlAction.PickNorth, "Gamepad ButtonY"),
            (KeyboardControlAction.PickEast, "Gamepad ButtonB"),
            (KeyboardControlAction.PickSouth, "Gamepad ButtonA"),
            (KeyboardControlAction.Backspace, "Gamepad LeftShoulder"),
            (KeyboardControlAction.Space, "Gamepad RightShoulder"),
            (KeyboardControlAction.Capitals, "Gamepad RightTrigger"),
            (KeyboardControlAction.Numbers, "Gamepad LeftTrigger"),
            (KeyboardControlAction.CursorLeft, "Gamepad DPadLeft"),
            (KeyboardControlAction.CursorRight, "Gamepad DPadRight"),
            (KeyboardControlAction.Home, "Gamepad DPadUp"),
            (KeyboardControlAction.End, "Gamepad DPadDown"),
            (KeyboardControlAction.Enter, "Gamepad ButtonStart"),
            (KeyboardControlAction.Suggestion1, "IHGamepad RightStickX"),
            (KeyboardControlAction.Suggestion2, "IHGamepad RightStickY"),
            (KeyboardControlAction.Suggestion3, "HGamepad RightStickX"),
            (KeyboardControlAction.Close, "Gamepad ButtonBack"),
            (KeyboardControlAction.MoveKeyboard, "Gamepad ButtonBack"),
        };

        private static (KeyboardControlAction Action, string Descriptor)[] Table(KeyboardLayout layout) => layout switch
        {
            KeyboardLayout.Split => SplitControls,
            KeyboardLayout.Wheel => WheelControls,
            _ => GridControls,
        };

        /// <summary>The controls a layout uses, in the editor's order.</summary>
        public static IReadOnlyList<KeyboardControlAction> Actions(KeyboardLayout layout)
        {
            var t = Table(layout);
            var list = new List<KeyboardControlAction>(t.Length);
            foreach (var (a, _) in t) list.Add(a);
            return list;
        }

        /// <summary>A control's default input in a layout, or "" when the
        /// layout does not use it.</summary>
        public static string DefaultFor(KeyboardLayout layout, KeyboardControlAction action)
        {
            foreach (var (a, d) in Table(layout))
                if (a == action) return d;
            return "";
        }

        /// <summary>A control's default as an input: the default descriptor,
        /// read on every controller that drives the menu.</summary>
        public static KeyboardInput DefaultInput(KeyboardLayout layout, KeyboardControlAction action)
            => new(DefaultFor(layout, action));

        /// <summary>The input a control has in a menu: its saved entry for the
        /// layout, else the layout's default. Empty when the layout does not
        /// use it or the user cleared it.</summary>
        public static KeyboardInput Effective(MenuDefinitionEntry menu, KeyboardLayout layout, KeyboardControlAction action)
        {
            var list = menu?.KeyboardControls;
            if (list != null)
            {
                // Index walk with a guard, the menu runtime's pattern: the UI
                // thread swaps the list while the poll thread reads it.
                for (int i = 0; i < list.Count; i++)
                {
                    KeyboardControlEntry e;
                    try { e = list[i]; } catch { break; }
                    if (e != null && e.Layout == layout && e.Action == action) return e.Input;
                }
            }
            return DefaultInput(layout, action);
        }

        /// <summary>True when a control has a saved entry, its input set by
        /// the user, cleared included.</summary>
        public static bool IsCustomized(MenuDefinitionEntry menu, KeyboardLayout layout, KeyboardControlAction action)
        {
            var list = menu?.KeyboardControls;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                KeyboardControlEntry e;
                try { e = list[i]; } catch { break; }
                if (e != null && e.Layout == layout && e.Action == action) return true;
            }
            return false;
        }

        /// <summary>Sets a control's input in a menu, or drops the entry when
        /// the input is the layout's default, so the menu keeps only what
        /// differs. The poll thread reads the list while the UI thread edits,
        /// so a new list is built and swapped in, as mapping rows swap their
        /// source lists, and a read never sees one half-edited.</summary>
        public static void Set(MenuDefinitionEntry menu, KeyboardLayout layout, KeyboardControlAction action, KeyboardInput input)
        {
            if (menu == null) return;
            var next = new List<KeyboardControlEntry>();
            if (menu.KeyboardControls != null)
                foreach (var e in menu.KeyboardControls)
                    if (e != null && !(e.Layout == layout && e.Action == action)) next.Add(e);
            if (!input.SameAs(DefaultInput(layout, action)))
                next.Add(new KeyboardControlEntry
                {
                    Layout = layout,
                    Action = action,
                    Descriptor = (input.Descriptor ?? "").Trim(),
                    DeviceGuid = input.IsEmpty ? "" : (input.DeviceGuid ?? "").Trim(),
                    AndDescriptor = input.IsEmpty ? "" : (input.AndDescriptor ?? "").Trim(),
                    AndDeviceGuid = input.IsEmpty || !input.HasAnd ? "" : (input.AndDeviceGuid ?? "").Trim(),
                });
            menu.KeyboardControls = next;
        }

        /// <summary>Opens With as an input.</summary>
        public static KeyboardInput OpenInput(MenuDefinitionEntry menu) => menu == null
            ? KeyboardInput.None
            : new(menu.OpenDescriptor ?? "", menu.OpenDeviceGuid ?? "", menu.OpenAndDescriptor ?? "", menu.OpenAndDeviceGuid ?? "");

        /// <summary>Sets Opens With. Each field is a reference the poll thread
        /// reads on its own, and a combination's two halves change together
        /// only when the descriptor goes last, so the second input and the
        /// devices are written first.</summary>
        public static void SetOpenInput(MenuDefinitionEntry menu, KeyboardInput input)
        {
            if (menu == null) return;
            bool empty = input.IsEmpty;
            menu.OpenDescriptor = "";
            menu.OpenAndDeviceGuid = empty || !input.HasAnd ? "" : (input.AndDeviceGuid ?? "").Trim();
            menu.OpenAndDescriptor = empty ? "" : (input.AndDescriptor ?? "").Trim();
            menu.OpenDeviceGuid = empty ? "" : (input.DeviceGuid ?? "").Trim();
            menu.OpenDescriptor = (input.Descriptor ?? "").Trim();
        }
    }
}
