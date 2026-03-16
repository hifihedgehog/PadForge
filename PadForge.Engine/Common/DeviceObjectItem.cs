using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Describes a single input object (axis, button, hat, slider) on a device.
    /// This is the metadata used by the mapping UI to display available inputs
    /// and by the mapping pipeline to resolve input source references.
    /// 
    /// Replaces the former DeviceObjectItem that depended on SharpDX.DirectInput types.
    /// All type information is now expressed through <see cref="PadForge.Engine"/> enums
    /// and well-known GUIDs from <see cref="ObjectGuid"/>.
    /// </summary>
    public class DeviceObjectItem
    {
        // ─────────────────────────────────────────────
        //  Identity
        // ─────────────────────────────────────────────

        /// <summary>
        /// Human-readable name of the object (e.g., "X Axis", "Button 3", "POV").
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The well-known GUID identifying the object type.
        /// See <see cref="ObjectGuid"/> for standard values (XAxis, YAxis, Button, PovController, etc.).
        /// </summary>
        public Guid ObjectTypeGuid { get; set; } = Guid.Empty;

        /// <summary>
        /// Classification flags describing the object's type and capabilities.
        /// For example, <see cref="DeviceObjectTypeFlags.AbsoluteAxis"/> for an analog stick axis,
        /// or <see cref="DeviceObjectTypeFlags.PushButton"/> for a momentary button.
        /// </summary>
        public DeviceObjectTypeFlags ObjectType { get; set; } = DeviceObjectTypeFlags.All;

        // ─────────────────────────────────────────────
        //  Position in the input state
        // ─────────────────────────────────────────────

        /// <summary>
        /// Zero-based index into the corresponding array in <see cref="CustomInputState"/>.
        /// For axes: index into <see cref="CustomInputState.Axis"/>.
        /// For buttons: index into <see cref="CustomInputState.Buttons"/>.
        /// For POVs: index into <see cref="CustomInputState.Povs"/>.
        /// 
        /// This replaces the former DiIndex field.
        /// </summary>
        public int InputIndex { get; set; }

        /// <summary>
        /// Byte offset within the device's data packet. Used for identification and
        /// compatibility with legacy mapping serialization. For SDL devices this is
        /// a synthetic value computed from the object's position.
        /// </summary>
        public int Offset { get; set; }

        // ─────────────────────────────────────────────
        //  Aspect and capabilities
        // ─────────────────────────────────────────────

        /// <summary>
        /// The aspect of the object (Position, Velocity, Acceleration, Force).
        /// Most objects are Position-aspect.
        /// </summary>
        public ObjectAspect Aspect { get; set; } = ObjectAspect.Position;

        /// <summary>
        /// Whether this object is a force-feedback actuator.
        /// </summary>
        public bool IsForceActuator =>
            (ObjectType & DeviceObjectTypeFlags.ForceFeedbackActuator) != 0;

        /// <summary>
        /// Whether this object is an axis (absolute or relative).
        /// </summary>
        public bool IsAxis =>
            (ObjectType & DeviceObjectTypeFlags.Axis) != 0;

        /// <summary>
        /// Whether this object is a button (push or toggle).
        /// </summary>
        public bool IsButton =>
            (ObjectType & DeviceObjectTypeFlags.Button) != 0;

        /// <summary>
        /// Whether this object is a POV hat controller.
        /// </summary>
        public bool IsPov =>
            (ObjectType & DeviceObjectTypeFlags.PointOfViewController) != 0;

        /// <summary>
        /// Whether this object is a slider (identified by ObjectTypeGuid).
        /// </summary>
        public bool IsSlider =>
            ObjectTypeGuid == ObjectGuid.Slider;

        // ─────────────────────────────────────────────
        //  Display
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns a display-friendly string for use in combo boxes and mapping rows.
        /// Format: "Name (Type, Index N)"
        /// </summary>
        public override string ToString()
        {
            string typeLabel;
            if (IsAxis && !IsSlider)
                typeLabel = "Axis";
            else if (IsSlider)
                typeLabel = "Slider";
            else if (IsPov)
                typeLabel = "POV";
            else if (IsButton)
                typeLabel = "Button";
            else
                typeLabel = "Object";

            return $"{Name} ({typeLabel}, Index {InputIndex})";
        }
    }
}
