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
        private const int XboxSlot = 0;
        private const int KbmSlot = 1;

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

            var slotCreated = new bool[maxPads];
            slotCreated[XboxSlot] = true;
            slotCreated[KbmSlot] = true;

            var slotEnabled = new bool[maxPads];
            slotEnabled[XboxSlot] = true;
            slotEnabled[KbmSlot] = true;

            var slotTypes = new int[maxPads];
            slotTypes[XboxSlot] = (int)VirtualControllerType.Xbox;
            slotTypes[KbmSlot] = (int)VirtualControllerType.KeyboardMouse;

            var slotProfileIds = new string[maxPads];
            slotProfileIds[XboxSlot] = InputManager.GetDefaultProfileId(VirtualControllerType.Xbox);
            slotProfileIds[KbmSlot] = InputManager.GetDefaultProfileId(VirtualControllerType.KeyboardMouse);

            var mappingSets = new MappingSet[maxPads];
            mappingSets[XboxSlot] = translated.XboxMappingSet ?? new MappingSet();
            mappingSets[KbmSlot] = translated.KbmMappingSet ?? new MappingSet();
            for (int i = 0; i < maxPads; i++)
                mappingSets[i] ??= new MappingSet();

            var macros = BuildMacros(translated.Macros);

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

        private static MacroData[] BuildMacros(List<TranslatedMacro> macros)
        {
            if (macros == null || macros.Count == 0) return null;
            var list = new List<MacroData>(macros.Count);
            foreach (var m in macros)
            {
                var data = BuildMacro(m);
                if (data != null) list.Add(data);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static MacroData BuildMacro(TranslatedMacro m)
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
                PadIndex = XboxSlot,
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
