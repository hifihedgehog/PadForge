using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Axis macros on the virtual DualShock 3's button pressures (#530,
    /// discussion #524): the ten pressure targets write the press and the
    /// pressure together through the real gamepad evaluator, every axis
    /// action reaches them, the pass's overlay and yield gate treat them as
    /// buttons, the pressure goes out through the wire gate, and the macro
    /// editor offers them only on the DualShock 3 (SIXAXIS): Full preset.
    /// </summary>
    // Macro evaluation mutates the static yield latch set, so this class
    // runs serialized with the other macro classes (AdvancedAxisMacroTests
    // documents the race).
    [Collection("SettingsManagerStatics")]
    public class PressureMacroTests
    {
        /// <summary>A trigger button no pressure rides.</summary>
        private const ushort Trigger = Gamepad.BACK;

        private const string Full = "dualshock-3-full";

        private static readonly ushort[] Bits =
        {
            Gamepad.A, Gamepad.B, Gamepad.X, Gamepad.Y,
            Gamepad.LEFT_SHOULDER, Gamepad.RIGHT_SHOULDER,
            Gamepad.DPAD_UP, Gamepad.DPAD_DOWN, Gamepad.DPAD_LEFT, Gamepad.DPAD_RIGHT,
        };

        public static IEnumerable<object[]> Targets()
            => Enumerable.Range(0, 10).Select(i => new object[] { MacroAxisTarget.PressureButtonA + i, i });

        private static MacroItem Macro(MacroTriggerMode mode, MacroRepeatMode repeat, ushort trigger, bool consume,
            params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "Pressure",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = trigger,
                TriggerMode = mode,
                RepeatMode = repeat,
                ConsumeTriggerButtons = consume,
            };
            foreach (var a in actions) m.Actions.Add(a);
            return m;
        }

        private static MacroItem Held(params MacroAction[] actions)
            => Macro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease, Trigger, false, actions);

        private static MacroAction Action(MacroActionType type, MacroAxisTarget target, short value,
            bool yield = false)
            => new MacroAction
            {
                Type = type,
                AxisTarget = target,
                AxisValue = value,
                DurationMs = 60000,
                AxisYieldToPhysical = yield,
            };

        /// <summary>One pass of the three-argument evaluator.</summary>
        private static (Gamepad Gp, ButtonPressureState Pressure) Pass(InputManager im, MacroItem[] macros,
            ushort buttons, ButtonPressureState pressure = default)
        {
            var gp = new Gamepad { Buttons = buttons };
            im.EvaluateSlotMacros(ref gp, macros, ref pressure);
            return (gp, pressure);
        }

        private static bool Pressed(in Gamepad gp, ushort bit) => (gp.Buttons & bit) != 0;

        // ── The pull scale ──

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(9830, 76)]
        [InlineData(16384, 128)]
        [InlineData(32767, 255)]
        public void ThePullScale_IsAPressure_AndALightPressNeverRoundsToARelease(int value, int pressure)
            => Assert.Equal(pressure, InputManager.PressureFromPull(value));

        // ── Set, Hold, Latched, Toggle, Repeat ──

        [Theory]
        [MemberData(nameof(Targets))]
        public void SetAxis_PressesItsButtonAtThePressure(MacroAxisTarget target, int index)
        {
            var im = new InputManager();
            var (gp, pressure) = Pass(im, new[] { Held(Action(MacroActionType.AxisSet, target, 16384)) }, Trigger);

            Assert.True(Pressed(gp, Bits[index]), target + " is not pressed");
            for (int i = 0; i < ButtonPressureState.Count; i++)
                Assert.Equal(i == index ? 128 : 0, pressure[i]);
            Assert.Equal(Trigger | Bits[index], gp.Buttons);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-16384)]
        public void SetAxis_AtZeroOrBelow_ReleasesThePhysicalPress(short value)
        {
            var im = new InputManager();
            var start = new ButtonPressureState { ButtonA = 200 };
            var (gp, pressure) = Pass(im, new[] { Held(Action(MacroActionType.AxisSet, MacroAxisTarget.PressureButtonA, value)) },
                Trigger | Gamepad.A, start);

            Assert.False(Pressed(gp, Gamepad.A));
            Assert.Equal(0, pressure.ButtonA);
        }

        [Fact]
        public void HoldAxis_WritesEveryFrame_AndTheButtonReturnsWithTheTrigger()
        {
            var im = new InputManager();
            var macros = new[] { Held(Action(MacroActionType.AxisHold, MacroAxisTarget.PressureButtonB, 8192)) };

            for (int frame = 0; frame < 3; frame++)
            {
                var (gp, pressure) = Pass(im, macros, Trigger);
                Assert.True(Pressed(gp, Gamepad.B));
                Assert.Equal(64, pressure.ButtonB);
            }

            var (released, after) = Pass(im, macros, 0);
            Assert.False(Pressed(released, Gamepad.B));
            Assert.Equal(0, after.ButtonB);
        }

        [Fact]
        public void SetAxisLatched_HoldsThePressureAfterTheTriggerLetsGo()
        {
            var im = new InputManager();
            var macros = new[]
            {
                Macro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, Trigger, false,
                    Action(MacroActionType.AxisSetLatched, MacroAxisTarget.PressureButtonY, 9830)),
            };

            Pass(im, macros, Trigger);
            for (int frame = 0; frame < 2; frame++)
            {
                var (gp, pressure) = Pass(im, macros, 0);
                Assert.True(Pressed(gp, Gamepad.Y));
                Assert.Equal(76, pressure.ButtonY);
            }
        }

        [Fact]
        public void ToggleAxis_LatchesThePressOnAndOff()
        {
            var im = new InputManager();
            var macros = new[]
            {
                Macro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, Trigger, false,
                    Action(MacroActionType.ToggleVcAxis, MacroAxisTarget.PressureLeftShoulder, 32767)),
            };

            Pass(im, macros, Trigger);
            var (on, onPressure) = Pass(im, macros, 0);
            Assert.True(Pressed(on, Gamepad.LEFT_SHOULDER));
            Assert.Equal(255, onPressure.LeftShoulder);

            Pass(im, macros, Trigger);
            var (off, offPressure) = Pass(im, macros, 0);
            Assert.False(Pressed(off, Gamepad.LEFT_SHOULDER));
            Assert.Equal(0, offPressure.LeftShoulder);
        }

        [Fact]
        public void RepeatAxisWhileHeld_PressesOnItsOnHalf()
        {
            var im = new InputManager();
            var action = Action(MacroActionType.RepeatVcAxisWhileHeld, MacroAxisTarget.PressureDPadUp, 19660);
            action.IntervalMs = 60000;
            var (gp, pressure) = Pass(im, new[] { Held(action) }, Trigger);

            Assert.True(Pressed(gp, Gamepad.DPAD_UP));
            Assert.Equal(InputManager.PressureFromPull(19660), pressure.DPadUp);
        }

        [Fact]
        public void ACycleTapListAxisStep_WritesAPressure()
        {
            var im = new InputManager();
            var action = new MacroAction
            {
                Type = MacroActionType.CycleTapList,
                CycleStepsCsv = $"A:{(int)MacroAxisTarget.PressureDPadLeft}:16384",
                DurationMs = 60000,
            };
            var (gp, pressure) = Pass(im, new[] { Held(action) }, Trigger);

            Assert.True(Pressed(gp, Gamepad.DPAD_LEFT));
            Assert.Equal(128, pressure.DPadLeft);
        }

        // ── Add and Scale read the pressure the button has now ──

        [Theory]
        [InlineData(100, true, 6553, 151)]     // pressed at 100, +20%
        [InlineData(0, true, -16383, 128)]     // a digital press reads 255, -50%
        [InlineData(0, false, 13107, 102)]     // released, +40% presses it
        [InlineData(250, true, 6553, 255)]     // clamped at a full press
        [InlineData(0, true, -32767, 0)]       // -100% of a full press releases it
        public void AxisAdd_AddsOntoThePressureTheButtonHasNow(int start, bool pressed, short add, int expected)
        {
            var im = new InputManager();
            var macros = new[] { Held(Action(MacroActionType.AxisAdd, MacroAxisTarget.PressureButtonX, add)) };
            var (gp, pressure) = Pass(im, macros, (ushort)(Trigger | (pressed ? Gamepad.X : 0)),
                new ButtonPressureState { ButtonX = (byte)start });

            Assert.Equal(expected, pressure.ButtonX);
            Assert.Equal(expected > 0, Pressed(gp, Gamepad.X));
        }

        [Theory]
        [InlineData(0, true, -19660, 102)]     // a digital press at 40%, the Pressure Modifier's job
        [InlineData(200, true, -16384, 100)]   // halved
        [InlineData(0, false, 16384, 0)]       // a released button stays released
        [InlineData(1, true, -19660, 1)]       // a light press scaled down stays pressed
        [InlineData(180, true, -32767, 0)]     // -100% releases
        public void ScaleAxis_ScalesThePressure_ADigitalPressCountingAsFull(int start, bool pressed, short scale, int expected)
        {
            var im = new InputManager();
            var macros = new[] { Held(Action(MacroActionType.AxisScale, MacroAxisTarget.PressureButtonA, scale)) };
            var (gp, pressure) = Pass(im, macros, (ushort)(Trigger | (pressed ? Gamepad.A : 0)),
                new ButtonPressureState { ButtonA = (byte)start });

            Assert.Equal(expected, pressure.ButtonA);
            Assert.Equal(expected > 0, Pressed(gp, Gamepad.A));
        }

        // ── The pass's overlay and yield gate ──

        [Fact]
        public void TheOverlay_KeepsThePress_WhenTheMacroConsumesItsOwnTrigger()
        {
            var im = new InputManager();
            var macros = new[]
            {
                Macro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease, Gamepad.A, true,
                    Action(MacroActionType.AxisHold, MacroAxisTarget.PressureButtonA, 13107)),
            };
            var (gp, pressure) = Pass(im, macros, Gamepad.A);

            Assert.True(Pressed(gp, Gamepad.A));
            Assert.Equal(102, pressure.ButtonA);
        }

        [Fact]
        public void TheConsumedTrigger_StaysConsumed_WhenTheMacroWritesAnotherButton()
        {
            var im = new InputManager();
            var macros = new[]
            {
                Macro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease, Gamepad.A, true,
                    Action(MacroActionType.AxisHold, MacroAxisTarget.PressureButtonB, 13107)),
            };
            var (gp, pressure) = Pass(im, macros, Gamepad.A);

            Assert.False(Pressed(gp, Gamepad.A));
            Assert.True(Pressed(gp, Gamepad.B));
            Assert.Equal(102, pressure.ButtonB);
        }

        [Fact]
        public void ALaterReleaseInThePass_IsNotUndoneByTheOverlay()
        {
            // Both macros consume their trigger, so the overlay re-asserts
            // what the pass pressed. The second one's pressure of 0 releases
            // Cross, and the overlay must not bring it back.
            var im = new InputManager();
            var press = Macro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease, Trigger, true,
                new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.A, DurationMs = 60000 });
            var release = Macro(MacroTriggerMode.WhileHeld, MacroRepeatMode.UntilRelease, Trigger, true,
                Action(MacroActionType.AxisHold, MacroAxisTarget.PressureButtonA, 0));
            var (gp, pressure) = Pass(im, new[] { press, release }, Trigger);

            Assert.False(Pressed(gp, Gamepad.A));
            Assert.False(Pressed(gp, Trigger));
            Assert.Equal(0, pressure.ButtonA);
        }

        [Fact]
        public void Yield_APhysicalPressTakesTheButtonBack_ForTheActivation()
        {
            var im = new InputManager();
            var macros = new[] { Held(Action(MacroActionType.AxisHold, MacroAxisTarget.PressureButtonX, 6553, yield: true)) };

            var (first, firstPressure) = Pass(im, macros, Trigger);
            Assert.True(Pressed(first, Gamepad.X));
            Assert.Equal(51, firstPressure.ButtonX);

            // A digital press reads as full, past the 12.5% threshold.
            var (yielded, yieldedPressure) = Pass(im, macros, Trigger | Gamepad.X);
            Assert.True(Pressed(yielded, Gamepad.X));
            Assert.Equal(0, yieldedPressure.ButtonX);

            // Latched for the activation, so the macro stays out.
            var (after, _) = Pass(im, macros, Trigger);
            Assert.False(Pressed(after, Gamepad.X));
        }

        [Fact]
        public void TheEvaluator_CopiesThePressureInAndOut()
        {
            var im = new InputManager();
            var macros = new[] { Held(Action(MacroActionType.AxisHold, MacroAxisTarget.PressureButtonA, 16384)) };
            var (gp, pressure) = Pass(im, macros, Trigger | Gamepad.B,
                new ButtonPressureState { ButtonB = 90, DPadUp = 40 });

            Assert.Equal(128, pressure.ButtonA);
            Assert.Equal(90, pressure.ButtonB);
            Assert.Equal(40, pressure.DPadUp);
            Assert.True(Pressed(gp, Gamepad.B));
        }

        [Fact]
        public void TheTwoArgumentEvaluator_LeavesPressureTargetsAlone()
        {
            var im = new InputManager();
            var macros = new[] { Held(Action(MacroActionType.AxisHold, MacroAxisTarget.PressureButtonA, 16384)) };
            var gp = new Gamepad { Buttons = Trigger };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(Trigger, gp.Buttons);
        }

        [Fact]
        public void TheRawLane_LeavesAPressureTargetAlone()
        {
            var im = new InputManager();
            var m = new MacroItem { Name = "Raw", IsEnabled = true, PadIndex = 0, TriggerButtons = 1,
                TriggerMode = MacroTriggerMode.WhileHeld, RepeatMode = MacroRepeatMode.UntilRelease };
            m.Actions.Add(Action(MacroActionType.AxisHold, MacroAxisTarget.PressureButtonA, 32767));
            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            var axes = (short[])raw.Axes.Clone();

            im.EvaluateSlotMacrosExtended(ref raw, new[] { m });

            Assert.Equal(axes, raw.Axes);
            Assert.Equal(1u, raw.Buttons[0]);
        }

        [Fact]
        public void TheWire_CarriesTheMacrosPressure()
        {
            var im = new InputManager();
            var macros = new[] { Held(Action(MacroActionType.AxisHold, MacroAxisTarget.PressureRightShoulder, 9830)) };
            var (gp, pressure) = Pass(im, macros, Trigger);

            var wire = HMaestroVirtualController.PressureOnTheWire(pressure, gp);
            Assert.Equal(76, wire.RightShoulder);
            Assert.Equal(0, wire.ButtonA);
        }

        [Fact]
        public void Step4b_HandsTheSlotsPressureToTheEvaluator()
        {
            string code = Source("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Contains("EvaluateSlotMacros(ref CombinedOutputStates[i], macros, ref CombinedPressureStates[i]);", code);
            Assert.DoesNotContain("EvaluateSlotMacros(ref CombinedOutputStates[i], macros);", code);
        }

        // ── The targets ──

        [Fact]
        public void TheTargets_KeepTheirNumbers_AndTheirOrder()
        {
            Assert.Equal(6, (int)MacroAxisTarget.RightTrigger);
            string[] names = PadForge.Engine.Data.MappingSetMigrator.PressureTargets.ToArray();
            for (int i = 0; i < names.Length; i++)
            {
                var target = MacroAxisTarget.PressureButtonA + i;
                Assert.Equal(7 + i, (int)target);
                Assert.Equal(names[i], target.ToString());
                Assert.Equal(i, target.PressureIndex());
                Assert.Equal(Bits[i], InputManager.PressureButtonBit(i));
            }
            Assert.Equal(16, (int)Enum.GetValues(typeof(MacroAxisTarget)).Cast<MacroAxisTarget>().Max());
            Assert.Equal(-1, MacroAxisTarget.RightTrigger.PressureIndex());
            Assert.Equal(-1, MacroAxisTarget.None.PressureIndex());
        }

        [Fact]
        public void TheLabels_AreThePressureRowsLabels()
        {
            var s = Strings.Instance;
            string[] buttons =
            {
                "✕", "○", "◻", "△", "L1", "R1",
                s.Btn_DPadUp, s.Btn_DPadDown, s.Btn_DPadLeft, s.Btn_DPadRight,
            };
            var labels = MacroPressureLabels.Instance;
            string[] bound =
            {
                labels.ButtonA, labels.ButtonB, labels.ButtonX, labels.ButtonY,
                labels.LeftShoulder, labels.RightShoulder,
                labels.DPadUp, labels.DPadDown, labels.DPadLeft, labels.DPadRight,
            };
            for (int i = 0; i < buttons.Length; i++)
            {
                string expected = string.Format(s.DevObj_Pressure_Format, buttons[i]);
                Assert.Equal(expected, (MacroAxisTarget.PressureButtonA + i).DisplayName());
                Assert.Equal(expected, bound[i]);
            }
        }

        [Fact]
        public void APressureTarget_SurvivesTheSettingsRoundTrip()
        {
            var m = new MacroItem { Name = "RT" };
            m.Actions.Add(Action(MacroActionType.AxisSetLatched, MacroAxisTarget.PressureDPadRight, 16384));
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            var back = SettingsService.LoadMacroFromData(data, VirtualControllerType.PlayStation, null, Full);
            var action = Assert.Single(back.Actions);
            Assert.Equal(MacroAxisTarget.PressureDPadRight, action.AxisTarget);
            Assert.Equal(16384, action.AxisValue);
        }

        // ── The editor ──

        [Theory]
        [InlineData(VirtualControllerType.PlayStation, Full, true)]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-3", false)]
        [InlineData(VirtualControllerType.PlayStation, "dualsense", false)]
        [InlineData(VirtualControllerType.Xbox, Full, false)]
        [InlineData(VirtualControllerType.Extended, Full, false)]
        public void ALoadedMacro_CarriesPressureOnTheFullPresetAlone(VirtualControllerType type, string profile, bool expected)
        {
            var m = new MacroItem { Name = "Load" };
            m.Actions.Add(Action(MacroActionType.AxisHold, MacroAxisTarget.LeftTrigger, 100));
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            var back = SettingsService.LoadMacroFromData(data, type, null, profile);

            Assert.Equal(expected, back.CarriesPressure);
            Assert.All(back.Actions, a => Assert.Equal(expected, a.CarriesPressure));
        }

        [Fact]
        public void TheSlot_StampsCarriesPressure_AndAddAndDuplicateCarryIt()
        {
            var vm = RestoredPad.Build(0, VirtualControllerType.PlayStation, Full);
            vm.AddMacroCommand.Execute(null);
            var macro = vm.Macros.Last();
            Assert.True(macro.CarriesPressure);

            macro.AddActionCommand.Execute(null);
            var added = macro.Actions.Last();
            Assert.True(added.CarriesPressure);
            macro.SelectedAction = added;
            macro.DuplicateActionCommand.Execute(null);
            Assert.True(macro.Actions.Last().CarriesPressure);

            // Another preset on the slot clears it on the macro and its actions.
            Restore(vm, "dualshock-3");
            Assert.False(macro.CarriesPressure);
            Assert.All(macro.Actions, a => Assert.False(a.CarriesPressure));

            Restore(vm, Full);
            Assert.True(macro.CarriesPressure);
            Assert.All(macro.Actions, a => Assert.True(a.CarriesPressure));
        }

        [Fact]
        public void TheEditor_OffersTheTargetsInTheTwoWriterPickers_OnlyWhileTheSlotCarriesPressure()
        {
            string xaml = Source("PadForge.App", "Views", "PadPage.xaml");
            string[] members = Enumerable.Range(0, 10).Select(i => (MacroAxisTarget.PressureButtonA + i).ToString()).ToArray();
            string[] labels =
            {
                "ButtonA", "ButtonB", "ButtonX", "ButtonY", "LeftShoulder", "RightShoulder",
                "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
            };
            var gates = new List<string>();
            for (int i = 0; i < members.Length; i++)
            {
                string tag = "Tag=\"{x:Static vm:MacroAxisTarget." + members[i] + "}\"";
                int at = -1, found = 0;
                while ((at = xaml.IndexOf(tag, at + 1, StringComparison.Ordinal)) >= 0)
                {
                    found++;
                    int itemStart = xaml.LastIndexOf("<ComboBoxItem", at, StringComparison.Ordinal);
                    int itemEnd = xaml.IndexOf("/>", at, StringComparison.Ordinal);
                    string item = xaml.Substring(itemStart, itemEnd - itemStart);
                    Assert.Contains("Content=\"{Binding " + labels[i] + ", Source={x:Static vm:MacroPressureLabels.Instance}}\"", item);
                    Assert.Contains("Visibility=\"{Binding CarriesPressure, Converter={StaticResource BoolToVisibilityConverter}}\"", item);

                    // The picker's group of fields is a template of the steps'
                    // list, and the part of the fields that holds it shows by
                    // the action family's flag.
                    int combo = xaml.LastIndexOf("<ComboBox ", at, StringComparison.Ordinal);
                    Assert.Contains("SelectedValue=\"{Binding AxisTarget, Mode=TwoWay}\"",
                        xaml.Substring(combo, xaml.IndexOf('>', combo) - combo));
                    const string opens = "<DataTemplate x:Key=\"";
                    int group = xaml.LastIndexOf(opens + "StepFields", combo, StringComparison.Ordinal);
                    Assert.True(group >= 0);
                    string key = xaml.Substring(group + opens.Length, xaml.IndexOf('"', group + opens.Length) - group - opens.Length);
                    string part = "<controls:ShownPart PartTemplate=\"{StaticResource " + key + "}\" Show=\"{Binding ";
                    gates.Add(xaml.Contains(part + "IsAnyAxisValueType}\"/>", StringComparison.Ordinal) ? "write"
                        : xaml.Contains(part + "IsAxisLatchReleaseType}\"/>", StringComparison.Ordinal) ? "release" : key);
                }
                Assert.Equal(2, found);
            }
            Assert.Equal(10, gates.Count(g => g == "write"));
            Assert.Equal(10, gates.Count(g => g == "release"));
        }

        // ── Helpers ──

        private static void Restore(PadViewModel vm, string profile)
        {
            string stamp = SettingsManager.GetWireStamp(vm.PadIndex);
            SettingsManager.StampNintendoWire(vm.PadIndex, profile);
            try { vm.ProfileId = profile; }
            finally { SettingsManager.StampNintendoWire(vm.PadIndex, stamp); }
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "PadForge.App"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }
    }
}
