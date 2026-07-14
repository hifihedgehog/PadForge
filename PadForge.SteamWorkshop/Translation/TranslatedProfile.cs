using System.Collections.Generic;
using PadForge.Engine.Data;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// Neutral translation output. Everything the engine already models is
    /// carried as the real Engine type (the two per-slot
    /// <see cref="MappingSet"/>s, rows, sources, shift activators); only the
    /// App-owned shapes (ProfileData's bag, MacroData/ActionData) stay out.
    /// PadForge.App's <c>WorkshopProfileMaterializer</c> turns this into a
    /// real <c>ProfileData</c>. Split layering because ProfileData lives in
    /// the WPF exe project and this library must not reference it.
    /// </summary>
    public sealed class TranslatedProfile
    {
        /// <summary>Profile display name (config title, override applied).</summary>
        public string Name { get; set; } = "";

        /// <summary>Config description (localized fallback applied).</summary>
        public string Description { get; set; } = "";

        /// <summary>Rows + shift activators for the pre-allocated Xbox VC
        /// slot. Sources use abstract descriptors with empty DeviceGuid.</summary>
        public MappingSet XboxMappingSet { get; set; } = new();

        /// <summary>Rows + shift activators for the pre-allocated
        /// keyboard/mouse VC slot.</summary>
        public MappingSet KbmMappingSet { get; set; } = new();

        /// <summary>Macro-backed bindings (cursor warp, key autofire,
        /// key-on-release). The materializer builds MacroData from these.</summary>
        public List<TranslatedMacro> Macros { get; set; } = new();

        /// <summary>True when the config binds the Xbox side at all: explicit
        /// rows (identity bindings included) or activators, or macros (their
        /// triggers read the Xbox slot's combined output). A pure
        /// keyboard/mouse config leaves this false and the materializer
        /// creates no Xbox slot for it.</summary>
        public bool NeedsXboxSlot { get; set; }

        /// <summary>True when the keyboard/mouse side received rows or
        /// activators.</summary>
        public bool NeedsKbmSlot { get; set; }

        public TranslationReport Report { get; set; } = new();
    }

    /// <summary>What a translated macro does when its trigger fires.</summary>
    public enum TranslatedMacroAction
    {
        /// <summary>Warp the cursor to a fixed screen position
        /// (<c>controller_action MOUSE_POSITION</c>). Coordinates are kept in
        /// Steam's normalized 0..65535 space; the materializer converts to
        /// primary-monitor pixels at import time (the translator has no
        /// screen).</summary>
        MoveMouseToScreenPosition = 0,

        /// <summary>Key autofire while the trigger is held
        /// (<c>key_press</c> with <c>hold_repeats</c>).</summary>
        RepeatKeyWhileHeld = 1,

        /// <summary>One key tap when the trigger releases
        /// (<c>release</c> activator on a key binding).</summary>
        KeyTap = 2,

        /// <summary>Set the pad's LED (<c>controller_action set_led
        /// r g b brightness saturation setting</c>). The materializer maps
        /// setting 1 to a Sticky lightbar-color hold, setting 0 to a
        /// lightbar clear, setting 2 to a clear (approximation, reported
        /// Partial at translate time), and the whole family to a Guide-LED
        /// brightness write for Steam Controller configs. Arg order and
        /// both saturation scales verified against the corpus: 1451857916
        /// (2018, saturation 0-255) and 3353604014 (2024, saturation
        /// 0-100); brightness is 0-100 in both eras. Saturation and
        /// brightness are normalized to percent here so the materializer
        /// never sees the vintage scale.</summary>
        SetLightbarColor = 3,

        /// <summary>Turbo / autofire for a virtual controller button
        /// (<c>xinput_button</c> with <c>hold_repeats</c>, wave 2A).
        /// <see cref="TranslatedMacro.TargetXboxButtons"/> names the pulsed
        /// button, <see cref="TranslatedMacro.IntervalMs"/> the pulse period
        /// (Steam's <c>repeat_rate</c> is a millisecond interval: the shipped
        /// configurator labels it "Repeat Interval" with a #Unit_Milliseconds
        /// suffix in chunk~2dcc5aaf7.js). The materializer lowers this to a
        /// MacroActionType.RepeatVcButtonWhileHeld action with
        /// RepeatMode=UntilRelease so pulsing stops at release.</summary>
        RepeatVcButtonWhileHeld = 4,

        /// <summary>Latch / unlatch a virtual controller button (the
        /// activator-level <c>toggle</c> setting on an xinput binding, wave
        /// 2A). Grounded on Valve's shipped strings: "Toggle will make this
        /// activator continue to be active after releasing it until it is
        /// pressed again." Lowers to MacroActionType.ToggleVcButton.</summary>
        ToggleVcButton = 5,

        /// <summary>Latch / unlatch a keyboard key (the <c>toggle</c>
        /// setting on a key binding, wave 2A). Lowers to
        /// MacroActionType.ToggleKey.</summary>
        ToggleKey = 6,

        /// <summary>Re-reference the slot's gyro-aim state
        /// (<c>controller_action camera_reset</c>, wave 2A). Steam's action
        /// re-levels the in-game camera through calibrated mouse motion
        /// ("Reset the camera to the Horizon ... requires the Dots Per 360°
        /// setting", shipped client strings); PadForge approximates with its
        /// gyro-recenter primitive, reported Partial at translate time.
        /// Lowers to MacroActionType.GyroRecenter.</summary>
        GyroRecenter = 7,

        /// <summary>Press a virtual controller button and hold it until the
        /// physical input releases (a Long_Press xinput binding, wave 2A).
        /// Grounded on Valve's shipped Long Press description: "Once the
        /// long press time has passed, it will activate stay on until you
        /// release it." Rides <see cref="TranslatedMacro.TriggerHoldMs"/>;
        /// the materializer lowers it to a ButtonPress action with
        /// RepeatMode=UntilRelease and RepeatDelayMs=0 (the button is
        /// re-written every frame from the threshold until release).</summary>
        HoldVcButton = 8,

        /// <summary>Confine the desktop cursor to a screen region while the
        /// hosting input is held (a <c>mouse_region</c> group, wave 2A).
        /// Geometry rides <see cref="TranslatedMacro.RegionXPercent"/> /
        /// <see cref="TranslatedMacro.RegionYPercent"/> /
        /// <see cref="TranslatedMacro.RegionScalePercent"/> (Steam stores
        /// position_x/position_y as screen percent and scale as region size;
        /// shipped configurator ids PositionXMouse / PositionYMouse /
        /// ScaleMouseRegion). TriggerMode is the SEMANTIC "WhileHeld"; the
        /// materializer lowers it to an OnPress + OnRelease pair of
        /// MouseLimitRegion clamp toggles because the engine clamp is a
        /// toggle primitive (#110).</summary>
        MouseLimitRegion = 9,
    }

    /// <summary>
    /// A device-independent macro description. Triggers ride the Xbox VC
    /// slot's combined output (MacroTriggerSource.OutputController), which is
    /// the only device-free trigger PadForge's macro engine offers: the
    /// physical input reaches it through the standard automap once the user
    /// assigns a pad to the slot. Bindings whose physical input has no Xbox
    /// output representation (paddles, touchpads, gyro) never produce one of
    /// these; they are reported Skipped instead.
    /// </summary>
    public sealed class TranslatedMacro
    {
        public string Name { get; set; } = "";

        public TranslatedMacroAction Action { get; set; }

        /// <summary>"OnPress" / "WhileHeld" / "OnRelease" / "HoldForMs"
        /// (MacroTriggerMode names). For <see cref="TranslatedMacroAction.MouseLimitRegion"/>
        /// "WhileHeld" is semantic (clamp engaged while the trigger holds);
        /// the materializer lowers it to an OnPress + OnRelease toggle pair.</summary>
        public string TriggerMode { get; set; } = "OnPress";

        /// <summary>Continuous-hold threshold in milliseconds for the
        /// "HoldForMs" trigger mode (a Long_Press activator's
        /// long_press_time; Steam's UI default is 500). 0 = not a hold
        /// trigger.</summary>
        public int TriggerHoldMs { get; set; }

        /// <summary>Xbox output button bitmask (Gamepad.* constants), or 0
        /// when the trigger is an axis (<see cref="TriggerAxisTarget"/>).</summary>
        public ushort TriggerXboxButtons { get; set; }

        /// <summary>"LeftTrigger" / "RightTrigger" when the trigger input is
        /// an analog trigger; null/empty otherwise.</summary>
        public string TriggerAxisTarget { get; set; } = "";

        /// <summary>Axis trigger threshold percent (1..100).</summary>
        public int TriggerAxisThresholdPercent { get; set; } = 50;

        /// <summary>When true the trigger buttons are consumed (removed from
        /// the VC output while the macro fires), reproducing Steam Input's
        /// "this input is keyboard-natured, not pad-natured" behavior.</summary>
        public bool ConsumeTrigger { get; set; }

        /// <summary>MOUSE_POSITION x in Steam's normalized 0..65535 space.</summary>
        public int NormalizedX { get; set; }

        /// <summary>MOUSE_POSITION y in Steam's normalized 0..65535 space.</summary>
        public int NormalizedY { get; set; }

        /// <summary>Win32 virtual-key code for the key actions.</summary>
        public int VirtualKey { get; set; }

        /// <summary>Autofire interval, ms (RepeatKeyWhileHeld and
        /// RepeatVcButtonWhileHeld; Steam's repeat_rate is stored in ms).</summary>
        public int IntervalMs { get; set; } = 100;

        /// <summary>Xbox output button bitmask the VC-button ACTIONS write
        /// (RepeatVcButtonWhileHeld / ToggleVcButton / HoldVcButton).
        /// Distinct from <see cref="TriggerXboxButtons"/>, which is the
        /// physical input's identity on the combined output.</summary>
        public ushort TargetXboxButtons { get; set; }

        // ── MouseLimitRegion payload (mouse_region) ──

        /// <summary>Region center X as percent of the primary screen
        /// (Steam position_x; shipped configurator: "the on screen
        /// horizontal position that the region will be centered around",
        /// #Unit_Percent). Default 50 = centered.</summary>
        public int RegionXPercent { get; set; } = 50;

        /// <summary>Region center Y as percent of the primary screen
        /// (Steam position_y). Default 50 = centered.</summary>
        public int RegionYPercent { get; set; } = 50;

        /// <summary>Region size as percent of the screen per axis (Steam
        /// scale; corpus: 10 = a small corner minimap region, 100 = the
        /// whole screen). Default 100.</summary>
        public int RegionScalePercent { get; set; } = 100;

        // ── SetLightbarColor payload (set_led) ──

        /// <summary>LED red 0..255.</summary>
        public int LedR { get; set; }

        /// <summary>LED green 0..255.</summary>
        public int LedG { get; set; }

        /// <summary>LED blue 0..255.</summary>
        public int LedB { get; set; }

        /// <summary>LED brightness percent 0..100.</summary>
        public int LedBrightnessPercent { get; set; } = 100;

        /// <summary>LED saturation percent 0..100 (already normalized from
        /// the vintage 0..255 scale when needed).</summary>
        public int LedSaturationPercent { get; set; } = 100;

        /// <summary>set_led mode argument: 1 = set the user color,
        /// 0 = restore, 2 = restore-to-default (approximated as restore).</summary>
        public int LedSetting { get; set; } = 1;
    }
}
