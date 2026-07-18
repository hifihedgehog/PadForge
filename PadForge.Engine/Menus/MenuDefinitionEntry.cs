using System.Collections.Generic;
using System.Xml.Serialization;

namespace PadForge.Engine.Menus
{
    /// <summary>Menu layout family. Values mirror Steam Input's two menu
    /// group modes (<c>radial_menu</c> / <c>touch_menu</c>): a radial ring
    /// selected by direction, or a grid selected by touch position.</summary>
    public enum MenuKind
    {
        Radial = 0,
        Grid = 1,
    }

    /// <summary>When a hovered menu item fires. Values are Steam Input's
    /// serialized <c>touchmenu_button_fire_type</c> (shipped configurator:
    /// "Touch Menu Activation Style" / "Radial Menu Button Type" with
    /// options Click / Release / Touch Release / Always; corpus carries
    /// 0, 2 and 3, and the option order in the shipped strings pins the
    /// numbering).</summary>
    public enum MenuFireType
    {
        /// <summary>"Button Click": the hovered item is active while the
        /// hosting surface is clicked (pad click / stick click).</summary>
        Click = 0,

        /// <summary>"Button Release": the hovered item fires once when the
        /// click releases.</summary>
        ClickRelease = 1,

        /// <summary>"Touch Release": the hovered item fires once when the
        /// surface disengages (touch lift, stick back inside the deadzone,
        /// or the hosting mode-shift layer ending). Shipped configurator:
        /// "when the trackpad is no longer touched or when the mode shift
        /// button is released. For joysticks any position outside of the
        /// deadzone is considered touched".</summary>
        TouchRelease = 2,

        /// <summary>"Always": the hovered item is active the whole time it
        /// is hovered ("Send the menu command continuously while the
        /// trackpad or joystick is being touched").</summary>
        Always = 3,
    }

    /// <summary>
    /// One cell of a menu. <see cref="Index"/> is the Steam-compatible slot
    /// index: grid cells are 0-based positions in the grid; radial index 0
    /// is the CENTER button (shipped configurator:
    /// "ControllerBinding_RadialMenuButton0" = "Radial Menu Center Button")
    /// and 1..N are the ring slots clockwise from the top.
    ///
    /// <para>Bindings come in two shapes. Imported Workshop menus leave the
    /// direct-binding fields at 0 and deliver through mapping rows / macros
    /// keyed on the item's fired descriptor ("Menu {id} Item {k}").
    /// Hand-authored items may instead carry ONE direct binding (a virtual
    /// key or a virtual-controller button mask) that the menu runtime fires
    /// itself, so authoring a simple item never requires a hidden row.</para>
    /// </summary>
    public sealed class MenuItemDefinition
    {
        [XmlAttribute] public int Index { get; set; }

        /// <summary>Display label rendered by the overlay (Steam configs
        /// carry it as the binding's comma-separated action label).</summary>
        [XmlAttribute] public string Label { get; set; } = "";

        /// <summary>Direct key binding: Win32 virtual-key code, 0 = none.</summary>
        [XmlAttribute] public int VirtualKey { get; set; }

        /// <summary>Direct virtual-controller binding: Xbox button bitmask
        /// (Gamepad.* constants), 0 = none. Delivered on Xbox and
        /// PlayStation slots (the Sony packer translates the shared mask).</summary>
        [XmlAttribute] public int XboxButtons { get; set; }

        /// <summary>Direct virtual-controller binding for Extended slots:
        /// 1-based raw button number in the slot's custom layout, 0 = none.
        /// Extended output is raw HID (up to 128 buttons), where an Xbox
        /// mask has no meaning, so Extended cells bind by button number the
        /// same way macro custom-button actions do. Schema append-only:
        /// absent in older files = 0.</summary>
        [XmlAttribute] public int ExtendedButton { get; set; }

        /// <summary>Steam Input cell icon name (the binding string's third
        /// comma field, e.g. "ghost_050_menu_0030.png"): a bare PNG file
        /// name in the local Steam client's binding-icon art
        /// (tenfoot\resource\images\library\controller\binding_icons).
        /// The overlay resolves it against the Steam install at display
        /// time and falls back to the text label when Steam or the file
        /// is absent. Never a path: only names passing
        /// <see cref="IsValidIconName"/> are stored. Schema append-only:
        /// absent in older files = empty.</summary>
        [XmlAttribute] public string Icon { get; set; } = "";

        /// <summary>Whether <paramref name="name"/> is a plausible Steam
        /// binding-icon file name: a bare "*.png" with no directory
        /// separators, drive colons, or characters outside the set the
        /// client's own art uses, which is letters, digits, '_', '-',
        /// and '.' (census over the shipped binding_icons directory,
        /// 2026-07-18). Shared
        /// by the Workshop translator's carry gate and the display-time
        /// resolver so both sides agree on what an icon reference is.</summary>
        public static bool IsValidIconName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
            if (!name.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Length <= ".png".Length) return false;
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                if (!ok) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// XML-serializable per-(device) menu definition. Lives on
    /// <see cref="PadForge.Engine.Data.MappingSet.Menus"/> (the slot-scoped
    /// home shared with shift activators), with per-entry device scope via
    /// <see cref="DeviceGuid"/>. An empty <see cref="DeviceGuid"/> means
    /// "any device on the slot", the documented device-free form Workshop
    /// imports use.
    /// </summary>
    public sealed class MenuDefinitionEntry
    {
        [XmlAttribute] public string DeviceGuid { get; set; } = "";

        /// <summary>Stable id unique within the owning PadSetting. Rides
        /// the fired descriptor grammar ("Menu {MenuId} Item {k}").</summary>
        [XmlAttribute] public int MenuId { get; set; } = 1;

        [XmlAttribute] public string Name { get; set; } = "";

        [XmlAttribute] public MenuKind Kind { get; set; } = MenuKind.Radial;

        /// <summary>The input surface that drives the menu: an abstract
        /// stick ("Gamepad LeftStick" / "Gamepad RightStick") or a touchpad
        /// ("Touchpad 0".."Touchpad 2"). Sticks engage on deflection past
        /// <see cref="EngageDeadzonePercent"/>; touchpads engage on touch.</summary>
        [XmlAttribute] public string HostDescriptor { get; set; } = "Gamepad RightStick";

        /// <summary>Horizontal half-window for single-physical-pad hosts
        /// (DS4 / DualSense trackpad halves, #9 B-1): 0 = whole surface,
        /// 1 = left half, 2 = right half. Sticks are always 0.</summary>
        [XmlAttribute] public int HostHalf { get; set; }

        /// <summary>Custom opener: when <see cref="HostDescriptor"/> is
        /// <c>"Custom"</c>, these two RAW axis descriptors ("Axis 3",
        /// "Slider 0", ...) steer the menu instead of a named stick or
        /// touchpad. This is what makes joysticks, wheels, and other
        /// non-gamepad devices first-class openers: "Left/Right Stick" is
        /// a gamepad-only convention that means nothing on them. Engage
        /// follows the same deadzone rule as sticks. Schema append-only:
        /// absent in older files = empty.</summary>
        [XmlAttribute] public string CustomXDescriptor { get; set; } = "";

        [XmlAttribute] public string CustomYDescriptor { get; set; } = "";

        /// <summary>Assignable Click input for the Click / Click Release
        /// fire modes, any button-family descriptor. Empty = the host's
        /// default (stick click for stick hosts, pad click for touchpads,
        /// none for Custom). The default used to be HARD-WIRED to the
        /// under-stick button, which is not a convention non-gamepad
        /// devices share. Schema append-only.</summary>
        [XmlAttribute] public string ClickDescriptor { get; set; } = "";

        /// <summary>Shift layer this menu belongs to. Empty or "Base" =
        /// always available; anything else engages the menu only while
        /// that layer is held (imported mode-shift menus).</summary>
        [XmlAttribute] public string LayerMask { get; set; } = "";

        [XmlAttribute] public MenuFireType FireType { get; set; } = MenuFireType.Click;

        /// <summary>Grid: total cell count (Steam's
        /// <c>touch_menu_button_count</c>; absent in the config = the
        /// command count). Radial: ring slot count (center excluded).</summary>
        [XmlAttribute] public int CellCount { get; set; } = 4;

        /// <summary>Radial only: item index 0 exists as the center cell,
        /// selected while the surface rests inside the deadzone.</summary>
        [XmlAttribute] public bool HasCenter { get; set; }

        /// <summary>Overlay: render item labels (Steam
        /// <c>touch_menu_show_labels</c>; absent = on).</summary>
        [XmlAttribute] public bool ShowLabels { get; set; } = true;

        /// <summary>Overlay center position as percent of the work area
        /// (Steam <c>touch_menu_position_x</c>/<c>_y</c>; 50/50 = centered).</summary>
        [XmlAttribute] public int PosXPercent { get; set; } = 50;

        [XmlAttribute] public int PosYPercent { get; set; } = 50;

        /// <summary>Overlay size percent (Steam <c>touch_menu_scale</c>).</summary>
        [XmlAttribute] public int ScalePercent { get; set; } = 100;

        /// <summary>Overlay opacity percent (Steam <c>touch_menu_opacity</c>).</summary>
        [XmlAttribute] public int OpacityPercent { get; set; } = 90;

        /// <summary>Stick engage / radial center deadzone as percent of
        /// full deflection. Grounded defaults: Steam's shipped strings say
        /// "any position outside of the deadzone is considered touched"
        /// without a serialized per-menu knob in the corpus; sc-controller
        /// engages its stick menus at 1/3 deflection and cancels inside
        /// 1/8 (scc/special_actions.py MIN_STICK_DISTANCE, scc/osd/menu.py
        /// _control_equals_cancel). 25% sits between the two and matches
        /// Steam's default stick deadzone neighborhood; imported groups
        /// carrying deadzone_inner_radius override it.</summary>
        [XmlAttribute] public int EngageDeadzonePercent { get; set; } = 25;

        /// <summary>In-Menu Sensitivity as percent (Steam's touch_menu
        /// <c>sensitivity</c> key, translator v26): scales the hover
        /// vector before selection, so a higher value reaches the ring /
        /// crosses the engage deadzone with less physical deflection and
        /// a lower value demands more. 100 = the identity. Schema
        /// append-only: absent in older files = 100.</summary>
        [XmlAttribute] public int SensitivityPercent { get; set; } = 100;

        [XmlAttribute] public bool Enabled { get; set; } = true;

        [XmlElement("Item")] public List<MenuItemDefinition> Items { get; set; } = new();

        /// <summary>Deep copy. Every clone site (profile apply, slot copy,
        /// editor round-trips) must use this so item lists never alias.</summary>
        public MenuDefinitionEntry Clone()
        {
            var copy = new MenuDefinitionEntry
            {
                DeviceGuid = DeviceGuid,
                MenuId = MenuId,
                Name = Name,
                Kind = Kind,
                HostDescriptor = HostDescriptor,
                HostHalf = HostHalf,
                CustomXDescriptor = CustomXDescriptor,
                CustomYDescriptor = CustomYDescriptor,
                ClickDescriptor = ClickDescriptor,
                LayerMask = LayerMask,
                FireType = FireType,
                CellCount = CellCount,
                HasCenter = HasCenter,
                ShowLabels = ShowLabels,
                PosXPercent = PosXPercent,
                PosYPercent = PosYPercent,
                ScalePercent = ScalePercent,
                OpacityPercent = OpacityPercent,
                EngageDeadzonePercent = EngageDeadzonePercent,
                Enabled = Enabled,
            };
            if (Items != null)
            {
                foreach (var it in Items)
                {
                    if (it == null) continue;
                    copy.Items.Add(new MenuItemDefinition
                    {
                        Index = it.Index,
                        Label = it.Label,
                        VirtualKey = it.VirtualKey,
                        XboxButtons = it.XboxButtons,
                        ExtendedButton = it.ExtendedButton,
                        Icon = it.Icon,
                    });
                }
            }
            return copy;
        }
    }
}
