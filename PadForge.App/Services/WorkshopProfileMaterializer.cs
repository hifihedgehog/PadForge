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
                // Slot-level workshop stamps (v18): Steam's deadzone_shape
                // rides the Xbox set (the thumb pairs it shapes live
                // there); the runtime overlays it onto the resolved
                // per-device tuning, the gesture auto-arm contract.
                mappingSets[xboxSlot].WorkshopLeftStickDeadZoneShape =
                    translated.LeftStickDeadZoneShape ?? "";
                mappingSets[xboxSlot].WorkshopRightStickDeadZoneShape =
                    translated.RightStickDeadZoneShape ?? "";
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
            // The gyro engage stamp (v18, Steam gyro_button) rides EVERY
            // claimed slot: split configs host gyro mouse rows on the KbM
            // slot and gyro stick rows on the Xbox slot, and the engage
            // gate is per slot.
            if (!string.IsNullOrEmpty(translated.GyroEngageDescriptor))
            {
                if (xboxSlot >= 0)
                {
                    mappingSets[xboxSlot].WorkshopGyroEngageDescriptor = translated.GyroEngageDescriptor;
                    mappingSets[xboxSlot].WorkshopGyroEngageInvert = translated.GyroEngageInvert;
                }
                if (kbmSlot >= 0)
                {
                    mappingSets[kbmSlot].WorkshopGyroEngageDescriptor = translated.GyroEngageDescriptor;
                    mappingSets[kbmSlot].WorkshopGyroEngageInvert = translated.GyroEngageInvert;
                }
            }
            // The gyro ratchet stamp (v22, Steam gyro_ratchet_button_mask)
            // rides every claimed slot like the engage stamp above: the
            // clutch gate is per slot and split configs host gyro rows on
            // both sides.
            if (translated.GyroRatchetDescriptors != null && translated.GyroRatchetDescriptors.Count > 0)
            {
                string ratchet = string.Join("|", translated.GyroRatchetDescriptors);
                if (xboxSlot >= 0) mappingSets[xboxSlot].WorkshopGyroRatchetDescriptors = ratchet;
                if (kbmSlot >= 0) mappingSets[kbmSlot].WorkshopGyroRatchetDescriptors = ratchet;
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
            int pairSeq = 0; // nonzero PairId per hold pair, unique in-profile
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
                if (m?.Action == TranslatedMacroAction.HoldKey
                    || m?.Action == TranslatedMacroAction.HoldMouseButton)
                {
                    // Held key / mouse button (v10 G10/G11, relatched
                    // audit #2 M4): a press leg that SETs the key /
                    // button latch, plus an OnRelease twin that CLEARs it
                    // through the shared PairId.
                    list.AddRange(BuildHoldPair(m, xboxSlot, ++pairSeq));
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
                    PulseWhileLatched = m.PulseWhileLatched,
                    IntervalMs = m.IntervalMs > 0 ? m.IntervalMs : 100,
                },
                TranslatedMacroAction.ToggleKey => new ActionData
                {
                    Type = MacroActionType.ToggleKey,
                    KeyCode = m.VirtualKey,
                    PulseWhileLatched = m.PulseWhileLatched,
                    IntervalMs = m.IntervalMs > 0 ? m.IntervalMs : 100,
                },
                TranslatedMacroAction.GyroRecenter => new ActionData
                {
                    Type = MacroActionType.GyroRecenter,
                },
                // RumblePulse (v10 G1): one reactive rumble hit, both motors
                // at the level-scaled strength. Hold/fade ride the ActionData
                // defaults (100 ms hold + 200 ms fade), the macro Rumble
                // action's one-shot pulse shape.
                TranslatedMacroAction.RumblePulse => new ActionData
                {
                    Type = MacroActionType.Rumble,
                    RumbleHoldMode = ViewModels.MacroRumbleHoldMode.Reactive,
                    RumbleStrengthLeft = Math.Clamp(m.RumbleStrengthPercent, 1, 100),
                    RumbleStrengthRight = Math.Clamp(m.RumbleStrengthPercent, 1, 100),
                },
                // MouseButtonTap (v10 G6): MouseButtonPress is down +
                // DurationMs + up, one click.
                TranslatedMacroAction.MouseButtonTap => new ActionData
                {
                    Type = MacroActionType.MouseButtonPress,
                    MouseButton = (ViewModels.MacroMouseButton)Math.Clamp(m.MouseButtonIndex, 0, 4),
                },
                // VcButtonTap (v10 G6): ButtonPress ORs the target for
                // DurationMs, one tap. TapDurationMs (v18) overrides the
                // default length for the delay_end release-extension twins.
                TranslatedMacroAction.VcButtonTap => new ActionData
                {
                    Type = MacroActionType.ButtonPress,
                    ButtonFlags = m.TargetXboxButtons,
                    DurationMs = m.TapDurationMs > 0 ? m.TapDurationMs : 50,
                },
                // SHOW_KEYBOARD (v10 G7): launch the Windows touch keyboard,
                // falling back to the classic osk.exe when TabTip is absent.
                TranslatedMacroAction.ShowOnScreenKeyboard => new ActionData
                {
                    Type = MacroActionType.RunProgram,
                    ProgramPath = ResolveOnScreenKeyboardPath(),
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
                // VcAxisTap / HoldVcAxis (v15): AxisHold asserts the axis
                // value every frame while current. The tap form runs one
                // default-duration assert; the hold form takes the
                // HoldVcButton repeat shape below.
                TranslatedMacroAction.VcAxisTap => BuildAxisHoldAction(m),
                TranslatedMacroAction.HoldVcAxis => BuildAxisHoldAction(m),
                // MouseWheelTap (v15): one discrete wheel detent per fire.
                TranslatedMacroAction.MouseWheelTap => new ActionData
                {
                    Type = MacroActionType.MouseWheelTap,
                    AxisValue = (short)Math.Clamp(m.WheelTicks, short.MinValue, short.MaxValue),
                    WheelHorizontal = m.WheelHorizontal,
                },
                // MouseNudge (v16): one fixed-pixel cursor nudge per fire
                // (Steam's mouse_delta "Move by Amount"). The authored
                // values are already SendInput screen-frame pixels, so
                // they pass through unscaled and unclamped (negative
                // deltas are the point).
                TranslatedMacroAction.MouseNudge => new ActionData
                {
                    Type = MacroActionType.MouseNudge,
                    NudgeDx = m.DeltaX,
                    NudgeDy = m.DeltaY,
                },
                // CycleList (v16): Steam's Scroll Wheel List as the
                // engine's CycleTapList, steps encoded into the CSV
                // vocabulary (same-item bindings fold into one '+'-joined
                // stop so they fire together).
                TranslatedMacroAction.CycleList => BuildCycleTapListAction(m),
                // v18 latch family: mouse-button / VC-axis / wheel latches
                // plus the axis turbo, with the toggle + hold_repeats
                // composite riding PulseWhileLatched.
                TranslatedMacroAction.ToggleMouseButton => new ActionData
                {
                    Type = MacroActionType.ToggleMouseButton,
                    MouseButton = (ViewModels.MacroMouseButton)Math.Clamp(m.MouseButtonIndex, 0, 4),
                    PulseWhileLatched = m.PulseWhileLatched,
                    IntervalMs = m.IntervalMs > 0 ? m.IntervalMs : 100,
                },
                TranslatedMacroAction.ToggleVcAxis => BuildAxisLatchAction(m,
                    MacroActionType.ToggleVcAxis),
                TranslatedMacroAction.RepeatVcAxisWhileHeld => BuildAxisLatchAction(m,
                    MacroActionType.RepeatVcAxisWhileHeld),
                TranslatedMacroAction.ToggleWheel => new ActionData
                {
                    Type = MacroActionType.ToggleWheel,
                    AxisValue = (short)Math.Clamp(m.WheelTicks, short.MinValue, short.MaxValue),
                    WheelHorizontal = m.WheelHorizontal,
                    IntervalMs = m.IntervalMs > 0 ? m.IntervalMs : 100,
                },
                // RepeatWheelWhileHeld (v19, T1): one MouseWheelTap detent
                // per authored repeat_rate while the trigger is held. The
                // tap is a one-shot, so the cadence rides the macro repeat
                // machinery below (UntilRelease + RepeatDelayMs).
                TranslatedMacroAction.RepeatWheelWhileHeld => new ActionData
                {
                    Type = MacroActionType.MouseWheelTap,
                    AxisValue = (short)Math.Clamp(m.WheelTicks, short.MinValue, short.MaxValue),
                    WheelHorizontal = m.WheelHorizontal,
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
            if (mode == MacroTriggerMode.DoublePress)
                data.TriggerDoublePressMs = Math.Clamp(m.TriggerDoublePressMs, 50, 5000); // MacroItem clamp range

            // Continuous actions (autofire pulses) run for as long as the
            // macro executes; only RepeatMode=UntilRelease stops execution
            // when the trigger releases (Step4b stops UntilRelease macros on
            // !triggerActive; a WhileHeld + Once macro whose actions are all
            // continuous would keep pulsing forever after release).
            if (m.Action == TranslatedMacroAction.RepeatKeyWhileHeld
                || m.Action == TranslatedMacroAction.RepeatVcButtonWhileHeld
                // v19 (M2): the axis turbo is a continuous action too
                // (Step4b pulses it while executing), so it needs the same
                // release stop or it keeps pulsing forever after the
                // trigger releases.
                || m.Action == TranslatedMacroAction.RepeatVcAxisWhileHeld)
            {
                data.RepeatMode = MacroRepeatMode.UntilRelease;
            }
            else if (m.Action == TranslatedMacroAction.HoldVcButton
                || m.Action == TranslatedMacroAction.HoldVcAxis)
            {
                // Restart the one-action sequence every frame with no gap so
                // ButtonPress / AxisHold re-writes the output continuously
                // until the release stops the macro.
                data.RepeatMode = MacroRepeatMode.UntilRelease;
                data.RepeatDelayMs = 0;
            }
            else if (m.Action == TranslatedMacroAction.RepeatWheelWhileHeld)
            {
                // v19 (T1): re-run the one-shot detent every authored
                // interval until the trigger releases, Steam's
                // hold_repeats cadence on a wheel binding.
                data.RepeatMode = MacroRepeatMode.UntilRelease;
                data.RepeatDelayMs = Math.Clamp(m.IntervalMs > 0 ? m.IntervalMs : 100, 10, 1000);
            }

            // Autofire delay_end (v22): the translator stamps DelayEndMs
            // on the UntilRelease pulse shapes as the release linger (the
            // pulse train keeps running that long past the release, and a
            // re-press cancels the pending stop). A Delay STEP could not
            // carry it: the stop leg is the trigger release, not a
            // sequence position. The VC hold pairs never land here (their
            // delay_end rides the release-extension twin instead).
            if (data.RepeatMode == MacroRepeatMode.UntilRelease
                && m.DelayEndMs > 0
                && (m.Action == TranslatedMacroAction.RepeatKeyWhileHeld
                    || m.Action == TranslatedMacroAction.RepeatVcButtonWhileHeld
                    || m.Action == TranslatedMacroAction.RepeatVcAxisWhileHeld
                    || m.Action == TranslatedMacroAction.RepeatWheelWhileHeld))
            {
                data.ReleaseLingerMs = m.DelayEndMs;
            }

            // Press-leg tap extension (v22): delay_end on a press-fired
            // tap deactivates the output late, so the assert grows to the
            // authored length (KeyPress / MouseButtonPress executors are
            // down + DurationMs + up; VcButtonTap and the AxisHold taps
            // wire TapDurationMs in their own builders).
            if (m.TapDurationMs > 0
                && (action.Type == MacroActionType.KeyPress
                    || action.Type == MacroActionType.MouseButtonPress))
            {
                action.DurationMs = m.TapDurationMs;
            }

            // Activator fire delays (v10 G5): a Delay step before the
            // action. OnRelease-triggered macros are the release leg and
            // take delay_end; everything else takes delay_start. The
            // translator stamps these on one-shot shapes only.
            int preDelayMs = mode == MacroTriggerMode.OnRelease ? m.DelayEndMs : m.DelayStartMs;
            if (preDelayMs > 0)
            {
                data.Actions = new[]
                {
                    new ActionData { Type = MacroActionType.Delay, DurationMs = preDelayMs },
                    action,
                };
            }

            return data;
        }

        /// <summary>Lowers a translated HoldKey / HoldMouseButton to the
        /// engine pair (v10 G10/G11, relatched audit #2 M4): the press
        /// leg fires on the translated trigger (OnPress or HoldForMs) and
        /// SETs the ToggleKey / ToggleMouseButton latch, so the held key
        /// rides the per-frame reconcile and its engine-stop /
        /// profile-switch release paths instead of a raw KeyPress Down
        /// those paths cannot see; the OnRelease twin CLEARs the latch
        /// through the shared PairId, and its start cancels the twin's
        /// pending delayed release (M6). RepeatMode=UntilRelease on the
        /// press leg stops a delay_start leg on early release before the
        /// Set fires. Clearing an unset latch is a no-op, so short taps
        /// (below a Long_Press threshold) stay harmless. Activator fire
        /// delays ride the pair naturally: delay_start before the press
        /// leg, delay_end before the release leg (Steam's shifted-window
        /// semantics).</summary>
        private static MacroData[] BuildHoldPair(TranslatedMacro m, int xboxSlot, int pairId)
        {
            bool key = m.Action == TranslatedMacroAction.HoldKey;
            if (!Enum.TryParse(m.TriggerMode, out MacroTriggerMode pressMode))
                pressMode = MacroTriggerMode.OnPress;
            var mouseButton = (ViewModels.MacroMouseButton)Math.Clamp(m.MouseButtonIndex, 0, 4);

            MacroData Build(MacroTriggerMode mode, ActionData action, int preDelayMs,
                string suffix, bool untilRelease)
            {
                var data = new MacroData
                {
                    PadIndex = xboxSlot,
                    Name = $"{(string.IsNullOrWhiteSpace(m.Name) ? "Workshop Macro" : m.Name)} {suffix}",
                    IsEnabled = true,
                    TriggerSource = MacroTriggerSource.OutputController,
                    TriggerMode = mode,
                    TriggerButtons = m.TriggerXboxButtons,
                    // Never consume: both legs read the same trigger, and a
                    // consumed bit would release the twin early.
                    ConsumeTriggerButtons = false,
                    PairId = pairId,
                    TriggerAxisTargets = string.IsNullOrEmpty(m.TriggerAxisTarget) ? null : m.TriggerAxisTarget,
                    TriggerAxisThreshold = Math.Clamp(m.TriggerAxisThresholdPercent, 1, 100),
                    Actions = preDelayMs > 0
                        ? new[]
                        {
                            new ActionData { Type = MacroActionType.Delay, DurationMs = preDelayMs },
                            action,
                        }
                        : new[] { action },
                };
                if (mode == MacroTriggerMode.HoldForMs)
                    data.TriggerHoldMs = Math.Clamp(m.TriggerHoldMs, 50, 10000); // MacroItem clamp range
                if (mode == MacroTriggerMode.DoublePress)
                    data.TriggerDoublePressMs = Math.Clamp(m.TriggerDoublePressMs, 50, 5000); // MacroItem clamp range
                if (untilRelease)
                    data.RepeatMode = MacroRepeatMode.UntilRelease;
                return ApplyDeviceFreeTrigger(data, m) ? data : null;
            }

            var press = Build(pressMode,
                key
                    ? new ActionData
                    {
                        Type = MacroActionType.ToggleKey,
                        KeyCode = m.VirtualKey,
                        LatchDirection = ViewModels.MacroLatchDirection.On,
                    }
                    : new ActionData
                    {
                        Type = MacroActionType.ToggleMouseButton,
                        MouseButton = mouseButton,
                        LatchDirection = ViewModels.MacroLatchDirection.On,
                    },
                m.DelayStartMs, "(hold)", untilRelease: true);
            var release = Build(MacroTriggerMode.OnRelease,
                key
                    ? new ActionData
                    {
                        Type = MacroActionType.ToggleKey,
                        KeyCode = m.VirtualKey,
                        LatchDirection = ViewModels.MacroLatchDirection.Off,
                    }
                    : new ActionData
                    {
                        Type = MacroActionType.ToggleMouseButton,
                        MouseButton = mouseButton,
                        LatchDirection = ViewModels.MacroLatchDirection.Off,
                    },
                m.DelayEndMs, "(release)", untilRelease: false);
            if (press == null || release == null) return Array.Empty<MacroData>();
            return new[] { press, release };
        }

        /// <summary>Lowers a VcAxisTap / HoldVcAxis payload (v15) to the
        /// AxisHold action. Trigger targets assert a FULL pull (32767 on
        /// AxisHold's 0..32767 pull scale). Stick targets convert the
        /// translator's SDL row frame (+Y down, up / left = negative via
        /// TargetAxisNegative) into the XInput thumb frame the executor
        /// writes: X keeps its sign, Y negates (XInput up = positive).
        /// Unknown axis names return null and the macro is dropped, the
        /// same contract as an unconvertible trigger descriptor.</summary>
        private static ActionData BuildAxisHoldAction(TranslatedMacro m)
        {
            var payload = MapAxisTarget(m.TargetAxis, m.TargetAxisNegative);
            if (payload == null) return null;
            var data = new ActionData
            {
                Type = MacroActionType.AxisHold,
                AxisTarget = payload.Value.Target,
                AxisValue = payload.Value.Value,
            };
            // v18: the delay_end release-extension twins carry an explicit
            // assert duration.
            if (m.TapDurationMs > 0) data.DurationMs = m.TapDurationMs;
            return data;
        }

        /// <summary>Axis latch / turbo lowering (v18): the AxisHold payload
        /// mapping with the latch pulse composite carried through.</summary>
        private static ActionData BuildAxisLatchAction(TranslatedMacro m, MacroActionType type)
        {
            var payload = MapAxisTarget(m.TargetAxis, m.TargetAxisNegative);
            if (payload == null) return null;
            return new ActionData
            {
                Type = type,
                AxisTarget = payload.Value.Target,
                AxisValue = payload.Value.Value,
                PulseWhileLatched = m.PulseWhileLatched,
                IntervalMs = m.IntervalMs > 0 ? m.IntervalMs : 100,
            };
        }

        /// <summary>Translator SDL row frame to the XInput macro axis frame
        /// (v15, shared by the v16 cycle steps): trigger targets assert a
        /// full pull on AxisHold's 0..32767 pull scale. Stick X keeps its
        /// sign and stick Y negates (XInput up = positive). Unknown names
        /// return null.</summary>
        private static (MacroAxisTarget Target, short Value)? MapAxisTarget(string targetAxis, bool neg)
            => targetAxis switch
            {
                "LeftTrigger" => (MacroAxisTarget.LeftTrigger, (short)32767),
                "RightTrigger" => (MacroAxisTarget.RightTrigger, (short)32767),
                "LeftThumbAxisX" => (MacroAxisTarget.LeftStickX, neg ? (short)-32768 : (short)32767),
                "RightThumbAxisX" => (MacroAxisTarget.RightStickX, neg ? (short)-32768 : (short)32767),
                // SDL-frame up (negative) is XInput +32767; down is -32768.
                "LeftThumbAxisY" => (MacroAxisTarget.LeftStickY, neg ? (short)32767 : (short)-32768),
                "RightThumbAxisY" => (MacroAxisTarget.RightStickY, neg ? (short)32767 : (short)-32768),
                _ => null,
            };

        /// <summary>Lowers a CycleList payload (v16) to the CycleTapList
        /// action: each TranslatedCycleStep becomes one CSV part, steps
        /// sharing an ItemIndex fold into one '+'-joined stop (Steam fires
        /// everything on the reached item together). Unencodable steps
        /// (an axis name outside the table) are dropped part-wise, and a list
        /// with no encodable step drops the macro, the null contract.</summary>
        private static ActionData BuildCycleTapListAction(TranslatedMacro m)
        {
            if (m.CycleSteps == null || m.CycleSteps.Count == 0) return null;
            var stops = new List<string>();
            string current = null;
            int currentIndex = int.MinValue;
            foreach (var step in m.CycleSteps)
            {
                string part = step.Kind switch
                {
                    TranslatedCycleStepKind.KeyTap => $"K:{step.VirtualKey}",
                    TranslatedCycleStepKind.MouseButtonTap => $"M:{Math.Clamp(step.MouseButtonIndex, 0, 4)}",
                    TranslatedCycleStepKind.WheelTap => step.WheelHorizontal
                        ? $"H:{step.WheelTicks}"
                        : $"W:{step.WheelTicks}",
                    TranslatedCycleStepKind.VcButtonTap => $"B:{step.TargetXboxButtons}",
                    TranslatedCycleStepKind.VcAxisTap =>
                        MapAxisTarget(step.TargetAxis, step.TargetAxisNegative) is { } axis
                            ? $"A:{(int)axis.Target}:{axis.Value}"
                            : null,
                    _ => null,
                };
                if (part == null) continue;
                if (step.ItemIndex == currentIndex && current != null)
                {
                    current = $"{current}+{part}";
                }
                else
                {
                    if (current != null) stops.Add(current);
                    current = part;
                    currentIndex = step.ItemIndex;
                }
            }
            if (current != null) stops.Add(current);
            if (stops.Count == 0) return null;
            return new ActionData
            {
                Type = MacroActionType.CycleTapList,
                CycleStepsCsv = string.Join(",", stops),
                CycleWrap = m.CycleWrap,
            };
        }

        /// <summary>The Windows touch keyboard when present, else the
        /// classic on-screen keyboard (v10 G7). Resolved at import time so
        /// the RunProgram action carries a concrete path.</summary>
        private static string ResolveOnScreenKeyboardPath()
        {
            string tabTip = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                "microsoft shared", "ink", "TabTip.exe");
            return System.IO.File.Exists(tabTip) ? tabTip : "osk.exe";
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
                var clamp = new ActionData
                {
                    Type = MacroActionType.MouseLimitRegion,
                    CursorClampMode = ViewModels.CursorClampMode.XAndY,
                    CursorClampInsetX = insetX,
                    CursorClampInsetY = insetY,
                };
                // Activator delays (v18): the engage leg waits
                // delay_start after the press, the release leg waits
                // delay_end after the release, Steam's shifted window.
                int legDelay = mode == MacroTriggerMode.OnRelease ? m.DelayEndMs : m.DelayStartMs;
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
                    Actions = legDelay > 0
                        ? new[]
                        {
                            new ActionData { Type = MacroActionType.Delay, DurationMs = legDelay },
                            clamp,
                        }
                        : new[] { clamp },
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
                // Axis-shaped entries read the FULL axis by default. A
                // wedge-hosted trigger (v12: a stick-as-dpad member, a
                // trigger-pull click) carries the hosting read's half-axis
                // shape on the translated macro, so the entry keeps the
                // wedge's direction and threshold instead of firing on any
                // deflection of the whole axis.
                if (entry.AxisTarget != MacroAxisTarget.None && m.TriggerDescriptorHalfAxis)
                {
                    entry.HalfAxis = true;
                    entry.Invert = m.TriggerDescriptorInvert;
                    if (m.TriggerDescriptorDeadZonePercent > 0)
                        entry.DeadZone = m.TriggerDescriptorDeadZonePercent;
                }
                // Descriptor-shaped entries (v15: a gyro-hosted swipe's
                // signed rate read) take the same half stamp on the entry's
                // cached MappingSource, so the engine's half-aware reads
                // fire on ONE direction. The deadzone stamp rides
                // DescriptorDeadZone (0 keeps the engine default, e.g. the
                // gyro read's 30 deg/s rate threshold).
                else if (!string.IsNullOrEmpty(entry.SourceDescriptor)
                    && m.TriggerDescriptorHalfAxis)
                {
                    entry.HalfAxis = true;
                    entry.Invert = m.TriggerDescriptorInvert;
                    if (m.TriggerDescriptorDeadZonePercent > 0)
                        entry.DescriptorDeadZone = m.TriggerDescriptorDeadZonePercent;
                }
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
