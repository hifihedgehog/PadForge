using System;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #238: the TriplePress trigger mode. Three rising edges
    /// chained within the shared press window fire once; slower chains
    /// re-arm; the chain is consumed on fire. Runs through the REAL slot
    /// evaluator like the DoublePress suite.
    /// </summary>
    public class TriplePressTriggerTests
    {
        private static MacroItem TriplePressMacro(int windowMs = 3000)
        {
            var m = new MacroItem
            {
                Name = "TP",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.TriplePress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
                TriggerDoublePressMs = windowMs,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 1000,
            });
            return m;
        }

        private static void Press(InputManager im, MacroItem[] macros, out Gamepad gp)
        {
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            var up = new Gamepad();
            im.EvaluateSlotMacros(ref up, macros);
        }

        [Fact]
        public void ThreeFastPresses_FireOnce_OnTheThird()
        {
            var im = new InputManager();
            var m = TriplePressMacro();
            var macros = new[] { m };

            Press(im, macros, out var gp1);
            Assert.Equal(0, gp1.LeftTrigger);          // first press: arm
            Press(im, macros, out var gp2);
            Assert.Equal(0, gp2.LeftTrigger);          // second press: chain
            Press(im, macros, out var gp3);
            Assert.Equal(1000, gp3.LeftTrigger);       // third press: fire
        }

        [Fact]
        public void ChainIsConsumed_FourthPressStartsFresh()
        {
            var im = new InputManager();
            var m = TriplePressMacro();
            var macros = new[] { m };

            Press(im, macros, out _);
            Press(im, macros, out _);
            Press(im, macros, out var fired);
            Assert.Equal(1000, fired.LeftTrigger);

            // Press 4 must be a fresh chain start, not a second fire.
            Press(im, macros, out var gp4);
            Assert.Equal(0, gp4.LeftTrigger);
            Press(im, macros, out var gp5);
            Assert.Equal(0, gp5.LeftTrigger);
            Press(im, macros, out var gp6);
            Assert.Equal(1000, gp6.LeftTrigger);       // presses 4-6 fire again
        }

        [Fact]
        public void SlowPress_ReArmsTheChain()
        {
            var im = new InputManager();
            var m = TriplePressMacro(windowMs: 60);
            var macros = new[] { m };

            Press(im, macros, out _);
            Press(im, macros, out _);                  // streak 2
            Thread.Sleep(150);                          // outside the 60 ms window
            Press(im, macros, out var slow);
            Assert.Equal(0, slow.LeftTrigger);         // fresh first press, no fire

            // The two presses after it complete the NEW chain.
            Press(im, macros, out _);
            Press(im, macros, out var fired);
            Assert.Equal(1000, fired.LeftTrigger);
        }

        [Fact]
        public void TriggerModeEnum_TriplePressPinnedOrdinal()
        {
            // The tail moved to Turbo (10); TriplePress's ordinal stays
            // pinned (the clipboard serializes numerically).
            Assert.Equal(7, (int)MacroTriggerMode.TriplePress);
            var values = Enum.GetValues<MacroTriggerMode>();
            Assert.Equal(MacroTriggerMode.TriplePress, values[^4]);
        }

        [Fact]
        public void WindowGate_ShowsForTriplePress()
        {
            var m = TriplePressMacro();
            Assert.True(m.IsDoublePressMode);          // the shared window row
            Assert.True(m.ShowsTriggerComboEditor);
        }
    }
}
