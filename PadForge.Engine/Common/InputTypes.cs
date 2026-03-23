using System;

namespace PadForge.Engine
{
    // ─────────────────────────────────────────────────────────────────
    //  DeviceObjectTypeFlags
    //  Device object type classification flags. Values match the
    //  original DirectInput constants for mapping compatibility.
    // ─────────────────────────────────────────────────────────────────

    [Flags]
    public enum DeviceObjectTypeFlags : int
    {
        All = 0,
        RelativeAxis = 1,
        AbsoluteAxis = 2,
        Axis = 3,
        PushButton = 4,
        Button = 12,
        PointOfViewController = 16,
        ForceFeedbackActuator = 16777216        // 0x01000000
    }

    // ─────────────────────────────────────────────────────────────────
    //  ObjectAspect
    //  Used when filtering device objects by aspect (position,
    //  velocity, acceleration, force).
    // ─────────────────────────────────────────────────────────────────

    [Flags]
    public enum ObjectAspect : int
    {
        Position = 0x100
    }

    // ─────────────────────────────────────────────────────────────────
    //  ObjectGuid
    //  Static class providing well-known GUIDs for device object types
    //  (axes, buttons, POV controllers). Values match DirectInput
    //  GUID_XAxis, GUID_YAxis, etc.
    // ─────────────────────────────────────────────────────────────────

    public static class ObjectGuid
    {
        /// <summary>GUID_XAxis — {A36D02E0-C9F3-11CF-BFC7-444553540000}</summary>
        public static readonly Guid XAxis = new Guid("A36D02E0-C9F3-11CF-BFC7-444553540000");

        /// <summary>GUID_YAxis — {A36D02E1-C9F3-11CF-BFC7-444553540000}</summary>
        public static readonly Guid YAxis = new Guid("A36D02E1-C9F3-11CF-BFC7-444553540000");

        /// <summary>GUID_ZAxis — {A36D02E2-C9F3-11CF-BFC7-444553540000}</summary>
        public static readonly Guid ZAxis = new Guid("A36D02E2-C9F3-11CF-BFC7-444553540000");

        /// <summary>GUID_RxAxis — {A36D02F4-C9F3-11CF-BFC7-444553540000}</summary>
        public static readonly Guid RxAxis = new Guid("A36D02F4-C9F3-11CF-BFC7-444553540000");

        /// <summary>GUID_RyAxis — {A36D02F5-C9F3-11CF-BFC7-444553540000}</summary>
        public static readonly Guid RyAxis = new Guid("A36D02F5-C9F3-11CF-BFC7-444553540000");

        /// <summary>GUID_RzAxis — {A36D02E3-C9F3-11CF-BFC7-444553540000}</summary>
        public static readonly Guid RzAxis = new Guid("A36D02E3-C9F3-11CF-BFC7-444553540000");

        /// <summary>GUID_Slider — {A36D02E4-C9F3-11CF-BFC7-444553540000}</summary>
        public static readonly Guid Slider = new Guid("A36D02E4-C9F3-11CF-BFC7-444553540000");

        /// <summary>GUID_Button — {A36D02F0-C9F3-11CF-BFC7-444553540000}</summary>
        public static readonly Guid Button = new Guid("A36D02F0-C9F3-11CF-BFC7-444553540000");

        /// <summary>GUID_Key — {55728220-D33C-11CF-BFC7-444553540000}</summary>
        public static readonly Guid Key = new Guid("55728220-D33C-11CF-BFC7-444553540000");

        /// <summary>GUID_POV — {A36D02F2-C9F3-11CF-BFC7-444553540000}</summary>
        public static readonly Guid PovController = new Guid("A36D02F2-C9F3-11CF-BFC7-444553540000");

        /// <summary>GUID_Unknown — {00000000-0000-0000-0000-000000000000}</summary>
        public static readonly Guid Unknown = Guid.Empty;
    }

    // ─────────────────────────────────────────────────────────────────
    //  InputDeviceType
    //  Integer constants matching DirectInput device type values.
    //  Used for UserDevice.CapType to identify device categories.
    // ─────────────────────────────────────────────────────────────────

    public static class InputDeviceType
    {
        public const int Device = 17;
        public const int Mouse = 18;
        public const int Keyboard = 19;
        public const int Joystick = 20;
        public const int Gamepad = 21;
        public const int Driving = 22;
        public const int Flight = 23;
        public const int FirstPerson = 24;
        public const int Supplemental = 25;
        public const int Touchpad = 26;
    }

    // ─────────────────────────────────────────────────────────────────
    //  MapType
    //  Identifies the source type of a mapping: axis, button, slider,
    //  or POV hat switch.
    // ─────────────────────────────────────────────────────────────────

    public enum MapType : int
    {
        None = 0,
        Axis = 1,
        Button = 2,
        Slider = 3,
        POV = 4
    }
}
