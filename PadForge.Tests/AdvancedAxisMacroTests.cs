using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #237 (advanced axis macros, the reWASD combo blocks):
    /// AxisAdd (relative deflection summed with the mapped value),
    /// ComboBreak (multi-part sequences resuming on the next press), and
    /// the AxisYieldToPhysical gate on the absolute holds. Dispatch runs
    /// through the REAL slot evaluators on both output shapes, and the
    /// persistence field rides the same ActionData DTO round-trip the
    /// settings XML uses.
    /// </summary>
    public class AdvancedAxisMacroTests
    {
        private static MacroItem GamepadMacro(MacroTriggerMode mode, MacroRepeatMode repeat, params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "AAM",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = repeat,
                ConsumeTriggerButtons = false,
            };
            foreach (var a in actions) m.Actions.Add(a);
            return m;
        }

        private static MacroAction RoundTrip(MacroAction a)
        {
            var m = new MacroItem { Name = "RT" };
            m.Actions.Add(a);
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            return Assert.Single(clone.Actions);
        }

        // ── Persistence ──

        [Fact]
        public void AxisYieldToPhysical_SurvivesTheActionDataRoundTrip()
        {
            var back = RoundTrip(new MacroAction
            {
                Type = MacroActionType.AxisHold,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 32767,
                AxisYieldToPhysical = true,
            });
            Assert.True(back.AxisYieldToPhysical);

            var off = RoundTrip(new MacroAction
            {
                Type = MacroActionType.AxisHold,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 32767,
            });
            Assert.False(off.AxisYieldToPhysical);
        }

        [Fact]
        public void AxisAdd_And_ComboBreak_SurviveTheRoundTrip()
        {
            var add = RoundTrip(new MacroAction
            {
                Type = MacroActionType.AxisAdd,
                AxisTarget = MacroAxisTarget.RightStickX,
                AxisValue = -16000,
            });
            Assert.Equal(MacroActionType.AxisAdd, add.Type);
            Assert.Equal(-16000, add.AxisValue);

            var brk = RoundTrip(new MacroAction { Type = MacroActionType.ComboBreak });
            Assert.Equal(MacroActionType.ComboBreak, brk.Type);
        }

        // ── AxisAdd semantics (Gamepad path) ──

        [Fact]
        public void AxisAdd_SumsWithTheMappedValue_StickFrame()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisAdd,
                    AxisTarget = MacroAxisTarget.LeftStickX,
                    AxisValue = -16000,
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            // Physical stick at +20000; the relative add lands on top.
            var gp = new Gamepad { Buttons = Gamepad.A, ThumbLX = 20000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(4000, gp.ThumbLX);
        }

        [Fact]
        public void AxisAdd_ClampsAtTheRangeEdges()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisAdd,
                    AxisTarget = MacroAxisTarget.LeftStickX,
                    AxisValue = -30000,
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A, ThumbLX = -20000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(short.MinValue, gp.ThumbLX);
        }

        [Fact]
        public void AxisAdd_TriggerTarget_AddsOnThePullScale()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisAdd,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = 8000,   // +8000 * 2 = +16000 on the 0..65535 output
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A, LeftTrigger = 30000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(46000, gp.LeftTrigger);

            // Negative add subtracts and clamps at zero.
            m.Actions[0].AxisValue = -20000;
            gp = new Gamepad { Buttons = Gamepad.A, LeftTrigger = 30000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.LeftTrigger);
        }

        // ── Yield-to-physical (absolute deflection, reWASD contract) ──

        [Fact]
        public void AxisHold_WithoutYield_MacroWins()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisHold,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = 32767,
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A, LeftTrigger = 20000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(65535, gp.LeftTrigger);
        }

        [Fact]
        public void AxisHold_WithYield_PhysicalMovementWins_AndLatches()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction
                {
                    Type = MacroActionType.AxisHold,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = 32767,
                    AxisYieldToPhysical = true,
                    DurationMs = 60000,
                });
            var macros = new[] { m };

            // Frame 1: physical at rest, macro asserts the full pull.
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(65535, gp.LeftTrigger);

            // Frame 2: the user pulls the physical trigger past the yield
            // threshold; the macro write is suppressed, physical survives.
            gp = new Gamepad { Buttons = Gamepad.A, LeftTrigger = 20000 };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(20000, gp.LeftTrigger);

            // Frame 3: the yield is LATCHED for the activation; even with
            // the physical back at rest the macro stays yielded.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.LeftTrigger);

            // Release re-arms; the next activation asserts again.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(65535, gp.LeftTrigger);
        }

        // ── Combo break sequencing ──

        [Fact]
        public void ComboBreak_ParksAndResumesOnTheNextPress()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 1000 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 2000 });
            var macros = new[] { m };

            // Press 1: part one runs (AxisSet advances same frame, the
            // break parks on the following frame).
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(1000, gp.LeftTrigger);

            gp = new Gamepad { Buttons = Gamepad.A };  // still held: break parks
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(m.IsExecuting);
            Assert.Equal(2, m.ComboResumeIndex);

            // Release, then press 2: resumes at part two.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(2000, gp.LeftTrigger);

            // Completing the final part re-arms from the top.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, m.ComboResumeIndex);

            // Release, press 3: back to part one.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(1000, gp.LeftTrigger);
        }

        [Fact]
        public void ComboBreak_WhileHeldTrigger_NeverAutoResumesThroughTheBreak()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 1000 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 2000 });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);      // part one
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);      // break parks
            Assert.False(m.IsExecuting);
            Assert.True(m.AwaitReleaseAfterBreak);

            // Held frames must NOT restart through the break.
            for (int i = 0; i < 3; i++)
            {
                gp = new Gamepad { Buttons = Gamepad.A };
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.False(m.IsExecuting);
                Assert.Equal(0, gp.LeftTrigger);
            }

            // Release opens the guard; the next hold resumes at part two.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(m.AwaitReleaseAfterBreak);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(2000, gp.LeftTrigger);
        }

        [Fact]
        public void ComboBreak_DisablingTheMacro_ResetsThePark()
        {
            var im = new InputManager();
            var m = GamepadMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 1000 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSet, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 2000 });
            var macros = new[] { m };

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(2, m.ComboResumeIndex);

            m.IsEnabled = false;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, m.ComboResumeIndex);
            Assert.False(m.AwaitReleaseAfterBreak);

            // Re-enabled: a released frame re-arms the OnPress edge, then
            // the press starts from the top.
            m.IsEnabled = true;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(1000, gp.LeftTrigger);
        }

        // ── Extended raw-path siblings ──

        [Fact]
        public void ExtendedPath_AxisAdd_And_Break_MirrorTheGamepadSemantics()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "AAMX",
                IsEnabled = true,
                PadIndex = 0,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisAdd,
                AxisTarget = MacroAxisTarget.LeftStickX,
                AxisValue = -16000,
                DurationMs = 0,
            });
            m.Actions.Add(new MacroAction { Type = MacroActionType.ComboBreak });
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.LeftStickX,
                AxisValue = 5000,
            });
            var macros = new[] { m };

            var raw = ExtendedRawState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            raw.Axes[0] = 20000;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(4000, raw.Axes[0]);            // additive, word frame

            raw = ExtendedRawState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);  // break parks
            Assert.False(m.IsExecuting);
            Assert.Equal(2, m.ComboResumeIndex);

            raw = ExtendedRawState.Create(8, 32, 1);    // release
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            raw = ExtendedRawState.Create(8, 32, 1);    // press 2: part two
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(5000, raw.Axes[0]);
        }
    }
}
