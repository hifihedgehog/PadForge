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
        public void AssetFolders_SwitchProGets2DSetAndSwitch2ProMesh()
        {
            // Both Switch Pro generations share the Switch2Pro mesh, the
            // same arrangement as Xbox Series profiles riding the Xbox
            // One mesh.
            var (name2D, name3D) = HMaestroProfileCatalog.ResolveAssetFolders(
                "switch-pro", VirtualControllerType.Nintendo);
            Assert.Equal("SWITCHPRO", name2D);
            Assert.Equal("Switch2Pro", name3D);

            (name2D, name3D) = HMaestroProfileCatalog.ResolveAssetFolders(
                "switch2-pro-controller", VirtualControllerType.Nintendo);
            Assert.Equal("SWITCHPRO", name2D);
            Assert.Equal("Switch2Pro", name3D);
        }

        // ── Lettering (#215) rides the type via the Numbered style ──

        [Fact]
        public void DeriveStyle_NintendoIsNumbered_LettersViaProfile()
        {
            Assert.Equal(MacroButtonStyle.Numbered,
                MacroButtonNames.DeriveStyle(VirtualControllerType.Nintendo));
            // The switch-pro profile letters raw button 1 as B, per the
            // descriptor order the lettering table documents.
            Assert.Equal("B", MacroButtonNames.RawButtonLabel("switch-pro", 1));
            Assert.Equal("A", MacroButtonNames.RawButtonLabel("switch-pro", 2));
        }

        // ── Layout translation: Nintendo is Extended-shaped on the wire ──

        [Fact]
        public void LayoutKind_MatchesExtended_LabelStaysNintendo()
        {
            // Same canonical positions as an Extended raw layout, so Copy
            // From / clipboard translation is lossless between the two.
            var extSlot = MappingTranslation.GetPosition("RawBtn5",
                VirtualControllerType.Extended, isExtended: true);
            var ninSlot = MappingTranslation.GetPosition("RawBtn5",
                VirtualControllerType.Nintendo, isExtended: true);
            Assert.Equal(extSlot, ninSlot);

            // But the Copy From dialog names it Nintendo, not Extended.
            Assert.Equal("Nintendo",
                MappingTranslation.GetLayoutLabel(VirtualControllerType.Nintendo, isExtended: true));
        }

        // ── HM v1.3.18 (HM#33): the virtual Switch Pro gained a real
        //    IMU surface, so Nintendo joined the motion-capable set ──

        [Fact]
        public void EnsureMotionRows_TreatsNintendoAsMotionCapable()
        {
            var devices = new (string DeviceGuid, bool HasGyro, bool HasAccel)[]
            {
                (System.Guid.NewGuid().ToString(), true, true),
            };

            var nintendo = new MappingSet();
            MappingSetMigrator.EnsureMotionRows(nintendo,
                (int)VirtualControllerType.Nintendo, devices);
            Assert.Contains(nintendo.Rows,
                r => r?.Target == MappingSetMigrator.MotionGyroTarget);
            Assert.Contains(nintendo.Rows,
                r => r?.Target == MappingSetMigrator.MotionAccelTarget);

            // The gate stays closed for the families without a motion
            // surface (Extended raw slots have none).
            var extended = new MappingSet();
            MappingSetMigrator.EnsureMotionRows(extended,
                (int)VirtualControllerType.Extended, devices);
            Assert.DoesNotContain(extended.Rows,
                r => r?.Target == MappingSetMigrator.MotionGyroTarget);
        }

        [Fact]
        public void LetteredButtonCount_CapsTheDeadRailBits()
        {
            // The switch-pro descriptor declares 18 buttons; only the
            // role-mapped 14 reach the wire through the SDK packer.
            Assert.Equal(14, MacroButtonNames.NintendoLetteredButtonCount);
            // Every lettered index resolves; the first past the cap
            // falls back to the numbered format.
            for (int i = 0; i < MacroButtonNames.NintendoLetteredButtonCount; i++)
                Assert.NotNull(MacroButtonNames.NintendoExtendedLabel(i));
            Assert.Null(MacroButtonNames.NintendoExtendedLabel(
                MacroButtonNames.NintendoLetteredButtonCount));
        }

        // ── Positional automap (owner direction 2026-07-19): physical
        //    placement carries over, letters follow the Switch caps ──

        private static DeviceObjectItem Obj(int idx, DeviceObjectTypeFlags kind) => new()
        {
            InputIndex = idx,
            ObjectType = kind,
        };

        [Fact]
        public void CreateDefaultPadSetting_Nintendo_MapsPositionally()
        {
            var objs = new System.Collections.Generic.List<DeviceObjectItem>();
            for (int i = 0; i < 6; i++) objs.Add(Obj(i, DeviceObjectTypeFlags.AbsoluteAxis));
            for (int i = 0; i < 12; i++) objs.Add(Obj(i, DeviceObjectTypeFlags.PushButton));
            objs.Add(Obj(0, DeviceObjectTypeFlags.PointOfViewController));
            var ud = new UserDevice
            {
                CapType = (int)InputDeviceType.Gamepad,
                DeviceObjects = objs.ToArray(),
            };

            var ps = SettingsManager.CreateDefaultPadSetting(ud, VirtualControllerType.Nintendo);

            // Sticks pack at 0-3 (no analog triggers on the wire).
            Assert.Equal("Axis 0", ps.GetRawMapping("RawAxis0"));
            Assert.Equal("Axis 1", ps.GetRawMapping("RawAxis1"));
            Assert.Equal("Axis 3", ps.GetRawMapping("RawAxis2"));
            Assert.Equal("Axis 4", ps.GetRawMapping("RawAxis3"));
            // Positional face diamond: physical south lands on Switch B.
            Assert.Equal("Button 0", ps.GetRawMapping("RawBtn0"));
            Assert.Equal("Button 3", ps.GetRawMapping("RawBtn3"));
            // Trigger pulls press the digital ZL/ZR.
            Assert.Equal("Axis 2", ps.GetRawMapping("RawBtn6"));
            Assert.Equal("Axis 5", ps.GetRawMapping("RawBtn7"));
            // System cluster: Back to Minus, Start to Plus, Guide to Home,
            // Misc1 to Capture.
            Assert.Equal("Button 6", ps.GetRawMapping("RawBtn8"));
            Assert.Equal("Button 7", ps.GetRawMapping("RawBtn9"));
            Assert.Equal("Button 10", ps.GetRawMapping("RawBtn12"));
            Assert.Equal("Button 11", ps.GetRawMapping("RawBtn13"));
            Assert.Equal("POV 0 Up", ps.GetRawMapping("RawPov0Up"));
            // The gamepad-shaped fields stay untouched: the raw dict IS
            // the Nintendo surface.
            Assert.True(string.IsNullOrEmpty(ps.ButtonA));
        }

        [Fact]
        public void CreateDefaultPadSetting_Nintendo_GatesOnCapabilities()
        {
            // A pad with no Misc1 and no hat authors neither Capture nor
            // the D-pad rows (capability-assuming defaults tattoo).
            var objs = new System.Collections.Generic.List<DeviceObjectItem>();
            for (int i = 0; i < 6; i++) objs.Add(Obj(i, DeviceObjectTypeFlags.AbsoluteAxis));
            for (int i = 0; i < 11; i++) objs.Add(Obj(i, DeviceObjectTypeFlags.PushButton));
            var ud = new UserDevice
            {
                CapType = (int)InputDeviceType.Gamepad,
                DeviceObjects = objs.ToArray(),
            };

            var ps = SettingsManager.CreateDefaultPadSetting(ud, VirtualControllerType.Nintendo);
            Assert.True(string.IsNullOrEmpty(ps.GetRawMapping("RawBtn13")));
            Assert.True(string.IsNullOrEmpty(ps.GetRawMapping("RawPov0Up")));
        }

        [Fact]
        public void BuildFromLegacy_TranslatesRawDictIntoGridRows()
        {
            // The Mappings grid reads ONLY MappingSet rows, so raw-surface
            // automap entries must translate into rows or they render as
            // nothing (the DualSense-on-Nintendo report).
            var ps = new PadSetting();
            ps.SetRawMapping("RawBtn0", "Button 0");
            ps.SetRawMapping("RawBtn6", "Axis 2");
            ps.SetRawMapping("RawAxis0", "Axis 0");
            ps.SetRawMapping("RawPov0Up", "POV 0 Up");
            // Tuning keys share the dictionary and must NOT become rows.
            ps.SetRawMapping("RawStick0DzShape", "Circle");
            ps.SetRawMapping("FlickStickDots", "1.0");
            ps.FlushRawMappings();

            string guid = System.Guid.NewGuid().ToString();
            var ms = MappingSetMigrator.BuildFromLegacy(0,
                new (string, PadSetting, bool)[] { (guid, ps, true) });

            MappingRow Row(string t) => ms.Rows.Find(r => r?.Target == t);
            Assert.Equal("Button 0", Row("RawBtn0")?.Sources?[0]?.Descriptor);
            Assert.Equal("Axis 2", Row("RawBtn6")?.Sources?[0]?.Descriptor);
            Assert.Equal("Axis 0", Row("RawAxis0")?.Sources?[0]?.Descriptor);
            Assert.Equal("POV 0 Up", Row("RawPov0Up")?.Sources?[0]?.Descriptor);
            Assert.Equal(guid, Row("RawBtn0")?.Sources?[0]?.DeviceGuid);
            Assert.Null(Row("RawStick0DzShape"));
            Assert.Null(Row("FlickStickDots"));
        }

        // ── Raw-grammar migration (2026-07-19): the raw surface is
        //    category-neutral, its tokens no longer wear the Extended
        //    category's name; saves from before the rename normalize ──

        [Fact]
        public void LegacyRawTokens_NormalizeOnLoad()
        {
            Assert.Equal("RawBtn6", MappingSetMigrator.NormalizeRawToken("ExtendedBtn6"));
            Assert.Equal("RawAxis3Neg", MappingSetMigrator.NormalizeRawToken("ExtendedAxis3Neg"));
            Assert.Equal("RawPov0Up", MappingSetMigrator.NormalizeRawToken("ExtendedPov0Up"));
            Assert.Equal("RawStick0DzShape", MappingSetMigrator.NormalizeRawToken("ExtendedStick0DzShape"));
            // Non-raw tokens pass through untouched, including the
            // Extended CATEGORY's own persisted names.
            Assert.Equal("ExtendedMappings", MappingSetMigrator.NormalizeRawToken("ExtendedMappings"));
            Assert.Equal("ButtonA", MappingSetMigrator.NormalizeRawToken("ButtonA"));

            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow { Target = "ExtendedBtn0", LayerMask = "Base" });
            ms.Rows.Add(new MappingRow { Target = "MotionGyro", LayerMask = "Base" });
            MappingSetMigrator.NormalizeRawSurfaceTargets(ms);
            Assert.Equal("RawBtn0", ms.Rows[0].Target);
            Assert.Equal("MotionGyro", ms.Rows[1].Target);
        }

        [Fact]
        public void LegacyRawDictKeys_NormalizeOnFirstRead()
        {
            // Simulate a pre-rename save: the serialized array carries the
            // old spelling; the first dict read serves the new grammar.
            var ps = new PadSetting
            {
                RawMappingEntries = new[]
                {
                    new RawMappingEntry { Key = "ExtendedBtn3", Value = "Button 3" },
                    new RawMappingEntry { Key = "ExtendedAxis0", Value = "Axis 0" },
                },
            };
            Assert.Equal("Button 3", ps.GetRawMapping("RawBtn3"));
            Assert.Equal("Axis 0", ps.GetRawMapping("RawAxis0"));
        }

        [Fact]
        public void BuildFromLegacy_NormalizesLegacyArrayKeysIntoRows()
        {
            // Pre-rename saves carry legacy keys in the serialized ARRAY
            // (the dict only rebuilds keys on first read); the migrator
            // lane must normalize before its new-grammar filter or the
            // rows silently drop on upgrade.
            var ps = new PadSetting
            {
                RawMappingEntries = new[]
                {
                    new RawMappingEntry { Key = "ExtendedBtn5", Value = "Button 5" },
                },
            };
            var ms = MappingSetMigrator.BuildFromLegacy(0,
                new (string, PadSetting, bool)[] { (System.Guid.NewGuid().ToString(), ps, true) });
            Assert.Contains(ms.Rows, r => r?.Target == "RawBtn5"
                && r.Sources?.Count > 0 && r.Sources[0].Descriptor == "Button 5");
        }

        [Fact]
        public void PadSettingJson_RoundTripsNintendoRawMappings()
        {
            var ps = new PadSetting();
            ps.SetRawMapping("RawBtn3", "Gamepad ButtonA");
            string json = ps.ToJson(VirtualControllerType.Nintendo, isExtended: true);

            var restored = PadSetting.FromJson(json,
                out VirtualControllerType srcType, out bool srcIsExtended);
            Assert.Equal(VirtualControllerType.Nintendo, srcType);
            Assert.True(srcIsExtended);
            Assert.Contains(restored.RawMappingEntries ?? Array.Empty<RawMappingEntry>(),
                e => e.Key == "RawBtn3");
        }
    }
}
