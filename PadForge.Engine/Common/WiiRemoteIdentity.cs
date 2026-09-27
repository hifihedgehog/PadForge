using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Single source of truth for "does this Wii Remote configuration carry
    /// the IR camera's axes." The SDL fork's Wii driver gives the bare remote,
    /// and the remote with a Nunchuk or a Classic Controller, four IR axes
    /// after the six gamepad axes, and no other configuration
    /// (SDL_hidapi_wii.c, WiiRemoteHasIRCamera). A decoded extension, the
    /// guitar, drum kit, turntable, Taiko drum, the two tablets and the
    /// Shinkansen controller (hifihedgehog/SDL#33 Part 2), spends those axes
    /// on its own controls, and an unknown extension gets none. The names are
    /// the ones the driver gives each configuration
    /// (SDL_hidapi_wii_ext_proto.c, SDL_WiiExt_TypeName). Used by
    /// <see cref="SdlDeviceWrapper"/> for the IR pointer read and by
    /// <c>UserDevice.HasIrCamera</c> for the picker, online or offline.
    /// </summary>
    public static class WiiRemoteIdentity
    {
        public const ushort NintendoVid = 0x057E;

        private static readonly string[] IrCameraConfigurations =
        {
            "Nintendo Wii Remote",
            "Nintendo Wii Remote with Nunchuk",
            "Nintendo Wii Remote with Classic Controller",
        };

        public static bool CarriesIrCamera(ushort vid, string name)
        {
            if (vid != NintendoVid || string.IsNullOrEmpty(name)) return false;
            string trimmed = name.Trim();
            foreach (string configuration in IrCameraConfigurations)
                if (string.Equals(trimmed, configuration, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
