using System.Globalization;
using System.Text;
using PadForge.Engine.Data;
using PadForge.SteamWorkshop.Translation;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Renders a <see cref="TranslatedProfile"/> as a compact, deterministic,
    /// line-oriented text snapshot for the golden files. Deliberately NOT a
    /// general serializer: every emitted field is spelled explicitly so a
    /// translator change shows up as a reviewable one-line diff.
    /// </summary>
    internal static class GoldenProjection
    {
        public static string Render(TranslatedProfile p)
        {
            var sb = new StringBuilder(16 * 1024);
            sb.Append("name: ").Append(p.Name).Append('\n');
            if (!string.IsNullOrEmpty(p.Description))
            {
                string d = p.Description.Replace("\r", "").Replace("\n", " ");
                if (d.Length > 100) d = d.Substring(0, 100) + "...";
                sb.Append("description: ").Append(d).Append('\n');
            }

            sb.Append("slots:");
            if (p.NeedsXboxSlot) sb.Append(" xbox");
            if (p.NeedsKbmSlot) sb.Append(" kbm");
            if (!p.NeedsXboxSlot && !p.NeedsKbmSlot) sb.Append(" none");
            sb.Append('\n');

            RenderSet(sb, "xbox", p.XboxMappingSet);
            RenderSet(sb, "kbm", p.KbmMappingSet);

            sb.Append("macros: ").Append(p.Macros.Count).Append('\n');
            foreach (var m in p.Macros)
            {
                sb.Append("  macro ").Append(m.Action).Append(' ').Append(m.TriggerMode)
                  .Append(" | ").Append(m.Name)
                  .Append(" | btns=0x").Append(m.TriggerXboxButtons.ToString("X4", CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(m.TriggerAxisTarget))
                    sb.Append(" axis=").Append(m.TriggerAxisTarget)
                      .Append('@').Append(m.TriggerAxisThresholdPercent).Append('%');
                if (m.ConsumeTrigger) sb.Append(" consume");
                switch (m.Action)
                {
                    case TranslatedMacroAction.MoveMouseToScreenPosition:
                        sb.Append(" norm=(").Append(m.NormalizedX).Append(',').Append(m.NormalizedY).Append(')');
                        break;
                    case TranslatedMacroAction.RepeatKeyWhileHeld:
                        sb.Append(" vk=0x").Append(m.VirtualKey.ToString("X2", CultureInfo.InvariantCulture))
                          .Append(" interval=").Append(m.IntervalMs).Append("ms");
                        break;
                    case TranslatedMacroAction.KeyTap:
                        sb.Append(" vk=0x").Append(m.VirtualKey.ToString("X2", CultureInfo.InvariantCulture));
                        break;
                }
                sb.Append('\n');
            }

            var r = p.Report;
            sb.Append("report: ").Append(r.ToSummaryString()).Append('\n');
            foreach (var e in r.Entries)
            {
                sb.Append("  ").Append(e.Status).Append(' ')
                  .Append(ShortReason(e.ReasonKey));
                if (e.ReasonArgs.Count > 0)
                    sb.Append('(').Append(string.Join(", ", e.ReasonArgs)).Append(')');
                sb.Append(" @ ").Append(e.SourcePath);
                if (!string.IsNullOrEmpty(e.Emitted))
                    sb.Append(" => ").Append(e.Emitted);
                else if (!string.IsNullOrEmpty(e.Binding))
                    sb.Append(" :: ").Append(Truncate(e.Binding, 80));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static void RenderSet(StringBuilder sb, string label, MappingSet set)
        {
            sb.Append(label).Append(" rows: ").Append(set.Rows.Count).Append('\n');
            foreach (var row in set.Rows)
            {
                sb.Append("  ").Append(row.LayerMask).Append(" | ").Append(row.Target);
                if (!string.IsNullOrEmpty(row.CombineMode))
                    sb.Append(" | ").Append(row.CombineMode);
                sb.Append('\n');
                foreach (var s in row.Sources)
                    sb.Append("    <- ").Append(RenderSource(s)).Append('\n');
            }
            if (set.ShiftActivators.Count > 0)
            {
                sb.Append(label).Append(" activators: ").Append(set.ShiftActivators.Count).Append('\n');
                foreach (var a in set.ShiftActivators)
                {
                    sb.Append("  ").Append(a.Mode).Append(' ').Append(a.LayerMask);
                    if (!string.IsNullOrEmpty(a.JumpToLayer)) sb.Append(" -> ").Append(a.JumpToLayer);
                    if (!string.IsNullOrEmpty(a.CycleLayers))
                    {
                        sb.Append(" cycle=").Append(a.CycleLayers);
                        if (a.CycleIncludeBase) sb.Append("+Base");
                    }
                    sb.Append(" | ").Append(a.Descriptor);
                    if (a.Kind != "Button") sb.Append(" | kind=").Append(a.Kind);
                    if (a.InheritUnmapped) sb.Append(" | inherit");
                    if (!string.IsNullOrEmpty(a.LayerName)) sb.Append(" | name=").Append(a.LayerName);
                    sb.Append('\n');
                }
            }
        }

        private static string RenderSource(MappingSource s)
        {
            var sb = new StringBuilder();
            sb.Append(s.Descriptor);
            if (s.HalfAxis) sb.Append(s.Invert ? " [lower-half]" : " [upper-half]");
            else if (s.Invert) sb.Append(" [inverted]");
            if (s.Bidirectional) sb.Append(" [bidir]");
            if (s.DeadZone != 50) sb.Append(" dz=").Append(s.DeadZone);
            if (s.Sensitivity != 1.0)
                sb.Append(" sens=").Append(s.Sensitivity.ToString("0.###", CultureInfo.InvariantCulture));
            if (s.GyroSensitivity != 1.0)
                sb.Append(" gyroSens=").Append(s.GyroSensitivity.ToString("0.###", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(s.DeviceGuid)) sb.Append(" guid=").Append(s.DeviceGuid);
            return sb.ToString();
        }

        private static string ShortReason(string key)
            => key != null && key.StartsWith("Workshop_Tr_") ? key.Substring("Workshop_Tr_".Length) : key ?? "";

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
