using System.Linq;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Menus;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Steam's hotbar mode, as the client runs it (steamclient64.dll
    /// CHotbarMode, built on its D-pad mode): Down shows or hides the bar,
    /// Left and Right step it, Up fires the selected cell. The hotbar's own
    /// settings: scroll_wrap (default off), test_param_0 for Dismiss After
    /// Activation and test_param_1 for Re-Center Each Time (both on),
    /// touchmenu_button_fire_type (default 0), the bar at x 10 on the left
    /// trackpad, left stick and D-pad and at x 90 elsewhere, y 80, and the
    /// D-pad read at layout 0 with a dead zone of 20000.</summary>
    public class SteamHotbarImportTests
    {
        private static TranslatedProfile Translate(string vdf)
        {
            var config = Model.SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = 47 });
        }

        private static string Config(string source, string controllerType = "controller_ps4",
            params (string Key, string Value)[] settings)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Hotbar\"\n");
            sb.Append($"\t\"controller_type\"\t\"{controllerType}\"\n");
            sb.Append("\t\"group\"\n\t{\n\t\t\"id\"\t\"1\"\n\t\t\"mode\"\t\"hotbar\"\n\t\t\"inputs\"\n\t\t{\n");
            foreach (var (index, key) in new[] { (0, "F1"), (1, "F2"), (3, "F4") })
            {
                sb.Append($"\t\t\t\"touch_menu_button_{index}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n");
                sb.Append("\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n");
                sb.Append($"\t\t\t\t\t\t\t\"binding\"\t\"key_press {key}\"\n");
                sb.Append("\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n");
            }
            sb.Append("\t\t}\n");
            if (settings.Length > 0)
            {
                sb.Append("\t\t\"settings\"\n\t\t{\n");
                foreach (var (k, v) in settings) sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
                sb.Append("\t\t}\n");
            }
            sb.Append("\t}\n");
            sb.Append("\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Default\"\n");
            sb.Append($"\t\t\"group_source_bindings\"\n\t\t{{\n\t\t\t\"1\"\t\"{source} active\"\n\t\t}}\n\t}}\n}}\n");
            return sb.ToString();
        }

        [Fact]
        public void OnTheDpad_IsAHotbar_RunByTheDpadItself()
        {
            var p = Translate(Config("dpad"));
            var m = Assert.Single(p.Menus);
            Assert.Equal(MenuKind.Hotbar, m.Kind);
            Assert.Equal("Gamepad DPad", m.HostDescriptor);
            Assert.Equal("Gamepad DPadDown", MenuHotbar.ControlDescriptor(m, HotbarControl.Show));
            Assert.Equal("Gamepad DPadLeft", MenuHotbar.ControlDescriptor(m, HotbarControl.Previous));
            Assert.Equal("Gamepad DPadRight", MenuHotbar.ControlDescriptor(m, HotbarControl.Next));
            Assert.Equal("Gamepad DPadUp", MenuHotbar.ControlDescriptor(m, HotbarControl.Fire));
            Assert.Equal(MenuFireType.Click, m.FireType);
            Assert.False(m.HotbarWrap);
            Assert.True(m.HotbarDismissOnFire);
            Assert.True(m.HotbarRecenter);
            Assert.Equal(new[] { 0, 1, 3 }, m.Items.Select(i => i.Index).ToArray());
        }

        [Fact]
        public void OnTheFaceButtons_ReadsThemByPosition()
        {
            var m = Assert.Single(Translate(Config("button_diamond")).Menus);
            Assert.Equal(MenuKind.Hotbar, m.Kind);
            Assert.Equal("Gamepad Diamond", m.HostDescriptor);
            Assert.Equal("Gamepad ButtonA", MenuHotbar.ControlDescriptor(m, HotbarControl.Show));
            Assert.Equal("Gamepad ButtonY", MenuHotbar.ControlDescriptor(m, HotbarControl.Fire));
        }

        [Fact]
        public void OnAStick_ReadsSteamsFourDirections_PastTheHotbarsDeadZone()
        {
            var m = Assert.Single(Translate(Config("joystick")).Menus);
            Assert.Equal(MenuKind.Hotbar, m.Kind);
            // Layout 0: four 90-degree spans with a margin of a fiftieth of a
            // span, engaged past 20000 plus 1.25 percent of 32767.
            var (press, release) = ConfigTranslator.SteamTriggerEdges(20000, 1.25);
            string Wedge(double center) => BoundedReads.Wedge(center, 45, 1.8, press, release,
                BoundedGate.None, SourceCoercion.LeftStickRingDescriptor);
            Assert.Equal(Wedge(270), m.HotbarShowDescriptor);
            Assert.Equal(Wedge(180), m.HotbarPreviousDescriptor);
            Assert.Equal(Wedge(0), m.HotbarNextDescriptor);
            Assert.Equal(Wedge(90), m.HotbarFireDescriptor);
        }

        [Fact]
        public void OnATrackpad_ReadsTheDirectionsOnlyWhileClicked()
        {
            var m = Assert.Single(Translate(Config("right_trackpad", "controller_steamcontroller_gordon")).Menus);
            Assert.Equal(MenuKind.Hotbar, m.Kind);
            Assert.StartsWith("Wedge 90 45 1.8 ", m.HotbarFireDescriptor);
            Assert.Contains(" click ", m.HotbarFireDescriptor);
            Assert.EndsWith("Touchpad 1 Finger 0 Ring", m.HotbarFireDescriptor);
        }

        [Fact]
        public void TheGroupsOwnDeadZoneAndLayout_ShapeTheDirections()
        {
            var m = Assert.Single(Translate(Config("joystick", "controller_ps4",
                ("deadzone", "8000"), ("layout", "1"))).Menus);
            var (press, release) = ConfigTranslator.SteamTriggerEdges(8000, 1.25);
            // Layout 1 widens each span by 180000 / overlap_region (4000) degrees.
            double width = 90.0 + 180000.0 / 4000.0;
            Assert.Equal(BoundedReads.Wedge(90, width / 2, width / 50, press, release, BoundedGate.None,
                SourceCoercion.LeftStickRingDescriptor), m.HotbarFireDescriptor);
        }

        [Fact]
        public void Settings_WrapDismissAndRecenter_AndTheFireType()
        {
            var m = Assert.Single(Translate(Config("dpad", "controller_ps4",
                ("scroll_wrap", "1"), ("test_param_0", "0"), ("test_param_1", "0"),
                ("touchmenu_button_fire_type", "3"))).Menus);
            Assert.True(m.HotbarWrap);
            Assert.False(m.HotbarDismissOnFire);
            Assert.False(m.HotbarRecenter);
            Assert.Equal(MenuFireType.Always, m.FireType);
        }

        [Fact]
        public void CellCount_IsTheLastBoundCell_CutByTheButtonCount()
        {
            // The client's bar ends at the last bound cell, and a button
            // count set lower cuts it (0x1388bfbf7 and 0x1388c1656).
            Assert.Equal(4, Assert.Single(Translate(Config("dpad")).Menus).CellCount);
            Assert.Equal(4, Assert.Single(Translate(Config("dpad", "controller_ps4",
                ("touch_menu_button_count", "9"))).Menus).CellCount);
            Assert.Equal(2, Assert.Single(Translate(Config("dpad", "controller_ps4",
                ("touch_menu_button_count", "2"))).Menus).CellCount);
        }

        [Theory]
        [InlineData("dpad", 10)]
        [InlineData("joystick", 10)]
        [InlineData("left_trackpad", 10)]
        [InlineData("right_joystick", 90)]
        [InlineData("right_trackpad", 90)]
        [InlineData("button_diamond", 90)]
        public void TheBar_SitsOnItsHostsSide_AtTheBottom(string source, int x)
        {
            var m = Assert.Single(Translate(Config(source, "controller_steamcontroller_gordon")).Menus);
            Assert.Equal(x, m.PosXPercent);
            Assert.Equal(80, m.PosYPercent);
            Assert.Equal(90, m.OpacityPercent);
        }
    }
}
