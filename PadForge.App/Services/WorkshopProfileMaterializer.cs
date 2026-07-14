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

            // Menus (#9 B-17) ride EVERY claimed slot's MappingSet: split
            // configs feed both slots from the same physical device, the
            // menu runtime and the fired-set provider are slot-keyed (like
            // the gesture engine), and each slot's rows read their own
            // slot's fires, so both slots need their own copy. The overlay
            // publisher dedupes at display time (first engaged menu wins).
            if (translated.Menus != null && translated.Menus.Count > 0)
            {
                foreach (var menu in translated.Menus)
                {
                    if (menu == null) continue;
                    if (xboxSlot >= 0) mappingSets[xboxSlot].Menus.Add(menu.Clone());
                    if (kbmSlot >= 0) mappingSets[kbmSlot].Menus.Add(menu.Clone());
                }
            }

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

        /// <summary>Lowers the translated macro list into profile DTOs.
        /// Returns EMPTY, never null, when the config carries no macros (or
        /// none survive translation). On ProfileData, null Macros is the
        /// legacy sentinel meaning "saved before macros rode profiles, leave
        /// the live set alone". A Workshop import is an authored profile that
        /// owns its state outright, so it must clear the outgoing profile's
        /// macros rather than inherit them.</summary>
        private static MacroData[] BuildMacros(List<TranslatedMacro> macros, int xboxSlot,
            string controllerType)
        {
            if (macros == null || macros.Count == 0) return Array.Empty<MacroData>();
            var list = new List<MacroData>(macros.Count);
            foreach (var m in macros)
            {
                if (m?.Action == TranslatedMacroAction.MouseLimitRegion)
                {
                    // The translator's "WhileHeld" clamp is semantic; the
                    // engine clamp is a toggle primitive (#110), so it lowers
                    // to an engage-on-press / release-on-release pair.
                    list.AddRange(BuildRegionClampPair(m, xboxSlot));
                    continue;
                }
                var data = BuildMacro(m, xboxSlot, controllerType);
                if (data != null) list.Add(data);
            }
            return list.ToArray();   // empty, not null: see the sentinel note above
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
                TranslatedMacroAction.RepeatVcButtonWhileHeld => new ActionData
                {
                    Type = MacroActionType.RepeatVcButtonWhileHeld,
                    ButtonFlags = m.TargetXboxButtons,
                    IntervalMs = m.IntervalMs > 0 ? m.IntervalMs : 100,
                },
                TranslatedMacroAction.ToggleVcButton => new ActionData
                {
                    Type = MacroActionType.ToggleVcButton,
                    ButtonFlags = m.TargetXboxButtons,
                },
                TranslatedMacroAction.ToggleKey => new ActionData
                {
                    Type = MacroActionType.ToggleKey,
                    KeyCode = m.VirtualKey,
                },
                TranslatedMacroAction.GyroRecenter => new ActionData
                {
                    Type = MacroActionType.GyroRecenter,
                },
                // HoldVcButton: ButtonPress ORs its flags into the combined
                // output every frame while it is the current action, and the
                // UntilRelease + RepeatDelayMs=0 shape below restarts the
                // sequence each frame, so the button stays down from the
                // HoldForMs threshold until the physical release (Steam's
                // documented Long_Press behavior).
                TranslatedMacroAction.HoldVcButton => new ActionData
                {
                    Type = MacroActionType.ButtonPress,
                    ButtonFlags = m.TargetXboxButtons,
                },
                _ => null,
            };
            if (action == null) return null;

            if (!Enum.TryParse(m.TriggerMode, out MacroTriggerMode mode))
                mode = MacroTriggerMode.OnPress;

            var data = new MacroData
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

            if (!ApplyDeviceFreeTrigger(data, m)) return null;

            if (mode == MacroTriggerMode.HoldForMs)
                data.TriggerHoldMs = Math.Clamp(m.TriggerHoldMs, 50, 10000); // MacroItem clamp range

            // Continuous actions (autofire pulses) run for as long as the
            // macro executes; only RepeatMode=UntilRelease stops execution
            // when the trigger releases (Step4b stops UntilRelease macros on
            // !triggerActive; a WhileHeld + Once macro whose actions are all
            // continuous would keep pulsing forever after release).
            if (m.Action == TranslatedMacroAction.RepeatKeyWhileHeld
                || m.Action == TranslatedMacroAction.RepeatVcButtonWhileHeld)
            {
                data.RepeatMode = MacroRepeatMode.UntilRelease;
            }
            else if (m.Action == TranslatedMacroAction.HoldVcButton)
            {
                // Restart the one-action sequence every frame with no gap so
                // ButtonPress re-writes the button continuously until the
                // release stops the macro.
                data.RepeatMode = MacroRepeatMode.UntilRelease;
                data.RepeatDelayMs = 0;
            }

            return data;
        }

        /// <summary>Lowers a translated mouse_region clamp to the engine's
        /// toggle primitive (#110): one macro engages the clamp on the
        /// trigger's press, its twin releases it on the release, so the
        /// region is held exactly while the hosting input is active. Region
        /// geometry (center position_x/position_y percent, size scale
        /// percent, Steam's shipped configurator units) folds into the
        /// centered per-edge insets the clamp supports; an off-center region
        /// clamps at the same size around the screen center (the translator
        /// already reported the approximation Partial). Trackpad hosts
        /// carry a device-free InputDevice trigger (wave 3): both pair
        /// members get the same descriptor entries, engaging on the touch
        /// edge and releasing on the lift.</summary>
        private static MacroData[] BuildRegionClampPair(TranslatedMacro m, int xboxSlot)
        {
            int scale = Math.Clamp(m.RegionScalePercent, 1, 100);
            int insetX = RegionInsetPixels(scale, GetSystemMetrics(SM_CXSCREEN));
            int insetY = RegionInsetPixels(scale, GetSystemMetrics(SM_CYSCREEN));

            MacroData Build(MacroTriggerMode mode, string suffix)
            {
                var data = new MacroData
                {
                    PadIndex = xboxSlot,
                    Name = $"{(string.IsNullOrWhiteSpace(m.Name) ? "Cursor region" : m.Name)} {suffix}",
                    IsEnabled = true,
                    TriggerSource = MacroTriggerSource.OutputController,
                    TriggerMode = mode,
                    TriggerButtons = m.TriggerXboxButtons,
                    ConsumeTriggerButtons = false,
                    TriggerAxisTargets = string.IsNullOrEmpty(m.TriggerAxisTarget) ? null : m.TriggerAxisTarget,
                    TriggerAxisThreshold = Math.Clamp(m.TriggerAxisThresholdPercent, 1, 100),
                    Actions = new[]
                    {
                        new ActionData
                        {
                            Type = MacroActionType.MouseLimitRegion,
                            CursorClampMode = ViewModels.CursorClampMode.XAndY,
                            CursorClampInsetX = insetX,
                            CursorClampInsetY = insetY,
                        },
                    },
                };
                return ApplyDeviceFreeTrigger(data, m) ? data : null;
            }

            var engage = Build(MacroTriggerMode.OnPress, "(engage)");
            var release = Build(MacroTriggerMode.OnRelease, "(release)");
            if (engage == null || release == null) return Array.Empty<MacroData>();
            return new[] { engage, release };
        }

        /// <summary>Applies a translated macro's device-free InputDevice
        /// trigger (wave 3), when it carries one: every descriptor converts
        /// through the exact picker path (<see cref="MacroItem.TryBuildTriggerEntry"/>
        /// from an "(Any device)" choice with the empty guid), the specs
        /// pipe-join into <see cref="MacroData.TriggerInputs"/>, and the
        /// combined-output fields zero out. Multiple entries AND together
        /// per the trigger evaluator's contract. Returns false when any
        /// descriptor fails to convert (the trigger would be incomplete
        /// and fire too easily, so the caller drops the macro); by
        /// construction the translator only emits convertible descriptors.
        /// Consume is forced off: an input-device trigger has no output
        /// bits to consume.</summary>
        private static bool ApplyDeviceFreeTrigger(MacroData data, TranslatedMacro m)
        {
            var descriptors = m.TriggerInputDescriptors;
            if (descriptors == null || descriptors.Count == 0) return true;

            var specs = new List<string>(descriptors.Count);
            foreach (var descriptor in descriptors)
            {
                var choice = new ViewModels.InputChoice
                {
                    Descriptor = descriptor,
                    DeviceGuid = string.Empty,
                };
                if (!MacroItem.TryBuildTriggerEntry(choice, out var entry)) return false;
                string spec = entry.Spec;
                if (string.IsNullOrEmpty(spec)) return false;
                specs.Add(spec);
            }

            data.TriggerSource = MacroTriggerSource.InputDevice;
            data.TriggerInputs = string.Join("|", specs);
            data.TriggerButtons = 0;
            data.TriggerAxisTargets = null;
            data.ConsumeTriggerButtons = false;
            return true;
        }

        /// <summary>Per-edge clamp inset for a centered region covering
        /// <paramref name="scalePercent"/> of <paramref name="screenSize"/>.</summary>
        private static int RegionInsetPixels(int scalePercent, int screenSize)
        {
            if (screenSize <= 0) return 0;
            return (int)Math.Clamp(Math.Round(screenSize * (100 - scalePercent) / 200.0),
                0, screenSize / 2);
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
