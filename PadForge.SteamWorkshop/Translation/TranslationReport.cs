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
        /// macros with region geometry.</summary>
        public const int CurrentTranslatorVersion = 3;

        public int TranslatorVersion { get; set; } = CurrentTranslatorVersion;

        public string ConfigTitle { get; set; } = "";

        public string ControllerType { get; set; } = "";

        public int SchemaVersion { get; set; }

        public List<TranslationEntry> Entries { get; set; } = new();

        public int XboxRowCount { get; set; }

        public int KbmRowCount { get; set; }

        public int MacroCount { get; set; }

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
        public const string TouchpadTuningNotPerRow = "Workshop_Tr_TouchpadTuningNotPerRow";
        public const string SoftPressApproximated = "Workshop_Tr_SoftPressApproximated";
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
        /// engaged while the hosting input is held.</summary>
        public const string MouseRegionApproximated = "Workshop_Tr_MouseRegionApproximated";       // {0} scale {1} x {2} y
    }
}
