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
    [Collection("SettingsManagerStatics")]
    public class SinglePressTriggerTests
    {
        private static MacroItem Macro(MacroTriggerMode mode, short value, int windowMs = 800)
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
            Thread.Sleep(950);                                // window expires
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
            Thread.Sleep(950);
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
            Thread.Sleep(500);
            Assert.Equal(0, Tick(im, macros, held: false));
        }

        [Fact]
        public void ChainResets_NextIsolatedPressFiresAgain()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var im = new InputManager { SinglePressUtcNow = () => now };
            var macros = new[] { Macro(MacroTriggerMode.SinglePress, 1000, windowMs: 800) };

            // Fast pair: suppressed.
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            now = now.AddMilliseconds(950);
            Assert.Equal(0, Tick(im, macros, held: false));  // quiet: chain resets, no fire

            // A later isolated press fires normally.
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            now = now.AddMilliseconds(950);
            double elapsedBeforeTick = (now - macros[0].TriggerLastPressUtc).TotalMilliseconds;
            ushort result = Tick(im, macros, held: false);
            Assert.True(result == 1000, $"Expected 1000, got {result}; elapsed before tick: {elapsedBeforeTick:F2} ms");
        }

        [Fact]
        public void SingleAndDouble_ShareOneButton()
        {
            var im = new InputManager();
            var single = Macro(MacroTriggerMode.SinglePress, 1000, windowMs: 800);
            var dbl = Macro(MacroTriggerMode.DoublePress, 2000, windowMs: 800);
            var macros = new[] { single, dbl };

            // Fast double tap: the Double fires, the Single stays quiet.
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            ushort onSecondPress = Tick(im, macros, held: true);
            Assert.Equal(2000, onSecondPress);
            Tick(im, macros, held: false);
            Thread.Sleep(950);
            Assert.Equal(0, Tick(im, macros, held: false));

            // Isolated tap: the Single fires, the Double stays quiet.
            Tick(im, macros, held: true);
            Tick(im, macros, held: false);
            Thread.Sleep(950);
            Assert.Equal(1000, Tick(im, macros, held: false));
        }

        [Theory]
        [InlineData(false, 800, 0)]
        [InlineData(false, 801, 1000)]
        [InlineData(false, 1050, 1000)]
        [InlineData(false, 1051, 0)]
        [InlineData(true, 800, 0)]
        [InlineData(true, 801, 1000)]
        [InlineData(true, 1050, 1000)]
        [InlineData(true, 1051, 0)]
        public void SinglePressClock_PreservesWindowAndGraceInBothEvaluators(
            bool extended, int elapsedMs, int expected)
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var im = new InputManager { SinglePressUtcNow = () => now };
            var macro = Macro(MacroTriggerMode.SinglePress, 1000, windowMs: 800);
            macro.Actions[0].AxisTarget = MacroAxisTarget.LeftStickX;
            if (extended)
            {
                macro.TriggerButtons = 0;
                macro.TriggerCustomButtons = "00000001,00000000,00000000,00000000";
            }
            var macros = new[] { macro };
            short Evaluate(bool held)
            {
                if (extended)
                {
                    var raw = RawHidState.Create(8, 32, 1);
                    raw.Buttons[0] = held ? 1u : 0u;
                    im.EvaluateSlotMacrosExtended(ref raw, macros);
                    return raw.Axes[0];
                }
                var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
                im.EvaluateSlotMacros(ref gp, macros);
                return gp.ThumbLX;
            }

            Assert.Equal(0, Evaluate(true));
            Assert.Equal(now, macro.TriggerLastPressUtc);
            Assert.Equal(0, Evaluate(false));
            now = now.AddMilliseconds(elapsedMs);
            double elapsedBeforeTick = (now - macro.TriggerLastPressUtc).TotalMilliseconds;
            short result = Evaluate(false);
            Assert.True(result == expected,
                $"Expected {expected}, got {result}; elapsed before tick: {elapsedBeforeTick:F2} ms");
            if (elapsedMs > 800)
            {
                Assert.Equal(0, macro.TriggerPressStreak);
                Assert.Equal(DateTime.MinValue, macro.TriggerLastPressUtc);
                Assert.Equal(0, Evaluate(false));
            }
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
