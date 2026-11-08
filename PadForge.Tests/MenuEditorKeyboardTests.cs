using System.Linq;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Menus tab's editor for a keyboard menu (#566): the Keyboard style,
    /// the rows each layout lists, a recorded press on a row, the resets,
    /// Types Through's list, Opens On's time, and the D-pad and face buttons
    /// in the steering list.
    /// </summary>
    public class MenuEditorKeyboardTests
    {
        private const string G = "11111111-2222-3333-4444-555555555555";

        private static MenuEditorItem Keyboard()
            => new(KeyboardMenuDefaults.Create(1, "Keyboard"));

        private static KeyboardInputRow Row(MenuEditorItem e, KeyboardControlAction a)
            => e.KeyboardControlRows.Single(r => r.Action == a);

        [Fact]
        public void TheStyleList_OffersTheKeyboard_AndTurningAMenuIntoOne_TakesItsDefaults()
        {
            var e = new MenuEditorItem(new MenuDefinitionEntry { MenuId = 1, Kind = MenuKind.Radial, CellCount = 4, HasCenter = true });
            Assert.Equal(new[] { 0, 1, 2 }, e.KindOptions.Select(o => o.Value));
            Assert.Empty(e.KeyboardControlRows);
            Assert.True(e.IsCellMenu);

            e.KindIndex = 2;
            Assert.Equal(MenuKind.Keyboard, e.Entry.Kind);
            Assert.True(e.IsKeyboard);
            Assert.False(e.IsCellMenu);
            Assert.False(e.ShowsClickRow);
            Assert.False(e.Entry.HasCenter);
            // The left stick steers and the keyboard sits at the bottom, as
            // the Windows and Steam keyboards do.
            Assert.Equal(KeyboardMenuDefaults.Host, e.Entry.HostDescriptor);
            Assert.Equal(KeyboardMenuDefaults.PosYPercent, e.Entry.PosYPercent);
            Assert.Equal(KeyboardMenuDefaults.Actions(KeyboardLayout.Grid), e.KeyboardControlRows.Select(r => r.Action.Value));

            // A choice the user made is kept.
            var touch = new MenuEditorItem(new MenuDefinitionEntry { MenuId = 2, Kind = MenuKind.Grid, HostDescriptor = "Touchpad 0", PosYPercent = 20 });
            touch.KindIndex = 2;
            Assert.Equal("Touchpad 0", touch.Entry.HostDescriptor);
            Assert.Equal(20, touch.Entry.PosYPercent);

            // And back: no keyboard rows on a cell menu.
            e.KindIndex = 0;
            Assert.Empty(e.KeyboardControlRows);
            Assert.True(e.IsCellMenu);
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
                Assert.Equal(layout == KeyboardLayout.Split, e.IsSplitKeyboard);
                Assert.All(e.KeyboardControlRows, r => Assert.Equal(layout, r.Layout));
            }
            // The rows' ids are the controls', for UI Automation.
            Assert.Equal("MenuKeyboard_TypeKey", Row(e, KeyboardControlAction.TypeKey).AutomationId);
            Assert.Equal("MenuKeyboard_OpensWith", e.OpenRow.AutomationId);
            Assert.True(e.OpenRow.IsOpensWith);
            // A selector that loses its items pushes -1, which is no edit.
            e.KeyboardLayoutIndex = -1;
            Assert.Equal(KeyboardLayout.Grid, e.Entry.KeyboardLayout);
        }

        [Fact]
        public void ARecordedPress_SetsTheRow_AndAGestureOrAMenuCellIsRefused()
        {
            var e = Keyboard();
            var type = Row(e, KeyboardControlAction.TypeKey);
            Assert.True(e.TryApplyKeyboardPress(type, new RecorderService.RecordedPress("Button 3", G, "", "")));
            Assert.Equal(new KeyboardInput("Button 3", G), KeyboardMenuDefaults.Effective(e.Entry, KeyboardLayout.Grid, KeyboardControlAction.TypeKey));
            Assert.Equal(new KeyboardInput("Button 3", G), type.Input);

            Assert.True(e.TryApplyKeyboardPress(type, new RecorderService.RecordedPress("Gamepad LeftShoulder", "", "Gamepad ButtonA", "")));
            Assert.Equal(new KeyboardInput("Gamepad LeftShoulder", "", "Gamepad ButtonA", ""), type.Input);

            foreach (var refused in new[] { "Touchpad 0 Tap", "Mouse Gesture Left", "Menu 2 Item 1" })
            {
                Assert.False(e.TryApplyKeyboardPress(type, new RecorderService.RecordedPress(refused, "", "", "")), refused);
                Assert.False(e.TryApplyKeyboardPress(type, new RecorderService.RecordedPress("Button 1", "", refused, "")), refused);
            }
            Assert.Equal(new KeyboardInput("Gamepad LeftShoulder", "", "Gamepad ButtonA", ""), type.Input);

            // Opens With records the same way.
            Assert.True(e.TryApplyKeyboardPress(e.OpenRow, new RecorderService.RecordedPress("Gamepad ButtonGuide", "", "", "")));
            Assert.Equal("Gamepad ButtonGuide", e.Entry.OpenDescriptor);
            Assert.Equal(new KeyboardInput("Gamepad ButtonGuide"), e.OpenRow.Input);
        }

        [Fact]
        public void ARowsReset_PutsItsDefaultBack_AndResetAllLeavesTheOtherLayouts()
        {
            var e = Keyboard();
            var type = Row(e, KeyboardControlAction.TypeKey);
            e.TryApplyKeyboardPress(type, new RecorderService.RecordedPress("Button 3", G, "", ""));
            type.ResetInputCommand.Execute(null);
            Assert.False(KeyboardMenuDefaults.IsCustomized(e.Entry, KeyboardLayout.Grid, KeyboardControlAction.TypeKey));
            Assert.Equal("Gamepad ButtonA", type.Input.Descriptor);

            e.TryApplyKeyboardPress(type, new RecorderService.RecordedPress("Button 3", G, "", ""));
            e.KeyboardLayoutIndex = (int)KeyboardLayout.Wheel;
            e.TryApplyKeyboardPress(Row(e, KeyboardControlAction.PickNorth), new RecorderService.RecordedPress("Button 4", G, "", ""));
            e.ResetKeyboardControlsCommand.Execute(null);
            Assert.False(KeyboardMenuDefaults.IsCustomized(e.Entry, KeyboardLayout.Wheel, KeyboardControlAction.PickNorth));
            Assert.True(KeyboardMenuDefaults.IsCustomized(e.Entry, KeyboardLayout.Grid, KeyboardControlAction.TypeKey));

            // Opens With's reset clears it: it has no default.
            e.TryApplyKeyboardPress(e.OpenRow, new RecorderService.RecordedPress("Gamepad ButtonGuide", "", "", ""));
            e.OpenRow.ResetInputCommand.Execute(null);
            Assert.Equal("", e.Entry.OpenDescriptor);
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
        public void OpensOn_ShowsItsTimeForHoldAndDoublePressOnly()
        {
            var e = Keyboard();
            Assert.Equal(0, e.OpenPressIndex);
            Assert.False(e.ShowsOpenPressMs);
            e.OpenPressIndex = 1;
            Assert.Equal(KeyboardOpenPress.Hold, e.Entry.OpenPress);
            Assert.True(e.ShowsOpenPressMs);
            Assert.Equal(KeyboardMenuDefaults.DefaultHoldMs, e.OpenPressMs);
            e.OpenPressMs = 20;
            Assert.Equal(50, e.Entry.OpenPressMs);
            // Another mode starts at its own default.
            e.OpenPressIndex = 2;
            Assert.Equal(0, e.Entry.OpenPressMs);
            Assert.Equal(KeyboardMenuDefaults.DefaultDoublePressMs, e.OpenPressMs);
            e.ResetOpenPressCommand.Execute(null);
            Assert.Equal(KeyboardOpenPress.Press, e.Entry.OpenPress);
            Assert.False(e.ShowsOpenPressMs);
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
        public void TheSteeringList_HasTheDpadAndTheFaceButtons_AndARecordedPressFoldsOntoThem()
        {
            var e = Keyboard();
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

            // Split's right half folds the same way into its own fields.
            e.KeyboardLayoutIndex = (int)KeyboardLayout.Split;
            Assert.Equal(KeyboardMenuDefaults.Host2, e.SelectedHost2.Descriptor);
            Assert.False(e.IsCustomHost2);
            Assert.True(e.TryApplyRecordedHost2("Gamepad DPadLeft"));
            Assert.Equal("Gamepad DPad", e.Entry.Host2Descriptor);
            Assert.True(e.TryApplyRecordedHost2("Slider 0"));
            Assert.Equal("Custom", e.Entry.Host2Descriptor);
            Assert.Equal("Slider 0", e.Entry.Custom2XDescriptor);
            Assert.True(e.IsCustomHost2);
            Assert.Equal("Gamepad LeftStick", e.Entry.HostDescriptor);
        }
    }
}
