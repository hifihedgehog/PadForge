using System.Collections.Generic;
using System.Text;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// One line of the translation preview: what a binding (or an aggregate,
    /// like a preset's passthrough recognition) became, or why it didn't.
    /// Reason text is NOT localized here. <see cref="ReasonKey"/> names a
    /// <c>Workshop_Tr_*</c> string resource (added with the Phase C/D resx
    /// pass) and <see cref="ReasonArgs"/> carries its format arguments, so
    /// the UI renders in the active culture while this DTO stays culture-free
    /// and serializable.
    /// </summary>
    public sealed class TranslationEntry
    {
        public TranslationStatus Status { get; set; }

        /// <summary>Localization key, e.g. <c>Workshop_Tr_UnknownKey</c>.</summary>
        public string ReasonKey { get; set; } = "";

        /// <summary>Format arguments for the localized reason string.</summary>
        public List<string> ReasonArgs { get; set; } = new();

        /// <summary>Where in the config the binding lives, e.g.
        /// <c>"Default/left_trackpad/group 1 (dpad)/dpad_north/Full_Press"</c>.</summary>
        public string SourcePath { get; set; } = "";

        /// <summary>The raw <c>binding</c> value string, when the entry is
        /// about a single binding. Empty for aggregates.</summary>
        public string Binding { get; set; } = "";

        /// <summary>Diagnostic trace of what was emitted, e.g.
        /// <c>"KbmKey57 &lt;- Touchpad 0 DPadUp"</c>. Not localized, not shown
        /// as primary UI text.</summary>
        public string Emitted { get; set; } = "";
    }

    /// <summary>
    /// Serializable result summary of one <see cref="ConfigTranslator"/> run.
    /// The Workshop preview UI binds to <see cref="Entries"/>; provenance
    /// (Phase D) stores <see cref="ToSummaryString"/>.
    /// </summary>
    public sealed class TranslationReport
    {
        /// <summary>Bumped when translation output changes shape, so
        /// update detection can flag "translator improved since import".
        /// The value rides provenance inside
        /// <see cref="ToSummaryString"/> ("v2 rows:..."), which the
        /// materializer stamps on SteamWorkshopSource.TranslationSummary.
        /// v2: Wave 1a (#9 follow-up). Adds single_button / gyro_to_mouse
        /// modes, Steam Controller digital-trigger switch members, #token
        /// titles, inner deadzone, set_led macros, Long_Press layer
        /// carries, and the named-skip vocabulary below.
        /// v3: Wave 2A. Long_Press key/button macros (HoldForMs trigger),
        /// xinput hold_repeats turbo, the activator toggle setting
        /// (ToggleVcButton / ToggleKey latches, Toggle-mode layer carries),
        /// camera_reset as a gyro recenter, and mouse_region cursor-clamp
        /// macros with region geometry.
        /// v4: Wave 3. Device-free InputDevice macro triggers replace the
        /// NoDeviceFreeTrigger skips (touchpad / paddle / gyro hosted
        /// macros, mouse_region engage on trackpads), single-pad trackpad
        /// halves (DS4 / DualSense left_/right_trackpad onto pad 0's
        /// region-windowed sources), four_buttons-on-trackpad quadrant
        /// collapse, and per-row touchpad mouse sensitivity replacing the
        /// TouchpadTuningNotPerRow drop.
        /// v5: Wave 4a (#225). The flickstick joystick mode emits a
        /// "Flick Stick Right"/"Flick Stick Left" KbmMouseX source with
        /// the group's "sensitivity" (Steam's Dots Per 360) carried on
        /// ParamFlickCountsPer360; trackpad-hosted flickstick and the
        /// ungrounded flick tuning keys get named entries.
        /// v6: Wave 4b (#9 B-15). Trackpad mouse_region groups emit Clean
        /// absolute "Touchpad {p} Pointer X/Y" rows (region geometry on the
        /// per-source ParamPointerCenter/Extent, sensitivity scales
        /// consumed as extent) instead of the wave-2A clamp macro;
        /// teleport/edge keys get a named entry. The absolute_mouse
        /// AbsoluteMouseApproximated Partial is retired as a false alarm
        /// (the mode is relative on trackpads and gyro; the relative rows
        /// are faithful and Clean).
        /// v7: Wave 4c (#9 B-17). radial_menu / touch_menu groups become
        /// first-class overlay-backed menus: the group emits an engine
        /// MenuDefinitionEntry (kind, host stick/trackpad, layer, Steam's
        /// touchmenu_button_fire_type commit semantics, cell geometry,
        /// labels, overlay position/scale/opacity) and every bound cell's
        /// bindings translate through the normal walk against a
        /// "Menu {id} Item {k}" source the menu runtime fires on
        /// hover-commit. Retires the TouchMenuNeedsOverlay /
        /// RadialMenuNeedsOverlay skips and the two-cell touch-spot
        /// approximation; per-cell icons get a named Partial.</summary>
        public const int CurrentTranslatorVersion = 7;

        public int TranslatorVersion { get; set; } = CurrentTranslatorVersion;

        public string ConfigTitle { get; set; } = "";

        public string ControllerType { get; set; } = "";

        public int SchemaVersion { get; set; }

        public List<TranslationEntry> Entries { get; set; } = new();

        public int XboxRowCount { get; set; }

        public int KbmRowCount { get; set; }

        public int MacroCount { get; set; }

        public int MenuCount { get; set; }

        public int ShiftActivatorCount { get; set; }

        public int CleanCount { get; set; }

        public int PartialCount { get; set; }

        public int SkippedCount { get; set; }

        public int ErrorCount { get; set; }

        public TranslationEntry Add(TranslationStatus status, string reasonKey,
            string sourcePath, string binding = "", string emitted = "", params string[] args)
        {
            var e = new TranslationEntry
            {
                Status = status,
                ReasonKey = reasonKey ?? "",
                SourcePath = sourcePath ?? "",
                Binding = binding ?? "",
                Emitted = emitted ?? "",
            };
            if (args != null && args.Length > 0)
                e.ReasonArgs.AddRange(args);
            Entries.Add(e);
            switch (status)
            {
                case TranslationStatus.Clean: CleanCount++; break;
                case TranslationStatus.Partial: PartialCount++; break;
                case TranslationStatus.Skipped: SkippedCount++; break;
                case TranslationStatus.Error: ErrorCount++; break;
            }
            return e;
        }

        /// <summary>One-line, culture-free digest for the provenance record.</summary>
        public string ToSummaryString()
        {
            var sb = new StringBuilder();
            sb.Append("v").Append(TranslatorVersion)
              .Append(" rows:x").Append(XboxRowCount).Append("+k").Append(KbmRowCount)
              .Append(" macros:").Append(MacroCount)
              .Append(" menus:").Append(MenuCount)
              .Append(" layers:").Append(ShiftActivatorCount)
              .Append(" clean:").Append(CleanCount)
              .Append(" partial:").Append(PartialCount)
              .Append(" skipped:").Append(SkippedCount)
              .Append(" errors:").Append(ErrorCount);
            return sb.ToString();
        }
    }

    /// <summary>
    /// The <c>Workshop_Tr_*</c> reason-key vocabulary. English resx strings
    /// land with the Phase C/D localization pass; the keys are fixed now so
    /// reports serialized before that pass stay renderable.
    /// </summary>
    public static class TranslationReasons
    {
        // Legacy-render-only: no longer emitted (identity bindings now
        // materialize as explicit rows) but kept, with its resx strings,
        // because the vocabulary is fixed and reports serialized by older
        // translator versions must stay renderable.
        public const string DefaultAutomapPassthrough = "Workshop_Tr_DefaultAutomapPassthrough"; // {0} bindings
        public const string RowEmitted = "Workshop_Tr_RowEmitted";
        public const string MacroEmitted = "Workshop_Tr_MacroEmitted";
        public const string ShiftLayerEmitted = "Workshop_Tr_ShiftLayerEmitted";                 // {0} layer name
        public const string TrackpadFeatureRequired = "Workshop_Tr_TrackpadFeatureRequired";     // {0} feature name
        // Legacy-render-only since translator v4: touchpad mouse rows carry
        // the per-row Sensitivity now (#9 B-13 widened the knob to the
        // finger reads), so nothing is dropped. Kept, with its resx
        // strings, for reports serialized by older translator versions.
        public const string TouchpadTuningNotPerRow = "Workshop_Tr_TouchpadTuningNotPerRow";
        public const string SoftPressApproximated = "Workshop_Tr_SoftPressApproximated";
        // Legacy-render-only since translator v6: the v2-v5 Partial was a
        // false alarm. Trackpad/gyro absolute_mouse IS relative cursor
        // movement in Steam (trackball/friction settings vocabulary, the
        // Steam Input API's delta delivery, sc-controller's importer, and
        // Valve's mobile-touch template naming all agree; see the
        // absolute_mouse case in ConfigTranslator), so the relative rows
        // are faithful and Clean. Kept, with its resx strings, for reports
        // serialized by older translator versions.
        public const string AbsoluteMouseApproximated = "Workshop_Tr_AbsoluteMouseApproximated";
        public const string TriggerThresholdApproximated = "Workshop_Tr_TriggerThresholdApproximated";
        public const string MacroTriggerViaXboxOutput = "Workshop_Tr_MacroTriggerViaXboxOutput";
        public const string NoDeviceFreeTrigger = "Workshop_Tr_NoDeviceFreeTrigger";
        public const string GameActionsNotSupported = "Workshop_Tr_GameActionsNotSupported";     // {0} count
        public const string SteamSystemAction = "Workshop_Tr_SteamSystemAction";                 // {0} action
        public const string UnsupportedControllerAction = "Workshop_Tr_UnsupportedControllerAction"; // {0} action
        public const string UnknownBindingType = "Workshop_Tr_UnknownBindingType";               // {0} type
        public const string UnknownKey = "Workshop_Tr_UnknownKey";                               // {0} key
        public const string UnsupportedKey = "Workshop_Tr_UnsupportedKey";                       // {0} key
        public const string UnknownMouseButton = "Workshop_Tr_UnknownMouseButton";               // {0} name
        public const string UnknownXInputButton = "Workshop_Tr_UnknownXInputButton";             // {0} name
        public const string UnknownPhysicalInput = "Workshop_Tr_UnknownPhysicalInput";           // {0} slot {1} input
        public const string UnknownGroupMode = "Workshop_Tr_UnknownGroupMode";                   // {0} mode
        // Legacy-render-only since translator v7 (#9 B-17): menus are
        // first-class now, so nothing needs an overlay it doesn't have.
        // Kept, with their resx strings, for reports serialized by older
        // translator versions.
        public const string TouchMenuNeedsOverlay = "Workshop_Tr_TouchMenuNeedsOverlay";         // {0} cell count
        public const string RadialMenuNeedsOverlay = "Workshop_Tr_RadialMenuNeedsOverlay";       // {0} cell count
        public const string MouseRegionNotSupported = "Workshop_Tr_MouseRegionNotSupported";
        public const string ScrollWheelModeNotSupported = "Workshop_Tr_ScrollWheelModeNotSupported";
        public const string EdgeInputNotSupported = "Workshop_Tr_EdgeInputNotSupported";
        public const string ReleaseActivatorNotSupported = "Workshop_Tr_ReleaseActivatorNotSupported";
        public const string LongPressNotSupported = "Workshop_Tr_LongPressNotSupported";
        public const string DoublePressNotSupported = "Workshop_Tr_DoublePressNotSupported";
        public const string UnknownActivatorType = "Workshop_Tr_UnknownActivatorType";           // {0} type
        public const string RepeatDropped = "Workshop_Tr_RepeatDropped";
        public const string MissingGroup = "Workshop_Tr_MissingGroup";                           // {0} group id
        public const string MissingModeShiftGroup = "Workshop_Tr_MissingModeShiftGroup";         // {0} slot {1} group id
        public const string MissingPreset = "Workshop_Tr_MissingPreset";                         // {0} preset id
        public const string ReferenceCycle = "Workshop_Tr_ReferenceCycle";                       // {0} group id
        public const string RemoveLayerApproximated = "Workshop_Tr_RemoveLayerApproximated";
        public const string ActivatorInputNotSupported = "Workshop_Tr_ActivatorInputNotSupported";
        public const string ClickGateDropped = "Workshop_Tr_ClickGateDropped";
        public const string RowCapExceeded = "Workshop_Tr_RowCapExceeded";                       // {0} slot class
        public const string PresetHasNoActivator = "Workshop_Tr_PresetHasNoActivator";           // {0} preset name
        public const string ShiftLayerEmpty = "Workshop_Tr_ShiftLayerEmpty";                     // {0} layer name
        // Legacy-render-only: no longer emitted (authoritative imported sets
        // stop the automap from asserting, so there is nothing to warn
        // about) but kept, with its resx strings, for old serialized reports.
        public const string AutomapAlsoActive = "Workshop_Tr_AutomapAlsoActive";                 // {0} source {1} target

        // ── Translator v2 (Wave 1a) vocabulary ──
        public const string ScrollGestureModeNotSupported = "Workshop_Tr_ScrollGestureModeNotSupported";
        public const string HapticIntensityDropped = "Workshop_Tr_HapticIntensityDropped";       // {0} count
        public const string ResponseCurveNotSupported = "Workshop_Tr_ResponseCurveNotSupported"; // {0} setting keys
        public const string GyroButtonMaskDropped = "Workshop_Tr_GyroButtonMaskDropped";         // {0} setting key {1} value
        public const string ActivatorDelayDropped = "Workshop_Tr_ActivatorDelayDropped";         // {0} delays
        public const string InterruptibleDropped = "Workshop_Tr_InterruptibleDropped";
        public const string PlayerNumberActionNotSupported = "Workshop_Tr_PlayerNumberActionNotSupported";
        public const string LizardModeActionNotSupported = "Workshop_Tr_LizardModeActionNotSupported";
        public const string SetLedDefaultApproximated = "Workshop_Tr_SetLedDefaultApproximated";

        // ── Translator v3 (Wave 2A) vocabulary ──
        /// <summary>The activator toggle setting became a latch macro
        /// (ToggleVcButton / ToggleKey). Clean for the macro-only structure;
        /// Partial when the momentary identity row is kept alongside the
        /// latch so the macro's trigger stays fed.</summary>
        public const string ToggleLatchEmitted = "Workshop_Tr_ToggleLatchEmitted";                 // {0} target
        /// <summary>toggle=1 on a binding kind with no latch primitive
        /// (mouse buttons, wheel, trigger-axis targets): the binding stays
        /// momentary.</summary>
        public const string ToggleDropped = "Workshop_Tr_ToggleDropped";
        /// <summary>A Long_Press key binding fires one tap at the hold
        /// threshold; Steam holds the key down until release.</summary>
        public const string LongPressKeyTap = "Workshop_Tr_LongPressKeyTap";                       // {0} key
        /// <summary>camera_reset re-levels the camera via calibrated mouse
        /// motion in Steam; PadForge re-references its gyro aim state.</summary>
        public const string CameraResetApproximated = "Workshop_Tr_CameraResetApproximated";
        /// <summary>mouse_region approximated as a centered cursor clamp
        /// engaged while the hosting input is held. Stick/gyro hosts only
        /// since translator v6; trackpad hosts translate to Clean absolute
        /// pointer rows (#9 B-15).</summary>
        public const string MouseRegionApproximated = "Workshop_Tr_MouseRegionApproximated";       // {0} scale {1} x {2} y
        /// <summary>Trackpad mouse_region keys with no pointer-row channel
        /// (teleport start/stop snap, edge-binding radius/invert): the
        /// region itself translates, these shape its engage/release
        /// behavior.</summary>
        public const string MouseRegionTuningDropped = "Workshop_Tr_MouseRegionTuningDropped";     // {0} setting keys

        // ── Translator v4 (Wave 3) vocabulary ──
        /// <summary>four_buttons cells hosted on a touch surface: the
        /// touch-spot grammar has no quadrant zones, so every cell reads
        /// the (region-windowed) contact bool and fires together.</summary>
        public const string TouchQuadrantApproximated = "Workshop_Tr_TouchQuadrantApproximated";
        /// <summary>A group hosted on one half of a single physical pad
        /// whose translated surface has no half window (anchor D-pad
        /// wedges, two-cell touch menus): the rows read the whole pad.</summary>
        public const string TrackpadHalfApproximated = "Workshop_Tr_TrackpadHalfApproximated";

        // ── Translator v7 (Wave 4c, #9 B-17) vocabulary ──
        /// <summary>A radial_menu / touch_menu group became an
        /// overlay-backed menu with working hover-commit.</summary>
        public const string MenuEmitted = "Workshop_Tr_MenuEmitted";                             // {0} bound cell count
        /// <summary>A menu group with no bound cells: nothing to show or
        /// fire (corpus carries several placeholder menus).</summary>
        public const string MenuEmpty = "Workshop_Tr_MenuEmpty";
        /// <summary>A menu group hosted on a surface with no direction /
        /// position read (not a stick or trackpad).</summary>
        public const string MenuSurfaceNotSupported = "Workshop_Tr_MenuSurfaceNotSupported";     // {0} slot
        /// <summary>Cells carrying Steam icon glyphs (ghost_*.png): the
        /// PadForge overlay renders text labels only.</summary>
        public const string MenuIconsDropped = "Workshop_Tr_MenuIconsDropped";                   // {0} cell count
        /// <summary>Menu settings with no PadForge channel (In-Menu
        /// Sensitivity).</summary>
        public const string MenuTuningDropped = "Workshop_Tr_MenuTuningDropped";                 // {0} setting keys

        // ── Translator v5 (Wave 4a) vocabulary ──
        /// <summary>A flickstick group hosted on a touch surface (the
        /// gordon-era corpus binds it to trackpads): PadForge's flick
        /// stick reads a physical stick only, so the mode is skipped and
        /// the member inputs translate on their own.</summary>
        public const string FlickStickSurfaceNotSupported = "Workshop_Tr_FlickStickSurfaceNotSupported";
        /// <summary>Flickstick tuning keys with no grounded PadForge
        /// channel (rotation offset, mouse smoothing, transition time,
        /// edge command radius): the flick itself translates, these
        /// shape it.</summary>
        public const string FlickStickTuningDropped = "Workshop_Tr_FlickStickTuningDropped";       // {0} setting keys
    }
}
