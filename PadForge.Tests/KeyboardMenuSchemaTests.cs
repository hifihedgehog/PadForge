using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A keyboard menu in the settings file (#566): the enums keep their
    /// saved numbers, every keyboard setting survives a save and a load, a
    /// menu of another style writes none of them, a copy shares no list with
    /// the menu it came from, and a control keeps only what differs from its
    /// layout's default.
    /// </summary>
    public class KeyboardMenuSchemaTests
    {
        private const string G = "11111111-2222-3333-4444-555555555555";
        private const string G2 = "66666666-7777-8888-9999-000000000000";

        [Fact]
        public void TheEnums_KeepTheirSavedNumbers()
        {
            Assert.Equal(2, (int)MenuKind.Keyboard);
            Assert.Equal(MenuKind.Keyboard, Enum.GetValues<MenuKind>()[^1]);
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<KeyboardLayout>().Select(v => (int)v));
            Assert.Equal(KeyboardLayout.Wheel, Enum.GetValues<KeyboardLayout>()[^1]);
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<KeyboardOpenPress>().Select(v => (int)v));
            Assert.Equal(KeyboardOpenPress.DoublePress, Enum.GetValues<KeyboardOpenPress>()[^1]);
            var actions = Enum.GetValues<KeyboardControlAction>();
            Assert.Equal(Enumerable.Range(0, 32), actions.Select(v => (int)v));
            Assert.Equal(KeyboardControlAction.End, actions[^1]);
            Assert.Equal(4, (int)KeyboardControlAction.Close);
            Assert.Equal(17, (int)KeyboardControlAction.MoveKeyboard);
            Assert.Equal(27, (int)KeyboardControlAction.PickSouth);
        }

        private static MenuDefinitionEntry EveryField()
        {
            var def = KeyboardMenuDefaults.Create(4, "Typing");
            def.KeyboardLayout = KeyboardLayout.Split;
            KeyboardMenuDefaults.SetOpenInput(def, new KeyboardInput("Gamepad ButtonBack", "", "Button 3", G));
            def.OpenPress = KeyboardOpenPress.DoublePress;
            def.OpenPressMs = 300;
            def.OpenOnTextFocus = true;
            def.Host2Descriptor = "Custom";
            def.Custom2XDescriptor = "Axis 3";
            def.Custom2YDescriptor = "IAxis 4";
            def.TypesThrough = "slot:6";
            def.FunctionRow = true;
            def.Suggestions = false;
            def.Symbols = "@ # $";
            def.LayerMask = "L2";
            KeyboardMenuDefaults.Set(def, KeyboardLayout.Split, KeyboardControlAction.Space,
                new KeyboardInput("Button 9", G, "Gamepad LeftShoulder", ""));
            KeyboardMenuDefaults.Set(def, KeyboardLayout.Grid, KeyboardControlAction.Close, KeyboardInput.None);
            KeyboardMenuDefaults.Set(def, KeyboardLayout.Wheel, KeyboardControlAction.PickNorth,
                new KeyboardInput("Button 1", G, "Button 2", G2));
            return def;
        }

        private static void AssertEveryField(MenuDefinitionEntry back)
        {
            Assert.Equal(MenuKind.Keyboard, back.Kind);
            Assert.Equal(4, back.MenuId);
            Assert.Equal("Typing", back.Name);
            Assert.Equal(KeyboardLayout.Split, back.KeyboardLayout);
            Assert.Equal(new KeyboardInput("Gamepad ButtonBack", "", "Button 3", G), KeyboardMenuDefaults.OpenInput(back));
            Assert.Equal(KeyboardOpenPress.DoublePress, back.OpenPress);
            Assert.Equal(300, back.OpenPressMs);
            Assert.True(back.OpenOnTextFocus);
            Assert.Equal(KeyboardMenuDefaults.Host, back.HostDescriptor);
            Assert.Equal("Custom", back.Host2Descriptor);
            Assert.Equal("Axis 3", back.Custom2XDescriptor);
            Assert.Equal("IAxis 4", back.Custom2YDescriptor);
            Assert.Equal("slot:6", back.TypesThrough);
            Assert.True(back.FunctionRow);
            Assert.False(back.Suggestions);
            Assert.Equal("@ # $", back.Symbols);
            Assert.Equal("L2", back.LayerMask);
            Assert.Equal(KeyboardMenuDefaults.PosYPercent, back.PosYPercent);
            Assert.Equal(new KeyboardInput("Button 9", G, "Gamepad LeftShoulder", ""),
                KeyboardMenuDefaults.Effective(back, KeyboardLayout.Split, KeyboardControlAction.Space));
            Assert.True(KeyboardMenuDefaults.Effective(back, KeyboardLayout.Grid, KeyboardControlAction.Close).IsEmpty);
            Assert.True(KeyboardMenuDefaults.IsCustomized(back, KeyboardLayout.Grid, KeyboardControlAction.Close));
            Assert.Equal(new KeyboardInput("Button 1", G, "Button 2", G2),
                KeyboardMenuDefaults.Effective(back, KeyboardLayout.Wheel, KeyboardControlAction.PickNorth));
            // A control left alone reads its layout's default.
            Assert.Equal("Gamepad ButtonA", KeyboardMenuDefaults.Effective(back, KeyboardLayout.Grid, KeyboardControlAction.TypeKey).Descriptor);
        }

        private static string Xml(MappingSet set)
        {
            var w = new StringWriter();
            new XmlSerializer(typeof(MappingSet)).Serialize(w, set);
            return w.ToString();
        }

        private static MappingSet Read(string xml)
            => (MappingSet)new XmlSerializer(typeof(MappingSet)).Deserialize(new StringReader(xml));

        [Fact]
        public void EveryKeyboardSetting_SurvivesTheSettingsFile()
        {
            var set = new MappingSet();
            set.Menus.Add(EveryField());
            AssertEveryField(Assert.Single(Read(Xml(set)).Menus));
        }

        [Fact]
        public void AMenuOfAnotherStyle_WritesNoKeyboardSetting()
        {
            var set = new MappingSet();
            var radial = new MenuDefinitionEntry { MenuId = 1, Kind = MenuKind.Radial, CellCount = 4 };
            // Even with keyboard fields set, as a menu that was a keyboard keeps them.
            radial.OpenDescriptor = "Gamepad ButtonX";
            radial.OpenPress = KeyboardOpenPress.Hold;
            radial.OpenPressMs = 700;
            radial.TypesThrough = "slot:1";
            radial.Symbols = "x";
            KeyboardMenuDefaults.Set(radial, KeyboardLayout.Grid, KeyboardControlAction.TypeKey, new KeyboardInput("Button 5"));
            set.Menus.Add(radial);
            string xml = Xml(set);
            foreach (var name in new[]
            {
                "KeyboardLayout=", "OpenDescriptor=", "OpenDeviceGuid=", "OpenAndDescriptor=", "OpenPress=", "OpenPressMs=",
                "OpenOnTextFocus=", "Host2Descriptor=", "Custom2XDescriptor=", "Custom2YDescriptor=", "TypesThrough=",
                "FunctionRow=", "Suggestions=", "Symbols=", "<KeyboardControl",
            })
                Assert.DoesNotContain(name, xml);
        }

        [Fact]
        public void AnUntouchedKeyboardMenu_SavesNoControlAndNoPressMode()
        {
            var set = new MappingSet();
            set.Menus.Add(KeyboardMenuDefaults.Create(1, "Keyboard"));
            string xml = Xml(set);
            Assert.DoesNotContain("<KeyboardControl", xml);
            Assert.DoesNotContain("OpenPress=", xml);
            Assert.DoesNotContain("OpenPressMs=", xml);
            Assert.DoesNotContain("TypesThrough=", xml);
            var back = Assert.Single(Read(xml).Menus);
            Assert.Equal(MenuKind.Keyboard, back.Kind);
            Assert.True(back.Suggestions);
            Assert.Empty(back.KeyboardControls);
        }

        [Fact]
        public void ACopy_CarriesEveryKeyboardSetting_AndSharesNoList()
        {
            var def = EveryField();
            var copy = def.Clone();
            AssertEveryField(copy);
            Assert.NotSame(def.KeyboardControls, copy.KeyboardControls);
            for (int i = 0; i < def.KeyboardControls.Count; i++)
                Assert.NotSame(def.KeyboardControls[i], copy.KeyboardControls[i]);
            copy.KeyboardControls[0].Descriptor = "Button 15";
            AssertEveryField(def);
        }

        [Fact]
        public void AControlSetToItsDefault_DropsItsEntry_AndAClearedOneKeepsIt()
        {
            var def = KeyboardMenuDefaults.Create(1, "Keyboard");
            var before = def.KeyboardControls;
            KeyboardMenuDefaults.Set(def, KeyboardLayout.Grid, KeyboardControlAction.Backspace, new KeyboardInput("Gamepad ButtonY"));
            // Each edit swaps in a new list, so the poll thread never reads one half-edited.
            Assert.NotSame(before, def.KeyboardControls);
            Assert.Single(def.KeyboardControls);
            KeyboardMenuDefaults.Set(def, KeyboardLayout.Grid, KeyboardControlAction.Backspace,
                KeyboardMenuDefaults.DefaultInput(KeyboardLayout.Grid, KeyboardControlAction.Backspace));
            Assert.Empty(def.KeyboardControls);
            KeyboardMenuDefaults.Set(def, KeyboardLayout.Grid, KeyboardControlAction.Backspace, KeyboardInput.None);
            Assert.True(KeyboardMenuDefaults.IsCustomized(def, KeyboardLayout.Grid, KeyboardControlAction.Backspace));
            Assert.True(KeyboardMenuDefaults.Effective(def, KeyboardLayout.Grid, KeyboardControlAction.Backspace).IsEmpty);
            // Each layout keeps its own: the wheel's Backspace is untouched.
            Assert.False(KeyboardMenuDefaults.IsCustomized(def, KeyboardLayout.Wheel, KeyboardControlAction.Backspace));
            Assert.Equal("Gamepad LeftShoulder", KeyboardMenuDefaults.Effective(def, KeyboardLayout.Wheel, KeyboardControlAction.Backspace).Descriptor);
            // A layout's unused control has no input.
            Assert.True(KeyboardMenuDefaults.Effective(def, KeyboardLayout.Grid, KeyboardControlAction.PickWest).IsEmpty);
        }

        [Fact]
        public void EachLayout_GivesEveryControlItUsesADefault()
        {
            foreach (var layout in Enum.GetValues<KeyboardLayout>())
            {
                var actions = KeyboardMenuDefaults.Actions(layout);
                Assert.Equal(actions.Count, actions.Distinct().Count());
                foreach (var a in actions)
                    Assert.False(string.IsNullOrEmpty(KeyboardMenuDefaults.DefaultFor(layout, a)), $"{layout} {a}");
            }
        }

        [Fact]
        public void OpensWith_SetsAndClearsAsOne()
        {
            var def = KeyboardMenuDefaults.Create(1, "Keyboard");
            KeyboardMenuDefaults.SetOpenInput(def, new KeyboardInput(" Button 1 ", G, "Button 2", G2));
            Assert.Equal(new KeyboardInput("Button 1", G, "Button 2", G2), KeyboardMenuDefaults.OpenInput(def));
            KeyboardMenuDefaults.SetOpenInput(def, KeyboardInput.None);
            Assert.Equal("", def.OpenDescriptor);
            Assert.Equal("", def.OpenDeviceGuid);
            Assert.Equal("", def.OpenAndDescriptor);
            Assert.Equal("", def.OpenAndDeviceGuid);
        }

        [Fact]
        public void OpensWithsTime_TakesTheMacroTriggersDefaults()
        {
            var def = KeyboardMenuDefaults.Create(1, "Keyboard");
            Assert.Equal(0, KeyboardMenuDefaults.OpenPressMs(def));
            def.OpenPress = KeyboardOpenPress.Hold;
            Assert.Equal(new MacroData().TriggerHoldMs, KeyboardMenuDefaults.OpenPressMs(def));
            def.OpenPress = KeyboardOpenPress.DoublePress;
            Assert.Equal(new MacroData().TriggerDoublePressMs, KeyboardMenuDefaults.OpenPressMs(def));
            def.OpenPressMs = 99_999;
            Assert.Equal(KeyboardMenuDefaults.MaxOpenPressMs, KeyboardMenuDefaults.OpenPressMs(def));
            def.OpenPress = KeyboardOpenPress.Press;
            Assert.Equal(0, KeyboardMenuDefaults.OpenPressMs(def));
        }
    }
}
