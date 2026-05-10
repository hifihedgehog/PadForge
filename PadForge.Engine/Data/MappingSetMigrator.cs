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
            var ms = new MappingSet();
            if (devicesAndPadSettings == null || devicesAndPadSettings.Count == 0)
                return ms;

            // Button-class targets (single-source per device).
            foreach (var target in ButtonTargets)
                AppendSimpleRow(ms, target, devicesAndPadSettings);

            // Trigger targets (single-source per device, like buttons here —
            // bipolar pair is axis-only).
            AppendSimpleRow(ms, TriggerLeft,  devicesAndPadSettings);
            AppendSimpleRow(ms, TriggerRight, devicesAndPadSettings);

            // Bipolar axis targets: collapse primary + Neg fields into one
            // row with up-to-2 sources per device (negative source has
            // Invert flipped relative to descriptor's encoded inversion).
            foreach (var target in AxisTargets)
                AppendBipolarRow(ms, target, devicesAndPadSettings);

            // Combined DPad: emit only for devices whose individual DPad
            // direction fields are all empty AND DPad descriptor is non-empty.
            // Step 3's hasIndividualDPad check picks individuals first, so
            // any device with even one DPadUp/Down/Left/Right set will not
            // contribute its DPad descriptor here either.
            AppendCombinedDPadRow(ms, devicesAndPadSettings);

            return ms;
        }

        private static void AppendSimpleRow(
            MappingSet ms,
            string target,
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting)> devices)
        {
            var sources = new List<MappingSource>();
            foreach (var (guid, ps) in devices)
            {
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
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting)> devices)
        {
            var (primary, neg) = GetPairedFieldNames(target);
            var sources = new List<MappingSource>();

            foreach (var (guid, ps) in devices)
            {
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
                            // Negative source: flip Invert relative to the
                            // descriptor's encoded inversion. Net effect:
                            // pressed → -1 instead of +1 on a button source.
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
            IReadOnlyList<(string DeviceGuid, PadSetting PadSetting)> devices)
        {
            var sources = new List<MappingSource>();
            foreach (var (guid, ps) in devices)
            {
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
            else if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase) && s.Length > 1 && !char.IsDigit(s[1]))
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
    }
}
