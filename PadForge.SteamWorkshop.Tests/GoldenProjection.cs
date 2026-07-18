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

            // Slot-level workshop stamps (v18): non-default only, so
            // pre-v18 goldens stay byte-identical on this seam.
            if (!string.IsNullOrEmpty(p.LeftStickDeadZoneShape)
                || !string.IsNullOrEmpty(p.RightStickDeadZoneShape))
            {
                sb.Append("dzShape:");
                if (!string.IsNullOrEmpty(p.LeftStickDeadZoneShape))
                    sb.Append(" left=").Append(p.LeftStickDeadZoneShape);
                if (!string.IsNullOrEmpty(p.RightStickDeadZoneShape))
                    sb.Append(" right=").Append(p.RightStickDeadZoneShape);
                sb.Append('\n');
            }
            if (!string.IsNullOrEmpty(p.GyroEngageDescriptor))
            {
                sb.Append("gyroEngage: ").Append(p.GyroEngageDescriptor);
                if (p.GyroEngageInvert) sb.Append(" [inverted]");
                sb.Append('\n');
            }

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
                // Wave 3 device-free InputDevice trigger descriptors
                // (empty-guid entries, ANDed). Spelled out so the goldens
                // review every converted trigger shape.
                if (m.TriggerInputDescriptors != null && m.TriggerInputDescriptors.Count > 0)
                {
                    sb.Append(" in=[").Append(string.Join("; ", m.TriggerInputDescriptors)).Append(']');
                    // Wedge shape on the descriptor trigger (v12):
                    // non-default only, so goldens without half-axis hosts
                    // stay byte-identical.
                    if (m.TriggerDescriptorHalfAxis)
                    {
                        sb.Append(m.TriggerDescriptorInvert ? " in-half=lower" : " in-half=upper");
                        if (m.TriggerDescriptorDeadZonePercent > 0)
                            sb.Append(" in-dz=").Append(m.TriggerDescriptorDeadZonePercent);
                    }
                }
                if (m.TriggerHoldMs > 0)
                    sb.Append(" hold=").Append(m.TriggerHoldMs).Append("ms");
                // Double-press window (v17): non-default only, so pre-v17
                // goldens stay byte-identical on this seam.
                if (m.TriggerDoublePressMs > 0)
                    sb.Append(" dbl=").Append(m.TriggerDoublePressMs).Append("ms");
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
                    case TranslatedMacroAction.SetLightbarColor:
                        sb.Append(" rgb=(").Append(m.LedR).Append(',').Append(m.LedG)
                          .Append(',').Append(m.LedB).Append(')')
                          .Append(" bright=").Append(m.LedBrightnessPercent).Append('%')
                          .Append(" sat=").Append(m.LedSaturationPercent).Append('%')
                          .Append(" setting=").Append(m.LedSetting);
                        break;
                    case TranslatedMacroAction.RepeatVcButtonWhileHeld:
                        sb.Append(" target=0x").Append(m.TargetXboxButtons.ToString("X4", CultureInfo.InvariantCulture))
                          .Append(" interval=").Append(m.IntervalMs).Append("ms");
                        break;
                    case TranslatedMacroAction.ToggleVcButton:
                    case TranslatedMacroAction.HoldVcButton:
                        sb.Append(" target=0x").Append(m.TargetXboxButtons.ToString("X4", CultureInfo.InvariantCulture));
                        break;
                    case TranslatedMacroAction.ToggleKey:
                        sb.Append(" vk=0x").Append(m.VirtualKey.ToString("X2", CultureInfo.InvariantCulture));
                        break;
                    // v18 latch / turbo family.
                    case TranslatedMacroAction.ToggleMouseButton:
                        sb.Append(" mbtn=").Append(m.MouseButtonIndex);
                        break;
                    case TranslatedMacroAction.ToggleVcAxis:
                    case TranslatedMacroAction.RepeatVcAxisWhileHeld:
                        sb.Append(" targetAxis=").Append(m.TargetAxis);
                        if (m.TargetAxisNegative) sb.Append('-');
                        if (m.Action == TranslatedMacroAction.RepeatVcAxisWhileHeld)
                            sb.Append(" interval=").Append(m.IntervalMs).Append("ms");
                        break;
                    case TranslatedMacroAction.ToggleWheel:
                    // v19 (T1): the wheel turbo renders the same tick /
                    // interval shape as the wheel latch.
                    case TranslatedMacroAction.RepeatWheelWhileHeld:
                        sb.Append(" ticks=").Append(m.WheelTicks);
                        if (m.WheelHorizontal) sb.Append('H');
                        sb.Append(" interval=").Append(m.IntervalMs).Append("ms");
                        break;
                    case TranslatedMacroAction.MouseLimitRegion:
                        sb.Append(" region=(").Append(m.RegionXPercent).Append("%,")
                          .Append(m.RegionYPercent).Append("%,scale ")
                          .Append(m.RegionScalePercent).Append("%)");
                        break;
                    case TranslatedMacroAction.RumblePulse:
                        sb.Append(" strength=").Append(m.RumbleStrengthPercent).Append('%');
                        break;
                    case TranslatedMacroAction.MouseButtonTap:
                    case TranslatedMacroAction.HoldMouseButton:
                        sb.Append(" mbtn=").Append(m.MouseButtonIndex);
                        break;
                    case TranslatedMacroAction.VcButtonTap:
                        sb.Append(" target=0x").Append(m.TargetXboxButtons.ToString("X4", CultureInfo.InvariantCulture));
                        break;
                    case TranslatedMacroAction.HoldKey:
                        sb.Append(" vk=0x").Append(m.VirtualKey.ToString("X2", CultureInfo.InvariantCulture));
                        break;
                    case TranslatedMacroAction.MouseNudge:
                        sb.Append(" delta=(").Append(m.DeltaX).Append(',').Append(m.DeltaY).Append(')');
                        break;
                    case TranslatedMacroAction.CycleList:
                        sb.Append(" steps=[");
                        for (int i = 0; i < m.CycleSteps.Count; i++)
                        {
                            if (i > 0) sb.Append("; ");
                            var s = m.CycleSteps[i];
                            sb.Append(s.ItemIndex).Append(':');
                            switch (s.Kind)
                            {
                                case TranslatedCycleStepKind.KeyTap:
                                    sb.Append("K0x").Append(s.VirtualKey.ToString("X2", CultureInfo.InvariantCulture));
                                    break;
                                case TranslatedCycleStepKind.MouseButtonTap:
                                    sb.Append("M").Append(s.MouseButtonIndex);
                                    break;
                                case TranslatedCycleStepKind.WheelTap:
                                    sb.Append("W").Append(s.WheelTicks);
                                    if (s.WheelHorizontal) sb.Append('H');
                                    break;
                                case TranslatedCycleStepKind.VcButtonTap:
                                    sb.Append("B0x").Append(s.TargetXboxButtons.ToString("X4", CultureInfo.InvariantCulture));
                                    break;
                                case TranslatedCycleStepKind.VcAxisTap:
                                    sb.Append("A").Append(s.TargetAxis);
                                    if (s.TargetAxisNegative) sb.Append('-');
                                    break;
                            }
                        }
                        sb.Append(']');
                        if (!m.CycleWrap) sb.Append(" nowrap");
                        break;
                }
                // Activator fire delays (v10 G5): non-default only, so
                // pre-v10 goldens stay byte-identical on this seam.
                if (m.DelayStartMs > 0) sb.Append(" delayStart=").Append(m.DelayStartMs).Append("ms");
                if (m.DelayEndMs > 0) sb.Append(" delayEnd=").Append(m.DelayEndMs).Append("ms");
                // v18: the toggle + hold_repeats composite and the
                // release-extension tap length, non-default only.
                if (m.PulseWhileLatched) sb.Append(" pulse=").Append(m.IntervalMs).Append("ms");
                if (m.TapDurationMs > 0) sb.Append(" tap=").Append(m.TapDurationMs).Append("ms");
                sb.Append('\n');
            }

            // Menus (#9 B-17): structure only; the cell bindings ride the
            // rows / macros / activators above via "Menu {id} Item {k}"
            // sources, so every commit path is already reviewable there.
            if (p.Menus.Count > 0)
            {
                sb.Append("menus: ").Append(p.Menus.Count).Append('\n');
                foreach (var m in p.Menus)
                {
                    sb.Append("  menu ").Append(m.MenuId).Append(' ').Append(m.Kind)
                      .Append(" | ").Append(m.HostDescriptor);
                    if (m.HostHalf == 1) sb.Append(" [left half]");
                    else if (m.HostHalf == 2) sb.Append(" [right half]");
                    sb.Append(" | ").Append(string.IsNullOrEmpty(m.LayerMask) ? "Base" : m.LayerMask)
                      .Append(" | fire=").Append(m.FireType)
                      .Append(" cells=").Append(m.CellCount);
                    if (m.HasCenter) sb.Append("+center");
                    if (!m.ShowLabels) sb.Append(" labels=off");
                    if (m.PosXPercent != 50 || m.PosYPercent != 50)
                        sb.Append(" pos=(").Append(m.PosXPercent).Append("%,")
                          .Append(m.PosYPercent).Append("%)");
                    if (m.ScalePercent != 100) sb.Append(" scale=").Append(m.ScalePercent).Append('%');
                    if (m.OpacityPercent != 90) sb.Append(" opacity=").Append(m.OpacityPercent).Append('%');
                    if (m.EngageDeadzonePercent != 25) sb.Append(" dz=").Append(m.EngageDeadzonePercent);
                    if (!string.IsNullOrEmpty(m.Name)) sb.Append(" | name=").Append(m.Name);
                    sb.Append('\n');
                    foreach (var it in m.Items)
                    {
                        sb.Append("    [").Append(it.Index).Append("] ").Append(it.Label);
                        if (!string.IsNullOrEmpty(it.Icon))
                            sb.Append(" icon=").Append(it.Icon);
                        sb.Append('\n');
                    }
                }
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
                    else if (a.CycleIncludeBase)
                    {
                        // v20 single-set sentinel ring: an empty queue
                        // with Base as the only stop stays reviewable.
                        sb.Append(" cycle=Base");
                    }
                    sb.Append(" | ").Append(a.Descriptor);
                    if (!string.IsNullOrEmpty(a.ChordSecondDescriptor))
                        sb.Append(" & ").Append(a.ChordSecondDescriptor);
                    if (a.Kind != "Button") sb.Append(" | kind=").Append(a.Kind);
                    if (a.DelayMs > 0) sb.Append(" | delay=").Append(a.DelayMs).Append("ms");
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
            // Output-side polarity (the half-axis-safe flip: wheel rows,
            // v13 stick-direction rows). Non-default only.
            if (s.InvertOutput) sb.Append(" [out-inverted]");
            if (s.Bidirectional) sb.Append(" [bidir]");
            if (s.DeadZone != 50) sb.Append(" dz=").Append(s.DeadZone);
            if (s.Sensitivity != 1.0)
                sb.Append(" sens=").Append(s.Sensitivity.ToString("0.###", CultureInfo.InvariantCulture));
            if (s.GyroSensitivity != 1.0)
                sb.Append(" gyroSens=").Append(s.GyroSensitivity.ToString("0.###", CultureInfo.InvariantCulture));
            // Flick stick (#225): only the translator-carried knob; the rest
            // stay at their JSM defaults, so rendering them would add noise
            // to every source line. Non-default only, so pre-wave-4a goldens
            // are byte-identical.
            if (s.ParamFlickCountsPer360 != 14400)
                sb.Append(" flickDots=").Append(s.ParamFlickCountsPer360.ToString("0.###", CultureInfo.InvariantCulture));
            // Absolute pointer region window (#9 B-15): non-default only,
            // same byte-stability rule as the flick knob above.
            if (s.ParamPointerCenter != 0.5)
                sb.Append(" ptrCenter=").Append(s.ParamPointerCenter.ToString("0.###", CultureInfo.InvariantCulture));
            if (s.ParamPointerExtent != 1.0)
                sb.Append(" ptrExtent=").Append(s.ParamPointerExtent.ToString("0.###", CultureInfo.InvariantCulture));
            // Response curve / outer range channel (translator v11):
            // non-default only, same byte-stability rule.
            if (s.ParamCurveExponent != 0)
                sb.Append(" curve=").Append(s.ParamCurveExponent.ToString("0.###", CultureInfo.InvariantCulture));
            if (s.ParamRangeOuter != 0)
                sb.Append(" rangeOuter=").Append(s.ParamRangeOuter.ToString("0.###", CultureInfo.InvariantCulture));
            // v18 channels: the per-source AND gate, the anti-deadzone
            // floor, the flick easing override, and the mouse-feel knobs.
            // Non-default only, same byte-stability rule.
            if (!string.IsNullOrEmpty(s.GateDescriptor))
                sb.Append(" gate=[").Append(s.GateDescriptor).Append(']');
            if (s.ParamAntiDeadzone != 0)
                sb.Append(" anti=").Append(s.ParamAntiDeadzone.ToString("0.###", CultureInfo.InvariantCulture));
            if (s.ParamFlickTime != 0.1)
                sb.Append(" flickTime=").Append(s.ParamFlickTime.ToString("0.###", CultureInfo.InvariantCulture));
            if (s.ParamSmoothingAlpha != 0)
                sb.Append(" smooth=").Append(s.ParamSmoothingAlpha.ToString("0.###", CultureInfo.InvariantCulture));
            if (s.ParamAccel != 0)
                sb.Append(" accel=").Append(s.ParamAccel.ToString("0.###", CultureInfo.InvariantCulture));
            if (s.ParamMoveThreshold != 0)
                sb.Append(" moveThresh=").Append(s.ParamMoveThreshold.ToString("0.###", CultureInfo.InvariantCulture));
            if (s.ParamTrackballDecay != 0)
                sb.Append(" trackball=").Append(s.ParamTrackballDecay.ToString("0.####", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(s.DeviceGuid)) sb.Append(" guid=").Append(s.DeviceGuid);
            return sb.ToString();
        }

        private static string ShortReason(string key)
            => key != null && key.StartsWith("Workshop_Tr_") ? key.Substring("Workshop_Tr_".Length) : key ?? "";

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
