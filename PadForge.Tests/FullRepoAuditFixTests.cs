using System;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins for the 2026-07-18 full-repo audit fixes: the SinglePress
    /// released-tap one-pass contract, the stale-fire grace, mode-switch
    /// transient resets, the raw yield boundary, cross-macro yield
    /// isolation, and the window tooltip mode-following.
    /// </summary>
    public class FullRepoAuditFixTests
    {
        private static MacroItem Macro(MacroTriggerMode mode, MacroRepeatMode repeat,
            int windowMs, params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "FRA",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = repeat,
                ConsumeTriggerButtons = false,
                TriggerDoublePressMs = windowMs,
            };
            foreach (var a in actions) m.Actions.Add(a);
            return m;
        }

        private static MacroAction TriggerSet(short value) => new()
        {
            Type = MacroActionType.AxisSet,
            AxisTarget = MacroAxisTarget.LeftTrigger,
            AxisValue = value,
        };

        private static ushort Tick(InputManager im, MacroItem[] macros, bool held)
        {
            var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
            im.EvaluateSlotMacros(ref gp, macros);
            return gp.LeftTrigger;
        }

        // ─── D1: released-tap + UntilRelease runs one full pass ───

        [Fact]
        public void SinglePress_ReleasedTap_UntilRelease_RunsOnePass()
        {
            var im = new InputManager();
            var m = Macro(MacroTriggerMode.SinglePress, MacroRepeatMode.UntilRelease, 80,
                TriggerSet(1000));
            var macros = new[] { m };

            Tick(im, macros, held: true);
            Tick(im, macros, held: false);   // released inside the window
            Thread.Sleep(150);                // window expires
            // The deferred fire lands with the button up; the release-stop
            // must not kill the pass in the same frame.
            Assert.Equal(1000, Tick(im, macros, held: false));
            // One pass only: the UntilRelease repeat does not loop forever.
            Tick(im, macros, held: false);
            Assert.False(m.IsExecuting);
            Assert.False(m.RunReleasedFireToCompletion);
        }

        // ─── D4: stale-fire grace ───

        [Fact]
        public void SinglePress_StaleArm_NeverGhostFires()
        {
            var im = new InputManager();
            var m = Macro(MacroTriggerMode.SinglePress, MacroRepeatMode.Once, 40,
                TriggerSet(1000));
            var macros = new[] { m };

            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            // Simulate a stopped pipeline: no evaluation until far past
            // the window plus the grace.
            Thread.Sleep(500);
            Assert.Equal(0, Tick(im, macros, held: false));  // reset, no ghost
            Assert.Equal(0, m.TriggerPressStreak);

            // A fresh isolated press still fires normally.
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            Thread.Sleep(80);
            Assert.Equal(1000, Tick(im, macros, held: false));
        }

        // ─── D2: mode switch voids transients ───

        [Fact]
        public void TriggerModeSwitch_ClearsArmedPressChain()
        {
            var im = new InputManager();
            var m = Macro(MacroTriggerMode.SinglePress, MacroRepeatMode.Once, 60000,
                TriggerSet(1000));
            var macros = new[] { m };

            Tick(im, macros, held: true);    // arm the single (streak 1)
            Tick(im, macros, held: false);
            Assert.Equal(1, m.TriggerPressStreak);

            // Switch to DoublePress: the armed timestamp must die, or the
            // FIRST post-switch press would read as the second of a pair.
            m.TriggerMode = MacroTriggerMode.DoublePress;
            Assert.Equal(0, m.TriggerPressStreak);
            Assert.Equal(DateTime.MinValue, m.TriggerLastPressUtc);
            Assert.Equal(0, Tick(im, macros, held: true));   // press 1: arm only
        }

        [Fact]
        public void TriggerModeSwitch_ClearsHoldTransients()
        {
            var m = Macro(MacroTriggerMode.HoldForMs, MacroRepeatMode.Once, 400, TriggerSet(1000));
            m.TriggerHoldStartUtc = DateTime.UtcNow;
            m.TriggerHoldFired = true;
            m.TriggerMode = MacroTriggerMode.OnPress;
            Assert.Equal(DateTime.MinValue, m.TriggerHoldStartUtc);
            Assert.False(m.TriggerHoldFired);
        }

        // ─── D8: raw trigger yield boundary matches the Gamepad twin ───

        [Fact]
        public void ExtendedTriggerYield_BoundaryMatchesTwelvePointFivePercent()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "FRB",
                IsEnabled = true,
                PadIndex = 0,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.WhileHeld,
                RepeatMode = MacroRepeatMode.UntilRelease,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisHold,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 32767,
                AxisYieldToPhysical = true,
                DurationMs = 60000,
            });
            var macros = new[] { m };

            // 20% pull from rest: -32768 + 0.20*65536 = -19661. Between the
            // correct 12.5% boundary (8192) and the buggy 25% one (16384):
            // the yield must trip here.
            var raw = ExtendedRawState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            raw.Axes[2] = -19661;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(-19661, raw.Axes[2]);   // physical survives
        }

        // ─── D9: cross-macro yield isolation ───

        [Fact]
        public void Yield_IgnoresAnotherMacrosSameFrameWrite()
        {
            var im = new InputManager();
            // Macro 1: latches the trigger to 40%.
            var first = Macro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, 400,
                new MacroAction
                {
                    Type = MacroActionType.ToggleVcAxis,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = 13107,
                });
            // Macro 2: yield-enabled latch to 90% on the SAME target.
            var second = Macro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, 400,
                new MacroAction
                {
                    Type = MacroActionType.ToggleVcAxis,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = 29490,
                    AxisYieldToPhysical = true,
                });
            second.TriggerButtons = Gamepad.B;
            var macros = new[] { first, second };

            // Press A: macro 1 latches 40%.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(gp.LeftTrigger > 20000);

            // Press B with A released: macro 2's yield must NOT read
            // macro 1's same-frame latch write as physical input.
            gp = new Gamepad { Buttons = Gamepad.B };
            im.EvaluateSlotMacros(ref gp, macros);
            // Macro 2's latch (applied after macro 1 in list order) wins.
            Assert.True(gp.LeftTrigger > 50000,
                $"macro 2 yielded to macro 1's write (LT={gp.LeftTrigger})");
        }

        // ─── D7: window tooltip follows the mode ───

        [Fact]
        public void PressWindowTooltip_FollowsAllThreeModes()
        {
            var m = Macro(MacroTriggerMode.SinglePress, MacroRepeatMode.Once, 400, TriggerSet(1));
            string single = m.TriggerPressWindowToolTip;
            m.TriggerMode = MacroTriggerMode.DoublePress;
            string dbl = m.TriggerPressWindowToolTip;
            m.TriggerMode = MacroTriggerMode.TriplePress;
            string triple = m.TriggerPressWindowToolTip;
            Assert.NotEqual(single, dbl);
            Assert.NotEqual(dbl, triple);
            Assert.NotEqual(single, triple);
        }
    }
}
