using System;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #238 follow-up: SinglePress, the DEFERRED single (as
    /// distinct from OnPress = Start Press). An isolated press fires once
    /// when its window expires; a fast chain fires nothing; a Single and
    /// a Double macro share one button cleanly.
    /// </summary>
    public class SinglePressTriggerTests
    {
        private static MacroItem Macro(MacroTriggerMode mode, short value, int windowMs = 80)
        {
            var m = new MacroItem
            {
                Name = "SP",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
                TriggerDoublePressMs = windowMs,
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
        public void IsolatedPress_FiresOnceAfterTheWindow()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.SinglePress, 1000) };

            Assert.Equal(0, Tick(im, macros, held: true));   // press: defer
            Assert.Equal(0, Tick(im, macros, held: false));  // release inside window
            Thread.Sleep(150);                                // window expires
            Assert.Equal(1000, Tick(im, macros, held: false));
            // One-shot: nothing further.
            Assert.Equal(0, Tick(im, macros, held: false));
        }

        [Fact]
        public void HeldPress_FiresAtWindowExpiryWhileStillDown()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.SinglePress, 1000) };

            Assert.Equal(0, Tick(im, macros, held: true));
            Thread.Sleep(150);
            Assert.Equal(1000, Tick(im, macros, held: true));
        }

        [Fact]
        public void FastDoubleTap_NeverFiresTheSingle()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.SinglePress, 1000, windowMs: 3000) };

            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            Tick(im, macros, held: true);    // second press inside the window
            Tick(im, macros, held: false);
            // Even long after, the chained pair must not fire the single.
            Thread.Sleep(50);
            Assert.Equal(0, Tick(im, macros, held: false));
        }

        [Fact]
        public void ChainResets_NextIsolatedPressFiresAgain()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.SinglePress, 1000, windowMs: 80) };

            // Fast pair: suppressed.
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            Thread.Sleep(150);
            Assert.Equal(0, Tick(im, macros, held: false));  // quiet: chain resets, no fire

            // A later isolated press fires normally.
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            Thread.Sleep(150);
            Assert.Equal(1000, Tick(im, macros, held: false));
        }

        [Fact]
        public void SingleAndDouble_ShareOneButton()
        {
            var im = new InputManager();
            var single = Macro(MacroTriggerMode.SinglePress, 1000, windowMs: 80);
            var dbl = Macro(MacroTriggerMode.DoublePress, 2000, windowMs: 80);
            var macros = new[] { single, dbl };

            // Fast double tap: the Double fires, the Single stays quiet.
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            ushort onSecondPress = Tick(im, macros, held: true);
            Assert.Equal(2000, onSecondPress);
            Tick(im, macros, held: false);
            Thread.Sleep(150);
            Assert.Equal(0, Tick(im, macros, held: false));

            // Isolated tap: the Single fires, the Double stays quiet.
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            Thread.Sleep(150);
            Assert.Equal(1000, Tick(im, macros, held: false));
        }

        [Fact]
        public void TriggerModeEnum_SinglePressPinnedAtTail()
        {
            // #238 Toggle/Turbo and #253 ShortPress appended after;
            // SinglePress's ordinal stays pinned (the clipboard
            // serializes numerically).
            Assert.Equal(8, (int)MacroTriggerMode.SinglePress);
            var values = Enum.GetValues<MacroTriggerMode>();
            Assert.Equal(MacroTriggerMode.SinglePress, values[^4]);
        }

        [Fact]
        public void WindowRow_And_ComboEditor_ShowForSinglePress()
        {
            var m = Macro(MacroTriggerMode.SinglePress, 1000);
            Assert.True(m.IsDoublePressMode);
            Assert.True(m.ShowsTriggerComboEditor);
        }
    }
}
