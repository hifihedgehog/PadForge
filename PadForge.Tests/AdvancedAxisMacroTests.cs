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

            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            raw.Axes[0] = 20000;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(4000, raw.Axes[0]);            // additive, word frame

            raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);  // break parks
            Assert.False(m.IsExecuting);
            Assert.Equal(2, m.ComboResumeIndex);

            raw = RawHidState.Create(8, 32, 1);    // release
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            raw = RawHidState.Create(8, 32, 1);    // press 2: part two
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(5000, raw.Axes[0]);
        }
    
        // ── #251: latched ladder, release, proportional scale ──

        private static MacroItem LadderMacro()
        {
            return GamepadMacro(MacroTriggerMode.OnPress, MacroRepeatMode.Once,
                new MacroAction { Type = MacroActionType.AxisSetLatched, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 24575 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSetLatched, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 27852 },
                new MacroAction { Type = MacroActionType.ComboBreak },
                new MacroAction { Type = MacroActionType.AxisSetLatched, AxisTarget = MacroAxisTarget.LeftTrigger, AxisValue = 31128 });
        }

        private static ushort LadderTick(InputManager im, MacroItem[] macros, bool held)
        {
            var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
            im.EvaluateSlotMacros(ref gp, macros);
            return gp.LeftTrigger;
        }

        /// <summary>Use case 1 (#251): a press-by-press ladder whose value
        /// HOLDS between presses (across combo-break parks), each press
        /// REPLACING the value, and lap 2 relatching instead of the
        /// ToggleVcAxis flip that unlatches.</summary>
        [Fact]
        public void Ladder_HoldsAndReplacesAcrossParks()
        {
            var im = new InputManager();
            var macros = new[] { LadderMacro() };

            // One sequential action executes per tick, so a real press
            // spans the latch tick AND the break tick before the release
            // (exactly as a 1 kHz poll would see it).
            LadderTick(im, macros, held: true);              // latch 75
            LadderTick(im, macros, held: true);              // break parks
            ushort held75 = LadderTick(im, macros, held: false);
            Assert.True(Math.Abs(held75 - 49150) < 700, $"expected ~75% pull, got {held75}");

            LadderTick(im, macros, held: true);              // latch 85 (replaces 75)
            LadderTick(im, macros, held: true);              // break parks
            ushort held85 = LadderTick(im, macros, held: false);
            Assert.True(Math.Abs(held85 - 55704) < 700, $"expected ~85% pull, got {held85}");

            LadderTick(im, macros, held: true);              // latch 95
            LadderTick(im, macros, held: true);              // sequence completes, re-arms
            ushort held95 = LadderTick(im, macros, held: false);
            Assert.True(Math.Abs(held95 - 62256) < 700, $"expected ~95% pull, got {held95}");

            LadderTick(im, macros, held: true);              // lap 2: latch 75 again
            LadderTick(im, macros, held: true);
            ushort lap2 = LadderTick(im, macros, held: false);
            Assert.True(Math.Abs(lap2 - 49150) < 700, $"lap 2 must relatch ~75%, got {lap2}");
        }

        /// <summary>Use case 1's nullify key (#251): a SECOND macro's
        /// Release Axis Latches clears the ladder macro's latch, returning
        /// the axis to physical control.</summary>
        [Fact]
        public void ReleaseLatches_ClearsAcrossMacros()
        {
            var im = new InputManager();
            var ladder = LadderMacro();
            var nullify = new MacroItem
            {
                Name = "Nullify",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.B,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
            };
            nullify.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisLatchRelease,
                AxisTarget = MacroAxisTarget.None,
            });
            var macros = new[] { ladder, nullify };

            LadderTick(im, macros, held: true);
            LadderTick(im, macros, held: true);   // break tick
            Assert.True(LadderTick(im, macros, held: false) > 40000);

            var gp = new Gamepad { Buttons = Gamepad.B };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, LadderTick(im, macros, held: false));
        }

        /// <summary>Use case 2 (#251): Scale Axis -50% halves the current
        /// deflection, +50% amplifies with a full-scale clamp.</summary>
        [Fact]
        public void ScaleAxis_HalvesAndAmplifies()
        {
            var walkDown = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction { Type = MacroActionType.AxisScale, AxisTarget = MacroAxisTarget.LeftStickX, AxisValue = -16384, DurationMs = 1000 });
            var im = new InputManager();
            var gp = new Gamepad { Buttons = Gamepad.A, ThumbLX = 30000 };
            im.EvaluateSlotMacros(ref gp, new[] { walkDown });
            Assert.True(Math.Abs(gp.ThumbLX - 15000) < 300, $"expected ~15000, got {gp.ThumbLX}");

            var amplify = GamepadMacro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease,
                new MacroAction { Type = MacroActionType.AxisScale, AxisTarget = MacroAxisTarget.LeftStickX, AxisValue = 16384, DurationMs = 1000 });
            var im2 = new InputManager();
            var gp2 = new Gamepad { Buttons = Gamepad.A, ThumbLX = 30000 };
            im2.EvaluateSlotMacros(ref gp2, new[] { amplify });
            Assert.Equal(short.MaxValue, gp2.ThumbLX);
        }

        /// <summary>#251 members sit at pinned tail ordinals (the clipboard
        /// serializes numerically).</summary>
        [Fact]
        public void ActionTypeEnum_251TailPinned()
        {
            Assert.Equal(49, (int)MacroActionType.AxisSetLatched);
            Assert.Equal(50, (int)MacroActionType.AxisLatchRelease);
            Assert.Equal(51, (int)MacroActionType.AxisScale);
            var values = Enum.GetValues<MacroActionType>();
            Assert.Equal(MacroActionType.AxisScale, values[^1]);
        }
    }
}
