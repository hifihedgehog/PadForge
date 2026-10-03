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

        /// <summary>The raw joystick buttons that carry the remote's own B (the
        /// trigger) and Home in a configuration. A bare remote and one with a
        /// Nunchuk post them on the gamepad positions, B as south (0) and Home
        /// as guide (5). With a Classic Controller the Classic takes those
        /// positions, and the remote's buttons stay in the raw block from
        /// misc1 (15): B 16 and Home 21 (SDL_hidapi_wii.c, the extension
        /// switch after HandleNunchuckButtonData, and k_eWiiButtons).</summary>
        public static (int B, int Home) RemoteButtons(string name)
            => string.Equals(name?.Trim(), "Nintendo Wii Remote with Classic Controller", StringComparison.OrdinalIgnoreCase)
                ? (16, 21)
                : (0, 5);
    }
}
