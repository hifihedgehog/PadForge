using System;
using System.Collections.Generic;
using System.Globalization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Translates legacy per-(VC × Device) <see cref="PadSetting"/> mapping
    /// fields into a per-VC <see cref="MappingSet"/>. Used in Phase 1b on
    /// settings load: for each slot, collapse mapping descriptors from all
    /// devices assigned to that slot into one MappingSet.
    ///
    /// <para>
    /// Two devices on the same slot mapping the same Xbox output collapse
    /// into ONE row with multiple sources, so the user sees the per-target
    /// combine behavior in one place rather than as ghost rows scattered
    /// across the table. Default combine modes (empty string) match
    /// today's implicit Step 4 cross-device combine: OR for buttons, MaxAbs
    /// for axes/triggers.
    /// </para>
    ///
    /// <para>
    /// Legacy paired-axis fields (<c>LeftThumbAxisX</c> + <c>LeftThumbAxisXNeg</c>)
    /// emit two sources on one row, the negative-direction source carrying
    /// <see cref="MappingSource.Invert"/>=<c>true</c> XOR'd with whatever
    /// inversion is encoded in the descriptor's "I"/"IH" prefix.
    /// </para>
    /// </summary>
    public static class MappingSetMigrator
    {

        // Output target names. Order matters for migration determinism (rows
        // appear in this order in the resulting MappingSet for a tidy XML).
        private static readonly string[] ButtonTargets =
        {
            "ButtonA", "ButtonB", "ButtonX", "ButtonY",
            "LeftShoulder", "RightShoulder",
            "ButtonBack", "ButtonStart", "ButtonGuide", "ButtonShare",
            "LeftThumbButton", "RightThumbButton",
            "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
        };

        private static readonly string[] AxisTargets =
        {
            "LeftThumbAxisX", "LeftThumbAxisY",
            "RightThumbAxisX", "RightThumbAxisY",
        };

        // Legacy-only "combined POV" target. Migration emits a row only if
        // at least one device has a non-empty DPad descriptor AND none of
        // the four individual DPad direction fields are populated on that
        // device (current Step 3 prefers individual over combined when
        // both are set).
        private const string CombinedDPadTarget = "DPad";

        private const string TriggerLeft = "LeftTrigger";
        private const string TriggerRight = "RightTrigger";

        // Bundled motion-passthrough targets. Sony-class VCs only — Xbox and
        // friends have no motion channel to relay. Sub-channels: one row per
        // sensor type, source descriptors "Motion Gyro" / "Motion Accel".
        public const string MotionGyroTarget  = "MotionGyro";
        public const string MotionAccelTarget = "MotionAccel";
        public const string MotionGyroSourceDescriptor  = "Motion Gyro";
        public const string MotionAccelSourceDescriptor = "Motion Accel";

        /// <summary>Aux (left-side) accelerometer variant for the MotionAccel
        /// row (issue #199 follow-up): sources the slot's single IMU stream
        /// from the Nunchuk's own sensor (or a combined pair's left Joy-Con)
        /// instead of the body accelerometer. The reading side is
        /// InputManager.UpdateMotionSnapshots, which pulls
        /// CustomInputState.AccelAux when the row's source carries this
        /// descriptor. Exact-match family like "Motion Lean L": the leading
        /// 'M' clears the I/H prefix grammar, IsMotionDescriptor stays true
        /// (benign), and ParseMotionSubChannel returns -1 ("Accel L" is not
        /// "Accel").</summary>
        public const string MotionAccelAuxSourceDescriptor = "Motion Accel L";

        /// <summary>True when the descriptor is
        /// <see cref="MotionAccelAuxSourceDescriptor"/>.</summary>
        public static bool IsMotionAccelAuxDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor)
            && string.Equals(descriptor.Trim(), MotionAccelAuxSourceDescriptor, StringComparison.OrdinalIgnoreCase);

        // Touchpad output targets. Stored as plain string properties on
        // PadSetting (TouchpadX1, TouchpadY1, …, TouchpadClick), reached via
        // the same reflection path as ButtonTargets / AxisTargets. Emitted
        // for ANY assigned device whose PadSetting has the field set —
        // gamepad-only filter is OFF because both gamepad devices that
        // expose a touchpad surface (DualSense / DS4 / Steam Deck) AND
        // pure touchpad devices (web touchpad client, precision touchpad)
        // legitimately contribute here. Without this, a second device
        // assigned to a PlayStation slot wouldn't add a TouchpadX1 row to
        // the per-VC MappingSet for the merge to pick up, and its touchpad
        // mapping would never appear alongside the existing device's as a
        // second source on the same row.
        private static readonly string[] TouchpadTargets =
        {
            "TouchpadX1", "TouchpadY1",
            "TouchpadX2", "TouchpadY2",
            "TouchpadContact1", "TouchpadContact2",
            "TouchpadClick",
        };

        /// <summary>
        /// Resolves the property-name pair for a paired axis target.
        /// Returns <c>(primaryFieldName, negFieldName)</c> for axis targets,
        /// or <c>(target, null)</c> for non-paired targets.
        /// </summary>
        private static (string primary, string neg) GetPairedFieldNames(string target)
        {
            return target switch
            {
                "LeftThumbAxisX"  => ("LeftThumbAxisX",  "LeftThumbAxisXNeg"),
                "LeftThumbAxisY"  => ("LeftThumbAxisY",  "LeftThumbAxisYNeg"),
                "RightThumbAxisX" => ("RightThumbAxisX", "RightThumbAxisXNeg"),
                "RightThumbAxisY" => ("RightThumbAxisY", "RightThumbAxisYNeg"),
                _ => (target, null),
            };
        }

        /// <summary>
        /// Reads a string property from a <see cref="PadSetting"/> by name
        /// using reflection. Used so the migrator stays decoupled from the
        /// growing set of mapping fields on PadSetting.
        /// </summary>
        private static string GetField(PadSetting ps, string name)
        {
            if (ps == null || string.IsNullOrEmpty(name)) return "";
            var prop = ps.GetType().GetProperty(name);
            if (prop == null) return "";
            return (prop.GetValue(ps) as string) ?? "";
        }

        /// <summary>
        /// Builds one <see cref="MappingSet"/> from the per-device legacy
        /// PadSettings of every device assigned to a slot.
        /// </summary>
        /// <param name="slot">VC slot index.</param>
        /// <param name="devicesAndPadSettings">Per-device pairs of
        /// (device InstanceGuid, that device's PadSetting). Order is the
        /// order sources appear within a multi-device row.</param>
        public static MappingSet BuildFromLegacy(
            int slot,
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting)> devicesAndPadSettings)
        {
            // Wrap into the richer signature without requiring callers to
            // change yet; gamepad-vs-not filtering happens later when the
            // caller passes through CapType.
            var withCap = new List<(string, PadSetting, bool)>(devicesAndPadSettings.Count);
            foreach (var t in devicesAndPadSettings)
                withCap.Add((t.DeviceGuid, t.PadSetting, true)); // assume gamepad-eligible
            return BuildFromLegacy(slot, withCap);
        }

        /// <summary>
        /// Richer overload: per-device tuple includes <paramref name="isGamepadEligible"/>
        /// which the gamepad-target emitters consult so a non-gamepad
        /// device (keyboard, mouse, touchpad) never contributes a Source
        /// to gamepad-class rows even if its PadSetting field happens to
        /// hold a stale gamepad descriptor (e.g. from a prior misconfig
        /// or a copy/paste mishap).
        /// </summary>
        public static MappingSet BuildFromLegacy(
            int slot,
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting, bool IsGamepadEligible)> devicesAndPadSettings)
        {
            var ms = new MappingSet();
            if (devicesAndPadSettings == null || devicesAndPadSettings.Count == 0)
                return ms;

            // Button-class targets (single-source per device, gamepad
            // devices only — keyboards/mice/touchpads with stray gamepad
            // descriptors don't contribute).
            foreach (var target in ButtonTargets)
                AppendSimpleRow(ms, target, devicesAndPadSettings, gamepadOnly: true);

            // Trigger targets (gamepad-only, axis-class).
            AppendSimpleRow(ms, TriggerLeft,  devicesAndPadSettings, gamepadOnly: true);
            AppendSimpleRow(ms, TriggerRight, devicesAndPadSettings, gamepadOnly: true);

            // Bipolar axis targets: collapse primary + Neg fields into one
            // row with up-to-2 sources per device (negative source has
            // Invert flipped relative to descriptor's encoded inversion).
            foreach (var target in AxisTargets)
                AppendBipolarRow(ms, target, devicesAndPadSettings);

            // Combined DPad: emit only for devices whose individual DPad
            // direction fields are all empty AND DPad descriptor is non-empty.
            AppendCombinedDPadRow(ms, devicesAndPadSettings);

            // Touchpad targets — any device with a non-empty field
            // contributes (web touchpad, PTP, DS4, DualSense, …). Multiple
            // devices on the same slot land as multiple Sources on one row,
            // which is what makes the user-visible Mappings tab show every
            // device's touchpad contribution side-by-side instead of only
            // the first-assigned device's.
            foreach (var target in TouchpadTargets)
                AppendSimpleRow(ms, target, devicesAndPadSettings, gamepadOnly: false);

            // Raw-surface targets (profile-driven slots: Nintendo, Extended
            // custom): per-device mappings live in the PadSetting's raw
            // mapping dictionary under the persisted raw target grammar.
            // Without this lane those mappings evaluate only through the
            // engine's per-device fallback and never surface as rows, so
            // the Mappings grid showed nothing for them (the Nintendo
            // positional automap was invisible). The dictionary also
            // carries non-mapping tuning keys (deadzone shapes, steering,
            // flick stick), so only exact raw-target keys translate.
            AppendRawSurfaceRows(ms, devicesAndPadSettings);

            // MIDI and KBM surfaces are the raw surface's dictionary
            // siblings and rot the same way without a lane here, but
            // WORSE: the save pipeline regenerates every device's
            // PadSetting from the grid view, so a merge that drops these
            // rows turns the next save into a wipe of the authored
            // mappings (the MIDI automap "didn't stick", 2026-07-23).
            AppendDictionarySurfaceRows(ms, devicesAndPadSettings,
                MidiTargetKey, MidiAxisTargetPrefixes, ps => ps?.MidiMappingEntries,
                (ps, key) => ps?.GetMidiMapping(key));
            AppendDictionarySurfaceRows(ms, devicesAndPadSettings,
                KbmTargetKey, KbmAxisTargetPrefixes, ps => ps?.KbmMappingEntries,
                (ps, key) => ps?.GetKbmMapping(key));

            return ms;
        }

        private static readonly System.Text.RegularExpressions.Regex RawTargetKey =
            new(@"^Raw(Axis\d+|Btn\d+|Pov\d+(Up|Down|Left|Right))$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // MIDI surface grammar (MidiVirtualController targets): CC slots
        // are axis-like (Neg legs fold), note slots are button-like.
        private static readonly System.Text.RegularExpressions.Regex MidiTargetKey =
            new(@"^Midi(CC\d+|Note\d+)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly string[] MidiAxisTargetPrefixes = { "MidiCC" };

        // KBM surface grammar (KeyboardMouseVirtualController targets):
        // keys and mouse buttons are button-like; mouse axes and scroll
        // are axis-like.
        private static readonly System.Text.RegularExpressions.Regex KbmTargetKey =
            new(@"^Kbm(Key[0-9A-Fa-f]+|MBtn\d+|MouseX|MouseY|Scroll)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly string[] KbmAxisTargetPrefixes = { "KbmMouseX", "KbmMouseY", "KbmScroll" };

        /// <summary>Rewrites legacy raw-surface tokens (the pre-2026-07-19
        /// "Extended*" spellings) to the current "Raw*" grammar. Applies to
        /// one token (a row target or a dictionary key); returns the input
        /// unchanged when it is not a legacy raw token. The raw surface is
        /// category-neutral (Nintendo and Extended slots both ride it), so
        /// its grammar no longer wears the Extended category's name.</summary>
        public static string NormalizeRawToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return token;
            if (!token.StartsWith("Extended", StringComparison.Ordinal)) return token;
            string rest = token.Substring("Extended".Length);
            if (rest.StartsWith("Axis", StringComparison.Ordinal)
                || rest.StartsWith("Btn", StringComparison.Ordinal)
                || rest.StartsWith("Pov", StringComparison.Ordinal)
                || rest.StartsWith("Stick", StringComparison.Ordinal)
                || rest.StartsWith("Trigger", StringComparison.Ordinal))
                return "Raw" + rest;
            return token;
        }

        /// <summary>Normalizes every row target in <paramref name="ms"/>
        /// through <see cref="NormalizeRawToken"/>. Idempotent; called at
        /// every lane persisted MappingSets enter the process (settings
        /// load, profile apply, legacy merge) so saves written before the
        /// grammar rename keep working and re-save upgraded.</summary>
        public static void NormalizeRawSurfaceTargets(MappingSet ms)
        {
            if (ms?.Rows == null) return;
            foreach (var row in ms.Rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Target)) continue;
                row.Target = NormalizeRawToken(row.Target);
            }
        }

        /// <summary>Shared lane for the dictionary-backed target surfaces
        /// (MIDI, KBM). Same shape as <see cref="AppendRawSurfaceRows"/>:
        /// collect exact-grammar targets in first-seen order across the
        /// slot's devices, emit one row per target with one source per
        /// contributing device, and fold "{target}Neg" legs into the
        /// axis-like targets' rows with the bipolar polarity rule.</summary>
        private static void AppendDictionarySurfaceRows(
            MappingSet ms,
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting, bool IsGamepadEligible)> devices,
            System.Text.RegularExpressions.Regex targetKey,
            string[] axisTargetPrefixes,
            Func<PadSetting, RawMappingEntry[]> getEntries,
            Func<PadSetting, string, string> getMapping)
        {
            var targets = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, ps, _) in devices)
            {
                var entries = getEntries(ps);
                if (entries == null) continue;
                foreach (var e in entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.Key) || string.IsNullOrEmpty(e.Value)) continue;
                    string target = e.Key.EndsWith("Neg", StringComparison.Ordinal)
                        ? e.Key.Substring(0, e.Key.Length - 3)
                        : e.Key;
                    if (!targetKey.IsMatch(target)) continue;
                    if (seen.Add(target)) targets.Add(target);
                }
            }

            foreach (var target in targets)
            {
                bool axisLike = false;
                foreach (var prefix in axisTargetPrefixes)
                {
                    if (target.StartsWith(prefix, StringComparison.Ordinal)) { axisLike = true; break; }
                }

                var sources = new List<MappingSource>();
                foreach (var (guid, ps, _) in devices)
                {
                    string desc = getMapping(ps, target);
                    if (!string.IsNullOrEmpty(desc))
                    {
                        var src = BuildSource(guid, desc, ps?.GetMappingDeadZone(target));
                        if (src != null) sources.Add(src);
                    }

                    if (!axisLike) continue;
                    string negDesc = getMapping(ps, target + "Neg");
                    if (string.IsNullOrEmpty(negDesc)) continue;
                    var negSrc = BuildSource(guid, negDesc, ps?.GetMappingDeadZone(target));
                    if (negSrc == null) continue;
                    // Same polarity rule as AppendBipolarRow's negative leg.
                    if (Common.Mapping.SourceCoercion.InvertConsumedByHalfAxisRead(negSrc))
                        negSrc.InvertOutput = !negSrc.InvertOutput;
                    else
                        negSrc.Invert = !negSrc.Invert;
                    sources.Add(negSrc);
                }
                if (sources.Count == 0) continue;

                ms.Rows.Add(new MappingRow
                {
                    Target = target,
                    LayerMask = "Base",
                    CombineMode = "",
                    Sources = sources,
                });
            }
        }

        private static void AppendRawSurfaceRows(
            MappingSet ms,
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting, bool IsGamepadEligible)> devices)
        {
            // Targets present on any device, in first-seen order. Negative
            // axis legs ("RawAxis{N}Neg") fold into their positive
            // target's row below, mirroring AppendBipolarRow.
            var targets = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, ps, _) in devices)
            {
                var entries = ps?.RawMappingEntries;
                if (entries == null) continue;
                foreach (var e in entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.Key) || string.IsNullOrEmpty(e.Value)) continue;
                    // Pre-rename saves still carry legacy array keys here
                    // (the array only re-flushes on save); normalize before
                    // the new-grammar filter or their rows silently drop.
                    string key = NormalizeRawToken(e.Key);
                    string target = key.EndsWith("Neg", StringComparison.Ordinal)
                        ? key.Substring(0, key.Length - 3)
                        : key;
                    if (!RawTargetKey.IsMatch(target)) continue;
                    if (seen.Add(target)) targets.Add(target);
                }
            }

            foreach (var target in targets)
            {
                var sources = new List<MappingSource>();
                foreach (var (guid, ps, _) in devices)
                {
                    string raw = ps?.GetRawMapping(target);
                    if (!string.IsNullOrEmpty(raw))
                    {
                        var src = BuildSource(guid, raw, ps?.GetMappingDeadZone(target));
                        if (src != null) sources.Add(src);
                    }

                    if (!target.StartsWith("RawAxis", StringComparison.Ordinal)) continue;
                    string rawNeg = ps?.GetRawMapping(target + "Neg");
                    if (string.IsNullOrEmpty(rawNeg)) continue;
                    var negSrc = BuildSource(guid, rawNeg, ps?.GetMappingDeadZone(target));
                    if (negSrc == null) continue;
                    // Same polarity rule as AppendBipolarRow's negative leg.
                    if (Common.Mapping.SourceCoercion.InvertConsumedByHalfAxisRead(negSrc))
                        negSrc.InvertOutput = !negSrc.InvertOutput;
                    else
                        negSrc.Invert = !negSrc.Invert;
                    sources.Add(negSrc);
                }
                if (sources.Count == 0) continue;

                ms.Rows.Add(new MappingRow
                {
                    Target = target,
                    LayerMask = "Base",
                    CombineMode = "",
                    Sources = sources,
                });
            }
        }

        private static void AppendSimpleRow(
            MappingSet ms,
            string target,
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting, bool IsGamepadEligible)> devices,
            bool gamepadOnly)
        {
            var sources = new List<MappingSource>();
            foreach (var (guid, ps, isGamepad) in devices)
            {
                if (gamepadOnly && !isGamepad) continue;
                var raw = GetField(ps, target);
                if (string.IsNullOrEmpty(raw)) continue;

                var src = BuildSource(guid, raw, ps?.GetMappingDeadZone(target));
                if (src != null) sources.Add(src);
            }
            if (sources.Count == 0) return;

            ms.Rows.Add(new MappingRow
            {
                Target = target,
                LayerMask = "Base",
                CombineMode = "",
                Sources = sources,
            });
        }

        private static void AppendBipolarRow(
            MappingSet ms,
            string target,
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting, bool IsGamepadEligible)> devices)
        {
            var (primary, neg) = GetPairedFieldNames(target);
            var sources = new List<MappingSource>();

            foreach (var (guid, ps, isGamepad) in devices)
            {
                if (!isGamepad) continue; // bipolar axes are gamepad-only
                var rawPrimary = GetField(ps, primary);
                if (!string.IsNullOrEmpty(rawPrimary))
                {
                    var src = BuildSource(guid, rawPrimary, ps?.GetMappingDeadZone(target));
                    if (src != null) sources.Add(src);
                }

                if (!string.IsNullOrEmpty(neg))
                {
                    var rawNeg = GetField(ps, neg);
                    if (!string.IsNullOrEmpty(rawNeg))
                    {
                        var src = BuildSource(guid, rawNeg, ps?.GetMappingDeadZone(target));
                        if (src != null)
                        {
                            // Negative source: flip the OUTPUT sign relative to
                            // the descriptor's encoded inversion. Net effect:
                            // pressed → -1 instead of +1 on a button source.
                            //
                            // Which field carries that flip is not a free
                            // choice. On a half-axis read of a centered
                            // "Axis N" the engine consumes Invert INSIDE the
                            // read as the half SELECTOR, so assigning the
                            // polarity there does not negate anything: it
                            // silently moves the source to the other half.
                            // A legacy "HAxis 6" negative leg came out reading
                            // the lower half and emitting POSITIVE, i.e. wrong
                            // in both halves of its job. Ask the engine's own
                            // predicate which field is free rather than
                            // re-deriving the rule here.
                            if (Common.Mapping.SourceCoercion.InvertConsumedByHalfAxisRead(src))
                                src.InvertOutput = !src.InvertOutput;
                            else
                                src.Invert = !src.Invert;
                            sources.Add(src);
                        }
                    }
                }
            }

            if (sources.Count == 0) return;

            ms.Rows.Add(new MappingRow
            {
                Target = target,
                LayerMask = "Base",
                CombineMode = "",
                Sources = sources,
            });
        }

        private static void AppendCombinedDPadRow(
            MappingSet ms,
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting, bool IsGamepadEligible)> devices)
        {
            var sources = new List<MappingSource>();
            foreach (var (guid, ps, isGamepad) in devices)
            {
                if (!isGamepad) continue;
                if (ps == null) continue;
                var combined = ps.DPad ?? "";
                if (string.IsNullOrEmpty(combined)) continue;

                bool hasIndividuals =
                       !string.IsNullOrEmpty(ps.DPadUp)
                    || !string.IsNullOrEmpty(ps.DPadDown)
                    || !string.IsNullOrEmpty(ps.DPadLeft)
                    || !string.IsNullOrEmpty(ps.DPadRight);
                if (hasIndividuals) continue;

                var src = BuildSource(guid, combined, ps.GetMappingDeadZone(CombinedDPadTarget));
                if (src != null) sources.Add(src);
            }

            if (sources.Count == 0) return;

            ms.Rows.Add(new MappingRow
            {
                Target = CombinedDPadTarget,
                LayerMask = "Base",
                CombineMode = "",
                Sources = sources,
            });
        }

        /// <summary>
        /// Parses a legacy descriptor string ("Button 0", "IHAxis 1",
        /// "POV 0 Up", "Slider 0") into a <see cref="MappingSource"/>.
        /// The "I" / "H" / "IH" prefixes encode invert / half-axis flags;
        /// the new schema splits those into per-source bool flags so the
        /// stored Descriptor is the unprefixed form.
        /// </summary>
        private static MappingSource BuildSource(string deviceGuid, string rawDescriptor, string deadZoneStr)
        {
            if (string.IsNullOrWhiteSpace(rawDescriptor) || rawDescriptor == "0") return null;

            string s = rawDescriptor.Trim();
            bool inverted = false;
            bool halfAxis = false;

            if (s.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
            {
                inverted = true;
                halfAxis = true;
                s = s.Substring(2);
            }
            else if (s.StartsWith("H", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
            {
                halfAxis = true;
                s = s.Substring(1);
            }
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(s))
            {
                inverted = true;
                s = s.Substring(1);
            }

            int dz = 50;
            if (!string.IsNullOrEmpty(deadZoneStr) &&
                int.TryParse(deadZoneStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                dz = parsed;
            }

            return new MappingSource
            {
                Kind = "Direct",
                DeviceGuid = deviceGuid ?? "",
                Descriptor = s,
                Invert = inverted,
                HalfAxis = halfAxis,
                DeadZone = dz,
            };
        }

        // ─────────────────────────────────────────────
        //  Motion-passthrough auto-row backfill
        // ─────────────────────────────────────────────

        /// <summary>
        /// Idempotent backfill: ensures every gyro / accel-capable device
        /// assigned to a Sony-class slot has its motion-passthrough source
        /// represented in the slot's <see cref="MappingSet"/>. Runs every
        /// load (both XML-content and legacy-migration branches) so the
        /// engine's motion path can stay rows-only with no first-wins
        /// fallback.
        ///
        /// <para>Non-Sony slots are no-ops — Xbox / Extended-gamepad /
        /// MIDI / KBM virtual controllers have no motion channel to
        /// relay. Devices without the relevant sensor capability are
        /// skipped per sub-channel.</para>
        ///
        /// <para>Idempotency: a device already present in a row's
        /// <c>Sources</c> for the same motion target is not re-added.
        /// Multiple devices on the same slot accumulate as multiple
        /// sources on the single per-target row, matching the existing
        /// multi-source pattern for buttons / axes / touchpad. The
        /// engine resolves at runtime by walking sources in order and
        /// picking the first online device (first-mapped-and-active wins).
        /// </para>
        /// </summary>
        public static void EnsureMotionRows(
            MappingSet ms,
            int slotType,
            IReadOnlyList<(string DeviceGuid, bool HasGyro, bool HasAccel)> devices)
        {
            if (ms == null || devices == null || devices.Count == 0) return;

            // Motion-capable slot families only. slotType integer encoding
            // matches the VirtualControllerType enum's underlying values
            // (PlayStation=1, Nintendo=5; the virtual Switch Pro gained a
            // real IMU surface in HIDMaestro v1.3.18, HM#33). Encoded as
            // int here so PadForge.Engine.Data doesn't need a
            // back-reference to PadForge.Engine for the enum type.
            if (slotType != 1 && slotType != 5) return;

            EnsureMotionRowForSensor(ms, MotionGyroTarget,  MotionGyroSourceDescriptor,
                devices, dev => dev.HasGyro);
            EnsureMotionRowForSensor(ms, MotionAccelTarget, MotionAccelSourceDescriptor,
                devices, dev => dev.HasAccel);
        }

        private static void EnsureMotionRowForSensor(
            MappingSet ms, string target, string descriptor,
            IReadOnlyList<(string DeviceGuid, bool HasGyro, bool HasAccel)> devices,
            Func<(string DeviceGuid, bool HasGyro, bool HasAccel), bool> capCheck)
        {
            // Find or create the target's row.
            MappingRow row = null;
            for (int i = 0; i < ms.Rows.Count; i++)
            {
                if (ms.Rows[i] != null && ms.Rows[i].Target == target)
                {
                    row = ms.Rows[i];
                    break;
                }
            }

            // Collect already-represented device guids for this target so we
            // don't double-add. Case-insensitive guid match (XML round-trips
            // can change case).
            HashSet<string> existing = null;
            if (row?.Sources != null)
            {
                existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var src in row.Sources)
                    if (src != null && !string.IsNullOrEmpty(src.DeviceGuid))
                        existing.Add(src.DeviceGuid);
            }

            // Build the additions list in input order.
            List<MappingSource> additions = null;
            foreach (var dev in devices)
            {
                if (string.IsNullOrEmpty(dev.DeviceGuid)) continue;
                if (!capCheck(dev)) continue;
                if (existing != null && existing.Contains(dev.DeviceGuid)) continue;
                (additions ??= new List<MappingSource>()).Add(new MappingSource
                {
                    Kind       = "Direct",
                    DeviceGuid = dev.DeviceGuid,
                    Descriptor = descriptor,
                });
            }

            if (additions == null) return;

            if (row == null)
            {
                ms.Rows.Add(new MappingRow
                {
                    Target      = target,
                    LayerMask   = "Base",
                    CombineMode = "",  // engine special-cases motion; combine ignored
                    Sources     = additions,
                });
            }
            else
            {
                row.Sources.AddRange(additions);
            }
        }
    }
}
