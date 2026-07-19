using System;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Translator v22 engine pins: the two release-linger lanes
    /// (Steam's activator delay_end). UntilRelease macros keep executing
    /// ReleaseLingerMs past the trigger release with a re-press cancelling
    /// the pending stop (the M6 cancel-on-re-press shape on the pulse stop
    /// leg), Hold-mode shift activators keep their layer engaged
    /// ReleaseDelayMs past the release the same way, and the materializer
    /// wires the translated DelayEndMs / TapDurationMs channels through
    /// the DTOs.</summary>
    public class ReleaseLingerTests
    {
        // ── Macro release linger (Step4b, Gamepad path) ──

        private static MacroItem TurboMacro(MacroAction action)
        {
            var m = new MacroItem
            {
                Name = "V22",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.WhileHeld,
                RepeatMode = MacroRepeatMode.UntilRelease,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(action);
            return m;
        }

        [Fact]
        public void Turbo_ReleaseLinger_KeepsExecutingPastRelease_RepressCancels()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                ButtonFlags = Gamepad.B,
                IntervalMs = 100,
            };
            var macro = TurboMacro(action);
            macro.ReleaseLingerMs = 30000; // wide window: no timing race
            var macros = new[] { macro };

            // Held: executing.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(macro.IsExecuting);

            // Released inside the window: the pulse train keeps running
            // and the pending stop is armed.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(macro.IsExecuting);
            Assert.NotEqual(DateTime.MinValue, macro.ReleaseLingerStartUtc);

            // Re-press: the pending stop is cancelled (M6 shape).
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(macro.IsExecuting);
            Assert.Equal(DateTime.MinValue, macro.ReleaseLingerStartUtc);

            // Release again, then expire the window (injected clock): stops.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(macro.IsExecuting);
            macro.ReleaseLingerStartUtc = DateTime.UtcNow.AddMilliseconds(-30001);
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(macro.IsExecuting);
            Assert.Equal(DateTime.MinValue, macro.ReleaseLingerStartUtc);
        }

        [Fact]
        public void Turbo_WithoutLinger_StillStopsAtRelease()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                ButtonFlags = Gamepad.B,
                IntervalMs = 100,
            };
            var macros = new[] { TurboMacro(action) };

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(macros[0].IsExecuting);

            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(macros[0].IsExecuting);
        }

        // ── Layer release linger (Step3 Hold mode) ──

        private static MappingSet HoldLayerSet(int releaseDelayMs)
        {
            var ms = new MappingSet();
            var row = new MappingRow { Target = "RawBtn60", LayerMask = "View" };
            row.Sources.Add(new MappingSource { Descriptor = "Button 16" });
            ms.Rows.Add(row);
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = "Button 28",
                Mode = "Hold",
                LayerMask = "View",
                LayerName = "View",
                Kind = "Button",
                DelayMs = 0,
                ReleaseDelayMs = releaseDelayMs,
            });
            return ms;
        }

        [Fact]
        public void HoldLayer_ReleaseDelay_LingersPastRelease_AndRepressContinues()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 2;
            const string guid = "";
            var ms = HoldLayerSet(releaseDelayMs: 30000); // wide window

            var state = new CustomInputState();
            state.Buttons[28] = true;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            // Released: the layer lingers.
            state.Buttons[28] = false;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            // Re-press inside the window: still engaged, window renewed.
            state.Buttons[28] = true;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            InputManager.ClearAllShiftRuntime();
        }

        [Fact]
        public void HoldLayer_ReleaseDelay_DisengagesAfterTheWindow()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 3;
            const string guid = "";
            var ms = HoldLayerSet(releaseDelayMs: 40);

            var state = new CustomInputState();
            state.Buttons[28] = true;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            state.Buttons[28] = false;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));
            Thread.Sleep(90); // let the 40 ms window lapse
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            InputManager.ClearAllShiftRuntime();
        }

        [Fact]
        public void HoldLayer_WithoutReleaseDelay_DisengagesImmediately()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 4;
            const string guid = "";
            var ms = HoldLayerSet(releaseDelayMs: 0);

            var state = new CustomInputState();
            state.Buttons[28] = true;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));
            state.Buttons[28] = false;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            InputManager.ClearAllShiftRuntime();
        }

        // ── Materializer wiring ──

        private static TranslatedProfile XboxProfile()
            => new() { Name = "V22", NeedsXboxSlot = true };

        [Fact]
        public void Materialize_AutofireDelayEnd_BecomesReleaseLinger()
        {
            var t = XboxProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Turbo E (button_a)",
                Action = TranslatedMacroAction.RepeatKeyWhileHeld,
                TriggerMode = "WhileHeld",
                TriggerXboxButtons = Gamepad.A,
                VirtualKey = 0x45,
                IntervalMs = 150,
                DelayStartMs = 90,
                DelayEndMs = 400,
            });
            var p = WorkshopProfileMaterializer.Materialize(t);
            var m = Assert.Single(p.Macros);
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
            Assert.Equal(400, m.ReleaseLingerMs);
            // delay_start stays the one-shot Delay step before the
            // continuous action.
            Assert.Equal(2, m.Actions.Length);
            Assert.Equal(MacroActionType.Delay, m.Actions[0].Type);
            Assert.Equal(90, m.Actions[0].DurationMs);
        }

        [Fact]
        public void Materialize_WheelTurboDelays_HoldThresholdPlusLinger()
        {
            var t = XboxProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Turbo wheel (button_a)",
                Action = TranslatedMacroAction.RepeatWheelWhileHeld,
                TriggerMode = "HoldForMs",
                TriggerHoldMs = 120,
                TriggerXboxButtons = Gamepad.A,
                WheelTicks = -1,
                IntervalMs = 100,
                DelayEndMs = 250,
            });
            var p = WorkshopProfileMaterializer.Materialize(t);
            var m = Assert.Single(p.Macros);
            Assert.Equal(MacroTriggerMode.HoldForMs, m.TriggerMode);
            Assert.Equal(120, m.TriggerHoldMs);
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
            Assert.Equal(100, m.RepeatDelayMs);
            Assert.Equal(250, m.ReleaseLingerMs);
            // No Delay step: it would re-run inside every detent iteration.
            var a = Assert.Single(m.Actions);
            Assert.Equal(MacroActionType.MouseWheelTap, a.Type);
        }

        [Fact]
        public void Materialize_PressLegKeyTap_TapDurationRidesKeyPress()
        {
            var t = XboxProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Tap E (dpad_north)",
                Action = TranslatedMacroAction.KeyTap,
                TriggerMode = "OnPress",
                TriggerXboxButtons = Gamepad.A,
                VirtualKey = 0x45,
                TapDurationMs = 220,
            });
            var p = WorkshopProfileMaterializer.Materialize(t);
            var a = Assert.Single(Assert.Single(p.Macros).Actions);
            Assert.Equal(MacroActionType.KeyPress, a.Type);
            Assert.Equal(220, a.DurationMs);
        }

        [Fact]
        public void ReleaseLingerMs_RoundTripsThroughTheDtos()
        {
            var m = new MacroItem { Name = "RT", ReleaseLingerMs = 400 };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                ButtonFlags = Gamepad.B,
            });
            var md = SettingsService.BuildMacroDataForMacro(m, 0);
            Assert.Equal(400, md.ReleaseLingerMs);
            var clone = SettingsService.LoadMacroFromData(md, VirtualControllerType.Xbox, null);
            Assert.Equal(400, clone.ReleaseLingerMs);
        }

        [Fact]
        public void ShiftActivator_Clone_CarriesReleaseDelay()
        {
            var act = new ShiftActivator { Mode = "Hold", ReleaseDelayMs = 350 };
            Assert.Equal(350, act.Clone().ReleaseDelayMs);
        }

        // ── Gyro ratchet stamp plumbing ──

        [Fact]
        public void Materialize_GyroRatchet_StampsEveryClaimedSlot()
        {
            var t = new TranslatedProfile { Name = "V22", NeedsXboxSlot = true, NeedsKbmSlot = true };
            t.GyroRatchetDescriptors.Add("Gamepad Paddle4");
            t.GyroRatchetDescriptors.Add("Touchpad 1 Finger 0 Down");
            var p = WorkshopProfileMaterializer.Materialize(t);
            var stamped = p.SlotMappingSets
                .Where(s => s != null && !string.IsNullOrEmpty(s.WorkshopGyroRatchetDescriptors))
                .ToList();
            Assert.Equal(2, stamped.Count);
            Assert.All(stamped, s =>
            {
                Assert.Equal("Gamepad Paddle4|Touchpad 1 Finger 0 Down", s.WorkshopGyroRatchetDescriptors);
                Assert.Equal(new[] { "Gamepad Paddle4", "Touchpad 1 Finger 0 Down" }, s.WorkshopGyroRatchetList);
            });
        }

        [Fact]
        public void CopyWorkshopStamps_CarriesTheRatchetDescriptors()
        {
            var src = new MappingSet { WorkshopGyroRatchetDescriptors = "Gamepad Paddle4" };
            var dst = new MappingSet();
            src.CopyWorkshopStampsTo(dst);
            Assert.Equal("Gamepad Paddle4", dst.WorkshopGyroRatchetDescriptors);
            Assert.Equal(new[] { "Gamepad Paddle4" }, dst.WorkshopGyroRatchetList);
        }
    }
}
