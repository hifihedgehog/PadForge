using System;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #238 follow-up: Toggle and Turbo as first-class Fire
    /// modes. Toggle is a trigger-level latch (press latches the actions
    /// on, press again releases; disable clears the latch). Turbo repeats
    /// the sequence at RepeatDelayMs while held and stops on release,
    /// regardless of the authored RepeatMode.
    /// </summary>
    public class ToggleTurboTriggerTests
    {
        private static MacroItem Macro(MacroTriggerMode mode, short value, int repeatDelayMs = 0)
        {
            var m = new MacroItem
            {
                Name = "TT",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
                RepeatDelayMs = repeatDelayMs,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = value,
            });
            return m;
        }

        private static ushort Tick(InputManager im, MacroItem[] macros, bool held)
        {
            var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
            im.EvaluateSlotMacros(ref gp, macros);
            return gp.LeftTrigger;
        }

        [Fact]
        public void Toggle_LatchesAcrossRelease_UnlatchesOnSecondPress()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.Toggle, 30000) };

            // First press latches on; the action asserts.
            Assert.Equal(30000, Tick(im, macros, held: true));
            // Physical release: the latch keeps the actions running.
            Assert.Equal(30000, Tick(im, macros, held: false));
            for (int i = 0; i < 5; i++)
                Assert.Equal(30000, Tick(im, macros, held: false));

            // Second press unlatches; the run stops (same tick or next).
            Tick(im, macros, held: true);
            Assert.Equal(0, Tick(im, macros, held: true));
            // And stays stopped after the second press releases.
            Assert.Equal(0, Tick(im, macros, held: false));
        }

        [Fact]
        public void Toggle_HoldingTheFirstPress_DoesNotUnlatch()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.Toggle, 30000) };

            // A long first hold is ONE rising edge: latch stays on the
            // whole time (no repeat-fire of the flip).
            for (int i = 0; i < 8; i++)
                Assert.Equal(30000, Tick(im, macros, held: true));
            Assert.Equal(30000, Tick(im, macros, held: false));
        }

        [Fact]
        public void Toggle_DisableClearsTheLatch()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.Toggle, 30000) };

            Assert.Equal(30000, Tick(im, macros, held: true));
            Assert.Equal(30000, Tick(im, macros, held: false));

            // Disable while latched: the evaluator's reset lane clears it.
            macros[0].IsEnabled = false;
            Tick(im, macros, held: false);
            macros[0].IsEnabled = true;

            // Re-enabled: unlatched, no output until a fresh press,
            // and that press is a fresh latch-ON (not a surprise off).
            Assert.Equal(0, Tick(im, macros, held: false));
            Assert.Equal(30000, Tick(im, macros, held: true));
        }

        [Fact]
        public void Turbo_RepeatsAtInterval_WithRepeatModeOnce()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.Turbo, 30000, repeatDelayMs: 60) };

            // First pass fires on press.
            Assert.Equal(30000, Tick(im, macros, held: true));
            // Inside the interval: the run idles between passes.
            Assert.Equal(0, Tick(im, macros, held: true));
            // Past the interval: the next pass fires, even though the
            // authored RepeatMode is Once (Turbo forces until-release).
            Thread.Sleep(80);
            Assert.Equal(30000, Tick(im, macros, held: true));
        }

        [Fact]
        public void Turbo_StopsOnRelease()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.Turbo, 30000, repeatDelayMs: 40) };

            Assert.Equal(30000, Tick(im, macros, held: true));
            // Release: no further passes, including past the interval.
            Tick(im, macros, held: false);
            Thread.Sleep(60);
            Assert.Equal(0, Tick(im, macros, held: false));
            // Re-press starts a fresh run.
            Assert.Equal(30000, Tick(im, macros, held: true));
        }
    }
}
