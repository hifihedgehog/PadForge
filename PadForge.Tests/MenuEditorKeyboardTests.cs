using System.Collections.Generic;
using System.Linq;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Menus tab's editor for a keyboard menu (#566): the Keyboard style,
    /// the rows each layout lists, steering and pointers among them, a
    /// recorded press or axis on a row, each row's list, the press point, the
    /// resets, Types Through's list, Opens On's press modes and time, and the
    /// D-pad and face buttons in a ring's opener list.
    /// </summary>
    public class MenuEditorKeyboardTests
    {
        private const string G = "11111111-2222-3333-4444-555555555555";

        private static MenuEditorItem Keyboard()
            => new(KeyboardMenuDefaults.Create(1, "Keyboard"));

        private static KeyboardInputRow Row(MenuEditorItem e, KeyboardControlAction a)
            => e.KeyboardControlRows.Single(r => r.Action == a);

        private static RecorderService.RecordedPress Press(string d, string g = "", string and = "", string andG = "")
            => new(d, g, and, andG);

        [Fact]
        public void TheStyleList_OffersTheKeyboard_AndTurningAMenuIntoOne_TakesItsDefaults()
        {
            var e = new MenuEditorItem(new MenuDefinitionEntry { MenuId = 1, Kind = MenuKind.Radial, CellCount = 4, HasCenter = true });
            // Radial Ring, Touch Grid, Keyboard and Hotbar (MenuHotbarEditorTests).
            Assert.Equal(new[] { 0, 1, 2, 3 }, e.KindOptions.Select(o => o.Value));
            Assert.Empty(e.KeyboardControlRows);
            Assert.True(e.IsCellMenu);

            e.KindIndex = 2;
            Assert.Equal(MenuKind.Keyboard, e.Entry.Kind);
            Assert.True(e.IsKeyboard);
            Assert.False(e.IsCellMenu);
            Assert.False(e.ShowsClickRow);
            // A keyboard reads no Center Cell, so the ring's stays for a turn back.
            Assert.True(e.Entry.HasCenter);
            // Steering waits for half a push and the keyboard sits at the
            // bottom, as the Windows and Steam keyboards do.
            Assert.Equal(KeyboardMenuDefaults.SteerDeadzonePercent, e.Entry.EngageDeadzonePercent);
            Assert.Equal(KeyboardMenuDefaults.PosYPercent, e.Entry.PosYPercent);
            Assert.Equal(KeyboardMenuDefaults.Actions(KeyboardLayout.Grid), e.KeyboardControlRows.Select(r => r.Action.Value));

            // A choice the user made is kept.
            var touch = new MenuEditorItem(new MenuDefinitionEntry { MenuId = 2, Kind = MenuKind.Grid, PosYPercent = 20, EngageDeadzonePercent = 40 });
            touch.KindIndex = 2;
            Assert.Equal(20, touch.Entry.PosYPercent);
            Assert.Equal(40, touch.Entry.EngageDeadzonePercent);

            // And back: no keyboard rows on a cell menu, and the ring takes
            // back its own place, deadzone and Center Cell.
            e.KindIndex = 0;
            Assert.Empty(e.KeyboardControlRows);
            Assert.True(e.IsCellMenu);
            Assert.True(e.Entry.HasCenter);
            Assert.Equal(50, e.Entry.PosYPercent);
            Assert.Equal(25, e.Entry.EngageDeadzonePercent);

            // A place or a deadzone the user set on the keyboard stays.
            var set = Keyboard();
            set.Entry.PosYPercent = 80;
            set.Entry.EngageDeadzonePercent = 60;
            set.KindIndex = 0;
            Assert.Equal(80, set.Entry.PosYPercent);
            Assert.Equal(60, set.Entry.EngageDeadzonePercent);
            // A touch grid loses its Center Cell, as before.
            var grid = new MenuEditorItem(new MenuDefinitionEntry { MenuId = 3, Kind = MenuKind.Radial, CellCount = 4, HasCenter = true });
            grid.KindIndex = 1;
            Assert.False(grid.Entry.HasCenter);
        }

        [Fact]
        public void EachLayout_ListsItsOwnControls()
        {
            var e = Keyboard();
            foreach (var layout in new[] { KeyboardLayout.Split, KeyboardLayout.Wheel, KeyboardLayout.Grid })
            {
                e.KeyboardLayoutIndex = (int)layout;
                Assert.Equal(layout, e.Entry.KeyboardLayout);
                Assert.Equal(KeyboardMenuDefaults.Actions(layout), e.KeyboardControlRows.Select(r => r.Action.Value));
                Assert.All(e.KeyboardControlRows, r => Assert.Equal(layout, r.Layout));
            }
            // The rows' ids are the controls', for UI Automation.
            Assert.Equal("MenuKeyboard_TypeKey", Row(e, KeyboardControlAction.TypeKey).AutomationId);
            Assert.Equal("MenuKeyboard_SteerUp", Row(e, KeyboardControlAction.SteerUp).AutomationId);
            Assert.Equal("MenuKeyboard_OpensWith", e.OpenRow.AutomationId);
            Assert.Equal("MenuKeyboard_TypeKey_Deadzone", Row(e, KeyboardControlAction.TypeKey).ThresholdAutomationId);
            Assert.True(e.OpenRow.IsOpensWith);
            // A selector that loses its items pushes -1, which is no edit.
            e.KeyboardLayoutIndex = -1;
            Assert.Equal(KeyboardLayout.Grid, e.Entry.KeyboardLayout);
        }

        [Fact]
        public void TheSteeringAndPointerRows_AreNamed_SplitsByTheHandTheyMove()
        {
            var s = Strings.Instance;
            var e = Keyboard();
            Assert.Equal(s.Osk_Control_SteerUp, Row(e, KeyboardControlAction.SteerUp).Label);
            Assert.Equal(s.Osk_Control_SteerRight, Row(e, KeyboardControlAction.SteerRight).Label);
            Assert.Equal(string.Format(s.Osk_Control_PointerX_Format, 1), Row(e, KeyboardControlAction.Pointer1X).Label);
            Assert.Equal(string.Format(s.Osk_Control_PointerClick_Format, 2), Row(e, KeyboardControlAction.Pointer2Click).Label);
            e.KeyboardLayoutIndex = (int)KeyboardLayout.Split;
            Assert.Equal(string.Format(s.Osk_Control_LeftHand_Format, s.Osk_Control_SteerUp), Row(e, KeyboardControlAction.SteerUp).Label);
            Assert.Equal(string.Format(s.Osk_Control_RightHand_Format, s.Osk_Control_SteerLeft), Row(e, KeyboardControlAction.Steer2Left).Label);
            // Every row of every layout has a name of its own, and no steering
            // or pointer row falls back to its enum name.
            foreach (var layout in new[] { KeyboardLayout.Grid, KeyboardLayout.Split, KeyboardLayout.Wheel })
            {
                var names = KeyboardMenuDefaults.Actions(layout).Select(a => MenuEditorItem.ControlName(layout, a)).ToList();
                Assert.Equal(names.Count, names.Distinct().Count());
                Assert.All(KeyboardMenuDefaults.Actions(layout).Where(a => KeyboardMenuDefaults.IsSteer(a) || KeyboardMenuDefaults.IsPointer(a)),
                    a => Assert.NotEqual(a.ToString(), MenuEditorItem.ControlName(layout, a)));
            }
        }

        [Fact]
        public void ARecordedPress_SetsTheRow_AndAGestureOrAMenuCellOpensOnly()
        {
            var e = Keyboard();
            var type = Row(e, KeyboardControlAction.TypeKey);
            Assert.True(e.TryApplyKeyboardPress(type, Press("Button 3", G)));
            Assert.Equal(new KeyboardInput("Button 3", G), KeyboardMenuDefaults.Effective(e.Entry, KeyboardLayout.Grid, KeyboardControlAction.TypeKey));
            Assert.Equal(new KeyboardInput("Button 3", G), type.Input);

            Assert.True(e.TryApplyKeyboardPress(type, Press("Gamepad LeftShoulder", "", "Gamepad ButtonA")));
            Assert.Equal(new KeyboardInput("Gamepad LeftShoulder", "", "Gamepad ButtonA", ""), type.Input);

            // A gesture and a menu cell come from the state the keyboard holds
            // at rest, so no control can read them while it is open.
            foreach (var refused in new[] { "Touchpad 0 Tap", "Mouse Gesture Left", "Menu 2 Item 1" })
            {
                Assert.False(e.TryApplyKeyboardPress(type, Press(refused)), refused);
                Assert.False(e.TryApplyKeyboardPress(type, Press("Button 1", "", refused)), refused);
                Assert.False(e.TryApplyKeyboardPress(Row(e, KeyboardControlAction.SteerUp), Press(refused)), refused);
            }
            Assert.Equal(new KeyboardInput("Gamepad LeftShoulder", "", "Gamepad ButtonA", ""), type.Input);

            // Opens With takes every input, those two families too: the
            // keyboard is closed when it opens.
            Assert.True(e.TryApplyKeyboardPress(e.OpenRow, Press("Gamepad ButtonGuide")));
            Assert.Equal("Gamepad ButtonGuide", e.Entry.OpenDescriptor);
            Assert.Equal(new KeyboardInput("Gamepad ButtonGuide"), e.OpenRow.Input);
            Assert.True(e.TryApplyKeyboardPress(e.OpenRow, Press("Touchpad 0 Tap", G)));
            Assert.Equal(new KeyboardInput("Touchpad 0 Tap", G), e.OpenRow.Input);
            Assert.True(e.TryApplyKeyboardPress(e.OpenRow, Press("Gamepad ButtonBack", "", "Menu 2 Item 1")));
            Assert.Equal(new KeyboardInput("Gamepad ButtonBack", "", "Menu 2 Item 1", ""), e.OpenRow.Input);
        }

        [Fact]
        public void APointersXAndY_TakeAnInputWithAPlace_AndRecordAnAxis()
        {
            var e = Keyboard();
            var x = Row(e, KeyboardControlAction.Pointer1X);
            Assert.True(x.RecordsAnAxis);
            Assert.False(Row(e, KeyboardControlAction.Pointer1Click).RecordsAnAxis);
            Assert.False(e.OpenRow.RecordsAnAxis);

            // A flight stick's axis, a slider, a gamepad stick and a touchpad
            // finger have a place. A button, a gyro's speed and a combination
            // do not.
            Assert.True(e.TryApplyKeyboardAxis(x, "Axis 3", G));
            Assert.Equal(new KeyboardInput("Axis 3", G), x.Input);
            Assert.True(e.TryApplyKeyboardAxis(x, "Slider 1", G));
            Assert.True(e.TryApplyKeyboardAxis(x, "Gamepad RightStickX", ""));
            Assert.True(e.TryApplyKeyboardPress(x, Press("Touchpad 1 Finger 0 X")));
            Assert.Equal(new KeyboardInput("Touchpad 1 Finger 0 X"), x.Input);
            Assert.False(e.TryApplyKeyboardAxis(x, "Button 2", G));
            Assert.False(e.TryApplyKeyboardAxis(x, "Gyro X", G));
            Assert.False(e.TryApplyKeyboardPress(x, Press("Axis 0", G, "Button 2", G)));
            Assert.False(e.TryApplyKeyboardPress(x, Press("Button 2", G)));
            // An axis recording is for a pointer's place alone.
            Assert.False(e.TryApplyKeyboardAxis(Row(e, KeyboardControlAction.TypeKey), "Axis 3", G));
            Assert.Equal(new KeyboardInput("Touchpad 1 Finger 0 X"), x.Input);

            // While it records, its line says to move an axis.
            e.RecordingKeyboardRow = x;
            Assert.Equal(Strings.Instance.Menu_Keyboard_RecordingAxisPrompt, x.Subtitle);
            e.RecordingKeyboardRow = Row(e, KeyboardControlAction.TypeKey);
            Assert.Equal(Strings.Instance.Menu_Keyboard_RecordingPrompt, e.RecordingKeyboardRow.Subtitle);
        }

        [Fact]
        public void EachRowsList_OffersTheInputsItReads()
        {
            var e = Keyboard();
            e.InputChoicesProvider = () => new[]
            {
                new InputChoice { Descriptor = "Button 3", DisplayName = "Fire", DeviceGuid = G, DeviceLabel = "Stick" },
                new InputChoice { Descriptor = "Axis 2", DisplayName = "Throttle", DeviceGuid = G, DeviceLabel = "Stick" },
                new InputChoice { Descriptor = "Touchpad 0 Tap", DisplayName = "Tap", DeviceGuid = G, DeviceLabel = "Stick" },
                new InputChoice { Descriptor = "Menu 2 Item 1", DisplayName = "Cell 1", DeviceGuid = "", DeviceLabel = "Menus" },
            };
            List<string> Offered(KeyboardInputRow row)
                => row.ChoicesView.SourceCollection.Cast<KeyboardChoice>()
                    .Select(c => c.Input.Descriptor).Where(d => d != row.Input.Descriptor).ToList();
            // An axis is followed by its two halves: a press or a steering
            // direction reads one half of a stick.
            Assert.Equal(new[] { "Button 3", "Axis 2", "HAxis 2", "IHAxis 2" }, Offered(Row(e, KeyboardControlAction.TypeKey)));
            Assert.Equal(new[] { "Button 3", "Axis 2", "HAxis 2", "IHAxis 2" }, Offered(Row(e, KeyboardControlAction.SteerUp)));
            Assert.Equal(new[] { "Axis 2" }, Offered(Row(e, KeyboardControlAction.Pointer1Y)));
            Assert.Equal(new[] { "Button 3", "Axis 2", "HAxis 2", "IHAxis 2", "Touchpad 0 Tap", "Menu 2 Item 1" }, Offered(e.OpenRow));
            var halves = Row(e, KeyboardControlAction.TypeKey).ChoicesView.SourceCollection.Cast<KeyboardChoice>()
                .Where(c => c.Input.Descriptor.EndsWith("Axis 2")).Select(c => c.DisplayName).ToList();
            Assert.Equal(new[] { "Throttle", "Throttle +", "Throttle -" }, halves);

            // Picking an entry sets the row.
            var steer = Row(e, KeyboardControlAction.SteerUp);
            steer.SelectedKey = MenuEditorItem.KeyOf(new KeyboardInput("IHAxis 2", G));
            Assert.Equal(new KeyboardInput("IHAxis 2", G), steer.Input);

            // A stick has halves, a trigger and a button do not, and a half
            // has none of its own.
            Assert.True(MenuEditorItem.HasHalves("Gamepad LeftStickX"));
            Assert.True(MenuEditorItem.HasHalves("Axis 4"));
            Assert.False(MenuEditorItem.HasHalves("Gamepad RightTrigger"));
            Assert.False(MenuEditorItem.HasHalves("Gamepad LeftStick"));
            Assert.False(MenuEditorItem.HasHalves("Button 3"));
            Assert.False(MenuEditorItem.HasHalves("IHGamepad LeftStickY"));
            Assert.False(MenuEditorItem.HasHalves("Slider 0"));
        }

        [Fact]
        public void APressRow_RefusesAFingersPlace_WhichOnlyAPointerReads()
        {
            var e = Keyboard();
            e.InputChoicesProvider = () => new[]
            {
                new InputChoice { Descriptor = "Touchpad 0 Finger 0 X", DisplayName = "Finger X", DeviceGuid = "", DeviceLabel = "Any" },
                new InputChoice { Descriptor = "Touchpad 0 Finger 0 Y", DisplayName = "Finger Y", DeviceGuid = "", DeviceLabel = "Any" },
                new InputChoice { Descriptor = "Touchpad 0 Finger 0 Down", DisplayName = "Finger Down", DeviceGuid = "", DeviceLabel = "Any" },
            };
            List<string> Offered(KeyboardInputRow row)
                => row.ChoicesView.SourceCollection.Cast<KeyboardChoice>()
                    .Select(c => c.Input.Descriptor).Where(d => d != row.Input.Descriptor).ToList();
            // A button read of a finger's place reads nothing.
            foreach (var row in new[] { Row(e, KeyboardControlAction.TypeKey), Row(e, KeyboardControlAction.Pointer1Click), e.OpenRow })
            {
                Assert.Equal(new[] { "Touchpad 0 Finger 0 Down" }, Offered(row));
                Assert.False(e.TryApplyKeyboardPress(row, Press("Touchpad 0 Finger 0 X")));
            }
            // A pointer's X reads it as a place.
            Assert.Contains("Touchpad 0 Finger 0 Y", Offered(Row(e, KeyboardControlAction.Pointer1X)));
        }

        [Fact]
        public void ThePressPoint_ShowsWhereItActs_AndStaysWithTheRow()
        {
            var e = Keyboard();
            var type = Row(e, KeyboardControlAction.TypeKey);
            // A button presses at once, so the default A has no press point.
            Assert.False(type.ShowsThreshold);
            Assert.True(e.TryApplyKeyboardPress(type, Press("Gamepad RightTrigger")));
            Assert.True(type.ShowsThreshold);
            Assert.Equal(KeyboardInput.DefaultThreshold, type.ThresholdPercent);
            type.ThresholdPercent = 80;
            Assert.Equal(80, KeyboardMenuDefaults.Effective(e.Entry, KeyboardLayout.Grid, KeyboardControlAction.TypeKey).ThresholdPercent);
            type.ThresholdPercent = 140;
            Assert.Equal(99, type.ThresholdPercent);
            type.ThresholdPercent = 80;
            // A new input keeps the row's press point, as a Mappings row's
            // deadzone stays with its source.
            Assert.True(e.TryApplyKeyboardPress(type, Press("Axis 2", G)));
            Assert.Equal(new KeyboardInput("Axis 2", G, "", "", 80), type.Input);
            type.ResetThresholdCommand.Execute(null);
            Assert.Equal(new KeyboardInput("Axis 2", G), type.Input);
            // A combination shows it when either input has depth.
            Assert.True(e.TryApplyKeyboardPress(type, Press("Button 1", G, "Gamepad LeftTrigger")));
            Assert.True(type.ShowsThreshold);
            // A touchpad finger's place and a gesture press on their own.
            Assert.True(e.TryApplyKeyboardPress(type, Press("Touchpad 0 Click")));
            Assert.False(type.ShowsThreshold);

            // A steering row reads its input's depth: only a combination's
            // second input, which holds the steering as a press, has a press point.
            var steer = Row(e, KeyboardControlAction.SteerUp);
            Assert.False(steer.ShowsThreshold);
            Assert.True(e.TryApplyKeyboardPress(steer, Press("IHAxis 1", G, "Gamepad RightTrigger")));
            Assert.True(steer.ShowsThreshold);
            // A pointer's place has none.
            Assert.False(Row(e, KeyboardControlAction.Pointer1X).ShowsThreshold);
            // Opens With has one, saved on the menu.
            Assert.True(e.TryApplyKeyboardPress(e.OpenRow, Press("Gamepad LeftTrigger")));
            Assert.True(e.OpenRow.ShowsThreshold);
            e.OpenRow.ThresholdPercent = 25;
            Assert.Equal(25, e.Entry.OpenThreshold);
            Assert.Equal(string.Format(Strings.Instance.Menu_Keyboard_Threshold_Name_Format, e.OpenRow.Label), e.OpenRow.ThresholdName);
        }

        [Fact]
        public void ARowsReset_PutsItsDefaultBack_AndResetAllLeavesTheOtherLayouts()
        {
            var e = Keyboard();
            var type = Row(e, KeyboardControlAction.TypeKey);
            e.TryApplyKeyboardPress(type, Press("Button 3", G));
            type.ResetInputCommand.Execute(null);
            Assert.False(KeyboardMenuDefaults.IsCustomized(e.Entry, KeyboardLayout.Grid, KeyboardControlAction.TypeKey));
            Assert.Equal("Gamepad ButtonA", type.Input.Descriptor);

            e.TryApplyKeyboardPress(type, Press("Button 3", G));
            e.KeyboardLayoutIndex = (int)KeyboardLayout.Wheel;
            e.TryApplyKeyboardPress(Row(e, KeyboardControlAction.PickNorth), Press("Button 4", G));
            e.ResetKeyboardControlsCommand.Execute(null);
            Assert.False(KeyboardMenuDefaults.IsCustomized(e.Entry, KeyboardLayout.Wheel, KeyboardControlAction.PickNorth));
            Assert.True(KeyboardMenuDefaults.IsCustomized(e.Entry, KeyboardLayout.Grid, KeyboardControlAction.TypeKey));

            // Opens With's reset clears it: it has no default.
            e.TryApplyKeyboardPress(e.OpenRow, Press("Gamepad ButtonGuide"));
            e.OpenRow.ResetInputCommand.Execute(null);
            Assert.Equal("", e.Entry.OpenDescriptor);
        }

        [Fact]
        public void TheEngageDeadzonesReset_TakesTheStylesDefault()
        {
            var e = Keyboard();
            e.EngageDeadzonePercent = 70;
            e.ResetDeadzoneCommand.Execute(null);
            Assert.Equal(KeyboardMenuDefaults.SteerDeadzonePercent, e.Entry.EngageDeadzonePercent);
            var ring = new MenuEditorItem(new MenuDefinitionEntry { MenuId = 2, Kind = MenuKind.Radial, CellCount = 4 });
            ring.EngageDeadzonePercent = 70;
            ring.ResetDeadzoneCommand.Execute(null);
            Assert.Equal(25, ring.Entry.EngageDeadzonePercent);
        }

        [Fact]
        public void TypesThrough_ListsWindowsInput_TheKeyboardAndMouseSlots_AndTheKeyboards()
        {
            var e = Keyboard();
            e.KbmSlotsProvider = () => new[] { new KbmSlotChoice(4, "Keyboard + Mouse 1") };
            var options = e.TypesThroughOptions;
            Assert.Equal("", options[0].Value);
            Assert.Equal("slot:4", options[1].Value);
            Assert.Contains(options, o => o.Value == "corsair-k55-rgb-pro-xt");
            Assert.Equal("", e.TypesThrough);

            e.TypesThrough = "slot:4";
            Assert.Equal("slot:4", e.Entry.TypesThrough);
            e.ResetTypesThroughCommand.Execute(null);
            Assert.Equal("", e.Entry.TypesThrough);

            // A slot that is no longer Keyboard + Mouse keeps a marked entry,
            // so the selection never lies.
            e.Entry.TypesThrough = "slot:9";
            Assert.Equal("slot:9", e.TypesThroughOptions.Last().Value);
            Assert.Equal("slot:9", e.TypesThrough);
        }

        [Fact]
        public void OpensOn_ListsTheMacroTriggersPressModes_EachWithItsOwnTime()
        {
            var s = Strings.Instance;
            var e = Keyboard();
            Assert.Equal(
                new[] { s.Macro_OnPress, s.Macro_OnRelease, s.Macro_WhileHeld, s.Macro_HoldForMs, s.Macro_ShortPress,
                        s.Macro_DoublePress, s.Macro_TriplePress, s.Macro_SinglePress },
                e.OpenPressOptions.Select(o => o.Label));
            Assert.All(e.OpenPressOptions, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
            Assert.Equal(e.OpenPressOptions.Count, e.OpenPressOptions.Select(o => o.Value).Distinct().Count());

            Assert.Equal(0, e.OpenPressIndex);
            Assert.False(e.ShowsOpenPressMs);
            e.OpenPressIndex = (int)KeyboardOpenPress.Hold;
            Assert.Equal(KeyboardOpenPress.Hold, e.Entry.OpenPress);
            Assert.True(e.ShowsOpenPressMs);
            Assert.Equal(s.Macro_TriggerHoldMs_Label, e.OpenPressMsLabel);
            Assert.Equal(KeyboardMenuDefaults.DefaultHoldMs, e.OpenPressMs);
            e.OpenPressMs = 20;
            Assert.Equal(50, e.Entry.OpenPressMs);
            // Another mode starts at its own default.
            e.OpenPressIndex = (int)KeyboardOpenPress.DoublePress;
            Assert.Equal(0, e.Entry.OpenPressMs);
            Assert.Equal(KeyboardMenuDefaults.DefaultDoublePressMs, e.OpenPressMs);
            Assert.Equal(s.Macro_TriggerDoublePressMs_Label, e.OpenPressMsLabel);
            e.OpenPressIndex = (int)KeyboardOpenPress.ShortPress;
            Assert.Equal(s.Macro_TriggerHoldMs_Label, e.OpenPressMsLabel);
            e.OpenPressIndex = (int)KeyboardOpenPress.SinglePress;
            Assert.Equal(s.Macro_TriggerDoublePressMs_Label, e.OpenPressMsLabel);
            foreach (var none in new[] { KeyboardOpenPress.Release, KeyboardOpenPress.WhileHeld })
            {
                e.OpenPressIndex = (int)none;
                Assert.False(e.ShowsOpenPressMs);
            }
            e.ResetOpenPressCommand.Execute(null);
            Assert.Equal(KeyboardOpenPress.Press, e.Entry.OpenPress);
            Assert.False(e.ShowsOpenPressMs);
        }

        [Fact]
        public void ATypesThroughValueNoEntryCarries_SaysWhatIsMissing()
        {
            var e = Keyboard();
            e.KbmSlotsProvider = () => new[] { new KbmSlotChoice(4, "Keyboard + Mouse 1") };
            e.Entry.TypesThrough = "slot:2";
            var slot = e.TypesThroughOptions.Single(o => o.Value == "slot:2");
            Assert.Equal(Strings.Get("Menu_TypesThrough_MissingSlot"), slot.Label);
            Assert.DoesNotContain("slot:", slot.Label);
            e.Entry.TypesThrough = "nokeyboard-xyz";
            var model = e.TypesThroughOptions.Single(o => o.Value == "nokeyboard-xyz");
            Assert.Equal(string.Format(Strings.Get("Menu_TypesThrough_MissingKeyboard_Format"), "nokeyboard-xyz"), model.Label);
        }

        [Fact]
        public void TheSymbolsBox_ShowsTheDefaultSet_AndWritingItBackStoresNothing()
        {
            var e = Keyboard();
            Assert.Equal(PadForge.Common.Input.OnScreenKeyboard.OskLayoutFrame.DefaultSymbols, e.Symbols);
            e.Symbols = "@ #";
            Assert.Equal("@ #", e.Entry.Symbols);
            e.Symbols = PadForge.Common.Input.OnScreenKeyboard.OskLayoutFrame.DefaultSymbols;
            Assert.Equal("", e.Entry.Symbols);
        }

        [Fact]
        public void ARingsOpenerList_HasTheDpadAndTheFaceButtons_AndARecordedPressFoldsOntoThem()
        {
            var e = new MenuEditorItem(new MenuDefinitionEntry { MenuId = 1, Kind = MenuKind.Radial, CellCount = 4 });
            Assert.Contains(e.HostOptions, o => o.Descriptor == "Gamepad DPad");
            Assert.Contains(e.HostOptions, o => o.Descriptor == "Gamepad Diamond");
            Assert.True(e.TryApplyRecordedHost("POV 0 Up"));
            Assert.Equal("Gamepad DPad", e.Entry.HostDescriptor);
            Assert.True(e.TryApplyRecordedHost("Gamepad ButtonX"));
            Assert.Equal("Gamepad Diamond", e.Entry.HostDescriptor);
            Assert.True(e.TryApplyRecordedHost("Axis 1"));
            Assert.Equal("Gamepad LeftStick", e.Entry.HostDescriptor);
            // A key has no surface to steer with.
            Assert.False(e.TryApplyRecordedHost("Key 65"));
            // Any other axis opens with Custom Axes.
            Assert.True(e.TryApplyRecordedHost("Slider 0"));
            Assert.Equal("Custom", e.Entry.HostDescriptor);
            Assert.Equal("Slider 0", e.Entry.CustomXDescriptor);
        }
    }
}
