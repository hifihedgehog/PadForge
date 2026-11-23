using System.Collections.Generic;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// <para>The words the translator writes into the names it stores: an
    /// unnamed preset, menu or config, a macro's verb, a mode shift's layer,
    /// an unlabeled menu cell's command.
    /// Each is a format in PadForge's string table, named by its key, the way
    /// the report names its reasons (<see cref="TranslationReasons"/>). The
    /// app passes its lookup in <see cref="TranslationOptions.NameFormat"/>,
    /// so an import under any interface language names things in that
    /// language.</para>
    /// <para><see cref="English"/> holds the base English text of each key,
    /// for a run with no lookup (tests, the corpus sweep). A test holds it
    /// equal to Strings.resx.</para>
    /// </summary>
    public static class TranslationNames
    {
        public const string UnnamedPreset = "Import_UnnamedPreset_Format";
        public const string UnnamedRadialMenu = "Import_UnnamedRadialMenu_Format";
        public const string UnnamedTouchMenu = "Import_UnnamedTouchMenu_Format";
        public const string UntitledConfig = "Workshop_DefaultProfileName";
        public const string ModeShiftLayer = "Workshop_Layer_ModeShift_Format";
        public const string WheelList = "Workshop_Macro_WheelList_Format";
        public const string CursorRegion = "Workshop_Macro_CursorRegion_Format";
        public const string HoldMouse = "Workshop_Macro_HoldMouse_Format";
        public const string ClickMouse = "Workshop_Macro_ClickMouse_Format";
        public const string ToggleMouse = "Workshop_Macro_ToggleMouse_Format";
        public const string HapticPulse = "Workshop_Macro_HapticPulse_Format";
        public const string ReleaseTail = "Workshop_Macro_ReleaseTail_Format";
        public const string Tap = "Workshop_Macro_Tap_Format";
        public const string Autofire = "Workshop_Macro_Autofire_Format";
        public const string Hold = "Workshop_Macro_Hold_Format";
        public const string Toggle = "Workshop_Macro_Toggle_Format";
        public const string Turbo = "Workshop_Macro_Turbo_Format";
        public const string DoublePress = "Workshop_Macro_DoublePress_Format";
        public const string LongPress = "Workshop_Macro_LongPress_Format";
        public const string WheelTick = "Workshop_Macro_WheelTick_Format";
        public const string ToggleWheel = "Workshop_Macro_ToggleWheel_Format";
        public const string TurboWheel = "Workshop_Macro_TurboWheel_Format";
        public const string WarpCursor = "Workshop_Macro_WarpCursor_Format";
        public const string SetLed = "Workshop_Macro_SetLed_Format";
        public const string RecenterGyro = "Workshop_Macro_RecenterGyro_Format";
        public const string NudgeCursor = "Workshop_Macro_NudgeCursor_Format";
        public const string ScreenshotKey = "Workshop_Macro_ScreenshotKey_Format";
        public const string OnScreenKeyboard = "Workshop_Macro_OnScreenKeyboard_Format";

        /// <summary>Each key's base English text, as Strings.resx has it.</summary>
        public static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
        {
            [UnnamedPreset] = "Preset {0}",
            [UnnamedRadialMenu] = "Radial Menu {0}",
            [UnnamedTouchMenu] = "Touch Menu {0}",
            [UntitledConfig] = "Workshop Profile",
            [ModeShiftLayer] = "{0} shift",
            [WheelList] = "Wheel list ({0})",
            [CursorRegion] = "Cursor region ({0})",
            [HoldMouse] = "Hold mouse {0} ({1})",
            [ClickMouse] = "Click mouse {0} ({1})",
            [ToggleMouse] = "Toggle mouse {0} ({1})",
            [HapticPulse] = "Haptic pulse ({0})",
            [ReleaseTail] = "{0} (release tail)",
            [Tap] = "Tap {0} ({1})",
            [Autofire] = "Autofire {0} ({1})",
            [Hold] = "Hold {0} ({1})",
            [Toggle] = "Toggle {0} ({1})",
            [Turbo] = "Turbo {0} ({1})",
            [DoublePress] = "Double press {0} ({1})",
            [LongPress] = "Long press {0} ({1})",
            [WheelTick] = "Wheel tick ({0})",
            [ToggleWheel] = "Toggle wheel ({0})",
            [TurboWheel] = "Turbo wheel ({0})",
            [WarpCursor] = "Warp cursor ({0})",
            [SetLed] = "Set LED ({0})",
            [RecenterGyro] = "Recenter gyro ({0})",
            [NudgeCursor] = "Nudge cursor ({0})",
            [ScreenshotKey] = "Screenshot key ({0})",
            [OnScreenKeyboard] = "On-screen keyboard ({0})",

            // A Steam command's phrase, for a menu cell the author left
            // unlabeled (SteamVocabulary.CommandLabel).
            ["Workshop_Command_SetLed"] = "Set light color",
            ["Workshop_Command_CameraReset"] = "Recenter camera",
            ["Workshop_Command_MousePosition"] = "Warp cursor",
            ["Workshop_Command_MouseDelta"] = "Nudge cursor",
            ["Workshop_Command_Screenshot"] = "Screenshot",
            ["Workshop_Command_ShowKeyboard"] = "On-screen keyboard",
            ["Workshop_Command_ChangePreset"] = "Switch shift layer",
            ["Workshop_Command_AddLayer"] = "Turn on a shift layer",
            ["Workshop_Command_RemoveLayer"] = "Turn off a shift layer",
            ["Workshop_Command_HoldLayer"] = "Hold a shift layer",
            ["Workshop_Command_ChangePlayerNumber"] = "Change player number",
            ["Workshop_Command_Unbound"] = "Unbound",
            ["Workshop_Command_ToggleDesktopMode"] = "Toggle desktop mode",
            ["Workshop_Command_SystemKey"] = "Steam system key",
            ["Workshop_Command_ToggleMagnifier"] = "Toggle magnifier",
            ["Workshop_Command_BrightnessUp"] = "Brightness up",
            ["Workshop_Command_BrightnessDown"] = "Brightness down",
            ["Workshop_Command_PowerOff"] = "Power off controller",
            ["Workshop_Command_OpenConfigurator"] = "Open Steam configurator",
            ["Workshop_Command_OpenQuickMenu"] = "Open Steam quick menu",
            ["Workshop_Command_QuitGame"] = "Quit game",
            ["Workshop_Command_ToggleHaptics"] = "Toggle haptics",
            ["Workshop_Command_ToggleHud"] = "Toggle Steam HUD",
            ["Workshop_Command_ToggleRumble"] = "Toggle rumble",
            ["Workshop_Command_ForceGuide"] = "Force Guide button",
            ["Workshop_Command_GyroCalibrationSpin"] = "Gyro calibration spin",
            ["Workshop_Command_TurnToFace"] = "Turn to face direction",
        };
    }
}
