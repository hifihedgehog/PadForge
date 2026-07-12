using System;
using System.Globalization;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Home button LED brightness for the 2015 Steam Controller
    /// (discussion #209), via SDL's own lane: the
    /// SDL_JOYSTICK_HIDAPI_STEAM_HOME_LED hint (SDL_hints.h). SDL's Steam
    /// HIDAPI driver registers a callback for the hint at joystick open
    /// (SDL_hidapi_steam.c HIDAPI_DriverSteam_OpenJoystick), and
    /// SDL_AddHintCallback fires it immediately with the current value
    /// (SDL_hints.c SDL_AddHintCallback), so a controller connected after
    /// the hint is set still picks the brightness up. On change the
    /// callback writes SETTING_LED_USER_BRIGHTNESS through a feature
    /// report (SetHomeLED / controller_constants.h).
    ///
    /// The hint is GLOBAL to every Steam Controller in the process. Two
    /// 2015 Steam Controllers cannot hold different brightness values,
    /// and the last write wins. Acceptable for this device population.
    ///
    /// The value is always written in SDL's float 0..1 form ("0.50"),
    /// never as a bare integer string: SDL_HomeLEDHintChanged parses a
    /// dotted string as a 0..1 fraction but treats a dotless string as a
    /// boolean, where "50" would read as true and jump to 100.
    /// </summary>
    internal static class SteamHomeLedSetter
    {
        /// <summary>SDL_HINT_JOYSTICK_HIDAPI_STEAM_HOME_LED
        /// (SDL_hints.h).</summary>
        private const string HintName = "SDL_JOYSTICK_HIDAPI_STEAM_HOME_LED";

        /// <summary>The 2015 Steam Controller family under Valve's VID
        /// 0x28DE, per SDL controller_list.h k_eControllerType_SteamController
        /// entries: 0x1101 legacy (CHELL), 0x1102 wired (D0G), 0x1105 and
        /// 0x1106 Bluetooth (D0G), 0x1142 wireless dongle. The Steam Deck
        /// built-in controller is excluded: SDL_hidapi_steamdeck.c has no
        /// home-LED hint. The Steam Controller 2026 rides a different
        /// driver and is out of scope here.</summary>
        internal static bool IsSteamController2015(ushort vendorId, ushort productId)
            => vendorId == 0x28DE
            && (productId == 0x1101 || productId == 0x1102
                || productId == 0x1105 || productId == 0x1106
                || productId == 0x1142);

        /// <summary>Formats a 0-100 percent as SDL's dotted 0..1 hint
        /// string, invariant culture, so SDL_HomeLEDHintChanged's
        /// float path (value contains '.') always parses it.</summary>
        internal static string FormatHintValue(int percent)
            => (Math.Clamp(percent, 0, 100) / 100.0)
                .ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>Sets the process-global home-LED hint. SDL only
        /// refires callbacks when the value actually changes, so repeated
        /// identical writes are free. SDL_SetHint is thread-safe. Never
        /// throws.</summary>
        public static void TrySet(int percent)
        {
            string val = FormatHintValue(percent);
            try
            {
                bool ok = SDL3.SDL.SDL_SetHint(HintName, val);
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"GUIDELED steam sethint val=\"{val}\" ret={ok}");
            }
            catch (Exception ex)
            {
                // SDL not loaded yet. The next apply pass retries.
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"GUIDELED steam sethint val=\"{val}\" EXCEPTION {ex.GetType().Name}");
            }
        }
    }
}
