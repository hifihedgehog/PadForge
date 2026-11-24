using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Center Mouse Steering macro action (#597): one press puts every
    /// "Mouse Travel" wheel of its own slot straight, on a gamepad slot and
    /// on an Extended one, and the action has its place in the picker, its
    /// card in the editor and its saved form. The wheels are the slot
    /// runtimes the app keeps, which a poll of any test stamps with its
    /// frame, so the class runs in the collection of the tests that poll.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class CenterMouseSteeringMacroTests
    {
        // Slots and a row no other test reads.
        private const int GamepadSlot = 11, ExtendedSlot = 8;
        private const string Target = "CenterMouseSteeringTestAxis";

        private static readonly MappingSource Wheel = new() { Kind = "Direct", Descriptor = "Mouse Travel X" };

        /// <summary>Turns the slot's wheel a tenth of the way to its lock
        /// and answers where it stands.</summary>
        private static double Turn(int slot)
        {
            var rt = InputManager.GetSlotSourceKindRuntime(slot);
            rt.ResetForRow(slot, Target);
            rt.FrameSeq++;
            return rt.TickMouseTravel(slot, Target, 0, Wheel, new CustomInputState { MouseRawDX = 200 }, 0.001, false);
        }

        /// <summary>Where the slot's wheel stands a frame later, with the
        /// mouse still.</summary>
        private static double Standing(int slot)
        {
            var rt = InputManager.GetSlotSourceKindRuntime(slot);
            rt.FrameSeq++;
            return rt.TickMouseTravel(slot, Target, 0, Wheel, new CustomInputState(), 0.001, false);
        }

        // Nothing here types: the action sends no key. The key fields hold
        // the values a macro test gives them all the same.
        private static MacroAction Center() => new()
        {
            Type = MacroActionType.CenterMouseSteering,
            KeyString = "",
            KeyCode = 0xFC,
        };

        private static MacroItem Macro(int slot, bool extended)
        {
            var m = new MacroItem
            {
                Name = "Center",
                IsEnabled = true,
                PadIndex = slot,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            if (extended) m.TriggerCustomButtons = "00000001,00000000,00000000,00000000";
            else m.TriggerButtons = Gamepad.A;
            m.Actions.Add(Center());
            return m;
        }

        [Fact]
        public void OnAGamepadSlot_OnePressPutsTheWheelStraight()
        {
            Assert.Equal(0.1, Turn(GamepadSlot));
            Assert.Equal(0.1, Standing(GamepadSlot));

            var macros = new[] { Macro(GamepadSlot, extended: false) };
            var gp = new Gamepad { Buttons = Gamepad.A };
            new InputManager().EvaluateSlotMacros(ref gp, macros);

            Assert.Equal(0.0, Standing(GamepadSlot));
            // The macro went past the action: a loop with no case for it
            // would leave the macro waiting on it for good.
            Assert.False(macros[0].IsExecuting && macros[0].CurrentActionIndex == 0);
        }

        [Fact]
        public void OnAnExtendedSlot_OnePressPutsTheWheelStraight()
        {
            Assert.Equal(0.1, Turn(ExtendedSlot));

            var macros = new[] { Macro(ExtendedSlot, extended: true) };
            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 0x1;
            new InputManager().EvaluateSlotMacrosExtended(ref raw, macros);

            Assert.Equal(0.0, Standing(ExtendedSlot));
        }

        [Fact]
        public void ThePress_CentersItsOwnSlotOnly()
        {
            Assert.Equal(0.1, Turn(GamepadSlot));
            Assert.Equal(0.1, Turn(ExtendedSlot));

            var macros = new[] { Macro(GamepadSlot, extended: false) };
            var gp = new Gamepad { Buttons = Gamepad.A };
            new InputManager().EvaluateSlotMacros(ref gp, macros);

            Assert.Equal(0.0, Standing(GamepadSlot));
            Assert.Equal(0.1, Standing(ExtendedSlot));
        }

        [Fact]
        public void ASlotOutsideTheRange_DoesNothing()
        {
            Assert.Equal(0.1, Turn(GamepadSlot));
            foreach (int slot in new[] { -1, InputManager.MaxPads })
            {
                var macros = new[] { Macro(slot, extended: false) };
                var gp = new Gamepad { Buttons = Gamepad.A };
                new InputManager().EvaluateSlotMacros(ref gp, macros);
            }
            Assert.Equal(0.1, Standing(GamepadSlot));
        }

        [Fact]
        public void TheAction_IsAppendedAt71_AndSitsInBothLoops()
        {
            Assert.Equal(71, (int)MacroActionType.CenterMouseSteering);
            Assert.Equal(MacroActionType.CenterMouseSteering, Enum.GetValues<MacroActionType>()[^2]);

            string loops = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Equal(2, loops.Split("case MacroActionType.CenterMouseSteering:").Length - 1);
            Assert.Equal(2, loops.Split("ApplyCenterMouseSteeringAction(macro);").Length - 1);
        }

        [Fact]
        public void TheAction_HasItsPlaceInThePicker_ItsNameAndItsCard()
        {
            var choice = Assert.Single(MacroTypeCatalog.Choices, c => c.Type == MacroActionType.CenterMouseSteering);
            Assert.Equal(Strings.Instance.MacroAction_Type_CenterMouseSteering, choice.Label);
            Assert.Equal(Strings.Instance.Macro_Cat_Mouse, choice.Category);
            Assert.Equal("Center Mouse Steering", Strings.Instance.MacroAction_Type_CenterMouseSteering);

            var action = Center();
            Assert.Equal(Strings.Instance.MacroAction_Type_CenterMouseSteering, action.DisplayText);
            Assert.True(action.IsCenterMouseSteeringType);
            Assert.False(action.IsGyroRecenterType);

            // The card shows for this action and no other, and follows a
            // change of the action.
            var said = new System.Collections.Generic.List<string>();
            action.PropertyChanged += (_, e) => said.Add(e.PropertyName);
            action.Type = MacroActionType.GyroRecenter;
            Assert.False(action.IsCenterMouseSteeringType);
            Assert.Contains(nameof(MacroAction.IsCenterMouseSteeringType), said);

            string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml");
            Assert.Contains("<DataTemplate x:Key=\"StepFieldsCenterMouseSteering\">", page);
            Assert.Contains("PartTemplate=\"{StaticResource StepFieldsCenterMouseSteering}\" Show=\"{Binding IsCenterMouseSteeringType}\"", page);
            Assert.Contains("{Binding MacroAction_CenterMouseSteering_Tooltip, Source={x:Static strings:Strings.Instance}}", page);
        }

        [Fact]
        public void TheAction_RoundTripsThroughTheSavedForm()
        {
            var m = new MacroItem { Name = "RT" };
            m.Actions.Add(Center());
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            Assert.Equal(MacroActionType.CenterMouseSteering, data.Actions.Single().Type);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            Assert.Equal(MacroActionType.CenterMouseSteering, Assert.Single(clone.Actions).Type);
        }
    }
}
