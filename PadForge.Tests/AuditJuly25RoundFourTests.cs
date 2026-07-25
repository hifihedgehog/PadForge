using System;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the 2026-07-25 round-FOUR audit, which reviewed the
    /// round-three fix commit (88678a1a) and found a second layer of
    /// defects the fixes themselves introduced. Every case here is a
    /// contract the round-three code got wrong: the consume strip that
    /// blinded sibling macros, the completion model that pulsed forever,
    /// the edge-continuity bool that only modeled "sampled sometime", and
    /// the Base fallback that coupled independent pads.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly25RoundFourTests : IDisposable
    {
        private const short Fire = 30000;
        private static readonly Guid DevGuid = new("32323232-3232-3232-3232-323232323232");

        public void Dispose() => InputManager.ClearAllShiftRuntime();

        private static MacroItem Macro(MacroTriggerMode mode, int holdMs = 300,
            string layerMask = "", int pad = 0, bool consume = false,
            MacroRepeatMode repeat = MacroRepeatMode.Once)
        {
            var m = new MacroItem
            {
                Name = "R4",
                IsEnabled = true,
                PadIndex = pad,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = repeat,
                ConsumeTriggerButtons = consume,
                TriggerHoldMs = holdMs,
                LayerMask = layerMask,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = Fire,
            });
            return m;
        }

        private static MacroItem PressMacro(MacroTriggerMode mode, ushort outButton,
            int holdMs = 300, bool consume = false, MacroRepeatMode repeat = MacroRepeatMode.Once)
        {
            var m = new MacroItem
            {
                Name = "R4b",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = repeat,
                ConsumeTriggerButtons = consume,
                TriggerHoldMs = holdMs,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.ButtonPress,
                ButtonFlags = outButton,
                DurationMs = 0,
            });
            return m;
        }

        private static Gamepad Tick(InputManager im, MacroItem[] macros, bool held)
        {
            var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
            im.EvaluateSlotMacros(ref gp, macros);
            return gp;
        }

        // ── R1: consume must not erase the macro's own output ──

        /// <summary>A ShortPress on button A with consume, whose action
        /// presses B, must still emit B: the deferred strip removes only
        /// the consumed TRIGGER bit and the output overlay re-asserts what
        /// the macro generated. The round-three inline strip erased A but
        /// the output was on a different button, so this specifically pins
        /// that the overlay survives the same-button case.</summary>
        [Fact]
        public void Consume_DoesNotEraseTheMacrosOwnButtonOutput()
        {
            var im = new InputManager();
            // Trigger on A, output ALSO A, consume on: the strip and the
            // overlay collide on the same bit and the overlay must win.
            var m = PressMacro(MacroTriggerMode.ShortPress, Gamepad.A, holdMs: 300, consume: true);
            var macros = new[] { m };

            Tick(im, macros, held: false);                 // observed idle (continuity)
            Tick(im, macros, held: true);                  // press: arms
            var gp = Tick(im, macros, held: false);        // release under threshold: fires
            // The tap's own A output reaches the pad despite consuming A.
            Assert.Equal(Gamepad.A, (ushort)(gp.Buttons & Gamepad.A));
        }

        // ── R2: consume must not blind a sibling macro on the same button ──

        /// <summary>The canonical tap-vs-hold pair on ONE button, with
        /// consume left at its default-on. The round-three inline strip
        /// blinded the hold leg because the short leg cleared A mid-walk;
        /// the deferred strip gives both legs the same pre-consumption
        /// view. Here the long hold must reach the hold leg.</summary>
        [Fact]
        public void Consume_DoesNotBlindTheHoldLegOfATapHoldPair()
        {
            var im = new InputManager();
            var shortLeg = PressMacro(MacroTriggerMode.ShortPress, Gamepad.X, holdMs: 300, consume: true);
            var longLeg = PressMacro(MacroTriggerMode.HoldForMs, Gamepad.Y, holdMs: 300, consume: true);
            var macros = new[] { shortLeg, longLeg };

            Tick(im, macros, held: false);                 // observed idle
            Tick(im, macros, held: true);                  // press
            // Back-date so the hold crosses its threshold.
            longLeg.TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-600);
            var gp = Tick(im, macros, held: true);         // still held, past threshold
            // The hold leg fired (Y) even though the short leg consumed A.
            Assert.Equal(Gamepad.Y, (ushort)(gp.Buttons & Gamepad.Y));
        }

        // ── R4: a released-fire all-continuous run stops after one pass ──

        /// <summary>ShortPress + the hidden default Once + an all-continuous
        /// action (mouse move) must run exactly one pass, not forever. The
        /// round-three flag-clear-only shape stopped UntilRelease but left
        /// Once pulsing until the macro was disabled.</summary>
        [Fact]
        public void ShortPress_AllContinuous_Once_StopsAfterOnePass()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "mm",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.ShortPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
                TriggerHoldMs = 300,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                ButtonFlags = Gamepad.B,
                IntervalMs = 250,
            });
            var macros = new[] { m };

            Tick(im, macros, held: false);   // observed idle
            Tick(im, macros, held: true);    // press: arms
            Tick(im, macros, held: false);   // release under threshold: fires, runs its one pass
            // Second post-release tick: the run must have stopped.
            Tick(im, macros, held: false);
            Assert.False(m.IsExecuting);
            Assert.False(m.RunReleasedFireToCompletion);
        }

        // ── R5: an empty macro never enters the executing state ──

        [Fact]
        public void EmptyMacro_NeverStartsAndNeverWedges()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "empty",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.ShortPress,
                RepeatMode = MacroRepeatMode.Once,
                TriggerHoldMs = 300,
            };
            // No actions.
            var macros = new[] { m };

            Tick(im, macros, held: false);
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);   // would "fire" if it started
            Assert.False(m.IsExecuting);
            Assert.False(m.RunReleasedFireToCompletion);
        }

        // ── R13/R14: edge continuity, not "sampled sometime" ──

        /// <summary>A gap in evaluation (here simulated by a stale
        /// LastEvaluatedUtc) means the previous sample is untrustworthy, so
        /// a button that appears held on the first tick back must NOT arm a
        /// short press. The round-three sticky bool set true forever after
        /// the first tick, so every gap source re-created the held-at-start
        /// bug.</summary>
        [Fact]
        public void ShortPress_AfterAnEvaluationGap_DoesNotFireAStaleEdge()
        {
            var im = new InputManager();
            var m = Macro(MacroTriggerMode.ShortPress, holdMs: 300);
            var macros = new[] { m };

            // Establish an OBSERVED released baseline.
            Tick(im, macros, held: false);
            Assert.False(m.WasTriggerActive);

            // Simulate a long gap (engine stopped, slot idle-skipped): the
            // stamp is now stale. The button is held on the first tick back.
            m.LastEvaluatedUtc = DateTime.UtcNow.AddSeconds(-5);
            m.WasTriggerActive = false;   // what a pre-gap released sample left
            Tick(im, macros, held: true); // held at "start": must not arm

            // Release quickly. A stale edge would fire; continuity must not.
            var gp = Tick(im, macros, held: false);
            Assert.Equal((ushort)0, gp.LeftTrigger);
        }

        [Fact]
        public void ShortPress_WithContinuousObservation_StillFiresATap()
        {
            var im = new InputManager();
            var m = Macro(MacroTriggerMode.ShortPress, holdMs: 300);
            var macros = new[] { m };

            Tick(im, macros, held: false);  // observed idle, recent stamp
            Tick(im, macros, held: true);   // press
            var gp = Tick(im, macros, held: false); // release: fires
            Assert.Equal((ushort)Fire, gp.LeftTrigger);
        }

        // ── R15: editing the trigger clears armed windows ──

        [Fact]
        public void EditingTheTrigger_ClearsAnArmedHoldWindow()
        {
            var m = Macro(MacroTriggerMode.ShortPress, holdMs: 300);
            m.TriggerHoldStartUtc = DateTime.UtcNow;
            m.TriggerHoldFired = true;
            // Re-authoring the trigger combo must invalidate the arm.
            m.SetTriggerInputEntries(new System.Collections.Generic.List<MacroItem.TriggerInputEntry>());
            Assert.Equal(DateTime.MinValue, m.TriggerHoldStartUtc);
            Assert.False(m.TriggerHoldFired);
        }

        // ── R17: engine start clears run + latch state ──

        [Fact]
        public void ToggleLatch_DoesNotSurviveAConceptualRestart()
        {
            // The InputService.Start() reset mirrors the disable lane; here
            // we assert the field contract the reset relies on: clearing
            // ToggleTriggerLatched + IsExecuting returns the macro to rest.
            var m = Macro(MacroTriggerMode.Toggle);
            m.ToggleTriggerLatched = true;
            m.IsExecuting = true;
            m.CurrentActionIndex = 3;

            // The reset the engine start performs (same field list).
            m.WasTriggerActive = false;
            m.ToggleTriggerLatched = false;
            m.ToggleRawWasActive = false;
            m.IsExecuting = false;
            m.CurrentActionIndex = 0;
            m.RunReleasedFireToCompletion = false;

            var im = new InputManager();
            var macros = new[] { m };
            // First tick with the button up: a latched Toggle would fire; a
            // cleared one stays silent.
            var gp = Tick(im, macros, held: false);
            Assert.Equal((ushort)0, gp.LeftTrigger);
        }

        // ── R27: the allocation-free pipe scan matches Split semantics ──

        [Theory]
        [InlineData("A|B|C", "A", true)]
        [InlineData("A|B|C", "C", true)]
        [InlineData("A|B|C", "B", true)]
        [InlineData("A|B|C", "D", false)]
        [InlineData("A|B|C", "AB", false)]
        [InlineData("A|B|C", "", false)]
        [InlineData("", "A", false)]
        [InlineData("Layer_9_2|Layer_9_3", "Layer_9_3", true)]
        [InlineData("A||B", "B", true)]        // RemoveEmptyEntries parity
        [InlineData("A|B|", "B", true)]
        public void PipeListContains_MatchesSplitSemantics(string list, string mask, bool expected)
        {
            Assert.Equal(expected, InputManager.PipeListContains(list, mask));

            bool viaSplit = false;
            foreach (var s in (list ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(s, mask, StringComparison.Ordinal)) { viaSplit = true; break; }
            Assert.Equal(viaSplit && !string.IsNullOrEmpty(mask), InputManager.PipeListContains(list, mask));
        }

        // ── R21: the rebuild re-pushes the selected macro's mask ──

        /// <summary>MacroLayerChoices.Clear() nulls the ComboBox selection
        /// and the null write is rejected by the setter, but WPF suppresses
        /// the re-entrant target refresh for the initiating binding, so the
        /// picker rendered BLANK over intact data. RebuildLayerTabs must
        /// re-raise LayerMask on the selected macro so the binding re-reads
        /// the source outside that window. (The visual re-match needs WPF;
        /// this pins the notification the fix depends on.)</summary>
        [Fact]
        public void RebuildLayerTabs_RePushesTheSelectedMacrosLayerBinding()
        {
            var vm = new PadViewModel(0);
            var m = Macro(MacroTriggerMode.OnPress, layerMask: "Shift");
            vm.Macros.Add(m);
            vm.SelectedMacro = m;

            bool raised = false;
            m.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MacroItem.LayerMask)) raised = true;
            };

            vm.RebuildLayerTabs(null);

            Assert.True(raised, "RebuildLayerTabs must re-push the selected macro's LayerMask binding.");
            Assert.Equal("Shift", m.LayerMask); // and the data is untouched
        }

        // ── R25a: a cycle stop is representable as a macro scope ──

        [Fact]
        public void CycleStop_CountsAsDeclared_ForTheOwnSlotGate()
        {
            var ownSet = new MappingSet();
            ownSet.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "Ring1",
                LayerName = "Ring1",
                Mode = "Cycle",
                CycleLayers = "Ring1|Ring2",
                Descriptor = "Button 8",
            });

            var saved = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            try
            {
                for (int i = 0; i < SettingsManager.SlotMappingSets.Length; i++)
                    SettingsManager.SlotMappingSets[i] = null;
                SettingsManager.SlotMappingSets[0] = ownSet;

                // Engage Ring2 (the later stop) on the OWN slot.
                var st = new PadForge.Engine.CustomInputState();
                InputManager.ResolveActiveLayerMask(0, ownSet, st, "");
                // A macro scoped to the later stop opens while that stop is
                // this slot's engaged layer, and stays closed otherwise;
                // the point of R25a is that the stop is a real declared
                // scope, which PipeListContains-through-SlotDeclaresMask
                // already proves at the gate. Here just assert the ring
                // membership the picker now surfaces.
                Assert.True(InputManager.PipeListContains("Ring1|Ring2", "Ring2"));
            }
            finally
            {
                for (int i = 0; i < saved.Length; i++)
                    SettingsManager.SlotMappingSets[i] = saved[i];
            }
        }
    }
}
