using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Engine.Data;
using PadForge.SteamWorkshop.Model;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// Turns a parsed <see cref="SteamInputConfig"/> into a
    /// <see cref="TranslatedProfile"/>: mapping rows for one pre-allocated
    /// Xbox VC slot plus one keyboard/mouse VC slot (split configs are the
    /// norm), shift layers for extra presets / action layers / mode shifts,
    /// device-free macros for the cursor-warp and key-autofire bindings, and
    /// a per-binding <see cref="TranslationReport"/>.
    ///
    /// <para>Determinism: the same config + options produce an identical
    /// TranslatedProfile. Presets iterate by ascending id, a preset's groups
    /// by (slot token, group id), a group's inputs by name; rows are sorted
    /// (layer, target) at the end; layer names derive from
    /// (FileId, preset id) and mode-shift names add (slot, group id).</para>
    ///
    /// <para>Sources are emitted with an EMPTY DeviceGuid, the documented
    /// "first device on the slot" form, so no device snapshot is baked at
    /// import time. Everything Steam would output gets an explicit row:
    /// automap-identical bindings, and the matched-side implicit analog
    /// outputs of trigger and joystick_move groups (gated on the Xbox side
    /// being in play through bindings). Imported sets are authoritative
    /// (<see cref="MappingSet.Authoritative"/>), so the legacy automap
    /// never adds to them and nothing may stay implicit. Bindings whose
    /// target already has a row join it as extra sources instead of
    /// creating a duplicate.</para>
    /// </summary>
    public sealed class ConfigTranslator
    {
        private const int MaxRowsPerSlot = 5000;
        private const int MaxReferenceDepth = 4;

        // Sensitivity baselines: the Steam Input slider value that maps to
        // PadForge's 1.0x. Ground truth from the corpus: joystick_mouse
        // defaults to 80, the trackpad mouse modes to 50 (HoMM3 Deckified
        // carries 80/25/50 which the recipe pins as 1.0/0.3/1.0).
        private const double StickMouseBaseline = 80.0;
        private const double TrackpadMouseBaseline = 50.0;
        private const double GenericBaseline = 100.0;

        public TranslatedProfile Translate(SteamInputConfig config, TranslationOptions options)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            options ??= new TranslationOptions();

            var run = new Run(config, options);

            foreach (var preset in SelectPresets(config, options))
                TranslatePreset(run, preset);

            return Finalize(run);
        }

        // ─────────────────────────────────────────────
        //  Per-run state
        // ─────────────────────────────────────────────

        private sealed class Run
        {
            public readonly SteamInputConfig Config;
            public readonly TranslationOptions Options;
            public readonly TranslatedProfile Profile = new();
            public readonly TranslationReport Report;
            public readonly Dictionary<int, SteamInputGroup> GroupsById = new();

            // (isKbm, layer, target) -> accumulating row.
            public readonly Dictionary<(bool Kbm, string Layer, string Target), PendingRow> Rows = new();
            public readonly List<(bool Kbm, string Layer, string Target)> RowOrder = new();

            // Automap-identical xinput bindings, held back until the run
            // ends: targets that gained a real row re-absorb them as
            // sources; the rest become explicit identity rows of their own.
            public readonly List<PendingIdentity> Identities = new();

            // Matched-side implicit analog outputs (a trigger group's pull,
            // a joystick_move group's axis pair), held back until the run
            // ends. Steam emits these without any binding object, so the
            // authoritative set must spell them out too. They materialize
            // only when the Xbox side is otherwise in play: a pure
            // keyboard/mouse config must not sprout an Xbox slot for a
            // stick nobody consumes.
            public readonly List<(string Layer, string Target, string Descriptor, string Path, int DeadZonePct)>
                MatchedAnalogs = new();
            private readonly HashSet<string> _matchedAnalogSeen = new(StringComparer.Ordinal);

            public void AddMatchedAnalog(string layer, string target, string descriptor, string path,
                int deadZonePct = 0)
            {
                if (_matchedAnalogSeen.Add($"{layer}|{target}|{descriptor}"))
                    MatchedAnalogs.Add((layer, target, descriptor, path, deadZonePct));
            }

            public readonly List<ActivatorRequest> Activators = new();

            public int? BasePresetId;

            /// <summary>Next menu id (#9 B-17). 1-based, assigned in walk
            /// order (deterministic: presets ascend by id, groups by
            /// (slot token, group id)); rides the "Menu {id} Item {k}"
            /// source descriptors and the emitted MenuDefinitionEntry.</summary>
            public int NextMenuId = 1;

            public readonly Dictionary<int, string> LayerByPreset = new();
            public readonly Dictionary<int, int> GameActionsByPreset = new();
            public readonly Dictionary<int, string> PresetNames = new();
            public bool XboxRowCapHit;
            public bool KbmRowCapHit;

            /// <summary>Count of haptic_intensity / haptic_intensity_override
            /// occurrences (value != 0) on translated activators and groups.
            /// Reported once per config in Finalize; per-binding entries
            /// would drown the report (49 in one corpus fixture).</summary>
            public int HapticDropCount;

            /// <summary>Switch-family config: diamond members are named by
            /// Nintendo label and fold onto positions during resolution.</summary>
            public readonly bool NintendoLabels;

            /// <summary>Single-touchpad config (DS4 / DualSense, #9 B-1):
            /// trackpad tokens address halves of physical pad 0 instead of
            /// three separate pads.</summary>
            public readonly bool SinglePadTrackpads;

            public Run(SteamInputConfig config, TranslationOptions options)
            {
                Config = config;
                Options = options;
                Report = Profile.Report;
                Report.SchemaVersion = config.Version;
                Report.ControllerType = config.ControllerType ?? "";
                NintendoLabels = PhysicalSlotResolver.UsesNintendoLabels(config.ControllerType);
                SinglePadTrackpads = PhysicalSlotResolver.UsesSinglePadTrackpads(config.ControllerType);
                foreach (var g in config.Groups)
                    if (!GroupsById.ContainsKey(g.Id))
                        GroupsById[g.Id] = g;
            }
        }

        private sealed class PendingRow
        {
            public bool IsAxis;
            // Set by the Finalize matched-analog pass. Keeps the row on the
            // axis default combine (max-abs) instead of Sum: summing a click
            // identity's upper-half leg onto the full pull would overdrive
            // the top half of an otherwise clean analog pull.
            public bool HasMatchedPassthrough;
            public readonly List<MappingSource> Sources = new();
            // Click gate for a single trackpad-dpad feed. Dropped (with a
            // Partial entry) if any other source joins the same target.
            public MappingSource ClickGate;
            public string ClickGatePath;
        }

        private sealed class PendingIdentity
        {
            public string Layer;
            public string Target;
            public ResolvedSource Source;
            public string Path;
            public string Binding;
            // Trigger identities materialize as axis rows (Sum combine when
            // another source joins), same as EmitSourceRow would build them.
            public bool IsAxis;
        }

        private sealed class ActivatorRequest
        {
            public string LayerMask;
            public string LayerName;
            public string Mode = "Hold";
            public string JumpToLayer = "";
            public bool InheritUnmapped;
            public string Descriptor = "";
            public string Kind = "Button";
            public double AxisThreshold = 0.5;
            public string Path = "";
            /// <summary>Hold-before-engage debounce, ms (ShiftActivator.DelayMs).
            /// Long_Press layer carries set it to the activator's
            /// long_press_time; 0 = instant.</summary>
            public int DelayMs;
            // Cycle mode (same-input preset jumps merged by
            // MergeSameInputJumpsIntoCycles).
            public string CycleLayers = "";
            public bool CycleIncludeBase;
        }

        // ─────────────────────────────────────────────
        //  Preset walk
        // ─────────────────────────────────────────────

        private static IEnumerable<SteamInputPreset> SelectPresets(
            SteamInputConfig config, TranslationOptions options)
        {
            return config.Presets
                .Where(p => options.IncludedPresetIds == null || options.IncludedPresetIds.Contains(p.Id))
                .OrderBy(p => p.Id);
        }

        private void TranslatePreset(Run run, SteamInputPreset preset)
        {
            run.BasePresetId ??= preset.Id;
            bool isBase = preset.Id == run.BasePresetId.Value;
            string layer = isBase ? "Base" : $"Layer_{run.Options.FileId}_{preset.Id}";
            run.LayerByPreset[preset.Id] = layer;
            run.PresetNames[preset.Id] = string.IsNullOrWhiteSpace(preset.Name)
                ? $"Preset {preset.Id}" : preset.Name;

            // Deterministic slot walk: (slot token, group id).
            var entries = preset.GroupSourceBindings
                .Select(kv => (GroupId: kv.Key, Value: kv.Value ?? ""))
                .Select(e =>
                {
                    var tokens = e.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string slotToken = tokens.Length > 0 ? tokens[0] : "";
                    bool active = tokens.Length > 1 && tokens[1].Equals("active", StringComparison.OrdinalIgnoreCase);
                    bool modeshift = tokens.Any(t => t.Equals("modeshift", StringComparison.OrdinalIgnoreCase));
                    return (e.GroupId, SlotToken: slotToken, Active: active, Modeshift: modeshift);
                })
                .OrderBy(e => e.SlotToken, StringComparer.Ordinal)
                .ThenBy(e => e.GroupId)
                .ToList();

            foreach (var entry in entries)
            {
                if (!entry.Active) continue; // inactive groups (incl. inactive modeshift) are dormant alternates

                var slot = PhysicalSlotResolver.ParseSlot(entry.SlotToken);
                string presetPath = run.PresetNames[preset.Id];
                string groupPath = $"{presetPath}/{entry.SlotToken}/group {entry.GroupId}";

                if (!run.GroupsById.TryGetValue(entry.GroupId, out var group))
                {
                    run.Report.Add(TranslationStatus.Error, TranslationReasons.MissingGroup,
                        groupPath, args: entry.GroupId.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                // "active modeshift" groups only fire while a mode_shift
                // binding holds their layer; route their rows there.
                string groupLayer = entry.Modeshift
                    ? ModeShiftLayer(run, preset.Id, entry.SlotToken, entry.GroupId)
                    : layer;

                TranslateGroup(run, preset, group, slot, entry.SlotToken, groupLayer, groupPath);
            }

            // Aggregate skips for this preset.
            if (run.GameActionsByPreset.TryGetValue(preset.Id, out int gameActions) && gameActions > 0)
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.GameActionsNotSupported,
                    run.PresetNames[preset.Id],
                    args: gameActions.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string ModeShiftLayer(Run run, int presetId, string slotToken, int groupId)
            => $"Layer_{run.Options.FileId}_{presetId}_MS_{slotToken}_{groupId}";

        // ─────────────────────────────────────────────
        //  Group translation
        // ─────────────────────────────────────────────

        private void TranslateGroup(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string slotToken, string layer, string path)
        {
            // reference groups inline another group's mode/inputs (cycle-safe).
            var visited = new HashSet<int> { group.Id };
            var effective = group;
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MergeSettings(settings, group.Settings);
            int depth = 0;
            while (string.Equals(effective.Mode, "reference", StringComparison.OrdinalIgnoreCase))
            {
                if (++depth > MaxReferenceDepth
                    || effective.ReferencedGroupId == null
                    || !run.GroupsById.TryGetValue(effective.ReferencedGroupId.Value, out var referenced)
                    || !visited.Add(referenced.Id))
                {
                    run.Report.Add(TranslationStatus.Error, TranslationReasons.ReferenceCycle,
                        path, args: group.Id.ToString(CultureInfo.InvariantCulture));
                    return;
                }
                effective = referenced;
                // Referenced settings are the base; the referring group's
                // (already merged) values win.
                var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                MergeSettings(merged, effective.Settings);
                MergeSettings(merged, settings);
                settings = merged;
            }

            string mode = (effective.Mode ?? "").Trim().ToLowerInvariant();
            path = $"{path} ({mode})";

            // Group settings PadForge has no channel for get named notes
            // instead of silence, but only on modes that otherwise
            // translate (a wholly-skipped group's own entry covers it).
            if (ProductiveModes.Contains(mode))
                ReportDroppedGroupSettings(run, settings, path,
                    // Trackpad mouse_region consumes its per-axis
                    // sensitivity scales as region extent since v6
                    // (#9 B-15), so the curve-drop note must not name
                    // them there; every other host still drops them.
                    skipRegionScales: mode == "mouse_region"
                        && PhysicalSlotResolver.IsTrackpad(slot),
                    reportMouseTuning: MouseTuningModes.Contains(mode));

            switch (mode)
            {
                case "four_buttons":
                case "switches":
                case "dpad":
                // single_button: the whole pad / stick face acts as one
                // button. Its click and touch members resolve like any
                // other member ("Touchpad {p} Click" / "Finger 0 Down").
                case "single_button":
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "trigger":
                    TranslateTriggerGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "joystick_move":
                    TranslateJoystickMove(run, preset, effective, slot, layer, path, settings);
                    break;

                case "joystick_mouse":
                case "mouse_joystick":
                case "joystick_camera":
                    EmitMouseAxes(run, slot, layer, path, settings, StickMouseBaseline);
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "gyro_to_mouse":
                    // The post-2022 gyro mode: same output surface as the
                    // joystick_mouse-hosted gyro path (Gyro Yaw/Pitch into
                    // the KbM mouse delta) with its own sensitivity key.
                    // gyro_natural_sensitivity stores percent of natural
                    // 1:1 aim (100 = 1.0x; corpus 3737909570 carries 75,
                    // and Valve's shipped gyro templates carry no settings
                    // at all, so absent = 1.0x).
                    EmitMouseAxes(run, slot, layer, path, settings, GenericBaseline,
                        sensitivityKey: "gyro_natural_sensitivity");
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "flickstick":
                    // Jibb Smart's flick stick (#225). Token spelling
                    // grounded on Valve's shipped templates
                    // (controller_ps4/ps5/switch_pro_gamepad_flickstick.vdf,
                    // group "mode" "flickstick") and the wild corpus
                    // (fixture 2374887917 plus DOOM Eternal configs
                    // 2779652507 / 2228940979). Members (click, and edge on
                    // sticks) translate through the standard walk, like the
                    // other joystick modes.
                    EmitFlickStick(run, slot, layer, path, settings);
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "absolute_mouse":
                case "relative_mouse":
                    // absolute_mouse is Steam's "As Mouse" style and moves
                    // the cursor RELATIVELY (v6 verdict, retiring the v2-v5
                    // AbsoluteMouseApproximated Partial as a false alarm).
                    // "Absolute" names the pad's input reading, not the
                    // output: the mode's own settings are trackball /
                    // friction / smoothing ("Trackball mode makes the pad
                    // act like a trackball instead of a mouse", shipped
                    // configurator ControllerBinding_Trackball_Description),
                    // the Steam Input API delivers absolute_mouse analog
                    // actions as deltas, sc-controller's proven VDF importer
                    // lowers the mode to a plain relative MouseAction
                    // (scc/foreign/vdf.py, mode == "absolute_mouse"), and
                    // Valve's own template pair names the TOUCHSCREEN
                    // absolute_mouse "Mouse point and click" vs
                    // relative_mouse "Mouse trackpad"
                    // (controller_mobile_touch_*.vdf). The 1:1 pad-to-screen
                    // construct on trackpads is mouse_region, handled below.
                    // So the relative rows ARE the faithful translation:
                    // Clean.
                    EmitMouseAxes(run, slot, layer, path, settings, TrackpadMouseBaseline);
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "scrollwheel":
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.ScrollWheelModeNotSupported, path);
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings,
                        onlyInputs: new[] { "click" });
                    break;

                case "touch_menu":
                    TranslateMenuGroup(run, preset, effective, slot, layer, path, settings,
                        radial: false);
                    break;

                case "radial_menu":
                    TranslateMenuGroup(run, preset, effective, slot, layer, path, settings,
                        radial: true);
                    break;

                case "mouse_region":
                    TranslateMouseRegion(run, slot, layer, path, settings);
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings,
                        onlyInputs: new[] { "click", "touch", "edge" });
                    break;

                case "2dscroll":
                    // Directional swipe. PadForge has no swipe-step gesture
                    // channel; named skip instead of the generic
                    // unknown-mode line (most-seen named gap in the corpus
                    // discovery run).
                    run.Report.Add(TranslationStatus.Skipped,
                        TranslationReasons.ScrollGestureModeNotSupported, path);
                    break;

                case "disabled":
                case "":
                    break;

                default:
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownGroupMode,
                        path, args: mode);
                    break;
            }
        }

        private static void MergeSettings(Dictionary<string, string> into,
            IReadOnlyDictionary<string, string> from)
        {
            if (from == null) return;
            foreach (var kv in from) into[kv.Key] = kv.Value;
        }

        /// <summary>Modes that produce output (rows, macros, activators, or
        /// mouse axes). Only these get the dropped-group-settings notes; a
        /// wholly-skipped group's own entry already covers its settings.</summary>
        private static readonly HashSet<string> ProductiveModes = new(StringComparer.Ordinal)
        {
            "four_buttons", "switches", "dpad", "single_button", "trigger",
            "joystick_move", "joystick_mouse", "mouse_joystick", "joystick_camera",
            "absolute_mouse", "relative_mouse", "scrollwheel", "touch_menu",
            "radial_menu", "mouse_region", "gyro_to_mouse", "flickstick",
        };

        /// <summary>Response-shaping group settings PadForge has no per-row
        /// channel for. Named per issue #20's sensitivity-curve backlog;
        /// key list grounded on the corpus (deadzone_outer_radius 28640..31999,
        /// curve_exponent 4) plus the sibling keys of the same UI cluster.
        /// The mouse_region per-axis sensitivity scales (corpus 2795727040:
        /// 110/70; shipped configurator ids
        /// Horizontal/VerticalSensitivityMouseRegion) scale the region per
        /// axis. Since v6 (#9 B-15) trackpad-hosted regions consume them as
        /// the pointer rows' extent, so the drop note skips them there
        /// (skipRegionScales); stick/gyro hosts keep the wave-2A clamp
        /// approximation and still drop them named.</summary>
        private static readonly string[] CurveSettingKeys =
        {
            "deadzone_outer_radius", "deadzone_shape",
            "custom_curve_exponent", "curve_exponent", "output_curve",
            "sensitivity_horiz_scale", "sensitivity_vert_scale",
        };

        /// <summary>Mouse/region-mode feel settings PadForge has no channel
        /// for, named per group when present (finding 1g-2). rotation is a
        /// geometric rotation of the pad-to-cursor map (behavior, not just
        /// feel); friction / mouse_smoothing / trackball shape the cursor
        /// response. Corpus values: rotation -18/-21, friction 1,
        /// mouse_smoothing 22, trackball 0/1. Only named on the mouse/region
        /// modes (<see cref="MouseTuningModes"/>); flickstick keeps its own
        /// FlickStickTuningDropped note for the overlapping keys.</summary>
        private static readonly string[] MouseModeTuningKeys =
        {
            "rotation", "friction", "mouse_smoothing", "trackball",
        };

        /// <summary>Modes whose dropped <see cref="MouseModeTuningKeys"/> get
        /// the MouseModeTuningDropped note. flickstick is excluded: it reports
        /// the same keys through FlickStickTuningDropped in EmitFlickStick.</summary>
        private static readonly HashSet<string> MouseTuningModes = new(StringComparer.Ordinal)
        {
            "absolute_mouse", "relative_mouse", "joystick_mouse", "mouse_joystick",
            "joystick_camera", "gyro_to_mouse", "mouse_region",
        };

        /// <summary>True when a Steam group setting stores an "on" (non-zero)
        /// boolean, e.g. <c>invert_x/invert_y/invert_z "1"</c>. Absent / "0" /
        /// junk read as off.</summary>
        private static bool SettingIsOn(Dictionary<string, string> settings, string key)
            => settings.TryGetValue(key, out var v)
               && int.TryParse((v ?? "").Trim(), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out int n)
               && n != 0;

        /// <summary>Names an axis inversion a mouse-axis emitter could not
        /// apply, so the row stays honest instead of dropping the flag under
        /// a Clean label (finding 1g-1 sibling). invert_z has no third
        /// mouse-delta axis (the pairs are X/Y only); flick stick's angle
        /// read never consults <see cref="MappingSource.Invert"/>.</summary>
        private static void ReportUnappliedInversion(Run run, string path, string keys)
        {
            run.Report.Add(TranslationStatus.Partial, TranslationReasons.AxisInversionNotApplied,
                path, args: keys);
        }

        /// <summary>Named notes for group settings that used to drop
        /// silently: response-curve shaping, gyro engage/ratchet button
        /// masks, and the group-level haptic override (counted into the
        /// per-config aggregate).</summary>
        private void ReportDroppedGroupSettings(Run run,
            Dictionary<string, string> settings, string path,
            bool skipRegionScales = false, bool reportMouseTuning = false)
        {
            var curves = CurveSettingKeys
                .Where(k => settings.ContainsKey(k)
                    && !(skipRegionScales
                        && (k == "sensitivity_horiz_scale" || k == "sensitivity_vert_scale")))
                .ToList();
            if (curves.Count > 0)
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.ResponseCurveNotSupported,
                    path, args: string.Join(", ", curves));
            }

            // Mouse/region feel settings with no PadForge channel (finding
            // 1g-2): rotation rotates the pad-to-cursor map, the rest shape
            // cursor response. Only on the mouse/region modes; flickstick
            // names its own overlapping keys.
            if (reportMouseTuning)
            {
                var mouseTuning = MouseModeTuningKeys
                    .Where(k => settings.TryGetValue(k, out var v)
                        && (v ?? "").Trim().Length > 0 && (v ?? "").Trim() != "0")
                    .ToList();
                if (mouseTuning.Count > 0)
                {
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.MouseModeTuningDropped,
                        path, args: string.Join(", ", mouseTuning));
                }
            }

            foreach (var key in new[] { "gyro_button", "gyro_ratchet_button_mask" })
            {
                if (!settings.TryGetValue(key, out var v)) continue;
                // Ratchet mask 0 = no ratchet button, the default; the
                // engage button index is meaningful at every value
                // (0 = right-pad touch on the Steam Controller).
                if (key == "gyro_ratchet_button_mask" && (v ?? "").Trim() == "0") continue;
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.GyroButtonMaskDropped,
                    path, args: new[] { key, v ?? "" });
            }

            // Group haptics have no PadForge channel; both the override twin
            // and the plain group-level intensity feed the per-config
            // aggregate (finding 1g-3; the HapticDropCount field comment
            // already states both group and activator haptics are counted).
            if (settings.TryGetValue("haptic_intensity_override", out var h)
                && (h ?? "").Trim() != "0")
            {
                run.HapticDropCount++;
            }
            if (settings.TryGetValue("haptic_intensity", out var hg)
                && (hg ?? "").Trim() != "0")
            {
                run.HapticDropCount++;
            }
        }

        /// <summary>Steam's group-level inner deadzone (0..32767 of full
        /// deflection) as a PadForge DeadZone percent. 0 / absent / junk
        /// return 0 (keep the engine default; Steam's 0 is region geometry,
        /// not a hair-trigger request).</summary>
        private static int GroupDeadZonePercent(Dictionary<string, string> settings)
        {
            if (!settings.TryGetValue("deadzone_inner_radius", out var raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                || v <= 0)
            {
                return 0;
            }
            return Math.Clamp((int)Math.Round(v * 100.0 / 32767.0), 1, 100);
        }

        /// <summary>Copy of <paramref name="s"/> carrying the group inner
        /// deadzone. Full field list on purpose (ResolvedSource is
        /// init-only); a new ResolvedSource field must be added here too.</summary>
        private static ResolvedSource WithDeadZone(ResolvedSource s, int deadZone) => new()
        {
            Descriptor = s.Descriptor,
            HalfAxis = s.HalfAxis,
            Invert = s.Invert,
            DeadZone = deadZone,
            AutomapTarget = s.AutomapTarget,
            XboxButtonBit = s.XboxButtonBit,
            MacroAxisTarget = s.MacroAxisTarget,
            TrackpadFeature = s.TrackpadFeature,
            IsAnalogTriggerPull = s.IsAnalogTriggerPull,
            GateDescriptor = s.GateDescriptor,
            PartialReasonKey = s.PartialReasonKey,
        };

        /// <summary>Copy of <paramref name="s"/> carrying the group-level
        /// requires_click gate as its AND companion, so macro-shaped
        /// translations (a wedge-hosted set_led, a Long_Press key) inherit
        /// the click requirement exactly like the rows do. Same full-field
        /// discipline as <see cref="WithDeadZone"/>.</summary>
        private static ResolvedSource WithGate(ResolvedSource s, string gate) => new()
        {
            Descriptor = s.Descriptor,
            HalfAxis = s.HalfAxis,
            Invert = s.Invert,
            DeadZone = s.DeadZone,
            AutomapTarget = s.AutomapTarget,
            XboxButtonBit = s.XboxButtonBit,
            MacroAxisTarget = s.MacroAxisTarget,
            TrackpadFeature = s.TrackpadFeature,
            IsAnalogTriggerPull = s.IsAnalogTriggerPull,
            GateDescriptor = gate,
            PartialReasonKey = s.PartialReasonKey,
        };

        /// <summary>Walks a group's named inputs and translates each
        /// activator's bindings against the resolved physical source.</summary>
        private void TranslateMemberGroup(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings,
            IReadOnlyList<string> onlyInputs = null)
        {
            bool requiresClick = RequiresClick(slot, group, settings);
            int groupDeadZonePct = GroupDeadZonePercent(settings);
            var half = PhysicalSlotResolver.HalfFor(slot, run.SinglePadTrackpads);
            bool halfNoted = false;

            foreach (var inputName in group.Inputs.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (onlyInputs != null && !onlyInputs.Contains(inputName, StringComparer.OrdinalIgnoreCase))
                    continue;
                var input = group.Inputs[inputName];
                if (input.Activators.Count == 0) continue;

                var source = PhysicalSlotResolver.Resolve(slot, inputName, run.NintendoLabels,
                    run.SinglePadTrackpads);
                // The group inner deadzone lands on the axis-natured member
                // reads (stick-as-dpad wedges). Explicit thresholds (the
                // trigger click's 75 / edge's 15) encode reachable-range
                // semantics and stay.
                if (source != null && groupDeadZonePct > 0
                    && source.HalfAxis && source.DeadZone == 0)
                {
                    source = WithDeadZone(source, groupDeadZonePct);
                }
                string inputPath = $"{path}/{inputName}";
                if (source == null)
                {
                    string reason = inputName.Equals("edge", StringComparison.OrdinalIgnoreCase)
                        ? TranslationReasons.EdgeInputNotSupported
                        : TranslationReasons.UnknownPhysicalInput;
                    foreach (var act in input.Activators)
                        foreach (var b in act.Bindings)
                            ReportSkipUnlessSilent(run, reason, inputPath, b,
                                slotArg: slot.ToString(), inputArg: inputName);
                    continue;
                }

                // Click-gate the trackpad D-pad wedges when the group
                // requires a pad click (the classic Steam Controller feel).
                bool isWedge = inputName.StartsWith("dpad_", StringComparison.OrdinalIgnoreCase);
                string clickGate = requiresClick
                    && PhysicalSlotResolver.IsTrackpad(slot)
                    && isWedge
                        ? $"Touchpad {PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads)} Click"
                        : null;
                // The gate rides the source too, so macro-shaped
                // translations of the wedge (set_led, Long_Press keys)
                // inherit the click requirement in their device-free
                // trigger entries, not only the rows (wave 3).
                if (clickGate != null && source.GateDescriptor == null)
                    source = WithGate(source, clickGate);

                // Half-hosted D-pad wedges (#9 B-1): the anchor-relative
                // wedge gesture has no half window, so the group's wedges
                // read the whole pad. One honest note per group, and only
                // when a wedge actually emitted something (a group whose
                // bindings all skip approximates nothing).
                bool watchHalf = !halfNoted && isWedge && half != TrackpadHalf.Whole;
                int emittedBefore = watchHalf ? CountEmitted(run) : 0;

                TranslateInput(run, preset, input, source, clickGate, layer, inputPath);

                if (watchHalf && CountEmitted(run) > emittedBefore)
                {
                    run.Report.Add(TranslationStatus.Partial,
                        TranslationReasons.TrackpadHalfApproximated, path);
                    halfNoted = true;
                }
            }
        }

        /// <summary>Count of report entries that emitted output (rows,
        /// macros, layer switches). Cheap proxy for "did this member
        /// produce anything", used by the half-approximation notes so
        /// all-skipped groups stay note-free.</summary>
        private static int CountEmitted(Run run)
        {
            int n = 0;
            var entries = run.Report.Entries;
            for (int i = 0; i < entries.Count; i++)
                if (!string.IsNullOrEmpty(entries[i].Emitted)) n++;
            return n;
        }

        private static bool RequiresClick(SteamSlot slot, SteamInputGroup group,
            Dictionary<string, string> settings)
        {
            if (!PhysicalSlotResolver.IsTrackpad(slot)) return false;
            if (!string.Equals(group.Mode, "dpad", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(group.Mode, "reference", StringComparison.OrdinalIgnoreCase))
                return false;
            // Absent = require click (the Steam Controller's classic
            // trackpad D-pad default); explicit "0" = act on touch.
            return !settings.TryGetValue("requires_click", out var v)
                || !string.Equals(v?.Trim(), "0", StringComparison.Ordinal);
        }

        /// <summary>Menu group settings the overlay-backed menus have no
        /// channel for, named per group when present and non-zero. The
        /// only corpus-era key is "sensitivity" (shipped configurator
        /// "In-Menu Sensitivity": cursor movement within the menu; the
        /// PadForge hover math has no in-menu cursor).</summary>
        private static readonly string[] MenuDroppedKeys = { "sensitivity" };

        /// <summary>radial_menu / touch_menu groups (#9 B-17): first-class
        /// overlay-backed menus. The group becomes a MenuDefinitionEntry
        /// (structure: kind, host surface, layer, fire type, cells,
        /// labels, overlay geometry) and each bound
        /// <c>touch_menu_button_N</c> cell translates its bindings through
        /// the NORMAL activator/binding walk against a synthetic
        /// "Menu {id} Item {n}" source descriptor, which the engine's menu
        /// runtime asserts on hover-commit. Key semantics grounded on the
        /// shipped configurator strings: fire types Click / Release /
        /// Touch Release / Always (touchmenu_button_fire_type 0..3);
        /// radial button_0 is the CENTER button
        /// ("ControllerBinding_RadialMenuButton0" = "Radial Menu Center
        /// Button") and 1..N the ring; grids size from
        /// touch_menu_button_count with "Same As Command Count" when
        /// absent; position/scale/opacity/show-labels ride the overlay.
        /// Ring slots are POSITIONAL: buttons serialize under stable slot
        /// keys and wild configs preserve gaps (corpus 3456927474: ring
        /// 1,2,3,5,8,9,10,12), so slots = the highest bound ring index
        /// and unbound slots stay empty wedges.</summary>
        private void TranslateMenuGroup(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings,
            bool radial)
        {
            // Host surface: a stick (deflection hovers) or a trackpad
            // (touch position hovers). Nothing else has a direction /
            // position surface to hover with.
            string host;
            int hostHalf = 0;
            if (PhysicalSlotResolver.IsStick(slot))
            {
                host = slot == SteamSlot.Joystick ? "Gamepad LeftStick" : "Gamepad RightStick";
            }
            else if (PhysicalSlotResolver.IsTrackpad(slot))
            {
                host = $"Touchpad {PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads)}";
                hostHalf = (int)PhysicalSlotResolver.HalfFor(slot, run.SinglePadTrackpads);
            }
            else
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.MenuSurfaceNotSupported,
                    path, args: slot.ToString());
                return;
            }

            // Bound cells: touch_menu_button_{n} inputs with activators.
            var cells = group.Inputs
                .Where(kv => kv.Key.StartsWith("touch_menu_button_", StringComparison.OrdinalIgnoreCase))
                .Select(kv => (Index: ParseTrailingInt(kv.Key), Input: kv.Value))
                .Where(c => c.Index >= 0 && c.Input.Activators.Count > 0)
                .OrderBy(c => c.Index)
                .ToList();
            if (cells.Count == 0)
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.MenuEmpty, path);
                return;
            }

            int maxIndex = cells[cells.Count - 1].Index;
            bool hasCenter = radial && cells[0].Index == 0;
            // Positional and uncapped on purpose: every bound cell keeps
            // its slot (Steam's own vocabulary tops out at RadialMenuButton20
            // / 16-cell grids, so wild counts are already bounded by what
            // the configurator can author; the OVERLAY renderer carries its
            // own sanity cap so a hand-hacked config cannot demand an
            // unbounded window).
            int cellCount = radial
                ? maxIndex // ring slots, center (index 0) excluded
                : Math.Max(ParseIntSetting(settings, "touch_menu_button_count", 0), maxIndex + 1);

            int menuId = run.NextMenuId++;

            var entry = new PadForge.Engine.Menus.MenuDefinitionEntry
            {
                DeviceGuid = "",
                MenuId = menuId,
                Name = string.IsNullOrWhiteSpace(group.Name)
                    ? $"{(radial ? "Radial" : "Touch")} Menu {menuId}" : group.Name.Trim(),
                Kind = radial ? PadForge.Engine.Menus.MenuKind.Radial
                              : PadForge.Engine.Menus.MenuKind.Grid,
                HostDescriptor = host,
                HostHalf = hostHalf,
                LayerMask = layer == "Base" ? "" : layer,
                FireType = (PadForge.Engine.Menus.MenuFireType)Math.Clamp(
                    ParseIntSetting(settings, "touchmenu_button_fire_type", 0), 0, 3),
                CellCount = cellCount,
                HasCenter = hasCenter,
                ShowLabels = !(settings.TryGetValue("touch_menu_show_labels", out var sl)
                    && (sl ?? "").Trim() == "0"),
                PosXPercent = Math.Clamp(ParseIntSetting(settings, "touch_menu_position_x", 50), 0, 100),
                PosYPercent = Math.Clamp(ParseIntSetting(settings, "touch_menu_position_y", 50), 0, 100),
                ScalePercent = Math.Clamp(ParseIntSetting(settings, "touch_menu_scale", 100), 10, 400),
                OpacityPercent = Math.Clamp(ParseIntSetting(settings, "touch_menu_opacity", 90), 5, 100),
            };
            int engageDz = GroupDeadZonePercent(settings);
            if (engageDz > 0) entry.EngageDeadzonePercent = engageDz;

            int iconCells = 0;
            foreach (var cell in cells)
            {
                entry.Items.Add(new PadForge.Engine.Menus.MenuItemDefinition
                {
                    Index = cell.Index,
                    Label = CellLabel(cell.Input),
                });
                if (cell.Input.Activators.Any(a => a.Bindings.Any(b =>
                        (b.Raw ?? "").Contains(".png", StringComparison.OrdinalIgnoreCase))))
                {
                    iconCells++;
                }
            }
            run.Profile.Menus.Add(entry);

            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MenuEmitted, path,
                emitted: $"{(radial ? "Radial" : "Grid")} menu {menuId} on {host}: "
                    + $"{cells.Count} bound cells",
                args: cells.Count.ToString(CultureInfo.InvariantCulture));

            // Steam renders per-cell icon glyphs (ghost_*.png); PadForge's
            // overlay renders text labels only. One honest note per menu.
            if (iconCells > 0)
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.MenuIconsDropped,
                    path, args: iconCells.ToString(CultureInfo.InvariantCulture));
            }

            var droppedKeys = MenuDroppedKeys
                .Where(k => settings.TryGetValue(k, out var v) && (v ?? "").Trim() != "0")
                .ToList();
            if (droppedKeys.Count > 0)
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.MenuTuningDropped,
                    path, args: string.Join(", ", droppedKeys));
            }

            // Cell bindings ride the normal activator/binding walk against
            // the menu-item source, so keys become rows, layer engages
            // become activators, cursor warps become macros, all triggered
            // by the item's hover-commit fire.
            foreach (var cell in cells)
            {
                var source = new ResolvedSource { Descriptor = $"Menu {menuId} Item {cell.Index}" };
                TranslateInput(run, preset, cell.Input, source, null, layer,
                    $"{path}/touch_menu_button_{cell.Index}");
            }

            // The configurator also offers menu-level Click / Touch
            // commands beside the cells ("ControllerBinding_TouchMenuClick"
            // = "Click"); when bound they translate as ordinary members of
            // the hosting surface.
            TranslateMemberGroup(run, preset, group, slot, layer, path, settings,
                onlyInputs: new[] { "click", "touch" });
        }

        /// <summary>Overlay label for a menu cell: the author's label when
        /// the binding carries one (the second comma field), else the
        /// binding's own parameter ("F5", "A"), matching how Steam falls
        /// back to the command glyph when no label is set.</summary>
        private static string CellLabel(SteamInputInput input)
        {
            foreach (var act in input.Activators)
            {
                foreach (var b in act.Bindings)
                {
                    if (!string.IsNullOrWhiteSpace(b.ActionName)) return b.ActionName.Trim();
                }
            }
            foreach (var act in input.Activators)
            {
                foreach (var b in act.Bindings)
                {
                    string p = FirstToken(b.Param);
                    if (p.Length > 0) return p;
                }
            }
            return "";
        }

        /// <summary>Mouse-region keys the pointer rows have no channel
        /// for, named per group when present (the flick-stick shape).
        /// teleport_start/teleport_stop are the shipped configurator's
        /// "Snap Cursor on Activation" / "Return Cursor on Deactivation"
        /// (the pointer already warps on touch and freezes on lift, but
        /// the mode-shift snap-back is a cursor-history behavior PadForge
        /// does not keep); edge_binding_radius/_invert shape WHERE the
        /// group's own "edge" member fires, which translates untuned.
        /// Zero values mean "off" for all four, so only non-zero values
        /// are named.</summary>
        private static readonly string[] MouseRegionDroppedKeys =
        {
            "teleport_start", "teleport_stop",
            "edge_binding_radius", "edge_binding_invert",
        };

        /// <summary>mouse_region: Steam maps the hosting surface absolutely
        /// onto a screen region centered at (position_x%, position_y%)
        /// sized scale% of the screen, active while the surface is touched
        /// (shipped configurator: PositionXMouse / PositionYMouse are
        /// #Unit_Percent "the on screen position that the region will be
        /// centered around", ScaleMouseRegion "scale[s] the size of the
        /// region that is mapped to the outer extents of the pad/stick").
        /// Since v6 (#9 B-15) trackpad hosts translate FAITHFULLY to the
        /// engine's absolute "Touchpad {p} Pointer X/Y" rows with the
        /// region geometry on the per-source window params: Clean rows,
        /// no macro, halves via the region-window suffix. Hosts whose
        /// surface has no absolute position (sticks, gyro) keep the wave-2A
        /// approximation: PadForge's nearest primitive there is the #110
        /// cursor clamp, a centered inset rectangle, lowered to a
        /// while-held clamp macro engaged on the pull's axis trigger; hosts
        /// with no press surface at all keep the named skip. The group's
        /// click/touch/edge members translate as normal bindings either way
        /// (the caller runs TranslateMemberGroup).</summary>
        private void TranslateMouseRegion(Run run, SteamSlot slot, string layer, string path,
            Dictionary<string, string> settings)
        {
            int scale = Math.Clamp(ParseIntSetting(settings, "scale", 100), 1, 100);
            int posX = Math.Clamp(ParseIntSetting(settings, "position_x", 50), 0, 100);
            int posY = Math.Clamp(ParseIntSetting(settings, "position_y", 50), 0, 100);

            // Trackpad hosts get the REAL thing since v6 (#9 B-15): Steam's
            // mouse_region "treats the pad as a 1:1 map to screen space, so
            // touching a particular place on the pad will always put the
            // cursor in the same place on the screen" (Steamworks Input
            // Source Modes doc), which is exactly the engine's absolute
            // "Touchpad {p} Pointer X/Y" family on the KbM mouse targets.
            // Region geometry rides the per-source window params:
            //   center = position_x / 100 (X), 1 - position_y / 100 (Y;
            //     Steam's position_y is bottom-origin per sc-controller's
            //     proven importer, scc/foreign/vdf.py "y = 1.0 - (y/100.0)",
            //     while the engine's screen axis is top-origin),
            //   extent = scale/100 x sensitivity_axis_scale/100 (the shipped
            //     configurator's ScaleMouseRegion and
            //     Horizontal-/VerticalSensitivityMouseRegion, both percent).
            // The group engages only while touched, which the pointer's
            // finger-down validity gate reproduces, so no macro is needed.
            if (PhysicalSlotResolver.IsTrackpad(slot))
            {
                var pair = PhysicalSlotResolver.PointerAxisPair(slot, run.SinglePadTrackpads);
                double sensH = Math.Clamp(ParseIntSetting(settings, "sensitivity_horiz_scale", 100), 1, 400) / 100.0;
                double sensV = Math.Clamp(ParseIntSetting(settings, "sensitivity_vert_scale", 100), 1, 400) / 100.0;

                // invert_x/invert_y (finding 1g-1): the absolute pointer
                // reads through the same bipolar evaluator as the finger
                // axes and defers Invert to the wrapper's `Invert ? -raw`
                // (ReadTunedTouchpadPointer applies none itself; Step 3's
                // MouseAbs routing calls EvaluateForBipolarAxisTarget), so
                // the flipped map stays Clean.
                bool invertX = SettingIsOn(settings, "invert_x");
                bool invertY = SettingIsOn(settings, "invert_y");

                var srcX = new MappingSource
                {
                    Descriptor = pair.Value.X,
                    ParamPointerCenter = posX / 100.0,
                    ParamPointerExtent = scale / 100.0 * sensH,
                };
                if (invertX) srcX.Invert = true;
                var srcY = new MappingSource
                {
                    Descriptor = pair.Value.Y,
                    ParamPointerCenter = 1.0 - posY / 100.0,
                    ParamPointerExtent = scale / 100.0 * sensV,
                };
                if (invertY) srcY.Invert = true;
                AddRowSource(run, isKbm: true, layer, "KbmMouseX", srcX, isAxis: true,
                    TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                AddRowSource(run, isKbm: true, layer, "KbmMouseY", srcY, isAxis: true,
                    TranslationStatus.Clean, TranslationReasons.RowEmitted, path);

                var dropped = MouseRegionDroppedKeys
                    .Where(k => settings.TryGetValue(k, out var v) && (v ?? "").Trim() != "0")
                    .ToList();
                if (dropped.Count > 0)
                {
                    run.Report.Add(TranslationStatus.Partial,
                        TranslationReasons.MouseRegionTuningDropped, path,
                        args: string.Join(", ", dropped));
                }

                // No third pointer axis: invert_z can't ride the source.
                if (SettingIsOn(settings, "invert_z"))
                    ReportUnappliedInversion(run, path, "invert_z");
                return;
            }

            // Sticks and gyro have no absolute position surface, so they
            // keep the wave-2A clamp-macro approximation.
            var host = PhysicalSlotResolver.RegionEngageSource(slot, run.SinglePadTrackpads);
            if (host == null)
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.NoDeviceFreeTrigger,
                    path);
                return;
            }

            var macro = new TranslatedMacro
            {
                Name = $"Cursor region ({SlotToken(slot)})",
                Action = TranslatedMacroAction.MouseLimitRegion,
                TriggerMode = "WhileHeld", // semantic; materializer lowers to an on/off toggle pair
                ConsumeTrigger = false,
                RegionXPercent = posX,
                RegionYPercent = posY,
                RegionScalePercent = scale,
            };
            string feature = FillMacroTrigger(macro, host);
            run.Profile.Macros.Add(macro);
            if (feature != null)
            {
                // A half-hosted region engages on the half's touch spot,
                // which needs the Touchpad-tab feature: name it beside the
                // geometry approximation below.
                run.Report.Add(TranslationStatus.Partial,
                    TranslationReasons.TrackpadFeatureRequired, path, args: feature);
            }
            run.Report.Add(TranslationStatus.Partial, TranslationReasons.MouseRegionApproximated,
                path, emitted: "Cursor region clamp macro",
                args: new[]
                {
                    scale.ToString(CultureInfo.InvariantCulture),
                    posX.ToString(CultureInfo.InvariantCulture),
                    posY.ToString(CultureInfo.InvariantCulture),
                });
        }

        private static int ParseIntSetting(Dictionary<string, string> settings, string key, int fallback)
            => settings.TryGetValue(key, out var raw)
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v : fallback;

        /// <summary>Display token for macro names, mirroring the config's
        /// own slot vocabulary.</summary>
        private static string SlotToken(SteamSlot slot) => slot switch
        {
            SteamSlot.LeftTrackpad => "left_trackpad",
            SteamSlot.RightTrackpad => "right_trackpad",
            SteamSlot.CenterTrackpad => "center_trackpad",
            SteamSlot.Joystick => "joystick",
            SteamSlot.RightJoystick => "right_joystick",
            SteamSlot.LeftTrigger => "left_trigger",
            SteamSlot.RightTrigger => "right_trigger",
            SteamSlot.Gyro => "gyro",
            SteamSlot.ButtonDiamond => "button_diamond",
            SteamSlot.Switch => "switch",
            SteamSlot.Dpad => "dpad",
            _ => "input",
        };

        /// <summary>Trigger groups: the analog pull passes through to the
        /// xinput trigger implicitly. Both sides emit an explicit axis row
        /// (authoritative sets spell out every output Steam produces): the
        /// crossed side to the opposite trigger here, the matched side via
        /// the Finalize matched-analog pass so a click identity for the
        /// same trigger absorbs behind the analog source.</summary>
        private void TranslateTriggerGroup(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings)
        {
            if (slot == SteamSlot.LeftTrigger || slot == SteamSlot.RightTrigger)
            {
                bool left = slot == SteamSlot.LeftTrigger;
                int dzPct = GroupDeadZonePercent(settings);
                // output_trigger: 1 = left, 2 = right, 0/absent = matched side.
                int output = settings.TryGetValue("output_trigger", out var raw)
                    && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int o)
                        ? o : 0;
                bool crossed = (output == 1 && !left) || (output == 2 && left);
                string sourceDesc = left ? "Gamepad LeftTrigger" : "Gamepad RightTrigger";
                if (crossed)
                {
                    string target = left ? "RightTrigger" : "LeftTrigger";
                    var src = new MappingSource { Descriptor = sourceDesc };
                    if (dzPct > 0) src.DeadZone = dzPct;
                    AddRowSource(run, isKbm: false, layer, target,
                        src, isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path,
                        binding: $"output_trigger {output}");
                }
                else
                {
                    run.AddMatchedAnalog(layer, left ? "LeftTrigger" : "RightTrigger",
                        sourceDesc, path, dzPct);
                }
            }

            TranslateMemberGroup(run, preset, group, slot, layer, path, settings);
        }

        /// <summary>joystick_move: stick passthrough. Both sides emit the
        /// explicit axis pair (authoritative sets spell out every output
        /// Steam produces): output_joystick redirects to the other stick
        /// here, the matched side via the Finalize matched-analog pass.
        /// Trackpad-as-stick rides the gesture StickX/StickY channel
        /// (Partial: needs the Touchpad-tab toggle).</summary>
        private void TranslateJoystickMove(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings)
        {
            int output = settings.TryGetValue("output_joystick", out var raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int o)
                    ? o : 0;
            int dzPct = GroupDeadZonePercent(settings);

            MappingSource Src(string descriptor)
            {
                var s = new MappingSource { Descriptor = descriptor };
                if (dzPct > 0) s.DeadZone = dzPct;
                return s;
            }

            if (PhysicalSlotResolver.IsStick(slot))
            {
                bool left = slot == SteamSlot.Joystick;
                bool crossed = (output == 1 && !left) || (output == 2 && left);
                string src = left ? "LeftStick" : "RightStick";
                if (crossed)
                {
                    string dst = left ? "Right" : "Left";
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisX",
                        Src($"Gamepad {src}X"), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path,
                        binding: $"output_joystick {output}");
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisY",
                        Src($"Gamepad {src}Y"), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path,
                        binding: $"output_joystick {output}");
                }
                else
                {
                    string dst = left ? "Left" : "Right";
                    run.AddMatchedAnalog(layer, $"{dst}ThumbAxisX", $"Gamepad {src}X", path, dzPct);
                    run.AddMatchedAnalog(layer, $"{dst}ThumbAxisY", $"Gamepad {src}Y", path, dzPct);
                }
            }
            else if (PhysicalSlotResolver.IsTrackpad(slot))
            {
                int p = PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads);
                var half = PhysicalSlotResolver.HalfFor(slot, run.SinglePadTrackpads);
                // 2 = right stick, anything else lands on the left.
                string dst = output == 2 ? "Right" : "Left";
                if (half != TrackpadHalf.Whole)
                {
                    // A stick hosted on one half of a single physical pad
                    // (#9 B-1) rides the region-windowed absolute finger
                    // reads: deflection from the half's center, exactly
                    // Steam's region-centered virtual stick, live with no
                    // feature toggle. (The gesture Stick channel below has
                    // no half window, so it can't carry this case.)
                    string sfx = PhysicalSlotResolver.HalfSuffix(half);
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisX",
                        Src($"Touchpad {p} Finger 0 X{sfx}"), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisY",
                        Src($"Touchpad {p} Finger 0 Y{sfx}"), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                }
                else
                {
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisX",
                        Src($"Touchpad {p} StickX"), isAxis: true,
                        TranslationStatus.Partial, TranslationReasons.TrackpadFeatureRequired, path,
                        args: PhysicalSlotResolver.FeatureJoystickOutput);
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisY",
                        Src($"Touchpad {p} StickY"), isAxis: true,
                        TranslationStatus.Partial, TranslationReasons.TrackpadFeatureRequired, path,
                        args: PhysicalSlotResolver.FeatureJoystickOutput);
                }
            }

            TranslateMemberGroup(run, preset, group, slot, layer, path, settings);
        }

        /// <summary>Mouse-mode groups: the slot's analog surface drives the
        /// KbM mouse delta. Multiple groups merging into KbmMouseX/Y get
        /// Combine=Sum (mouse deltas are additive). The sensitivity key is
        /// per mode: the classic modes store "sensitivity", gyro_to_mouse
        /// stores "gyro_natural_sensitivity". Touchpad rows (family 1)
        /// carry the ratio on the generic per-source Sensitivity too: B-13
        /// widened that knob to the finger X/Y reads (relative delta
        /// included), so the config's per-group touch sensitivity lives on
        /// the row instead of the old TouchpadTuningNotPerRow drop.</summary>
        private void EmitMouseAxes(Run run, SteamSlot slot, string layer, string path,
            Dictionary<string, string> settings, double baseline,
            string sensitivityKey = "sensitivity")
        {
            var pair = PhysicalSlotResolver.MouseAxisPair(slot, run.SinglePadTrackpads);
            if (pair == null) return;

            double ratio = 1.0;
            if (settings.TryGetValue(sensitivityKey, out var sensRaw)
                && double.TryParse(sensRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double sens)
                && sens > 0)
            {
                ratio = Math.Clamp(sens / baseline, 0.05, 20.0);
            }

            var (x, y, family) = pair.Value;
            int dzPct = GroupDeadZonePercent(settings);

            // Steam's per-group axis inversion (finding 1g-1). Every
            // mouse-axis family here reads through the bipolar evaluator's
            // `Invert ? -raw` transform (SourceCoercion.EvaluateForBipolarAxisTarget):
            // the stick axes (family 0), the touchpad finger delta (family 1,
            // TryReadTouchpadAxis returns the raw delta and the wrapper flips
            // it), and the gyro rate (family 2) all honor MappingSource.Invert
            // and don't consume it internally, so the flipped rows stay Clean.
            bool invertX = SettingIsOn(settings, "invert_x");
            bool invertY = SettingIsOn(settings, "invert_y");

            MappingSource Make(string descriptor, bool invert)
            {
                var src = new MappingSource { Descriptor = descriptor };
                if (family == 0 || family == 1) src.Sensitivity = ratio;
                else if (family == 2) src.GyroSensitivity = ratio;
                if (dzPct > 0) src.DeadZone = dzPct;
                if (invert) src.Invert = true;
                return src;
            }

            AddRowSource(run, isKbm: true, layer, "KbmMouseX", Make(x, invertX), isAxis: true,
                TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
            AddRowSource(run, isKbm: true, layer, "KbmMouseY", Make(y, invertY), isAxis: true,
                TranslationStatus.Clean, TranslationReasons.RowEmitted, path);

            // invert_z addresses a third (roll) axis the X/Y mouse-delta pair
            // does not emit, so name it rather than drop it under Clean rows.
            if (SettingIsOn(settings, "invert_z"))
                ReportUnappliedInversion(run, path, "invert_z");
        }

        /// <summary>Flick stick keys the engine has no grounded channel
        /// for, named per group when present. The list is the wild-corpus
        /// vocabulary (2779652507 / 2228940979: edge_binding_radius,
        /// mouse_smoothing, rotation, transition_time); mapping any of
        /// them onto the JSM-ported knobs would be a semantics guess, so
        /// they ride a named Partial instead.</summary>
        private static readonly string[] FlickStickDroppedKeys =
        {
            "edge_binding_radius", "mouse_smoothing", "rotation", "transition_time",
        };

        /// <summary>flickstick groups (#225): the stick becomes a
        /// "Flick Stick Right"/"Flick Stick Left" source on the KbM mouse X
        /// row. The group's "sensitivity" is Steam's shared Dots Per 360
        /// (client l10n: "Flick Stick ° to Mouse Pixels (Dots Per 360°)";
        /// corpus values 2603..2800) and lands 1:1 on
        /// ParamFlickCountsPer360; every other flick knob keeps its
        /// JSM-grounded default. Trackpad-hosted flickstick has no PadForge
        /// read (flick stick is a stick-only family), so it gets a named
        /// skip and the members still translate.</summary>
        private void EmitFlickStick(Run run, SteamSlot slot, string layer, string path,
            Dictionary<string, string> settings)
        {
            if (!PhysicalSlotResolver.IsStick(slot))
            {
                run.Report.Add(TranslationStatus.Skipped,
                    TranslationReasons.FlickStickSurfaceNotSupported, path,
                    args: slot.ToString());
                return;
            }

            var src = new MappingSource
            {
                Descriptor = slot == SteamSlot.RightJoystick
                    ? "Flick Stick Right"
                    : "Flick Stick Left",
            };
            if (settings.TryGetValue("sensitivity", out var dotsRaw)
                && double.TryParse(dotsRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double dots)
                && dots > 0)
            {
                src.ParamFlickCountsPer360 = dots;
            }

            AddRowSource(run, isKbm: true, layer, "KbmMouseX", src, isAxis: true,
                TranslationStatus.Clean, TranslationReasons.RowEmitted, path);

            var dropped = FlickStickDroppedKeys.Where(settings.ContainsKey).ToList();
            if (dropped.Count > 0)
            {
                run.Report.Add(TranslationStatus.Partial,
                    TranslationReasons.FlickStickTuningDropped, path,
                    args: string.Join(", ", dropped));
            }

            // Flick stick maps the stick ANGLE to camera rotation through
            // SourceKindRuntime.TickFlickStick, a specialized read that never
            // consults MappingSource.Invert (unlike the plain axis→mouse
            // modes). A Steam invert_x/y/z can't ride the source flag here, so
            // name it rather than emit an un-inverted row under a Clean label.
            var unappliedInvert = new[] { "invert_x", "invert_y", "invert_z" }
                .Where(k => SettingIsOn(settings, k))
                .ToList();
            if (unappliedInvert.Count > 0)
                ReportUnappliedInversion(run, path, string.Join(", ", unappliedInvert));
        }

        // ─────────────────────────────────────────────
        //  Input / activator / binding translation
        // ─────────────────────────────────────────────

        private void TranslateInput(Run run, SteamInputPreset preset, SteamInputInput input,
            ResolvedSource source, string clickGate, string layer, string path)
        {
            foreach (var activator in input.Activators)
            {
                string type = (activator.Type ?? "").Trim();
                string actPath = $"{path}/{type}";

                bool soft = false;
                bool onRelease = false;
                switch (type.ToLowerInvariant())
                {
                    case "full_press":
                    case "start_press":
                        break;
                    case "soft_press":
                        soft = true;
                        break;
                    case "release":
                        onRelease = true;
                        break;
                    case "long_press":
                        TranslateLongPress(run, preset, activator, input, source, layer, actPath);
                        continue;
                    case "double_press":
                        foreach (var b in activator.Bindings)
                            ReportSkipUnlessSilent(run, TranslationReasons.DoublePressNotSupported, actPath, b);
                        continue;
                    default:
                        foreach (var b in activator.Bindings)
                            ReportSkipUnlessSilent(run, TranslationReasons.UnknownActivatorType, actPath, b,
                                slotArg: type);
                        continue;
                }

                ReportDroppedActivatorExtras(run, activator, actPath);

                // hold_repeats enables key autofire; repeat_rate alone is
                // just the stored slider value with the feature off.
                bool holdRepeats = activator.Settings.TryGetValue("hold_repeats", out var hr)
                    && hr?.Trim() == "1";
                int intervalMs = 100;
                if (activator.Settings.TryGetValue("repeat_rate", out var rr)
                    && int.TryParse(rr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rrv))
                {
                    intervalMs = Math.Clamp(rrv, 10, 1000);
                }
                // The activator toggle setting latches the binding's output
                // until the input is pressed again (Valve's shipped strings:
                // "Toggle will make this activator continue to be active
                // after releasing it until it is pressed again"). Grounded
                // key name: Valve's own chord.vdf carries "toggle" "1" on a
                // Full_Press key binding.
                bool toggle = activator.Settings.TryGetValue("toggle", out var tg)
                    && tg?.Trim() == "1";

                foreach (var binding in activator.Bindings)
                {
                    TranslateBinding(run, preset, binding, source, clickGate, layer, actPath,
                        soft, onRelease, holdRepeats, intervalMs, toggle, input.Name);
                }
            }
        }

        /// <summary>Long_Press activators. Grounded on Valve's shipped
        /// description: "Long Press Activator requires the button to be held
        /// for a period of time to activate. Once the long press time has
        /// passed, it will activate stay on until you release it."
        /// long_press_time is milliseconds (corpus: 222 / 224 on 789818086;
        /// shipped configurator suffixes LongPress_LongPressTime with
        /// #Unit_Milliseconds); absent = Steam's UI default of 500 ms.
        ///
        /// <para>Layer engages (mode_shift / hold_layer / add_layer) map to
        /// the layer's ShiftActivator with DelayMs = long_press_time (the
        /// engine's hold-before-engage debounce is the same construct).
        /// Keys and buttons (wave 2A) ride the HoldForMs macro trigger:
        /// xinput targets hold the output button from the threshold until
        /// release (exact semantics via ButtonPress + UntilRelease), key
        /// targets fire one tap at the threshold (Partial: PadForge has no
        /// hold-a-key-until-release primitive), and the activator's own
        /// hold_repeats / toggle settings compose the turbo and latch
        /// variants at the same threshold.</para></summary>
        private void TranslateLongPress(Run run, SteamInputPreset preset,
            SteamInputActivator activator, SteamInputInput input, ResolvedSource source,
            string layer, string actPath)
        {
            int delayMs = 500;
            if (activator.Settings.TryGetValue("long_press_time", out var lpt)
                && int.TryParse(lpt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lptMs))
            {
                delayMs = Math.Clamp(lptMs, 1, 5000);
            }

            bool holdRepeats = activator.Settings.TryGetValue("hold_repeats", out var hr)
                && hr?.Trim() == "1";
            int intervalMs = 100;
            if (activator.Settings.TryGetValue("repeat_rate", out var rr)
                && int.TryParse(rr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rrv))
            {
                intervalMs = Math.Clamp(rrv, 10, 1000);
            }
            bool toggle = activator.Settings.TryGetValue("toggle", out var tg)
                && tg?.Trim() == "1";

            bool anyCarry = false;
            foreach (var binding in activator.Bindings)
            {
                string bt = (binding.Type ?? "").Trim().ToLowerInvariant();
                string action = FirstToken(binding.Param).ToUpperInvariant();
                if (bt == "mode_shift")
                {
                    anyCarry = true;
                    TranslateModeShift(run, preset, binding, source, actPath, delayMs, toggle);
                }
                else if (bt == "controller_action"
                    && (action == "ADD_LAYER" || action == "HOLD_LAYER" || action == "CAMERA_RESET"))
                {
                    anyCarry = true;
                    TranslateControllerAction(run, preset, binding, source, layer, actPath,
                        onRelease: false, input.Name, delayMs, toggle);
                }
                else if (bt == "key_press")
                {
                    anyCarry |= TranslateLongPressKey(run, preset, binding, source, actPath,
                        delayMs, holdRepeats, intervalMs, toggle, input.Name);
                }
                else if (bt == "xinput_button")
                {
                    anyCarry |= TranslateLongPressVc(run, binding, source, actPath,
                        delayMs, holdRepeats, intervalMs, toggle, input.Name);
                }
                else
                {
                    ReportSkipUnlessSilent(run, TranslationReasons.LongPressNotSupported, actPath, binding);
                }
            }

            if (anyCarry)
                ReportDroppedActivatorExtras(run, activator, actPath);
        }

        /// <summary>A Long_Press key binding (wave 2A): one tap at the hold
        /// threshold (or the autofire / latch variants when the activator
        /// also carries hold_repeats / toggle). Returns true when a macro
        /// was emitted (the activator counts as translated for the
        /// dropped-extras notes; a NoDeviceFreeTrigger skip does not).</summary>
        private bool TranslateLongPressKey(Run run, SteamInputPreset preset,
            SteamInputBinding binding, ResolvedSource source, string path,
            int holdMs, bool holdRepeats, int intervalMs, bool toggle, string inputName)
        {
            string keyName = FirstToken(binding.Param);
            if (!SteamInputVkTable.TryResolve(keyName, out byte vk, out bool supported))
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownKey,
                    path, binding.Raw, args: keyName);
                return false;
            }
            if (!supported)
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnsupportedKey,
                    path, binding.Raw, args: keyName);
                return false;
            }

            if (toggle)
            {
                bool latched = EmitKeyToggleMacro(run, binding, source, path, vk, keyName,
                    onRelease: false, inputName, holdMs);
                if (latched && holdRepeats)
                {
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.RepeatDropped,
                        path, binding.Raw);
                }
                return latched;
            }

            return EmitKeyMacro(run, preset, binding, source, path,
                holdRepeats
                    ? (TranslatedMacroAction.RepeatKeyWhileHeld, "HoldForMs")
                    : (TranslatedMacroAction.KeyTap, "HoldForMs"),
                vk, intervalMs, keyName, inputName, holdMs);
        }

        /// <summary>A Long_Press xinput binding (wave 2A): the target button
        /// is pressed at the hold threshold and held until the physical
        /// input releases (Valve's grounded semantics), or pulsed / latched
        /// when the activator also carries hold_repeats / toggle.
        /// Trigger-axis targets (LT/RT pulls) have no button-hold or pulse
        /// primitive and keep the named Long_Press skip.</summary>
        private bool TranslateLongPressVc(Run run, SteamInputBinding binding,
            ResolvedSource source, string path, int holdMs, bool holdRepeats,
            int intervalMs, bool toggle, string inputName)
        {
            if (!XInputTargetTable.TryResolve(binding.Param, out var xt))
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownXInputButton,
                    path, binding.Raw, args: binding.Param);
                return false;
            }
            if (xt.IsTriggerAxis)
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.LongPressNotSupported,
                    path, binding.Raw);
                return false;
            }

            if (toggle)
            {
                bool latched = EmitVcToggleMacro(run, binding, source, path, xt,
                    rowKept: false, inputName, holdMs);
                if (latched && holdRepeats)
                {
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.RepeatDropped,
                        path, binding.Raw);
                }
                return latched;
            }
            if (holdRepeats)
                return EmitVcTurboMacro(run, binding, source, path, xt, intervalMs, holdMs, inputName);
            return EmitVcHoldMacro(run, binding, source, path, xt, holdMs, inputName);
        }

        /// <summary>Named notes for activator settings that used to drop
        /// silently: press delays, the interruptible-off flag, and haptic
        /// intensity (counted into the per-config aggregate). Called only
        /// for activators that translate; a skipped activator's own entry
        /// covers its settings.</summary>
        private static void ReportDroppedActivatorExtras(Run run,
            SteamInputActivator activator, string path)
        {
            var drops = new List<string>();
            foreach (var key in new[] { "delay_start", "delay_end" })
            {
                if (activator.Settings.TryGetValue(key, out var raw)
                    && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms)
                    && ms > 0)
                {
                    drops.Add($"{key} {ms}");
                }
            }
            if (drops.Count > 0)
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.ActivatorDelayDropped,
                    path, args: string.Join(", ", drops));
            }

            // Steam's default is interruptible on; only the stored "0"
            // diverges from PadForge behavior.
            if (activator.Settings.TryGetValue("interruptable", out var i)
                && (i ?? "").Trim() == "0")
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.InterruptibleDropped, path);
            }

            if (activator.Settings.TryGetValue("haptic_intensity", out var h)
                && (h ?? "").Trim() != "0")
            {
                run.HapticDropCount++;
            }
        }

        private void TranslateBinding(Run run, SteamInputPreset preset, SteamInputBinding binding,
            ResolvedSource source, string clickGate, string layer, string path,
            bool soft, bool onRelease, bool holdRepeats, int intervalMs, bool toggle,
            string inputName)
        {
            string type = (binding.Type ?? "").Trim().ToLowerInvariant();
            switch (type)
            {
                case "key_press":
                    TranslateKeyPress(run, preset, binding, source, clickGate, layer, path,
                        soft, onRelease, holdRepeats, intervalMs, toggle, inputName);
                    break;

                case "mouse_button":
                {
                    if (!SteamInputVkTable.TryResolveMouseButton(binding.Param, out string target))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownMouseButton,
                            path, binding.Raw, args: binding.Param);
                        break;
                    }
                    if (onRelease)
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.ReleaseActivatorNotSupported,
                            path, binding.Raw);
                        break;
                    }
                    if (toggle)
                    {
                        // No mouse-button latch primitive; the binding stays
                        // momentary (named note instead of the old silence).
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.ToggleDropped,
                            path, binding.Raw);
                    }
                    EmitSourceRow(run, isKbm: true, layer, target, source, clickGate, isAxis: false,
                        soft, path, binding.Raw);
                    break;
                }

                case "mouse_wheel":
                {
                    // KbmScroll positive = up after Step 3's negation, and a
                    // pressed button source evaluates positive (SDL "down"),
                    // so scroll-up needs Invert and scroll-down doesn't.
                    // Horizontal has no negation: right is plain.
                    string param = (binding.Param ?? "").Trim().ToUpperInvariant();
                    (string target, bool invert)? wheel = param switch
                    {
                        "SCROLL_UP" => ("KbmScroll", true),
                        "SCROLL_DOWN" => ("KbmScroll", false),
                        "SCROLL_RIGHT" => ("KbmScrollH", false),
                        "SCROLL_LEFT" => ("KbmScrollH", true),
                        _ => null,
                    };
                    if (wheel == null)
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                            path, binding.Raw, args: $"mouse_wheel {binding.Param}");
                        break;
                    }
                    if (onRelease)
                    {
                        // Same reason the mouse_button and XInput legs above
                        // skip: the emitted row reads the source's CURRENT
                        // state, so a release binding would scroll for the
                        // whole hold and stop on release, which is the
                        // opposite of what it asked for. Skip with a named
                        // note rather than emit the inverted behavior.
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.ReleaseActivatorNotSupported,
                            path, binding.Raw);
                        break;
                    }
                    if (toggle)
                    {
                        // A latched scroll would scroll forever; the binding
                        // stays momentary (named note).
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.ToggleDropped,
                            path, binding.Raw);
                    }
                    var src = BuildSource(source, soft);
                    SetOutputInvert(src, wheel.Value.invert);
                    // Same AND-companion handling as EmitSourceRow: a
                    // single-pad click member carries its half's touch-spot
                    // gate (#9 B-1).
                    AddRowSource(run, isKbm: true, layer, wheel.Value.target, src, isAxis: true,
                        StatusFor(source, soft), ReasonFor(source, soft), path, binding.Raw,
                        args: source.TrackpadFeature,
                        clickGate: source.GateDescriptor != null
                            ? new MappingSource { Descriptor = source.GateDescriptor } : null);
                    break;
                }

                case "xinput_button":
                {
                    if (!XInputTargetTable.TryResolve(binding.Param, out var xt))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownXInputButton,
                            path, binding.Raw, args: binding.Param);
                        break;
                    }
                    if (onRelease)
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.ReleaseActivatorNotSupported,
                            path, binding.Raw);
                        break;
                    }
                    bool identity = !soft && clickGate == null
                        && string.Equals(source.AutomapTarget, xt.Target, StringComparison.Ordinal);

                    // Activator toggle (wave 2A): the press latches the
                    // target until the next press. Steam's toggle replaces
                    // the momentary output, but a macro-only structure is a
                    // dead letter here: the latch macro's only device-free
                    // trigger is the combined Xbox output, and with the row
                    // removed nothing feeds it (proven on 2774979654, whose
                    // stick click hosts ONLY the toggle binding). So the
                    // Wave-1 momentary row stays as the trigger's feed, the
                    // latch macro fires on the TARGET bit's press edge (the
                    // row asserts it exactly while the physical input is
                    // held; latches apply after trigger reads each frame, so
                    // the latch never masks its own edge), and the entry is
                    // Partial: the row re-asserts the target for the
                    // duration of the unlatching press, and any other
                    // binding feeding the same target also flips the latch.
                    // Trigger-axis targets have no latch primitive and keep
                    // the momentary row with a named drop.
                    bool latchEmitted = false;
                    if (toggle)
                    {
                        if (!xt.IsTriggerAxis)
                        {
                            EmitVcToggleMacro(run, binding, source, path, xt,
                                rowKept: true, inputName);
                            latchEmitted = true;
                            if (holdRepeats)
                            {
                                run.Report.Add(TranslationStatus.Partial, TranslationReasons.RepeatDropped,
                                    path, binding.Raw);
                            }
                            // fall through: the row emits below (identity or
                            // divergent), feeding the latch trigger.
                        }
                        else
                        {
                            run.Report.Add(TranslationStatus.Partial, TranslationReasons.ToggleDropped,
                                path, binding.Raw);
                        }
                    }
                    // Turbo (wave 2A): hold_repeats pulses the target while
                    // the physical input is held (Steam stores repeat_rate
                    // in ms; shipped configurator suffixes it
                    // #Unit_Milliseconds). A latch already replaced the
                    // output when one was emitted above. Identity turbo is
                    // impossible with the combined-output trigger (the
                    // identity row that feeds the trigger would hold the
                    // pulsed bit solid), so it keeps the Wave-1 row +
                    // RepeatDropped note; trigger-axis targets have no
                    // button bit to pulse and keep it too. Triggerless
                    // hosts (touchpads, paddles) ride device-free
                    // InputDevice descriptor triggers since wave 3.
                    if (!latchEmitted && holdRepeats)
                    {
                        if (!identity && !xt.IsTriggerAxis)
                        {
                            EmitVcTurboMacro(run, binding, source, path, xt, intervalMs,
                                holdMs: 0, inputName);
                            break;
                        }
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.RepeatDropped,
                            path, binding.Raw);
                    }
                    if (identity)
                    {
                        run.Identities.Add(new PendingIdentity
                        {
                            Layer = layer,
                            Target = xt.Target,
                            Source = source,
                            Path = path,
                            Binding = binding.Raw,
                            IsAxis = xt.IsTriggerAxis,
                        });
                        break;
                    }
                    EmitSourceRow(run, isKbm: false, layer, xt.Target, source, clickGate,
                        isAxis: xt.IsTriggerAxis, soft, path, binding.Raw);
                    break;
                }

                case "mode_shift":
                    TranslateModeShift(run, preset, binding, source, path, toggle: toggle);
                    break;

                case "controller_action":
                    TranslateControllerAction(run, preset, binding, source, layer, path, onRelease,
                        inputName, toggle: toggle);
                    break;

                case "game_action":
                    run.GameActionsByPreset[preset.Id] =
                        run.GameActionsByPreset.GetValueOrDefault(preset.Id) + 1;
                    break;

                default:
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                        path, binding.Raw, args: binding.Type ?? "");
                    break;
            }
        }

        private void TranslateKeyPress(Run run, SteamInputPreset preset, SteamInputBinding binding,
            ResolvedSource source, string clickGate, string layer, string path,
            bool soft, bool onRelease, bool holdRepeats, int intervalMs, bool toggle,
            string inputName)
        {
            string keyName = FirstToken(binding.Param);
            if (!SteamInputVkTable.TryResolve(keyName, out byte vk, out bool supported))
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownKey,
                    path, binding.Raw, args: keyName);
                return;
            }
            if (!supported)
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnsupportedKey,
                    path, binding.Raw, args: keyName);
                return;
            }

            // Activator toggle (wave 2A): the key latches down until the
            // input is pressed again. The latch replaces the momentary row
            // entirely (Steam's toggle replaces the momentary output). A
            // release activator's toggle flips on release instead. Hosts
            // without an Xbox output representation ride a device-free
            // InputDevice descriptor trigger (wave 3) instead of the old
            // keep-the-row ToggleDropped fallback.
            if (toggle)
            {
                EmitKeyToggleMacro(run, binding, source, path, vk, keyName, onRelease, inputName);
                if (holdRepeats)
                {
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.RepeatDropped,
                        path, binding.Raw);
                }
                return;
            }

            if (onRelease || holdRepeats)
            {
                // Macro-backed forms. Both need a device-free trigger, which
                // exists only for inputs with an Xbox output representation.
                EmitKeyMacro(run, preset, binding, source, path,
                    onRelease
                        ? (TranslatedMacroAction.KeyTap, "OnRelease")
                        : (TranslatedMacroAction.RepeatKeyWhileHeld, "WhileHeld"),
                    vk, intervalMs, keyName, inputName);
                return;
            }

            EmitSourceRow(run, isKbm: true, layer, SteamInputVkTable.KbmKeyTarget(vk),
                source, clickGate, isAxis: false, soft, path, binding.Raw);
        }

        private bool EmitKeyMacro(Run run, SteamInputPreset preset, SteamInputBinding binding,
            ResolvedSource source, string path,
            (TranslatedMacroAction Action, string TriggerMode) shape,
            byte vk, int intervalMs, string keyName, string inputName, int holdMs = 0)
        {
            bool longPressTap = shape.Action == TranslatedMacroAction.KeyTap && holdMs > 0;
            string verb = shape.Action == TranslatedMacroAction.KeyTap
                ? (longPressTap ? "Long press" : "Tap")
                : "Autofire";
            var macro = new TranslatedMacro
            {
                Name = $"{verb} {keyName} ({inputName})",
                Action = shape.Action,
                TriggerMode = shape.TriggerMode,
                TriggerHoldMs = holdMs,
                ConsumeTrigger = true,
                VirtualKey = vk,
                IntervalMs = intervalMs,
            };
            string feature = FillMacroTrigger(macro, source);
            run.Profile.Macros.Add(macro);
            // A Long_Press key gets its own named Partial: Steam holds the
            // key down from the threshold until release, PadForge fires one
            // tap at the threshold (no hold-a-key-until-release primitive).
            if (longPressTap)
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.LongPressKeyTap,
                    path, binding.Raw, emitted: $"{verb} {keyName} macro", keyName);
            }
            else
            {
                var (status, reason, arg) = MacroTriggerReport(source, feature);
                run.Report.Add(status, reason, path, binding.Raw,
                    emitted: $"{verb} {keyName} macro",
                    args: arg == null ? Array.Empty<string>() : new[] { arg });
            }
            return true;
        }

        /// <summary>Applies an OUTPUT-side sign flip to a built source without
        /// clobbering a half-axis selection.
        ///
        /// <para>MappingSource.Invert is dual-purpose: on a half-axis read of a
        /// centered "Axis N" it is consumed INSIDE the read as the half
        /// SELECTOR, and only elsewhere does it mean "negate the result".
        /// Assigning polarity straight onto Invert therefore silently flipped
        /// which half of the stick a binding read. The resolver sets the
        /// selector (PhysicalSlotResolver's north/south split); this writes the
        /// polarity to InvertOutput for exactly the sources where the engine
        /// says Invert is already spoken for, asking the engine's own predicate
        /// rather than re-deriving the rule here.</para></summary>
        private static void SetOutputInvert(MappingSource src, bool invert)
        {
            if (PadForge.Engine.Common.Mapping.SourceCoercion.InvertConsumedByHalfAxisRead(src))
                src.InvertOutput = invert;
            else
                src.Invert = invert;
        }

        /// <summary>True when a resolved physical source can drive a macro
        /// trigger through the Xbox slot's combined output: an Xbox button
        /// bit or an analog trigger read. Sources without one (paddles,
        /// touchpads, gyro) ride device-free InputDevice descriptor
        /// triggers instead (wave 3), so this is a trigger-SHAPE selector
        /// now, no longer an emit gate.</summary>
        private static bool HasDeviceFreeTrigger(ResolvedSource source)
            => source.XboxButtonBit != 0 || !string.IsNullOrEmpty(source.MacroAxisTarget);

        /// <summary>Stamps the macro's trigger from the hosting physical
        /// source. Inputs with an Xbox output representation keep the
        /// combined-output trigger (cheaper, consume-capable); everything
        /// else gets a device-free InputDevice descriptor trigger (wave 3):
        /// the hosting input's own descriptor with an empty device guid
        /// ("the device on the slot"), plus its AND-gate companion when the
        /// source carries one (a click gated on its half's touch spot).
        /// Descriptor triggers have no output bits to consume, so
        /// ConsumeTrigger is forced off for them. Returns the
        /// Touchpad-tab feature the trigger depends on (the source's
        /// TrackpadFeature), or null when the trigger is live by
        /// default; callers fold a non-null feature into a Partial
        /// report entry.</summary>
        private static string FillMacroTrigger(TranslatedMacro macro, ResolvedSource source)
        {
            if (HasDeviceFreeTrigger(source))
            {
                macro.TriggerXboxButtons = source.XboxButtonBit;
                macro.TriggerAxisTarget = source.XboxButtonBit == 0 ? source.MacroAxisTarget ?? "" : "";
                macro.TriggerAxisThresholdPercent = source.DeadZone > 0 ? source.DeadZone : 50;
                // Keep the hosting input's own descriptor. A combined-output
                // trigger only fires if some row actually FEEDS that bit, and
                // the macro-backed key forms (autofire / on-release) emit no
                // row for their own source. When nothing else feeds it, the
                // trigger is unreachable and the macro is dead. FinalizeMacro-
                // Triggers detects that after all rows materialize and swaps
                // these in; storing them costs nothing when it doesn't.
                macro.TriggerFallbackDescriptor = source.Descriptor ?? "";
                macro.TriggerFallbackGateDescriptor = source.GateDescriptor ?? "";
                return null;
            }
            macro.TriggerXboxButtons = 0;
            macro.TriggerAxisTarget = "";
            macro.TriggerInputDescriptors.Add(source.Descriptor);
            if (!string.IsNullOrEmpty(source.GateDescriptor))
                macro.TriggerInputDescriptors.Add(source.GateDescriptor);
            macro.ConsumeTrigger = false;
            return source.TrackpadFeature;
        }

        /// <summary>Status/reason pair for a macro whose trigger came from
        /// <see cref="FillMacroTrigger"/>: combined-output triggers keep the
        /// wave-2A Partial (the trigger rides the Xbox output, not the
        /// physical input); descriptor triggers read the hosting input
        /// directly and are Clean unless they depend on a Touchpad-tab
        /// feature.</summary>
        private static (TranslationStatus Status, string Reason, string Arg) MacroTriggerReport(
            ResolvedSource source, string feature)
        {
            if (HasDeviceFreeTrigger(source))
                return (TranslationStatus.Partial, TranslationReasons.MacroTriggerViaXboxOutput, null);
            if (feature != null)
                return (TranslationStatus.Partial, TranslationReasons.TrackpadFeatureRequired, feature);
            return (TranslationStatus.Clean, TranslationReasons.MacroEmitted, null);
        }

        /// <summary>The activator toggle on an xinput binding (wave 2A): a
        /// ToggleVcButton latch macro. Two trigger shapes:
        /// <paramref name="rowKept"/> = true (press-type activators) keeps
        /// the Wave-1 momentary row and fires the latch on the TARGET bit's
        /// press edge, which that row feeds whenever the physical input is
        /// pressed (the only self-contained device-free trigger; proven
        /// necessary on 2774979654, where the host input carries no other
        /// binding). Reported Partial: the row re-asserts the target during
        /// the unlatching press, and other feeders of the same target also
        /// flip the latch. <paramref name="rowKept"/> = false (Long_Press,
        /// which never emits rows) fires on the SOURCE's identity through
        /// the standard device-free gate and reports Clean.</summary>
        private bool EmitVcToggleMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, XInputTargetTable.XInputTarget xt, bool rowKept, string inputName,
            int holdMs = 0)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Toggle {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.ToggleVcButton,
                TriggerMode = holdMs > 0 ? "HoldForMs" : "OnPress",
                TriggerHoldMs = holdMs,
                TargetXboxButtons = xt.XboxButtonBit,
                ConsumeTrigger = false,
            };
            string feature = null;
            if (rowKept)
            {
                macro.TriggerXboxButtons = xt.XboxButtonBit;
                macro.TriggerAxisTarget = "";
            }
            else
            {
                feature = FillMacroTrigger(macro, source);
            }
            run.Profile.Macros.Add(macro);
            // rowKept latches stay Partial (the kept row re-asserts the
            // target during the unlatching press); descriptor-triggered
            // latches are Partial only when the trigger needs a
            // Touchpad-tab feature.
            run.Report.Add(rowKept || feature != null
                    ? TranslationStatus.Partial : TranslationStatus.Clean,
                TranslationReasons.ToggleLatchEmitted,
                path, binding.Raw, emitted: $"Toggle {xt.Target} latch macro", xt.Target);
            return true;
        }

        /// <summary>The activator toggle on a key binding (wave 2A): a
        /// ToggleKey latch macro replacing the momentary row.</summary>
        private bool EmitKeyToggleMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, byte vk, string keyName, bool onRelease, string inputName, int holdMs = 0)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Toggle {keyName} ({inputName})",
                Action = TranslatedMacroAction.ToggleKey,
                TriggerMode = holdMs > 0 ? "HoldForMs" : (onRelease ? "OnRelease" : "OnPress"),
                TriggerHoldMs = holdMs,
                ConsumeTrigger = false,
                VirtualKey = vk,
            };
            string feature = FillMacroTrigger(macro, source);
            run.Profile.Macros.Add(macro);
            run.Report.Add(feature != null ? TranslationStatus.Partial : TranslationStatus.Clean,
                TranslationReasons.ToggleLatchEmitted,
                path, binding.Raw, emitted: $"Toggle {keyName} latch macro", keyName);
            return true;
        }

        /// <summary>hold_repeats on an xinput binding (wave 2A): a
        /// RepeatVcButtonWhileHeld turbo macro pulsing the target at
        /// repeat_rate ms while the physical input is held (from the
        /// Long_Press threshold when <paramref name="holdMs"/> is set).
        /// Long_Press turbo consumes its trigger bits, approximating
        /// Steam's interruptable pause of same-input activators once a
        /// long press fires.</summary>
        private bool EmitVcTurboMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, XInputTargetTable.XInputTarget xt, int intervalMs, int holdMs,
            string inputName)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Turbo {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.RepeatVcButtonWhileHeld,
                TriggerMode = holdMs > 0 ? "HoldForMs" : "WhileHeld",
                TriggerHoldMs = holdMs,
                TargetXboxButtons = xt.XboxButtonBit,
                ConsumeTrigger = holdMs > 0,
                IntervalMs = intervalMs,
            };
            string feature = FillMacroTrigger(macro, source);
            run.Profile.Macros.Add(macro);
            var (status, reason, arg) = MacroTriggerReport(source, feature);
            run.Report.Add(status, reason,
                path, binding.Raw, emitted: $"Turbo {xt.Target} macro ({intervalMs} ms)",
                args: arg == null ? Array.Empty<string>() : new[] { arg });
            return true;
        }

        /// <summary>A plain Long_Press xinput binding (wave 2A): the target
        /// button engages at the hold threshold and stays down until the
        /// physical input releases, Valve's documented Long_Press shape.
        /// Consumes its trigger bits while active (the interruptable-pause
        /// approximation, same as the turbo variant).</summary>
        private bool EmitVcHoldMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, XInputTargetTable.XInputTarget xt, int holdMs, string inputName)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Long press {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.HoldVcButton,
                TriggerMode = "HoldForMs",
                TriggerHoldMs = holdMs,
                TargetXboxButtons = xt.XboxButtonBit,
                ConsumeTrigger = true,
            };
            string feature = FillMacroTrigger(macro, source);
            run.Profile.Macros.Add(macro);
            var (status, reason, arg) = MacroTriggerReport(source, feature);
            run.Report.Add(status, reason,
                path, binding.Raw, emitted: $"Long-press hold macro: {xt.Target}",
                args: arg == null ? Array.Empty<string>() : new[] { arg });
            return true;
        }

        private void TranslateModeShift(Run run, SteamInputPreset preset, SteamInputBinding binding,
            ResolvedSource source, string path, int activatorDelayMs = 0, bool toggle = false)
        {
            // Param: "{slot} {groupId}". The layer holds the groups the
            // preset marks "{slot} active modeshift".
            var tokens = (binding.Param ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2
                || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int groupId))
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                    path, binding.Raw, args: $"mode_shift {binding.Param}");
                return;
            }
            string slotToken = tokens[0];

            bool presentInPreset = preset.GroupSourceBindings.TryGetValue(groupId, out var sourceBinding)
                && (sourceBinding ?? "").Contains("modeshift", StringComparison.OrdinalIgnoreCase)
                && (sourceBinding ?? "").StartsWith(slotToken, StringComparison.OrdinalIgnoreCase);
            if (!presentInPreset || !run.GroupsById.ContainsKey(groupId))
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.MissingModeShiftGroup,
                    path, binding.Raw, args: new[] { slotToken, groupId.ToString(CultureInfo.InvariantCulture) });
                return;
            }

            if (!IsActivatorCapable(source))
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.ActivatorInputNotSupported,
                    path, binding.Raw);
                return;
            }

            string layerMask = ModeShiftLayer(run, preset.Id, slotToken, groupId);
            run.Activators.Add(new ActivatorRequest
            {
                LayerMask = layerMask,
                LayerName = $"{slotToken} shift",
                // The activator toggle setting latches the shift instead of
                // holding it (wave 2A); the engine's Toggle mode is the
                // same construct.
                Mode = toggle ? "Toggle" : "Hold",
                InheritUnmapped = true, // mode shift overlays the slot; everything else keeps working
                DelayMs = activatorDelayMs,
                Descriptor = source.Descriptor,
                // Button kind even for trigger pulls: the button-like
                // activator read thresholds the raw axis at 50% of
                // full range, which is a half pull on a unipolar
                // trigger. The Axis kind tests |bipolar| >= 0.5 and a
                // trigger RESTS at bipolar -1, so it would engage the
                // layer permanently.
                Kind = "Button",
                Path = path,
            });
        }

        private void TranslateControllerAction(Run run, SteamInputPreset preset,
            SteamInputBinding binding, ResolvedSource source, string layer, string path,
            bool onRelease, string inputName, int activatorDelayMs = 0, bool toggle = false)
        {
            var tokens = (binding.Param ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string action = tokens.Length > 0 ? tokens[0] : "";

            switch (action.ToUpperInvariant())
            {
                case "MOUSE_POSITION":
                {
                    if (tokens.Length < 3
                        || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nx)
                        || !int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ny))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnsupportedControllerAction,
                            path, binding.Raw, args: binding.Param);
                        return;
                    }
                    var warp = new TranslatedMacro
                    {
                        Name = $"Warp cursor ({inputName})",
                        Action = TranslatedMacroAction.MoveMouseToScreenPosition,
                        TriggerMode = onRelease ? "OnRelease" : "OnPress",
                        ConsumeTrigger = false,
                        NormalizedX = Math.Clamp(nx, 0, 65535),
                        NormalizedY = Math.Clamp(ny, 0, 65535),
                    };
                    string warpFeature = FillMacroTrigger(warp, source);
                    run.Profile.Macros.Add(warp);
                    var (warpStatus, warpReason, warpArg) = MacroTriggerReport(source, warpFeature);
                    run.Report.Add(warpStatus, warpReason,
                        path, binding.Raw, emitted: "Cursor warp macro",
                        args: warpArg == null ? Array.Empty<string>() : new[] { warpArg });
                    return;
                }

                case "ADD_LAYER":
                case "HOLD_LAYER":
                case "REMOVE_LAYER":
                {
                    // add_layer/hold_layer engage an action layer (a preset).
                    if (tokens.Length < 2
                        || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int presetIndex))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnsupportedControllerAction,
                            path, binding.Raw, args: binding.Param);
                        return;
                    }
                    if (action.Equals("REMOVE_LAYER", StringComparison.OrdinalIgnoreCase))
                    {
                        // PadForge's Toggle disengages on the SAME input; a
                        // separate remove binding has no direct equivalent.
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.RemoveLayerApproximated,
                            path, binding.Raw);
                        return;
                    }
                    if (!TryResolvePresetIndex(run, presetIndex, out int presetId))
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.MissingPreset,
                            path, binding.Raw, args: presetIndex.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                    if (!IsActivatorCapable(source))
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.ActivatorInputNotSupported,
                            path, binding.Raw);
                        return;
                    }
                    run.Activators.Add(new ActivatorRequest
                    {
                        LayerMask = $"Layer_{run.Options.FileId}_{presetId}",
                        LayerName = PresetLayerName(run, presetId),
                        // add_layer latches by nature; hold_layer holds
                        // unless the activator's toggle setting latches it
                        // (wave 2A).
                        Mode = action.Equals("HOLD_LAYER", StringComparison.OrdinalIgnoreCase) && !toggle
                            ? "Hold" : "Toggle",
                        InheritUnmapped = true, // Steam action layers overlay the set below
                        DelayMs = activatorDelayMs,
                        Descriptor = source.Descriptor,
                        // Button kind even for trigger pulls: the button-like
                        // activator read thresholds the raw axis at 50% of
                        // full range, which is a half pull on a unipolar
                        // trigger. The Axis kind tests |bipolar| >= 0.5 and a
                        // trigger RESTS at bipolar -1, so it would engage the
                        // layer permanently.
                        Kind = "Button",
                        Path = path,
                    });
                    return;
                }

                case "CHANGE_PRESET":
                {
                    if (tokens.Length < 2
                        || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int presetIndex))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnsupportedControllerAction,
                            path, binding.Raw, args: binding.Param);
                        return;
                    }
                    if (!TryResolvePresetIndex(run, presetIndex, out int presetId))
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.MissingPreset,
                            path, binding.Raw, args: presetIndex.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                    if (!IsActivatorCapable(source))
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.ActivatorInputNotSupported,
                            path, binding.Raw);
                        return;
                    }
                    bool toBase = run.BasePresetId.HasValue && presetId == run.BasePresetId.Value;
                    run.Activators.Add(new ActivatorRequest
                    {
                        LayerMask = $"Jump_{run.Options.FileId}_{presetId}",
                        LayerName = PresetLayerName(run, presetId),
                        Mode = "Custom",
                        JumpToLayer = toBase ? "Base" : $"Layer_{run.Options.FileId}_{presetId}",
                        InheritUnmapped = false, // action sets replace
                        Descriptor = source.Descriptor,
                        // Button kind even for trigger pulls: the button-like
                        // activator read thresholds the raw axis at 50% of
                        // full range, which is a half pull on a unipolar
                        // trigger. The Axis kind tests |bipolar| >= 0.5 and a
                        // trigger RESTS at bipolar -1, so it would engage the
                        // layer permanently.
                        Kind = "Button",
                        Path = path,
                    });
                    return;
                }

                case "SET_LED":
                {
                    // set_led r g b brightness saturation setting. Arg
                    // order verified against the corpus: 1451857916 (2018,
                    // "0 255 0 100 255 1", saturation on the vintage 0-255
                    // scale) and 3353604014 (2024, "255 0 0 43 100 1",
                    // saturation 0-100). Brightness is 0-100 in both eras;
                    // a saturation above 100 marks the vintage scale.
                    if (tokens.Length < 7
                        || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)
                        || !int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int g)
                        || !int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b)
                        || !int.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bright)
                        || !int.TryParse(tokens[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sat)
                        || !int.TryParse(tokens[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ledSetting)
                        || ledSetting < 0 || ledSetting > 2)
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnsupportedControllerAction,
                            path, binding.Raw, args: binding.Param);
                        return;
                    }
                    int satPct = sat > 100 ? (int)Math.Round(sat * 100.0 / 255.0) : sat;
                    var led = new TranslatedMacro
                    {
                        Name = $"Set LED ({inputName})",
                        Action = TranslatedMacroAction.SetLightbarColor,
                        TriggerMode = onRelease ? "OnRelease" : "OnPress",
                        ConsumeTrigger = false,
                        LedR = Math.Clamp(r, 0, 255),
                        LedG = Math.Clamp(g, 0, 255),
                        LedB = Math.Clamp(b, 0, 255),
                        LedBrightnessPercent = Math.Clamp(bright, 0, 100),
                        LedSaturationPercent = Math.Clamp(satPct, 0, 100),
                        LedSetting = ledSetting,
                    };
                    string ledFeature = FillMacroTrigger(led, source);
                    run.Profile.Macros.Add(led);
                    if (ledSetting == 2)
                    {
                        // "Restore default lighting" has no PadForge verb;
                        // the materializer approximates it as clearing the
                        // override.
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.SetLedDefaultApproximated,
                            path, binding.Raw);
                    }
                    var (ledStatus, ledReason, ledArg) = MacroTriggerReport(source, ledFeature);
                    run.Report.Add(ledStatus, ledReason,
                        path, binding.Raw, emitted: "Set LED macro",
                        args: ledArg == null ? Array.Empty<string>() : new[] { ledArg });
                    return;
                }

                case "CAMERA_RESET":
                {
                    // controller_action camera_reset {yaw} {pitch} {speed}
                    // (Valve's shipped gyro/flick-stick templates carry
                    // "camera_reset 180 66 90"). Steam re-levels the in-game
                    // camera through calibrated mouse motion ("Reset the
                    // camera to the Horizon ... requires the Dots Per 360°
                    // setting"); PadForge has no dots-per-360 channel, so the
                    // nearest primitive is the gyro-recenter macro, which
                    // re-references the slot's gyro aim state (wave 2A,
                    // Partial). The numeric args calibrate Steam's camera
                    // surgery and are dropped.
                    var macro = new TranslatedMacro
                    {
                        Name = $"Recenter gyro ({inputName})",
                        Action = TranslatedMacroAction.GyroRecenter,
                        TriggerMode = activatorDelayMs > 0 ? "HoldForMs"
                            : onRelease ? "OnRelease" : "OnPress",
                        TriggerHoldMs = activatorDelayMs,
                        ConsumeTrigger = false,
                    };
                    // The action itself approximates (gyro recenter for
                    // Steam's calibrated camera surgery), so the entry
                    // stays Partial whatever the trigger shape.
                    FillMacroTrigger(macro, source);
                    run.Profile.Macros.Add(macro);
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.CameraResetApproximated,
                        path, binding.Raw, emitted: "Gyro recenter macro");
                    return;
                }

                case "CHANGE_PLAYER_NUMBER":
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.PlayerNumberActionNotSupported,
                        path, binding.Raw);
                    return;

                case "TOGGLE_LIZARD_MODE":
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.LizardModeActionNotSupported,
                        path, binding.Raw);
                    return;

                case "SCREENSHOT":
                case "SYSTEM_KEY_1":
                case "SHOW_KEYBOARD":
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.SteamSystemAction,
                        path, binding.Raw, args: action);
                    return;

                case "EMPTY_SUB_COMMAND":
                    return; // placeholder, silent

                default:
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnsupportedControllerAction,
                        path, binding.Raw, args: action);
                    return;
            }
        }

        private static string PresetLayerName(Run run, int presetId)
            => run.PresetNames.TryGetValue(presetId, out var n) ? n :
                (run.Config.Presets.FirstOrDefault(p => p.Id == presetId)?.Name ?? $"Preset {presetId}");

        /// <summary>CHANGE_PRESET / add_layer / hold_layer reference presets
        /// by 1-BASED INDEX in id order, not by preset id. Corpus ground
        /// truth: 708227783 carries CHANGE_PRESET 1 and 2 over presets
        /// {0, 1}, and 3451446931 carries hold_layer 2 over {0, 1}; the
        /// index reading resolves every in-corpus reference, the id reading
        /// leaves danglers in three fixtures.</summary>
        private static bool TryResolvePresetIndex(Run run, int oneBasedIndex, out int presetId)
        {
            presetId = -1;
            if (oneBasedIndex < 1) return false;
            var ordered = run.Config.Presets.OrderBy(p => p.Id).ToList();
            if (oneBasedIndex > ordered.Count) return false;
            presetId = ordered[oneBasedIndex - 1].Id;
            return true;
        }

        /// <summary>Shift activators read button-like inputs (or an axis
        /// with a threshold). Gesture-gated trackpad wedges can't drive one.</summary>
        private static bool IsActivatorCapable(ResolvedSource source)
            => source != null
            && string.IsNullOrEmpty(source.TrackpadFeature)
            && !source.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal);

        // ─────────────────────────────────────────────
        //  Row accumulation
        // ─────────────────────────────────────────────

        private static TranslationStatus StatusFor(ResolvedSource source, bool soft)
            => source.TrackpadFeature != null ? TranslationStatus.Partial
             : source.PartialReasonKey != null ? TranslationStatus.Partial
             : soft ? TranslationStatus.Partial
             : TranslationStatus.Clean;

        private static string ReasonFor(ResolvedSource source, bool soft)
            => source.TrackpadFeature != null ? TranslationReasons.TrackpadFeatureRequired
             : source.PartialReasonKey != null ? source.PartialReasonKey
             : soft ? TranslationReasons.SoftPressApproximated
             : TranslationReasons.RowEmitted;

        private static MappingSource BuildSource(ResolvedSource source, bool soft)
        {
            var src = new MappingSource
            {
                Descriptor = source.Descriptor,
                HalfAxis = source.HalfAxis,
                Invert = source.Invert,
            };
            if (source.DeadZone > 0) src.DeadZone = source.DeadZone;
            if (soft && source.IsAnalogTriggerPull) src.DeadZone = 15;
            return src;
        }

        private void EmitSourceRow(Run run, bool isKbm, string layer, string target,
            ResolvedSource source, string clickGate, bool isAxis, bool soft,
            string path, string binding)
        {
            var src = BuildSource(source, soft);
            // The AND companion: either the group-level requires_click gate
            // (trackpad D-pad wedges) or the source's own GateDescriptor
            // (a single-pad click gated on its half's touch spot, #9 B-1).
            // They never co-occur: wedges carry no GateDescriptor and the
            // gated clicks are never wedge members.
            string gateDescriptor = clickGate ?? source.GateDescriptor;
            MappingSource gate = gateDescriptor != null
                ? new MappingSource { Descriptor = gateDescriptor } : null;
            AddRowSource(run, isKbm, layer, target, src, isAxis,
                StatusFor(source, soft), ReasonFor(source, soft), path, binding,
                args: source.TrackpadFeature, clickGate: gate);
        }

        private void AddRowSource(Run run, bool isKbm, string layer, string target,
            MappingSource src, bool isAxis, TranslationStatus status, string reason,
            string path, string binding = "", string args = null, MappingSource clickGate = null)
        {
            if (isKbm ? run.KbmRowCapHit : run.XboxRowCapHit) return;
            int slotRows = run.RowOrder.Count(k => k.Kbm == isKbm);
            var key = (isKbm, layer, target);
            if (!run.Rows.TryGetValue(key, out var row))
            {
                if (slotRows >= MaxRowsPerSlot)
                {
                    if (isKbm) run.KbmRowCapHit = true; else run.XboxRowCapHit = true;
                    run.Report.Add(TranslationStatus.Error, TranslationReasons.RowCapExceeded,
                        path, args: isKbm ? "KeyboardMouse" : "Xbox");
                    return;
                }
                row = new PendingRow { IsAxis = isAxis };
                run.Rows[key] = row;
                run.RowOrder.Add(key);
            }

            if (clickGate != null && row.Sources.Count == 0 && row.ClickGate == null)
            {
                row.ClickGate = clickGate;
                row.ClickGatePath = path;
            }
            else if (clickGate != null || (row.ClickGate != null && row.Sources.Count > 0))
            {
                // A second feed joined a click-gated target (or vice versa):
                // AND across unrelated sources would break them all, so the
                // gate is dropped.
                if (row.ClickGate != null)
                {
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.ClickGateDropped,
                        row.ClickGatePath ?? path);
                    row.ClickGate = null;
                    row.ClickGatePath = null;
                }
            }

            row.Sources.Add(src);

            string emitted = $"{target} <- {src.Descriptor}"
                + (src.HalfAxis ? (src.Invert ? " (lower half)" : " (upper half)") : src.Invert ? " (inverted)" : "");
            run.Report.Add(status, reason, path, binding, emitted,
                args == null ? Array.Empty<string>() : new[] { args });
        }

        private static void ReportSkipUnlessSilent(Run run, string reason, string path,
            SteamInputBinding binding, string slotArg = null, string inputArg = null)
        {
            // empty bindings and Steam-internal fillers add noise, not signal.
            string t = (binding.Type ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return;
            if (t == "controller_action"
                && (binding.Param ?? "").StartsWith("empty_sub_command", StringComparison.OrdinalIgnoreCase))
                return;
            var args = new List<string>();
            if (slotArg != null) args.Add(slotArg);
            if (inputArg != null) args.Add(inputArg);
            run.Report.Add(TranslationStatus.Skipped, reason, path, binding.Raw,
                args: args.ToArray());
        }

        // ─────────────────────────────────────────────
        //  Finalize
        // ─────────────────────────────────────────────

        /// <summary>Rescues macros whose trigger rides an Xbox output bit that
        /// nothing feeds.
        ///
        /// <para>A combined-output trigger is indirect by design: it fires when
        /// the SLOT's output button goes down, whoever drove it. That is a
        /// feature when the hosting input also emits a row (the Wave-2A xinput
        /// toggle keeps its row deliberately), and a dead end when it does not.
        /// The macro-backed key forms return without emitting a row for their
        /// own source (autofire, on-release), so a config whose button carries
        /// ONLY such a binding produced a macro triggering on a bit with no
        /// feeder: the imported profile looked complete and the macro could
        /// never fire. Imported sets are authoritative, so the legacy automap
        /// never fills that gap in either.</para>
        ///
        /// <para>The hosting input's own descriptor was stashed at trigger-fill
        /// time, so the rescue is a swap to the Wave-3 device-free descriptor
        /// trigger, which reads the physical input directly. Only macros whose
        /// bit has no feeder are touched: a trigger that IS fed keeps the
        /// cheaper consume-capable combined-output shape.</para></summary>
        private static void FinalizeMacroTriggers(Run run)
        {
            var profile = run.Profile;
            if (profile.Macros.Count == 0) return;

            // A ZERO-row Xbox set is not a dead end: an empty set does not
            // replace the legacy mapping at runtime, so the slot's automap
            // still drives every Xbox bit from the physical pad and a
            // combined-output trigger fires normally. That is the documented
            // macro-only shape ("a macro-only config keeps its zero-row set
            // riding the whole-set legacy passthrough its triggers depend on",
            // above). The trap is the NON-EMPTY authoritative set: it
            // suppresses the passthrough, so only its own rows feed anything,
            // and a bit with no row is unreachable.
            if (profile.XboxMappingSet.Rows.Count == 0) return;

            // Every Xbox output some emitted row actually drives. Both trigger
            // shapes FillMacroTrigger can emit are collected here: the button
            // bitmask AND the analog-trigger target name. A combined-output
            // macro trigger is unreachable the same way in either shape, and
            // the axis shape is the one an autofire on a trigger pull takes.
            ushort fedBits = 0;
            var fedAxes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in profile.XboxMappingSet.Rows)
            {
                if (row?.Sources == null || row.Sources.Count == 0) continue;
                fedBits |= XInputTargetTable.BitForTarget(row.Target);
                if (!string.IsNullOrEmpty(row.Target)) fedAxes.Add(row.Target);
            }

            foreach (var m in profile.Macros)
            {
                if (m == null) continue;
                // Already a device-free descriptor trigger (it reads the
                // physical input directly), so there is no output bit to be
                // unreachable. Both combined-output shapes fall through.
                bool isButtonTrigger = m.TriggerXboxButtons != 0;
                bool isAxisTrigger = !string.IsNullOrEmpty(m.TriggerAxisTarget);
                if (!isButtonTrigger && !isAxisTrigger) continue;
                // Fed by at least one row: the indirect trigger works.
                if (isButtonTrigger && (m.TriggerXboxButtons & fedBits) != 0) continue;
                if (isAxisTrigger && fedAxes.Contains(m.TriggerAxisTarget)) continue;
                // Unfed, and no descriptor to fall back to: leave it alone and
                // let the existing report entry stand rather than silently
                // producing a trigger-less macro.
                if (string.IsNullOrEmpty(m.TriggerFallbackDescriptor)) continue;

                m.TriggerXboxButtons = 0;
                m.TriggerAxisTarget = "";
                m.TriggerInputDescriptors.Clear();
                m.TriggerInputDescriptors.Add(m.TriggerFallbackDescriptor);
                if (!string.IsNullOrEmpty(m.TriggerFallbackGateDescriptor))
                    m.TriggerInputDescriptors.Add(m.TriggerFallbackGateDescriptor);
                // Descriptor triggers read the physical input, not an output
                // bit, so there are no bits to consume.
                m.ConsumeTrigger = false;

                run.Report.Add(TranslationStatus.Partial,
                    TranslationReasons.MacroTriggerRetargetedToInput,
                    "config", emitted: m.Name, args: m.TriggerFallbackDescriptor);
            }
        }

        private TranslatedProfile Finalize(Run run)
        {
            var profile = run.Profile;
            profile.Name = !string.IsNullOrWhiteSpace(run.Options.ProfileNameOverride)
                ? run.Options.ProfileNameOverride
                : ResolveText(run, run.Config.Title, "title", "Steam Workshop Config");
            profile.Description = ResolveText(run, run.Config.Description, "description", "");
            run.Report.ConfigTitle = profile.Name;

            // Matched-side implicit analog outputs (trigger pulls, stick
            // axis pairs) become explicit rows, gated on the Xbox side
            // being in play at all through bindings: a pure keyboard/mouse
            // config keeps zero Xbox rows (no phantom Xbox slot), and a
            // macro-only config keeps its zero-row set riding the
            // whole-set legacy passthrough its triggers depend on. Runs
            // BEFORE the identity pass so a trigger's click identity
            // absorbs behind the direct analog source.
            bool xboxInPlay = run.Identities.Count > 0 || run.RowOrder.Any(k => !k.Kbm);
            if (xboxInPlay)
            {
                foreach (var ma in run.MatchedAnalogs)
                {
                    var src = new MappingSource { Descriptor = ma.Descriptor };
                    if (ma.DeadZonePct > 0) src.DeadZone = ma.DeadZonePct;
                    AddRowSource(run, isKbm: false, ma.Layer, ma.Target,
                        src, isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, ma.Path);
                    if (run.Rows.TryGetValue((false, ma.Layer, ma.Target), out var pr))
                        pr.HasMatchedPassthrough = true;
                }
            }

            // Identity bindings: the ones whose target got a real row from
            // a divergent binding join that row as extra sources. The rest
            // become explicit identity rows. Imported sets are
            // authoritative, the automap never adds to them, so nothing may
            // stay implicit.
            foreach (var id in run.Identities)
            {
                var key = (false, id.Layer, id.Target);
                if (run.Rows.TryGetValue(key, out var row))
                {
                    var src = BuildSource(id.Source, soft: false);
                    row.Sources.Add(src);
                    run.Report.Add(TranslationStatus.Clean, TranslationReasons.RowEmitted,
                        id.Path, id.Binding, $"{id.Target} <- {src.Descriptor}");
                }
                else
                {
                    // Registers the row in run.Rows/RowOrder (so a later
                    // identity for the same target absorbs above) and emits
                    // the Clean RowEmitted entry.
                    AddRowSource(run, isKbm: false, id.Layer, id.Target,
                        BuildSource(id.Source, soft: false), id.IsAxis,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted,
                        id.Path, id.Binding);
                }
            }

            // Materialize rows, deterministically ordered (Base first, then
            // layers by name; targets ordinal within a layer).
            foreach (var key in run.RowOrder
                .OrderBy(k => k.Kbm)
                .ThenBy(k => k.Layer == "Base" ? 0 : 1)
                .ThenBy(k => k.Layer, StringComparer.Ordinal)
                .ThenBy(k => k.Target, StringComparer.Ordinal))
            {
                var pending = run.Rows[key];
                if (pending.Sources.Count == 0) continue;

                var row = new MappingRow { Target = key.Target, LayerMask = key.Layer };
                if (pending.ClickGate != null && pending.Sources.Count == 1)
                {
                    row.Sources.Add(pending.Sources[0]);
                    row.Sources.Add(pending.ClickGate);
                    row.CombineMode = "AND";
                }
                else
                {
                    if (pending.ClickGate != null)
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.ClickGateDropped,
                            pending.ClickGatePath ?? "");
                    }
                    row.Sources.AddRange(pending.Sources);
                    if (pending.Sources.Count > 1 && pending.IsAxis && !pending.HasMatchedPassthrough)
                        row.CombineMode = "Sum"; // mouse deltas and merged axes are additive
                    // Rows carrying a matched analog passthrough keep the
                    // axis default (max-abs), so extra legs (a click
                    // identity, a bumper-as-trigger binding) ride on top of
                    // a clean analog pull instead of summing into overdrive.
                    // Multi-source buttons keep the engine's OR default.
                }

                (key.Kbm ? profile.KbmMappingSet : profile.XboxMappingSet).Rows.Add(row);
            }

            EmitActivators(run);
            ReportActivatorlessPresets(run);

            // Rows are final, so a combined-output macro trigger can now be
            // checked against what actually feeds it. Must run before the
            // counts and NeedsXboxSlot below: rewriting a trigger off the Xbox
            // output can be what decides a macro no longer needs an Xbox slot.
            FinalizeMacroTriggers(run);

            // Haptic feedback has no PadForge channel; one aggregate note
            // per config (49 per-binding entries in one corpus fixture
            // would drown the report).
            if (run.HapticDropCount > 0)
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.HapticIntensityDropped,
                    "config", args: run.HapticDropCount.ToString(CultureInfo.InvariantCulture));
            }

            run.Report.XboxRowCount = profile.XboxMappingSet.Rows.Count;
            run.Report.KbmRowCount = profile.KbmMappingSet.Rows.Count;
            run.Report.MacroCount = profile.Macros.Count;
            run.Report.MenuCount = profile.Menus.Count;
            run.Report.ShiftActivatorCount =
                profile.XboxMappingSet.ShiftActivators.Count + profile.KbmMappingSet.ShiftActivators.Count;

            // Slot demand (owner report 2026-07-13: a pure keyboard config
            // imported with an empty Xbox VC). The Xbox slot is needed for
            // rows/activators, for macros whose triggers read the Xbox
            // slot's combined output, and for macros whose ACTIONS write
            // virtual-controller buttons. Wave 3's device-free InputDevice
            // triggers read the slot's physical device directly, so a
            // key-natured macro riding one (a touchpad key latch on a
            // keyboard-only config) no longer forces an Xbox slot into
            // existence. Identity bindings now materialize as rows, so the
            // row count covers them; the Identities clause stays as
            // belt-and-braces (a row-cap overflow could drop an identity
            // row, and the slot must still exist for it).
            profile.NeedsXboxSlot = profile.XboxMappingSet.Rows.Count > 0
                || profile.XboxMappingSet.ShiftActivators.Count > 0
                || run.Identities.Count > 0
                || profile.Macros.Any(MacroNeedsXboxSlot);
            // Every macro still needs SOME slot to evaluate against (its
            // device-free entries resolve that slot's devices); when no
            // Xbox slot is demanded, macros ride the KbM slot, so a
            // key-latch-only config materializes one. Menus (#9 B-17)
            // follow the same rule: their definitions live on a slot's
            // PadSetting and their runtime resolves that slot's devices.
            profile.NeedsKbmSlot = profile.KbmMappingSet.Rows.Count > 0
                || profile.KbmMappingSet.ShiftActivators.Count > 0
                || (!profile.NeedsXboxSlot
                    && (profile.Macros.Count > 0 || profile.Menus.Count > 0));
            return profile;
        }

        /// <summary>True when a macro depends on the Xbox slot existing:
        /// its trigger reads the slot's combined output (no descriptor
        /// entries), or its action writes virtual-controller buttons
        /// (turbo / latch / hold), which only an Xbox-class slot renders.
        /// Key, cursor, LED, and gyro actions on a device-free descriptor
        /// trigger run against whichever slot hosts them.</summary>
        private static bool MacroNeedsXboxSlot(TranslatedMacro m)
            => m.TriggerInputDescriptors == null
            || m.TriggerInputDescriptors.Count == 0
            || m.Action == TranslatedMacroAction.RepeatVcButtonWhileHeld
            || m.Action == TranslatedMacroAction.ToggleVcButton
            || m.Action == TranslatedMacroAction.HoldVcButton;

        private void EmitActivators(Run run)
        {
            MergeSameInputJumpsIntoCycles(run);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var req in run.Activators
                .OrderBy(a => a.LayerMask, StringComparer.Ordinal)
                .ThenBy(a => a.Descriptor, StringComparer.Ordinal)
                .ThenBy(a => a.Mode, StringComparer.Ordinal))
            {
                if (!seen.Add($"{req.LayerMask}|{req.Descriptor}|{req.Mode}|{req.JumpToLayer}|{req.CycleLayers}")) continue;

                bool xboxHas, kbmHas;
                if (req.Mode == "Cycle")
                {
                    var stops = req.CycleLayers.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    xboxHas = stops.Any(l => LayerHasRows(run.Profile.XboxMappingSet, l));
                    kbmHas = stops.Any(l => LayerHasRows(run.Profile.KbmMappingSet, l));
                }
                else
                {
                    string engagedLayer = req.Mode == "Custom" ? req.JumpToLayer : req.LayerMask;
                    xboxHas = LayerHasRows(run.Profile.XboxMappingSet, engagedLayer);
                    kbmHas = LayerHasRows(run.Profile.KbmMappingSet, engagedLayer);
                    if (engagedLayer == "Base")
                    {
                        // "Return to base" applies wherever any layer rows exist.
                        xboxHas = run.Profile.XboxMappingSet.Rows.Any(r => (r.LayerMask ?? "Base") != "Base");
                        kbmHas = run.Profile.KbmMappingSet.Rows.Any(r => (r.LayerMask ?? "Base") != "Base");
                    }
                }
                if (!xboxHas && !kbmHas)
                {
                    // The layer produced no rows (its bindings' own entries
                    // say why); note the dropped switch instead of silence.
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.ShiftLayerEmpty,
                        req.Path, args: req.LayerName);
                    continue;
                }

                var act = new ShiftActivator
                {
                    Descriptor = req.Descriptor,
                    Mode = req.Mode,
                    LayerMask = req.LayerMask,
                    LayerName = req.LayerName,
                    JumpToLayer = req.JumpToLayer,
                    InheritUnmapped = req.InheritUnmapped,
                    Kind = req.Kind,
                    AxisThreshold = req.AxisThreshold,
                    DelayMs = req.DelayMs,
                    CycleLayers = req.CycleLayers,
                    CycleIncludeBase = req.CycleIncludeBase,
                };
                if (xboxHas) run.Profile.XboxMappingSet.ShiftActivators.Add(Clone(act));
                if (kbmHas) run.Profile.KbmMappingSet.ShiftActivators.Add(Clone(act));

                string engagedText = req.Mode switch
                {
                    "Custom" => req.JumpToLayer,
                    "Cycle" => req.CycleLayers + (req.CycleIncludeBase ? "|Base" : ""),
                    _ => req.LayerMask,
                };
                run.Report.Add(TranslationStatus.Clean, TranslationReasons.ShiftLayerEmitted,
                    req.Path, emitted: $"{req.Mode} -> {engagedText}",
                    args: req.LayerName);
            }
        }

        /// <summary>CHANGE_PRESET pairs (each preset's copy of the same
        /// button jumping to the other preset) can't ride two always-on
        /// Custom activators: both would fire on every press and the later
        /// write would win, so the button could never come back. The
        /// engine's Cycle mode is the exact construct for a same-button
        /// preset rotation, so same-descriptor Custom jumps merge into one
        /// Cycle through the non-Base stops (Base joins the ring when one
        /// of the jumps targeted it).</summary>
        private static void MergeSameInputJumpsIntoCycles(Run run)
        {
            var jumpGroups = run.Activators
                .Where(a => a.Mode == "Custom")
                .GroupBy(a => $"{a.Kind}|{a.Descriptor}", StringComparer.Ordinal)
                .Where(g => g.Select(a => a.JumpToLayer).Distinct(StringComparer.Ordinal).Count() > 1)
                .ToList();

            foreach (var g in jumpGroups)
            {
                var members = g.ToList();
                foreach (var m in members) run.Activators.Remove(m);

                var stops = members.Select(m => m.JumpToLayer)
                    .Where(l => l != "Base")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(l => l, StringComparer.Ordinal)
                    .ToList();
                bool includeBase = members.Any(m => m.JumpToLayer == "Base");
                if (stops.Count == 0) continue; // all jumps to Base: nothing to cycle

                var first = members[0];
                run.Activators.Add(new ActivatorRequest
                {
                    LayerMask = stops[0],
                    LayerName = string.Join(" / ", members
                        .Select(m => m.LayerName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Distinct(StringComparer.Ordinal)),
                    Mode = "Cycle",
                    Descriptor = first.Descriptor,
                    Kind = first.Kind,
                    AxisThreshold = first.AxisThreshold,
                    CycleLayers = string.Join("|", stops),
                    CycleIncludeBase = includeBase,
                    Path = first.Path,
                });
            }
        }

        private static ShiftActivator Clone(ShiftActivator a) => new()
        {
            Descriptor = a.Descriptor,
            Mode = a.Mode,
            LayerMask = a.LayerMask,
            LayerName = a.LayerName,
            JumpToLayer = a.JumpToLayer,
            InheritUnmapped = a.InheritUnmapped,
            Kind = a.Kind,
            AxisThreshold = a.AxisThreshold,
            DelayMs = a.DelayMs,
            CycleLayers = a.CycleLayers,
            CycleIncludeBase = a.CycleIncludeBase,
        };

        private static bool LayerHasRows(MappingSet set, string layer)
            => !string.IsNullOrEmpty(layer)
            && set.Rows.Any(r => string.Equals(r.LayerMask ?? "Base", layer, StringComparison.Ordinal));

        private void ReportActivatorlessPresets(Run run)
        {
            foreach (var kv in run.LayerByPreset.OrderBy(k => k.Key))
            {
                if (kv.Value == "Base") continue;
                bool hasRows = LayerHasRows(run.Profile.XboxMappingSet, kv.Value)
                    || LayerHasRows(run.Profile.KbmMappingSet, kv.Value);
                if (!hasRows) continue;
                bool hasActivator =
                    run.Profile.XboxMappingSet.ShiftActivators.Any(a =>
                        a.LayerMask == kv.Value || a.JumpToLayer == kv.Value)
                    || run.Profile.KbmMappingSet.ShiftActivators.Any(a =>
                        a.LayerMask == kv.Value || a.JumpToLayer == kv.Value);
                if (hasActivator) continue;
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.PresetHasNoActivator,
                    run.PresetNames.TryGetValue(kv.Key, out var n) ? n : kv.Value,
                    args: run.PresetNames.TryGetValue(kv.Key, out var n2) ? n2 : kv.Value);
            }
        }

        private static string ResolveText(Run run, string rootValue, string field, string fallback)
        {
            // Root wins when it is a real string; template-derived configs
            // carry a #token or empty root and localize per language.
            string root = (rootValue ?? "").Trim();
            if (root.Length > 0 && !root.StartsWith("#", StringComparison.Ordinal))
                return root;

            string lang = string.IsNullOrWhiteSpace(run.Options.PreferredLanguage)
                ? "english" : run.Options.PreferredLanguage;

            // A '#token' root names a key in the config's OWN localization
            // block. Ground truth: Valve's official TF2 config (1172518660)
            // carries title "#Title_TF2Default" and localization
            // english/Title_TF2Default "Team Fortress 2 Defaults".
            // Preferred language first, then english, then any language in
            // ordinal order for determinism. Steam-library tokens such as
            // #Library_ControllerSaveDefaultTitle (770509247) match nothing
            // in the config and fall through to the fallback.
            if (root.Length > 1 && root.StartsWith("#", StringComparison.Ordinal))
            {
                string token = root.Substring(1);
                foreach (var candidate in new[] { lang, "english" })
                {
                    if (run.Config.Localization.TryGetValue(candidate, out var map)
                        && map.TryGetValue(token, out var v)
                        && !string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
                foreach (var language in run.Config.Localization.Keys
                    .OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (run.Config.Localization[language].TryGetValue(token, out var v)
                        && !string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
            }

            foreach (var candidate in new[] { lang, "english" })
            {
                if (run.Config.Localization.TryGetValue(candidate, out var map)
                    && map.TryGetValue(field, out var v)
                    && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
            // No localization either. An unresolved '#token' root (a Steam
            // library string reference) is meaningless to users; use the
            // fallback. Only empty or #token roots reach this point.
            return fallback;
        }

        private static string FirstToken(string s)
        {
            s = (s ?? "").Trim();
            int sp = s.IndexOf(' ');
            return sp < 0 ? s : s.Substring(0, sp);
        }

        private static int ParseTrailingInt(string name)
        {
            int idx = name.LastIndexOf('_');
            if (idx < 0 || idx == name.Length - 1) return -1;
            return int.TryParse(name.Substring(idx + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int v) ? v : -1;
        }
    }
}
