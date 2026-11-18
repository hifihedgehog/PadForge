namespace PadForge.Engine
{
    /// <summary>
    /// Whether a USB VID and PID belong to an SInput pad (#584): the list
    /// SDL_IsJoystickSInputController keeps (SDL fork 51470ad786,
    /// src/joystick/SDL_joystick.c:4469-4485, with the IDs at usb_ids.h:30,
    /// 94 and 313-323). SDL opens these through its HIDAPI SInput driver,
    /// SDL_hidapi_sinput.c, which lights the pad's player LED from the
    /// joystick's player index (:691-709) and sends rumble as the protocol's
    /// type 2 haptics command (:766-788).
    /// </summary>
    public static class SInputControllerIdentity
    {
        /// <summary>Raspberry Pi's VID, under which Hand Held Legend and
        /// others register their SInput pads (usb_ids.h:94).</summary>
        public const ushort RaspberryPiVid = 0x2E8A;

        /// <summary>The generic SInput PID, the one SINPUT-LIB-HID and
        /// HOJA ship by default (SINPUT-LIB-HID sinput_lib_hid.c:16-17).</summary>
        public const ushort GenericPid = 0x10C6;

        /// <summary>AndGamer's VID (usb_ids.h:30).</summary>
        public const ushort AndGamerVid = 0x38D2;

        /// <summary>True for the IDs SDL opens with its SInput driver:
        /// the generic pad 0x10C6, the ProGCC 0x10DF, the GC Ultimate 0x10DD,
        /// Bonziri's FireBird 0x10E0 and Void Gaming's PS4 FireBird 0x10E5
        /// under 2E8A, and Void Gaming's Genesis 0x1014 under 38D2.</summary>
        public static bool IsSInput(int vendorId, int productId) => vendorId switch
        {
            RaspberryPiVid => productId is GenericPid or 0x10DF or 0x10DD or 0x10E0 or 0x10E5,
            AndGamerVid => productId == 0x1014,
            _ => false,
        };
    }
}
