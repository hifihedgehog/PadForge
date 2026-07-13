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

            var macros = BuildMacros(translated.Macros, Math.Max(xboxSlot, 0));

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

        private static MacroData[] BuildMacros(List<TranslatedMacro> macros, int xboxSlot)
        {
            if (macros == null || macros.Count == 0) return null;
            var list = new List<MacroData>(macros.Count);
            foreach (var m in macros)
            {
                var data = BuildMacro(m, xboxSlot);
                if (data != null) list.Add(data);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static MacroData BuildMacro(TranslatedMacro m, int xboxSlot)
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
