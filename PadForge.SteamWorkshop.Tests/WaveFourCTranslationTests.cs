using System.Linq;
using PadForge.Engine.Menus;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v7 (Wave 4c, #9 B-17) contracts: radial_menu /
    /// touch_menu groups become first-class overlay-backed menus.
    /// Grounded semantics: fire types are Steam's serialized
    /// touchmenu_button_fire_type 0..3 (shipped configurator "Touch Menu
    /// Activation Style" / "Radial Menu Button Type": Click / Release /
    /// Touch Release / Always); radial button_0 is the CENTER button
    /// ("ControllerBinding_RadialMenuButton0" = "Radial Menu Center
    /// Button") with ring slots 1..N clockwise from the top; grid size is
    /// touch_menu_button_count with "Same As Command Count" when absent
    /// (shipped value string ControllerSettingValue_TouchMenu_ButtonBindings);
    /// position / scale / opacity / show-labels ride the overlay
    /// (TouchMenuPosX/PosY/Scale/Opacity/ShowLabels). Corpus coverage
    /// rides the goldens (789818086, 3451446931, 3456927474, 2790927974
    /// above all); these tests pin the per-branch contracts.</summary>
    public class WaveFourCTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 47)
        {
            var config = Model.SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"Menus\"\n";
        private const string HeadPs4 = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"Menus\"\n\t\"controller_type\"\t\"controller_ps4\"\n";

        private static string Group(int id, string mode, string body = "", string name = null)
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n"
             + (name != null ? $"\t\t\"name\"\t\"{name}\"\n" : "")
             + body + "\t}\n";

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

        private static string Inputs(params (int Index, string Binding)[] cells)
        {
            var sb = new System.Text.StringBuilder("\t\t\"inputs\"\n\t\t{\n");
            foreach (var (index, binding) in cells)
            {
                sb.Append($"\t\t\t\"touch_menu_button_{index}\"\n\t\t\t{{\n");
                sb.Append("\t\t\t\t\"activators\"\n\t\t\t\t{\n");
                sb.Append("\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n");
                sb.Append("\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n");
                sb.Append($"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n");
                sb.Append("\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n");
            }
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

        private static string Preset(int id, string name, params (int GroupId, string Binding)[] entries)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"\t\"preset\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"name\"\t\"{name}\"\n");
            sb.Append("\t\t\"group_source_bindings\"\n\t\t{\n");
            foreach (var e in entries)
                sb.Append($"\t\t\t\"{e.GroupId}\"\t\"{e.Binding}\"\n");
            sb.Append("\t\t}\n\t}\n");
            return sb.ToString();
        }

        // ─── Radial menus ────────────────────────────────────────────────

        [Fact]
        public void RadialMenu_OnRightJoystick_EmitsMenuAndKeyRows()
        {
            string vdf = Head
                + Group(1, "radial_menu", Inputs(
                    (1, "key_press 1"), (2, "key_press 2"), (3, "key_press 3"), (4, "key_press 4")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.Equal(MenuKind.Radial, m.Kind);
            Assert.Equal("Gamepad RightStick", m.HostDescriptor);
            Assert.Equal(4, m.CellCount);
            Assert.False(m.HasCenter);
            Assert.Equal("", m.LayerMask);
            Assert.Equal(4, m.Items.Count);

            // Each key cell is an ordinary row fed by the item descriptor.
            var row1 = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey31"); // '1'
            Assert.Equal("Menu 1 Item 1", Assert.Single(row1.Sources).Descriptor);
            Assert.Equal(4, p.KbmMappingSet.Rows.Count);

            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MenuEmitted && e.Status == TranslationStatus.Clean);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.RadialMenuNeedsOverlay);
            Assert.Equal(1, p.Report.MenuCount);
            Assert.Contains(" menus:1 ", p.Report.ToSummaryString());
        }

        [Fact]
        public void RadialMenu_ButtonZero_IsTheCenterButton()
        {
            // Shipped configurator: RadialMenuButton0 = "Radial Menu
            // Center Button"; ring slots are 1..N.
            string vdf = Head
                + Group(1, "radial_menu", Inputs(
                    (0, "key_press Q"), (1, "key_press 1"), (2, "key_press 2"), (3, "key_press 3")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.True(m.HasCenter);
            Assert.Equal(3, m.CellCount); // ring slots exclude the center
            Assert.Equal("Gamepad LeftStick", m.HostDescriptor);
        }

        [Fact]
        public void RadialMenu_SparseRing_KeepsPositionalSlots()
        {
            // Corpus 3456927474 g15 carries ring indices 1,2,3,5,8,9,10,12:
            // buttons serialize under stable slot keys, so gaps stay gaps
            // (slots = highest bound ring index).
            string vdf = Head
                + Group(1, "radial_menu", Inputs(
                    (1, "key_press 1"), (2, "key_press 2"), (3, "key_press 3"), (5, "key_press 5")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.Equal(5, m.CellCount);
            Assert.Equal(new[] { 1, 2, 3, 5 }, m.Items.Select(i => i.Index).ToArray());
        }

        // ─── Touch menus (grids) ─────────────────────────────────────────

        [Fact]
        public void TouchMenu_GridSize_FromButtonCountSetting()
        {
            string vdf = Head
                + Group(1, "touch_menu",
                    Inputs((0, "key_press 1"), (1, "key_press 2"))
                    + Settings(("touch_menu_button_count", "9")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.Equal(MenuKind.Grid, m.Kind);
            Assert.Equal(9, m.CellCount);
            Assert.False(m.HasCenter); // grids have no center concept
            Assert.Equal("Touchpad 0", m.HostDescriptor);
        }

        [Fact]
        public void TouchMenu_AbsentButtonCount_UsesCommandCount()
        {
            // "Same As Command Count", the shipped default
            // (ControllerSettingValue_TouchMenu_ButtonBindings).
            string vdf = Head
                + Group(1, "touch_menu", Inputs((0, "key_press F5"), (1, "key_press F9")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(2, Assert.Single(p.Menus).CellCount);
        }

        [Fact]
        public void TwoCellTouchMenu_NoLongerRidesTouchSpots()
        {
            // v4-v6 mapped 2-cell trackpad menus onto the feature-gated
            // TouchLeft / TouchRight spots; v7 gives them the real menu.
            string vdf = HeadPs4
                + Group(1, "touch_menu", Inputs((0, "key_press F5"), (1, "key_press F9")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Single(p.Menus);
            Assert.DoesNotContain(p.KbmMappingSet.Rows, r =>
                r.Sources.Any(s => s.Descriptor.Contains("TouchLeft") || s.Descriptor.Contains("TouchRight")));
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_TrackpadFeatureRequired");
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.TouchMenuNeedsOverlay);
        }

        [Fact]
        public void Ps4LeftTrackpadMenu_HostsPadZeroLeftHalf()
        {
            // Single-pad controllers (#9 B-1): left_trackpad = the left
            // half of physical pad 0.
            string vdf = HeadPs4
                + Group(1, "touch_menu", Inputs((0, "key_press F5"), (1, "key_press F9")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Menus);
            Assert.Equal("Touchpad 0", m.HostDescriptor);
            Assert.Equal(1, m.HostHalf);
        }

        // ─── Settings vocabulary ─────────────────────────────────────────

        [Fact]
        public void FireType_ParsesSteamValues_AndDefaultsToClick()
        {
            string vdf = Head
                + Group(1, "radial_menu",
                    Inputs((1, "key_press 1"))
                    + Settings(("touchmenu_button_fire_type", "2")))
                + Group(2, "radial_menu", Inputs((1, "key_press 2")))
                + Preset(0, "Default", (1, "joystick active"), (2, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Equal(2, p.Menus.Count);
            Assert.Equal(MenuFireType.TouchRelease, p.Menus[0].FireType);
            Assert.Equal(MenuFireType.Click, p.Menus[1].FireType);
        }

        [Fact]
        public void OverlayGeometry_AndLabelsFlag_AreConsumed()
        {
            // 789818086 g24's shape: positioned bottom-right, scaled,
            // translucent; 3451446931 g11 carries show_labels 0.
            string vdf = Head
                + Group(1, "radial_menu",
                    Inputs((1, "key_press 1"))
                    + Settings(
                        ("touch_menu_position_x", "97"), ("touch_menu_position_y", "97"),
                        ("touch_menu_scale", "99"), ("touch_menu_opacity", "89"),
                        ("touch_menu_show_labels", "0"),
                        ("deadzone_inner_radius", "8192")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.Equal(97, m.PosXPercent);
            Assert.Equal(97, m.PosYPercent);
            Assert.Equal(99, m.ScalePercent);
            Assert.Equal(89, m.OpacityPercent);
            Assert.False(m.ShowLabels);
            Assert.Equal(25, m.EngageDeadzonePercent); // 8192/32767 = 25%
        }

        [Fact]
        public void InMenuSensitivity_GetsTheNamedPartial()
        {
            string vdf = Head
                + Group(1, "radial_menu",
                    Inputs((1, "key_press 1")) + Settings(("sensitivity", "120")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MenuTuningDropped);
            Assert.Contains("sensitivity", entry.ReasonArgs[0]);
        }

        // ─── Labels and icons ────────────────────────────────────────────

        [Fact]
        public void CellLabels_UseTheActionName_ThenTheParamFallback()
        {
            string vdf = Head
                + Group(1, "touch_menu", Inputs(
                    (0, "key_press F5, Quicksave, ghost_075_utility_020.png #000000 #E4E4E4"),
                    (1, "key_press F9")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.Equal("Quicksave", m.Items[0].Label);
            Assert.Equal("F9", m.Items[1].Label);
            // The authored icon carries on the item (v21), the icon-free
            // cell stays empty, and nothing reports.
            Assert.Equal("ghost_075_utility_020.png", m.Items[0].Icon);
            Assert.Equal("", m.Items[1].Icon);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MenuIconUnresolved);
        }

        [Fact]
        public void CellIcons_Carry_AcrossBothCorpusColorShapes()
        {
            // The corpus writes the icon field two ways: colors inside the
            // icon field ("icon.png #232323 #E4E4E4") and colors in a
            // fourth field ("icon.png, #232323 #E4E4E4"). Both carry the
            // bare name. The colors are not part of the reference.
            string vdf = Head
                + Group(1, "touch_menu", Inputs(
                    (0, "key_press M, Quick Map, ghost_050_menu_0030.png #000000 #E4E4E4"),
                    (1, "key_press T, Tech, ghost_070_setting_0040.png, "),
                    (2, "key_press S, , ghost_040_act_0315.png, #232323 #00AD3D")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.Equal("ghost_050_menu_0030.png", m.Items[0].Icon);
            Assert.Equal("ghost_070_setting_0040.png", m.Items[1].Icon);
            Assert.Equal("ghost_040_act_0315.png", m.Items[2].Icon);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MenuIconUnresolved);
        }

        [Fact]
        public void CellIcon_OutsideTheClientNameShape_DropsWithThePreciseNote()
        {
            // A pathed reference cannot resolve against the Steam
            // client's flat binding-icon art: the cell keeps its label
            // and the note names the exact file, per cell.
            string vdf = Head
                + Group(1, "touch_menu", Inputs(
                    (0, "key_press A, Attack, art/custom_attack.png #000000 #E4E4E4"),
                    (1, "key_press B, Block, ghost_040_act_0050.png #000000 #E4E4E4")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.Equal("", m.Items[0].Icon);
            Assert.Equal("ghost_040_act_0050.png", m.Items[1].Icon);
            var note = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MenuIconUnresolved);
            Assert.Equal(TranslationStatus.Partial, note.Status);
            Assert.Equal("art/custom_attack.png", note.ReasonArgs[0]);
            Assert.EndsWith("/touch_menu_button_0", note.SourcePath);
        }

        // ─── Cell binding shapes ─────────────────────────────────────────

        [Fact]
        public void LayerCells_BecomeActivators_OnTheItemDescriptor()
        {
            // 3353173512 g12 carries "controller_action add_layer 2 1 1"
            // inside a touch menu cell.
            string vdf = Head
                + Group(1, "touch_menu", Inputs(
                    (0, "controller_action add_layer 2 1 1, Numpad"),
                    (1, "key_press K")))
                + Group(2, "four_buttons",
                    "\t\t\"inputs\"\n\t\t{\n\t\t\t\"button_a\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
                    + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
                    + "\t\t\t\t\t\t\t\"binding\"\t\"key_press A\"\n"
                    + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n")
                + Preset(0, "Default", (1, "left_trackpad active"))
                + Preset(1, "Preset_1000001", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Menu 1 Item 0", act.Descriptor);
            Assert.Equal("Toggle", act.Mode); // add_layer latches
        }

        [Fact]
        public void CursorWarpCells_BecomeDescriptorTriggeredMacros()
        {
            // 3456927474 g15's shape: MOUSE_POSITION cells inside a radial.
            string vdf = Head
                + Group(1, "radial_menu", Inputs(
                    (1, "controller_action MOUSE_POSITION 10667 1473 1"),
                    (2, "key_press 2")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var macro = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.MoveMouseToScreenPosition, macro.Action);
            Assert.Equal("Menu 1 Item 1", Assert.Single(macro.TriggerInputDescriptors));
            Assert.False(macro.ConsumeTrigger);
        }

        [Fact]
        public void GameActionCells_StayInTheAggregate_ButTheMenuStillEmits()
        {
            string vdf = Head
                + Group(1, "touch_menu", Inputs(
                    (0, "game_action FPSControls taunt, Taunt"),
                    (1, "key_press T")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.Equal(2, m.Items.Count); // the label still renders
            Assert.Equal("Taunt", m.Items[0].Label);
            Assert.Single(p.KbmMappingSet.Rows); // only the key cell binds
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GameActionsNotSupported);
        }

        // ─── Hosting variants ────────────────────────────────────────────

        [Fact]
        public void ModeShiftMenu_LandsOnTheModeShiftLayer()
        {
            string vdf = Head
                + Group(1, "radial_menu", Inputs((1, "key_press 1")))
                + Preset(0, "Default", (1, "right_trackpad active modeshift"))
                + "}\n";
            var p = Translate(vdf);

            var m = Assert.Single(p.Menus);
            Assert.StartsWith("Layer_47_0_MS_", m.LayerMask);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal(m.LayerMask, row.LayerMask);
        }

        [Fact]
        public void EmptyMenu_GetsTheNamedSkip()
        {
            // 1957995349 g9/g10 and friends: placeholder menus with no
            // bound cells.
            string vdf = Head
                + Group(1, "touch_menu",
                    "\t\t\"inputs\"\n\t\t{\n\t\t}\n" + Settings(("touch_menu_button_count", "2")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Menus);
            Assert.Contains(p.Report.Entries, e => e.ReasonKey == TranslationReasons.MenuEmpty);
        }

        [Fact]
        public void RadialMenuOnDiamond_HostsOnTheButtonPair()
        {
            // v25: the face diamond hosts radial menus (Steam renders the
            // menu; the four buttons ARE the selector). Lowered onto the
            // "Gamepad Diamond" button-pair host.
            string vdf = Head
                + Group(1, "radial_menu", Inputs((1, "key_press 1")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var menu = Assert.Single(p.Menus);
            Assert.Equal("Gamepad Diamond", menu.HostDescriptor);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MenuSurfaceNotSupported);
        }

        [Fact]
        public void RadialMenuOnDpad_HostsOnTheButtonPair()
        {
            // Wild witness 1095852548: a six-cell key_press ring hosted on
            // the physical dpad (diagonal chords select between-wedge
            // cells through the 8-way pair vector).
            string vdf = Head
                + Group(1, "radial_menu", Inputs((1, "key_press 1"), (2, "key_press 2"),
                    (3, "key_press 3"), (4, "key_press 4"), (5, "key_press 5"), (6, "key_press 6")))
                + Preset(0, "Default", (1, "dpad active"))
                + "}\n";
            var p = Translate(vdf);
            var menu = Assert.Single(p.Menus);
            Assert.Equal("Gamepad DPad", menu.HostDescriptor);
            Assert.Equal(6, menu.CellCount);
        }

        [Fact]
        public void GridMenuOnGyro_KeepsTheNamedSkip()
        {
            // The gyro has no hover surface (and grid menus need an
            // absolute position even on button hosts): the named skip
            // survives for exactly these.
            string vdf = Head
                + Group(1, "touch_menu", Inputs((1, "key_press 1")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Menus);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MenuSurfaceNotSupported);
        }

        [Fact]
        public void GroupName_BecomesTheMenuName()
        {
            // 3451446931 g11 is named "Systems".
            string vdf = Head
                + Group(1, "radial_menu", Inputs((1, "key_press S")), name: "Systems")
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Systems", Assert.Single(p.Menus).Name);
        }

        [Fact]
        public void MenuOnlyConfig_StillDemandsASlot()
        {
            // A menu whose only cell is a game_action produces no rows and
            // no macros; the menu still needs a slot to live on.
            string vdf = Head
                + Group(1, "touch_menu", Inputs((0, "game_action X y, Label")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Single(p.Menus);
            Assert.False(p.NeedsXboxSlot);
            Assert.True(p.NeedsKbmSlot);
        }

        [Fact]
        public void TwoMenus_GetDistinctIds_InWalkOrder()
        {
            string vdf = Head
                + Group(1, "radial_menu", Inputs((1, "key_press 1")))
                + Group(2, "touch_menu", Inputs((0, "key_press 2")))
                + Preset(0, "Default", (1, "joystick active"), (2, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Equal(2, p.Menus.Count);
            // Walk order is (slot token, group id): joystick < left_trackpad.
            Assert.Equal(1, p.Menus.Single(m => m.Kind == MenuKind.Radial).MenuId);
            Assert.Equal(2, p.Menus.Single(m => m.Kind == MenuKind.Grid).MenuId);
        }
    }
}
