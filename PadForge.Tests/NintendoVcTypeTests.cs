using System;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins for the Nintendo virtual controller type: a first-class
    /// console family (own enum value, bucket, ordering, icon, labels)
    /// that rides the raw-HID data path with the switch-pro catalog
    /// profile and the #215 Nintendo lettering.
    /// </summary>
    public class NintendoVcTypeTests
    {
        // ── Persistence pins ──

        [Fact]
        public void EnumValue_IsAppendedAtFive()
        {
            // The numeric value is persisted in PadForge.xml; never reorder.
            Assert.Equal(5, (int)VirtualControllerType.Nintendo);
            var values = (VirtualControllerType[])Enum.GetValues(typeof(VirtualControllerType));
            Assert.Equal(VirtualControllerType.Nintendo, values[^1]);
        }

        [Fact]
        public void GroupOrder_NintendoSitsBetweenPlayStationAndExtended()
        {
            var order = VirtualControllerGroups.InOrder;
            int ps = Array.IndexOf(order, VirtualControllerType.PlayStation);
            int nin = Array.IndexOf(order, VirtualControllerType.Nintendo);
            int ext = Array.IndexOf(order, VirtualControllerType.Extended);
            Assert.True(ps >= 0 && nin == ps + 1 && ext == nin + 1,
                $"expected PlayStation < Nintendo < Extended, got ps={ps} nin={nin} ext={ext}");
        }

        // ── The load-bearing order gate: GetOrderFor must not throw ──

        [Fact]
        public void SlotOrders_GetOrderFor_ReturnsTheNintendoList()
        {
            var list = SettingsManager.SlotOrders.GetOrderFor(VirtualControllerType.Nintendo);
            Assert.Same(SettingsManager.NintendoSlotOrder, list);
        }

        // ── Default profile + assets ──

        [Fact]
        public void DefaultProfile_IsSwitchPro()
        {
            Assert.Equal("switch-pro",
                InputManager.GetDefaultProfileId(VirtualControllerType.Nintendo));
        }

        [Fact]
        public void AssetFolders_SwitchProGets2DSetAndNo3DMesh()
        {
            var (name2D, name3D) = HMaestroProfileCatalog.ResolveAssetFolders(
                "switch-pro", VirtualControllerType.Nintendo);
            Assert.Equal("SWITCHPRO", name2D);
            Assert.Null(name3D);
        }

        // ── Lettering (#215) rides the type via the Numbered style ──

        [Fact]
        public void DeriveStyle_NintendoIsNumbered_LettersViaProfile()
        {
            Assert.Equal(MacroButtonStyle.Numbered,
                MacroButtonNames.DeriveStyle(VirtualControllerType.Nintendo));
            // The switch-pro profile letters raw button 1 as B, per the
            // descriptor order the lettering table documents.
            Assert.Equal("B", MacroButtonNames.ExtendedButtonLabel("switch-pro", 1));
            Assert.Equal("A", MacroButtonNames.ExtendedButtonLabel("switch-pro", 2));
        }

        // ── Layout translation: Nintendo is Extended-shaped on the wire ──

        [Fact]
        public void LayoutKind_MatchesExtended_LabelStaysNintendo()
        {
            // Same canonical positions as an Extended raw layout, so Copy
            // From / clipboard translation is lossless between the two.
            var extSlot = MappingTranslation.GetPosition("ExtendedBtn5",
                VirtualControllerType.Extended, isExtended: true);
            var ninSlot = MappingTranslation.GetPosition("ExtendedBtn5",
                VirtualControllerType.Nintendo, isExtended: true);
            Assert.Equal(extSlot, ninSlot);

            // But the Copy From dialog names it Nintendo, not Extended.
            Assert.Equal("Nintendo",
                MappingTranslation.GetLayoutLabel(VirtualControllerType.Nintendo, isExtended: true));
        }

        [Fact]
        public void PadSettingJson_RoundTripsNintendoRawMappings()
        {
            var ps = new PadSetting();
            ps.SetExtendedMapping("ExtendedBtn3", "Gamepad ButtonA");
            string json = ps.ToJson(VirtualControllerType.Nintendo, isExtended: true);

            var restored = PadSetting.FromJson(json,
                out VirtualControllerType srcType, out bool srcIsExtended);
            Assert.Equal(VirtualControllerType.Nintendo, srcType);
            Assert.True(srcIsExtended);
            Assert.Contains(restored.ExtendedMappingEntries ?? Array.Empty<ExtendedMappingEntry>(),
                e => e.Key == "ExtendedBtn3");
        }
    }
}
