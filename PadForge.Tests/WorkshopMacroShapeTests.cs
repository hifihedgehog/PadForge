using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Wave 2A end-to-end shape proofs: the exact MacroData the Workshop
    /// materializer emits, loaded through the REAL settings loader
    /// (SettingsService.LoadMacroFromData) and driven through the REAL slot
    /// evaluator (InputManager.EvaluateSlotMacros). These pin the load-bearing
    /// dispatch claims behind the lowerings: a HoldVcButton macro holds the
    /// target from the HoldForMs threshold until the physical release with no
    /// gaps, and a WhileHeld turbo stops pulsing when the trigger releases.
    /// Only VC-button actions are driven (key actions would SendInput real
    /// keystrokes into the test host).
    /// </summary>
    public class WorkshopMacroShapeTests
    {
        private static MacroItem Load(TranslatedMacro translated)
        {
            var t = new TranslatedProfile { Name = "Shape", NeedsXboxSlot = true };
            t.Macros.Add(translated);
            var profile = WorkshopProfileMaterializer.Materialize(t);
            var data = Assert.Single(profile.Macros);
            return SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
        }

        [Fact]
        public void HoldVcButton_HoldsTargetFromThresholdUntilRelease_NoGaps()
        {
            var im = new InputManager();
            var macro = Load(new TranslatedMacro
            {
                Name = "Long press ButtonY (button_a)",
                Action = TranslatedMacroAction.HoldVcButton,
                TriggerMode = "HoldForMs",
                TriggerHoldMs = 500,
                TriggerXboxButtons = Gamepad.A,
                TargetXboxButtons = Gamepad.Y,
                ConsumeTrigger = true,
            });
            var macros = new[] { macro };

            // Below the threshold: armed, nothing written, trigger not consumed.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons & Gamepad.Y);
            Assert.Equal(Gamepad.A, (ushort)(gp.Buttons & Gamepad.A));

            // Cross the threshold (injected hold-start): the target engages
            // and the trigger's own identity is consumed (the Steam
            // interruptable-pause approximation).
            macro.TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-600);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(Gamepad.Y, (ushort)(gp.Buttons & Gamepad.Y));
            Assert.Equal(0, gp.Buttons & Gamepad.A);

            // Still held, ButtonPress mid-duration: still down.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(Gamepad.Y, (ushort)(gp.Buttons & Gamepad.Y));

            // Past the ButtonPress duration: UntilRelease + RepeatDelayMs=0
            // restarts the sequence the same frame, so the hold has no gap.
            macro.ActionStartTime = DateTime.UtcNow.AddMilliseconds(-200);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(Gamepad.Y, (ushort)(gp.Buttons & Gamepad.Y));
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(Gamepad.Y, (ushort)(gp.Buttons & Gamepad.Y));

            // Physical release: the macro stops and the target releases.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons);
            Assert.False(macro.IsExecuting);

            // A short tap afterwards never re-fires below the threshold.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons & Gamepad.Y);
        }

        [Fact]
        public void VcTurbo_MaterializedShape_PulsesAndStopsOnRelease()
        {
            var im = new InputManager();
            var macro = Load(new TranslatedMacro
            {
                Name = "Turbo ButtonB (button_a)",
                Action = TranslatedMacroAction.RepeatVcButtonWhileHeld,
                TriggerMode = "WhileHeld",
                TriggerXboxButtons = Gamepad.A,
                TargetXboxButtons = Gamepad.B,
                IntervalMs = 100,
            });
            var macros = new[] { macro };
            var action = Assert.Single(macro.Actions);

            // Held: the pulse starts ON.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(Gamepad.B, (ushort)(gp.Buttons & Gamepad.B));

            // Half-interval later (injected): OFF phase.
            action.RepeatVcLastToggleUtc = DateTime.UtcNow.AddMilliseconds(-60);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons & Gamepad.B);

            // Release: UntilRelease stops the macro (the wave-2A RepeatMode
            // fix; with Once it would pulse forever).
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons);
            Assert.False(macro.IsExecuting);
        }

        [Fact]
        public void ToggleVcButton_MaterializedShape_LatchesOnTargetEdge()
        {
            // The unified toggle structure: the momentary row asserts the
            // target while the physical input is held, and the latch macro
            // triggers on that same target bit. The evaluator applies
            // latches AFTER trigger reads, so the latch never masks its own
            // press edge.
            var im = new InputManager();
            var macro = Load(new TranslatedMacro
            {
                Name = "Toggle ButtonB (click)",
                Action = TranslatedMacroAction.ToggleVcButton,
                TriggerMode = "OnPress",
                TriggerXboxButtons = Gamepad.B, // the target bit, fed by the kept row
                TargetXboxButtons = Gamepad.B,
            });
            var macros = new[] { macro };
            var action = Assert.Single(macro.Actions);

            // Press 1 (the row asserts B): latch engages.
            var gp = new Gamepad { Buttons = Gamepad.B };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(action.VcToggleLatched);

            // Released: the latch alone keeps B down.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(Gamepad.B, (ushort)(gp.Buttons & Gamepad.B));

            // Press 2 (row asserts B again -> fresh press edge): unlatches.
            gp = new Gamepad { Buttons = Gamepad.B };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(action.VcToggleLatched);

            // Idle: nothing held.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons);
        }
    }
}
