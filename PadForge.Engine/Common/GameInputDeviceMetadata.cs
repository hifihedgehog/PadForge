using System;
using System.Globalization;
using static SDL3.SDL;

namespace PadForge.Engine
{
    /// <summary>Immutable GameInput details supplied by the SDL backend for one open device.</summary>
    public sealed record GameInputDeviceMetadata(
        string DeviceId,
        string RootId,
        string NativePath,
        string ContainerId,
        string Firmware,
        uint SupportedLayout,
        string RuntimePath,
        string RuntimeVersion)
    {
        private const string Prefix = "SDL.joystick.gameinput.";

        internal static GameInputDeviceMetadata Read(uint properties)
        {
            if (properties == 0) return null;
            string id = SDL_GetStringProperty(properties, Prefix + "device_id", "");
            if (string.IsNullOrEmpty(id)) return null;
            long layout = SDL_GetNumberProperty(properties, Prefix + "supported_layout", 0);
            return new GameInputDeviceMetadata(
                id,
                SDL_GetStringProperty(properties, Prefix + "root_id", ""),
                SDL_GetStringProperty(properties, Prefix + "pnp_path", ""),
                SDL_GetStringProperty(properties, Prefix + "container_id", ""),
                SDL_GetStringProperty(properties, Prefix + "firmware", ""),
                layout is >= 0 and <= uint.MaxValue ? (uint)layout : 0,
                SDL_GetStringProperty(properties, Prefix + "runtime_path", ""),
                SDL_GetStringProperty(properties, Prefix + "runtime_version", ""));
        }

        internal string HardwarePath(string sdlPath) =>
            string.IsNullOrEmpty(NativePath) ? sdlPath : NativePath;

        internal string DiagnosticText =>
            $"GAMEINPUT device={DeviceId} root={RootId} pnp={NativePath} container={ContainerId} " +
            $"firmware={Firmware} layout=0x{SupportedLayout.ToString("X8", CultureInfo.InvariantCulture)} " +
            $"runtime={RuntimeVersion} runtimePath={RuntimePath}";
    }
}
