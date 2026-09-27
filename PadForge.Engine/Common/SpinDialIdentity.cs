using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Which devices carry a spin dial that the SDL fork reports as trackball
    /// motion. The fork's serial driver gives the Logitech WingMan Warrior one
    /// ball and reports the dial on it as horizontal motion, the counts each
    /// packet carries (hifihedgehog/SDL#33 Part 5,
    /// SDL_serial_warrior_proto.c), and no other driver in the fork reports a
    /// ball. A port the serial hint names carries no USB IDs, so the vendor
    /// is 0 and the name the driver gives is the identity. Used by
    /// <see cref="SdlDeviceWrapper"/> for the dial read and by
    /// <c>UserDevice.HasSpinDial</c> for the picker, online or offline.
    /// </summary>
    public static class SpinDialIdentity
    {
        public const string WingManWarrior = "Logitech WingMan Warrior";

        public static bool HasSpinDial(string name) =>
            !string.IsNullOrEmpty(name)
            && string.Equals(name.Trim(), WingManWarrior, StringComparison.OrdinalIgnoreCase);
    }
}
