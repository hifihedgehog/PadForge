using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A source's five shaping values and its two gates belong to
    /// the source line now: the row editor's two view models load them, the
    /// user edits them, and the save rebuilds each source from them, so each
    /// direction of an axis keeps its own and a value set to off stays off.
    /// They used to ride a capture-and-restore list in the save, which no
    /// control could reach and which put back whatever the user removed.</summary>
    [Collection("SettingsManagerStatics")]
    public sealed class SourceShapingAndGateTests : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool _stale = InputService.VmMappingsStale;
        private readonly bool _suppress = InputService.SuppressMappingEditPush;

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            InputService.VmMappingsStale = _stale;
            InputService.SuppressMappingEditPush = _suppress;
        }

        private static readonly string Zone = BoundedReads.Between(30, 66, false, false, "Gamepad LeftStickRing");

        /// <summary>A left-stick X row whose two directions carry their own
        /// shaping and gates, the shape an import writes.</summary>
        private static MappingSet ImportedSet() => new()
        {
            Rows = new List<MappingRow>
            {
                new MappingRow
                {
                    Target = "LeftThumbAxisX", LayerMask = "Base",
                    Sources = new List<MappingSource>
                    {
                        new MappingSource
                        {
                            Descriptor = "Axis 0", ParamCurveExponent = 2.0, ParamRangeOuter = 0.8,
                            ParamAntiDeadzone = 0.1, ParamStickDeadZoneShape = 2, ParamStickDeadZoneInner = 0.05,
                            GateDescriptor = "Button 4", Gate2Descriptor = "Button 6",
                        },
                        new MappingSource
                        {
                            Descriptor = "Axis 0", Invert = true, HalfAxis = true, ParamCurveExponent = 3.0,
                            ParamRangeOuter = 0.6, GateDescriptor = Zone,
                        },
                    },
                },
            },
        };

        private static (MainViewModel vm, SettingsService ss) Arrange(MappingSet set)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotMappingSets[0] = set;
            InputService.VmMappingsStale = false;
            InputService.SuppressMappingEditPush = false;
            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            typeof(InputService).GetMethod("RefreshMappingsCore", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { vm.Pads[0] });
            return (vm, ss);
        }

        [Fact]
        public void TheEditor_LoadsEachSourcesOwnValues()
        {
            var (vm, _) = Arrange(ImportedSet());
            var row = vm.Pads[0].Mappings.Single(m => m.TargetSettingName == "LeftThumbAxisX");
            Assert.Equal(2.0, row.ParamCurveExponent);
            Assert.Equal(80.0, row.ParamRangeOuterPercent);
            Assert.Equal(10.0, row.ParamAntiDeadzonePercent);
            Assert.Equal(2, row.ParamStickDeadZoneShape);
            Assert.Equal(5.0, row.ParamStickDeadZoneInnerPercent);
            Assert.Equal("Button 4", row.GateDescriptor);
            Assert.Equal("Button 6", row.Gate2Descriptor);
            Assert.True(row.IsShapingShown);

            var neg = Assert.Single(row.ExtraSources);
            Assert.Equal(3.0, neg.ParamCurveExponent);
            Assert.Equal(60.0, neg.ParamRangeOuterPercent);
            Assert.Equal(Zone, neg.GateDescriptor);
            // The zone test reads as what it checks, never as its code.
            Assert.False(string.IsNullOrEmpty(neg.GateText));
            Assert.NotEqual(Zone, neg.GateText);
        }

        [Fact]
        public void TheSave_KeepsEachDirectionsValues_AndAnEditStays()
        {
            var (vm, ss) = Arrange(ImportedSet());
            var row = vm.Pads[0].Mappings.Single(m => m.TargetSettingName == "LeftThumbAxisX");
            var neg = row.ExtraSources.Single();

            // The user's edits: a new curve on one direction, anti-deadzone
            // and a gate turned off, the other direction's zone test cleared.
            row.ParamCurveExponent = 1.5;
            row.ParamAntiDeadzonePercent = 0;
            row.Gate2Descriptor = "";
            neg.ResetGateDescriptorCommand.Execute(null);
            vm.Pads[0].MappingsViewLoaded = true;

            ss.PushUiExtraSourcesIntoSlotMappingSets();

            var saved = SettingsManager.SlotMappingSets[0].Rows.Single(r => r.Target == "LeftThumbAxisX" && r.LayerMask == "Base");
            Assert.Equal(2, saved.Sources.Count);
            var p = saved.Sources[0];
            Assert.Equal(1.5, p.ParamCurveExponent);
            Assert.Equal(0.8, p.ParamRangeOuter, 6);
            Assert.Equal(0.0, p.ParamAntiDeadzone);
            Assert.Equal(2, p.ParamStickDeadZoneShape);
            Assert.Equal(0.05, p.ParamStickDeadZoneInner, 6);
            Assert.Equal("Button 4", p.GateDescriptor);
            Assert.Equal("", p.Gate2Descriptor);
            var n = saved.Sources[1];
            Assert.Equal(3.0, n.ParamCurveExponent);
            Assert.Equal(0.6, n.ParamRangeOuter, 6);
            Assert.Equal("", n.GateDescriptor);
        }

        [Fact]
        public void TheDials_ShowWhereNoCardShapes_AndHideOnACardOutputWithNone()
        {
            var mouse = new MappingItem("Mouse X", "MouseX", MappingCategory.Buttons) { SourceDescriptor = "Axis 0" };
            Assert.True(mouse.IsParamAccelApplicable);
            Assert.True(mouse.IsShapingShown);

            var stick = new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick) { SourceDescriptor = "Axis 0" };
            Assert.False(stick.IsShapingShown);
            stick.ParamCurveExponent = 2.0;
            Assert.True(stick.IsShapingShown);

            var button = new MappingItem("A", "ButtonA", MappingCategory.Buttons) { SourceDescriptor = "Button 0" };
            Assert.False(button.HasGate);
            button.GateDescriptor = "Button 4";
            Assert.True(button.HasGate);
        }

        [Fact]
        public void AnAddedSource_RoundTripsThroughItsItem()
        {
            var src = new MappingSource
            {
                Descriptor = "Axis 3", ParamCurveExponent = 0.5, ParamRangeOuter = 0.9, ParamAntiDeadzone = 0.2,
                ParamStickDeadZoneShape = 1, ParamStickDeadZoneInner = 0.1, GateDescriptor = Zone, Gate2Descriptor = "Button 2",
            };
            var back = MappingSourceItem.FromDomain(src).ToDomain();
            Assert.Equal(0.5, back.ParamCurveExponent);
            Assert.Equal(0.9, back.ParamRangeOuter, 6);
            Assert.Equal(0.2, back.ParamAntiDeadzone, 6);
            Assert.Equal(1, back.ParamStickDeadZoneShape);
            Assert.Equal(0.1, back.ParamStickDeadZoneInner, 6);
            Assert.Equal(Zone, back.GateDescriptor);
            Assert.Equal("Button 2", back.Gate2Descriptor);
        }

        /// <summary>The Flick Stick card pushes a flick source's values into
        /// the row only once it is seeded, and the save starts every source
        /// fresh, so a save with the card unseeded (no device yet, the usual
        /// state right after an import) must carry the row's own values
        /// across. It used to carry only the rotation offset and reset the
        /// rest to their defaults.</summary>
        [Fact]
        public void AnUnseededFlickCard_KeepsTheImportsFlickValues()
        {
            var set = new MappingSet
            {
                Rows = new List<MappingRow>
                {
                    new MappingRow
                    {
                        Target = "RightThumbAxisX", LayerMask = "Base",
                        Sources = new List<MappingSource>
                        {
                            new MappingSource
                            {
                                Descriptor = "Flick Stick Right", ParamFlickCountsPer360 = 2788, ParamFlickTime = 0.2,
                                ParamFlickThreshold = 0.8, ParamFlickSnapMode = "Eight", ParamFlickSmooth = 0.02,
                                ParamFlickRotationOffsetDeg = -37.5,
                            },
                        },
                    },
                },
            };
            var (vm, ss) = Arrange(set);
            vm.Pads[0].MappingsViewLoaded = true;

            ss.PushUiExtraSourcesIntoSlotMappingSets();

            var src = SettingsManager.SlotMappingSets[0].Rows
                .Single(r => r.Target == "RightThumbAxisX" && r.LayerMask == "Base").Sources.Single();
            Assert.Equal("Flick Stick Right", src.Descriptor);
            Assert.Equal(2788, src.ParamFlickCountsPer360, 3);
            Assert.Equal(0.2, src.ParamFlickTime, 3);
            Assert.Equal(0.8, src.ParamFlickThreshold, 3);
            Assert.Equal("Eight", src.ParamFlickSnapMode);
            Assert.Equal(0.02, src.ParamFlickSmooth, 3);
            Assert.Equal(-37.5, src.ParamFlickRotationOffsetDeg, 3);
        }

        /// <summary>The line under every source shows the shaping template,
        /// and every dial in it has its reset.</summary>
        [Fact]
        public void TheSourceLine_CarriesTheControls()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string xaml = File.ReadAllText(Path.Combine(dir!.FullName, "PadForge.App", "Views", "PadPage.xaml"));
            Assert.Contains("ContentTemplate=\"{StaticResource SourceShaping}\"", xaml);
            foreach (var (bind, reset) in new[]
            {
                ("Binding GatePick,", "ResetGateDescriptorCommand"),
                ("Binding Gate2Pick,", "ResetGate2DescriptorCommand"),
                ("Binding ParamCurveExponent,", "ResetParamCurveExponentCommand"),
                ("Binding ParamRangeOuterPercent,", "ResetParamRangeOuterCommand"),
                ("Binding ParamAntiDeadzonePercent,", "ResetParamAntiDeadzoneCommand"),
                ("Binding ParamStickDeadZoneShape,", "ResetParamStickDeadZoneShapeCommand"),
                ("Binding ParamStickDeadZoneInnerPercent,", "ResetParamStickDeadZoneInnerCommand"),
            })
            {
                Assert.Contains(bind, xaml);
                Assert.Contains(reset, xaml);
            }
        }
    }
}
