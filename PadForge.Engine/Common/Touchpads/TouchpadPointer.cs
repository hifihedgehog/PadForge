namespace PadForge.Engine.Touchpads;

/// <summary>
/// The pointer motion of a precision touchpad PadForge reads alone. Windows
/// moves no pointer with such a touchpad, so its mouse row reads what this
/// works out from the touchpad's own frames: the motion libinput gives a
/// pointer before acceleration, in the counts of a 1000-count-per-inch
/// mouse, and the touchpad's buttons.
/// </summary>
public static class TouchpadPointer
{
    /// <summary>The mouse libinput scales a touchpad's motion to, in counts
    /// per inch (libinput-util.h:60, DEFAULT_MOUSE_DPI), the base speed it
    /// accelerates from (evdev-mt-touchpad.c:2979-2988).</summary>
    public const double CountsPerInch = 1000;

    /// <summary>The size libinput assumes for a touchpad whose descriptor
    /// gives none, in millimeters (evdev-mt-touchpad.c:3591-3592).</summary>
    public const int AssumedWidthMm = 69, AssumedHeightMm = 50;

    /// <summary>The integrated button of a click-pad (Button 1), and the
    /// external primary and secondary buttons (Buttons 2 and 3) of a pad
    /// that has them, in the Microsoft precision touchpad specification's
    /// input report.</summary>
    public const int IntegratedButton = 1, PrimaryButton = 2, SecondaryButton = 4;

    /// <summary>
    /// Units per millimeter along one axis, read as Linux reads an absolute
    /// axis's resolution (hid-input.c hidinput_calc_abs_res): logical
    /// extents over physical extents scaled by the unit exponent, with
    /// centimeters and inches turned into millimeters. Zero when the
    /// descriptor gives no physical size, uses another unit, or a step
    /// overflows, as Linux returns.
    /// </summary>
    /// <param name="unit">The HID unit item: 0x11 centimeters, 0x13 inches.</param>
    /// <param name="unitExponent">The unit exponent as Windows' parser
    /// hands it over, the item's raw value (HIDP_VALUE_CAPS.UnitsExp).</param>
    public static int UnitsPerMm(int logicalMin, int logicalMax, int physicalMin, int physicalMax,
        uint unit, uint unitExponent)
    {
        long logical = (long)logicalMax - logicalMin;
        long physical = (long)physicalMax - physicalMin;
        if (logical <= 0 || physical <= 0 || logical > int.MaxValue || physical > int.MaxValue) return 0;
        int exponent = Exponent(unitExponent);
        if (unit == 0x11)
        {
            // Centimeters to millimeters.
            exponent += 1;
        }
        else if (unit == 0x13)
        {
            // Inches to millimeters.
            physical *= 254;
            if (physical > int.MaxValue) return 0;
            exponent -= 1;
        }
        else return 0;
        for (; exponent < 0; exponent++)
        {
            logical *= 10;
            if (logical > int.MaxValue) return 0;
        }
        for (; exponent > 0; exponent--)
        {
            physical *= 10;
            if (physical > int.MaxValue) return 0;
        }
        // DIV_ROUND_CLOSEST for two positive values.
        return (int)((logical + physical / 2) / physical);
    }

    /// <summary>A unit exponent as Linux decodes the item (hid-core.c:455-465):
    /// a value that fits in four bits is a two's complement nibble, which
    /// many devices send, and a wider one is the signed item value.</summary>
    public static int Exponent(uint raw)
    {
        if ((raw & 0xFFFFFFF0) == 0) return raw >= 8 ? (int)raw - 16 : (int)raw;
        if (raw <= 0xFF) return (sbyte)(byte)raw;
        if (raw <= 0xFFFF) return (short)(ushort)raw;
        return (int)raw;
    }

    /// <summary>Mouse counts per touchpad unit along an axis: libinput's
    /// scale to its 1000-count-per-inch mouse (evdev-mt-touchpad.c:2987-2988).
    /// An axis with no resolution takes the one libinput gives a touchpad of
    /// its assumed size: the axis's range over that size
    /// (evdev-mt-touchpad.c:3608-3609, the range as util-input-event.h:73-76
    /// counts it).</summary>
    public static double CountsPerUnit(int unitsPerMm, int logicalMin, int logicalMax, int assumedMm)
    {
        long range = (long)logicalMax - logicalMin + 1;
        double resolution = unitsPerMm > 0 ? unitsPerMm : Math.Max(1, range / assumedMm);
        return CountsPerInch / 25.4 / resolution;
    }

    /// <summary>
    /// The pointer motion of one touch frame, in touchpad units. One touch
    /// moves the pointer by its own motion. While a click-pad is pressed,
    /// every touch's motion moves it together, so a second finger can drag
    /// what the first one clicked (evdev-mt-touchpad-gestures.c:179-190). A
    /// touch that began in this frame has moved nowhere yet
    /// (evdev-mt-touchpad.c:468-482). Two touches or more on a pad that is
    /// not pressed move no pointer, since libinput reads them as a scroll or
    /// a gesture.
    /// </summary>
    /// <param name="touches">Each touch down in the frame: its motion since
    /// the last frame, and whether it was down in that frame.</param>
    public static (double Dx, double Dy) FrameMotion(ReadOnlySpan<(double Dx, double Dy, bool Moved)> touches, bool pressed)
    {
        double dx = 0, dy = 0;
        if (pressed)
        {
            foreach (var touch in touches)
            {
                if (!touch.Moved) continue;
                dx += touch.Dx;
                dy += touch.Dy;
            }
        }
        else if (touches.Length == 1 && touches[0].Moved)
        {
            dx = touches[0].Dx;
            dy = touches[0].Dy;
        }
        return (dx, dy);
    }

    /// <summary>A touchpad's external buttons as a mouse row's left and
    /// right buttons, bit 0 and bit 1: the external primary one clicks
    /// left and the secondary one right, as the Microsoft specification
    /// names them. The integrated button of a click-pad clicks by where and
    /// with how many fingers it is pressed (<see cref="TouchpadMouse"/>).</summary>
    public static int MouseButtons(int touchpadButtons)
        => ((touchpadButtons & PrimaryButton) != 0 ? 1 : 0)
         | ((touchpadButtons & SecondaryButton) != 0 ? 2 : 0);
}
