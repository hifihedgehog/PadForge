using System;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Radial / touch menu engine contracts (#9 B-17, Workshop wave 4c).
    /// Selection math pins the SHIPPED radial-zone convention (0 degrees =
    /// up, clockwise, 360/N wedges; GestureRecognizer.DetectRadialZones),
    /// which is also sc-controller's proven radial menu (angle =
    /// atan2(x, y) from up, item hit when |degdiff| &lt; 180/n). Fire types
    /// pin Steam's serialized touchmenu_button_fire_type semantics from
    /// the shipped configurator strings (Click / Release / Touch Release /
    /// Always), including the mode-shift-end commit and the dead-center
    /// dismiss.
    /// </summary>
    public class MenuRuntimeTests : IDisposable
    {
        public MenuRuntimeTests()
        {
            _savedProvider = SourceCoercion.MenuItemFiredProvider;
        }

        private readonly Func<int, string, int, int, bool> _savedProvider;

        public void Dispose()
        {
            SourceCoercion.MenuItemFiredProvider = _savedProvider;
        }

        // ─── Radial selection math ───

        [Theory]
        // 4-slot ring: up=1, right=2, down=3, left=4 (clockwise from top).
        [InlineData(0.0, -1.0, 4, 1)]
        [InlineData(1.0, 0.0, 4, 2)]
        [InlineData(0.0, 1.0, 4, 3)]
        [InlineData(-1.0, 0.0, 4, 4)]
        // Wedge boundaries: a 4-slot wedge spans ±45° around its center,
        // so 44° right-of-up still hovers slot 1 and 46° hovers slot 2.
        [InlineData(0.694, -0.719, 4, 1)]   // sin/cos 44°
        [InlineData(0.719, -0.694, 4, 2)]   // sin/cos 46°
        // Counterclockwise of up wraps to the LAST slot.
        [InlineData(-0.719, -0.694, 4, 4)]  // 46° left-of-up
        // 8-slot ring: the up-right diagonal is slot 2.
        [InlineData(0.707, -0.707, 8, 2)]
        public void RadialIndex_ZoneMathMatchesTheShippedConvention(
            double dx, double dy, int slots, int expected)
        {
            Assert.Equal(expected,
                MenuSelectionMath.RadialIndexFromVector(dx, dy, slots, hasCenter: false, 0.25));
        }

        [Fact]
        public void RadialIndex_Deadzone_SelectsCenterOnlyWhenBound()
        {
            Assert.Equal(0, MenuSelectionMath.RadialIndexFromVector(0.1, 0.1, 8, true, 0.25));
            Assert.Equal(-1, MenuSelectionMath.RadialIndexFromVector(0.1, 0.1, 8, false, 0.25));
            // Zero ring slots: center-only menus are legal (corpus
            // 793611331 g19 is a one-cell wheel; a bound center alone
            // still selects).
            Assert.Equal(-1, MenuSelectionMath.RadialIndexFromVector(1.0, 0.0, 0, false, 0.25));
            Assert.Equal(0, MenuSelectionMath.RadialIndexFromVector(0.0, 0.0, 0, true, 0.25));
        }

        // ─── Grid selection math ───

        [Theory]
        // Steam's rectangular counts land exactly; the hex counts
        // (5 / 7 / 13) rectangularize (named approximation).
        [InlineData(2, 2, 1)]
        [InlineData(4, 2, 2)]
        [InlineData(5, 3, 2)]
        [InlineData(7, 3, 3)]
        [InlineData(9, 3, 3)]
        [InlineData(12, 4, 3)]
        [InlineData(13, 4, 4)]
        [InlineData(16, 4, 4)]
        public void GridShape_IsNearSquare_WiderThanTall(int count, int cols, int rows)
        {
            Assert.Equal((cols, rows), MenuSelectionMath.GridShape(count));
        }

        [Theory]
        // 3x3 grid of 9: corners and center.
        [InlineData(0.05, 0.05, 9, 0)]
        [InlineData(0.95, 0.05, 9, 2)]
        [InlineData(0.5, 0.5, 9, 4)]
        [InlineData(0.05, 0.95, 9, 6)]
        [InlineData(0.95, 0.95, 9, 8)]
        // Partial last row (7 cells on 3x3): the empty bottom-right
        // clamps to the last real cell.
        [InlineData(0.95, 0.95, 7, 6)]
        // 2-cell menu: left / right halves.
        [InlineData(0.2, 0.5, 2, 0)]
        [InlineData(0.8, 0.5, 2, 1)]
        public void GridIndex_MapsPositionToCell(double nx, double ny, int count, int expected)
        {
            Assert.Equal(expected, MenuSelectionMath.GridIndexFromPosition(nx, ny, count));
        }

        // ─── Fire-type state machine ───

        private static MenuDefinitionEntry Radial4(MenuFireType fire, bool hasCenter = false) => new()
        {
            MenuId = 1,
            Kind = MenuKind.Radial,
            CellCount = 4,
            HasCenter = hasCenter,
            FireType = fire,
            EngageDeadzonePercent = 25,
        };

        private static void Tick(MenuRuntimeState st, MenuDefinitionEntry def, bool active,
            bool clicked, double dx, double dy, long nowMs)
            => MenuEvaluator.Update(st, def, active, clicked, dx, dy,
                (dx + 1) / 2, (dy + 1) / 2, nowMs);

        [Fact]
        public void TouchRelease_CommitsTheHoveredItem_OnDisengage()
        {
            var def = Radial4(MenuFireType.TouchRelease);
            var st = new MenuRuntimeState();

            Tick(st, def, active: true, clicked: false, 1.0, 0.0, nowMs: 1000); // hover slot 2
            Assert.Equal(2, st.HoveredIndex);
            Assert.False(MenuEvaluator.IsItemFired(st, 2, 1000)); // nothing fires while held

            Tick(st, def, active: false, clicked: false, 0, 0, nowMs: 1010); // lift
            Assert.True(MenuEvaluator.IsItemFired(st, 2, 1010));
            Assert.False(MenuEvaluator.IsItemFired(st, 1, 1010));

            // The commit is a pulse (the gesture engine's 100 ms latch
            // shape), not a latch: it expires.
            Tick(st, def, active: false, clicked: false, 0, 0, nowMs: 1200);
            Assert.False(MenuEvaluator.IsItemFired(st, 2, 1200));
        }

        [Fact]
        public void TouchRelease_DismissesSilently_FromTheDeadCenter()
        {
            var def = Radial4(MenuFireType.TouchRelease);
            var st = new MenuRuntimeState();

            Tick(st, def, true, false, 1.0, 0.0, 1000);  // hover slot 2
            Tick(st, def, true, false, 0.05, 0.0, 1010); // return to center (no center item)
            Assert.Equal(-1, st.HoveredIndex);
            Tick(st, def, false, false, 0, 0, 1020);     // lift in the dead zone
            for (int i = 0; i <= 4; i++)
                Assert.False(MenuEvaluator.IsItemFired(st, i, 1020));
        }

        [Fact]
        public void TouchRelease_CommitsTheCenterItem_WhenBound()
        {
            var def = Radial4(MenuFireType.TouchRelease, hasCenter: true);
            var st = new MenuRuntimeState();

            Tick(st, def, true, false, 0.05, 0.0, 1000); // rest in the deadzone
            Assert.Equal(0, st.HoveredIndex);            // center hovered
            Tick(st, def, false, false, 0, 0, 1010);
            Assert.True(MenuEvaluator.IsItemFired(st, 0, 1010));
        }

        [Fact]
        public void TouchRelease_LayerEnd_IsAReleaseEdge()
        {
            // Steam: "when the mode shift button is released". The caller
            // folds the layer into surfaceActive, so a layer ending while
            // the finger still touches commits exactly like a lift.
            var def = Radial4(MenuFireType.TouchRelease);
            var st = new MenuRuntimeState();

            Tick(st, def, true, false, 0.0, -1.0, 1000); // hover slot 1
            Tick(st, def, false, false, 0.0, -1.0, 1010); // layer off, finger still down
            Assert.True(MenuEvaluator.IsItemFired(st, 1, 1010));
        }

        [Fact]
        public void Click_AssertsWhileHoveredAndClicked()
        {
            var def = Radial4(MenuFireType.Click);
            var st = new MenuRuntimeState();

            Tick(st, def, true, false, 0.0, 1.0, 1000); // hover slot 3, no click
            Assert.False(MenuEvaluator.IsItemFired(st, 3, 1000));

            Tick(st, def, true, true, 0.0, 1.0, 1010);  // click
            Assert.True(MenuEvaluator.IsItemFired(st, 3, 1010));

            Tick(st, def, true, false, 0.0, 1.0, 1020); // release the click
            Assert.False(MenuEvaluator.IsItemFired(st, 3, 1020));
        }

        [Fact]
        public void ClickRelease_FiresOnce_OnTheClickFallingEdge()
        {
            var def = Radial4(MenuFireType.ClickRelease);
            var st = new MenuRuntimeState();

            Tick(st, def, true, true, -1.0, 0.0, 1000); // hover slot 4, click down
            Assert.False(MenuEvaluator.IsItemFired(st, 4, 1000));
            Tick(st, def, true, false, -1.0, 0.0, 1010); // click up
            Assert.True(MenuEvaluator.IsItemFired(st, 4, 1010));
            Tick(st, def, true, false, -1.0, 0.0, 1200); // pulse expires
            Assert.False(MenuEvaluator.IsItemFired(st, 4, 1200));
        }

        [Fact]
        public void Always_AssertsWhileHovered()
        {
            var def = Radial4(MenuFireType.Always);
            var st = new MenuRuntimeState();

            Tick(st, def, true, false, 0.0, -1.0, 1000);
            Assert.True(MenuEvaluator.IsItemFired(st, 1, 1000));
            Tick(st, def, true, false, 1.0, 0.0, 1010); // move to slot 2
            Assert.False(MenuEvaluator.IsItemFired(st, 1, 1010));
            Assert.True(MenuEvaluator.IsItemFired(st, 2, 1010));
            Tick(st, def, false, false, 0, 0, 1020);
            Assert.False(MenuEvaluator.IsItemFired(st, 2, 1020));
        }

        // ─── Descriptor grammar + coercion routing ───

        [Theory]
        [InlineData("Menu 1 Item 3", true, 1, 3)]
        [InlineData("Menu 12 Item 0", true, 12, 0)]
        [InlineData("Menu 1 Item", false, -1, -1)]
        [InlineData("Menu x Item 3", false, -1, -1)]
        [InlineData("Menu 1 Cell 3", false, -1, -1)]
        [InlineData("Menus 1 Item 3", false, -1, -1)]
        [InlineData("", false, -1, -1)]
        public void MenuItemDescriptor_ParsesStrictly(string d, bool ok, int menuId, int item)
        {
            Assert.Equal(ok, SourceCoercion.TryParseMenuItem(d, out int m, out int i));
            if (ok)
            {
                Assert.Equal(menuId, m);
                Assert.Equal(item, i);
            }
        }

        [Fact]
        public void MenuItemDescriptor_Classifies_AndIsPrefixSafe()
        {
            Assert.Equal(SourceCoercion.SourceType.MenuItem,
                SourceCoercion.ClassifyDescriptor("Menu 2 Item 5"));
            // Leading 'M' never collides with the legacy I/H prefix grammar.
            Assert.False(SourceCoercion.IsPrefixExemptDescriptor("Menu 2 Item 5"));
        }

        [Fact]
        public void MenuItemSource_ReadsThroughTheFiredProvider_ButtonAndAnalog()
        {
            (int Slot, string Guid, int Menu, int Item) seen = default;
            SourceCoercion.MenuItemFiredProvider = (slot, guid, menuId, item) =>
            {
                seen = (slot, guid, menuId, item);
                return menuId == 3 && item == 2;
            };

            var state = new CustomInputState();
            var src = new MappingSource { Descriptor = "Menu 3 Item 2" };

            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 7, "dev-guid"));
            Assert.Equal((7, "dev-guid", 3, 2), seen);
            Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(state, src, 7,
                false, "dev-guid"));
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(state, src, 7, "dev-guid"));

            var miss = new MappingSource { Descriptor = "Menu 3 Item 1" };
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, miss, 50, 7, "dev-guid"));
        }

        [Fact]
        public void MenuItemSource_ReadsFalse_WhenUnwired()
        {
            SourceCoercion.MenuItemFiredProvider = null;
            Assert.False(SourceCoercion.EvaluateForButtonTarget(
                new CustomInputState(), new MappingSource { Descriptor = "Menu 1 Item 1" }, 50, 0, "g"));
        }

        [Theory]
        [InlineData("Menu 1 Item 3")]
        public void BuildFromLegacy_KeepsMenuDescriptorsIntact(string descriptor)
        {
            // Same 1k grammar guard the IR families needed: the migrator
            // must never eat a leading letter off a menu descriptor.
            var ps = new PadSetting { LeftThumbAxisX = descriptor };
            var ms = MappingSetMigrator.BuildFromLegacy(
                0, new[] { ("11111111-1111-1111-1111-111111111111", ps) });
            var row = ms.Rows.FirstOrDefault(r => r.Target == "LeftThumbAxisX");
            Assert.NotNull(row);
            Assert.Equal(descriptor, Assert.Single(row.Sources).Descriptor);
        }

        // ─── Persistence ───

        private static MenuDefinitionEntry SampleMenu() => new()
        {
            DeviceGuid = "",
            MenuId = 4,
            Name = "Systems",
            Kind = MenuKind.Radial,
            HostDescriptor = "Touchpad 0",
            HostHalf = 1,
            CustomXDescriptor = "Axis 5",
            CustomYDescriptor = "Slider 0",
            ClickDescriptor = "Button 3",
            LayerMask = "Layer_47_1",
            FireType = MenuFireType.TouchRelease,
            CellCount = 5,
            HasCenter = true,
            ShowLabels = false,
            PosXPercent = 4,
            PosYPercent = 75,
            ScalePercent = 99,
            OpacityPercent = 89,
            EngageDeadzonePercent = 30,
            Items =
            {
                new MenuItemDefinition { Index = 0, Label = "Center", VirtualKey = 0x51 },
                new MenuItemDefinition { Index = 2, Label = "Fire", XboxButtons = Gamepad.A },
                new MenuItemDefinition { Index = 3, Label = "Raw", ExtendedButton = 37 },
                new MenuItemDefinition { Index = 5, Label = "Map" },
            },
        };

        private static void AssertMenusEqual(MenuDefinitionEntry a, MenuDefinitionEntry b)
        {
            Assert.Equal(a.DeviceGuid, b.DeviceGuid);
            Assert.Equal(a.MenuId, b.MenuId);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.HostDescriptor, b.HostDescriptor);
            Assert.Equal(a.HostHalf, b.HostHalf);
            Assert.Equal(a.CustomXDescriptor, b.CustomXDescriptor);
            Assert.Equal(a.CustomYDescriptor, b.CustomYDescriptor);
            Assert.Equal(a.ClickDescriptor, b.ClickDescriptor);
            Assert.Equal(a.LayerMask, b.LayerMask);
            Assert.Equal(a.FireType, b.FireType);
            Assert.Equal(a.CellCount, b.CellCount);
            Assert.Equal(a.HasCenter, b.HasCenter);
            Assert.Equal(a.ShowLabels, b.ShowLabels);
            Assert.Equal(a.PosXPercent, b.PosXPercent);
            Assert.Equal(a.PosYPercent, b.PosYPercent);
            Assert.Equal(a.ScalePercent, b.ScalePercent);
            Assert.Equal(a.OpacityPercent, b.OpacityPercent);
            Assert.Equal(a.EngageDeadzonePercent, b.EngageDeadzonePercent);
            Assert.Equal(a.Items.Count, b.Items.Count);
            for (int i = 0; i < a.Items.Count; i++)
            {
                Assert.Equal(a.Items[i].Index, b.Items[i].Index);
                Assert.Equal(a.Items[i].Label, b.Items[i].Label);
                Assert.Equal(a.Items[i].VirtualKey, b.Items[i].VirtualKey);
                Assert.Equal(a.Items[i].XboxButtons, b.Items[i].XboxButtons);
                Assert.Equal(a.Items[i].ExtendedButton, b.Items[i].ExtendedButton);
            }
        }

        [Fact]
        public void MappingSet_Menus_RoundTripXml()
        {
            var set = new MappingSet();
            set.Menus.Add(SampleMenu());

            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(MappingSet));
            var sb = new System.Text.StringBuilder();
            using (var w = new System.IO.StringWriter(sb))
                serializer.Serialize(w, set);
            MappingSet loaded;
            using (var r = new System.IO.StringReader(sb.ToString()))
                loaded = (MappingSet)serializer.Deserialize(r);

            AssertMenusEqual(SampleMenu(), Assert.Single(loaded.Menus));
        }

        [Fact]
        public void MappingSet_WithoutMenus_KeepsItsXmlShape()
        {
            // Old profiles have no <Menu> elements; loading them yields an
            // empty list, and an empty list serializes no elements (no
            // schema churn for every existing profile).
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(MappingSet));
            var sb = new System.Text.StringBuilder();
            using (var w = new System.IO.StringWriter(sb))
                serializer.Serialize(w, new MappingSet());
            Assert.DoesNotContain("<Menu", sb.ToString());
            using var r = new System.IO.StringReader(sb.ToString());
            var loaded = (MappingSet)serializer.Deserialize(r);
            Assert.NotNull(loaded.Menus);
            Assert.Empty(loaded.Menus);
        }

        [Fact]
        public void MenuDefinitionEntry_Clone_IsDeep()
        {
            var a = SampleMenu();
            var b = a.Clone();
            AssertMenusEqual(a, b);
            b.Items[0].Label = "changed";
            b.CellCount = 9;
            Assert.Equal("Center", a.Items[0].Label);
            Assert.Equal(5, a.CellCount);
        }

        [Fact]
        public void MenuHostOptions_AreTheFullGrammar_UngatedAndNeverLying()
        {
            // The opener list is device-agnostic, like the mapping
            // picker's "(Any device)" group: imported profiles land on
            // slots with nothing assigned yet, so every possible host is
            // prefilled regardless of assignment or connection state.
            var editor = new PadForge.ViewModels.MenuEditorItem(new MenuDefinitionEntry
            {
                HostDescriptor = "Touchpad 1",
            });
            var opts = editor.HostOptions;
            Assert.Equal(5, opts.Count);
            Assert.Equal("Gamepad LeftStick", opts[0].Descriptor);
            Assert.Equal("Gamepad RightStick", opts[1].Descriptor);
            Assert.Equal("Touchpad 0", opts[2].Descriptor);
            Assert.Equal("Touchpad 1", opts[3].Descriptor);
            Assert.Equal("Custom", opts[4].Descriptor);
            Assert.True(opts[2].IsTouchpad);
            Assert.True(opts[3].IsTouchpad);
            Assert.Equal("Touchpad 1", editor.SelectedHost.Descriptor);

            // An authored descriptor outside the grammar (a typeless
            // config's center_trackpad third pad, hand-edited XML) is
            // still listed with a faithful 1-based label, so the
            // selection never silently lies or falls back.
            editor.Entry.HostDescriptor = "Touchpad 2";
            opts = editor.HostOptions;
            Assert.Equal(6, opts.Count);
            Assert.Equal("Touchpad 2", opts[5].Descriptor);
            Assert.True(opts[5].IsTouchpad);
            Assert.Contains("3", opts[5].Label);
            Assert.Equal("Touchpad 2", editor.SelectedHost.Descriptor);
        }

        [Fact]
        public void MenuCell_BindingKinds_AreDynamicPerSlotType()
        {
            var editor = new PadForge.ViewModels.MenuEditorItem(new MenuDefinitionEntry());
            var cell = editor.Cells[0];

            // Button-capable slots (default): all three kinds offered.
            Assert.Equal(3, cell.BindingKindOptions.Count);

            // A KBM / MIDI slot must not OFFER a dead Controller Button
            // choice at all.
            editor.SupportsControllerButtons = false;
            Assert.Equal(2, cell.BindingKindOptions.Count);
            Assert.DoesNotContain(cell.BindingKindOptions, o => o.Value == 2);

            // ...unless the cell already carries a stale button binding
            // (slot-type switch): then the choice stays visible, marked,
            // so the selection never lies.
            editor.SupportsControllerButtons = true;
            cell.BindingKind = 2;
            editor.SupportsControllerButtons = false;
            Assert.Contains(cell.BindingKindOptions, o => o.Value == 2);
            Assert.NotEqual(
                PadForge.Resources.Strings.Strings.Instance.Menu_Binding_Button,
                cell.BindingKindOptions[2].Label);
        }

        [Fact]
        public void CustomOpener_RecordedAxesAndClick_RoundTripThroughTheEditor()
        {
            var editor = new PadForge.ViewModels.MenuEditorItem(new MenuDefinitionEntry());

            // Recording a non-gamepad analog folds to a Custom opener.
            Assert.True(editor.TryApplyRecordedHost("Axis 6"));
            Assert.True(editor.IsCustomHost);
            Assert.Equal("Axis 6", editor.Entry.CustomXDescriptor);

            // Steer Y and Click record through the shared targets; the
            // Click slot rejects analogs and the steer slots reject
            // buttons.
            Assert.True(editor.TryApplyRecorded(
                PadForge.ViewModels.MenuEditorItem.MenuRecordTarget.CustomY, "Slider 1"));
            Assert.Equal("Slider 1", editor.Entry.CustomYDescriptor);
            Assert.False(editor.TryApplyRecorded(
                PadForge.ViewModels.MenuEditorItem.MenuRecordTarget.Click, "Axis 2"));
            Assert.True(editor.TryApplyRecorded(
                PadForge.ViewModels.MenuEditorItem.MenuRecordTarget.Click, "Button 5"));
            Assert.Equal("Button 5", editor.Entry.ClickDescriptor);
            Assert.False(editor.TryApplyRecorded(
                PadForge.ViewModels.MenuEditorItem.MenuRecordTarget.CustomX, "Button 1"));
        }

        [Fact]
        public void MenuCustomInputDropdowns_MirrorTheRecordGates_AndNeverLie()
        {
            var editor = new PadForge.ViewModels.MenuEditorItem(new MenuDefinitionEntry
            {
                HostDescriptor = "Custom",
            });
            editor.InputChoicesProvider = () => new[]
            {
                // Abstract alias: canonicalizes to "Axis 0", analog.
                new PadForge.ViewModels.InputChoice { Descriptor = "Gamepad LeftStickX", DisplayName = "Left Stick X" },
                // Raw duplicate of the same canonical read: must collapse.
                new PadForge.ViewModels.InputChoice { Descriptor = "Axis 0", DisplayName = "Axis 1" },
                new PadForge.ViewModels.InputChoice { Descriptor = "Slider 0", DisplayName = "Slider 1" },
                new PadForge.ViewModels.InputChoice { Descriptor = "Button 3", DisplayName = "Button 4" },
                // A menu's own items must never feed its opener.
                new PadForge.ViewModels.InputChoice { Descriptor = "Menu 1 Item 2", DisplayName = "Menu 1 Item 2" },
            };

            // Steer: sentinel + the two analog reads (alias entry wins the
            // dedupe and carries the friendly label, canonical storage).
            var steer = editor.CustomXChoices;
            Assert.Equal(3, steer.Count);
            Assert.Equal("", steer[0].Descriptor);
            Assert.Equal("Axis 0", steer[1].Descriptor);
            Assert.Equal("Left Stick X", steer[1].Label);
            Assert.Equal("Slider 0", steer[2].Descriptor);

            // Click: sentinel + the button only. No analogs, no menu items.
            var click = editor.ClickChoices;
            Assert.Equal(2, click.Count);
            Assert.Equal("", click[0].Descriptor);
            Assert.Equal("Button 3", click[1].Descriptor);

            // Dropdown selection writes the model like the record path.
            editor.CustomXSelected = "Axis 0";
            Assert.Equal("Axis 0", editor.Entry.CustomXDescriptor);
            editor.ClickSelected = "Button 3";
            Assert.Equal("Button 3", editor.Entry.ClickDescriptor);

            // A stored descriptor the picker no longer offers still shows
            // as a never-lie entry instead of a blank combo.
            editor.CustomYSelected = "HAxis 7";
            Assert.Contains(editor.CustomYChoices, o => o.Descriptor == "HAxis 7");

            // Reset returns the row to the sentinel.
            editor.ResetCustomXCommand.Execute(null);
            Assert.Equal("", editor.CustomXSelected);
            Assert.Equal("", editor.Entry.CustomXDescriptor);
        }

        [Fact]
        public void MenuCell_ButtonPicker_FollowsSlotLettering()
        {
            // Xbox lettering (default): the picker's value space is the
            // shared button mask.
            var editor = new PadForge.ViewModels.MenuEditorItem(new MenuDefinitionEntry());
            var cell = editor.Cells[0];
            cell.BindingKind = 2;
            Assert.Equal(Gamepad.A, editor.Entry.Items[0].XboxButtons);
            Assert.Equal(0, editor.Entry.Items[0].ExtendedButton);

            // Extended lettering: the options become the layout's 1..N raw
            // button numbers and the value space swaps to ExtendedButton.
            editor.ExtendedButtonCount = 24;
            editor.ButtonStyle = PadForge.ViewModels.MacroButtonStyle.Numbered;
            Assert.Equal(24, cell.ButtonOptions.Count);
            Assert.Equal(5, cell.ButtonOptions[4].Value);
            cell.SelectedButtonFlag = 17;
            Assert.Equal(17, editor.Entry.Items[0].ExtendedButton);
            Assert.Equal(0, editor.Entry.Items[0].XboxButtons);

            // Back to a mask style: picking re-clears the raw number.
            editor.ButtonStyle = PadForge.ViewModels.MacroButtonStyle.DualShock4;
            cell.SelectedButtonFlag = Gamepad.B;
            Assert.Equal(Gamepad.B, editor.Entry.Items[0].XboxButtons);
            Assert.Equal(0, editor.Entry.Items[0].ExtendedButton);
        }

        [Fact]
        public void PadSettingClipboard_RoundTripsTheSlotMenusJson()
        {
            // Copy / Paste of a slot's settings carries the Menus tab
            // exactly like the shift authoring (__SlotShiftActivators).
            var ps = new PadSetting { SlotMenusJson = "[{\"MenuId\":4}]" };
            string json = ps.ToJson();
            Assert.Contains("__SlotMenus", json);
            var back = PadSetting.FromJson(json);
            Assert.Equal("[{\"MenuId\":4}]", back.SlotMenusJson);
        }

        [Fact]
        public void CloneMappingSetDeep_CarriesMenus()
        {
            var set = new MappingSet();
            set.Menus.Add(SampleMenu());
            var copy = PadForge.Services.InputService.CloneMappingSetDeep(set);
            var cloned = Assert.Single(copy.Menus);
            AssertMenusEqual(SampleMenu(), cloned);
            // Deep: mutating the copy never touches the profile snapshot.
            cloned.Items[0].Label = "poisoned";
            Assert.Equal("Center", set.Menus[0].Items[0].Label);
        }

        // ─── Macro trigger conversion ───

        [Fact]
        public void TryBuildTriggerEntry_ConvertsMenuItems_AsDescriptorEntries()
        {
            var choice = new PadForge.ViewModels.InputChoice
            {
                Descriptor = "Menu 1 Item 3",
                DeviceGuid = string.Empty,
            };
            Assert.True(PadForge.ViewModels.MacroItem.TryBuildTriggerEntry(choice, out var entry));
            Assert.Equal("Menu 1 Item 3", entry.SourceDescriptor);
            Assert.Equal(Guid.Empty, entry.DeviceGuid);
        }

        // ─── Chip display ───

        [Fact]
        public void ChipResolver_RendersMenuItemDescriptors()
        {
            string text = PadForge.Common.MappingDisplayResolver.ResolveDescriptorText(
                "Menu 2 Item 5", null, padPrefixAlways: true);
            Assert.Equal("Menu 2 Cell 5", text);
        }

        // ─── Workshop materialization ───

        [Fact]
        public void Materializer_ClonesMenus_IntoEveryClaimedSlot()
        {
            // Split configs feed both slots from one device; the fired
            // provider is slot-keyed, so each claimed slot needs its own
            // copy, and the copies must not alias the translator output.
            var translated = new PadForge.SteamWorkshop.Translation.TranslatedProfile
            {
                Name = "Menus",
                NeedsXboxSlot = true,
                NeedsKbmSlot = true,
            };
            translated.Menus.Add(SampleMenu());

            var p = PadForge.Services.WorkshopProfileMaterializer.Materialize(translated);

            var xbox = Assert.Single(p.SlotMappingSets[0].Menus);
            var kbm = Assert.Single(p.SlotMappingSets[1].Menus);
            AssertMenusEqual(SampleMenu(), xbox);
            AssertMenusEqual(SampleMenu(), kbm);
            Assert.NotSame(translated.Menus[0], xbox);
            Assert.NotSame(xbox, kbm);
        }

        [Fact]
        public void Materializer_MenuOnlyProfile_LandsOnTheKbmSlot()
        {
            var translated = new PadForge.SteamWorkshop.Translation.TranslatedProfile
            {
                Name = "MenusOnly",
                NeedsKbmSlot = true,
            };
            translated.Menus.Add(SampleMenu());

            var p = PadForge.Services.WorkshopProfileMaterializer.Materialize(translated);
            Assert.Single(p.SlotMappingSets[0].Menus);
        }
    }
}
