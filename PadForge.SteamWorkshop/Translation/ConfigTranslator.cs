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

            /// <summary>The Steam action-set layer currently being
            /// translated (#254 A-2). Set at the entry of every
            /// layer-carrying translation path (TranslateInput,
            /// TranslateScrollWheel, TranslateMouseRegion) so every macro
            /// emitter beneath them stamps its scope without threading the
            /// parameter through 28 construction sites.</summary>
            public string CurrentLayer = "Base";

            /// <summary>The single macro-add funnel (#254 A-2): stamps the
            /// current action-set layer so a set-scoped Steam binding stops
            /// firing in every other set after import. Base-set macros are
            /// stamped "Base" (Steam sets REPLACE), never "" (PadForge's
            /// "any layer"). An emitter that pre-set an explicit mask keeps
            /// it.</summary>
            public void AddMacro(TranslatedMacro m)
            {
                if (string.IsNullOrEmpty(m.LayerMask))
                    m.LayerMask = string.IsNullOrEmpty(CurrentLayer) ? "Base" : CurrentLayer;
                Profile.Macros.Add(m);
            }

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
            public readonly List<(string Layer, string Target, string Descriptor, string Path, int DeadZonePct,
                    double CurveExponent, double RangeOuter, double Sensitivity, double Anti)>
                MatchedAnalogs = new();
            private readonly HashSet<string> _matchedAnalogSeen = new(StringComparer.Ordinal);

            public void AddMatchedAnalog(string layer, string target, string descriptor, string path,
                int deadZonePct = 0, double curveExponent = 0, double rangeOuter = 0, double sensitivity = 1.0,
                double anti = 0)
            {
                if (_matchedAnalogSeen.Add($"{layer}|{target}|{descriptor}"))
                    MatchedAnalogs.Add((layer, target, descriptor, path, deadZonePct,
                        curveExponent, rangeOuter, sensitivity, anti));
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

            /// <summary>GROUP-level haptic intensity of the group currently
            /// being translated (v22): haptic_intensity_override wins over
            /// the plain haptic_intensity, Steam levels 1..3, 0 = none
            /// authored. Set by TranslateGroup around the mode dispatch and
            /// consumed by EmitHapticPulse as the fallback level for member
            /// activators that author none of their own, so the group's
            /// haptic ticks ride every member activation (the v13 ruling:
            /// the rumble pulse IS the haptic tick). An activator's own
            /// explicit 0 stays off. A group with no member activators has
            /// no activation to tick on and lowers silently (the
            /// continuous surface-motion tick has no channel).</summary>
            public int GroupHapticLevel;

            /// <summary>Macros emitted with no report line (the v17
            /// SCREENSHOT / SHOW_KEYBOARD arms: the note described exactly
            /// what a user expects, so it was noise). The v14-v17
            /// CountEmitted consumer retired with the half-approximation
            /// note in v18 (wedges window per half now); the counter
            /// stays as the cheap emissions census.</summary>
            public int SilentMacroEmissions;

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
            // Click gates ride each source's own GateDescriptor since v18
            // (the engine's per-source AND companion), so a second feed on
            // the same target never drops anybody's gate.
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
            /// <summary>v15 half stamp for Kind=Axis: one signed direction
            /// engages (ShiftActivator.AxisHalf / AxisInvert), so a wedge-
            /// or gyro-hosted request never fires on the opposite
            /// direction of its own axis.</summary>
            public bool AxisHalf;
            public bool AxisInvert;
            public string Path = "";
            /// <summary>AND companion of the activator input (a single-pad
            /// half click gated on its half's touch spot, #9 B-1).
            /// Materializes as Kind=Chord with ChordSecondDescriptor.</summary>
            public string GateDescriptor = "";
            /// <summary>Layer of the preset hosting the binding. A lone
            /// CHANGE_PRESET to Base lowers to a single-stop Cycle through
            /// this layer (the runtime has no one-way jump).</summary>
            public string HostLayer = "";
            /// <summary>Hold-before-engage debounce, ms (ShiftActivator.DelayMs).
            /// Long_Press layer carries set it to the activator's
            /// long_press_time; 0 = instant.</summary>
            public int DelayMs;
            /// <summary>Release linger, ms (ShiftActivator.ReleaseDelayMs,
            /// v22): a Hold-mode carrier's delay_end. The layer stays
            /// engaged this long past the release; a re-press cancels the
            /// pending disengage. 0 = instant disengage.</summary>
            public int ReleaseDelayMs;
            /// <summary>Double-press gate, ms (ShiftActivator.DoublePressMs,
            /// v25): a Double_Press-hosted layer carrier's double_tap_time.
            /// The activator engages only on the second press of a
            /// press-release-press pair inside the window. 0 = plain read.</summary>
            public int DoublePressMs;
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
            run.PresetNames[preset.Id] = PresetDisplayName(run, preset);

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
            // Analog gameactions linkage (v13 section census): the group
            // drives an in-game Steam Input API action while this set is
            // active (XCOM 2's TacticalCamera stick, TF2's Move pad). Same
            // Steam-only surface as game_action bindings, so it feeds the
            // same per-preset aggregate skip. The pair's key names its set.
            // A pair keyed to another set is counted where that set hosts
            // this group.
            foreach (var ga in group.GameActions)
            {
                if (string.Equals(ga.Key, preset.Name, StringComparison.OrdinalIgnoreCase))
                {
                    run.GameActionsByPreset[preset.Id] =
                        run.GameActionsByPreset.GetValueOrDefault(preset.Id) + 1;
                }
            }

            // center_trackpad reads pad 0 whole on every type (v25): the
            // token means the single central pad (25 of 30 wild authors
            // are controller_ps4), no SDL device registers a third pad,
            // and non-PS authors are type-converted leftovers whose
            // sections should drive whichever pad-bearing device the user
            // maps. The old multi-pad skip is retired with the routing.

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

            // Group-level haptics (v22): stash the group's level so
            // EmitHapticPulse can apply it to every member activator that
            // authors none of its own. Assigned per group (walks never
            // nest), so no reset is needed.
            run.GroupHapticLevel = ParseGroupHapticLevel(settings);

            // Group settings PadForge has no channel for get named notes
            // instead of silence, but only on modes that otherwise
            // translate (a wholly-skipped group's own entry covers it).
            if (ProductiveModes.Contains(mode))
                ReportDroppedGroupSettings(run, settings, path, slot, mode,
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
                case "joystick_camera":
                    EmitMouseAxes(run, slot, layer, path, settings, StickMouseBaseline,
                        curveChannel: CurveChannelApplies(slot, mode));
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "mouse_joystick":
                    // "As Joystick (Mouse-like)" OUTPUTS a stick, not the
                    // cursor: sc-controller's proven importer lowers the
                    // mode to ABS_RX/ABS_RY (scc/foreign/vdf.py,
                    // mode == "mouse_joystick"), so the host's analog pair
                    // lands on the right-stick axes (output_joystick 1
                    // redirects to the left).
                    EmitMouseJoystickAxes(run, slot, layer, path, settings, StickMouseBaseline,
                        curveChannel: CurveChannelApplies(slot, mode));
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
                        sensitivityKey: "gyro_natural_sensitivity",
                        curveChannel: CurveChannelApplies(slot, mode));
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "gyro_to_joystick":
                    // The post-2022 gyro-as-stick RATE mode (v26): rotation
                    // rate drives the virtual stick pair, the exact output
                    // surface the gyro-hosted mouse_joystick lowering
                    // already emits (Gyro Yaw / Gyro Pitch onto the
                    // {dst}ThumbAxis pair, per-source GyroSensitivity,
                    // sc-controller's ABS_RX/RY grounding for the shape),
                    // so it lowers through the same emitter. The group's
                    // gyro_button / ratchet settings ride the shared
                    // gyro-settings walk like every other gyro mode.
                    EmitMouseJoystickAxes(run, slot, layer, path, settings, StickMouseBaseline,
                        curveChannel: CurveChannelApplies(slot, mode));
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "gyro_to_joystick_deflection":
                    // The deflection sibling (v26): sustained TILT maps to
                    // stick deflection (position, not rate), which is the
                    // engine's gravity-lean pair. Signs are the physical
                    // stick's own frame by construction (Lean X positive =
                    // tilt right, Lean Y positive = nose up = stick pulled
                    // back), so the pair lowers 1:1 onto the thumb axes.
                    EmitGyroLeanJoystickAxes(run, layer, path, settings);
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
                    EmitMouseAxes(run, slot, layer, path, settings, TrackpadMouseBaseline,
                        curveChannel: CurveChannelApplies(slot, mode));
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "scrollwheel":
                    // Trackpad hosts lower to a vertical finger drag (v10
                    // G4) and stick hosts to the stick's Y deflection drag
                    // (v12): the wheel-shaped scroll_clockwise /
                    // scroll_counterclockwise bindings feed KbmScroll from
                    // the drag axis, sign per direction, and the Scroll
                    // Wheel List items step a CycleTapList macro per
                    // detent (v16). No other host exists in Steam's
                    // grammar (v16 census: every scrollwheel
                    // group_source_bindings entry across the corpus and
                    // Valve's shipped controller_base templates is a
                    // trackpad or joystick, and the shipped strings bind
                    // the mode's members to "the pad/stick"), so the old
                    // surfaceless-host skip arm is retired, and a hand-edited
                    // config outside that grammar routes through the
                    // member walk's UnknownPhysicalInput safety net.
                    if (PhysicalSlotResolver.IsTrackpad(slot) || PhysicalSlotResolver.IsStick(slot))
                    {
                        TranslateScrollWheel(run, preset, effective, slot, layer, path, settings);
                    }
                    else
                    {
                        TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    }
                    break;

                case "touch_menu":
                // hotbar serializes exactly like touch_menu
                // (touch_menu_button_{n} cells; corpus 2494749393), so it
                // lowers through the same grid-menu path (v10 G15).
                case "hotbar":
                    TranslateMenuGroup(run, preset, effective, slot, layer, path, settings,
                        radial: false);
                    break;

                case "radial_menu":
                    TranslateMenuGroup(run, preset, effective, slot, layer, path, settings,
                        radial: true);
                    break;

                case "mouse_region":
                    TranslateMouseRegion(run, effective, slot, layer, path, settings);
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings,
                        onlyInputs: new[] { "click", "touch", "edge" });
                    break;

                case "2dscroll":
                    // Directional swipe. Trackpad hosts lower onto the
                    // gesture engine's one-shot swipe fires (v10 G3):
                    // each dpad_* member reads "Touchpad {p} Swipe{Dir}",
                    // self-armed at apply since v14. Gyro hosts lower onto
                    // one-shot tap macros triggered by the SIGNED gyro
                    // rate descriptor's matching half (v15): a flick of
                    // the pad in a direction fires the binding once and
                    // re-arms below the engine's rate threshold. Every
                    // other host walks the same one-shot member lowering
                    // (v12 wedge taps on sticks; a dpad-hosted swipe taps
                    // once per press; members with no PadForge source keep
                    // UnknownPhysicalInput), so no host-level skip remains.
                    if (PhysicalSlotResolver.IsTrackpad(slot))
                    {
                        TranslateSwipeGroup(run, preset, effective, slot, layer, path, settings);
                    }
                    else if (slot == SteamSlot.Gyro)
                    {
                        TranslateGyroSwipeGroup(run, preset, effective, layer, path);
                    }
                    else
                    {
                        TranslateStickSwipeGroup(run, preset, effective, slot, layer, path, settings);
                    }
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
            "2dscroll", "hotbar",
        };

        /// <summary>Response-shaping group settings. Key list grounded on
        /// the corpus (deadzone_outer_radius 28640..31999, curve_exponent
        /// 1/4) plus the sibling keys of the same UI cluster. Since v11
        /// stick-hosted joystick groups CONSUME most of this cluster into
        /// the per-source curve/range channel (<see cref="CurveChannelConsumedKeys"/>,
        /// skipCurveChannel), so only genuinely dropped keys reach the note
        /// there. The mouse_region per-axis sensitivity scales (corpus
        /// 2795727040: 110/70; shipped configurator ids
        /// Horizontal/VerticalSensitivityMouseRegion) scale the region per
        /// axis. Since v6 (#9 B-15) trackpad-hosted regions consume them as
        /// the pointer rows' extent, so the drop note skips them there
        /// (skipRegionScales); stick/gyro hosts keep the wave-2A clamp
        /// approximation and still drop them named. output_curve has
        /// NEGATIVE grounding as a real Steam key (absent from the corpus,
        /// Valve's shipped controller_base templates, Valve's CSGO
        /// controller configs, and OpenSteamworks' EControllerSetting enum);
        /// it stays listed defensively so an unknown config carrying it
        /// still gets a named drop instead of silence.</summary>
        /// <summary>The still-dropped response-cluster keys (v18). The
        /// exponent / range / sensitivity / anti-deadzone keys are consumed
        /// on EVERY analog host now (per-source shaping runs on the stick,
        /// trigger, trackpad, gesture, and gyro lanes since v18), so only
        /// two names remain: output_curve has NEGATIVE grounding as a real
        /// Steam key (absent from the corpus, Valve's shipped
        /// controller_base templates, Valve's CSGO controller configs, and
        /// OpenSteamworks' EControllerSetting enum) and stays listed
        /// defensively; deadzone_shape names the arm the pair channel
        /// cannot reach (mouse-output hosts, whose X / Y rows evaluate
        /// independently with no pair read; thumb-pair outputs consume it
        /// via the slot-level DeadZoneShape stamp instead). Member-only
        /// modes (dpad, four_buttons, switches, single_button) consume the
        /// cluster silently: Steam's own configurator hides the response
        /// curve for them, so a stale key left by a mode switch shapes
        /// nothing there in Steam either.</summary>
        private static readonly string[] CurveSettingKeys =
        {
            "deadzone_shape", "output_curve",
        };

        /// <summary>Modes whose analog pair rides the per-source
        /// curve/range channel: v11 built the stick hosts, v18 widened the
        /// engine seam to the trackpad finger / gesture lanes, the gyro
        /// lane, and the unipolar trigger tail, so every analog host
        /// stamps.</summary>
        private static readonly HashSet<string> CurveChannelModes = new(StringComparer.Ordinal)
        {
            "joystick_move", "joystick_mouse", "joystick_camera", "mouse_joystick",
            "absolute_mouse", "relative_mouse", "gyro_to_mouse",
        };

        /// <summary>True when a group's curve cluster lands on the emitted
        /// analog rows. MUST stay the same predicate the emitters use to
        /// stamp, or the drop note and the stamps drift apart.</summary>
        private static bool CurveChannelApplies(SteamSlot slot, string mode)
            => (PhysicalSlotResolver.IsStick(slot) || PhysicalSlotResolver.IsTrackpad(slot)
                    || slot == SteamSlot.Gyro)
                && CurveChannelModes.Contains(mode);

        /// <summary>True when the group's output is a virtual thumb PAIR,
        /// so Steam's deadzone_shape lands on the engine's pair-shaped
        /// deadzone channel (the slot-level DeadZoneShape stamp the
        /// runtime overlays onto the VC stick processing).</summary>
        private static bool DeadZoneShapeApplies(string mode)
            => mode == "joystick_move" || mode == "mouse_joystick";

        /// <summary>Per-group curve/range channel values (v11), parsed once
        /// and stamped onto both member rows of the emitted axis pair.
        ///
        /// <para>Grounding. curve_exponent is Steam's PRESET SELECTOR, not a
        /// raw exponent: the shipped configurator strings enumerate
        /// ControllerBinding_CurveExponent_joystick_move_{Linear,Curve_1..4,
        /// Curve_Custom} as Linear / Aggressive / Relaxed / Wide / Extra
        /// Wide / Custom (steamui localization), matching corpus values 0/1/4
        /// and the wild custom form (curve_exponent 5 beside
        /// custom_curve_exponent 60, gw2-steam-controller). Steam's
        /// Aggressive "gets to 100% output faster" (exponent below 1) and
        /// Extra Wide "only reaching 100% at the extremes" (highest
        /// exponent), so the preset ints map onto the engine's curve shapes
        /// by SEMANTICS, not by PadForge's preset names: 1 = 0.5, 2 = 1.5,
        /// 3 = 2.0, 4 = 2.5. custom_curve_exponent stores the slider x100
        /// (Valve's CSGO ps4 gyro config: 195 = 1.95; wild 50/60 = 0.5/0.6;
        /// raw 195 or a x1000 read are implausible exponents).
        /// deadzone_outer_radius shares deadzone_inner_radius's 0..32767
        /// full-deflection scale (GroupDeadZonePercent): Valve's basicui
        /// templates carry 28000/32000/32767, i.e. full output at 85..100%
        /// travel, so 32767 is the identity and the fraction is
        /// value / 32767. sensitivity_horiz/vert_scale are percent (the
        /// shipped configurator ids are #Unit_Percent; the mouse_region path
        /// reads them clamp(1,400)/100 the same way).</para></summary>
        private readonly struct CurveRangeChannel
        {
            public readonly double Exponent;   // 0 = off (Linear)
            public readonly double RangeOuter; // 0 = off (full range)
            public readonly double SensX;      // 1.0 = neutral
            public readonly double SensY;
            public readonly double Anti;       // 0 = off (v18 anti_deadzone)

            private CurveRangeChannel(double exponent, double rangeOuter, double sensX, double sensY,
                double anti)
            {
                Exponent = exponent;
                RangeOuter = rangeOuter;
                SensX = sensX;
                SensY = sensY;
                Anti = anti;
            }

            public static CurveRangeChannel FromSettings(Dictionary<string, string> settings)
            {
                int preset = ParseIntSetting(settings, "curve_exponent", 0);
                double exponent = preset switch
                {
                    1 => 0.5, // Steam Aggressive: reaches 100% output faster
                    2 => 1.5, // Steam Relaxed: slightly more fine range
                    3 => 2.0, // Steam Wide: much slower ramp
                    4 => 2.5, // Steam Extra Wide: 100% only at the extremes
                    _ => 0.0, // 0 / absent = Linear; >= 5 = Custom, below
                };
                if (preset >= 5)
                {
                    // Custom slider, stored x100. A preset 1..4 beside a
                    // custom value means the stale slider lost to the named
                    // preset (Steam ignores it too), so custom reads only
                    // when the selector says Custom. Clamp = junk guard.
                    double custom = ParseIntSetting(settings, "custom_curve_exponent", 0) / 100.0;
                    if (custom > 0) exponent = Math.Clamp(custom, 0.1, 10.0);
                }
                if (exponent == 1.0) exponent = 0.0; // x^1 = identity, keep the off default

                double outer = 0.0;
                int outerRaw = ParseIntSetting(settings, "deadzone_outer_radius", 0);
                // 32767 (and junk past it) is the identity; 0 / absent is off.
                if (outerRaw > 0 && outerRaw < 32767) outer = outerRaw / 32767.0;

                double sensX = Math.Clamp(ParseIntSetting(settings, "sensitivity_horiz_scale", 100), 1, 400) / 100.0;
                double sensY = Math.Clamp(ParseIntSetting(settings, "sensitivity_vert_scale", 100), 1, 400) / 100.0;
                // anti_deadzone (v18): an output floor on the response,
                // the deadzone-radius 0..32767 scale (Valve's basicui
                // templates carry it on joystick groups). Junk past the
                // ceiling clamps below 1 so the floor stays a floor.
                double anti = 0.0;
                int antiRaw = ParseIntSetting(settings, "anti_deadzone", 0);
                if (antiRaw > 0) anti = Math.Min(antiRaw / 32767.0, 0.95);
                return new CurveRangeChannel(exponent, outer, sensX, sensY, anti);
            }

            /// <summary>Stamps one member row's source. The per-axis
            /// sensitivity scale multiplies INTO the existing Sensitivity so
            /// a mouse-mode ratio already on the source is preserved. Gyro
            /// sources carry their ratio on GyroSensitivity, so the scale
            /// folds there for them.</summary>
            public void StampAxis(MappingSource src, bool isX)
            {
                if (Exponent > 0) src.ParamCurveExponent = Exponent;
                if (RangeOuter > 0) src.ParamRangeOuter = RangeOuter;
                if (Anti > 0) src.ParamAntiDeadzone = Anti;
                double s = isX ? SensX : SensY;
                if (s != 1.0)
                {
                    if ((src.Descriptor ?? "").StartsWith("Gyro ", StringComparison.Ordinal))
                        src.GyroSensitivity *= s;
                    else
                        src.Sensitivity *= s;
                }
            }
        }

        /// <summary>The one mouse-feel key still without a channel (v18):
        /// mouse_dampening_trigger slows the cursor while a NAMED trigger
        /// is pulled, a cross-input modulation. The row grammar's only
        /// second-input constructs are the boolean AND gate and the
        /// InvertOnHold sign flip; a live analog scaling one source by
        /// another would be a new source kind, so the key stays named.
        /// Everything else in the family BUILT in v18: rotation is pure
        /// row math (two-source Sum with trigonometric coefficients),
        /// mouse_smoothing rides the per-source EMA, acceleration the
        /// rate gain, mouse_move_threshold the delta gate, and trackball
        /// + friction (+ friction_vert_scale) the momentum decay, all on
        /// MappingSource Param* knobs.</summary>
        private static readonly string[] MouseModeTuningKeys =
        {
            "mouse_dampening_trigger",
        };

        /// <summary>Mouse-feel channel (v18): the built remainder of the
        /// old MouseModeTuningDropped family, parsed once per group and
        /// stamped onto the emitted mouse-axis sources.
        ///
        /// <para>Grounding and scales. rotation is degrees (corpus -30..14;
        /// the shipped configurator's Rotation slider), applied as a
        /// rotation of the input vector: x' = x cos t - y sin t,
        /// y' = x sin t + y cos t in the pad's y-down frame (positive =
        /// visually clockwise). mouse_smoothing is a 0..100-ish strength
        /// (corpus 0..22), mapped to the per-tick EMA alpha / 100, the
        /// GyroTuning.SmoothingAlpha convention. acceleration is Steam's
        /// small enum-ish gain (corpus 1 / 3), mapped to a 0.5x-per-step
        /// rate gain on the engine's 1 + a * |v| curve.
        /// mouse_move_threshold gates small motions (corpus 2), mapped to
        /// thousandths of the normalized delta. trackball 1 enables
        /// momentum; friction (corpus 0 / 1 / 3) sets the per-tick decay
        /// (0 = spin practically forever, higher = stop faster) and
        /// friction_vert_scale (percent) scales the vertical decay.</para></summary>
        private readonly struct MouseFeelChannel
        {
            public readonly double RotationDeg;
            public readonly double SmoothingAlpha;
            public readonly double Accel;
            public readonly double MoveThreshold;
            public readonly double TrackballDecayX;
            public readonly double TrackballDecayY;

            private MouseFeelChannel(double rot, double alpha, double accel, double threshold,
                double decayX, double decayY)
            {
                RotationDeg = rot;
                SmoothingAlpha = alpha;
                Accel = accel;
                MoveThreshold = threshold;
                TrackballDecayX = decayX;
                TrackballDecayY = decayY;
            }

            public static MouseFeelChannel FromSettings(Dictionary<string, string> settings)
            {
                double rot = ParseIntSetting(settings, "rotation", 0);
                if (rot <= -360 || rot >= 360) rot = 0; // junk guard
                double alpha = Math.Clamp(ParseIntSetting(settings, "mouse_smoothing", 0), 0, 99) / 100.0;
                double accel = Math.Clamp(ParseIntSetting(settings, "acceleration", 0), 0, 10) * 0.5;
                double threshold = Math.Clamp(ParseIntSetting(settings, "mouse_move_threshold", 0), 0, 100) / 1000.0;
                double decayX = 0, decayY = 0;
                if (ParseIntSetting(settings, "trackball", 0) != 0)
                {
                    int friction = Math.Clamp(ParseIntSetting(settings, "friction", 1), 0, 10);
                    decayX = friction == 0 ? 0.9995 : 1.0 - 0.0015 * friction;
                    int vert = ParseIntSetting(settings, "friction_vert_scale", 100);
                    double frictionY = friction * Math.Clamp(vert, 1, 400) / 100.0;
                    decayY = friction == 0 ? decayX : Math.Clamp(1.0 - 0.0015 * frictionY, 0.9, 0.9995);
                }
                return new MouseFeelChannel(rot, alpha, accel, threshold, decayX, decayY);
            }

            public bool HasRotation => RotationDeg != 0;

            /// <summary>Stamps the non-rotation knobs onto one axis source
            /// (rotation is row structure, not a source knob).</summary>
            public void StampFeel(MappingSource src, bool isX)
            {
                if (SmoothingAlpha > 0) src.ParamSmoothingAlpha = SmoothingAlpha;
                if (Accel > 0) src.ParamAccel = Accel;
                if (MoveThreshold > 0) src.ParamMoveThreshold = MoveThreshold;
                double decay = isX ? TrackballDecayX : TrackballDecayY;
                if (decay > 0) src.ParamTrackballDecay = decay;
            }
        }

        /// <summary>Strips the nonlinear response stamps off one rotated
        /// leg (v19, T5). The rotation lowering Sums two per-source legs,
        /// and a nonlinear per-leg response does not commute with the sum:
        /// each leg would shape alone and the summed vector rotates by a
        /// speed-dependent angle. The linear knobs (sensitivity scales,
        /// outer range rescale, EMA smoothing, trackball decay) commute
        /// with the Sum and stay stamped.</summary>
        private static void WithholdNonlinearForRotation(MappingSource src)
        {
            src.ParamCurveExponent = 0;
            src.ParamAntiDeadzone = 0;
            src.ParamAccel = 0;
        }

        /// <summary>Names the withheld nonlinear keys once per rotated
        /// group (v19, T5), the honest twin of
        /// <see cref="WithholdNonlinearForRotation"/>.</summary>
        private void ReportRotationNonlinearWithheld(Run run, string path,
            in CurveRangeChannel curve, in MouseFeelChannel feel, bool curveChannel)
        {
            var keys = new List<string>(3);
            if (curveChannel && curve.Exponent > 0) keys.Add("curve_exponent");
            if (curveChannel && curve.Anti > 0) keys.Add("anti_deadzone");
            if (feel.Accel > 0) keys.Add("acceleration");
            if (keys.Count > 0)
            {
                run.Report.Add(TranslationStatus.Partial,
                    TranslationReasons.RotationNonlinearWithheld, path,
                    args: string.Join(", ", keys));
            }
        }

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

        /// <summary>Named notes for group settings without a channel, plus
        /// the v18 slot-level stamps: deadzone_shape lands on the pair
        /// channel where the output IS a thumb pair, gyro_button 0 lands
        /// on the slot-level engage stamp, and the group-level haptic
        /// override feeds the per-config aggregate.</summary>
        private void ReportDroppedGroupSettings(Run run,
            Dictionary<string, string> settings, string path,
            SteamSlot slot, string mode, bool reportMouseTuning = false)
        {
            // deadzone_shape (v18): thumb-pair outputs consume it into the
            // slot-level DeadZoneShape stamp (Steam 0 = Cross and 2 =
            // Square are per-axis checks, the engine's Axial; 1 = Circle
            // is the radial check, the engine's ScaledRadial default).
            bool shapeConsumed = false;
            if (DeadZoneShapeApplies(mode)
                && settings.TryGetValue("deadzone_shape", out var shapeRaw)
                && int.TryParse((shapeRaw ?? "").Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int shapeVal))
            {
                string stamp = shapeVal == 1 ? "2" : "0";
                // The pair the group FEEDS: joystick_move redirects via
                // output_joystick (1 = left, 2 = right, 0 = matched side);
                // mouse_joystick defaults right, output_joystick 1 = left.
                int oj = ParseIntSetting(settings, "output_joystick", 0);
                bool left = mode == "joystick_move"
                    ? (oj == 1 || (oj != 2 && slot != SteamSlot.RightJoystick))
                    : oj == 1;
                if (left) run.Profile.LeftStickDeadZoneShape = stamp;
                else run.Profile.RightStickDeadZoneShape = stamp;
                shapeConsumed = true;
            }

            // Stick-hosted mouse modes consume deadzone_shape into the
            // per-source stick geometry stamp (v25): EmitMouseAxes writes
            // ParamStickDeadZoneShape onto the emitted pair, and the
            // engine's bipolar Axis read applies the inner/outer rescale
            // radially (Steam Circle) or per axis (Cross / Square).
            if (!shapeConsumed
                && settings.ContainsKey("deadzone_shape")
                && PhysicalSlotResolver.IsStick(slot)
                && (mode == "joystick_mouse" || mode == "joystick_camera"))
            {
                shapeConsumed = true;
            }

            var curves = CurveSettingKeys
                .Where(k => settings.ContainsKey(k)
                    && !(shapeConsumed && k == "deadzone_shape"))
                .ToList();
            if (curves.Count > 0)
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.ResponseCurveNotSupported,
                    path, args: string.Join(", ", curves));
            }

            // The one mouse-feel key with no channel (v18): cross-input
            // dampening. Only on the mouse/region modes; flickstick names
            // its own overlapping keys.
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

            // gyro_button (v18, full enum since v23): the value INDEXES
            // Steam's k_eGamepadButtonBitMask enum, the same bit space
            // the v22 ratchet grounding read out of the shipped
            // configurator JS (the GyroEnableButton picker renders
            // through the same GyroButtonPicker visualizer and enum
            // glyph map as the ratchet mask. Corpus cross-check:
            // 2374887917, a DualSense config, authors 20, the right pad
            // touch, the classic gyro engage). Value 0 is the format's
            // none/default sentinel (Steam's own picker reads mask 0 as
            // no-button-selected) and keeps the v18 pad-touch default
            // engage (the SC right-pad touch; the single physical pad's
            // right half on DS4 / DualSense). Every other index lowers
            // through RatchetBitDescriptor onto the slot-level
            // device-free engage stamp, with gyro_button_invert 1 as
            // the engage-while-NOT-held flip. Only indices with no
            // grounded read (enum holes, macro buttons, CapSense aux)
            // keep the named note. The ratchet BITMASK builds since
            // v22: grounded bits lower onto the slot-level ratchet
            // clutch lane, and only genuinely ungrounded bits keep the
            // note (args carry the residual mask).
            foreach (var key in new[] { "gyro_button", "gyro_ratchet_button_mask", "gyro_button_invert" })
            {
                if (!settings.TryGetValue(key, out var v)) continue;
                string val = (v ?? "").Trim();
                if ((key == "gyro_ratchet_button_mask" || key == "gyro_button_invert")
                    && val == "0")
                {
                    continue;
                }
                if (key == "gyro_button" && val == "0")
                {
                    run.Profile.GyroEngageDescriptor = run.SinglePadTrackpads
                        ? "Touchpad 0 Finger 0 Down Right"
                        : "Touchpad 1 Finger 0 Down";
                    continue;
                }
                if (key == "gyro_button"
                    && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int engageIdx)
                    && engageIdx > 0 && engageIdx < 64
                    && RatchetBitDescriptor(engageIdx, run.SinglePadTrackpads) is string engageDesc)
                {
                    run.Profile.GyroEngageDescriptor = engageDesc;
                    continue;
                }
                if (key == "gyro_button_invert" && val == "1")
                {
                    run.Profile.GyroEngageInvert = true;
                    continue;
                }
                // gyro_button_invert is Steam's three-state "Gyro Button
                // Behavior" enum (shipped ControllerBinding_GyroButtonInvert*
                // strings): On (0, engage while held), Off (1, "gyro will
                // turn off when the button is pressed", the invert above),
                // Toggle (2, "toggle its state each time the button is
                // pressed"). 2 lands on the slot engage machinery's own
                // Toggle mode (UpdateGyroEngageStates flips a sticky bit
                // per rising edge), stamped beside the engage descriptor.
                if (key == "gyro_button_invert" && val == "2")
                {
                    run.Profile.GyroEngageToggle = true;
                    continue;
                }
                if (key == "gyro_ratchet_button_mask"
                    && ulong.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong mask)
                    && mask != 0)
                {
                    // Hold-to-disengage clutch (v22). Lowered onto the
                    // dedicated slot-level ratchet lane, NOT SetGyroEngaged
                    // Off/On macros and NOT the engage-invert channel: the
                    // engage read ORs the button bit with the macro bit
                    // (and Hold mode's empty descriptor reads always-on),
                    // so a macro Off could never clutch and would fight a
                    // configured engage button, and the invert flag owns
                    // the single engage descriptor, so it composes with
                    // neither an authored gyro_button nor a multi-bit
                    // mask. The AND-NOT lane cannot fight the engage gate
                    // by construction. Multiple gyro groups union their
                    // masks (a clutch surface too many is the safe
                    // approximation; the list is sorted for deterministic
                    // goldens).
                    ulong ungrounded = 0;
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) == 0) continue;
                        string desc = RatchetBitDescriptor(bit, run.SinglePadTrackpads);
                        if (desc == null)
                        {
                            ungrounded |= 1UL << bit;
                            continue;
                        }
                        if (!run.Profile.GyroRatchetDescriptors.Contains(desc))
                            run.Profile.GyroRatchetDescriptors.Add(desc);
                    }
                    run.Profile.GyroRatchetDescriptors.Sort(StringComparer.Ordinal);
                    if (ungrounded == 0) continue;
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.GyroButtonMaskDropped,
                        path, args: new[] { key, ungrounded.ToString(CultureInfo.InvariantCulture) });
                    continue;
                }
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.GyroButtonMaskDropped,
                    path, args: new[] { key, v ?? "" });
            }
        }

        /// <summary>Maps one k_eGamepadButtonBitMask position to its
        /// device-free descriptor, or null when the bit has no grounded
        /// PadForge read (v22). Serves BOTH gyro lanes: each set
        /// gyro_ratchet_button_mask bit, and since v23 the gyro_button
        /// engage value, which indexes the same enum (the configurator
        /// renders both settings through the one GyroButtonPicker glyph
        /// map). The bit space is Steam's own k_eGamepadButtonBitMask
        /// enum, read out of the shipped configurator
        /// (steamui/chunk~2dcc5aaf7.js, the same client build the v13
        /// vocabulary census used; the gyro panel renders the mask setting
        /// as a BigInt through that enum's glyph map): 0/1 trigger full
        /// pulls, 2/3 bumpers, 4-7 North/East/West/South, 8-11 D-pad
        /// up/right/left/down, 12/13/14 View/Steam/Options, 15/16 lower
        /// back grips, 17/18 pad clicks, 19/20 pad touches (CapSense),
        /// 22/26 stick clicks, 24/25 trigger soft pulls, 27/28 center pad,
        /// 29 Ancillary1 (the Capture button, the v10 G2 "Button 11"
        /// grounding), 30/31 stick deflection, 41/42 upper back grips.
        /// Corpus cross-checks: 3456927474 (Deck) authors 1&lt;&lt;41, the
        /// L4 upper-left paddle; 3725174032 (triton) authors 1&lt;&lt;20,
        /// the right pad touch (the classic gyro surface); 3353604014
        /// (DualSense) authors 71720947, exactly the face + D-pad + Start /
        /// Select + pad-click + stick-click + trigger-pull set with
        /// bumpers, Steam, grips, and touches excluded, the "pause gyro
        /// while pressing buttons" shape. Enum holes (21, 23, 40, 43) and
        /// surfaces PadForge cannot read (macro buttons 32-39 and 48+,
        /// CapSense aux / stick touches 44-47, the center pad on multi-pad
        /// families) return null and keep the named note. Trigger bits read
        /// through the engage lane's 50 percent bool threshold (soft and
        /// full pull collapse onto the same read; the clutch fires slightly
        /// early on a full-pull bit, never late). Single-pad half clicks
        /// (17/18) approximate onto the one physical click.</summary>
        private static string RatchetBitDescriptor(int bit, bool singlePad) => bit switch
        {
            0 => "Gamepad RightTrigger",
            1 => "Gamepad LeftTrigger",
            2 => "Gamepad RightShoulder",
            3 => "Gamepad LeftShoulder",
            4 => "Gamepad ButtonY",   // North (positional, SDL's frame)
            5 => "Gamepad ButtonB",   // East
            6 => "Gamepad ButtonX",   // West
            7 => "Gamepad ButtonA",   // South
            8 => "Gamepad DPadUp",
            9 => "Gamepad DPadRight",
            10 => "Gamepad DPadLeft",
            11 => "Gamepad DPadDown",
            12 => "Gamepad ButtonBack",
            13 => "Gamepad ButtonGuide",
            14 => "Gamepad ButtonStart",
            15 => "Gamepad Paddle2",  // button_back_left, the resolver's pairing
            16 => "Gamepad Paddle1",  // button_back_right
            17 => "Touchpad 0 Click",
            18 => singlePad ? "Touchpad 0 Click" : "Touchpad 1 Click",
            19 => singlePad ? "Touchpad 0 Finger 0 Down Left" : "Touchpad 0 Finger 0 Down",
            20 => singlePad ? "Touchpad 0 Finger 0 Down Right" : "Touchpad 1 Finger 0 Down",
            22 => "Gamepad LeftStick",
            24 => "Gamepad RightTrigger", // soft pull: same 50% bool read
            25 => "Gamepad LeftTrigger",
            26 => "Gamepad RightStick",
            27 => singlePad ? "Touchpad 0 Finger 0 Down" : null,
            28 => singlePad ? "Touchpad 0 Click" : null,
            29 => "Button 11",
            30 => "Gamepad LeftStickRing",
            31 => "Gamepad RightStickRing",
            // ButtonMacro0..4 (v25): the v24 button_macro grounding
            // (Steam macro N = SDL misc N+2, read at raw Buttons[17..21])
            // serves the mask lanes too. Macro5..7 (bits 37-39) exceed
            // SDL's misc space and stay null.
            32 => "Button 17",
            33 => "Button 18",
            34 => "Button 19",
            35 => "Button 20",
            36 => "Button 21",
            41 => "Gamepad Paddle4",  // button_back_left_upper
            42 => "Gamepad Paddle3",  // button_back_right_upper
            // Capsense bits (v26): CapSenseLeftAux / RightAux /
            // LeftStick / RightStick = 44..47 in the shipped
            // configurator's k_eGamepadButtonBitMask
            // (steamui/chunk~2dcc5aaf7.js), landing on the fork's
            // SDL_GetGamepadCapSense channels (LEFT_GRIP / RIGHT_GRIP /
            // LEFT_STICK / RIGHT_STICK) through the engine's capsense
            // bool family.
            44 => "Gamepad LeftGripTouch",
            45 => "Gamepad RightGripTouch",
            46 => "Gamepad LeftStickTouch",
            47 => "Gamepad RightStickTouch",
            _ => null,
        };

        /// <summary>Steam's group-level inner deadzone (0..32767 of full
        /// deflection) as a PadForge DeadZone percent. 0 / absent / junk
        /// return 0 (keep the engine default; Steam's 0 is region geometry,
        /// not a hair-trigger request). v19 (T3): the stick-hosted D-pad
        /// family stores the same knob under the bare "deadzone" key
        /// (same 0..32767 scale); deadzone_inner_radius wins when both
        /// are present.</summary>
        private static int GroupDeadZonePercent(Dictionary<string, string> settings)
        {
            if (!TryParseDeadZoneRaw(settings, "deadzone_inner_radius", out int v)
                && !TryParseDeadZoneRaw(settings, "deadzone", out v))
            {
                return 0;
            }
            return Math.Clamp((int)Math.Round(v * 100.0 / 32767.0), 1, 100);
        }

        private static bool TryParseDeadZoneRaw(Dictionary<string, string> settings,
            string key, out int value)
        {
            value = 0;
            return settings.TryGetValue(key, out var raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                && value > 0;
        }

        /// <summary>Names the pair-host deadzone residual (v19, T2): the
        /// authored inner radius lands on the per-source DeadZone, which
        /// only the digital (button-shaped) reads consume (the analog
        /// pair read has no per-source inner channel), and the outer
        /// radius applies per axis (ParamRangeOuter in the scalar shaping
        /// tail), not radially over the pair, so diagonals reach full
        /// deflection early. The radial application needs the per-source
        /// companion-axis pair read (a ParamYDescriptor-style channel on
        /// MappingSource) the engine does not carry yet; until it exists
        /// the residual rides this named Partial per analog pair host.</summary>
        private void ReportRadialDeadZoneResidual(Run run,
            Dictionary<string, string> settings, string path)
        {
            var keys = new List<string>(2);
            if (GroupDeadZonePercent(settings) > 0)
            {
                keys.Add(TryParseDeadZoneRaw(settings, "deadzone_inner_radius", out _)
                    ? "deadzone_inner_radius" : "deadzone");
            }
            if (TryParseDeadZoneRaw(settings, "deadzone_outer_radius", out int outer)
                && outer < 32767)
            {
                keys.Add("deadzone_outer_radius");
            }
            if (keys.Count > 0)
            {
                run.Report.Add(TranslationStatus.Partial,
                    TranslationReasons.DeadZoneRadialResidual, path,
                    args: string.Join(", ", keys));
            }
        }

        /// <summary>True when the group's Outer Ring zone covers the whole
        /// trackpad (v16): edge_binding_invert set with the radius at the
        /// 32767 ceiling ("inside the radius" = anywhere on the pad), or
        /// an un-inverted radius of 0 ("outside" = anywhere). Radius scale
        /// is the pad's own 0..32767 (corpus 3456927474 carries 32767
        /// alongside invert 1 on its mouse_region and joystick_mouse
        /// groups, and bare 0 on its trigger groups. The wild default
        /// rings sit near 25000). An absent radius is NOT degenerate:
        /// Steam's default is a partial ring.</summary>
        private static bool IsWholePadEdgeZone(Dictionary<string, string> settings)
        {
            bool invert = settings.TryGetValue("edge_binding_invert", out var inv)
                && (inv ?? "").Trim() != "0";
            if (!settings.TryGetValue("edge_binding_radius", out var raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int radius))
            {
                return false;
            }
            return invert ? radius >= 32767 : radius <= 0;
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
            Gate2Descriptor = s.Gate2Descriptor,
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
            Gate2Descriptor = s.Gate2Descriptor,
            PartialReasonKey = s.PartialReasonKey,
        };

        /// <summary>The gated host source for a chord activator (v24), or
        /// null when the chord cannot ground (caller keeps the named
        /// skip). chord_button INDEXES k_eGamepadButtonBitMask, the same
        /// value space as gyro_button (v23): the shipped configurator
        /// declares the setting as {id:"Chord_ChordButton",
        /// visualizerType:"ActivatorButtonChordMask"} and renders it
        /// through the same enum glyph map as the gyro pickers, and the
        /// corpus census agrees (every authored value 1-31 is a grounded
        /// enum index; an authored MASK would carry BigInt values like
        /// the ratchet masks do, and none exists). The partner descriptor
        /// comes from the shared RatchetBitDescriptor enum map and rides
        /// GateDescriptor, the engine's chord second leg.
        ///
        /// <para>The clone deliberately DROPS the combined-output trigger
        /// identity (XboxButtonBit / MacroAxisTarget: a combined trigger
        /// fires on the slot's output bit, which the plain Full_Press row
        /// feeds partner-or-not, so chord macros must ride descriptor
        /// triggers where FillMacroTrigger adds the gate entry) and the
        /// AutomapTarget (a gated read is not the automap's plain read,
        /// so identity folding must not swallow the gate). A host that
        /// already carries a gate folds where the engine has a composed
        /// read: the single-pad half click (#9 B-1) becomes the windowed
        /// click ("Touchpad 0 Click Left", the v18 family), freeing the
        /// gate slot for the partner. Anything else with a busy gate slot
        /// (wedge click gates) stays ungroundable.</para></summary>
        /// <summary>True when the chord activator carries a usable
        /// chord_button (v26): the absent key and the 0 value are both the
        /// unset picker (the shared value space's own 0 is the gyro_button
        /// none/default sentinel, v18/v23, and Steam's serializer omits
        /// defaults), the precise config-error arm. Anything else proceeds
        /// to the enum grounding and keeps the generic net on failure.</summary>
        private static bool HasChordPartner(SteamInputActivator activator)
            => activator.Settings.TryGetValue("chord_button", out var raw)
                && int.TryParse((raw ?? "").Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int idx)
                && idx > 0;

        private static ResolvedSource ChordHost(Run run, SteamInputActivator activator,
            ResolvedSource source)
        {
            // 0 stays ungroundable like the absent key: the shared value
            // space's own 0 is the gyro_button none/default sentinel
            // (v18/v23), no corpus chord authors it, and Steam's
            // serializer omits defaults, so a 0 here is an unset picker,
            // not a RightTriggerFullPull chord. Callers split that arm
            // out via HasChordPartner (the ChordWithoutPartner class)
            // before reaching here.
            if (!activator.Settings.TryGetValue("chord_button", out var raw)
                || !int.TryParse((raw ?? "").Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int idx)
                || idx <= 0)
            {
                return null;
            }
            string partner = RatchetBitDescriptor(idx, run.SinglePadTrackpads);
            if (partner == null) return null;

            string descriptor = source.Descriptor;
            string primaryGate = partner;
            string secondGate = null;
            if (!string.IsNullOrEmpty(source.GateDescriptor))
            {
                string folded = FoldHalfClickGate(source.Descriptor, source.GateDescriptor);
                if (folded != null)
                {
                    descriptor = folded;
                }
                else
                {
                    // Un-foldable primary gate (v26): a wedge already
                    // gated on its half's contact / click keeps that
                    // gate, and the chord partner rides the SECOND AND
                    // companion (MappingSource.Gate2Descriptor; macro
                    // triggers append a second ANDed entry). Wild
                    // witness 3290233831: single-pad trackpad D-pad
                    // wedges chorded with the right bumper.
                    primaryGate = source.GateDescriptor;
                    secondGate = partner;
                }
            }
            return new ResolvedSource
            {
                Descriptor = descriptor,
                HalfAxis = source.HalfAxis,
                Invert = source.Invert,
                DeadZone = source.DeadZone,
                TrackpadFeature = source.TrackpadFeature,
                IsAnalogTriggerPull = source.IsAnalogTriggerPull,
                GateDescriptor = primaryGate,
                Gate2Descriptor = secondGate,
                PartialReasonKey = source.PartialReasonKey,
            };
        }

        /// <summary>Folds a half-click AND gate into the engine's windowed
        /// click read: "Touchpad {p} Click" gated on "Touchpad {p}
        /// TouchLeft|TouchRight" (the B-1 single-pad shape) IS "Touchpad
        /// {p} Click Left|Right" (SourceCoercion's v18 windowed-click
        /// family), so the gate slot frees up for a chord partner. Null
        /// for any other descriptor/gate pairing.</summary>
        private static string FoldHalfClickGate(string descriptor, string gate)
        {
            descriptor = descriptor ?? "";
            gate = gate ?? "";
            if (!descriptor.StartsWith("Touchpad ", StringComparison.Ordinal)
                || !descriptor.EndsWith(" Click", StringComparison.Ordinal))
            {
                return null;
            }
            string pad = descriptor.Substring(0, descriptor.Length - " Click".Length);
            if (gate == pad + " TouchLeft") return descriptor + " Left";
            if (gate == pad + " TouchRight") return descriptor + " Right";
            return null;
        }

        /// <summary>Walks a group's named inputs and translates each
        /// activator's bindings against the resolved physical source.</summary>
        private void TranslateMemberGroup(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings,
            IReadOnlyList<string> onlyInputs = null)
        {
            bool requiresClick = RequiresClick(slot, group, settings);
            int groupDeadZonePct = GroupDeadZonePercent(settings);
            var half = PhysicalSlotResolver.HalfFor(slot, run.SinglePadTrackpads);

            foreach (var inputName in group.Inputs.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (onlyInputs != null && !onlyInputs.Contains(inputName, StringComparer.OrdinalIgnoreCase))
                    continue;
                var input = group.Inputs[inputName];
                if (input.Activators.Count == 0) continue;

                var source = PhysicalSlotResolver.Resolve(slot, inputName, run.NintendoLabels,
                    run.SinglePadTrackpads);
                // Outer Ring edge members (v16 whole-pad, v17 sticks, v26
                // trackpad rings): Steam's edge member fires while the
                // finger / stick is outside edge_binding_radius, or inside
                // it with edge_binding_invert ("If set, the command will
                // be sent when inside the radius instead of outside",
                // shipped EdgeBindingInvert strings).
                //
                // Trackpads: a zone that covers the WHOLE pad (invert with
                // the radius at the 32767 ceiling, or radius 0 un-inverted)
                // IS the touch read (corpus 3456927474 authors its
                // mouse_delta nudge this way). A PARTIAL ring BUILDS on
                // the engine's finger-ring read (v26, "Touchpad {p}
                // Finger 0 Ring", the stick ring's contract on the touch
                // surface): the authored radius rides DeadZone as a
                // percent and edge_binding_invert rides Invert, so the
                // geometry keys are CONSUMED and the old
                // MouseRegionTuningDropped ring note is gone.
                if (source == null
                    && inputName.Equals("edge", StringComparison.OrdinalIgnoreCase)
                    && PhysicalSlotResolver.IsTrackpad(slot))
                {
                    if (IsWholePadEdgeZone(settings))
                    {
                        source = PhysicalSlotResolver.Resolve(slot, "touch", run.NintendoLabels,
                            run.SinglePadTrackpads);
                    }
                    else
                    {
                        int tpIdx = PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads);
                        string tpSfx = PhysicalSlotResolver.HalfSuffix(half);
                        int radiusRaw = ParseIntSetting(settings, "edge_binding_radius", 25000);
                        bool ringInvert = settings.TryGetValue("edge_binding_invert", out var inv)
                            && (inv ?? "").Trim() != "0";
                        source = new ResolvedSource
                        {
                            Descriptor = $"Touchpad {tpIdx} Finger 0 Ring{tpSfx}",
                            Invert = ringInvert,
                            DeadZone = Math.Clamp(
                                (int)Math.Round(radiusRaw * 100.0 / 32767.0), 1, 100),
                        };
                    }
                }
                // Triggers (v26): the edge member is the pull-threshold
                // crossing, and edge_binding_radius is WHERE along the
                // travel it fires (0..32767 of full pull), so an authored
                // radius overrides the soft-pull default threshold
                // instead of dropping silently.
                if (source != null && source.IsAnalogTriggerPull
                    && inputName.Equals("edge", StringComparison.OrdinalIgnoreCase)
                    && TryParseDeadZoneRaw(settings, "edge_binding_radius", out int trigRadius))
                {
                    source = WithDeadZone(source, Math.Clamp(
                        (int)Math.Round(trigRadius * 100.0 / 32767.0), 1, 100));
                }
                // Sticks (v17): the ring member resolves onto the
                // deflection-magnitude family ("Gamepad Left/RightStickRing",
                // the flick-stick pair read). The authored radius
                // (deadzone_outer_radius's 0..32767 scale, the v11
                // grounding) rides the source DeadZone as a percent and
                // edge_binding_invert rides Invert (inner ring: deflected
                // but inside the radius, gated by the engine's ring read
                // on its rest floor). An absent radius takes Steam's untouched
                // slider default, which the serializer writes as
                // 24995..24999 across the corpus and Valve's own templates:
                // 25000, 76 percent.
                if (source == null
                    && inputName.Equals("edge", StringComparison.OrdinalIgnoreCase)
                    && PhysicalSlotResolver.IsStick(slot))
                {
                    int radiusRaw = ParseIntSetting(settings, "edge_binding_radius", 25000);
                    bool ringInvert = settings.TryGetValue("edge_binding_invert", out var inv)
                        && (inv ?? "").Trim() != "0";
                    source = new ResolvedSource
                    {
                        Descriptor = slot == SteamSlot.Joystick
                            ? "Gamepad LeftStickRing"
                            : "Gamepad RightStickRing",
                        HalfAxis = true,
                        Invert = ringInvert,
                        DeadZone = Math.Clamp(
                            (int)Math.Round(radiusRaw * 100.0 / 32767.0), 1, 100),
                    };
                }
                // The group inner deadzone lands on the axis-natured member
                // reads (stick-as-dpad wedges). Explicit thresholds (the
                // trigger click's 75 / edge's 15) encode reachable-range
                // semantics and stay.
                if (source != null && groupDeadZonePct > 0
                    && source.HalfAxis && source.DeadZone == 0)
                {
                    source = WithDeadZone(source, groupDeadZonePct);
                }
                // Gravity-lean wedges (v26): with no authored deadzone the
                // tilt threshold falls back to a comfortable 22.5-degree
                // wedge (DeadZone 25 on the 90-degree lean scale) instead
                // of the row-threshold default's 45 degrees. The wild
                // witnesses that DO author one land in the same band
                // (707592150: deadzone 14010 = 38 degrees).
                if (source != null && source.HalfAxis && source.DeadZone == 0
                    && (source.Descriptor ?? "").StartsWith("Gyro Lean", StringComparison.Ordinal))
                {
                    source = WithDeadZone(source, 25);
                }
                string inputPath = $"{path}/{inputName}";
                if (source == null)
                {
                    // Edge members resolve on every host Steam's grammar
                    // uses (triggers, trackpads, sticks, the v17 census
                    // guard), so a null source here is the safety net:
                    // generic for hand-edited inputs, PRECISE for the
                    // Steam Link on-screen touch controls (v26), which no
                    // physical device PadForge drives can carry.
                    string nullReason = PhysicalSlotResolver.IsMobileTouchOnlyToken(inputName)
                        ? TranslationReasons.MobileTouchSurfaceOnly
                        : TranslationReasons.UnknownPhysicalInput;
                    foreach (var act in input.Activators)
                        foreach (var b in act.Bindings)
                            ReportSkipUnlessSilent(run, nullReason,
                                inputPath, b, slotArg: slot.ToString(), inputArg: inputName);
                    continue;
                }

                // Click-gate the trackpad D-pad wedges when the group
                // requires a pad click (the classic Steam Controller feel).
                // Half-hosted groups (#9 B-1, built in v18): the wedge
                // gesture itself is anchor-relative and whole-pad, so the
                // half restriction rides the per-source AND gate: the
                // windowed click ("Touchpad 0 Click Left") when the group
                // requires a click, else the half's contact window
                // ("Touchpad 0 Finger 0 Down Left"), so only the hosting
                // half's finger fires the group.
                bool isWedge = inputName.StartsWith("dpad_", StringComparison.OrdinalIgnoreCase);
                string clickGate = null;
                if (isWedge && PhysicalSlotResolver.IsTrackpad(slot))
                {
                    int padIdx = PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads);
                    string windowSfx = PhysicalSlotResolver.HalfSuffix(half);
                    if (requiresClick)
                        clickGate = $"Touchpad {padIdx} Click{windowSfx}";
                    else if (half != TrackpadHalf.Whole)
                        clickGate = $"Touchpad {padIdx} Finger 0 Down{windowSfx}";
                }
                // The gate rides the source too, so macro-shaped
                // translations of the wedge (set_led, Long_Press keys)
                // inherit the click requirement in their device-free
                // trigger entries, not only the rows (wave 3).
                if (clickGate != null && source.GateDescriptor == null)
                    source = WithGate(source, clickGate);

                // Always-On members (v25): the constant-true read fires
                // forever, so macro-shaped bindings (a set_led color per
                // set, a mouse_delta nudge) must carry the hosting layer
                // as their gate or a non-Base set's command would fire at
                // profile apply instead of at set entry. Rows scope
                // through their own LayerMask; the macros minted in this
                // member's window get the stamp here.
                // #254 A-2: the AddMacro funnel stamps every emitted
                // macro with the current action-set layer, superseding the
                // old always-on-only stamping loop that lived here.
                TranslateInput(run, preset, input, source, clickGate, layer, inputPath);
            }
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

        /// <summary>scrollwheel on a trackpad (v10 G4) or a stick (v12):
        /// Steam spins a virtual wheel from circular finger motion (or
        /// stick rotation) and fires scroll_clockwise /
        /// scroll_counterclockwise per detent. PadForge has no
        /// circular-motion read; the nearest live channel is a vertical
        /// drag axis, so wheel-shaped bindings become KbmScroll rows fed
        /// by it: clockwise = drag down (+Y in SDL's convention, the
        /// physical wheel gesture), counterclockwise = the inverted read.
        /// Trackpads read the finger delta ("Touchpad {p} Finger 0 Y", the
        /// same read the mouse modes use, no feature toggle); sticks read
        /// the Y deflection ("Gamepad {stick}Y") with the group inner
        /// deadzone keeping centered rest jitter out of the wheel.
        /// Bindings that are not mouse_wheel (keys on a wheel detent) ride
        /// the one-shot tap walk on the detent direction read since v15
        /// (stick drag wedge / trackpad swipe gesture), one fire per
        /// flick. One geometry Partial per group names the
        /// rotation-vs-drag approximation. The click member translates as
        /// a normal member either way.</summary>
        private void TranslateScrollWheel(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings)
        {
            run.CurrentLayer = layer; // #254 A-2
            string drag;
            int dragDeadZone = 0;
            if (PhysicalSlotResolver.IsStick(slot))
            {
                drag = slot == SteamSlot.Joystick ? "Gamepad LeftStickY" : "Gamepad RightStickY";
                dragDeadZone = GroupDeadZonePercent(settings);
            }
            else
            {
                int p = PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads);
                string sfx = PhysicalSlotResolver.HalfSuffix(
                    PhysicalSlotResolver.HalfFor(slot, run.SinglePadTrackpads));
                drag = $"Touchpad {p} Finger 0 Y{sfx}";
            }
            bool emitted = false;
            // (target, net invert) pairs already emitted. The default
            // wheel is symmetric (clockwise scrolls down AND
            // counterclockwise scrolls up name the same drag-to-wheel
            // map), and both sources summing on one row would double the
            // scroll rate, so the twin folds into the first emission.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var memberName in new[] { "scroll_clockwise", "scroll_counterclockwise" })
            {
                if (!group.Inputs.TryGetValue(memberName, out var input)
                    || input.Activators.Count == 0)
                {
                    continue;
                }
                bool memberFlip = memberName == "scroll_counterclockwise";
                var source = new ResolvedSource
                {
                    Descriptor = drag,
                    Invert = memberFlip,
                    DeadZone = dragDeadZone,
                };
                // One-shot detent read for the NON-wheel bindings (v15):
                // sticks flick the drag wedge (clockwise = deflect down =
                // Y upper half in SDL frame), trackpads ride the one-shot
                // swipe gestures (clockwise = SwipeDown, the same
                // drag-to-wheel map the rows use), so a key on a detent
                // fires once per flick / swipe instead of skipping.
                var tapSource = PhysicalSlotResolver.IsStick(slot)
                    ? new ResolvedSource
                    {
                        Descriptor = drag,
                        HalfAxis = true,
                        Invert = memberFlip,
                        DeadZone = dragDeadZone,
                    }
                    : new ResolvedSource
                    {
                        Descriptor = $"Touchpad {PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads)} "
                            + (memberFlip ? "SwipeUp" : "SwipeDown"),
                        TrackpadFeature = PhysicalSlotResolver.FeatureSwipes,
                    };
                string inputPath = $"{path}/{memberName}";
                foreach (var activator in input.Activators)
                {
                    string actPath = $"{inputPath}/{(activator.Type ?? "").Trim()}";
                    foreach (var binding in activator.Bindings)
                    {
                        // Only the wheel-shaped bindings can ride a
                        // continuous drag axis: the finger X/Y reads have
                        // no bool coercion, so a key or button row fed by
                        // them would never fire. Everything else lowers
                        // through the one-shot tap walk on the detent
                        // direction read (v15).
                        var wheel = string.Equals((binding.Type ?? "").Trim(), "mouse_wheel",
                            StringComparison.OrdinalIgnoreCase)
                                ? ParseWheelParam(binding.Param) : null;
                        if (wheel == null)
                        {
                            int before = run.Profile.Macros.Count + run.Activators.Count;
                            TranslateOneShotSwipeBinding(run, preset, binding, tapSource,
                                layer, actPath, input.Name);
                            // The geometry Partial covers real emissions
                            // only; a skipped unknown key stays outside it.
                            if (run.Profile.Macros.Count + run.Activators.Count > before)
                                emitted = true;
                            continue;
                        }
                        if (!seen.Add($"{wheel.Value.Target}|{wheel.Value.Invert ^ memberFlip}"))
                        {
                            emitted = true; // represented by the twin's source
                            continue;
                        }
                        TranslateBinding(run, preset, binding, source, clickGate: null,
                            layer, actPath, soft: false, onRelease: false,
                            holdRepeats: false, intervalMs: 100, toggle: false,
                            input.Name);
                        emitted = true;
                    }
                }
            }

            // Scroll Wheel List (v16). Steam steps the wheel through
            // scroll_wheel_list_0..N, firing the REACHED item's binding
            // per detent ("You can assign a button or key to be sent to
            // the game when the Nth item is reached"), wrapping per
            // scroll_wrap ("When scrolling in a single direction, the
            // list will restart once either end is reached"). Lowered to
            // ONE CycleTapList macro triggered on the clockwise detent
            // read (the same one-shot shape the non-wheel detent bindings
            // ride: stick drag-down wedge / trackpad SwipeDown), whose
            // per-action index advances one item per fire. Forward
            // stepping only: a second trigger lane cannot share the
            // per-action index, so the counterclockwise back-step
            // collapses onto the forward walk, and the group's geometry
            // Partial names the wheel approximation. The corpus lists are
            // unlabeled plain bindings (key_press 1..9 on 2790927974,
            // wheel ticks on 3353604014), so the cycle primitive is the
            // honest lowering, not the labeled-grid hotbar shape.
            var cycleSteps = new List<TranslatedCycleStep>();
            var cycleEntries = new List<(string Path, string Raw, string Desc)>();
            var listMembers = group.Inputs.Keys
                .Where(k => k.StartsWith("scroll_wheel_list_", StringComparison.OrdinalIgnoreCase))
                .Select(k => (Name: k, Index: ParseTrailingInt(k)))
                .Where(m => m.Index >= 0)
                .OrderBy(m => m.Index)
                .ToList();
            foreach (var member in listMembers)
            {
                string listPath = $"{path}/{member.Name}";
                foreach (var act in group.Inputs[member.Name].Activators)
                {
                    foreach (var b in act.Bindings)
                    {
                        var step = TryBuildCycleStep(run, preset, b, listPath);
                        if (step == null) continue;
                        step.ItemIndex = member.Index;
                        cycleSteps.Add(step);
                        cycleEntries.Add((listPath, b.Raw,
                            $"Wheel list step {member.Index + 1}: {DescribeCycleStep(step)}"));
                    }
                }
            }
            if (cycleSteps.Count > 0)
            {
                var detent = PhysicalSlotResolver.IsStick(slot)
                    ? new ResolvedSource
                    {
                        Descriptor = drag,
                        HalfAxis = true,
                        DeadZone = dragDeadZone,
                    }
                    : new ResolvedSource
                    {
                        Descriptor = $"Touchpad {PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads)} SwipeDown",
                        TrackpadFeature = PhysicalSlotResolver.FeatureSwipes,
                    };
                var cycle = new TranslatedMacro
                {
                    Name = $"Wheel list ({SlotToken(slot)})",
                    Action = TranslatedMacroAction.CycleList,
                    TriggerMode = "OnPress",
                    ConsumeTrigger = false,
                    CycleSteps = cycleSteps,
                    // scroll_wrap consumed (v16). Absent = wrap: the
                    // forward-only walk would otherwise dead-end at the
                    // last item with no back-step to free it.
                    CycleWrap = !settings.TryGetValue("scroll_wrap", out var wrapRaw)
                        || (wrapRaw ?? "").Trim() != "0",
                };
                FillMacroTrigger(cycle, detent);
                run.AddMacro(cycle);
                foreach (var e in cycleEntries)
                {
                    run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                        e.Path, e.Raw, emitted: e.Desc);
                }
                emitted = true;
            }

            // One geometry Partial per group, covering the drag-for-
            // rotation approximation on the detent rows AND the cycle's
            // forward-only stepping.
            if (emitted)
            {
                run.Report.Add(TranslationStatus.Partial,
                    TranslationReasons.ScrollWheelApproximated, path);
            }

            TranslateMemberGroup(run, preset, group, slot, layer, path, settings,
                onlyInputs: new[] { "click" });
        }

        /// <summary>2dscroll on a trackpad (v10 G3): Steam's directional
        /// swipe fires a dpad_* member per swipe step. The gesture
        /// engine's one-shot swipe fires ("Touchpad {p} SwipeUp/Down/
        /// Left/Right", GestureRecognizer end-of-gesture classification)
        /// are the same construct read on finger lift, self-armed at
        /// apply since v14, so each member's bindings translate against
        /// the matching swipe descriptor through the normal walk.</summary>
        private void TranslateSwipeGroup(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings)
        {
            int p = PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads);
            foreach (var inputName in group.Inputs.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                string dir = inputName.ToLowerInvariant() switch
                {
                    "dpad_north" => "SwipeUp",
                    "dpad_south" => "SwipeDown",
                    "dpad_east" => "SwipeRight",
                    "dpad_west" => "SwipeLeft",
                    _ => null,
                };
                var input = group.Inputs[inputName];
                if (input.Activators.Count == 0) continue;
                string inputPath = $"{path}/{inputName}";
                if (dir == null)
                {
                    // Non-swipe members (the mode's own click command)
                    // are plain surface inputs and translate through the
                    // normal walk (v12); only members PadForge has no
                    // source for keep the skip.
                    var member = PhysicalSlotResolver.Resolve(slot, inputName,
                        run.NintendoLabels, run.SinglePadTrackpads);
                    if (member != null)
                    {
                        TranslateInput(run, preset, input, member, clickGate: null, layer, inputPath);
                        continue;
                    }
                    foreach (var act in input.Activators)
                        foreach (var b in act.Bindings)
                            ReportSkipUnlessSilent(run, TranslationReasons.UnknownPhysicalInput,
                                inputPath, b, slotArg: slot.ToString(), inputArg: inputName);
                    continue;
                }
                var source = new ResolvedSource
                {
                    Descriptor = $"Touchpad {p} {dir}",
                    TrackpadFeature = PhysicalSlotResolver.FeatureSwipes,
                };
                TranslateInput(run, preset, input, source, clickGate: null, layer, inputPath);
            }
        }

        /// <summary>2dscroll on a stick (v12) or any other non-trackpad,
        /// non-gyro host (v15): Steam's directional swipe is a flick toward
        /// a direction, firing the binding once per flick. The wedge read
        /// the stick-as-dpad members already resolve to (half of the
        /// matching axis, group deadzone honored) IS that construct once it
        /// drives a one-shot macro on its rising edge: entering the wedge
        /// fires exactly once and re-centering re-arms it. A button-natured
        /// member (a physical dpad host) fires once per press the same way.
        /// So each dpad_* member lowers its bindings onto one-shot
        /// descriptor-triggered macros (KeyTap / MouseButtonTap /
        /// VcButtonTap / VcAxisTap / MouseWheelTap plus the one-shot
        /// controller_action macros), with the wedge's half-axis shape
        /// carried on the trigger entry, and mode shifts / layer verbs
        /// lower to half-stamped shift activators (v15). Press / release /
        /// long-press activator distinctions have no carrier on a one-shot
        /// flick (the same collapse the DoubleTap path documents), so every
        /// activator's bindings fire on the wedge entry edge. Non-dpad
        /// members (the mode's click command) translate through the normal
        /// walk; members with no PadForge source keep
        /// UnknownPhysicalInput.</summary>
        private void TranslateStickSwipeGroup(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings)
        {
            run.CurrentLayer = layer; // #254 A-2 (audit C9): swipe groups emit macros
            int dzPct = GroupDeadZonePercent(settings);
            foreach (var inputName in group.Inputs.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var input = group.Inputs[inputName];
                if (input.Activators.Count == 0) continue;
                string inputPath = $"{path}/{inputName}";

                var source = PhysicalSlotResolver.Resolve(slot, inputName,
                    run.NintendoLabels, run.SinglePadTrackpads);
                if (source == null)
                {
                    foreach (var act in input.Activators)
                        foreach (var b in act.Bindings)
                            ReportSkipUnlessSilent(run, TranslationReasons.UnknownPhysicalInput,
                                inputPath, b, slotArg: slot.ToString(), inputArg: inputName);
                    continue;
                }

                if (!inputName.StartsWith("dpad_", StringComparison.OrdinalIgnoreCase))
                {
                    // The mode's own click command is a plain button and
                    // keeps continuous press semantics.
                    TranslateInput(run, preset, input, source, clickGate: null, layer, inputPath);
                    continue;
                }

                // The group inner deadzone shapes how far a flick must
                // travel before it counts, the same seam the member walk
                // applies to wedge rows.
                if (dzPct > 0 && source.HalfAxis && source.DeadZone == 0)
                    source = WithDeadZone(source, dzPct);

                foreach (var activator in input.Activators)
                {
                    string actPath = $"{inputPath}/{(activator.Type ?? "").Trim()}";
                    int macrosBefore = run.Profile.Macros.Count;
                    foreach (var binding in activator.Bindings)
                        TranslateOneShotSwipeBinding(run, preset, binding, source, layer, actPath,
                            input.Name);
                    EmitHapticPulse(run, activator, source, input.Name, actPath, "OnPress", holdMs: 0);
                    ConsumeActivatorDelays(run, activator, actPath, macrosBefore);
                }
            }
        }

        /// <summary>2dscroll on the gyro (v15): a flick of the pad itself.
        /// Gyro rates are SIGNED bipolar axes, so each dpad_* member reads
        /// the matching HALF of the matching rate descriptor: up = "Gyro
        /// Pitch" upper half, down = its lower half, west = "Gyro Yaw"
        /// upper half, east = its lower half. Sign frame per SDL_sensor.h
        /// (+X right / +Y up / +Z toward the player, counterclockwise
        /// positive), with Dolphin's SDLGamepad.h SDL_AXES_GYRO ("Pitch
        /// Up" = axis 0 scale +1, "Yaw Left" = axis 1 scale +1) as the
        /// proven consumer of the same frame: positive pitch = nose up,
        /// positive yaw = nose left. The trigger entry keeps DeadZone
        /// unset, so the engine's own gyro-as-button rate threshold
        /// (SourceCoercion.GyroButtonThreshold, 30 deg/s: "a deliberate
        /// twist, not idle hand tremor") gates the flick and dropping
        /// below it re-arms the one-shot. Steam's group inner deadzone is
        /// a stick-deflection quantity with no grounded rate meaning, so
        /// it does not remap onto the rate threshold. Bindings lower
        /// through the same one-shot walk the stick flicks use; the gyro
        /// has no non-dpad members to resolve (Resolve returns null for
        /// the whole slot), so anything else keeps
        /// UnknownPhysicalInput.</summary>
        private void TranslateGyroSwipeGroup(Run run, SteamInputPreset preset,
            SteamInputGroup group, string layer, string path)
        {
            run.CurrentLayer = layer; // #254 A-2 (audit C9): swipe groups emit macros
            foreach (var inputName in group.Inputs.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var input = group.Inputs[inputName];
                if (input.Activators.Count == 0) continue;
                string inputPath = $"{path}/{inputName}";

                var source = inputName.ToLowerInvariant() switch
                {
                    "dpad_north" => new ResolvedSource
                    { Descriptor = "Gyro Pitch", HalfAxis = true },
                    "dpad_south" => new ResolvedSource
                    { Descriptor = "Gyro Pitch", HalfAxis = true, Invert = true },
                    "dpad_west" => new ResolvedSource
                    { Descriptor = "Gyro Yaw", HalfAxis = true },
                    "dpad_east" => new ResolvedSource
                    { Descriptor = "Gyro Yaw", HalfAxis = true, Invert = true },
                    _ => null,
                };
                if (source == null)
                {
                    foreach (var act in input.Activators)
                        foreach (var b in act.Bindings)
                            ReportSkipUnlessSilent(run, TranslationReasons.UnknownPhysicalInput,
                                inputPath, b, slotArg: SteamSlot.Gyro.ToString(), inputArg: inputName);
                    continue;
                }

                foreach (var activator in input.Activators)
                {
                    string actPath = $"{inputPath}/{(activator.Type ?? "").Trim()}";
                    int macrosBefore = run.Profile.Macros.Count;
                    foreach (var binding in activator.Bindings)
                        TranslateOneShotSwipeBinding(run, preset, binding, source, layer, actPath,
                            input.Name);
                    EmitHapticPulse(run, activator, source, input.Name, actPath, "OnPress", holdMs: 0);
                    ConsumeActivatorDelays(run, activator, actPath, macrosBefore);
                }
            }
        }

        /// <summary>One binding of a one-shot swipe member (v12, closed
        /// v15): every kind lowers on the flick's rising edge. Keys, mouse
        /// buttons, and VC buttons tap; trigger pulls and stick directions
        /// ride the AxisHold tap (VcAxisTap); mouse_wheel fires one
        /// discrete detent (MouseWheelTap); mode shifts and layer verbs
        /// lower to half-stamped shift activators (Toggle / Latch / Cycle,
        /// the flick's one-shot fires their edge); every other
        /// controller_action routes through the canonical verb lowering
        /// (one-shot verbs fire per flick, Steam-client-only verbs keep
        /// their named skips); game_action feeds the per-preset aggregate.
        /// Only genuinely unknown vocabulary still skips.</summary>
        private void TranslateOneShotSwipeBinding(Run run, SteamInputPreset preset,
            SteamInputBinding binding, ResolvedSource source, string layer, string actPath,
            string inputName)
        {
            string type = (binding.Type ?? "").Trim().ToLowerInvariant();
            switch (type)
            {
                case "key_press":
                {
                    string keyName = FirstToken(binding.Param);
                    if (!SteamInputVkTable.TryResolve(keyName, out byte vk, out _))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownKey,
                            actPath, binding.Raw, args: keyName);
                        break;
                    }
                    // One tap per flick via SendInput (any VK works on the
                    // macro form, v10 G11).
                    EmitKeyMacro(run, binding, source, actPath,
                        (TranslatedMacroAction.KeyTap, "OnPress"), vk, intervalMs: 100,
                        keyName, inputName);
                    break;
                }

                case "mouse_button":
                {
                    if (!SteamInputVkTable.TryResolveMouseButtonIndex(binding.Param, out _))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownMouseButton,
                            actPath, binding.Raw, args: binding.Param);
                        break;
                    }
                    EmitMouseTapMacro(run, binding, source, actPath, inputName,
                        triggerMode: "OnPress");
                    break;
                }

                case "mouse_wheel":
                {
                    // One discrete detent per flick (v15).
                    var wheel = ParseWheelParam(binding.Param);
                    if (wheel == null)
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                            actPath, binding.Raw, args: $"mouse_wheel {binding.Param}");
                        break;
                    }
                    EmitWheelTapMacro(run, binding, source, actPath, wheel.Value, inputName,
                        triggerMode: "OnPress");
                    break;
                }

                case "xinput_button":
                {
                    if (!XInputTargetTable.TryResolve(binding.Param, out var xt))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownXInputButton,
                            actPath, binding.Raw, args: binding.Param);
                        break;
                    }
                    if (xt.IsTriggerAxis || xt.IsStickAxis)
                    {
                        // Axis-natured targets ride the timed-axis channel
                        // (v15): one AxisHold tap per flick.
                        EmitVcAxisTapMacro(run, binding, source, actPath, xt, inputName,
                            triggerMode: "OnPress");
                        break;
                    }
                    EmitVcTapMacro(run, binding, source, actPath, xt, inputName,
                        triggerMode: "OnPress");
                    break;
                }

                case "mode_shift":
                    // A flick toggles the shift (v15): the one-shot wedge /
                    // rate read drives the activator's edge through the
                    // Kind=Axis half stamp.
                    TranslateModeShift(run, preset, binding, source, actPath,
                        toggle: true, oneShotHost: true);
                    break;

                case "controller_action":
                    // The canonical verb walk (v15): one-shot verbs (cursor
                    // warp, set_led, camera_reset, screenshot, on-screen
                    // keyboard) fire once per flick through FillMacroTrigger;
                    // layer verbs lower to half-stamped activators; the
                    // Steam-client-only families keep their named skips.
                    TranslateControllerAction(run, preset, binding, source, layer, actPath,
                        onRelease: false, inputName, oneShotHost: true);
                    break;

                case "game_action_analog": // the analog sibling, same Steam-session surface (v24)
                case "game_action":
                    run.GameActionsByPreset[preset.Id] =
                        run.GameActionsByPreset.GetValueOrDefault(preset.Id) + 1;
                    break;

                default:
                    // Empty placeholder rows add noise, not signal (the
                    // ReportSkipUnlessSilent contract); only genuinely
                    // unknown vocabulary gets the named skip.
                    if (type.Length == 0) break;
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                        actPath, binding.Raw, args: binding.Type ?? "");
                    break;
            }
        }

        // MenuTuningDropped retired in v26: the one key it ever named,
        // "sensitivity" (shipped configurator "In-Menu Sensitivity"),
        // BUILT as MenuDefinitionEntry.SensitivityPercent, a hover-vector
        // scale consumed by the menu runtime before selection. The key
        // plus its locale strings were deleted.

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
            // Host surface: a stick (deflection hovers), a trackpad (touch
            // position hovers), the physical dpad / face diamond
            // (button-pair hosts: v25 for radials, v26 for hotbar grids,
            // where direction presses STEP the persistent selection
            // through MenuEvaluator.StepButtonPairGrid, matching Steam's
            // own hotbar strings: "the menu will remember the selected
            // command from the previous time it was invoked"), or, since
            // v26, the GYRO through the gravity-lean pair: a gyro-hosted
            // touch_menu hovers by TILTING the controller, which is the
            // Custom opener with "Gyro Lean X/Y" as the steering axes
            // (deflection-engaged like a stick host). Only a host with no
            // direction read at all keeps the named skip.
            string host;
            int hostHalf = 0;
            string customX = "", customY = "";
            if (PhysicalSlotResolver.IsStick(slot))
            {
                host = slot == SteamSlot.Joystick ? "Gamepad LeftStick" : "Gamepad RightStick";
            }
            else if (PhysicalSlotResolver.IsTrackpad(slot))
            {
                host = $"Touchpad {PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads)}";
                hostHalf = (int)PhysicalSlotResolver.HalfFor(slot, run.SinglePadTrackpads);
            }
            else if (slot == SteamSlot.Dpad)
            {
                host = "Gamepad DPad";
            }
            else if (slot == SteamSlot.ButtonDiamond)
            {
                host = "Gamepad Diamond";
            }
            else if (slot == SteamSlot.Gyro)
            {
                host = "Custom";
                customX = PadForge.Engine.Common.Mapping.SourceCoercion.GyroLeanXDescriptor;
                customY = PadForge.Engine.Common.Mapping.SourceCoercion.GyroLeanYDescriptor;
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
                CustomXDescriptor = customX,
                CustomYDescriptor = customY,
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
            // Cap at the runtime's own clamp (menus engage-clamp 1..95):
            // storing 96-100 while reporting the conversion Clean hid a
            // silent runtime change of the imported value.
            int engageDz = GroupDeadZonePercent(settings);
            if (engageDz > 0) entry.EngageDeadzonePercent = Math.Min(engageDz, 95);
            // In-Menu Sensitivity (v26): the "sensitivity" key scales the
            // hover vector before selection (MenuDefinitionEntry
            // .SensitivityPercent, 100 = identity; wild witness 2846236146
            // authors 150), so the old MenuTuningDropped note is gone.
            int menuSens = ParseIntSetting(settings, "sensitivity", 0);
            if (menuSens > 0) entry.SensitivityPercent = Math.Clamp(menuSens, 10, 400);

            foreach (var cell in cells)
            {
                // Cell icons carry as authored names (v21): the overlay
                // resolves them against the local Steam client's own
                // binding-icon art at display time (import users have
                // Steam installed, the PNGs are on disk beside the config
                // they subscribed to) and falls back to the text label
                // when the file is absent. Only a reference outside the
                // client's bare-filename shape stays behind, named
                // precisely so nothing is dropped in silence.
                string icon = CellIcon(cell.Input, out string unresolvedRef);
                entry.Items.Add(new PadForge.Engine.Menus.MenuItemDefinition
                {
                    Index = cell.Index,
                    Label = CellLabel(cell.Input),
                    Icon = icon,
                });
                if (unresolvedRef != null)
                {
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.MenuIconUnresolved,
                        $"{path}/touch_menu_button_{cell.Index}", args: unresolvedRef);
                }
            }
            run.Profile.Menus.Add(entry);

            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MenuEmitted, path,
                emitted: $"{(radial ? "Radial" : "Grid")} menu {menuId} on {host}: "
                    + $"{cells.Count} bound cells",
                args: cells.Count.ToString(CultureInfo.InvariantCulture));

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
            // the hosting surface. Button-pair hosts (v25) have no click /
            // touch surface distinct from the selector buttons themselves,
            // so the tail walk is skipped there (nothing to resolve).
            if (PhysicalSlotResolver.IsStick(slot) || PhysicalSlotResolver.IsTrackpad(slot))
            {
                TranslateMemberGroup(run, preset, group, slot, layer, path, settings,
                    onlyInputs: new[] { "click", "touch" });
            }
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

        /// <summary>Icon name for a menu cell (v21): the first binding's
        /// authored icon reference, carried when it is a bare Steam
        /// binding-icon file name (the client's own art shape, e.g.
        /// "ghost_050_menu_0030.png"). A reference outside that shape (a
        /// path, an invalid character set) returns empty and comes back
        /// through <paramref name="unresolvedRef"/> so the caller can name
        /// the exact file in a Partial note. Same activator walk order as
        /// <see cref="CellLabel"/>, first icon-bearing binding wins.</summary>
        private static string CellIcon(SteamInputInput input, out string unresolvedRef)
        {
            unresolvedRef = null;
            foreach (var act in input.Activators)
            {
                foreach (var b in act.Bindings)
                {
                    string reference = b.Icon;
                    if (string.IsNullOrEmpty(reference))
                    {
                        // A ".png" the field parse could not shape into a
                        // token (or one hiding outside the icon fields)
                        // still names itself rather than vanishing.
                        if ((b.Raw ?? "").Contains(".png", StringComparison.OrdinalIgnoreCase)
                            && b.Icon == null)
                        {
                            reference = PngReference(b.Raw);
                        }
                        if (string.IsNullOrEmpty(reference)) continue;
                    }
                    // "@" marks an APP-PROVIDED icon (v24): the shipped
                    // configurator files every "@"-prefixed name under the
                    // AppIcons category and validates it against
                    // SteamClient.Input.GetTouchMenuIconsForApp(appid)
                    // (steamui/chunk~2dcc5aaf7.js: CategoryForFilename's
                    // startsWith("@") branch and the m_mapAppIcons check),
                    // art the GAME ships and the client serves from its
                    // internal /appcontrollericons route. Corpus witness:
                    // the seven VRChat configs' @gesture_*.png and CK3's
                    // @my_realm.png. No local directory to probe (the
                    // files are not in the client's binding-icon dirs and
                    // the per-app manifest is client-internal), so the
                    // cell degrades SILENTLY to its text label, the same
                    // fallback the overlay uses for any absent file:
                    // icons are cosmetic and the label carries the cell.
                    if (reference.StartsWith("@", StringComparison.Ordinal))
                        return "";
                    if (PadForge.Engine.Menus.MenuItemDefinition.IsValidIconName(reference))
                        return reference;
                    unresolvedRef = reference;
                    return "";
                }
            }
            return "";
        }

        /// <summary>The comma / whitespace-delimited token around the first
        /// ".png" in <paramref name="raw"/>, for naming a reference the
        /// positional icon parse did not surface.</summary>
        private static string PngReference(string raw)
        {
            int at = raw.IndexOf(".png", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "";
            int start = at;
            while (start > 0 && raw[start - 1] != ',' && raw[start - 1] != ' '
                   && raw[start - 1] != '\t')
            {
                start--;
            }
            return raw.Substring(start, at - start + ".png".Length).Trim();
        }

        /// <summary>Mouse-region keys the pointer rows have no channel
        /// for, named per group when present (the flick-stick shape).
        /// teleport_start/teleport_stop are the shipped configurator's
        /// "Snap Cursor on Activation" / "Return Cursor on Deactivation"
        /// (the pointer already warps on touch and freezes on lift, but
        /// the mode-shift snap-back is a cursor-history behavior PadForge
        /// does not keep). Zero values mean "off", so only non-zero
        /// values are named. edge_binding_radius/_invert left this list
        /// in v26: they shape WHERE the group's own "edge" member fires,
        /// and that member consumes them now on every host (the v17
        /// stick ring, the v26 finger ring), so they belong to the edge
        /// member's read, not to a drop note.</summary>
        private static readonly string[] MouseRegionDroppedKeys =
        {
            "teleport_start", "teleport_stop",
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
        private void TranslateMouseRegion(Run run, SteamInputGroup group, SteamSlot slot,
            string layer, string path, Dictionary<string, string> settings)
        {
            run.CurrentLayer = layer; // #254 A-2
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
            // keep the wave-2A clamp-macro approximation. Stick hosts
            // ENGAGE now (v26): RegionEngageSource returns the v17
            // deflection-ring bool, so the clamp holds exactly while the
            // stick is deflected, retiring the stick arm's old
            // NoDeviceFreeTrigger skip.
            var host = PhysicalSlotResolver.RegionEngageSource(slot, run.SinglePadTrackpads);
            if (host == null)
            {
                // A host with no position AND no engage read: on such a
                // surface a memberless region with identity geometry is a
                // provable NO-OP (v26): scale 100 centered at (50, 50) IS
                // the whole screen, clamping to the whole screen changes
                // nothing the OS does not already enforce, and with no
                // steering surface and no bound members there is nothing
                // else the group could do even in Steam. Wild witness
                // 2837961678: empty-inputs mouse_region groups parked on
                // the face diamond with no geometry keys at all. Anything
                // non-identity or with members keeps the named skip.
                bool anyBoundMember = group != null
                    && group.Inputs.Values.Any(i => i.Activators.Count > 0);
                if (!anyBoundMember && scale >= 100 && posX == 50 && posY == 50)
                    return;
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
            // A half-hosted region engages on the half's touch spot,
            // self-armed at apply since v14 (the imported set references
            // the spot descriptor), so only the geometry note remains.
            FillMacroTrigger(macro, host);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Partial, TranslationReasons.MouseRegionApproximated,
                path, emitted: "Cursor region clamp macro",
                args: new[]
                {
                    scale.ToString(CultureInfo.InvariantCulture),
                    posX.ToString(CultureInfo.InvariantCulture),
                    posY.ToString(CultureInfo.InvariantCulture),
                });

            // The teleport / edge-binding keys shape engage and release
            // behavior the clamp macro has no channel for, same named
            // note the trackpad pointer branch carries.
            var clampDropped = MouseRegionDroppedKeys
                .Where(k => settings.TryGetValue(k, out var v) && (v ?? "").Trim() != "0")
                .ToList();
            if (clampDropped.Count > 0)
            {
                run.Report.Add(TranslationStatus.Partial,
                    TranslationReasons.MouseRegionTuningDropped, path,
                    args: string.Join(", ", clampDropped));
            }
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
                // v18: the trigger group's response cluster rides the pull
                // row (the engine's unipolar tail shapes it now, the
                // stick seam's twin). A single pull axis reads the
                // horizontal sensitivity scale.
                var curve = CurveRangeChannel.FromSettings(settings);
                if (crossed)
                {
                    string target = left ? "RightTrigger" : "LeftTrigger";
                    var src = new MappingSource { Descriptor = sourceDesc };
                    if (dzPct > 0) src.DeadZone = dzPct;
                    curve.StampAxis(src, isX: true);
                    AddRowSource(run, isKbm: false, layer, target,
                        src, isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path,
                        binding: $"output_trigger {output}");
                }
                else
                {
                    run.AddMatchedAnalog(layer, left ? "LeftTrigger" : "RightTrigger",
                        sourceDesc, path, dzPct,
                        curve.Exponent, curve.RangeOuter, curve.SensX, curve.Anti);
                }
            }

            TranslateMemberGroup(run, preset, group, slot, layer, path, settings);
        }

        /// <summary>joystick_move: stick passthrough. Both sides emit the
        /// explicit axis pair (authoritative sets spell out every output
        /// Steam produces): output_joystick redirects to the other stick
        /// here, the matched side via the Finalize matched-analog pass.
        /// Trackpad-as-stick rides the gesture StickX/StickY channel,
        /// self-armed at apply since v14.</summary>
        private void TranslateJoystickMove(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings)
        {
            int output = settings.TryGetValue("output_joystick", out var raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int o)
                    ? o : 0;
            int dzPct = GroupDeadZonePercent(settings);
            // v19 (T2): the emitted thumb pair reads each axis alone, so
            // the authored radii do not apply radially; name the residual
            // on the pair-emitting hosts.
            if (PhysicalSlotResolver.IsStick(slot) || PhysicalSlotResolver.IsTrackpad(slot))
                ReportRadialDeadZoneResidual(run, settings, path);

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
                // v11: the group's response-curve cluster rides the emitted
                // pair as per-source params (both member rows, X and Y).
                var curve = CurveRangeChannel.FromSettings(settings);
                if (crossed)
                {
                    string dst = left ? "Right" : "Left";
                    var sx = Src($"Gamepad {src}X");
                    curve.StampAxis(sx, isX: true);
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisX",
                        sx, isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path,
                        binding: $"output_joystick {output}");
                    var sy = Src($"Gamepad {src}Y");
                    curve.StampAxis(sy, isX: false);
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisY",
                        sy, isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path,
                        binding: $"output_joystick {output}");
                }
                else
                {
                    string dst = left ? "Left" : "Right";
                    run.AddMatchedAnalog(layer, $"{dst}ThumbAxisX", $"Gamepad {src}X", path, dzPct,
                        curve.Exponent, curve.RangeOuter, curve.SensX, curve.Anti);
                    run.AddMatchedAnalog(layer, $"{dst}ThumbAxisY", $"Gamepad {src}Y", path, dzPct,
                        curve.Exponent, curve.RangeOuter, curve.SensY, curve.Anti);
                }
            }
            else if (PhysicalSlotResolver.IsTrackpad(slot))
            {
                int p = PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads);
                var half = PhysicalSlotResolver.HalfFor(slot, run.SinglePadTrackpads);
                // 2 = right stick, anything else lands on the left.
                string dst = output == 2 ? "Right" : "Left";
                // v18: the trackpad-as-stick lanes (absolute finger reads
                // and the gesture Stick channel) ride the same per-source
                // curve/range shaping as the stick hosts.
                var padCurve = CurveRangeChannel.FromSettings(settings);
                MappingSource PadSrc(string descriptor, bool isX)
                {
                    var s = Src(descriptor);
                    padCurve.StampAxis(s, isX);
                    return s;
                }
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
                        PadSrc($"Touchpad {p} Finger 0 X{sfx}", isX: true), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisY",
                        PadSrc($"Touchpad {p} Finger 0 Y{sfx}", isX: false), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                }
                else
                {
                    // The gesture Stick channel self-arms at apply since
                    // v14 (the imported set references the descriptors),
                    // so the rows are Clean with no user action needed.
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisX",
                        PadSrc($"Touchpad {p} StickX", isX: true), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                    AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisY",
                        PadSrc($"Touchpad {p} StickY", isX: false), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
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
            string sensitivityKey = "sensitivity", bool curveChannel = false)
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
            // v19 (T2): the pair-host inner/outer radii do not apply
            // radially at the analog reads; name the residual. Stick
            // hosts (family 0) consume the radii through the v25
            // per-source geometry stamp below and drop the note.
            if (family != 0)
                ReportRadialDeadZoneResidual(run, settings, path);
            // v11 (widened to every analog host in v18): the group's
            // response-curve cluster rides the emitted pair as per-source
            // params.
            var curve = curveChannel
                ? CurveRangeChannel.FromSettings(settings)
                : default;
            // v18: the built mouse-feel family (rotation / smoothing /
            // accel / threshold / trackball) rides the same sources.
            var feel = MouseFeelChannel.FromSettings(settings);

            // Steam's per-group axis inversion (finding 1g-1). Every
            // mouse-axis family here reads through the bipolar evaluator's
            // `Invert ? -raw` transform (SourceCoercion.EvaluateForBipolarAxisTarget):
            // the stick axes (family 0), the touchpad finger delta (family 1,
            // TryReadTouchpadAxis returns the raw delta and the wrapper flips
            // it), and the gyro rate (family 2) all honor MappingSource.Invert
            // and don't consume it internally, so the flipped rows stay Clean.
            bool invertX = SettingIsOn(settings, "invert_x");
            bool invertY = SettingIsOn(settings, "invert_y");

            // deadzone_shape on a stick-hosted mouse pair (v25): the
            // per-source geometry stamp. Steam 1 = Circle = the radial
            // pair test (engine shape 2); 0 = Cross and 2 = Square are
            // per-axis checks (engine shape 1, the Axial convention the
            // v18 slot stamp uses for the same fold), and an absent key
            // reads as the selector's serialized default, Cross. The
            // stamp rides whenever there is geometry to apply (an inner
            // radius, or the outer whose application just moves inside
            // the same transform), so the authored radii genuinely land
            // on the read and the v19 radial residual retires for stick
            // mouse hosts. Stick family only: the finger / gyro lanes
            // have no companion-axis pair read and keep the residual.
            int stickShape = 0;
            if (family == 0)
            {
                int mouseShapeVal = ParseIntSetting(settings, "deadzone_shape", 0);
                bool anyGeometry = dzPct > 0
                    || settings.ContainsKey("deadzone_shape")
                    || (TryParseDeadZoneRaw(settings, "deadzone_outer_radius", out int mo)
                        && mo < 32767);
                if (anyGeometry) stickShape = mouseShapeVal == 1 ? 2 : 1;
            }

            MappingSource Make(string descriptor, bool invert, bool isX, double coeff = 1.0)
            {
                var src = new MappingSource { Descriptor = descriptor };
                double scale = ratio * Math.Abs(coeff);
                if (family == 0 || family == 1) src.Sensitivity = scale;
                else if (family == 2) src.GyroSensitivity = scale;
                if (dzPct > 0) src.DeadZone = dzPct;
                if (stickShape != 0)
                {
                    src.ParamStickDeadZoneShape = stickShape;
                    // The geometry's own inner field: DeadZone's 50
                    // default is the button-threshold sentinel, so the
                    // analog inner radius must never read it.
                    if (dzPct > 0) src.ParamStickDeadZoneInner = dzPct / 100.0;
                }
                if (invert ^ (coeff < 0)) src.Invert = true;
                if (curveChannel) curve.StampAxis(src, isX);
                feel.StampFeel(src, isX);
                if (feel.HasRotation) WithholdNonlinearForRotation(src);
                return src;
            }

            if (feel.HasRotation)
            {
                // rotation (v18): rotate the input vector by the authored
                // angle, pure row math: each output row Sums the two pad
                // axes with the trigonometric coefficients folded into the
                // per-source sensitivity (sign via Invert). Near-zero legs
                // are dropped so 90-degree multiples stay two clean rows.
                ReportRotationNonlinearWithheld(run, path, curve, feel, curveChannel);
                double t = feel.RotationDeg * Math.PI / 180.0;
                double cos = Math.Cos(t), sin = Math.Sin(t);
                void AddLeg(string target, string desc, bool invert, bool isX, double coeff)
                {
                    if (Math.Abs(coeff) < 0.001) return;
                    AddRowSource(run, isKbm: true, layer, target,
                        Make(desc, invert, isX, coeff), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                }
                AddLeg("KbmMouseX", x, invertX, isX: true, cos);
                AddLeg("KbmMouseX", y, invertY, isX: true, -sin);
                AddLeg("KbmMouseY", x, invertX, isX: false, sin);
                AddLeg("KbmMouseY", y, invertY, isX: false, cos);
            }
            else
            {
                AddRowSource(run, isKbm: true, layer, "KbmMouseX", Make(x, invertX, isX: true), isAxis: true,
                    TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                AddRowSource(run, isKbm: true, layer, "KbmMouseY", Make(y, invertY, isX: false), isAxis: true,
                    TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
            }

            // invert_z addresses a third (roll) axis the X/Y mouse-delta pair
            // does not emit, so name it rather than drop it under Clean rows.
            if (SettingIsOn(settings, "invert_z"))
                ReportUnappliedInversion(run, path, "invert_z");
        }

        /// <summary>mouse_joystick groups: the slot's analog surface drives a
        /// VIRTUAL STICK, not the cursor (sc-controller's proven importer
        /// lowers the mode to ABS_RX/ABS_RY). Same source pair, sensitivity
        /// ratio, deadzone, and inversion handling as
        /// <see cref="EmitMouseAxes"/>, targeting the thumb axes on the Xbox
        /// slot. output_joystick 1 redirects to the left stick (the value
        /// joystick_move reads as "left"); anything else keeps the mode's
        /// right-stick default.</summary>
        private void EmitMouseJoystickAxes(Run run, SteamSlot slot, string layer, string path,
            Dictionary<string, string> settings, double baseline, bool curveChannel = false)
        {
            var pair = PhysicalSlotResolver.MouseAxisPair(slot, run.SinglePadTrackpads);
            if (pair == null) return;

            double ratio = 1.0;
            if (settings.TryGetValue("sensitivity", out var sensRaw)
                && double.TryParse(sensRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double sens)
                && sens > 0)
            {
                ratio = Math.Clamp(sens / baseline, 0.05, 20.0);
            }

            var (x, y, family) = pair.Value;
            int dzPct = GroupDeadZonePercent(settings);
            // v19 (T2): same pair-host radial residual note as EmitMouseAxes.
            ReportRadialDeadZoneResidual(run, settings, path);
            bool invertX = SettingIsOn(settings, "invert_x");
            bool invertY = SettingIsOn(settings, "invert_y");
            string dst = ParseIntSetting(settings, "output_joystick", 0) == 1 ? "Left" : "Right";
            // v11 (every analog host since v18): same curve-channel stamps
            // as EmitMouseAxes.
            var curve = curveChannel
                ? CurveRangeChannel.FromSettings(settings)
                : default;
            var feel = MouseFeelChannel.FromSettings(settings);

            MappingSource Make(string descriptor, bool invert, bool isX, double coeff = 1.0)
            {
                var src = new MappingSource { Descriptor = descriptor };
                double scale = ratio * Math.Abs(coeff);
                if (family == 0 || family == 1) src.Sensitivity = scale;
                else if (family == 2) src.GyroSensitivity = scale;
                if (dzPct > 0) src.DeadZone = dzPct;
                if (invert ^ (coeff < 0)) src.Invert = true;
                if (curveChannel) curve.StampAxis(src, isX);
                feel.StampFeel(src, isX);
                if (feel.HasRotation) WithholdNonlinearForRotation(src);
                return src;
            }

            if (feel.HasRotation)
            {
                // rotation (v18): same two-source Sum lowering as
                // EmitMouseAxes, on the thumb-axis targets.
                ReportRotationNonlinearWithheld(run, path, curve, feel, curveChannel);
                double t = feel.RotationDeg * Math.PI / 180.0;
                double cos = Math.Cos(t), sin = Math.Sin(t);
                void AddLeg(string target, string desc, bool invert, bool isX, double coeff)
                {
                    if (Math.Abs(coeff) < 0.001) return;
                    AddRowSource(run, isKbm: false, layer, target,
                        Make(desc, invert, isX, coeff), isAxis: true,
                        TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                }
                // Gyro pitch→X cross leg (v19, finding 1i): the engine's
                // per-source axis-frame seam flips gyro yaw / roll on the
                // thumb X targets but never pitch
                // (SourceEvaluator.ShouldFlipForAxisFrame), so the authored
                // -sin composed with that yaw flip realized
                // [[-cos, -sin], [sin, cos]] on the stick pair
                // (det = -cos 2θ, a shear). The rotation acts in the
                // SENSOR plane before the yaw→stick frame map
                // (out = B·R(θ)·g with B = diag(-1, 1)), so factoring the
                // per-source frame into the emitted coefficient means the
                // pitch→X leg flips to +sin for the gyro family, restoring
                // an orthogonal realized matrix [[-cos, sin], [sin, cos]]
                // (det = -1). Stick and touchpad pairs (families 0/1)
                // never hit that engine flip and keep -sin.
                AddLeg($"{dst}ThumbAxisX", x, invertX, isX: true, cos);
                AddLeg($"{dst}ThumbAxisX", y, invertY, isX: true, family == 2 ? sin : -sin);
                AddLeg($"{dst}ThumbAxisY", x, invertX, isX: false, sin);
                AddLeg($"{dst}ThumbAxisY", y, invertY, isX: false, cos);
            }
            else
            {
                AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisX", Make(x, invertX, isX: true), isAxis: true,
                    TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
                AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisY", Make(y, invertY, isX: false), isAxis: true,
                    TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
            }

            // invert_z addresses a third axis the X/Y pair does not emit.
            if (SettingIsOn(settings, "invert_z"))
                ReportUnappliedInversion(run, path, "invert_z");
        }

        /// <summary>gyro_to_joystick_deflection (v26): sustained TILT maps
        /// to stick deflection through the engine's gravity-lean pair
        /// ("Gyro Lean X/Y", 90 degrees = full scale, physical-stick
        /// signs). The default Sensitivity 2.0 anchors full deflection at
        /// 45 degrees of tilt, the JSM motion-stick envelope the engine's
        /// MotionLean channel already ships (ParamMotionInnerDz /
        /// OuterDz defaults 15 / 135: output saturates 45 degrees from
        /// level); an authored "sensitivity" scales it through the same
        /// baseline ratio the rate emitters use. output_joystick /
        /// invert_x / invert_y follow EmitMouseJoystickAxes exactly.</summary>
        private void EmitGyroLeanJoystickAxes(Run run, string layer, string path,
            Dictionary<string, string> settings)
        {
            double ratio = 1.0;
            if (settings.TryGetValue("sensitivity", out var sensRaw)
                && double.TryParse(sensRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double sens)
                && sens > 0)
            {
                ratio = Math.Clamp(sens / GenericBaseline, 0.05, 20.0);
            }
            string dst = ParseIntSetting(settings, "output_joystick", 0) == 1 ? "Left" : "Right";
            bool invertX = SettingIsOn(settings, "invert_x");
            bool invertY = SettingIsOn(settings, "invert_y");

            MappingSource MakeLean(string descriptor, bool invert)
            {
                var src = new MappingSource
                {
                    Descriptor = descriptor,
                    Sensitivity = 2.0 * ratio,
                };
                if (invert) src.Invert = true;
                return src;
            }

            AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisX",
                MakeLean(PadForge.Engine.Common.Mapping.SourceCoercion.GyroLeanXDescriptor, invertX),
                isAxis: true, TranslationStatus.Clean, TranslationReasons.RowEmitted, path);
            AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisY",
                MakeLean(PadForge.Engine.Common.Mapping.SourceCoercion.GyroLeanYDescriptor, invertY),
                isAxis: true, TranslationStatus.Clean, TranslationReasons.RowEmitted, path);

            if (SettingIsOn(settings, "invert_z"))
                ReportUnappliedInversion(run, path, "invert_z");
        }

        /// <summary>flickstick groups (#225, widened in v26): the host
        /// becomes a flick-stick source on the KbM mouse X row. Stick
        /// hosts read "Flick Stick Right/Left"; trackpad hosts read the
        /// touch-surface family ("Flick Stick Touchpad {p}[ Left|Right]",
        /// the finger's centered vector as the stick pair, half-windowed
        /// on single-pad hosts), so the old FlickStickSurfaceNotSupported
        /// skip fires only for a host with no analog pair at all
        /// (hand-edited grammar). The group's "sensitivity" is Steam's
        /// shared Dots Per 360 (client l10n: "Flick Stick ° to Mouse
        /// Pixels (Dots Per 360°)"; corpus values 2603..2800) and lands
        /// 1:1 on ParamFlickCountsPer360.
        ///
        /// <para>The old FlickStickTuningDropped list is fully consumed
        /// as of v26 (the key retired): rotation is degrees (wild corpus
        /// 1 / -1, the v18 mouse-feel rotation scale on the same key)
        /// onto ParamFlickRotationOffsetDeg; mouse_smoothing is the
        /// 0..100-ish strength (the v18 grounding for the same key on
        /// the mouse modes) mapped onto ParamFlickSmooth's rad-per-tick
        /// threshold with JSM's own auto band as the anchor (full
        /// strength = 0.04, the auto tier's upper threshold; authored 0
        /// = smoothing off, the engine's explicit-zero contract);
        /// transition_time consumes at any authored value including 0
        /// (the clamp floor 0.01 s is the near-instant flick an authored
        /// 0 asks for); and edge_binding_radius always belonged to the
        /// edge MEMBER, which consumes it through the v17 stick ring /
        /// v26 finger ring.</para></summary>
        private void EmitFlickStick(Run run, SteamSlot slot, string layer, string path,
            Dictionary<string, string> settings)
        {
            string descriptor;
            if (PhysicalSlotResolver.IsStick(slot))
            {
                descriptor = slot == SteamSlot.RightJoystick
                    ? "Flick Stick Right"
                    : "Flick Stick Left";
            }
            else if (PhysicalSlotResolver.IsTrackpad(slot))
            {
                int p = PhysicalSlotResolver.TrackpadIndex(slot, run.SinglePadTrackpads);
                string sfx = PhysicalSlotResolver.HalfSuffix(
                    PhysicalSlotResolver.HalfFor(slot, run.SinglePadTrackpads));
                descriptor = PadForge.Engine.Common.Mapping.SourceCoercion.FlickStickTouchpadPrefix
                    + p + sfx;
            }
            else
            {
                run.Report.Add(TranslationStatus.Skipped,
                    TranslationReasons.FlickStickSurfaceNotSupported, path,
                    args: slot.ToString());
                return;
            }

            var src = new MappingSource { Descriptor = descriptor };
            if (settings.TryGetValue("sensitivity", out var dotsRaw)
                && double.TryParse(dotsRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double dots)
                && dots > 0)
            {
                src.ParamFlickCountsPer360 = dots;
            }
            // transition_time: Steam's flick easing time, stored in ms
            // (the wild corpus scale), IS ParamFlickTime (seconds for a
            // full 180-degree flick). An authored 0 consumes as the clamp
            // floor: the near-instant flick it asks for (the engine reads
            // ParamFlickTime 0 as unset).
            if (settings.TryGetValue("transition_time", out var ttRaw)
                && int.TryParse(ttRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int transitionMs)
                && transitionMs >= 0)
            {
                src.ParamFlickTime = Math.Clamp(transitionMs / 1000.0, 0.01, 1.0);
            }
            // rotation: constant offset in degrees (v26).
            int rotOffset = ParseIntSetting(settings, "rotation", 0);
            if (rotOffset != 0 && rotOffset > -360 && rotOffset < 360)
                src.ParamFlickRotationOffsetDeg = rotOffset;
            // mouse_smoothing: percent strength onto the rad-per-tick
            // smoothing threshold (v26; absent keeps JSM's auto tier).
            if (settings.TryGetValue("mouse_smoothing", out var smRaw)
                && int.TryParse(smRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int smooth)
                && smooth >= 0)
            {
                src.ParamFlickSmooth = Math.Clamp(smooth, 0, 100) / 100.0 * 0.04;
            }

            AddRowSource(run, isKbm: true, layer, "KbmMouseX", src, isAxis: true,
                TranslationStatus.Clean, TranslationReasons.RowEmitted, path);

            // Flick stick maps the host ANGLE to camera rotation through
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
            run.CurrentLayer = layer; // #254 A-2: macro emitters stamp this
            foreach (var activator in input.Activators)
            {
                string type = (activator.Type ?? "").Trim();
                string actPath = $"{path}/{type}";

                bool soft = false;
                bool onRelease = false;
                var hostSource = source;
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
                        TranslateDoublePress(run, preset, activator, input, source, layer, actPath);
                        continue;
                    case "chord":
                        // Steam's Button Chord activator (v24): "activates
                        // when the both the button owning the activator and
                        // the chorded button are pressed" (shipped string
                        // ControllerBinding_ActivatorDropDown_Chord_Description).
                        // The partner rides the host source's AND-gate
                        // companion, so the whole normal walk below (rows,
                        // macro triggers, Kind=Chord layer activators)
                        // inherits the two-leg requirement. A chord with NO
                        // chord_button at all is the config's own unset
                        // picker (v26): not even Steam can fire it, so it
                        // gets the precise config-error class. Genuinely
                        // ungroundable partners (out-of-enum chord_button,
                        // an unfoldable second gate) keep the generic net.
                        if (!HasChordPartner(activator))
                        {
                            foreach (var b in activator.Bindings)
                                ReportSkipUnlessSilent(run, TranslationReasons.ChordWithoutPartner,
                                    actPath, b);
                            continue;
                        }
                        hostSource = ChordHost(run, activator, source);
                        if (hostSource == null)
                        {
                            foreach (var b in activator.Bindings)
                                ReportSkipUnlessSilent(run, TranslationReasons.UnknownActivatorType, actPath, b,
                                    slotArg: type);
                            continue;
                        }
                        break;
                    case "analog":
                        // Steam's Analog activator (v26): "Analog Activator
                        // produces an analog output" (shipped string
                        // ControllerBinding_ActivatorDropDown_Analog
                        // _Description): the input's live magnitude drives
                        // the binding instead of an edge. The normal walk
                        // already delivers exactly that: rows read the host
                        // source's analog value where the target is analog
                        // and its held state where it is a button, which on
                        // the wild corpus's digital hosts (bumpers driving
                        // xinput_button) is the Regular Press contract.
                        break;
                    default:
                        foreach (var b in activator.Bindings)
                            ReportSkipUnlessSilent(run, TranslationReasons.UnknownActivatorType, actPath, b,
                                slotArg: type);
                        continue;
                }


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

                // Activator delays (v18): known before the walk so
                // row-shaped bindings can reroute onto the delayed hold
                // macros, and layer switches emitted in the window can
                // take the engage debounce.
                int delayStartMs = ParseDelaySetting(activator, "delay_start");
                int delayEndMs = ParseDelaySetting(activator, "delay_end");

                int macrosBefore = run.Profile.Macros.Count;
                int activatorsBefore = run.Activators.Count;
                foreach (var binding in activator.Bindings)
                {
                    TranslateBinding(run, preset, binding, hostSource, clickGate, layer, actPath,
                        soft, onRelease, holdRepeats, intervalMs, toggle, input.Name,
                        delayStartMs, delayEndMs);
                }
                EmitHapticPulse(run, activator, hostSource, input.Name, actPath,
                    onRelease ? "OnRelease" : "OnPress", holdMs: 0);
                ConsumeActivatorDelays(run, activator, actPath, macrosBefore, activatorsBefore);
            }
        }

        /// <summary>double_press activators (v10 G13, built on every host
        /// in v17). Trackpad-hosted inputs approximate through the gesture
        /// engine's DoubleTap fire ("Touchpad {p} DoubleTap",
        /// GestureRecognizer's tap counter): the whole pad's double tap
        /// stands in for Steam's double press of the member, self-armed at
        /// apply since v14. Every other host rides the engine's DoublePress
        /// macro trigger (press, release, press within the window), with
        /// Valve's held-state semantics on the held-capable targets: "If
        /// held on the second press, it will remain pressed" (shipped
        /// Double Press string), so keys / mouse buttons / VC targets use
        /// the Hold* shapes stopping on release, and the activator's
        /// toggle / hold_repeats settings compose the latch and turbo
        /// variants at the same edge, the Long_Press pattern.
        ///
        /// <para>The window is the activator's double_tap_time (the
        /// serializer's own key, sitting beside repeat_rate and
        /// long_press_time in steamclient.dll's token table); the corpus
        /// authors none, and the default is 442 ms, the value Valve's own
        /// controller_base templates author (basicui.vdf /
        /// basicui_neptune.vdf). Layer verbs, preset jumps, and mode
        /// shifts lower through their canonical activator walks with the
        /// v25 double-press gate (ShiftActivator.DoublePressMs) stamped
        /// on the request, so the activator engages only on the second
        /// press of a press-release-press pair inside the same window
        /// (wild census 2026-07-18: 40 Double_Press CHANGE_PRESET sites
        /// across 20 configs).</para></summary>
        private void TranslateDoublePress(Run run, SteamInputPreset preset,
            SteamInputActivator activator, SteamInputInput input, ResolvedSource source,
            string layer, string actPath)
        {
            int pad = TouchpadIndexOf(source);
            if (pad >= 0)
            {
                var tap = new ResolvedSource
                {
                    Descriptor = $"Touchpad {pad} DoubleTap",
                    TrackpadFeature = PhysicalSlotResolver.FeatureTaps,
                };
                int tapMacrosBefore = run.Profile.Macros.Count;
                foreach (var binding in activator.Bindings)
                {
                    // DoubleTap is a one-frame pulse, so press-shaped
                    // translation is the whole vocabulary here: release /
                    // turbo / toggle variants have no held state to ride.
                    TranslateBinding(run, preset, binding, tap, clickGate: null, layer, actPath,
                        soft: false, onRelease: false, holdRepeats: false, intervalMs: 100,
                        toggle: false, input.Name);
                }
                EmitHapticPulse(run, activator, tap, input.Name, actPath, "OnPress", holdMs: 0);
                ConsumeActivatorDelays(run, activator, actPath, tapMacrosBefore);
                return;
            }

            int windowMs = 442;
            if (activator.Settings.TryGetValue("double_tap_time", out var dtt)
                && int.TryParse(dtt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dttMs))
            {
                windowMs = Math.Clamp(dttMs, 50, 5000);
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
            int macrosBefore = run.Profile.Macros.Count;
            foreach (var binding in activator.Bindings)
            {
                string bt = (binding.Type ?? "").Trim().ToLowerInvariant();
                switch (bt)
                {
                    case "key_press":
                    {
                        string keyName = FirstToken(binding.Param);
                        if (!SteamInputVkTable.TryResolve(keyName, out byte vk, out _))
                        {
                            run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownKey,
                                actPath, binding.Raw, args: keyName);
                            break;
                        }
                        if (toggle)
                        {
                            // toggle + hold_repeats composes the pulsed
                            // latch (v18).
                            anyCarry |= EmitKeyToggleMacro(run, binding, source, actPath, vk, keyName,
                                onRelease: false, input.Name, triggerMode: "DoublePress",
                                pulse: holdRepeats, pulseIntervalMs: intervalMs);
                        }
                        else if (holdRepeats)
                        {
                            anyCarry |= EmitKeyMacro(run, binding, source, actPath,
                                (TranslatedMacroAction.RepeatKeyWhileHeld, "DoublePress"),
                                vk, intervalMs, keyName, input.Name);
                        }
                        else
                        {
                            anyCarry |= EmitKeyHoldMacro(run, binding, source, actPath, vk, keyName,
                                "DoublePress", holdMs: 0, input.Name);
                        }
                        break;
                    }

                    case "mouse_button":
                    {
                        if (!SteamInputVkTable.TryResolveMouseButtonIndex(binding.Param, out int btn))
                        {
                            run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownMouseButton,
                                actPath, binding.Raw, args: binding.Param);
                            break;
                        }
                        var macro = new TranslatedMacro
                        {
                            Name = $"Hold mouse {FirstToken(binding.Param).ToUpperInvariant()} ({input.Name})",
                            Action = TranslatedMacroAction.HoldMouseButton,
                            TriggerMode = "DoublePress",
                            // Never consumed: the OnRelease twin reads the
                            // same trigger.
                            ConsumeTrigger = false,
                            MouseButtonIndex = btn,
                        };
                        FillMacroTrigger(macro, source);
                        run.AddMacro(macro);
                        run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                            actPath, binding.Raw,
                            emitted: $"Double-press hold macro: mouse button {btn}");
                        anyCarry = true;
                        break;
                    }

                    case "mouse_wheel":
                    {
                        var wheel = ParseWheelParam(binding.Param);
                        if (wheel == null)
                        {
                            run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                                actPath, binding.Raw, args: $"mouse_wheel {binding.Param}");
                            break;
                        }
                        EmitWheelTapMacro(run, binding, source, actPath, wheel.Value,
                            input.Name, triggerMode: "DoublePress");
                        anyCarry = true;
                        break;
                    }

                    case "xinput_button":
                    {
                        if (!XInputTargetTable.TryResolve(binding.Param, out var xt))
                        {
                            run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownXInputButton,
                                actPath, binding.Raw, args: binding.Param);
                            break;
                        }
                        if (xt.IsTriggerAxis || xt.IsStickAxis)
                        {
                            // The axis asserts on the double press and stays
                            // asserted until release (v15 AxisHold shape),
                            // with the v18 latch / turbo variants.
                            if (toggle)
                            {
                                anyCarry |= EmitVcAxisToggleMacro(run, binding, source, actPath,
                                    xt, input.Name, triggerMode: "DoublePress",
                                    pulse: holdRepeats, pulseIntervalMs: intervalMs);
                                break;
                            }
                            if (holdRepeats)
                            {
                                anyCarry |= EmitVcAxisTurboMacro(run, binding, source, actPath,
                                    xt, intervalMs, holdMs: 0, input.Name,
                                    triggerMode: "DoublePress");
                                break;
                            }
                            EmitVcAxisHoldMacro(run, binding, source, actPath, xt, holdMs: 0,
                                input.Name, triggerMode: "DoublePress");
                            anyCarry = true;
                            break;
                        }
                        if (toggle)
                        {
                            // toggle + hold_repeats composes the pulsed
                            // latch (v18).
                            anyCarry |= EmitVcToggleMacro(run, binding, source, actPath, xt,
                                rowKept: false, input.Name, triggerMode: "DoublePress",
                                pulse: holdRepeats, pulseIntervalMs: intervalMs);
                        }
                        else if (holdRepeats)
                        {
                            anyCarry |= EmitVcTurboMacro(run, binding, source, actPath, xt,
                                intervalMs, holdMs: 0, input.Name, triggerMode: "DoublePress");
                        }
                        else
                        {
                            anyCarry |= EmitVcHoldMacro(run, binding, source, actPath, xt, holdMs: 0,
                                input.Name, triggerMode: "DoublePress");
                        }
                        break;
                    }

                    case "controller_action":
                    {
                        // Layer verbs and preset jumps lower through the
                        // canonical walk with the v25 double-press gate
                        // stamped on their activators
                        // (ShiftActivator.DoublePressMs): the request
                        // engages only on the second press of a
                        // press-release-press pair inside the window, so
                        // CHANGE_PRESET / add_layer / hold_layer /
                        // REMOVE_LAYER carry Steam's Double_Press hosting
                        // exactly (wild census 2026-07-18: 40
                        // Double_Press CHANGE_PRESET sites). Macro-shaped
                        // one-shot verbs ride the DoublePress macro
                        // trigger (v17); Steam-client-only families keep
                        // their named skips inside.
                        string verb = FirstToken(binding.Param).ToUpperInvariant();
                        TranslateControllerAction(run, preset, binding, source, layer, actPath,
                            onRelease: false, input.Name, toggle: toggle,
                            triggerModeOverride: "DoublePress", doublePressMs: windowMs);
                        anyCarry |= LongPressEmittingVerbs.Contains(verb);
                        break;
                    }

                    case "mode_shift":
                        // Double-press mode shifts ride the same v25 gate
                        // on the shift activator.
                        TranslateModeShift(run, preset, binding, source, actPath,
                            toggle: toggle, doublePressMs: windowMs);
                        anyCarry = true;
                        break;

                    case "game_action_analog": // the analog sibling, same Steam-session surface (v24)
                    case "game_action":
                        run.GameActionsByPreset[preset.Id] =
                            run.GameActionsByPreset.GetValueOrDefault(preset.Id) + 1;
                        break;

                    default:
                        if (bt.Length == 0) break; // placeholder, silent
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                            actPath, binding.Raw, args: binding.Type ?? "");
                        break;
                }
            }

            if (anyCarry)
            {
                EmitHapticPulse(run, activator, source, input.Name, actPath,
                    "DoublePress", holdMs: 0);
            }

            // The window rides every macro the walk (and the haptic pulse)
            // emitted on the DoublePress trigger; release twins keep
            // OnRelease and need no window. The range pattern mirrors
            // ConsumeActivatorDelays.
            var macros = run.Profile.Macros;
            for (int i = macrosBefore; i < macros.Count; i++)
            {
                if (macros[i].TriggerMode == "DoublePress")
                    macros[i].TriggerDoublePressMs = windowMs;
            }

            if (anyCarry)
                ConsumeActivatorDelays(run, activator, actPath, macrosBefore);
        }

        /// <summary>Physical touchpad index of a resolved source
        /// ("Touchpad {p} ..."), or -1 when the source is not
        /// touchpad-hosted.</summary>
        private static int TouchpadIndexOf(ResolvedSource source)
        {
            string d = source?.Descriptor ?? "";
            if (!d.StartsWith("Touchpad ", StringComparison.Ordinal)) return -1;
            var parts = d.Split(' ');
            return parts.Length >= 2
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)
                && p >= 0 ? p : -1;
        }

        /// <summary>An activator-level <c>haptic_intensity</c> (v10 G1)
        /// becomes a reactive rumble pulse fired the same way the
        /// activator fires. Steam levels 1..3 (Low/Medium/High) scale the
        /// pulse to 33/66/100 percent. The trigger never consumes: haptics
        /// are feedback beside the binding, not a replacement for it.
        /// Since v22 the GROUP-level intensity (haptic_intensity_override
        /// winning over the plain key) is the fallback for member
        /// activators that author none of their own, so a group's haptics
        /// tick on every member activation; an activator's own explicit 0
        /// stays off.</summary>
        private void EmitHapticPulse(Run run, SteamInputActivator activator,
            ResolvedSource source, string inputName, string path, string triggerMode, int holdMs)
        {
            int level = run.GroupHapticLevel;
            if (activator.Settings.TryGetValue("haptic_intensity", out var raw)
                && int.TryParse((raw ?? "").Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int own))
            {
                level = own;
            }
            if (level <= 0) return;

            var macro = new TranslatedMacro
            {
                Name = $"Haptic pulse ({inputName})",
                Action = TranslatedMacroAction.RumblePulse,
                TriggerMode = holdMs > 0 ? "HoldForMs" : triggerMode,
                TriggerHoldMs = holdMs,
                ConsumeTrigger = false,
                RumbleStrengthPercent = level >= 3 ? 100 : level == 2 ? 66 : 33,
            };
            // Always the device-free descriptor trigger: a combined-output
            // trigger would demand an Xbox slot, and feedback on a pure
            // keyboard config must not sprout one (owner report 2026-07-13
            // is exactly that shape).
            FillMacroTrigger(macro, WithoutOutputTrigger(source));
            run.AddMacro(macro);
            // Silent clean lowering. Steam Input treats rumble and haptics
            // interchangeably, so the rumble pulse IS the haptic tick and
            // carries no report note (owner ruling 2026-07-17). A
            // gesture-hosted trigger self-arms at apply since v14.
        }

        /// <summary>Group-level haptic intensity (v22): the override twin
        /// wins when authored (an explicit override 0 silences a plain
        /// nonzero), else the plain group key, else 0. Steam levels 1..3,
        /// clamped.</summary>
        private static int ParseGroupHapticLevel(Dictionary<string, string> settings)
        {
            if (settings.TryGetValue("haptic_intensity_override", out var o)
                && int.TryParse((o ?? "").Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int ov))
            {
                return Math.Clamp(ov, 0, 3);
            }
            if (settings.TryGetValue("haptic_intensity", out var p)
                && int.TryParse((p ?? "").Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int pv))
            {
                return Math.Clamp(pv, 0, 3);
            }
            return 0;
        }

        /// <summary>Activator delay_start / delay_end (v10 G5, widened to
        /// every carrier in v18, closed whole in v22), grounded on Valve's
        /// shipped strings ("wait for this period of time after the button
        /// has been pressed before activating" / "... after the button has
        /// been released before deactivating").
        ///
        /// <para>Carriers. One-shot macros (taps, latches, the Hold*
        /// pairs) take Delay steps: press legs delay_start, release legs
        /// delay_end, pairs both. Continuous autofire takes a Delay step
        /// for delay_start (the continuous action never completes, so the
        /// step runs once) and the release linger for delay_end (the
        /// pulse train keeps running past the release, a re-press cancels
        /// the pending stop). The wheel turbo composes delay_start into
        /// its HoldForMs threshold (a Delay step would re-run inside
        /// every detent iteration, v19 T1) and takes the same linger. The
        /// VC hold shapes restart their sequence per frame, so their
        /// delay_start composes into the HoldForMs threshold and their
        /// delay_end becomes an OnRelease assert-extension twin. The
        /// region clamp pair takes the steps on its engage / release
        /// legs. Layer switches take delay_start as the engage debounce
        /// (ShiftActivator.DelayMs) and delay_end as the Hold-mode
        /// release linger (ShiftActivator.ReleaseDelayMs); non-Hold layer
        /// carriers (Toggle latches, preset Cycles, Custom jumps)
        /// deactivate on a press, never on the release, so Steam's own
        /// release+delay_end edge changes nothing for them and the value
        /// consumes silently. Press-leg one-shots resolve delay_end
        /// per shape: assert-shaped taps deactivate late (the assert
        /// grows to delay_end), and edge-fired commands (cursor warp,
        /// set_led, recenter, nudge, wheel detent, cycle step, rumble
        /// pulse, latch flips) emit nothing on Steam's own deactivation
        /// edge either, so the shifted edge is unobservable. Nothing is
        /// left to name; the ActivatorDelayDropped vocabulary retired
        /// with this closure.</para></summary>
        private static void ConsumeActivatorDelays(Run run, SteamInputActivator activator,
            string path, int macrosBefore, int activatorsBefore = -1)
        {
            int delayStart = ParseDelaySetting(activator, "delay_start");
            int delayEnd = ParseDelaySetting(activator, "delay_end");
            if (delayStart <= 0 && delayEnd <= 0) return;

            var macros = run.Profile.Macros;
            int macrosEnd = macros.Count;
            for (int i = macrosBefore; i < macrosEnd; i++)
            {
                var m = macros[i];
                switch (m.Action)
                {
                    case TranslatedMacroAction.RepeatKeyWhileHeld:
                    case TranslatedMacroAction.RepeatVcButtonWhileHeld:
                    case TranslatedMacroAction.RepeatVcAxisWhileHeld:
                        // A Delay step before a continuous action runs once
                        // (the action never completes, so the sequence
                        // never restarts past it).
                        if (delayStart > 0)
                        {
                            m.DelayStartMs = delayStart;
                        }
                        // v22: delay_end rides the release linger (the
                        // materializer maps DelayEndMs on the autofire
                        // shapes to MacroData.ReleaseLingerMs).
                        if (delayEnd > 0)
                        {
                            m.DelayEndMs = delayEnd;
                        }
                        continue;

                    case TranslatedMacroAction.RepeatWheelWhileHeld:
                        // v22: a Delay step would re-run inside every
                        // detent iteration and stretch the cadence (v19
                        // T1), so delay_start composes into the HoldForMs
                        // threshold (the VC-hold shape) and delay_end
                        // rides the release linger like the other
                        // autofire shapes.
                        if (delayStart > 0)
                        {
                            m.TriggerMode = "HoldForMs";
                            m.TriggerHoldMs += delayStart;
                        }
                        if (delayEnd > 0)
                        {
                            m.DelayEndMs = delayEnd;
                        }
                        continue;

                    case TranslatedMacroAction.HoldVcButton:
                    case TranslatedMacroAction.HoldVcAxis:
                        // Frame-restarting sequences would re-run a Delay
                        // step, so the wait composes into the hold
                        // threshold: an assert delayed past a release
                        // would have asserted for zero frames anyway.
                        if (delayStart > 0)
                        {
                            m.TriggerMode = "HoldForMs";
                            m.TriggerHoldMs += delayStart;
                        }
                        if (delayEnd > 0)
                        {
                            // Release-extension twin: re-assert the target
                            // for delay_end ms on the release edge, so the
                            // output deactivates late, Steam's semantics.
                            var ext = new TranslatedMacro
                            {
                                Name = $"{m.Name} (release tail)",
                                Action = m.Action == TranslatedMacroAction.HoldVcButton
                                    ? TranslatedMacroAction.VcButtonTap
                                    : TranslatedMacroAction.VcAxisTap,
                                TriggerMode = "OnRelease",
                                ConsumeTrigger = false,
                                TargetXboxButtons = m.TargetXboxButtons,
                                TargetAxis = m.TargetAxis,
                                TargetAxisNegative = m.TargetAxisNegative,
                                TapDurationMs = delayEnd,
                                TriggerXboxButtons = m.TriggerXboxButtons,
                                TriggerAxisTarget = m.TriggerAxisTarget,
                                TriggerAxisThresholdPercent = m.TriggerAxisThresholdPercent,
                                TriggerFallbackDescriptor = m.TriggerFallbackDescriptor,
                                TriggerFallbackGateDescriptor = m.TriggerFallbackGateDescriptor,
                                TriggerDescriptorHalfAxis = m.TriggerDescriptorHalfAxis,
                                TriggerDescriptorInvert = m.TriggerDescriptorInvert,
                                TriggerDescriptorDeadZonePercent = m.TriggerDescriptorDeadZonePercent,
                            };
                            ext.TriggerInputDescriptors.AddRange(m.TriggerInputDescriptors);
                            // Inherit the source macro's scope (audit
                            // 2026-07-25, C10). This synthesized tail is
                            // built after translation, so it bypasses the
                            // AddMacro funnel; without the copy it shipped
                            // ungated and asserted its button in every
                            // action set for the delay window.
                            ext.LayerMask = m.LayerMask;
                            macros.Add(ext);
                        }
                        continue;

                    case TranslatedMacroAction.MouseLimitRegion:
                        // The materializer's engage / release pair takes
                        // one Delay step per leg.
                        if (delayStart > 0) m.DelayStartMs = delayStart;
                        if (delayEnd > 0) m.DelayEndMs = delayEnd;
                        continue;
                }
                if (!IsOneShotMacro(m.Action)) continue;
                bool pair = m.Action == TranslatedMacroAction.HoldKey
                    || m.Action == TranslatedMacroAction.HoldMouseButton;
                bool releaseLeg = m.TriggerMode == "OnRelease";
                if (delayStart > 0 && (pair || !releaseLeg))
                {
                    m.DelayStartMs = delayStart;
                }
                if (delayEnd > 0 && (pair || releaseLeg))
                {
                    m.DelayEndMs = delayEnd;
                }
                else if (delayEnd > 0)
                {
                    // Press-leg one-shot (v22). Steam's delay_end shifts
                    // the binding's DEACTIVATION edge past the release.
                    // For the assert-shaped taps the output deactivates
                    // late, so the assert grows to the authored length
                    // (press at t, instant release, deactivate at
                    // t + delay_end). For every other press-fired shape
                    // (cursor warp, set_led, gyro recenter, mouse nudge,
                    // wheel detent, cycle step, rumble pulse, the
                    // on-screen keyboard, and the latch flips, whose
                    // unlatch is the NEXT press, not the release) Steam's
                    // own deactivation edge emits nothing, so shifting it
                    // is unobservable and the value consumes silently.
                    switch (m.Action)
                    {
                        case TranslatedMacroAction.VcButtonTap:
                        case TranslatedMacroAction.VcAxisTap:
                        case TranslatedMacroAction.KeyTap:
                        case TranslatedMacroAction.MouseButtonTap:
                            m.TapDurationMs = Math.Max(m.TapDurationMs, delayEnd);
                            break;
                    }
                }
            }

            // Layer switches / mode shifts (v18): delay_start is the
            // activator's own hold-before-engage debounce. A long-press
            // activator already carries its threshold; the delay adds on.
            // delay_end (v22): Hold-mode carriers take the release linger
            // (the layer stays engaged delay_end past the release, and a
            // re-press inside the window cancels the pending disengage).
            // Non-Hold carriers (Toggle latches, preset Cycles, Custom
            // jumps) deactivate on a press, never on the release, so
            // Steam's own release+delay_end edge changes nothing for them
            // and the value consumes silently.
            if (activatorsBefore >= 0 && (delayStart > 0 || delayEnd > 0))
            {
                for (int i = activatorsBefore; i < run.Activators.Count; i++)
                {
                    var req = run.Activators[i];
                    if (delayStart > 0) req.DelayMs += delayStart;
                    if (delayEnd > 0 && req.Mode == "Hold") req.ReleaseDelayMs += delayEnd;
                }
            }
        }

        private static int ParseDelaySetting(SteamInputActivator activator, string key)
            => activator.Settings.TryGetValue(key, out var raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms)
                && ms > 0 ? Math.Min(ms, 10000) : 0;

        /// <summary>Macro shapes whose action fires once per trigger, so a
        /// prepended Delay step runs exactly once (v10 G5).</summary>
        private static bool IsOneShotMacro(TranslatedMacroAction a) => a switch
        {
            TranslatedMacroAction.RepeatKeyWhileHeld => false,
            TranslatedMacroAction.RepeatVcButtonWhileHeld => false,
            TranslatedMacroAction.RepeatVcAxisWhileHeld => false,
            // v19 (T1): the wheel turbo repeats its one-shot detent on the
            // macro repeat machinery, so a prepended Delay step would
            // re-run inside EVERY iteration and stretch the cadence. Its
            // delays ride the HoldForMs threshold + release linger
            // instead (the explicit case above, v22).
            TranslatedMacroAction.RepeatWheelWhileHeld => false,
            TranslatedMacroAction.HoldVcButton => false,
            TranslatedMacroAction.HoldVcAxis => false,
            TranslatedMacroAction.MouseLimitRegion => false,
            _ => true,
        };

        /// <summary>The controller_action verbs whose Long_Press routing
        /// EMITS something (an activator, a macro, a silent macro), so the
        /// activator counts as translated for the haptic pulse and the
        /// delay channels. The named-skip families (SteamClientActions,
        /// unknown verbs, placeholders) stay outside: haptics must not
        /// tick for a binding that lowered to nothing.</summary>
        private static readonly HashSet<string> LongPressEmittingVerbs = new(StringComparer.Ordinal)
        {
            "ADD_LAYER", "HOLD_LAYER", "REMOVE_LAYER", "CHANGE_PRESET",
            "CAMERA_RESET", "SET_LED", "MOUSE_POSITION", "MOUSE_DELTA",
            "SCREENSHOT", "SYSTEM_KEY_1", "SHOW_KEYBOARD",
        };

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
            int macrosBefore = run.Profile.Macros.Count;
            // v22: layer carries emitted in this window take the delay
            // channels too (delay_start on top of the long-press debounce,
            // delay_end as the Hold-mode release linger).
            int activatorsBefore = run.Activators.Count;
            foreach (var binding in activator.Bindings)
            {
                string bt = (binding.Type ?? "").Trim().ToLowerInvariant();
                string action = FirstToken(binding.Param).ToUpperInvariant();
                if (bt == "mode_shift")
                {
                    anyCarry = true;
                    TranslateModeShift(run, preset, binding, source, actPath, delayMs, toggle);
                }
                else if (bt == "controller_action")
                {
                    // Every verb routes through the canonical walk (v24):
                    // layer verbs ride the DelayMs debounce, the macro
                    // verbs fire at the hold threshold (the SET_LED /
                    // camera_reset HoldForMs shape, which mouse_position /
                    // screenshot / show_keyboard adopted in v24), and the
                    // Steam-client-only families keep their OWN named
                    // skips (SteamSystemAction and friends) instead of a
                    // long-press line: the impossibility is the verb's,
                    // not the activator's. anyCarry only for verbs that
                    // emit something (haptics must not tick for a binding
                    // that lowered to a named skip).
                    anyCarry |= LongPressEmittingVerbs.Contains(action);
                    TranslateControllerAction(run, preset, binding, source, layer, actPath,
                        onRelease: false, input.Name, delayMs, toggle);
                }
                else if (bt == "game_action" || bt == "game_action_analog")
                {
                    // The binding is impossible (game-side Steam API
                    // session), not the Long_Press hosting it: count into
                    // the per-preset GameActionsNotSupported aggregate
                    // like every other walk (v24), instead of printing a
                    // long-press line per binding.
                    run.GameActionsByPreset[preset.Id] =
                        run.GameActionsByPreset.GetValueOrDefault(preset.Id) + 1;
                }
                else if (bt == "key_press")
                {
                    anyCarry |= TranslateLongPressKey(run, binding, source, actPath,
                        delayMs, holdRepeats, intervalMs, toggle, input.Name);
                }
                else if (bt == "mouse_button")
                {
                    anyCarry |= TranslateLongPressMouse(run, binding, source, actPath,
                        delayMs, input.Name);
                }
                else if (bt == "xinput_button")
                {
                    anyCarry |= TranslateLongPressVc(run, binding, source, actPath,
                        delayMs, holdRepeats, intervalMs, toggle, input.Name);
                }
                else if (bt == "mouse_wheel" && ParseWheelParam(binding.Param) is { } lpWheel)
                {
                    // One discrete detent at the hold threshold (v15): the
                    // MouseWheelTap macro on the HoldForMs trigger.
                    EmitWheelTapMacro(run, binding, source, actPath, lpWheel,
                        input.Name, triggerMode: "HoldForMs", holdMs: delayMs);
                    anyCarry = true;
                }
                else
                {
                    ReportSkipUnlessSilent(run, TranslationReasons.LongPressNotSupported, actPath, binding);
                }
            }

            if (anyCarry)
            {
                EmitHapticPulse(run, activator, source, input.Name, actPath,
                    "OnPress", holdMs: delayMs);
                ConsumeActivatorDelays(run, activator, actPath, macrosBefore, activatorsBefore);
            }
        }

        /// <summary>A Long_Press key binding (v10 G10): the key goes down
        /// at the hold threshold and stays down until the physical input
        /// releases, Valve's documented Long_Press shape, via the HoldKey
        /// pair. The autofire / latch variants keep their wave-2A shapes
        /// at the same threshold. Any VK rides SendInput on the macro
        /// forms, so the KbM row engine's closed key list does not gate
        /// here (v10 G11). Returns true when a macro was emitted (the
        /// activator counts as translated for the dropped-extras notes).</summary>
        private bool TranslateLongPressKey(Run run, SteamInputBinding binding,
            ResolvedSource source, string path,
            int holdMs, bool holdRepeats, int intervalMs, bool toggle, string inputName)
        {
            string keyName = FirstToken(binding.Param);
            if (!SteamInputVkTable.TryResolve(keyName, out byte vk, out _))
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownKey,
                    path, binding.Raw, args: keyName);
                return false;
            }

            if (toggle)
            {
                // toggle + hold_repeats composes the pulsed latch (v18).
                return EmitKeyToggleMacro(run, binding, source, path, vk, keyName,
                    onRelease: false, inputName, holdMs,
                    pulse: holdRepeats, pulseIntervalMs: intervalMs);
            }

            if (holdRepeats)
            {
                return EmitKeyMacro(run, binding, source, path,
                    (TranslatedMacroAction.RepeatKeyWhileHeld, "HoldForMs"),
                    vk, intervalMs, keyName, inputName, holdMs);
            }
            return EmitKeyHoldMacro(run, binding, source, path, vk, keyName,
                "HoldForMs", holdMs, inputName);
        }

        /// <summary>A Long_Press mouse_button binding (v10 G10): the mouse
        /// button goes down at the hold threshold and holds until the
        /// physical release, via the HoldMouseButton pair (the
        /// materializer's MouseButtonPress-until-release + OnRelease
        /// MouseButtonRelease twin).</summary>
        private bool TranslateLongPressMouse(Run run, SteamInputBinding binding,
            ResolvedSource source, string path, int holdMs, string inputName,
            string triggerMode = "HoldForMs")
        {
            if (!SteamInputVkTable.TryResolveMouseButtonIndex(binding.Param, out int btn))
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownMouseButton,
                    path, binding.Raw, args: binding.Param);
                return false;
            }
            var macro = new TranslatedMacro
            {
                Name = $"Hold mouse {FirstToken(binding.Param).ToUpperInvariant()} ({inputName})",
                Action = TranslatedMacroAction.HoldMouseButton,
                TriggerMode = triggerMode,
                TriggerHoldMs = holdMs,
                // Never consumed: the OnRelease twin reads the same
                // trigger, and a consumed bit would release it early.
                ConsumeTrigger = false,
                MouseButtonIndex = btn,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw,
                emitted: $"Long-press hold macro: mouse button {macro.MouseButtonIndex}");
            return true;
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
            // Axis-natured targets (trigger pulls, stick directions) ride
            // the AxisHold channel since v15, with the v18 latch and turbo
            // variants at the same threshold.
            if (xt.IsTriggerAxis || xt.IsStickAxis)
            {
                if (toggle)
                {
                    return EmitVcAxisToggleMacro(run, binding, source, path, xt, inputName,
                        holdMs, pulse: holdRepeats, pulseIntervalMs: intervalMs);
                }
                if (holdRepeats)
                {
                    return EmitVcAxisTurboMacro(run, binding, source, path, xt,
                        intervalMs, holdMs, inputName);
                }
                EmitVcAxisHoldMacro(run, binding, source, path, xt, holdMs, inputName);
                return true;
            }

            if (toggle)
            {
                // toggle + hold_repeats composes the pulsed latch (v18).
                return EmitVcToggleMacro(run, binding, source, path, xt,
                    rowKept: false, inputName, holdMs,
                    pulse: holdRepeats, pulseIntervalMs: intervalMs);
            }
            if (holdRepeats)
                return EmitVcTurboMacro(run, binding, source, path, xt, intervalMs, holdMs, inputName);
            return EmitVcHoldMacro(run, binding, source, path, xt, holdMs, inputName);
        }

        // InterruptibleDropped retired in v18. The stored "interruptable"
        // "0" MATCHES PadForge's native evaluation exactly: PadForge never
        // cancels one activator's output because a sibling on the same
        // input fired (a Start_Press row keeps firing while a Long_Press
        // macro engages), which is precisely Steam's interruptable-OFF
        // behavior. The note reported the matching case as a divergence,
        // so it was factually wrong, not merely noise. The un-implemented
        // direction is Steam's DEFAULT (interruptible on, sibling fires
        // cancel each other), which the report vocabulary has never named
        // per the defaults-are-silent convention.

        private void TranslateBinding(Run run, SteamInputPreset preset, SteamInputBinding binding,
            ResolvedSource source, string clickGate, string layer, string path,
            bool soft, bool onRelease, bool holdRepeats, int intervalMs, bool toggle,
            string inputName, int delayStartMs = 0, int delayEndMs = 0)
        {
            // Row-shaped bindings have no delay channel, so an activator
            // carrying delay_start / delay_end reroutes them onto the
            // delayed hold-macro lowerings (v18): the key / mouse-button /
            // VC hold pairs, whose Delay steps and HoldForMs composition
            // ConsumeActivatorDelays stamps after the walk.
            bool rerouteForDelays = (delayStartMs > 0 || delayEndMs > 0)
                && !onRelease && !toggle && !holdRepeats;
            string type = (binding.Type ?? "").Trim().ToLowerInvariant();
            switch (type)
            {
                case "key_press":
                    TranslateKeyPress(run, preset, binding, source, clickGate, layer, path,
                        soft, onRelease, holdRepeats, intervalMs, toggle, inputName,
                        rerouteForDelays);
                    break;

                case "mouse_button":
                {
                    if (!SteamInputVkTable.TryResolveMouseButton(binding.Param, out string target))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownMouseButton,
                            path, binding.Raw, args: binding.Param);
                        break;
                    }
                    if (toggle)
                    {
                        // v18: the latch replaces the momentary row
                        // (Steam's toggle replaces the momentary output),
                        // flipping on release for release activators, with
                        // hold_repeats composing the pulsed latch.
                        SteamInputVkTable.TryResolveMouseButtonIndex(binding.Param, out int toggleBtn);
                        EmitMouseToggleMacro(run, binding, source, path, toggleBtn, inputName,
                            onRelease, pulse: holdRepeats, pulseIntervalMs: intervalMs);
                        break;
                    }
                    if (onRelease)
                    {
                        // v10 G6: one click when the input releases, via a
                        // MouseButtonTap macro (a row would click for the
                        // whole hold instead).
                        EmitMouseTapMacro(run, binding, source, path, inputName);
                        break;
                    }
                    if (rerouteForDelays)
                    {
                        // v18: the delayed press rides the HoldMouseButton
                        // pair (down after delay_start, up delay_end after
                        // the release) instead of a row.
                        TranslateLongPressMouse(run, binding, source, path, holdMs: 0,
                            inputName, triggerMode: "OnPress");
                        break;
                    }
                    EmitSourceRow(run, isKbm: true, layer, target, source, clickGate, isAxis: false,
                        soft, path, binding.Raw);
                    break;
                }

                case "mouse_wheel":
                {
                    var wheel = ParseWheelParam(binding.Param);
                    if (wheel == null)
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                            path, binding.Raw, args: $"mouse_wheel {binding.Param}");
                        break;
                    }
                    if (toggle)
                    {
                        // v18: a latched wheel DOES scroll until unlatched,
                        // exactly what Steam's toggle asks of a binding
                        // whose held state scrolls continuously. The
                        // ToggleWheel latch replaces the momentary row.
                        EmitWheelToggleMacro(run, binding, source, path, wheel.Value,
                            inputName, intervalMs, onRelease);
                        break;
                    }
                    if (onRelease)
                    {
                        // One discrete detent on the release edge (v15): a
                        // row would scroll for the whole hold instead, the
                        // inverse of what the config asked for.
                        EmitWheelTapMacro(run, binding, source, path, wheel.Value,
                            inputName, triggerMode: "OnRelease");
                        break;
                    }
                    if (holdRepeats)
                    {
                        // hold_repeats (v19, T1): one detent per authored
                        // repeat_rate while held. The row would scroll
                        // continuously at the full per-frame rate instead
                        // (thousands of detents per second), ignoring the
                        // authored cadence entirely.
                        EmitWheelTurboMacro(run, binding, source, path, wheel.Value,
                            inputName, intervalMs);
                        break;
                    }
                    if (rerouteForDelays)
                    {
                        // v22: a wheel ROW has no delay channel, so the
                        // delayed binding rides the RepeatWheelWhileHeld
                        // turbo at the latch cadence (one detent per
                        // default 100 ms interval, the ToggleWheel
                        // held-row rate). ConsumeActivatorDelays then
                        // composes delay_start into the HoldForMs
                        // threshold and delay_end into the release
                        // linger.
                        EmitWheelTurboMacro(run, binding, source, path, wheel.Value,
                            inputName, intervalMs);
                        break;
                    }
                    var src = BuildSource(source, soft);
                    // Compose the wheel direction with a member-level
                    // output flip (a scrollwheel counterclockwise drag,
                    // v10 G4). On a non-half source Invert IS the output
                    // flip, so XOR the two rather than clobber; half-axis
                    // sources keep Invert as their half selector and take
                    // the wheel flip on InvertOutput as before.
                    bool memberFlip = !src.HalfAxis && src.Invert;
                    if (memberFlip) src.Invert = false;
                    SetOutputInvert(src, wheel.Value.Invert ^ memberFlip);
                    // The AND companion rides the source's own
                    // GateDescriptor (v18): a single-pad click member
                    // carries its half's touch-spot gate (#9 B-1).
                    AddRowSource(run, isKbm: true, layer, wheel.Value.Target, src, isAxis: true,
                        StatusFor(source), ReasonFor(source), path, binding.Raw);
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
                    // LSTICK_/RSTICK_ direction params (v13): the binding
                    // drives one direction of a virtual thumb stick, so it
                    // lowers to a bipolar axis row with the direction as
                    // the output polarity (the same output-flip seam the
                    // mouse_wheel rows use). Steam holds the stick at full
                    // deflection while the input is held. A pressed button
                    // source evaluates to the full axis value, and an
                    // analog host (a stick wedge, a trigger pull) carries
                    // its own magnitude. The release variant rides the
                    // AxisHold tap since v15; toggle / turbo still have no
                    // axis latch or pulse primitive and keep their notes.
                    if (xt.IsStickAxis)
                    {
                        if (onRelease)
                        {
                            EmitVcAxisTapMacro(run, binding, source, path, xt, inputName);
                            break;
                        }
                        if (toggle)
                        {
                            // v18: the axis latch replaces the momentary
                            // row; hold_repeats composes the pulsed latch.
                            EmitVcAxisToggleMacro(run, binding, source, path, xt, inputName,
                                pulse: holdRepeats, pulseIntervalMs: intervalMs);
                            break;
                        }
                        if (holdRepeats)
                        {
                            // v18: axis turbo replaces the momentary row.
                            EmitVcAxisTurboMacro(run, binding, source, path, xt,
                                intervalMs, holdMs: 0, inputName);
                            break;
                        }
                        if (rerouteForDelays)
                        {
                            // v18: delayed stick-direction bindings ride
                            // the axis hold macro (delays stamped after
                            // the walk).
                            EmitVcAxisHoldMacro(run, binding, source, path, xt, holdMs: 0,
                                inputName, triggerMode: "OnPress");
                            break;
                        }
                        EmitStickDirectionRow(run, binding, source, clickGate, layer, path, soft, xt);
                        break;
                    }
                    if (onRelease)
                    {
                        if (xt.IsTriggerAxis)
                        {
                            // One full-pull AxisHold tap on the release
                            // edge (v15).
                            EmitVcAxisTapMacro(run, binding, source, path, xt, inputName);
                            break;
                        }
                        // v10 G6: one button tap when the input releases,
                        // via a VcButtonTap macro.
                        EmitVcTapMacro(run, binding, source, path, xt, inputName);
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
                            // toggle + hold_repeats composes the pulsed
                            // latch (v18) instead of dropping the turbo.
                            EmitVcToggleMacro(run, binding, source, path, xt,
                                rowKept: true, inputName,
                                pulse: holdRepeats, pulseIntervalMs: intervalMs);
                            latchEmitted = true;
                            // fall through: the row emits below (identity or
                            // divergent), feeding the latch trigger.
                        }
                        else
                        {
                            // v18: trigger-axis toggles latch the pull via
                            // the ToggleVcAxis macro, replacing the row.
                            EmitVcAxisToggleMacro(run, binding, source, path, xt, inputName,
                                pulse: holdRepeats, pulseIntervalMs: intervalMs);
                            break;
                        }
                    }
                    // Turbo (wave 2A): hold_repeats pulses the target while
                    // the physical input is held (Steam stores repeat_rate
                    // in ms; shipped configurator suffixes it
                    // #Unit_Milliseconds). A latch already replaced the
                    // output when one was emitted above. Identity turbo
                    // (v10 G14) drops the identity row and pulses through
                    // a device-free descriptor trigger on the hosting
                    // input itself: a combined-output trigger fed by the
                    // identity row would hold the pulsed bit solid.
                    // Trigger-axis targets pulse the pull via the v18
                    // axis-turbo macro.
                    if (!latchEmitted && holdRepeats)
                    {
                        if (!xt.IsTriggerAxis)
                        {
                            EmitVcTurboMacro(run, binding,
                                identity ? WithoutOutputTrigger(source) : source,
                                path, xt, intervalMs, holdMs: 0, inputName);
                            break;
                        }
                        EmitVcAxisTurboMacro(run, binding,
                            identity ? WithoutOutputTrigger(source) : source,
                            path, xt, intervalMs, holdMs: 0, inputName);
                        break;
                    }
                    if (rerouteForDelays)
                    {
                        // v18: delayed presses ride the hold macros
                        // (identity included: a row cannot wait).
                        if (xt.IsTriggerAxis)
                        {
                            EmitVcAxisHoldMacro(run, binding,
                                identity ? WithoutOutputTrigger(source) : source,
                                path, xt, holdMs: 0, inputName, triggerMode: "OnPress");
                        }
                        else
                        {
                            EmitVcHoldMacro(run, binding,
                                identity ? WithoutOutputTrigger(source) : source,
                                path, xt, holdMs: 0, inputName, triggerMode: "OnPress");
                        }
                        break;
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
                    // Pulse-gesture hosts (double taps, swipes, v26) ride
                    // the one-shot latching arm, the flick-host rule.
                    TranslateModeShift(run, preset, binding, source, path, toggle: toggle,
                        oneShotHost: IsGesturePulseHost(source));
                    break;

                case "controller_action":
                    TranslateControllerAction(run, preset, binding, source, layer, path, onRelease,
                        inputName, toggle: toggle, oneShotHost: IsGesturePulseHost(source));
                    break;

                case "game_action_analog": // the analog sibling, same Steam-session surface (v24)
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

        /// <summary>An LSTICK_/RSTICK_ direction binding (v13) as a bipolar
        /// axis row on the thumb-axis target. Row value convention is SDL
        /// "+X right, +Y down" (identical to the joystick_move rows;
        /// Step 3's WriteBipolarAxisTarget negates Y onto the XInput
        /// thumb), so <see cref="XInputTargetTable.XInputTarget.StickAxisNegative"/>
        /// is the output polarity for up / left. The polarity composes
        /// with a member-level output flip exactly like the mouse_wheel
        /// rows: on a half-axis host Invert stays the half SELECTOR and
        /// the polarity rides InvertOutput (SetOutputInvert asks the
        /// engine's own predicate), so a stick wedge keeps its wedge and
        /// still pushes the virtual stick the bound way.</summary>
        private void EmitStickDirectionRow(Run run, SteamInputBinding binding,
            ResolvedSource source, string clickGate, string layer, string path, bool soft,
            XInputTargetTable.XInputTarget xt)
        {
            var src = BuildSource(source, soft);
            bool memberFlip = !src.HalfAxis && src.Invert;
            if (memberFlip) src.Invert = false;
            SetOutputInvert(src, xt.StickAxisNegative ^ memberFlip);
            // The AND companion rides the source (v18): the group click
            // gate wins where present, else the resolved source's own.
            if (!string.IsNullOrEmpty(clickGate)) src.GateDescriptor = clickGate;
            AddRowSource(run, isKbm: false, layer, xt.Target, src, isAxis: true,
                StatusFor(source), ReasonFor(source), path, binding.Raw);
        }

        /// <summary>mouse_wheel param to its KbM target and output flip.
        /// KbmScroll positive = up after Step 3's negation, and a pressed
        /// button source evaluates positive (SDL "down"), so scroll-up
        /// needs Invert and scroll-down doesn't. Horizontal has no
        /// negation: right is plain. Null for unknown params.</summary>
        private static (string Target, bool Invert)? ParseWheelParam(string param)
            => (param ?? "").Trim().ToUpperInvariant() switch
            {
                "SCROLL_UP" => ("KbmScroll", true),
                "SCROLL_DOWN" => ("KbmScroll", false),
                "SCROLL_RIGHT" => ("KbmScrollH", false),
                "SCROLL_LEFT" => ("KbmScrollH", true),
                _ => null,
            };

        private void TranslateKeyPress(Run run, SteamInputPreset preset, SteamInputBinding binding,
            ResolvedSource source, string clickGate, string layer, string path,
            bool soft, bool onRelease, bool holdRepeats, int intervalMs, bool toggle,
            string inputName, bool rerouteForDelays = false)
        {
            string keyName = FirstToken(binding.Param);
            if (!SteamInputVkTable.TryResolve(keyName, out byte vk, out bool supported))
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownKey,
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
                // toggle + hold_repeats composes the pulsed latch (v18).
                EmitKeyToggleMacro(run, binding, source, path, vk, keyName, onRelease, inputName,
                    pulse: holdRepeats, pulseIntervalMs: intervalMs);
                return;
            }

            if (onRelease || holdRepeats)
            {
                // Macro-backed forms (SendInput, so any VK works here).
                EmitKeyMacro(run, binding, source, path,
                    onRelease
                        ? (TranslatedMacroAction.KeyTap, "OnRelease")
                        : (TranslatedMacroAction.RepeatKeyWhileHeld, "WhileHeld"),
                    vk, intervalMs, keyName, inputName);
                return;
            }

            // VKs outside the KbM row engine's closed key list (F13-F24,
            // PrintScreen, the lock keys) have no row channel, so the
            // plain press rides the SendInput HoldKey pair instead of the
            // old UnsupportedKey skip (v10 G11): down on press, up on
            // release, exact Steam semantics. Activator delays reroute
            // the same way (v18): the HoldKey pair carries Delay steps.
            if (!supported || rerouteForDelays)
            {
                EmitKeyHoldMacro(run, binding, source, path, vk, keyName,
                    "OnPress", holdMs: 0, inputName);
                return;
            }

            EmitSourceRow(run, isKbm: true, layer, SteamInputVkTable.KbmKeyTarget(vk),
                source, clickGate, isAxis: false, soft, path, binding.Raw);
        }

        private bool EmitKeyMacro(Run run, SteamInputBinding binding,
            ResolvedSource source, string path,
            (TranslatedMacroAction Action, string TriggerMode) shape,
            byte vk, int intervalMs, string keyName, string inputName, int holdMs = 0)
        {
            string verb = shape.Action == TranslatedMacroAction.KeyTap ? "Tap" : "Autofire";
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
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw, emitted: $"{verb} {keyName} macro");
            return true;
        }

        /// <summary>The full-fidelity held key (v10 G10/G11): a HoldKey
        /// macro the materializer lowers to a KeyPress-until-release plus
        /// an OnRelease KeyRelease twin. <paramref name="triggerMode"/> is
        /// "OnPress" (plain press hosts whose VK has no row channel) or
        /// "HoldForMs" (Long_Press at <paramref name="holdMs"/>).</summary>
        private bool EmitKeyHoldMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, byte vk, string keyName, string triggerMode, int holdMs, string inputName)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Hold {keyName} ({inputName})",
                Action = TranslatedMacroAction.HoldKey,
                TriggerMode = triggerMode,
                TriggerHoldMs = holdMs,
                // Never consumed: the OnRelease twin reads the same
                // trigger, and a consumed bit would release it early.
                ConsumeTrigger = false,
                VirtualKey = vk,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw, emitted: $"Hold {keyName} macro");
            return true;
        }

        /// <summary>A one-shot mouse_button tap via a MouseButtonTap macro
        /// (down + tap duration + up through SendInput). Release activators
        /// ride "OnRelease" (v10 G6), stick swipe flicks "OnPress" (v12).</summary>
        private void EmitMouseTapMacro(Run run, SteamInputBinding binding,
            ResolvedSource source, string path, string inputName,
            string triggerMode = "OnRelease")
        {
            SteamInputVkTable.TryResolveMouseButtonIndex(binding.Param, out int btn);
            var macro = new TranslatedMacro
            {
                Name = $"Click mouse {FirstToken(binding.Param).ToUpperInvariant()} ({inputName})",
                Action = TranslatedMacroAction.MouseButtonTap,
                TriggerMode = triggerMode,
                ConsumeTrigger = false,
                MouseButtonIndex = btn,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw, emitted: $"Mouse tap macro (button {btn})");
        }

        /// <summary>A one-shot tap of the target virtual-controller button
        /// via a VcButtonTap macro. Release activators ride "OnRelease"
        /// (v10 G6), stick swipe flicks "OnPress" (v12).</summary>
        private void EmitVcTapMacro(Run run, SteamInputBinding binding,
            ResolvedSource source, string path, XInputTargetTable.XInputTarget xt,
            string inputName, string triggerMode = "OnRelease")
        {
            var macro = new TranslatedMacro
            {
                Name = $"Tap {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.VcButtonTap,
                TriggerMode = triggerMode,
                TargetXboxButtons = xt.XboxButtonBit,
                ConsumeTrigger = false,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw, emitted: $"Tap {xt.Target} macro");
        }

        /// <summary>A one-shot assert of an axis-natured VC target (v15):
        /// trigger pulls at full pull, stick directions at full deflection,
        /// via a VcAxisTap macro (the AxisHold action at the default tap
        /// duration). Release activators ride "OnRelease", swipe flicks
        /// "OnPress".</summary>
        private void EmitVcAxisTapMacro(Run run, SteamInputBinding binding,
            ResolvedSource source, string path, XInputTargetTable.XInputTarget xt,
            string inputName, string triggerMode = "OnRelease")
        {
            var macro = new TranslatedMacro
            {
                Name = $"Tap {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.VcAxisTap,
                TriggerMode = triggerMode,
                ConsumeTrigger = false,
                TargetAxis = xt.Target,
                TargetAxisNegative = xt.StickAxisNegative,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw, emitted: $"Tap {xt.Target} axis macro");
        }

        /// <summary>A Long_Press binding onto an axis-natured VC target
        /// (v15): the axis asserts at the hold threshold and stays asserted
        /// until the physical input releases, via a HoldVcAxis macro (the
        /// AxisHold action riding the HoldVcButton repeat shape). Consumes
        /// its trigger bits like the button hold (the interruptable-pause
        /// approximation); descriptor-triggered hosts have no bits and
        /// FillMacroTrigger forces the flag off there.</summary>
        private void EmitVcAxisHoldMacro(Run run, SteamInputBinding binding,
            ResolvedSource source, string path, XInputTargetTable.XInputTarget xt,
            int holdMs, string inputName, string triggerMode = "HoldForMs")
        {
            bool dbl = triggerMode == "DoublePress";
            var macro = new TranslatedMacro
            {
                Name = $"{(dbl ? "Double press" : "Long press")} {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.HoldVcAxis,
                TriggerMode = triggerMode,
                TriggerHoldMs = holdMs,
                ConsumeTrigger = true,
                TargetAxis = xt.Target,
                TargetAxisNegative = xt.StickAxisNegative,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw,
                emitted: $"{(dbl ? "Double-press" : "Long-press")} hold macro: {xt.Target} axis");
        }

        /// <summary>One discrete mouse-wheel detent per fire (v15), via a
        /// MouseWheelTap macro. Wheel direction from the parsed param:
        /// KbmScroll rides the vertical lane (its Invert flag means
        /// scroll-UP after Step 3's negation, so ticks are +1 when set,
        /// -1 for scroll-down), KbmScrollH the horizontal (Invert = left =
        /// -1). Release activators ride "OnRelease", swipe flicks
        /// "OnPress", Long_Press "HoldForMs".</summary>
        private void EmitWheelTapMacro(Run run, SteamInputBinding binding,
            ResolvedSource source, string path, (string Target, bool Invert) wheel,
            string inputName, string triggerMode, int holdMs = 0)
        {
            bool horizontal = wheel.Target == "KbmScrollH";
            var macro = new TranslatedMacro
            {
                Name = $"Wheel tick ({inputName})",
                Action = TranslatedMacroAction.MouseWheelTap,
                TriggerMode = triggerMode,
                TriggerHoldMs = holdMs,
                ConsumeTrigger = false,
                WheelHorizontal = horizontal,
                // Vertical: ParseWheelParam sets Invert for SCROLL_UP (the
                // row convention needs the flip); the tap sends +ticks for
                // up. Horizontal: Invert marks SCROLL_LEFT; +ticks = right.
                WheelTicks = horizontal ? (wheel.Invert ? -1 : 1) : (wheel.Invert ? 1 : -1),
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw, emitted: $"Wheel tick macro ({FirstToken(binding.Param).ToUpperInvariant()})");
        }

        /// <summary>One Scroll Wheel List item binding as a cycle step
        /// (v16). The list picker's own vocabulary is "a button or key"
        /// (shipped ScrollWheelListN strings), so the step kinds are the
        /// one-shot tap family: key / mouse button / wheel tick / VC
        /// button / VC axis. Wheel tick signs reuse
        /// <see cref="EmitWheelTapMacro"/>'s math. Anything outside that
        /// vocabulary gets its existing named skip (Steam-client verbs,
        /// game actions) or the generic safety net, and returns null.</summary>
        private TranslatedCycleStep TryBuildCycleStep(Run run, SteamInputPreset preset,
            SteamInputBinding binding, string listPath)
        {
            string type = (binding.Type ?? "").Trim().ToLowerInvariant();
            switch (type)
            {
                case "key_press":
                {
                    string keyName = FirstToken(binding.Param);
                    if (!SteamInputVkTable.TryResolve(keyName, out byte vk, out _))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownKey,
                            listPath, binding.Raw, args: keyName);
                        return null;
                    }
                    return new TranslatedCycleStep
                    { Kind = TranslatedCycleStepKind.KeyTap, VirtualKey = vk };
                }

                case "mouse_button":
                {
                    if (!SteamInputVkTable.TryResolveMouseButtonIndex(binding.Param, out int btn))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownMouseButton,
                            listPath, binding.Raw, args: binding.Param);
                        return null;
                    }
                    return new TranslatedCycleStep
                    { Kind = TranslatedCycleStepKind.MouseButtonTap, MouseButtonIndex = btn };
                }

                case "mouse_wheel":
                {
                    var wheel = ParseWheelParam(binding.Param);
                    if (wheel == null)
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                            listPath, binding.Raw, args: $"mouse_wheel {binding.Param}");
                        return null;
                    }
                    bool horizontal = wheel.Value.Target == "KbmScrollH";
                    return new TranslatedCycleStep
                    {
                        Kind = TranslatedCycleStepKind.WheelTap,
                        WheelHorizontal = horizontal,
                        WheelTicks = horizontal
                            ? (wheel.Value.Invert ? -1 : 1)
                            : (wheel.Value.Invert ? 1 : -1),
                    };
                }

                case "xinput_button":
                {
                    if (!XInputTargetTable.TryResolve(binding.Param, out var xt))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownXInputButton,
                            listPath, binding.Raw, args: binding.Param);
                        return null;
                    }
                    if (xt.IsTriggerAxis || xt.IsStickAxis)
                    {
                        return new TranslatedCycleStep
                        {
                            Kind = TranslatedCycleStepKind.VcAxisTap,
                            TargetAxis = xt.Target,
                            TargetAxisNegative = xt.StickAxisNegative,
                        };
                    }
                    return new TranslatedCycleStep
                    { Kind = TranslatedCycleStepKind.VcButtonTap, TargetXboxButtons = xt.XboxButtonBit };
                }

                case "controller_action":
                {
                    // A list stop has no press/hold state to drive the
                    // verb walk's layer / hold shapes, and the Steam-only
                    // families keep their class skips.
                    string verb = FirstToken(binding.Param).ToUpperInvariant();
                    run.Report.Add(TranslationStatus.Skipped,
                        IsSteamClientAction(verb)
                            ? TranslationReasons.SteamSystemAction
                            : TranslationReasons.UnsupportedControllerAction,
                        listPath, binding.Raw, args: verb);
                    return null;
                }

                case "game_action_analog": // the analog sibling, same Steam-session surface (v24)
                case "game_action":
                    // The per-preset aggregate, same as every other walk.
                    run.GameActionsByPreset[preset.Id] =
                        run.GameActionsByPreset.GetValueOrDefault(preset.Id) + 1;
                    return null;

                default:
                    if (type.Length == 0) return null; // placeholder, silent
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnknownBindingType,
                        listPath, binding.Raw, args: binding.Type ?? "");
                    return null;
            }
        }

        /// <summary>Report / golden text for one cycle step.</summary>
        private static string DescribeCycleStep(TranslatedCycleStep s) => s.Kind switch
        {
            TranslatedCycleStepKind.KeyTap =>
                $"Tap VK 0x{s.VirtualKey.ToString("X2", CultureInfo.InvariantCulture)}",
            TranslatedCycleStepKind.MouseButtonTap => $"Click mouse button {s.MouseButtonIndex}",
            TranslatedCycleStepKind.WheelTap =>
                $"Wheel tick {(s.WheelTicks >= 0 ? "+" : "")}{s.WheelTicks}{(s.WheelHorizontal ? " H" : "")}",
            TranslatedCycleStepKind.VcButtonTap =>
                $"Tap 0x{s.TargetXboxButtons.ToString("X4", CultureInfo.InvariantCulture)}",
            TranslatedCycleStepKind.VcAxisTap =>
                $"Tap {s.TargetAxis}{(s.TargetAxisNegative ? " (neg)" : "")}",
            _ => "step",
        };

        /// <summary>Copy of <paramref name="s"/> with the combined-output
        /// trigger identity stripped, so <see cref="FillMacroTrigger"/>
        /// takes the device-free descriptor shape. Used by the identity
        /// turbo (v10 G14), where the pulsed target bit and the trigger
        /// bit are the same bit and a combined trigger would read its own
        /// pulses. Same full-field discipline as <see cref="WithDeadZone"/>.</summary>
        private static ResolvedSource WithoutOutputTrigger(ResolvedSource s) => new()
        {
            Descriptor = s.Descriptor,
            HalfAxis = s.HalfAxis,
            Invert = s.Invert,
            DeadZone = s.DeadZone,
            AutomapTarget = s.AutomapTarget,
            XboxButtonBit = 0,
            MacroAxisTarget = null,
            TrackpadFeature = s.TrackpadFeature,
            IsAnalogTriggerPull = s.IsAnalogTriggerPull,
            GateDescriptor = s.GateDescriptor,
            Gate2Descriptor = s.Gate2Descriptor,
            PartialReasonKey = s.PartialReasonKey,
        };

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
        /// ConsumeTrigger is forced off for them. Gesture-gated reads
        /// need no note since v14: the imported set is Authoritative and
        /// the engine self-arms every referenced gesture family at apply
        /// (TouchpadGestureAutoArm).</summary>
        private static void FillMacroTrigger(TranslatedMacro macro, ResolvedSource source)
        {
            // Half-axis hosts (stick wedges, trigger pulls) carry their read
            // shape beside the trigger (v12). A descriptor trigger converts
            // to an axis entry that reads the FULL axis by default, so
            // without the stamp a dpad_north macro would fire on any
            // deflection of the whole Y axis. Stamped unconditionally: the
            // combined-output path needs it too, because FinalizeMacroTriggers
            // can swap that trigger onto the fallback descriptor later.
            if (source.HalfAxis)
            {
                macro.TriggerDescriptorHalfAxis = true;
                macro.TriggerDescriptorInvert = source.Invert;
                macro.TriggerDescriptorDeadZonePercent = source.DeadZone;
            }
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
                return;
            }
            macro.TriggerXboxButtons = 0;
            macro.TriggerAxisTarget = "";
            macro.TriggerInputDescriptors.Add(source.Descriptor);
            if (!string.IsNullOrEmpty(source.GateDescriptor))
                macro.TriggerInputDescriptors.Add(source.GateDescriptor);
            // Second AND companion (v26): the trigger entries are ANDed,
            // so a chord partner beside a spent primary gate simply
            // appends.
            if (!string.IsNullOrEmpty(source.Gate2Descriptor))
                macro.TriggerInputDescriptors.Add(source.Gate2Descriptor);
            macro.ConsumeTrigger = false;
        }

        // MacroTriggerReport retired in v15: both trigger shapes
        // FillMacroTrigger emits are normal working plumbing (the
        // combined-output form is rescued by FinalizeMacroTriggers when
        // unfed), so every macro emission reports Clean MacroEmitted at
        // its own site now.

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
            int holdMs = 0, string triggerMode = null, bool pulse = false, int pulseIntervalMs = 100)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Toggle {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.ToggleVcButton,
                TriggerMode = triggerMode ?? (holdMs > 0 ? "HoldForMs" : "OnPress"),
                TriggerHoldMs = holdMs,
                TargetXboxButtons = xt.XboxButtonBit,
                ConsumeTrigger = false,
                // toggle + hold_repeats (v18): the latch pulses on the
                // turbo square wave instead of holding solid.
                PulseWhileLatched = pulse,
                IntervalMs = pulseIntervalMs,
            };
            if (rowKept)
            {
                macro.TriggerXboxButtons = xt.XboxButtonBit;
                macro.TriggerAxisTarget = "";
            }
            else
            {
                FillMacroTrigger(macro, source);
            }
            run.AddMacro(macro);
            // rowKept latches stay Partial (the kept row re-asserts the
            // target during the unlatching press); descriptor-triggered
            // latches are Clean (gesture-gated triggers self-arm at
            // apply since v14).
            run.Report.Add(rowKept ? TranslationStatus.Partial : TranslationStatus.Clean,
                TranslationReasons.ToggleLatchEmitted,
                path, binding.Raw, emitted: $"Toggle {xt.Target} latch macro", xt.Target);
            return true;
        }

        /// <summary>The activator toggle on a key binding (wave 2A): a
        /// ToggleKey latch macro replacing the momentary row.</summary>
        private bool EmitKeyToggleMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, byte vk, string keyName, bool onRelease, string inputName, int holdMs = 0,
            string triggerMode = null, bool pulse = false, int pulseIntervalMs = 100)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Toggle {keyName} ({inputName})",
                Action = TranslatedMacroAction.ToggleKey,
                TriggerMode = triggerMode ?? (holdMs > 0 ? "HoldForMs" : (onRelease ? "OnRelease" : "OnPress")),
                TriggerHoldMs = holdMs,
                ConsumeTrigger = false,
                VirtualKey = vk,
                PulseWhileLatched = pulse,
                IntervalMs = pulseIntervalMs,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean,
                TranslationReasons.ToggleLatchEmitted,
                path, binding.Raw, emitted: $"Toggle {keyName} latch macro", keyName);
            return true;
        }

        /// <summary>The activator toggle on a mouse_button binding (v18): a
        /// ToggleMouseButton latch macro replacing the momentary row, the
        /// ToggleKey pattern (the engine's mouse-button reconcile sends
        /// the down on latch and the up on unlatch / disable / stop).</summary>
        private bool EmitMouseToggleMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, int btn, string inputName, bool onRelease = false, int holdMs = 0,
            string triggerMode = null, bool pulse = false, int pulseIntervalMs = 100)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Toggle mouse {FirstToken(binding.Param).ToUpperInvariant()} ({inputName})",
                Action = TranslatedMacroAction.ToggleMouseButton,
                TriggerMode = triggerMode ?? (holdMs > 0 ? "HoldForMs" : (onRelease ? "OnRelease" : "OnPress")),
                TriggerHoldMs = holdMs,
                ConsumeTrigger = false,
                MouseButtonIndex = btn,
                PulseWhileLatched = pulse,
                IntervalMs = pulseIntervalMs,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.ToggleLatchEmitted,
                path, binding.Raw, emitted: $"Toggle mouse button {btn} latch macro",
                $"mouse {btn}");
            return true;
        }

        /// <summary>The activator toggle on a mouse_wheel binding (v18): a
        /// ToggleWheel latch macro replacing the momentary row. A held
        /// KbmScroll row scrolls continuously, so the latch reproduces
        /// exactly that: one detent per interval while latched.</summary>
        private bool EmitWheelToggleMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, (string Target, bool Invert) wheel, string inputName,
            int intervalMs, bool onRelease = false, int holdMs = 0, string triggerMode = null)
        {
            bool horizontal = wheel.Target == "KbmScrollH";
            var macro = new TranslatedMacro
            {
                Name = $"Toggle wheel ({inputName})",
                Action = TranslatedMacroAction.ToggleWheel,
                TriggerMode = triggerMode ?? (holdMs > 0 ? "HoldForMs" : (onRelease ? "OnRelease" : "OnPress")),
                TriggerHoldMs = holdMs,
                ConsumeTrigger = false,
                WheelHorizontal = horizontal,
                // Same tick signs as EmitWheelTapMacro.
                WheelTicks = horizontal ? (wheel.Invert ? -1 : 1) : (wheel.Invert ? 1 : -1),
                IntervalMs = intervalMs,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.ToggleLatchEmitted,
                path, binding.Raw, emitted: "Toggle wheel latch macro",
                FirstToken(binding.Param).ToUpperInvariant());
            return true;
        }

        /// <summary>hold_repeats on a mouse_wheel binding (v19, T1): a
        /// RepeatWheelWhileHeld turbo macro pulsing one discrete detent per
        /// authored repeat_rate while the physical input is held (the
        /// materializer lowers it to a MouseWheelTap riding
        /// RepeatMode=UntilRelease with the interval as the repeat gap).
        /// Replaces the continuous full-scale row, which ignored the
        /// authored cadence.</summary>
        private bool EmitWheelTurboMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, (string Target, bool Invert) wheel, string inputName, int intervalMs)
        {
            bool horizontal = wheel.Target == "KbmScrollH";
            var macro = new TranslatedMacro
            {
                Name = $"Turbo wheel ({inputName})",
                Action = TranslatedMacroAction.RepeatWheelWhileHeld,
                TriggerMode = "WhileHeld",
                ConsumeTrigger = false,
                WheelHorizontal = horizontal,
                // Same tick signs as EmitWheelTapMacro.
                WheelTicks = horizontal ? (wheel.Invert ? -1 : 1) : (wheel.Invert ? 1 : -1),
                IntervalMs = intervalMs,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw, emitted: $"Turbo wheel macro ({intervalMs} ms)");
            return true;
        }

        /// <summary>The activator toggle on an axis-natured VC target (v18):
        /// a ToggleVcAxis latch macro replacing the momentary output, with
        /// the toggle + hold_repeats composite on the latch pulse.</summary>
        private bool EmitVcAxisToggleMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, XInputTargetTable.XInputTarget xt, string inputName,
            int holdMs = 0, string triggerMode = null, bool pulse = false, int pulseIntervalMs = 100)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Toggle {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.ToggleVcAxis,
                TriggerMode = triggerMode ?? (holdMs > 0 ? "HoldForMs" : "OnPress"),
                TriggerHoldMs = holdMs,
                ConsumeTrigger = false,
                TargetAxis = xt.Target,
                TargetAxisNegative = xt.StickAxisNegative,
                PulseWhileLatched = pulse,
                IntervalMs = pulseIntervalMs,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.ToggleLatchEmitted,
                path, binding.Raw, emitted: $"Toggle {xt.Target} axis latch macro", xt.Target);
            return true;
        }

        /// <summary>hold_repeats on an axis-natured VC target (v18): a
        /// RepeatVcAxisWhileHeld turbo macro pulsing the axis assert at
        /// repeat_rate while the physical input is held (from the
        /// Long_Press threshold when <paramref name="holdMs"/> is set).</summary>
        private bool EmitVcAxisTurboMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, XInputTargetTable.XInputTarget xt, int intervalMs, int holdMs,
            string inputName, string triggerMode = null)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Turbo {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.RepeatVcAxisWhileHeld,
                TriggerMode = triggerMode ?? (holdMs > 0 ? "HoldForMs" : "WhileHeld"),
                TriggerHoldMs = holdMs,
                ConsumeTrigger = holdMs > 0,
                TargetAxis = xt.Target,
                TargetAxisNegative = xt.StickAxisNegative,
                IntervalMs = intervalMs,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw, emitted: $"Turbo {xt.Target} axis macro ({intervalMs} ms)");
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
            string inputName, string triggerMode = null)
        {
            var macro = new TranslatedMacro
            {
                Name = $"Turbo {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.RepeatVcButtonWhileHeld,
                TriggerMode = triggerMode ?? (holdMs > 0 ? "HoldForMs" : "WhileHeld"),
                TriggerHoldMs = holdMs,
                TargetXboxButtons = xt.XboxButtonBit,
                ConsumeTrigger = holdMs > 0,
                IntervalMs = intervalMs,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw, emitted: $"Turbo {xt.Target} macro ({intervalMs} ms)");
            return true;
        }

        /// <summary>A plain Long_Press xinput binding (wave 2A): the target
        /// button engages at the hold threshold and stays down until the
        /// physical input releases, Valve's documented Long_Press shape.
        /// Consumes its trigger bits while active (the interruptable-pause
        /// approximation, same as the turbo variant).</summary>
        private bool EmitVcHoldMacro(Run run, SteamInputBinding binding, ResolvedSource source,
            string path, XInputTargetTable.XInputTarget xt, int holdMs, string inputName,
            string triggerMode = "HoldForMs")
        {
            bool dbl = triggerMode == "DoublePress";
            var macro = new TranslatedMacro
            {
                Name = $"{(dbl ? "Double press" : "Long press")} {xt.Target} ({inputName})",
                Action = TranslatedMacroAction.HoldVcButton,
                TriggerMode = triggerMode,
                TriggerHoldMs = holdMs,
                TargetXboxButtons = xt.XboxButtonBit,
                // Consuming approximates Steam's interruptable pause of
                // same-input activators once the long / double press fires
                // ("Any interruptable activators on the same button will
                // not fire if a double press is fired", shipped string).
                ConsumeTrigger = true,
            };
            FillMacroTrigger(macro, source);
            run.AddMacro(macro);
            run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                path, binding.Raw,
                emitted: $"{(dbl ? "Double-press" : "Long-press")} hold macro: {xt.Target}");
            return true;
        }

        private void TranslateModeShift(Run run, SteamInputPreset preset, SteamInputBinding binding,
            ResolvedSource source, string path, int activatorDelayMs = 0, bool toggle = false,
            bool oneShotHost = false, int doublePressMs = 0)
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

            if (!IsActivatorCapable(source, allowGyroHalf: oneShotHost))
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.ActivatorInputNotSupported,
                    path, binding.Raw);
                return;
            }

            string layerMask = ModeShiftLayer(run, preset.Id, slotToken, groupId);
            var req = new ActivatorRequest
            {
                LayerMask = layerMask,
                LayerName = $"{slotToken} shift",
                // The activator toggle setting latches the shift instead of
                // holding it (wave 2A); the engine's Toggle mode is the
                // same construct. A one-shot flick host (v15) has no held
                // state, so it always latches.
                Mode = toggle || oneShotHost ? "Toggle" : "Hold",
                InheritUnmapped = true, // mode shift overlays the slot; everything else keeps working
                DelayMs = activatorDelayMs,
                DoublePressMs = doublePressMs,
                Path = path,
            };
            FillActivatorInput(req, source);
            run.Activators.Add(req);
        }

        /// <summary><paramref name="triggerModeOverride"/> (v17) replaces
        /// the derived press/release/hold trigger on the MACRO-shaped
        /// one-shot verbs (cursor warp, set_led, camera_reset, nudge,
        /// screenshot, on-screen keyboard) so a Double_Press activator can
        /// host them; the layer / preset arms never see it (their callers
        /// route those to the activator safety net first).</summary>
        private void TranslateControllerAction(Run run, SteamInputPreset preset,
            SteamInputBinding binding, ResolvedSource source, string layer, string path,
            bool onRelease, string inputName, int activatorDelayMs = 0, bool toggle = false,
            bool oneShotHost = false, string triggerModeOverride = null, int doublePressMs = 0)
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
                        // Long_Press warps fire at the hold threshold, the
                        // SET_LED / camera_reset shape (v24).
                        TriggerMode = triggerModeOverride ?? (activatorDelayMs > 0 ? "HoldForMs"
                            : onRelease ? "OnRelease" : "OnPress"),
                        TriggerHoldMs = activatorDelayMs,
                        ConsumeTrigger = false,
                        NormalizedX = Math.Clamp(nx, 0, 65535),
                        NormalizedY = Math.Clamp(ny, 0, 65535),
                    };
                    FillMacroTrigger(warp, source);
                    run.AddMacro(warp);
                    run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                        path, binding.Raw, emitted: "Cursor warp macro");
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
                        // v10 G8: the corpus shape is a "back" binding
                        // hosted INSIDE the layer it removes. That lowers
                        // to a single-stop Cycle through the hosting layer
                        // with Base in the ring (the same press-to-step
                        // return construct the v9 unmerged-jump lowering
                        // uses), replacing the old no-op. A remove that
                        // targets a DIFFERENT layer, or one hosted in
                        // Base, still has no construct and keeps the
                        // note-only Partial.
                        bool hosted = TryResolvePresetIndex(run, presetIndex, out int removeId)
                            && !string.IsNullOrEmpty(layer) && layer != "Base"
                            && layer == $"Layer_{run.Options.FileId}_{removeId}"
                            && IsActivatorCapable(source, allowGyroHalf: oneShotHost);
                        if (hosted)
                        {
                            var removeReq = new ActivatorRequest
                            {
                                LayerMask = layer,
                                LayerName = PresetLayerName(run, removeId),
                                Mode = "Cycle",
                                InheritUnmapped = true, // leaving an overlay layer
                                DelayMs = activatorDelayMs,
                                DoublePressMs = doublePressMs,
                                CycleLayers = layer,
                                CycleIncludeBase = true,
                                Path = path,
                            };
                            FillActivatorInput(removeReq, source);
                            run.Activators.Add(removeReq);
                        }
                        // Partial either way: the Cycle is its own stepper
                        // beside whatever engaged the layer, so a press
                        // can need one extra step before it lands on Base.
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.RemoveLayerApproximated,
                            path, binding.Raw);
                        // v19 (T6): a release-hosted remove steps on the
                        // press edge instead (the Cycle activator has no
                        // release-edge trigger); name the shifted edge.
                        // Only when the Cycle actually lowered: the
                        // unhosted arm emits nothing, so the note-only
                        // Partial above already covers it whole.
                        if (onRelease && hosted)
                        {
                            run.Report.Add(TranslationStatus.Partial,
                                TranslationReasons.LayerReleaseEdgeApproximated,
                                path, binding.Raw, args: "REMOVE_LAYER");
                        }
                        return;
                    }
                    if (!TryResolvePresetIndex(run, presetIndex, out int presetId))
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.MissingPreset,
                            path, binding.Raw, args: presetIndex.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                    if (!IsActivatorCapable(source, allowGyroHalf: oneShotHost))
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.ActivatorInputNotSupported,
                            path, binding.Raw);
                        return;
                    }
                    var layerReq = new ActivatorRequest
                    {
                        LayerMask = $"Layer_{run.Options.FileId}_{presetId}",
                        LayerName = PresetLayerName(run, presetId),
                        // add_layer latches by nature; hold_layer holds
                        // unless the activator's toggle setting latches it
                        // (wave 2A) or the host is a one-shot flick (v15:
                        // nothing is held on a flick, so it latches).
                        Mode = action.Equals("HOLD_LAYER", StringComparison.OrdinalIgnoreCase)
                            && !toggle && !oneShotHost
                            ? "Hold" : "Toggle",
                        InheritUnmapped = true, // Steam action layers overlay the set below
                        DelayMs = activatorDelayMs,
                        DoublePressMs = doublePressMs,
                        Path = path,
                    };
                    FillActivatorInput(layerReq, source);
                    run.Activators.Add(layerReq);
                    // v19 (T6): a release-hosted add_layer / hold_layer
                    // engages on the press edge instead. Every
                    // ShiftActivator mode keys on the press edge (Hold
                    // while held, Toggle / Cycle / Custom on press), so a
                    // release edge is inexpressible; the layer change
                    // still lowers, one edge early, under a named Partial
                    // instead of a silent Clean.
                    if (onRelease)
                    {
                        run.Report.Add(TranslationStatus.Partial,
                            TranslationReasons.LayerReleaseEdgeApproximated,
                            path, binding.Raw, args: action.ToUpperInvariant());
                    }
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
                    // Sentinel set-cycle commands, not preset references
                    // (v20). Steam's own vocabulary binds 32766 as "change
                    // to next action set" and 32765 as "change to previous
                    // action set" (community .vdf grammar guide, file
                    // 932405100). They lower to the engine's Cycle
                    // activator over every action set in authored order,
                    // so MissingPreset stays reserved for genuinely
                    // dangling numeric references.
                    if (presetIndex == ChangePresetNextSentinel
                        || presetIndex == ChangePresetPreviousSentinel)
                    {
                        TranslateSentinelPresetCycle(run,
                            previous: presetIndex == ChangePresetPreviousSentinel,
                            binding, source, layer, path, onRelease, activatorDelayMs, oneShotHost,
                            doublePressMs);
                        return;
                    }
                    if (!TryResolvePresetIndex(run, presetIndex, out int presetId))
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.MissingPreset,
                            path, binding.Raw, args: presetIndex.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                    if (!IsActivatorCapable(source, allowGyroHalf: oneShotHost))
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.ActivatorInputNotSupported,
                            path, binding.Raw);
                        return;
                    }
                    bool toBase = run.BasePresetId.HasValue && presetId == run.BasePresetId.Value;
                    // The Jump_* mask is a merge placeholder only: paired
                    // same-input jumps become one Cycle, and any leftover is
                    // re-lowered by LowerUnmergedJumps (the runtime's Custom
                    // mode latches LayerMask itself and ignores JumpToLayer,
                    // so a persisted Jump_* mask would engage a rowless layer).
                    var jumpReq = new ActivatorRequest
                    {
                        LayerMask = $"Jump_{run.Options.FileId}_{presetId}",
                        LayerName = PresetLayerName(run, presetId),
                        Mode = "Custom",
                        JumpToLayer = toBase ? "Base" : $"Layer_{run.Options.FileId}_{presetId}",
                        InheritUnmapped = false, // action sets replace
                        // A Long_Press CHANGE_PRESET rides the activator's
                        // hold-before-fire debounce (#206 honors DelayMs on
                        // the Custom / Cycle edge modes too), v10 G10. A
                        // Double_Press one rides the v25 double-press gate.
                        DelayMs = activatorDelayMs,
                        DoublePressMs = doublePressMs,
                        HostLayer = layer,
                        Path = path,
                    };
                    FillActivatorInput(jumpReq, source);
                    run.Activators.Add(jumpReq);
                    // v19 (T6): a release-hosted preset jump fires on the
                    // press edge instead (Custom / Cycle activators key on
                    // the press edge only); named Partial, not silent Clean.
                    if (onRelease)
                    {
                        run.Report.Add(TranslationStatus.Partial,
                            TranslationReasons.LayerReleaseEdgeApproximated,
                            path, binding.Raw, args: "CHANGE_PRESET");
                    }
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
                        // Long_Press set_led fires at the hold threshold,
                        // same shape as camera_reset (v10 G10).
                        TriggerMode = triggerModeOverride ?? (activatorDelayMs > 0 ? "HoldForMs"
                            : onRelease ? "OnRelease" : "OnPress"),
                        TriggerHoldMs = activatorDelayMs,
                        ConsumeTrigger = false,
                        LedR = Math.Clamp(r, 0, 255),
                        LedG = Math.Clamp(g, 0, 255),
                        LedB = Math.Clamp(b, 0, 255),
                        LedBrightnessPercent = Math.Clamp(bright, 0, 100),
                        LedSaturationPercent = Math.Clamp(satPct, 0, 100),
                        LedSetting = ledSetting,
                    };
                    FillMacroTrigger(led, source);
                    run.AddMacro(led);
                    // ledSetting 2 ("restore default lighting") lowers to
                    // the materializer clearing the override. Silent since
                    // v17, the SCREENSHOT ruling: clearing the override IS
                    // restoring the default, so the note described exactly
                    // what a user expects and was noise.
                    run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                        path, binding.Raw, emitted: "Set LED macro");
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
                        TriggerMode = triggerModeOverride ?? (activatorDelayMs > 0 ? "HoldForMs"
                            : onRelease ? "OnRelease" : "OnPress"),
                        TriggerHoldMs = activatorDelayMs,
                        ConsumeTrigger = false,
                    };
                    // The action itself approximates (gyro recenter for
                    // Steam's calibrated camera surgery), so the entry
                    // stays Partial whatever the trigger shape.
                    FillMacroTrigger(macro, source);
                    run.AddMacro(macro);
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.CameraResetApproximated,
                        path, binding.Raw, emitted: "Gyro recenter macro");
                    return;
                }

                case "CHANGE_PLAYER_NUMBER":
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.PlayerNumberActionNotSupported,
                        path, binding.Raw);
                    return;

                case "TOGGLE_LIZARD_MODE":
                case "TOGGLE_LIZARD": // the serializer's own spelling (steamclient.dll token table)
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.LizardModeActionNotSupported,
                        path, binding.Raw);
                    return;

                case "MOUSE_DELTA":
                {
                    // "Move by Amount" (shipped configurator: "Each time
                    // this command fires the mouse will move by a set
                    // number of pixels", args dx dy, and corpus 3456927474
                    // carries "mouse_delta 100 0"). One fire = one nudge
                    // (v16): the MouseNudge macro enqueues the signed pixel
                    // delta once into the engine's accumulate-and-flush
                    // mouse lane, screen frame (+x right, +y down), the
                    // same coordinate space SendInput MOUSEEVENTF_MOVE
                    // consumes, so the authored values pass through
                    // unscaled. Long_Press fires at the hold threshold and
                    // release activators on release, the SET_LED shape.
                    if (tokens.Length < 3
                        || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ndx)
                        || !int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ndy))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnsupportedControllerAction,
                            path, binding.Raw, args: binding.Param);
                        return;
                    }
                    var nudge = new TranslatedMacro
                    {
                        Name = $"Nudge cursor ({inputName})",
                        Action = TranslatedMacroAction.MouseNudge,
                        TriggerMode = triggerModeOverride ?? (activatorDelayMs > 0 ? "HoldForMs"
                            : onRelease ? "OnRelease" : "OnPress"),
                        TriggerHoldMs = activatorDelayMs,
                        ConsumeTrigger = false,
                        DeltaX = ndx,
                        DeltaY = ndy,
                    };
                    // Descriptor trigger on purpose, the SCREENSHOT rule:
                    // a cursor verb on a pure keyboard config must not
                    // sprout an Xbox slot.
                    FillMacroTrigger(nudge, WithoutOutputTrigger(source));
                    run.AddMacro(nudge);
                    run.Report.Add(TranslationStatus.Clean, TranslationReasons.MacroEmitted,
                        path, binding.Raw, emitted: "Cursor nudge macro");
                    return;
                }

                case "SCREENSHOT":
                case "SYSTEM_KEY_1":
                {
                    // v10 G7: Steam's overlay screenshot has no client here;
                    // the nearest verb is a PrintScreen tap (VK_SNAPSHOT via
                    // SendInput), which most capture tools bind. Silent since
                    // v17: the note described exactly what a user expects
                    // the action to do, so it was noise.
                    // system_key_1 rides the same lowering (v20). Every
                    // occurrence in the corpus and in Valve's shipped
                    // controller_base configs binds it to button_capture
                    // Release: authors restoring the Capture button's
                    // native Steam behavior, which is taking a screenshot.
                    // system_key_0 stays on the SteamSystemAction note via
                    // the SteamClientActions set below.
                    var shot = new TranslatedMacro
                    {
                        Name = $"Screenshot key ({inputName})",
                        Action = TranslatedMacroAction.KeyTap,
                        // Long_Press taps at the hold threshold (v24).
                        TriggerMode = triggerModeOverride ?? (activatorDelayMs > 0 ? "HoldForMs"
                            : onRelease ? "OnRelease" : "OnPress"),
                        TriggerHoldMs = activatorDelayMs,
                        ConsumeTrigger = false,
                        VirtualKey = 0x2C, // VK_SNAPSHOT
                    };
                    // Descriptor trigger on purpose: a combined-output
                    // trigger would demand an Xbox slot, and a system
                    // action on a pure keyboard config must not sprout one.
                    FillMacroTrigger(shot, WithoutOutputTrigger(source));
                    run.AddMacro(shot);
                    run.SilentMacroEmissions++;
                    return;
                }

                case "SHOW_KEYBOARD":
                {
                    // v10 G7: Steam's overlay keyboard has no client here;
                    // launch the Windows on-screen keyboard instead (the
                    // materializer resolves TabTip.exe, falling back to
                    // osk.exe). Silent since v17, the SCREENSHOT ruling.
                    var osk = new TranslatedMacro
                    {
                        Name = $"On-screen keyboard ({inputName})",
                        Action = TranslatedMacroAction.ShowOnScreenKeyboard,
                        // Long_Press launches at the hold threshold (v24).
                        TriggerMode = triggerModeOverride ?? (activatorDelayMs > 0 ? "HoldForMs"
                            : onRelease ? "OnRelease" : "OnPress"),
                        TriggerHoldMs = activatorDelayMs,
                        ConsumeTrigger = false,
                    };
                    // Descriptor trigger for the same no-phantom-Xbox-slot
                    // reason as SCREENSHOT above.
                    FillMacroTrigger(osk, WithoutOutputTrigger(source));
                    run.AddMacro(osk);
                    run.SilentMacroEmissions++;
                    return;
                }

                case "EMPTY_SUB_COMMAND":
                case "EMPTY_BINDING": // same placeholder, later vintage (v10 G15)
                    return; // placeholder, silent

                default:
                    // Steam-client system verbs (v13) get the named
                    // SteamSystemAction entry (system_key_0 and the
                    // SteamClientActions families land here). The generic
                    // unknown below stays only for verbs outside Steam's
                    // own serializer vocabulary.
                    if (IsSteamClientAction(action))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.SteamSystemAction,
                            path, binding.Raw, args: action);
                        return;
                    }
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.UnsupportedControllerAction,
                        path, binding.Raw, args: action);
                    return;
            }
        }

        /// <summary>The Steam-client system verbs of the controller_action
        /// grammar (v13): actions that drive the Steam client, the host
        /// machine, or a Steam-side subsystem PadForge has no client for.
        /// Vocabulary harvested from the serializer's own token table
        /// (steamclient.dll: toggle_magnifier ... sr_prev_heading) plus
        /// Valve's shipped controller_base configs (chord / desktop /
        /// basicui carry TOGGLE_MAGNIFIER, BRIGHTNESS_UP/DOWN,
        /// CONTROLLER_POWEROFF, QUIT_APPLICATION and the SR_/GR_/TS_
        /// families). Prefix families cover the enumerated members and any
        /// sibling Valve adds later: SR_ = screen reader, GR_ = game
        /// recording, TS_ = touchscreen verbs, STEAMMUSIC_ = the Steam
        /// Music player, BIGPICTURE_ = Big Picture, HOST_ = power
        /// controls, CHORD_HINT_ = chord hint overlay.</summary>
        private static readonly HashSet<string> SteamClientActions = new(StringComparer.Ordinal)
        {
            "BRIGHTNESS_UP", "BRIGHTNESS_DOWN",
            "CONTROLLER_POWEROFF",
            "QUIT_APPLICATION",
            "TOGGLE_MAGNIFIER", "TOGGLE_RUMBLE", "TOGGLE_HAPTICS", "TOGGLE_HUD",
            "OPEN_CONFIGURATOR", "OPEN_QUICKMENU",
            "FORCE_GUIDE_UP",
            "SYSTEM_KEY_0",
            "DOTS_PER_360_CALIBRATION_SPIN",
            "TURN_TO_FACE_DIRECTION",
        };

        private static bool IsSteamClientAction(string action)
        {
            string a = (action ?? "").ToUpperInvariant();
            return SteamClientActions.Contains(a)
                || a.StartsWith("SR_", StringComparison.Ordinal)
                || a.StartsWith("GR_", StringComparison.Ordinal)
                || a.StartsWith("TS_", StringComparison.Ordinal)
                || a.StartsWith("STEAMMUSIC_", StringComparison.Ordinal)
                || a.StartsWith("BIGPICTURE_", StringComparison.Ordinal)
                || a.StartsWith("HOST_", StringComparison.Ordinal)
                || a.StartsWith("CHORD_HINT_", StringComparison.Ordinal);
        }

        private static string PresetLayerName(Run run, int presetId)
        {
            if (run.PresetNames.TryGetValue(presetId, out var n)) return n;
            var preset = run.Config.Presets.FirstOrDefault(p => p.Id == presetId);
            return preset != null ? PresetDisplayName(run, preset) : $"Preset {presetId}";
        }

        /// <summary>The author-facing set name (v13 section census). A
        /// preset's <c>name</c> is a set token (<c>Preset_1000001</c>,
        /// <c>MenuControls</c>). The actions / action_layers block carries
        /// the display title behind it, often a <c>#token</c> into the
        /// config's localization (Valve's TF2 config titles its sets
        /// <c>#MenuControls</c>-style, community layers carry plain titles
        /// such as "Secondary"). Falls back to the raw token.</summary>
        private static string PresetDisplayName(Run run, SteamInputPreset preset)
        {
            string raw = string.IsNullOrWhiteSpace(preset.Name)
                ? $"Preset {preset.Id}" : preset.Name;
            if (!string.IsNullOrWhiteSpace(preset.Name)
                && run.Config.ActionSetTitles.TryGetValue(preset.Name.Trim(), out var title))
            {
                return ResolveText(run, title, preset.Name.Trim(), raw);
            }
            return raw;
        }

        /// <summary>Steam's CHANGE_PRESET sentinel ids (v20). These are
        /// commands, not preset references: 32766 steps to the NEXT action
        /// set, 32765 to the PREVIOUS one. Grounding: the community .vdf
        /// grammar guide (Steam file 932405100) documents both forms, and
        /// corpus fixture 3353604014 carries the next form once. Valve's
        /// 54 shipped controller_base templates author only ordinary
        /// indices (1 and 2), so nothing else in the 32000+ range exists
        /// in any censused source.</summary>
        private const int ChangePresetNextSentinel = 32766;
        private const int ChangePresetPreviousSentinel = 32765;

        /// <summary>Lowers a sentinel CHANGE_PRESET to one Cycle activator
        /// whose ring is every selected action set in authored order (the
        /// preset walk's ascending-id order). The non-Base sets ride
        /// CycleLayers and the Base set rides CycleIncludeBase, the same
        /// ring shape MergeSameInputJumpsIntoCycles builds for same-input
        /// jump pairs. Previous walks the same ring in reverse: from the
        /// resting Base stop the first step lands on the LAST set, which
        /// is exactly Steam's previous-set wrap. A single-set config still
        /// gets its activator with an empty queue: the ring is Base alone,
        /// the runtime never steps an empty queue, and that matches a
        /// next-set press when there is only one set to land on.</summary>
        private static void TranslateSentinelPresetCycle(Run run, bool previous,
            SteamInputBinding binding, ResolvedSource source, string layer, string path,
            bool onRelease, int activatorDelayMs, bool oneShotHost, int doublePressMs = 0)
        {
            if (!IsActivatorCapable(source, allowGyroHalf: oneShotHost))
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.ActivatorInputNotSupported,
                    path, binding.Raw);
                return;
            }
            var stops = new List<string>();
            var names = new List<string>();
            foreach (var setPreset in SelectPresets(run.Config, run.Options))
            {
                names.Add(PresetLayerName(run, setPreset.Id));
                bool isBaseSet = run.BasePresetId.HasValue && setPreset.Id == run.BasePresetId.Value;
                if (!isBaseSet)
                    stops.Add($"Layer_{run.Options.FileId}_{setPreset.Id}");
            }
            if (previous)
            {
                stops.Reverse();
                names.Reverse();
            }
            var cycleReq = new ActivatorRequest
            {
                LayerMask = stops.Count > 0 ? stops[0] : "",
                LayerName = string.Join(" / ", names
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.Ordinal)),
                Mode = "Cycle",
                CycleLayers = string.Join("|", stops),
                CycleIncludeBase = true,
                InheritUnmapped = false, // action sets replace
                // A Long_Press sentinel rides the activator's
                // hold-before-fire debounce like the jump path (v10 G10);
                // a Double_Press one rides the v25 double-press gate.
                DelayMs = activatorDelayMs,
                DoublePressMs = doublePressMs,
                HostLayer = layer,
                Path = path,
            };
            FillActivatorInput(cycleReq, source);
            run.Activators.Add(cycleReq);
            // A release-hosted set cycle steps on the press edge instead
            // (Cycle activators key on the press edge only), same named
            // Partial as the jump path (v19 T6).
            if (onRelease)
            {
                run.Report.Add(TranslationStatus.Partial,
                    TranslationReasons.LayerReleaseEdgeApproximated,
                    path, binding.Raw, args: "CHANGE_PRESET");
            }
        }

        /// <summary>CHANGE_PRESET / add_layer / hold_layer reference presets
        /// by 1-BASED INDEX in id order, not by preset id. Corpus ground
        /// truth: 708227783 carries CHANGE_PRESET 1 and 2 over presets
        /// {0, 1}, and 3451446931 carries hold_layer 2 over {0, 1}; the
        /// index reading resolves every in-corpus reference, the id reading
        /// leaves danglers in three fixtures. Sentinel set-cycle ids never
        /// reach this resolver (v20): they are intercepted as commands.</summary>
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
        /// with a threshold). Gesture-FIRED trackpad reads (taps, swipes,
        /// the anchor D-pad wedges) can't drive one. The touch-spot
        /// family can, in both of its resolver shapes (v23, the lockdown
        /// re-audit): a single-pad half click (#9 B-1) is a
        /// plain pad-click button AND-gated on the half's touch spot,
        /// exactly the runtime's Kind=Chord read, and a bare half-touch
        /// host ("Touchpad {p} TouchLeft/TouchRight", the single-pad
        /// "touch" member) is itself a held-state bool: the recognizer
        /// adds the spot key at contact and removes it at lift
        /// (TouchpadGestureContext.CurrentTouchSpot), and the button-like
        /// evaluator reads it through TouchpadGestureFiredProvider, the
        /// same read a chord leg gets. The touch-spots feature self-arms
        /// at apply since v14 for both shapes (TouchpadGestureAutoArm
        /// classifies act.Descriptor and act.ChordSecondDescriptor
        /// alike).
        /// <paramref name="allowGyroHalf"/> (v15): a one-shot flick host
        /// admits a HALF-stamped gyro rate read, because the request rides
        /// Kind=Axis with the half selector and an edge-fired mode
        /// (Toggle / Latch / Cycle), so the rate's instant return to zero
        /// never flickers a held layer. Hold-natured gyro activators stay
        /// out.</summary>
        /// <summary>Pulse-shaped gesture hosts (v26): one-shot fires
        /// (taps, swipes) with no held state, so they carry an activator
        /// only through the latching arms (the flick-host rule: callers
        /// pass oneShotHost and the mode becomes Toggle / the press-edge
        /// jump). The held-state features (touch spots, the anchor D-pad
        /// wedges) are NOT pulses and host every mode.</summary>
        private static bool IsGesturePulseHost(ResolvedSource source)
            => source != null
            && (source.TrackpadFeature == PhysicalSlotResolver.FeatureSwipes
                || source.TrackpadFeature == PhysicalSlotResolver.FeatureTaps);

        private static bool IsActivatorCapable(ResolvedSource source, bool allowGyroHalf = false)
            => source != null
            // Held-state gesture reads (touch spots since v25, the anchor
            // D-pad wedges since v26: the v14 auto-arm reference scan
            // covers activator legs, so both self-arm at apply) carry
            // every mode; pulse gestures (taps, swipes) host only the
            // latching arms, gated by the caller's one-shot flag exactly
            // like the signed gyro-rate halves.
            && (string.IsNullOrEmpty(source.TrackpadFeature)
                || source.TrackpadFeature == PhysicalSlotResolver.FeatureTouchSpots
                || source.TrackpadFeature == PhysicalSlotResolver.FeatureJoystickOutput
                || (allowGyroHalf && IsGesturePulseHost(source)))
            // The gravity-lean wedges (v26) are sustained-tilt HELD state,
            // so they host every mode; the signed RATE halves stay
            // one-shot-only.
            && (!source.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal)
                || PadForge.Engine.Common.Mapping.SourceCoercion.IsGyroLeanDescriptor(source.Descriptor)
                || (allowGyroHalf && source.HalfAxis));
            // The v24 gated-half-axis carve-out retired in v26:
            // ShiftActivator.GateDescriptor carries the gate on the
            // Kind=Axis path now (ReadActivatorInput evaluates it like
            // the chord second leg), so a gated stick / trackpad wedge
            // is a full activator host.

        /// <summary>The engine's gyro-as-button rate threshold as a
        /// normalized Kind=Axis threshold (v15). Numerator: SourceCoercion.
        /// GyroButtonThreshold (30 deg/s, "a deliberate twist, not idle
        /// hand tremor"). Denominator: the full-scale rate behind the
        /// engine's normalized gyro axis read (SourceCoercion.GyroScale =
        /// one over 500 deg/s in rad), which is what the activator's
        /// Kind=Axis bipolar read returns. A gyro-hosted activator
        /// therefore fires exactly where a gyro-as-button mapping row
        /// fires.</summary>
        private const double GyroActivatorThreshold01 = 30.0 / 500.0;

        /// <summary>Stamps an activator request's input from the hosting
        /// source (v15). Directional analog hosts (stick wedges, gyro rate
        /// halves) carry HalfAxis: the button-like activator read has no
        /// half selector (it thresholds the raw upper half, so a north
        /// wedge would engage on the SOUTH deflection), so these ride
        /// Kind=Axis with the half stamp and the source's own threshold.
        /// Trigger pulls deliberately KEEP the Button kind: the
        /// button-like read thresholds the raw axis at 50% of full range
        /// (a half pull on a unipolar trigger), while the Axis kind's
        /// bipolar read rests at -1 and would engage permanently.
        /// Everything else keeps the Button / Chord pair.</summary>
        private static void FillActivatorInput(ActivatorRequest req, ResolvedSource source)
        {
            req.Descriptor = source.Descriptor;
            req.GateDescriptor = source.GateDescriptor ?? "";
            if (source.HalfAxis && !source.IsAnalogTriggerPull)
            {
                req.Kind = "Axis";
                req.AxisHalf = true;
                req.AxisInvert = source.Invert;
                // The gravity-lean pair (v26) is a POSITION on the
                // normalized 90-degree scale, so its threshold is the
                // wedge's own DeadZone percent like any axis; only the
                // RATE family takes the deg/s-derived constant.
                req.AxisThreshold =
                    source.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal)
                    && !PadForge.Engine.Common.Mapping.SourceCoercion.IsGyroLeanDescriptor(source.Descriptor)
                    ? GyroActivatorThreshold01
                    : Math.Max(source.DeadZone > 0 ? source.DeadZone : 50, 1) / 100.0;
            }
            else
            {
                req.Kind = string.IsNullOrEmpty(source.GateDescriptor) ? "Button" : "Chord";
            }
        }

        // ─────────────────────────────────────────────
        //  Row accumulation
        // ─────────────────────────────────────────────

        // Gesture-gated sources (TrackpadFeature != null) report Clean
        // since v14: the imported set is Authoritative and the engine
        // self-arms every referenced gesture family at apply. Soft_Press
        // rows report Clean since v17: "approximated as a press
        // threshold" described exactly what a soft press is, so the note
        // was noise. The threshold lowering stays in BuildSource.
        private static TranslationStatus StatusFor(ResolvedSource source)
            => source.PartialReasonKey != null ? TranslationStatus.Partial
             : TranslationStatus.Clean;

        private static string ReasonFor(ResolvedSource source)
            => source.PartialReasonKey ?? TranslationReasons.RowEmitted;

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
            // The AND companion rides the source itself since v18
            // (MappingSource.GateDescriptor, evaluated like the chord
            // second leg), so multi-source rows keep every gate. The
            // second companion (v26) carries a chord partner beside a
            // spent primary gate.
            if (!string.IsNullOrEmpty(source.GateDescriptor))
                src.GateDescriptor = source.GateDescriptor;
            if (!string.IsNullOrEmpty(source.Gate2Descriptor))
                src.Gate2Descriptor = source.Gate2Descriptor;
            return src;
        }

        private void EmitSourceRow(Run run, bool isKbm, string layer, string target,
            ResolvedSource source, string clickGate, bool isAxis, bool soft,
            string path, string binding)
        {
            var src = BuildSource(source, soft);
            // The group-level requires_click gate (trackpad D-pad wedges)
            // and the source's own GateDescriptor (a single-pad click gated
            // on its half's touch spot, #9 B-1) never co-occur: wedges
            // carry no GateDescriptor and gated clicks are never wedge
            // members.
            if (!string.IsNullOrEmpty(clickGate)) src.GateDescriptor = clickGate;
            AddRowSource(run, isKbm, layer, target, src, isAxis,
                StatusFor(source), ReasonFor(source), path, binding);
        }

        private void AddRowSource(Run run, bool isKbm, string layer, string target,
            MappingSource src, bool isAxis, TranslationStatus status, string reason,
            string path, string binding = "", string args = null)
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
                && ((binding.Param ?? "").StartsWith("empty_sub_command", StringComparison.OrdinalIgnoreCase)
                    || (binding.Param ?? "").StartsWith("empty_binding", StringComparison.OrdinalIgnoreCase)))
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
                // bit, so there are no bits to consume. The swap itself is
                // silent since v15: the macro fires exactly as the config
                // asks, so there is nothing for the user to do or feel
                // differently and a note would narrate plumbing.
                m.ConsumeTrigger = false;
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
                    // Curve/range channel (v11): the matched side of a
                    // stick-hosted joystick_move carries the group's cluster
                    // exactly like the crossed rows do.
                    if (ma.CurveExponent > 0) src.ParamCurveExponent = ma.CurveExponent;
                    if (ma.RangeOuter > 0) src.ParamRangeOuter = ma.RangeOuter;
                    if (ma.Sensitivity > 0 && ma.Sensitivity != 1.0) src.Sensitivity = ma.Sensitivity;
                    if (ma.Anti > 0) src.ParamAntiDeadzone = ma.Anti;
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
                row.Sources.AddRange(pending.Sources);
                if (pending.Sources.Count > 1 && pending.IsAxis && !pending.HasMatchedPassthrough)
                    row.CombineMode = "Sum"; // mouse deltas and merged axes are additive
                // Rows carrying a matched analog passthrough keep the
                // axis default (max-abs), so extra legs (a click
                // identity, a bumper-as-trigger binding) ride on top of
                // a clean analog pull instead of summing into overdrive.
                // Multi-source buttons keep the engine's OR default.
                // Click gates ride each source's GateDescriptor (v18).

                (key.Kbm ? profile.KbmMappingSet : profile.XboxMappingSet).Rows.Add(row);
            }

            EmitActivators(run);
            ReportActivatorlessPresets(run);

            // Rows are final, so a combined-output macro trigger can now be
            // checked against what actually feeds it. Must run before the
            // counts and NeedsXboxSlot below: rewriting a trigger off the Xbox
            // output can be what decides a macro no longer needs an Xbox slot.
            FinalizeMacroTriggers(run);

            // Haptics carry no note since v22: activator-level intensities
            // became RumblePulse macros in v10 (G1) and the group-level
            // intensity now rides every member activation through the
            // EmitHapticPulse fallback, so the old per-config aggregate
            // (HapticIntensityDropped) has nothing left to count. A group
            // with no member activators has no activation to tick on (its
            // continuous surface-motion tick has no channel) and lowers
            // silently, the same policy as every other consumed setting.

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
            || m.Action == TranslatedMacroAction.HoldVcButton
            || m.Action == TranslatedMacroAction.VcButtonTap
            || m.Action == TranslatedMacroAction.VcAxisTap
            || m.Action == TranslatedMacroAction.HoldVcAxis
            // A wheel-list cycle needs the Xbox slot only when a step
            // writes a virtual-controller target (v16).
            || (m.Action == TranslatedMacroAction.CycleList
                && m.CycleSteps.Any(s => s.Kind == TranslatedCycleStepKind.VcButtonTap
                    || s.Kind == TranslatedCycleStepKind.VcAxisTap));

        private void EmitActivators(Run run)
        {
            MergeSameInputJumpsIntoCycles(run);
            LowerUnmergedJumps(run);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var req in run.Activators
                .OrderBy(a => a.LayerMask, StringComparer.Ordinal)
                .ThenBy(a => a.Descriptor, StringComparer.Ordinal)
                .ThenBy(a => a.Mode, StringComparer.Ordinal))
            {
                if (!seen.Add($"{req.LayerMask}|{req.Descriptor}|{req.GateDescriptor}|{req.Mode}|{req.JumpToLayer}|{req.CycleLayers}|{(req.AxisHalf ? 1 : 0)}{(req.AxisInvert ? 1 : 0)}")) continue;

                // Layer-gated macros are layer CONTENT too (v25,
                // always_on_action): a set whose only authored binding is
                // an always-on one-shot must stay reachable, and its
                // macros ride the Xbox slot.
                // Deliberately consulted for xboxHas ONLY (#254 A-2
                // audit): translated macros always materialize onto the
                // Xbox slot, so macro-only layer content keeps the
                // activator alive through the xbox term. The KBM term
                // counting them too would double-count the same content.
                bool MacroHasLayer(string l) => !string.IsNullOrEmpty(l)
                    && run.Profile.Macros.Any(m => string.Equals(m.LayerMask, l, StringComparison.Ordinal));

                bool xboxHas, kbmHas;
                if (req.Mode == "Cycle")
                {
                    var stops = req.CycleLayers.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    xboxHas = stops.Any(l => LayerHasRows(run.Profile.XboxMappingSet, l)
                        || MacroHasLayer(l));
                    kbmHas = stops.Any(l => LayerHasRows(run.Profile.KbmMappingSet, l));
                    if (stops.Length == 0 && req.CycleIncludeBase)
                    {
                        // A sentinel set cycle on a single-set config has
                        // no queued stops (v20). Its whole ring is Base,
                        // so Base rows keep the activator live. The
                        // runtime never steps an empty queue, which is
                        // exactly what a next-set press does when there
                        // is only one set to land on.
                        xboxHas = LayerHasRows(run.Profile.XboxMappingSet, "Base");
                        kbmHas = LayerHasRows(run.Profile.KbmMappingSet, "Base");
                    }
                }
                else
                {
                    // Every non-Cycle mode engages its own LayerMask (the
                    // unmerged-jump lowering already rewrote Custom masks).
                    xboxHas = LayerHasRows(run.Profile.XboxMappingSet, req.LayerMask)
                        || MacroHasLayer(req.LayerMask);
                    kbmHas = LayerHasRows(run.Profile.KbmMappingSet, req.LayerMask);
                }

                // Round-four mirror (audit 2026-07-25, R8/R9): a
                // REPLACEMENT-set switch must land on the macro host slot
                // whenever the profile carries macros, even when the set
                // has no Xbox rows. Macros always materialize onto the
                // Xbox slot, and the engine gates them by the OWN slot's
                // engaged layer; a switch that lives only on the KBM set
                // leaves the Xbox slot reading Base forever, so Base
                // macros stayed open inside a set that REPLACES Base and
                // set-scoped macros never opened. InheritUnmapped is the
                // persisted replaces-vs-overlays bit: sets are emitted
                // non-inheriting, mode-shift and overlay layers inherit,
                // so the mirror never drags a mode-shift onto a slot that
                // lacks its rows.
                if (!req.InheritUnmapped && run.Profile.Macros.Count > 0)
                    xboxHas = true;

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
                    // The second leg lands on the kind's own carrier: the
                    // Chord kind reads ChordSecondDescriptor, the Axis
                    // kind (a gated wedge, v26) reads GateDescriptor.
                    ChordSecondDescriptor = req.Kind == "Chord" ? req.GateDescriptor : "",
                    GateDescriptor = req.Kind == "Axis" ? req.GateDescriptor : "",
                    AxisThreshold = req.AxisThreshold,
                    AxisHalf = req.AxisHalf,
                    AxisInvert = req.AxisInvert,
                    DelayMs = req.DelayMs,
                    ReleaseDelayMs = req.ReleaseDelayMs,
                    DoublePressMs = req.DoublePressMs,
                    CycleLayers = req.CycleLayers,
                    CycleIncludeBase = req.CycleIncludeBase,
                };
                if (xboxHas) run.Profile.XboxMappingSet.ShiftActivators.Add(Clone(act));
                if (kbmHas) run.Profile.KbmMappingSet.ShiftActivators.Add(Clone(act));

                string engagedText = req.Mode switch
                {
                    // An empty queue with Base in the ring is the v20
                    // single-set sentinel cycle. Render "Base" alone so
                    // the report never leads with a bare pipe.
                    "Cycle" => req.CycleLayers.Length == 0 && req.CycleIncludeBase
                        ? "Base"
                        : req.CycleLayers + (req.CycleIncludeBase ? "|Base" : ""),
                    _ => req.LayerMask,
                };
                run.Report.Add(TranslationStatus.Clean, TranslationReasons.ShiftLayerEmitted,
                    req.Path, emitted: $"{req.Mode} -> {engagedText}",
                    args: req.LayerName);
            }
        }

        /// <summary>CHANGE_PRESET jumps left unmerged (no counterpart on the
        /// same input) can't keep the placeholder Jump_* mask: the runtime's
        /// Custom mode latches the activator's OWN LayerMask and ignores
        /// JumpToLayer (#119 retired the jump-to-target behavior), so the
        /// placeholder engaged a rowless layer and blanked the pad. A jump
        /// to a preset layer becomes a Latch of that layer (press again
        /// releases back to Base, the mode's own gesture). A lone jump to
        /// Base becomes a single-stop Cycle through the hosting preset's
        /// layer with Base in the ring, the runtime's press-to-step
        /// return construct.</summary>
        private static void LowerUnmergedJumps(Run run)
        {
            for (int i = run.Activators.Count - 1; i >= 0; i--)
            {
                var req = run.Activators[i];
                if (req.Mode != "Custom") continue;
                if (req.JumpToLayer == "Base")
                {
                    if (string.IsNullOrEmpty(req.HostLayer) || req.HostLayer == "Base")
                    {
                        // A Base-hosted jump to Base switches nothing.
                        run.Activators.RemoveAt(i);
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.ShiftLayerEmpty,
                            req.Path, args: req.LayerName);
                        continue;
                    }
                    req.Mode = "Cycle";
                    req.LayerMask = req.HostLayer;
                    req.CycleLayers = req.HostLayer;
                    req.CycleIncludeBase = true;
                }
                else
                {
                    req.LayerMask = req.JumpToLayer;
                }
                req.JumpToLayer = "";
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
                // The gate leg is part of the input's identity: a single-pad
                // left-half click and right-half click share the pad-click
                // descriptor and differ only in gate (#9 B-1). The v15 half
                // stamp is too: opposite flick directions share an axis
                // descriptor and differ only in half. The v25 double-press
                // gate likewise: a Double_Press jump and a Full_Press jump
                // on the same button are two different triggers and must
                // not fold into one Cycle.
                .GroupBy(a => $"{a.Kind}|{a.Descriptor}|{a.GateDescriptor}|{(a.AxisHalf ? 1 : 0)}{(a.AxisInvert ? 1 : 0)}|{a.DoublePressMs}", StringComparer.Ordinal)
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
                    GateDescriptor = first.GateDescriptor,
                    AxisThreshold = first.AxisThreshold,
                    AxisHalf = first.AxisHalf,
                    AxisInvert = first.AxisInvert,
                    DoublePressMs = first.DoublePressMs,
                    CycleLayers = string.Join("|", stops),
                    CycleIncludeBase = includeBase,
                    Path = first.Path,
                });
            }
        }

        // The DTO's own full-field Clone, never a hand-list: the local
        // hand-list this replaced dropped ReleaseDelayMs on arrival (v22),
        // exactly the drift ShiftActivator.Clone's doc comment warns
        // about.
        private static ShiftActivator Clone(ShiftActivator a) => a.Clone();

        private static bool LayerHasRows(MappingSet set, string layer)
            => !string.IsNullOrEmpty(layer)
            && set.Rows.Any(r => string.Equals(r.LayerMask ?? "Base", layer, StringComparison.Ordinal));

        /// <summary>True when the activator engages the layer directly or
        /// holds it as a stop in its Cycle queue. The queue check keeps a
        /// v20 sentinel set cycle's later stops from a false
        /// PresetHasNoActivator note: the ring reaches every set even
        /// though only the first stop is the activator's own mask.</summary>
        private static bool ActivatorReachesLayer(ShiftActivator a, string layer)
            => a.LayerMask == layer
            || a.JumpToLayer == layer
            || (!string.IsNullOrEmpty(a.CycleLayers)
                && a.CycleLayers.Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(layer, StringComparer.Ordinal));

        private void ReportActivatorlessPresets(Run run)
        {
            foreach (var kv in run.LayerByPreset.OrderBy(k => k.Key))
            {
                if (kv.Value == "Base") continue;
                bool hasRows = LayerHasRows(run.Profile.XboxMappingSet, kv.Value)
                    || LayerHasRows(run.Profile.KbmMappingSet, kv.Value);
                if (!hasRows) continue;
                bool hasActivator =
                    run.Profile.XboxMappingSet.ShiftActivators.Any(a => ActivatorReachesLayer(a, kv.Value))
                    || run.Profile.KbmMappingSet.ShiftActivators.Any(a => ActivatorReachesLayer(a, kv.Value));
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
