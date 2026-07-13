using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Turns the translator's neutral <see cref="TranslatedProfile"/> into a
    /// real <see cref="ProfileData"/> (#9). The library half stays out of the
    /// WPF exe project, so the App-owned shapes are assembled here: the
    /// two pre-allocated VC slots (Xbox at 0, keyboard/mouse at 1, split
    /// configs are the norm), the per-slot MappingSets, and MacroData built
    /// from the device-independent macro descriptions. Device assignments are
    /// deliberately left empty: abstract Gamepad descriptors resolve on
    /// whichever pad the user maps into the slot.
    /// </summary>
    public static class WorkshopProfileMaterializer
    {
        /// <summary>
        /// Materializes the profile. When <paramref name="source"/> is given
        /// (the browse dialog passes the Workshop item's identity), it is
        /// stamped onto the profile as provenance, with the import-time facts
        /// (ImportedAt, the report digest) filled in here so the dialog only
        /// supplies what it already holds.
        /// </summary>
        public static ProfileData Materialize(TranslatedProfile translated, SteamWorkshopSource source = null)
        {
            if (translated == null) throw new ArgumentNullException(nameof(translated));

            if (source != null)
            {
                source.ImportedAt = DateTime.UtcNow;
                source.TranslationSummary = translated.Report?.ToSummaryString();
            }

            int maxPads = InputManager.MaxPads;

            // Only the slots the translation actually binds exist on the
            // imported profile (owner report 2026-07-13: a keyboard-only
            // config imported with an empty Xbox VC). The Xbox slot, when
            // needed, always sits first so the macro triggers reading its
            // combined output keep their index.
            int nextSlot = 0;
            int xboxSlot = translated.NeedsXboxSlot ? nextSlot++ : -1;
            int kbmSlot = translated.NeedsKbmSlot ? nextSlot++ : -1;

            var slotCreated = new bool[maxPads];
            var slotEnabled = new bool[maxPads];
            var slotTypes = new int[maxPads];
            var slotProfileIds = new string[maxPads];
            var mappingSets = new MappingSet[maxPads];

            if (xboxSlot >= 0)
            {
                slotCreated[xboxSlot] = true;
                slotEnabled[xboxSlot] = true;
                slotTypes[xboxSlot] = (int)VirtualControllerType.Xbox;
                slotProfileIds[xboxSlot] = InputManager.GetDefaultProfileId(VirtualControllerType.Xbox);
                mappingSets[xboxSlot] = translated.XboxMappingSet ?? new MappingSet();
                // The translator spells out every binding, automap-identical
                // ones included, so the legacy-automap merge must not add to
                // this set when the user assigns a device.
                mappingSets[xboxSlot].Authoritative = true;
            }
            if (kbmSlot >= 0)
            {
                slotCreated[kbmSlot] = true;
                slotEnabled[kbmSlot] = true;
                slotTypes[kbmSlot] = (int)VirtualControllerType.KeyboardMouse;
                slotProfileIds[kbmSlot] = InputManager.GetDefaultProfileId(VirtualControllerType.KeyboardMouse);
                mappingSets[kbmSlot] = translated.KbmMappingSet ?? new MappingSet();
                mappingSets[kbmSlot].Authoritative = true;
            }
            // Unclaimed slots stay non-authoritative on purpose: a slot the
            // user creates later must automap normally.
            for (int i = 0; i < maxPads; i++)
                mappingSets[i] ??= new MappingSet();

            var macros = BuildMacros(translated.Macros, Math.Max(xboxSlot, 0),
                translated.Report?.ControllerType);

            return new ProfileData
            {
                Name = string.IsNullOrWhiteSpace(translated.Name) ? "Workshop Profile" : translated.Name,
                SlotMappingSets = mappingSets,
                SlotCreated = slotCreated,
                SlotEnabled = slotEnabled,
                SlotControllerTypes = slotTypes,
                SlotProfileIds = slotProfileIds,
                Macros = macros,
                WorkshopSource = source,
            };
        }

        private static MacroData[] BuildMacros(List<TranslatedMacro> macros, int xboxSlot,
            string controllerType)
        {
            if (macros == null || macros.Count == 0) return null;
            var list = new List<MacroData>(macros.Count);
            foreach (var m in macros)
            {
                var data = BuildMacro(m, xboxSlot, controllerType);
                if (data != null) list.Add(data);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static MacroData BuildMacro(TranslatedMacro m, int xboxSlot, string controllerType)
        {
            if (m == null) return null;

            ActionData action = m.Action switch
            {
                TranslatedMacroAction.MoveMouseToScreenPosition => new ActionData
                {
                    Type = MacroActionType.MoveMouseToScreenPosition,
                    MouseX = NormalizedToPixels(m.NormalizedX, GetSystemMetrics(SM_CXSCREEN)),
                    MouseY = NormalizedToPixels(m.NormalizedY, GetSystemMetrics(SM_CYSCREEN)),
                },
                TranslatedMacroAction.RepeatKeyWhileHeld => new ActionData
                {
                    Type = MacroActionType.RepeatKeyWhileHeld,
                    KeyCode = m.VirtualKey,
                    IntervalMs = m.IntervalMs > 0 ? m.IntervalMs : 100,
                },
                TranslatedMacroAction.KeyTap => new ActionData
                {
                    // KeyPress is down + DurationMs hold + up: one tap.
                    Type = MacroActionType.KeyPress,
                    KeyCode = m.VirtualKey,
                },
                TranslatedMacroAction.SetLightbarColor => BuildSetLedAction(m, controllerType),
                _ => null,
            };
            if (action == null) return null;

            if (!Enum.TryParse(m.TriggerMode, out MacroTriggerMode mode))
                mode = MacroTriggerMode.OnPress;

            return new MacroData
            {
                PadIndex = xboxSlot,
                Name = string.IsNullOrWhiteSpace(m.Name) ? "Workshop Macro" : m.Name,
                IsEnabled = true,
                TriggerSource = MacroTriggerSource.OutputController,
                TriggerMode = mode,
                TriggerButtons = m.TriggerXboxButtons,
                ConsumeTriggerButtons = m.ConsumeTrigger,
                TriggerAxisTargets = string.IsNullOrEmpty(m.TriggerAxisTarget) ? null : m.TriggerAxisTarget,
                TriggerAxisThreshold = Math.Clamp(m.TriggerAxisThresholdPercent, 1, 100),
                Actions = new[] { action },
            };
        }

        /// <summary>Maps a translated set_led macro onto the existing
        /// lighting actions. Steam Controller configs drive the Guide/Home
        /// LED (the pad has no lightbar): GuideLedBrightness at the
        /// binding's brightness percent. Everything else drives the
        /// lightbar: setting 1 holds the fixed color (Sticky) with
        /// brightness and saturation folded into the RGB via HSV, setting
        /// 0 clears the override, and setting 2 ("restore default") is
        /// approximated as a clear (the translator already reported that
        /// Partial).</summary>
        private static ActionData BuildSetLedAction(TranslatedMacro m, string controllerType)
        {
            if ((controllerType ?? "").IndexOf("steamcontroller", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new ActionData
                {
                    Type = MacroActionType.GuideLedBrightness,
                    GuideLedPercent = Math.Clamp(m.LedBrightnessPercent, 0, 100),
                };
            }

            if (m.LedSetting != 1)
                return new ActionData { Type = MacroActionType.LightbarColorClear };

            var (r, g, b) = FoldSaturationBrightness(m.LedR, m.LedG, m.LedB,
                m.LedSaturationPercent, m.LedBrightnessPercent);
            return new ActionData
            {
                Type = MacroActionType.LightbarColor,
                LightbarHoldMode = ViewModels.MacroLightbarHoldMode.Sticky,
                LightbarColorSource = ViewModels.MacroLightbarColorSource.Fixed,
                LightbarR = r,
                LightbarG = g,
                LightbarB = b,
            };
        }

        /// <summary>Folds set_led's saturation and brightness percents into
        /// the RGB triplet via HSV (S and V scale multiplicatively), since
        /// the Sticky lightbar hold carries a plain RGB.</summary>
        private static (byte R, byte G, byte B) FoldSaturationBrightness(
            int r, int g, int b, int satPct, int brightPct)
        {
            double rf = Math.Clamp(r, 0, 255) / 255.0;
            double gf = Math.Clamp(g, 0, 255) / 255.0;
            double bf = Math.Clamp(b, 0, 255) / 255.0;

            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            double delta = max - min;

            double h = 0;
            if (delta > 0)
            {
                if (max == rf) h = 60.0 * (((gf - bf) / delta) % 6.0);
                else if (max == gf) h = 60.0 * (((bf - rf) / delta) + 2.0);
                else h = 60.0 * (((rf - gf) / delta) + 4.0);
                if (h < 0) h += 360.0;
            }
            double s = max <= 0 ? 0 : delta / max;
            double v = max;

            s *= Math.Clamp(satPct, 0, 100) / 100.0;
            v *= Math.Clamp(brightPct, 0, 100) / 100.0;

            double c = v * s;
            double x = c * (1.0 - Math.Abs(h / 60.0 % 2.0 - 1.0));
            double m2 = v - c;
            (double r2, double g2, double b2) = h switch
            {
                < 60.0 => (c, x, 0.0),
                < 120.0 => (x, c, 0.0),
                < 180.0 => (0.0, c, x),
                < 240.0 => (0.0, x, c),
                < 300.0 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            return (
                (byte)Math.Clamp(Math.Round((r2 + m2) * 255.0), 0, 255),
                (byte)Math.Clamp(Math.Round((g2 + m2) * 255.0), 0, 255),
                (byte)Math.Clamp(Math.Round((b2 + m2) * 255.0), 0, 255));
        }

        /// <summary>Steam's MOUSE_POSITION coordinates are normalized 0..65535;
        /// the cursor-warp action wants primary-monitor physical pixels
        /// (CursorControlService.MoveCursorTo is a straight SetCursorPos).</summary>
        private static int NormalizedToPixels(int normalized, int screenSize)
        {
            if (screenSize <= 0) return 0;
            double clamped = Math.Clamp(normalized, 0, 65535) / 65535.0;
            return (int)Math.Clamp(Math.Round(clamped * (screenSize - 1)), 0, screenSize - 1);
        }

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}
