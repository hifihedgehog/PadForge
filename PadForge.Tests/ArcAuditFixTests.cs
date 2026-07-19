using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins for the eight-item-arc audit fixes (2026-07-18): the Extended
    /// trigger rest-point contracts, the Gate2 dedup key, the pressure
    /// activator classification, and the TriplePress disable reset. Each
    /// test goes red when its fix is reverted (Phase 4 mutation bar).
    /// </summary>
    public class ArcAuditFixTests
    {
        // ─── Extended trigger yield: rest is short.MinValue, not 0 ───

        private static MacroItem ExtendedHoldMacro(bool yield)
        {
            var m = new MacroItem
            {
                Name = "AF",
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
                AxisYieldToPhysical = yield,
                DurationMs = 60000,
            });
            return m;
        }

        [Fact]
        public void ExtendedTriggerYield_DoesNotLatchOnTheRestingTrigger()
        {
            var im = new InputManager();
            var macros = new[] { ExtendedHoldMacro(yield: true) };

            // Trigger channel at rest = short.MinValue. The yield must NOT
            // read that as physical deflection; the macro asserts.
            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            raw.Axes[2] = short.MinValue;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(32767, raw.Axes[2]);
        }

        [Fact]
        public void ExtendedTriggerYield_YieldsToARealPull()
        {
            var im = new InputManager();
            var macros = new[] { ExtendedHoldMacro(yield: true) };

            // A genuine physical pull (well past the rest point) yields.
            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            raw.Axes[2] = 0;   // half pull in the signed word frame
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(0, raw.Axes[2]);
        }

        // ─── Extended trigger AxisAdd: pull scale parity ───

        [Fact]
        public void ExtendedTriggerAxisAdd_FullAddReachesFullPull()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "AFA",
                IsEnabled = true,
                PadIndex = 0,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.WhileHeld,
                RepeatMode = MacroRepeatMode.UntilRelease,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisAdd,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 32767,   // the UI's "+100%"
                DurationMs = 60000,
            });
            var macros = new[] { m };

            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            raw.Axes[2] = short.MinValue;   // rest
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            // Pull scale: rest + 32767*2 clamps to full, not the midpoint.
            Assert.True(raw.Axes[2] > 32000, $"add only reached {raw.Axes[2]}");
        }

        [Fact]
        public void RawStickAxisAdd_KeepsThePlainSignedFrame()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "AFS",
                IsEnabled = true,
                PadIndex = 0,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.WhileHeld,
                RepeatMode = MacroRepeatMode.UntilRelease,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisAdd,
                AxisTarget = MacroAxisTarget.LeftStickX,
                AxisValue = -16000,
                DurationMs = 60000,
            });
            var macros = new[] { m };

            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            raw.Axes[0] = 20000;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Equal(4000, raw.Axes[0]);   // no double-apply on sticks
        }

        // ─── Gate2Descriptor joins the dedup key ───

        [Fact]
        public void SanitizeMappingSet_KeepsSourcesDifferingOnlyInGate2()
        {
            var ms = new MappingSet();
            var row = new MappingRow { Target = "ButtonA" };
            row.Sources.Add(new MappingSource
            {
                Descriptor = "Touchpad 0 Finger 0 Down",
                GateDescriptor = "Touchpad 0 Click",
                Gate2Descriptor = "Gamepad ButtonA",
            });
            row.Sources.Add(new MappingSource
            {
                Descriptor = "Touchpad 0 Finger 0 Down",
                GateDescriptor = "Touchpad 0 Click",
                Gate2Descriptor = "Gamepad ButtonB",
            });
            ms.Rows.Add(row);

            SettingsService.SanitizeMappingSet(ms, 0);
            Assert.Equal(2, ms.Rows[0].Sources.Count);
        }

        // ─── Pressure classifies as axis-class (#239 threshold reach) ───

        [Theory]
        [InlineData("Touchpad 0 Finger 0 Pressure", true)]
        [InlineData("Touchpad 1 Finger 1 Pressure North", true)]
        [InlineData("Touchpad 0 Finger 0 Pressure Center", true)]
        [InlineData("Touchpad 0 Finger 0 X", false)]
        [InlineData("Touchpad 0 Click", false)]
        [InlineData("Axis 2", false)]
        public void IsTouchpadPressureDescriptor_MatchesExactlyThePressureFamily(string d, bool expected)
        {
            Assert.Equal(expected, SourceCoercion.IsTouchpadPressureDescriptor(d));
        }

        // ─── TriplePress chain resets on disable ───

        [Fact]
        public void TriplePress_DisableResetsThePressChain()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "TPD",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.TriplePress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
                TriggerDoublePressMs = 60000,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 1000,
            });
            var macros = new[] { m };

            // Two chained presses, then disable: the streak must die.
            for (int i = 0; i < 2; i++)
            {
                var press = new Gamepad { Buttons = Gamepad.A };
                im.EvaluateSlotMacros(ref press, macros);
                var up = new Gamepad();
                im.EvaluateSlotMacros(ref up, macros);
            }
            Assert.Equal(2, m.TriggerPressStreak);

            m.IsEnabled = false;
            var idle = new Gamepad();
            im.EvaluateSlotMacros(ref idle, macros);
            Assert.Equal(0, m.TriggerPressStreak);

            // Re-enabled: the next press is a fresh chain start, so the
            // third overall press must NOT fire.
            m.IsEnabled = true;
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.LeftTrigger);
        }
    }
}
