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

            public Run(SteamInputConfig config, TranslationOptions options)
            {
                Config = config;
                Options = options;
                Report = Profile.Report;
                Report.SchemaVersion = config.Version;
                Report.ControllerType = config.ControllerType ?? "";
                NintendoLabels = PhysicalSlotResolver.UsesNintendoLabels(config.ControllerType);
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
                ReportDroppedGroupSettings(run, settings, path);

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

                case "absolute_mouse":
                case "relative_mouse":
                    if (mode == "absolute_mouse")
                    {
                        // Issue #9 lists absolute_mouse under Partial: Steam
                        // positions the cursor absolutely on the pad surface,
                        // PadForge emits relative deltas.
                        run.Report.Add(TranslationStatus.Partial,
                            TranslationReasons.AbsoluteMouseApproximated, path);
                    }
                    EmitMouseAxes(run, slot, layer, path, settings, TrackpadMouseBaseline);
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings);
                    break;

                case "scrollwheel":
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.ScrollWheelModeNotSupported, path);
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings,
                        onlyInputs: new[] { "click" });
                    break;

                case "touch_menu":
                    TranslateTouchMenu(run, preset, effective, slot, layer, path, settings);
                    break;

                case "radial_menu":
                {
                    int cells = effective.Inputs.Keys.Count(k =>
                        k.StartsWith("touch_menu_button_", StringComparison.OrdinalIgnoreCase));
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.RadialMenuNeedsOverlay,
                        path, args: cells.ToString(CultureInfo.InvariantCulture));
                    TranslateMemberGroup(run, preset, effective, slot, layer, path, settings,
                        onlyInputs: new[] { "click" });
                    break;
                }

                case "mouse_region":
                    run.Report.Add(TranslationStatus.Skipped, TranslationReasons.MouseRegionNotSupported, path);
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
            "radial_menu", "mouse_region", "gyro_to_mouse",
        };

        /// <summary>Response-shaping group settings PadForge has no per-row
        /// channel for. Named per issue #20's sensitivity-curve backlog;
        /// key list grounded on the corpus (deadzone_outer_radius 28640..31999,
        /// curve_exponent 4) plus the sibling keys of the same UI cluster.</summary>
        private static readonly string[] CurveSettingKeys =
        {
            "deadzone_outer_radius", "deadzone_shape",
            "custom_curve_exponent", "curve_exponent", "output_curve",
        };

        /// <summary>Named notes for group settings that used to drop
        /// silently: response-curve shaping, gyro engage/ratchet button
        /// masks, and the group-level haptic override (counted into the
        /// per-config aggregate).</summary>
        private void ReportDroppedGroupSettings(Run run,
            Dictionary<string, string> settings, string path)
        {
            var curves = CurveSettingKeys.Where(settings.ContainsKey).ToList();
            if (curves.Count > 0)
            {
                run.Report.Add(TranslationStatus.Partial, TranslationReasons.ResponseCurveNotSupported,
                    path, args: string.Join(", ", curves));
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

            if (settings.TryGetValue("haptic_intensity_override", out var h)
                && (h ?? "").Trim() != "0")
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
        };

        /// <summary>Walks a group's named inputs and translates each
        /// activator's bindings against the resolved physical source.</summary>
        private void TranslateMemberGroup(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings,
            IReadOnlyList<string> onlyInputs = null)
        {
            bool requiresClick = RequiresClick(slot, group, settings);
            int groupDeadZonePct = GroupDeadZonePercent(settings);

            foreach (var inputName in group.Inputs.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (onlyInputs != null && !onlyInputs.Contains(inputName, StringComparer.OrdinalIgnoreCase))
                    continue;
                var input = group.Inputs[inputName];
                if (input.Activators.Count == 0) continue;

                var source = PhysicalSlotResolver.Resolve(slot, inputName, run.NintendoLabels);
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
                string clickGate = requiresClick
                    && PhysicalSlotResolver.IsTrackpad(slot)
                    && inputName.StartsWith("dpad_", StringComparison.OrdinalIgnoreCase)
                        ? $"Touchpad {PhysicalSlotResolver.TrackpadIndex(slot)} Click"
                        : null;

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

        /// <summary>Two-cell touch menus map onto the touch-spot left/right
        /// split; anything larger needs Steam's on-screen grid.</summary>
        private void TranslateTouchMenu(Run run, SteamInputPreset preset, SteamInputGroup group,
            SteamSlot slot, string layer, string path, Dictionary<string, string> settings)
        {
            var cells = group.Inputs.Keys
                .Where(k => k.StartsWith("touch_menu_button_", StringComparison.OrdinalIgnoreCase))
                .Select(k => (Name: k, Index: ParseTrailingInt(k)))
                .Where(c => c.Index >= 0)
                .OrderBy(c => c.Index)
                .ToList();

            if (!PhysicalSlotResolver.IsTrackpad(slot) || cells.Count != 2
                || cells[0].Index != 0 || cells[1].Index != 1)
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.TouchMenuNeedsOverlay,
                    path, args: cells.Count.ToString(CultureInfo.InvariantCulture));
                return;
            }

            int p = PhysicalSlotResolver.TrackpadIndex(slot);
            foreach (var cell in cells)
            {
                var source = PhysicalSlotResolver.TouchMenuSpot(p, cell.Index);
                TranslateInput(run, preset, group.Inputs[cell.Name], source, null, layer,
                    $"{path}/{cell.Name}");
            }
        }

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
                int p = PhysicalSlotResolver.TrackpadIndex(slot);
                // 2 = right stick, anything else lands on the left.
                string dst = output == 2 ? "Right" : "Left";
                AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisX",
                    Src($"Touchpad {p} StickX"), isAxis: true,
                    TranslationStatus.Partial, TranslationReasons.TrackpadFeatureRequired, path,
                    args: PhysicalSlotResolver.FeatureJoystickOutput);
                AddRowSource(run, isKbm: false, layer, $"{dst}ThumbAxisY",
                    Src($"Touchpad {p} StickY"), isAxis: true,
                    TranslationStatus.Partial, TranslationReasons.TrackpadFeatureRequired, path,
                    args: PhysicalSlotResolver.FeatureJoystickOutput);
            }

            TranslateMemberGroup(run, preset, group, slot, layer, path, settings);
        }

        /// <summary>Mouse-mode groups: the slot's analog surface drives the
        /// KbM mouse delta. Multiple groups merging into KbmMouseX/Y get
        /// Combine=Sum (mouse deltas are additive). The sensitivity key is
        /// per mode: the classic modes store "sensitivity", gyro_to_mouse
        /// stores "gyro_natural_sensitivity".</summary>
        private void EmitMouseAxes(Run run, SteamSlot slot, string layer, string path,
            Dictionary<string, string> settings, double baseline,
            string sensitivityKey = "sensitivity")
        {
            var pair = PhysicalSlotResolver.MouseAxisPair(slot);
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

            MappingSource Make(string descriptor)
            {
                var src = new MappingSource { Descriptor = descriptor };
                if (family == 0) src.Sensitivity = ratio;
                else if (family == 2) src.GyroSensitivity = ratio;
                if (dzPct > 0) src.DeadZone = dzPct;
                return src;
            }

            var status = TranslationStatus.Clean;
            string reason = TranslationReasons.RowEmitted;
            string arg = null;
            if (family == 1 && Math.Abs(ratio - 1.0) > 0.001)
            {
                // Touchpad mouse tuning lives on the Touchpad tab per
                // (slot, device), not on the row; the config's non-default
                // sensitivity doesn't carry over.
                status = TranslationStatus.Partial;
                reason = TranslationReasons.TouchpadTuningNotPerRow;
                arg = sensRaw;
            }

            AddRowSource(run, isKbm: true, layer, "KbmMouseX", Make(x), isAxis: true,
                status, reason, path, args: arg);
            AddRowSource(run, isKbm: true, layer, "KbmMouseY", Make(y), isAxis: true,
                status, reason, path, args: arg);
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

                foreach (var binding in activator.Bindings)
                {
                    TranslateBinding(run, preset, binding, source, clickGate, layer, actPath,
                        soft, onRelease, holdRepeats, intervalMs, input.Name);
                }
            }
        }

        /// <summary>Long_Press activators carrying a layer engage
        /// (mode_shift / hold_layer / add_layer) map to the layer's
        /// ShiftActivator with DelayMs = the activator's long_press_time.
        /// The engine's hold-before-engage debounce is the same construct.
        /// The stored value is milliseconds (corpus: 222 / 224 on
        /// 789818086); absent = Steam's UI default of 500 ms. Keys and
        /// buttons under Long_Press stay skipped this wave (the
        /// hold-threshold macro trigger is a separate build).</summary>
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

            bool anyLayerCarry = false;
            foreach (var binding in activator.Bindings)
            {
                string bt = (binding.Type ?? "").Trim().ToLowerInvariant();
                string action = FirstToken(binding.Param).ToUpperInvariant();
                if (bt == "mode_shift")
                {
                    anyLayerCarry = true;
                    TranslateModeShift(run, preset, binding, source, actPath, delayMs);
                }
                else if (bt == "controller_action"
                    && (action == "ADD_LAYER" || action == "HOLD_LAYER"))
                {
                    anyLayerCarry = true;
                    TranslateControllerAction(run, preset, binding, source, layer, actPath,
                        onRelease: false, input.Name, delayMs);
                }
                else
                {
                    ReportSkipUnlessSilent(run, TranslationReasons.LongPressNotSupported, actPath, binding);
                }
            }

            if (anyLayerCarry)
                ReportDroppedActivatorExtras(run, activator, actPath);
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
            bool soft, bool onRelease, bool holdRepeats, int intervalMs, string inputName)
        {
            string type = (binding.Type ?? "").Trim().ToLowerInvariant();
            switch (type)
            {
                case "key_press":
                    TranslateKeyPress(run, preset, binding, source, clickGate, layer, path,
                        soft, onRelease, holdRepeats, intervalMs, inputName);
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
                    var src = BuildSource(source, soft);
                    src.Invert = wheel.Value.invert;
                    AddRowSource(run, isKbm: true, layer, wheel.Value.target, src, isAxis: true,
                        StatusFor(source, soft), ReasonFor(source, soft), path, binding.Raw,
                        args: source.TrackpadFeature);
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
                    // Steam repeats the xinput press while held; PadForge
                    // rows hold the output instead (there is no xinput turbo
                    // action). Note the difference, then translate normally.
                    if (holdRepeats)
                    {
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.RepeatDropped,
                            path, binding.Raw);
                    }
                    bool identity = !soft && clickGate == null
                        && string.Equals(source.AutomapTarget, xt.Target, StringComparison.Ordinal);
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
                    TranslateModeShift(run, preset, binding, source, path);
                    break;

                case "controller_action":
                    TranslateControllerAction(run, preset, binding, source, layer, path, onRelease, inputName);
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
            bool soft, bool onRelease, bool holdRepeats, int intervalMs, string inputName)
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

        private void EmitKeyMacro(Run run, SteamInputPreset preset, SteamInputBinding binding,
            ResolvedSource source, string path,
            (TranslatedMacroAction Action, string TriggerMode) shape,
            byte vk, int intervalMs, string keyName, string inputName)
        {
            if (source.XboxButtonBit == 0 && string.IsNullOrEmpty(source.MacroAxisTarget))
            {
                run.Report.Add(TranslationStatus.Skipped, TranslationReasons.NoDeviceFreeTrigger,
                    path, binding.Raw);
                return;
            }

            string verb = shape.Action == TranslatedMacroAction.KeyTap ? "Tap" : "Autofire";
            run.Profile.Macros.Add(new TranslatedMacro
            {
                Name = $"{verb} {keyName} ({inputName})",
                Action = shape.Action,
                TriggerMode = shape.TriggerMode,
                TriggerXboxButtons = source.XboxButtonBit,
                TriggerAxisTarget = source.XboxButtonBit == 0 ? source.MacroAxisTarget ?? "" : "",
                TriggerAxisThresholdPercent = source.DeadZone > 0 ? source.DeadZone : 50,
                ConsumeTrigger = true,
                VirtualKey = vk,
                IntervalMs = intervalMs,
            });
            run.Report.Add(TranslationStatus.Partial, TranslationReasons.MacroTriggerViaXboxOutput,
                path, binding.Raw, emitted: $"{verb} {keyName} macro");
        }

        private void TranslateModeShift(Run run, SteamInputPreset preset, SteamInputBinding binding,
            ResolvedSource source, string path, int activatorDelayMs = 0)
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
                Mode = "Hold",
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
            bool onRelease, string inputName, int activatorDelayMs = 0)
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
                    if (source.XboxButtonBit == 0 && string.IsNullOrEmpty(source.MacroAxisTarget))
                    {
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.NoDeviceFreeTrigger,
                            path, binding.Raw);
                        return;
                    }
                    run.Profile.Macros.Add(new TranslatedMacro
                    {
                        Name = $"Warp cursor ({inputName})",
                        Action = TranslatedMacroAction.MoveMouseToScreenPosition,
                        TriggerMode = onRelease ? "OnRelease" : "OnPress",
                        TriggerXboxButtons = source.XboxButtonBit,
                        TriggerAxisTarget = source.XboxButtonBit == 0 ? source.MacroAxisTarget ?? "" : "",
                        TriggerAxisThresholdPercent = source.DeadZone > 0 ? source.DeadZone : 50,
                        ConsumeTrigger = false,
                        NormalizedX = Math.Clamp(nx, 0, 65535),
                        NormalizedY = Math.Clamp(ny, 0, 65535),
                    });
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.MacroTriggerViaXboxOutput,
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
                        Mode = action.Equals("HOLD_LAYER", StringComparison.OrdinalIgnoreCase) ? "Hold" : "Toggle",
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
                    if (source.XboxButtonBit == 0 && string.IsNullOrEmpty(source.MacroAxisTarget))
                    {
                        // Touchpad-hosted set_led (and every other input
                        // without an Xbox output representation) has no
                        // device-free trigger yet.
                        run.Report.Add(TranslationStatus.Skipped, TranslationReasons.NoDeviceFreeTrigger,
                            path, binding.Raw);
                        return;
                    }
                    int satPct = sat > 100 ? (int)Math.Round(sat * 100.0 / 255.0) : sat;
                    run.Profile.Macros.Add(new TranslatedMacro
                    {
                        Name = $"Set LED ({inputName})",
                        Action = TranslatedMacroAction.SetLightbarColor,
                        TriggerMode = onRelease ? "OnRelease" : "OnPress",
                        TriggerXboxButtons = source.XboxButtonBit,
                        TriggerAxisTarget = source.XboxButtonBit == 0 ? source.MacroAxisTarget ?? "" : "",
                        TriggerAxisThresholdPercent = source.DeadZone > 0 ? source.DeadZone : 50,
                        ConsumeTrigger = false,
                        LedR = Math.Clamp(r, 0, 255),
                        LedG = Math.Clamp(g, 0, 255),
                        LedB = Math.Clamp(b, 0, 255),
                        LedBrightnessPercent = Math.Clamp(bright, 0, 100),
                        LedSaturationPercent = Math.Clamp(satPct, 0, 100),
                        LedSetting = ledSetting,
                    });
                    if (ledSetting == 2)
                    {
                        // "Restore default lighting" has no PadForge verb;
                        // the materializer approximates it as clearing the
                        // override.
                        run.Report.Add(TranslationStatus.Partial, TranslationReasons.SetLedDefaultApproximated,
                            path, binding.Raw);
                    }
                    run.Report.Add(TranslationStatus.Partial, TranslationReasons.MacroTriggerViaXboxOutput,
                        path, binding.Raw, emitted: "Set LED macro");
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
             : soft ? TranslationStatus.Partial
             : TranslationStatus.Clean;

        private static string ReasonFor(ResolvedSource source, bool soft)
            => source.TrackpadFeature != null ? TranslationReasons.TrackpadFeatureRequired
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
            MappingSource gate = clickGate != null ? new MappingSource { Descriptor = clickGate } : null;
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
            run.Report.ShiftActivatorCount =
                profile.XboxMappingSet.ShiftActivators.Count + profile.KbmMappingSet.ShiftActivators.Count;

            // Slot demand (owner report 2026-07-13: a pure keyboard config
            // imported with an empty Xbox VC). The Xbox slot is needed for
            // rows/activators and for macros, whose triggers read the Xbox
            // slot's combined output. Identity bindings now materialize as
            // rows, so the row count covers them; the Identities clause
            // stays as belt-and-braces (a row-cap overflow could drop an
            // identity row, and the slot must still exist for it).
            profile.NeedsXboxSlot = profile.XboxMappingSet.Rows.Count > 0
                || profile.XboxMappingSet.ShiftActivators.Count > 0
                || run.Identities.Count > 0
                || profile.Macros.Count > 0;
            profile.NeedsKbmSlot = profile.KbmMappingSet.Rows.Count > 0
                || profile.KbmMappingSet.ShiftActivators.Count > 0;
            return profile;
        }

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
