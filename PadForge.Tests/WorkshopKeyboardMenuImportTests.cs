using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Steam's Show Keyboard imported as a keyboard menu (#566): opened by
    /// the binding's input, with Long_Press and Double_Press as Opens On and
    /// their times, on the binding's layer, on one slot only.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopKeyboardMenuImportTests
    {
        private static TranslatedMacro Show(string mode, params string[] inputs) => new()
        {
            Name = "On-screen keyboard (button_y)",
            Action = TranslatedMacroAction.ShowOnScreenKeyboard,
            TriggerMode = mode,
            TriggerInputDescriptors = inputs.ToList(),
        };

        [Fact]
        public void ShowKeyboard_ImportsAsAKeyboardMenu_OpenedByTheSameInput()
        {
            var t = new TranslatedProfile { Name = "Osk", NeedsXboxSlot = true };
            t.Macros.Add(Show("OnPress", "Gamepad ButtonY"));
            var p = WorkshopProfileMaterializer.Materialize(t);
            // No macro launches a program for it any more.
            Assert.Empty(p.Macros);
            var menu = Assert.Single(p.SlotMappingSets[0].Menus);
            Assert.Equal(MenuKind.Keyboard, menu.Kind);
            Assert.Equal("On-screen keyboard (button_y)", menu.Name);
            Assert.Equal(new KeyboardInput("Gamepad ButtonY"), KeyboardMenuDefaults.OpenInput(menu));
            Assert.Equal(KeyboardOpenPress.Press, menu.OpenPress);
            Assert.Equal(KeyboardMenuDefaults.SteerDeadzonePercent, menu.EngageDeadzonePercent);
            Assert.Equal(KeyboardMenuDefaults.PosYPercent, menu.PosYPercent);
            Assert.True(menu.Enabled);
        }

        [Fact]
        public void ASplitConfig_GetsOneKeyboardMenu_OnTheXboxSlot_AfterItsOtherMenus()
        {
            var t = new TranslatedProfile { Name = "Osk", NeedsXboxSlot = true, NeedsKbmSlot = true };
            t.Menus.Add(new MenuDefinitionEntry { MenuId = 1, Kind = MenuKind.Radial, CellCount = 4 });
            t.Macros.Add(Show("OnPress", "Gamepad ButtonY"));
            var p = WorkshopProfileMaterializer.Materialize(t);
            Assert.DoesNotContain(p.SlotMappingSets[1].Menus, m => m.Kind == MenuKind.Keyboard);
            var keyboard = Assert.Single(p.SlotMappingSets[0].Menus, m => m.Kind == MenuKind.Keyboard);
            Assert.Equal(2, keyboard.MenuId);
        }

        [Fact]
        public void AKeyboardOnlyConfig_PutsItOnTheKeyboardAndMouseSlot()
        {
            var t = new TranslatedProfile { Name = "Osk", NeedsKbmSlot = true };
            t.Macros.Add(Show("OnPress", "Gamepad ButtonY"));
            var p = WorkshopProfileMaterializer.Materialize(t);
            Assert.Single(p.SlotMappingSets[0].Menus, m => m.Kind == MenuKind.Keyboard);
        }

        [Fact]
        public void ALongPressADoublePressAndARelease_ImportAsTheirPresses()
        {
            var hold = Show("HoldForMs", "Gamepad LeftShoulder");
            hold.TriggerHoldMs = 650;
            var menu = WorkshopProfileMaterializer.KeyboardMenuFor(hold, 1);
            Assert.Equal(KeyboardOpenPress.Hold, menu.OpenPress);
            Assert.Equal(650, menu.OpenPressMs);

            var twice = Show("DoublePress", "Gamepad ButtonBack");
            twice.TriggerDoublePressMs = 300;
            menu = WorkshopProfileMaterializer.KeyboardMenuFor(twice, 1);
            Assert.Equal(KeyboardOpenPress.DoublePress, menu.OpenPress);
            Assert.Equal(300, menu.OpenPressMs);

            // No window authored: the macro trigger's default.
            menu = WorkshopProfileMaterializer.KeyboardMenuFor(Show("DoublePress", "Gamepad ButtonBack"), 1);
            Assert.Equal(0, menu.OpenPressMs);
            Assert.Equal(KeyboardMenuDefaults.DefaultDoublePressMs, KeyboardMenuDefaults.OpenPressMs(menu));

            // Release opens it when the input is let go, in Steam and here.
            Assert.Equal(KeyboardOpenPress.Release, WorkshopProfileMaterializer.KeyboardMenuFor(Show("OnRelease", "Gamepad ButtonA"), 1).OpenPress);
        }

        [Fact]
        public void AStickHalfAndASecondInput_ImportAsOpensWith_OnTheBindingsLayer()
        {
            var m = Show("OnPress", "Gamepad LeftStickX", "Gamepad ButtonA");
            m.TriggerDescriptorHalfAxis = true;
            m.TriggerDescriptorInvert = true;
            m.LayerMask = "L3";
            var menu = WorkshopProfileMaterializer.KeyboardMenuFor(m, 3);
            Assert.Equal(3, menu.MenuId);
            // The half goes on the axis only, never on the button.
            Assert.Equal(new KeyboardInput("IHGamepad LeftStickX", "", "Gamepad ButtonA", ""), KeyboardMenuDefaults.OpenInput(menu));
            Assert.Equal("L3", menu.LayerMask);

            var trigger = Show("OnPress", "Gamepad RightTrigger");
            trigger.TriggerDescriptorHalfAxis = true;
            Assert.Equal("HGamepad RightTrigger", WorkshopProfileMaterializer.KeyboardMenuFor(trigger, 1).OpenDescriptor);
            Assert.Equal(0, WorkshopProfileMaterializer.KeyboardMenuFor(trigger, 1).OpenThreshold);

            // A trigger click's own threshold is Opens With's press point, as
            // it is a macro trigger entry's deadzone.
            trigger.TriggerDescriptorDeadZonePercent = 75;
            var click = WorkshopProfileMaterializer.KeyboardMenuFor(trigger, 1);
            Assert.Equal(new KeyboardInput("HGamepad RightTrigger", "", "", "", 75), KeyboardMenuDefaults.OpenInput(click));
            // With no half to read, the input has no threshold of its own.
            var whole = Show("OnPress", "Gamepad ButtonY");
            whole.TriggerDescriptorDeadZonePercent = 75;
            Assert.Equal(0, WorkshopProfileMaterializer.KeyboardMenuFor(whole, 1).OpenThreshold);
        }

        [Fact]
        public void ABindingWithNoInput_ImportsNoMenu()
        {
            Assert.Null(WorkshopProfileMaterializer.KeyboardMenuFor(Show("OnPress"), 1));
            Assert.Null(WorkshopProfileMaterializer.KeyboardMenuFor(Show("OnPress", " "), 1));
        }
    }
}
