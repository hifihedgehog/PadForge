using System;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The two DualShock 3 presets (discussion #476). The pad has Select and
    /// Start where the DualShock 4 and the DualSense have Share and Options,
    /// and no touchpad, so a slot on either preset letters those buttons the
    /// DualShock 3's way and binds no touchpad: in the grid, the macro and
    /// menu editors, the SOCD list and the default mapping.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class DualShock3PresetTests
    {
        private const VirtualControllerType PS = VirtualControllerType.PlayStation;

        private static readonly string[] TouchpadTargets =
        {
            "TouchpadX1", "TouchpadY1", "TouchpadX2", "TouchpadY2",
            "TouchpadContact1", "TouchpadContact2", "TouchpadClick",
        };

        // ── The preset ──

        [Theory]
        [InlineData("dualshock-3", true)]
        [InlineData("dualshock-3-full", true)]
        [InlineData("DualShock-3-Full", true)]
        [InlineData("dualshock-4-v2", false)]
        [InlineData("dualsense-composite", false)]
        [InlineData("dualsense-edge", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyTheTwoDualShock3PresetsLackATouchpad(string id, bool ds3)
        {
            Assert.Equal(ds3, HMaestroProfileCatalog.IsDualShock3(id));
            Assert.Equal(!ds3, HMaestroProfileCatalog.ReportCarriesTouchpad(id));
        }

        /// <summary>The prefix test covers the catalog: the two DualShock 3
        /// presets, and every other PlayStation preset is a DualShock 4 or a
        /// DualSense, the families whose HM profiles declare a touchpad. A new
        /// PlayStation family fails here until someone says which it is.</summary>
        [Fact]
        public void EveryOtherPlayStationPresetIsADualShock4OrADualSense()
        {
            var ids = HMaestroProfileCatalog.PlayStationProfiles.Select(p => p.Id).ToList();
            Assert.Equal(new[] { "dualshock-3", "dualshock-3-full" },
                ids.Where(HMaestroProfileCatalog.IsDualShock3).OrderBy(id => id, StringComparer.Ordinal));
            Assert.All(ids.Where(id => !HMaestroProfileCatalog.IsDualShock3(id)), id =>
                Assert.True(id.StartsWith("dualshock-4", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("dualsense", StringComparison.OrdinalIgnoreCase), id));
            // A slot with no preset runs the category default, which has one.
            Assert.True(HMaestroProfileCatalog.ReportCarriesTouchpad(InputManager.GetDefaultProfileId(PS)));
        }

        // ── The grid ──

        private static PadViewModel Grid(string profile)
        {
            var vm = new PadViewModel(0) { OutputType = PS };
            if (profile != null) Restore(vm, profile);
            return vm;
        }

        /// <summary>Sets a preset the way a restore does, stamp first, so the
        /// change never reads as live whatever an earlier test left stamped
        /// on slot 0 (PressureRowsTests.Restore).</summary>
        private static void Restore(PadViewModel vm, string profile)
        {
            string stamp = SettingsManager.GetWireStamp(vm.PadIndex);
            SettingsManager.StampNintendoWire(vm.PadIndex, profile);
            try { vm.ProfileId = profile; }
            finally { SettingsManager.StampNintendoWire(vm.PadIndex, stamp); }
        }

        private static string Label(PadViewModel vm, string target)
            => vm.Mappings.Single(m => m.TargetSettingName == target).TargetLabel;

        private static string[] TouchpadRows(PadViewModel vm)
            => vm.Mappings.Select(m => m.TargetSettingName).Where(TouchpadTargets.Contains).ToArray();

        [Theory]
        [InlineData("dualshock-3")]
        [InlineData("dualshock-3-full")]
        public void ADualShock3GridHasSelectAndStartAndNoTouchpad(string profile)
        {
            var vm = Grid(profile);
            Assert.Equal("Select", Label(vm, "ButtonBack"));
            Assert.Equal("Start", Label(vm, "ButtonStart"));
            Assert.Equal("PS", Label(vm, "ButtonGuide"));
            Assert.Empty(TouchpadRows(vm));
            Assert.DoesNotContain(vm.Mappings, m => m.Category == MappingCategory.Touchpad);
            // Its motion sensors keep their rows.
            Assert.Contains(vm.Mappings, m => m.TargetSettingName == "MotionGyro");
            Assert.Contains(vm.Mappings, m => m.TargetSettingName == "MotionAccel");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("dualshock-4-v2")]
        [InlineData("dualsense")]
        [InlineData("dualsense-edge-composite")]
        public void EveryOtherPlayStationGridKeepsShareOptionsAndTheTouchpad(string profile)
        {
            var vm = Grid(profile);
            Assert.Equal("Share", Label(vm, "ButtonBack"));
            Assert.Equal("Options", Label(vm, "ButtonStart"));
            Assert.Equal(TouchpadTargets, TouchpadRows(vm));
        }

        [Fact]
        public void SwitchingPresetsRelabelsTheButtonsAndMovesTheTouchpadRows()
        {
            var vm = Grid("dualshock-4-v2");
            Restore(vm, "dualshock-3");
            Assert.Equal("Select", Label(vm, "ButtonBack"));
            Assert.Equal("Start", Label(vm, "ButtonStart"));
            Assert.Empty(TouchpadRows(vm));
            Restore(vm, "dualsense");
            Assert.Equal("Share", Label(vm, "ButtonBack"));
            Assert.Equal("Options", Label(vm, "ButtonStart"));
            Assert.Equal(TouchpadTargets, TouchpadRows(vm));
        }

        // ── Macros, menus and SOCD ──

        [Theory]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-3", MacroButtonStyle.DualShock3)]
        [InlineData(VirtualControllerType.PlayStation, "dualshock-3-full", MacroButtonStyle.DualShock3)]
        [InlineData(VirtualControllerType.PlayStation, "dualsense", MacroButtonStyle.DualShock4)]
        [InlineData(VirtualControllerType.PlayStation, null, MacroButtonStyle.DualShock4)]
        [InlineData(VirtualControllerType.Xbox, "dualshock-3", MacroButtonStyle.Xbox360)]
        [InlineData(VirtualControllerType.Extended, "dualshock-3", MacroButtonStyle.Numbered)]
        public void TheMacroStyleFollowsThePreset(VirtualControllerType type, string profile, MacroButtonStyle expected)
            => Assert.Equal(expected, MacroButtonNames.DeriveStyle(type, profile));

        [Fact]
        public void TheDualShock3StyleHasSelectAndStartAndNoTouchpadButton()
        {
            var s = Strings.Instance;
            var ds3 = MacroButtonNames.GetButtonDefs(MacroButtonStyle.DualShock3);
            var ds4 = MacroButtonNames.GetButtonDefs(MacroButtonStyle.DualShock4);
            Assert.Equal(s.DevObj_Select, ds3.Single(d => d.Flag == 0x0020).Label);
            Assert.Equal(s.Btn_Start, ds3.Single(d => d.Flag == 0x0010).Label);
            Assert.DoesNotContain(ds3, d => d.Flag == 0x0800);
            // Every other button reads as the DualShock 4's, in its order.
            Assert.Equal(ds4.Where(d => d.Flag is not (0x0020 or 0x0010 or 0x0800)),
                ds3.Where(d => d.Flag is not (0x0020 or 0x0010)));
            Assert.Equal(s.DevObj_Select + " + " + s.Btn_Start,
                MacroButtonNames.FormatButtons(0x0030, MacroButtonStyle.DualShock3));
        }

        [Fact]
        public void TheOutputChannelNamesFollowTheStyle()
        {
            var s = Strings.Instance;
            string Name(MacroOutputChannel c, MacroButtonStyle style) => MacroOutputChannelNames.DisplayName(c, style);
            Assert.Equal(s.DevObj_Select, Name(MacroOutputChannel.Back, MacroButtonStyle.DualShock3));
            Assert.Equal(s.Btn_Start, Name(MacroOutputChannel.Start, MacroButtonStyle.DualShock3));
            Assert.Equal(s.Btn_PS, Name(MacroOutputChannel.Guide, MacroButtonStyle.DualShock3));
            Assert.Equal("✕", Name(MacroOutputChannel.A, MacroButtonStyle.DualShock3));
            Assert.Equal("L2", Name(MacroOutputChannel.LT, MacroButtonStyle.DualShock3));
            Assert.Equal(s.Btn_Share, Name(MacroOutputChannel.Back, MacroButtonStyle.DualShock4));
            Assert.Equal(s.Btn_Options, Name(MacroOutputChannel.Start, MacroButtonStyle.DualShock4));
        }

        [Fact]
        public void TheSlotsMacrosFollowAPresetChange()
        {
            var vm = Grid("dualshock-4-v2");
            vm.AddMacroCommand.Execute(null);
            var first = Assert.Single(vm.Macros);
            Assert.Equal(MacroButtonStyle.DualShock4, first.ButtonStyle);

            Restore(vm, "dualshock-3-full");
            Assert.Equal(MacroButtonStyle.DualShock3, first.ButtonStyle);
            vm.AddMacroCommand.Execute(null);
            vm.AddSoundMacroCommand.Execute(null);
            Assert.Equal(3, vm.Macros.Count);
            Assert.All(vm.Macros, m => Assert.Equal(MacroButtonStyle.DualShock3, m.ButtonStyle));

            Restore(vm, "dualsense");
            Assert.All(vm.Macros, m => Assert.Equal(MacroButtonStyle.DualShock4, m.ButtonStyle));
        }

        [Fact]
        public void TheMenuEditorFollowsThePreset()
        {
            var sets = SettingsManager.SlotMappingSets;
            try
            {
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                var set = new MappingSet();
                set.Menus.Add(new PadForge.Engine.Menus.MenuDefinitionEntry { MenuId = 1, Name = "Menu 1" });
                SettingsManager.SlotMappingSets[0] = set;

                var vm = Grid("dualshock-3");
                var menu = Assert.Single(vm.Menus);
                Assert.Equal(MacroButtonStyle.DualShock3, menu.ButtonStyle);
                var labels = menu.Cells[0].ButtonOptions.Select(o => o.Label).ToList();
                Assert.Contains(Strings.Instance.DevObj_Select, labels);
                Assert.DoesNotContain(Strings.Instance.Btn_Touchpad, labels);

                Restore(vm, "dualshock-4-v2");
                Assert.Equal(MacroButtonStyle.DualShock4, Assert.Single(vm.Menus).ButtonStyle);
            }
            finally
            {
                SettingsManager.SlotMappingSets = sets;
            }
        }

        [Fact]
        public void TheSocdListLettersTheDualShock3sButtons()
        {
            var s = Strings.Instance;
            var vm = Grid("dualshock-3");
            string Name(string target) => vm.SocdButtonOptions.Single(o => o.Value == target).Display;
            Assert.Equal(s.DevObj_Select, Name("ButtonBack"));
            Assert.Equal(s.Btn_Start, Name("ButtonStart"));
            Assert.Equal(s.Btn_PS, Name("ButtonGuide"));
            Restore(vm, "dualshock-4-v2");
            Assert.Equal(s.Btn_Share, Name("ButtonBack"));
            Assert.Equal(s.Btn_Options, Name("ButtonStart"));
        }

        [Fact]
        public void ALoadedMacroTakesTheSlotsPreset()
        {
            var data = SettingsService.BuildMacroDataForMacro(new MacroItem { Name = "M" }, 0);
            var ds3 = SettingsService.LoadMacroFromData(data, PS, null, "dualshock-3");
            Assert.Equal(MacroButtonStyle.DualShock3, ds3.ButtonStyle);
            // The raw-button lettering stays an Extended and Nintendo matter.
            Assert.Null(ds3.RawProfileId);
            Assert.Equal(MacroButtonStyle.DualShock4,
                SettingsService.LoadMacroFromData(data, PS, null, "dualsense").ButtonStyle);
        }

        // ── The default mapping ──

        private static string Field(PadSetting ps, string target)
            => (string)typeof(PadSetting).GetProperty(target).GetValue(ps);

        private static DeviceObjectItem Obj(int idx) => new()
        {
            InputIndex = idx,
            ObjectType = DeviceObjectTypeFlags.PushButton,
        };

        [Fact]
        public void APadsTouchpadMapsOnlyToAPresetThatHasOne()
        {
            var pad = new UserDevice
            {
                CapType = InputDeviceType.Gamepad,
                HasTouchpad = true,
                DeviceObjects = Enumerable.Range(0, 17).Select(Obj).ToArray(),
            };
            foreach (var preset in new[] { "dualshock-3", "dualshock-3-full" })
            {
                var ps = SettingsManager.CreateDefaultPadSetting(pad, PS, preset);
                Assert.All(TouchpadTargets, t =>
                    Assert.True(string.IsNullOrEmpty(Field(ps, t)), $"{preset} bound {t}"));
                // Select and Start still map.
                Assert.Equal("Button 6", ps.ButtonBack);
                Assert.Equal("Button 7", ps.ButtonStart);
            }
            foreach (var preset in new[] { null, "dualshock-4-v2", "dualsense-composite" })
            {
                var ps = SettingsManager.CreateDefaultPadSetting(pad, PS, preset);
                Assert.Equal("Touchpad 0 Finger 0 X", ps.TouchpadX1);
                Assert.Equal("Touchpad 0 Finger 1 Down", ps.TouchpadContact2);
                Assert.Equal("Touchpad 0 Click", ps.TouchpadClick);
            }
        }

        [Fact]
        public void ATouchpadDeviceMapsNothingOnADualShock3()
        {
            var ptp = new UserDevice
            {
                CapType = InputDeviceType.Touchpad,
                HasTouchpad = true,
                CapTouchpadCount = 1,
                CapTouchpadFingerCounts = new[] { 2 },
            };
            Assert.False(SettingsManager.CreateDefaultPadSetting(ptp, PS, "dualshock-3").HasAnyMapping);
            Assert.False(SettingsManager.CreateDefaultPadSetting(ptp, PS, "dualshock-3-full").HasAnyMapping);
            Assert.Equal("Touchpad 0 Finger 0 X",
                SettingsManager.CreateDefaultPadSetting(ptp, PS, "dualshock-4-v2").TouchpadX1);
        }
    }
}
