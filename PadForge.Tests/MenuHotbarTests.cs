using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Hotbar style, step for step as steamclient64.dll's CHotbarMode
    /// runs (update 0x1388c1df0): the selection starts on the middle cell,
    /// Show or Hide toggles the bar and hiding it that way drops the
    /// selection, Previous and Next step over unbound cells and stop at the
    /// ends unless Wrap is on, Fire fires by the fire type and holds a cell
    /// at least 1/30 s, Hide After Firing hides on Fire, and Re-Center Each
    /// Time drops the selection while the bar is hidden.
    /// </summary>
    public class MenuHotbarTests
    {
        private static MenuDefinitionEntry Bar(int cells, params int[] bound)
        {
            var def = new MenuDefinitionEntry
            {
                MenuId = 3,
                Kind = MenuKind.Hotbar,
                HostDescriptor = "Gamepad DPad",
                CellCount = cells,
            };
            foreach (int i in bound) def.Items.Add(new MenuItemDefinition { Index = i, Label = "c" + i });
            return def;
        }

        /// <summary>One poll with the named controls held.</summary>
        private static void Poll(MenuRuntimeState st, MenuDefinitionEntry def, long t,
            bool show = false, bool prev = false, bool next = false, bool fire = false, bool layer = true)
            => MenuHotbar.Step(st, def, layer, show, prev, next, fire, t);

        /// <summary>A press and a release of one control, from time t.</summary>
        private static long Tap(MenuRuntimeState st, MenuDefinitionEntry def, long t,
            bool show = false, bool prev = false, bool next = false, bool fire = false)
        {
            Poll(st, def, t, show, prev, next, fire);
            Poll(st, def, t + 1);
            return t + 2;
        }

        [Fact]
        public void TheNumber_IsSaved_AfterTheKeyboards()
        {
            Assert.Equal(3, (int)MenuKind.Hotbar);
            Assert.Equal(MenuKind.Hotbar, Enum.GetValues<MenuKind>()[^1]);
        }

        [Fact]
        public void ShowOrHide_TogglesTheBar_OnTheMiddleCell()
        {
            var def = Bar(5, 0, 1, 2, 3, 4);
            var st = new MenuRuntimeState();
            Poll(st, def, 0);
            Assert.False(st.HotbarShown);
            Assert.Equal(-1, st.HoveredIndex);
            long t = Tap(st, def, 1, show: true);
            Assert.True(st.HotbarShown);
            Assert.Equal(2, st.HoveredIndex);
            Tap(st, def, t, show: true);
            Assert.False(st.HotbarShown);
            Assert.Equal(-1, st.HoveredIndex);
            Assert.Equal(-1, st.AssertedIndex);
        }

        [Fact]
        public void PreviousAndNext_StepOnlyWhileShown_OverUnboundCells_AndStopAtTheEnds()
        {
            var def = Bar(6, 0, 2, 3, 5);
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, next: true);
            Assert.False(st.HotbarShown);
            t = Tap(st, def, t, show: true);
            Assert.Equal(3, st.HoveredIndex);
            t = Tap(st, def, t, next: true);
            Assert.Equal(5, st.HoveredIndex);
            t = Tap(st, def, t, next: true);
            Assert.Equal(5, st.HoveredIndex);
            t = Tap(st, def, t, prev: true);
            Assert.Equal(3, st.HoveredIndex);
            t = Tap(st, def, t, prev: true);
            Assert.Equal(2, st.HoveredIndex);
            t = Tap(st, def, t, prev: true);
            Assert.Equal(0, st.HoveredIndex);
            Tap(st, def, t, prev: true);
            Assert.Equal(0, st.HoveredIndex);
            // No cell fires from stepping.
            Assert.Equal(-1, st.AssertedIndex);
        }

        [Fact]
        public void Wrap_GoesOnFromTheOtherEnd()
        {
            var def = Bar(4, 0, 1, 3);
            def.HotbarWrap = true;
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, show: true);
            // The middle cell, unbound here: the client starts there all the same.
            Assert.Equal(2, st.HoveredIndex);
            t = Tap(st, def, t, next: true);
            Assert.Equal(3, st.HoveredIndex);
            t = Tap(st, def, t, next: true);
            Assert.Equal(0, st.HoveredIndex);
            Tap(st, def, t, prev: true);
            Assert.Equal(3, st.HoveredIndex);
        }

        [Fact]
        public void PreviousAndNextTogether_PreviousDecides()
        {
            var def = Bar(5, 0, 1, 2, 3, 4);
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, show: true);
            Tap(st, def, t, prev: true, next: true);
            Assert.Equal(1, st.HoveredIndex);
        }

        [Fact]
        public void Click_FireHidesTheBar_AndTheCellIsAShortTap()
        {
            var def = Bar(3, 0, 1, 2);
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, show: true);
            Poll(st, def, t, fire: true);
            Assert.Equal(1, st.AssertedIndex);
            Assert.False(st.HotbarShown);
            // Fire still held, but the bar is gone: the cell lets go once
            // 1/30 s has passed.
            Poll(st, def, t + 20, fire: true);
            Assert.Equal(1, st.AssertedIndex);
            Poll(st, def, t + 34, fire: true);
            Assert.Equal(-1, st.AssertedIndex);
        }

        [Fact]
        public void Click_WithoutHideAfterFiring_HoldsTheCellWhileFireIsHeld()
        {
            var def = Bar(3, 0, 1, 2);
            def.HotbarDismissOnFire = false;
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, show: true);
            Poll(st, def, t, fire: true);
            Poll(st, def, t + 500, fire: true);
            Assert.Equal(1, st.AssertedIndex);
            Assert.True(st.HotbarShown);
            Poll(st, def, t + 501);
            Assert.Equal(-1, st.AssertedIndex);
            // The same cell fires again.
            Poll(st, def, t + 600, fire: true);
            Assert.Equal(1, st.AssertedIndex);
        }

        [Fact]
        public void ClickRelease_FiresOnTheRelease_OrAtOnceWhenFireHidesTheBar()
        {
            var def = Bar(3, 0, 1, 2);
            def.FireType = MenuFireType.ClickRelease;
            def.HotbarDismissOnFire = false;
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, show: true);
            Poll(st, def, t, fire: true);
            Assert.Equal(-1, st.AssertedIndex);
            Poll(st, def, t + 100);
            Assert.Equal(1, st.AssertedIndex);
            Poll(st, def, t + 140);
            Assert.Equal(-1, st.AssertedIndex);

            def.HotbarDismissOnFire = true;
            st = new MenuRuntimeState();
            t = Tap(st, def, 0, show: true);
            Poll(st, def, t, fire: true);
            Assert.Equal(-1, st.AssertedIndex);
            Poll(st, def, t + 1, fire: true);
            // The bar hid, so Fire no longer counts as held, which the client
            // reads as its release: the middle cell fires, Re-Center Each Time
            // having dropped the selection as the bar hid.
            Assert.Equal(1, st.AssertedIndex);
        }

        [Fact]
        public void TouchRelease_FiresWhenFireHidesTheBar_TheMiddleCellWhileReCentering()
        {
            var def = Bar(5, 0, 1, 2, 3, 4);
            def.FireType = MenuFireType.TouchRelease;
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, show: true);
            t = Tap(st, def, t, next: true);
            Assert.Equal(3, st.HoveredIndex);
            Poll(st, def, t, fire: true);
            Poll(st, def, t + 1, fire: true);
            // Re-Center Each Time dropped the selection as the bar hid, so the
            // client fires its middle cell, as its update orders it.
            Assert.Equal(2, st.AssertedIndex);

            def.HotbarRecenter = false;
            st = new MenuRuntimeState();
            t = Tap(st, def, 0, show: true);
            t = Tap(st, def, t, next: true);
            Poll(st, def, t, fire: true);
            Poll(st, def, t + 1, fire: true);
            Assert.Equal(3, st.AssertedIndex);
        }

        [Fact]
        public void TouchRelease_HidingWithShowOrHide_FiresNothing()
        {
            var def = Bar(3, 0, 1, 2);
            def.FireType = MenuFireType.TouchRelease;
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, show: true);
            t = Tap(st, def, t, show: true);
            Poll(st, def, t);
            Assert.Equal(-1, st.AssertedIndex);
        }

        [Fact]
        public void Always_HoldsTheSelectedCell_AndSwapsWithAGapWhenStepping()
        {
            var def = Bar(3, 0, 1, 2);
            def.FireType = MenuFireType.Always;
            var st = new MenuRuntimeState();
            Poll(st, def, 0);
            Assert.Equal(-1, st.AssertedIndex);
            long t = Tap(st, def, 1, show: true);
            Assert.Equal(1, st.AssertedIndex);
            Poll(st, def, t + 100, next: true);
            Assert.Equal(-1, st.AssertedIndex);
            Poll(st, def, t + 120);
            Assert.Equal(-1, st.AssertedIndex);
            Poll(st, def, t + 140);
            Assert.Equal(2, st.AssertedIndex);
        }

        [Fact]
        public void HidingWithShowOrHide_ReCentersEvenWithReCenterOff_HidingWithFireDoesNot()
        {
            var def = Bar(5, 0, 1, 2, 3, 4);
            def.HotbarRecenter = false;
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, show: true);
            t = Tap(st, def, t, next: true);
            t = Tap(st, def, t, show: true);
            t = Tap(st, def, t, show: true);
            Assert.Equal(2, st.HoveredIndex);

            t = Tap(st, def, t, next: true);
            Assert.Equal(3, st.HoveredIndex);
            t = Tap(st, def, t, fire: true);
            Assert.False(st.HotbarShown);
            Poll(st, def, t + 100);
            Tap(st, def, t + 200, show: true);
            Assert.Equal(3, st.HoveredIndex);
        }

        [Fact]
        public void TheLayerEnding_LetsGo_AndStartsOverHidden()
        {
            var def = Bar(3, 0, 1, 2);
            def.HotbarDismissOnFire = false;
            var st = new MenuRuntimeState();
            long t = Tap(st, def, 0, show: true);
            Poll(st, def, t, fire: true);
            Assert.Equal(1, st.AssertedIndex);
            Poll(st, def, t + 1, fire: true, layer: false);
            Assert.Equal(-1, st.AssertedIndex);
            Assert.False(st.HotbarShown);
            Assert.Equal(-1, st.HoveredIndex);
        }

        [Fact]
        public void TheHostsDirections_AreItsDefaultControls()
        {
            var def = Bar(3, 0);
            def.HostDescriptor = "Gamepad Diamond";
            Assert.Equal("Gamepad ButtonA", MenuHotbar.ControlDescriptor(def, HotbarControl.Show));
            Assert.Equal("Gamepad ButtonX", MenuHotbar.ControlDescriptor(def, HotbarControl.Previous));
            Assert.Equal("Gamepad ButtonB", MenuHotbar.ControlDescriptor(def, HotbarControl.Next));
            Assert.Equal("Gamepad ButtonY", MenuHotbar.ControlDescriptor(def, HotbarControl.Fire));

            def.HostDescriptor = "Gamepad RightStick";
            def.EngageDeadzonePercent = 61;
            Assert.Equal(BoundedReads.Wedge(90, 45, 1.8, 62.25, 59.75, BoundedGate.None,
                SourceCoercion.RightStickRingDescriptor), MenuHotbar.ControlDescriptor(def, HotbarControl.Fire));

            def.HostDescriptor = "Touchpad 0";
            def.HostHalf = 2;
            Assert.Equal(BoundedReads.Wedge(270, 45, 1.8, 62.25, 59.75, BoundedGate.Click,
                "Touchpad 0 Finger 0 Ring Right"), MenuHotbar.ControlDescriptor(def, HotbarControl.Show));

            def.HostDescriptor = "Custom";
            Assert.Equal("", MenuHotbar.ControlDescriptor(def, HotbarControl.Fire));
            def.HotbarFireDescriptor = "Button 7";
            Assert.Equal("Button 7", MenuHotbar.ControlDescriptor(def, HotbarControl.Fire));
        }

        private static string Xml(MappingSet set)
        {
            var w = new StringWriter();
            new XmlSerializer(typeof(MappingSet)).Serialize(w, set);
            return w.ToString();
        }

        [Fact]
        public void EveryHotbarSetting_SurvivesTheSettingsFile_AndACopy()
        {
            var def = Bar(4, 0, 2);
            def.HotbarShowDescriptor = "Button 1";
            def.HotbarPreviousDescriptor = "Button 2";
            def.HotbarNextDescriptor = "Button 3";
            def.HotbarFireDescriptor = "Button 4";
            def.HotbarWrap = true;
            def.HotbarDismissOnFire = false;
            def.HotbarRecenter = false;
            var set = new MappingSet();
            set.Menus.Add(def);
            var back = Assert.Single(((MappingSet)new XmlSerializer(typeof(MappingSet))
                .Deserialize(new StringReader(Xml(set)))).Menus);
            foreach (var m in new[] { back, def.Clone() })
            {
                Assert.Equal(MenuKind.Hotbar, m.Kind);
                Assert.Equal("Button 1", m.HotbarShowDescriptor);
                Assert.Equal("Button 2", m.HotbarPreviousDescriptor);
                Assert.Equal("Button 3", m.HotbarNextDescriptor);
                Assert.Equal("Button 4", m.HotbarFireDescriptor);
                Assert.True(m.HotbarWrap);
                Assert.False(m.HotbarDismissOnFire);
                Assert.False(m.HotbarRecenter);
            }
        }

        [Fact]
        public void AMenuOfAnotherStyle_WritesNoHotbarSetting()
        {
            var radial = new MenuDefinitionEntry { MenuId = 1, Kind = MenuKind.Radial, CellCount = 4 };
            radial.HotbarFireDescriptor = "Button 4";
            radial.HotbarWrap = true;
            radial.HotbarDismissOnFire = false;
            var set = new MappingSet();
            set.Menus.Add(radial);
            Assert.DoesNotContain("Hotbar", Xml(set));
        }
    }
}
