using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Common.Input.Telemetry;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #597: what a "Mouse Travel" input shows on its source's line, how its
    /// Steer setting and its gate stay one, how the line carries its
    /// settings to the saved form, and how Record picks the input on a
    /// virtual wheel's steering axis.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MouseTravelEditorTests
    {
        private static MappingItem PrimaryRow(string descriptor)
        {
            var row = new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick);
            row.LoadDescriptor(descriptor);
            return row;
        }

        private static MappingSourceItem AddedSource(string descriptor)
            => new MappingSourceItem { Kind = "Direct", Descriptor = descriptor };

        // ── What the line shows ──

        [Fact]
        public void TheFirstSourcesLine_ShowsTheSettingsOnlyForThisInput_AndOnlyTheOnesThatApply()
        {
            bool telemetry = GameTelemetryRuntime.Enabled;
            try
            {
                GameTelemetryRuntime.Enabled = false;

                // Mouse Position and Mouse Speed show none of them.
                foreach (string other in new[] { "Mouse Position X", "Axis 0" })
                {
                    var plain = PrimaryRow(other);
                    Assert.False(plain.IsMouseTravelSource, other);
                    Assert.False(plain.IsTravelReturnTimeShown, other);
                    Assert.False(plain.IsTravelSteerInputShown, other);
                    Assert.False(plain.IsTravelLockAtSpeedShown, other);
                }

                var row = PrimaryRow("Mouse Travel X");
                Assert.True(row.IsMouseTravelSource);
                Assert.True(PrimaryRow("Mouse Travel Y").IsMouseTravelSource);
                // A wheel's position takes no acceleration.
                Assert.False(row.IsParamAccelApplicable);
                Assert.True(row.IsHalfAxisApplicable);

                // Return Time: Spring and From Telemetry.
                Assert.False(row.IsTravelReturnTimeShown);
                row.ParamTravelCentering = MappingSource.TravelCenteringSpring;
                Assert.True(row.IsTravelReturnTimeShown);
                row.ParamTravelCentering = MappingSource.TravelCenteringTelemetry;
                Assert.True(row.IsTravelReturnTimeShown);
                row.ParamTravelCentering = MappingSource.TravelCenteringGameForce;
                Assert.False(row.IsTravelReturnTimeShown);

                // The Steer Input: While Held and Toggle.
                Assert.False(row.IsTravelSteerInputShown);
                row.ParamTravelSteer = MappingSource.TravelSteerHeld;
                Assert.True(row.IsTravelSteerInputShown);
                row.ParamTravelSteer = MappingSource.TravelSteerToggle;
                Assert.True(row.IsTravelSteerInputShown);
                row.ParamTravelSteer = MappingSource.TravelSteerAlways;
                Assert.False(row.IsTravelSteerInputShown);

                // Lock at Speed: while Game Telemetry is on.
                Assert.False(row.IsTravelLockAtSpeedShown);
                GameTelemetryRuntime.Enabled = true;
                Assert.True(row.IsTravelLockAtSpeedShown);

                // The gate is the Steer Input here, so the shaping line does
                // not show it a second time. On any other input it shows.
                row.ParamTravelSteer = MappingSource.TravelSteerHeld;
                row.GateDescriptor = "Button 2";
                Assert.True(row.HasGate);
                Assert.False(row.IsGateShown);
                var gated = PrimaryRow("Axis 0");
                gated.GateDescriptor = "Button 2";
                Assert.True(gated.IsGateShown);
            }
            finally
            {
                GameTelemetryRuntime.Enabled = telemetry;
            }
        }

        [Fact]
        public void AnAddedSourcesLine_ShowsTheSameSettings_AndAModeThatReadsKeysShowsNone()
        {
            bool telemetry = GameTelemetryRuntime.Enabled;
            try
            {
                GameTelemetryRuntime.Enabled = true;
                Assert.False(AddedSource("Mouse Position X").IsMouseTravelSource);

                var source = AddedSource("Mouse Travel X");
                Assert.True(source.IsMouseTravelSource);
                Assert.False(source.IsParamAccelApplicable);
                Assert.True(source.IsTravelLockAtSpeedShown);
                Assert.False(source.IsTravelReturnTimeShown);
                source.ParamTravelCentering = MappingSource.TravelCenteringSpring;
                Assert.True(source.IsTravelReturnTimeShown);
                Assert.False(source.IsTravelSteerInputShown);
                source.ParamTravelSteer = MappingSource.TravelSteerToggle;
                Assert.True(source.IsTravelSteerInputShown);

                // Incremental reads its Up and Down keys and leaves the input unread.
                var keys = new MappingSourceItem { Kind = "Incremental", Descriptor = "Mouse Travel X" };
                Assert.False(keys.IsMouseTravelSource);
                Assert.False(keys.IsTravelReturnTimeShown);
                Assert.False(keys.IsTravelSteerInputShown);
                Assert.False(keys.IsTravelLockAtSpeedShown);
            }
            finally
            {
                GameTelemetryRuntime.Enabled = telemetry;
            }
        }

        /// <summary>The Game Telemetry switch reaches every line: the flag
        /// the line reads is set before the change is raised, and the pad
        /// tells its rows and their added sources.</summary>
        [Fact]
        public void TheGameTelemetrySwitch_ShowsAndHidesLockAtSpeed_OnEveryLine()
        {
            bool telemetry = GameTelemetryRuntime.Enabled;
            try
            {
                GameTelemetryRuntime.Enabled = false;
                var dash = new DashboardViewModel();
                bool? seen = null;
                dash.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DashboardViewModel.EnableGameTelemetry))
                        seen = GameTelemetryRuntime.Enabled;
                };
                dash.EnableGameTelemetry = true;
                Assert.True(seen);
                dash.EnableGameTelemetry = false;
                Assert.False(seen);

                var pad = new PadViewModel(0);
                var row = pad.Mappings.First(m => m.TargetSettingName == "LeftThumbAxisX");
                row.LoadDescriptor("Mouse Travel X");
                var added = AddedSource("Mouse Travel Y");
                row.ExtraSources.Add(added);
                int rowHeard = 0, addedHeard = 0;
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MappingItem.IsTravelLockAtSpeedShown)) rowHeard++; };
                added.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MappingSourceItem.IsTravelLockAtSpeedShown)) addedHeard++; };
                GameTelemetryRuntime.Enabled = true;
                pad.RaiseMouseTravelRoadChanged();
                Assert.Equal(1, rowHeard);
                Assert.Equal(1, addedHeard);
                Assert.True(row.IsTravelLockAtSpeedShown);
                Assert.True(added.IsTravelLockAtSpeedShown);
            }
            finally
            {
                GameTelemetryRuntime.Enabled = telemetry;
            }
        }

        /// <summary>Search (Ctrl+K) finds every setting of the input, as a
        /// row option on the controller page.</summary>
        [Fact]
        public void SearchFindsEveryTravelSetting()
        {
            var markup = PadForge.Search.SearchSources.FileNames.ToDictionary(
                p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
            var index = PadForge.Search.SearchIndexBuilder.Read(markup, PadForge.Views.PadPage.TabOfGroupName);
            foreach (string key in new[]
                     {
                         "Mapping_MouseTravelCounts", "Mapping_MouseTravelCentering", "Mapping_MouseTravelReturnTime",
                         "Mapping_MouseTravelSteer", "Mapping_MouseTravelSteerInput", "Mapping_MouseTravelSmoothing",
                         "Mapping_MouseTravelLockAtSpeed",
                     })
                Assert.Contains(index.Entries, e => e.Kind == PadForge.Search.SearchKind.Setting
                    && e.Page == PadForge.Search.SearchPage.Pad && e.RowOptions && e.Label?.Key == key);
        }

        // ── Steer and the gate ──

        [Fact]
        public void SteerAlways_TakesTheInputOut_AndAModeThatReadsOneGivesItBack()
        {
            var row = PrimaryRow("Mouse Travel X");
            row.ParamTravelSteer = MappingSource.TravelSteerHeld;
            row.GateDescriptor = "Button 2";

            row.ParamTravelSteer = MappingSource.TravelSteerAlways;
            Assert.False(row.HasGate);

            row.ParamTravelSteer = MappingSource.TravelSteerToggle;
            Assert.Equal("Button 2", row.GateDescriptor);

            var source = AddedSource("Mouse Travel X");
            source.ParamTravelSteer = MappingSource.TravelSteerHeld;
            source.GateDescriptor = "Button 3";
            source.ParamTravelSteer = MappingSource.TravelSteerAlways;
            Assert.False(source.HasGate);
            source.ParamTravelSteer = MappingSource.TravelSteerHeld;
            Assert.Equal("Button 3", source.GateDescriptor);
        }

        [Fact]
        public void AGateBesideAlways_ShowsAsWhileHeld_TheWayTheEngineReadsIt()
        {
            // With Centering Off the engine leaves the wheel where it was let
            // go, which is While Held and Stay (#600).
            var saved = new MappingSource
            {
                Kind = "Direct", Descriptor = "Mouse Travel X", GateDescriptor = "Button 2",
            };
            var source = MappingSourceItem.FromDomain(saved);
            Assert.Equal(MappingSource.TravelSteerHeldStay, source.ParamTravelSteer);
            Assert.True(source.IsTravelSteerInputShown);

            // With a spring it brings the wheel back, which is While Held and
            // Center.
            saved.ParamTravelCentering = MappingSource.TravelCenteringSpring;
            Assert.Equal(MappingSource.TravelSteerHeldCenter, MappingSourceItem.FromDomain(saved).ParamTravelSteer);

            // An input with a gate that becomes Mouse Travel shows the same.
            var row = PrimaryRow("Axis 0");
            row.GateDescriptor = "Button 2";
            Assert.Equal(MappingSource.TravelSteerAlways, row.ParamTravelSteer);
            row.LoadDescriptor("Mouse Travel X");
            Assert.Equal(MappingSource.TravelSteerHeldStay, row.ParamTravelSteer);
        }

        /// <summary>#600: a mapping saved with the While Held of a build
        /// before While Held and Center and While Held and Stay opens in the
        /// one the engine reads it as, by its Centering, and the picker
        /// offers the four modes.</summary>
        [Fact]
        public void TheOldWhileHeld_OpensAsTheModeTheEngineReadsItIn()
        {
            var saved = new MappingSource
            {
                Kind = "Direct", Descriptor = "Mouse Travel X", GateDescriptor = "Button 2",
                ParamTravelSteer = MappingSource.TravelSteerHeld,
            };
            Assert.Equal(MappingSource.TravelSteerHeldStay, MappingSourceItem.FromDomain(saved).ParamTravelSteer);
            saved.ParamTravelCentering = MappingSource.TravelCenteringTelemetry;
            Assert.Equal(MappingSource.TravelSteerHeldCenter, MappingSourceItem.FromDomain(saved).ParamTravelSteer);
            var steers = new List<string>();
            foreach (var c in MouseTravelLine.SteerChoices()) steers.Add(c.Value);
            Assert.Equal(new[] { MappingSource.TravelSteerAlways, MappingSource.TravelSteerHeldCenter,
                MappingSource.TravelSteerHeldStay, MappingSource.TravelSteerToggle }, steers);
        }

        // ── The line and the saved form ──

        [Fact]
        public void TheLineCarriesEverySetting_ToTheSavedFormAndBack()
        {
            var saved = new MappingSource
            {
                Kind = "Direct", Descriptor = "Mouse Travel Y", GateDescriptor = "Button 4",
                ParamTravelCounts = 7500, ParamTravelCentering = MappingSource.TravelCenteringTelemetry,
                ParamTravelReturnTime = 1.5, ParamTravelSteer = MappingSource.TravelSteerToggle,
                ParamTravelSmoothingMs = 12, ParamTravelLockAtSpeed = 45,
            };

            var back = MappingSourceItem.FromDomain(saved).ToDomain();
            var row = PrimaryRow("Mouse Travel Y");
            row.LoadMouseTravel(saved);
            var stored = new MappingSource();
            row.StoreMouseTravel(stored);

            foreach (var s in new[] { back, stored })
            {
                Assert.Equal(7500, s.ParamTravelCounts);
                Assert.Equal(MappingSource.TravelCenteringTelemetry, s.ParamTravelCentering);
                Assert.Equal(1.5, s.ParamTravelReturnTime);
                Assert.Equal(MappingSource.TravelSteerToggle, s.ParamTravelSteer);
                Assert.Equal(12, s.ParamTravelSmoothingMs);
                Assert.Equal(45, s.ParamTravelLockAtSpeed);
            }
            Assert.Equal("Button 4", back.GateDescriptor);

            // A row cleared to no source takes every default.
            row.LoadMouseTravel(null);
            var cleared = new MappingSource();
            row.StoreMouseTravel(cleared);
            Assert.DoesNotContain("ParamTravel", MouseTravelXml(cleared));
        }

        private static string MouseTravelXml(MappingSource src)
        {
            var w = new System.IO.StringWriter();
            new System.Xml.Serialization.XmlSerializer(typeof(MappingSource)).Serialize(w, src);
            return w.ToString();
        }

        [Fact]
        public void TheTravelSlider_MultipliesAlongItsScale_AndLandsOnRoundCounts()
        {
            var row = PrimaryRow("Mouse Travel X");
            row.ParamTravelCountsDial = 0;
            Assert.Equal(100, row.ParamTravelCounts);
            row.ParamTravelCountsDial = 100;
            Assert.Equal(50000, row.ParamTravelCounts);
            row.ParamTravelCounts = MappingSource.DefaultTravelCounts;
            // The default stands about half way along, where a straight
            // scale would have put it a twenty-fifth of the way.
            Assert.InRange(row.ParamTravelCountsDial, 45, 55);
            row.ParamTravelCountsDial = row.ParamTravelCountsDial;
            Assert.Equal(2000, row.ParamTravelCounts);
            row.ParamTravelCounts = 99999;
            Assert.Equal(50000, row.ParamTravelCounts);
            row.ParamTravelCounts = double.NaN;
            Assert.Equal(50000, row.ParamTravelCounts);
        }

        // ── Record ──

        private sealed class MouseSession : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly MethodInfo _tick = typeof(RecorderService).GetMethod("PollTick", BindingFlags.Instance | BindingFlags.NonPublic);
            public MainViewModel ViewModel { get; } = new();
            public RecorderService Recorder { get; }
            public UserDevice Device { get; }
            public PadViewModel Pad => ViewModel.Pads[0];

            public MouseSession(VirtualControllerType output, string profileId)
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var state = new CustomInputState();
                Array.Fill(state.Axis, 32768);
                Device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), ProductName = "Mouse", IsOnline = true,
                    CapType = InputDeviceType.Mouse, InputState = state,
                };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(Device);
                Pad.OutputType = output;
                if (profileId != null) Pad.ProfileId = profileId;
                Pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = Device.InstanceGuid, Name = "Mouse", IsOnline = true });
                Recorder = new RecorderService(ViewModel);
            }

            public MappingItem Row(string target) => Pad.Mappings.Single(m => m.TargetSettingName == target);
            public void Tick() => _tick.Invoke(Recorder, new object[] { null, EventArgs.Empty });

            public string Record(string target, int axis, int value)
            {
                var row = Row(target);
                Recorder.StartRecording(row, 0);
                Assert.True(Recorder.IsRecording);
                Device.InputState.Axis[axis] = value;
                Tick();
                Assert.False(Recorder.IsRecording);
                return row.SourceDescriptor;
            }

            public void Dispose()
            {
                if (Recorder.IsRecording) Recorder.CancelRecording();
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
            }
        }

        // A wheel whose own axis, X, is its first stick's X.
        private const string WheelProfile = "logitech-g29";

        [Fact]
        public void ASidewaysMouseMove_OnAVirtualWheelsSteeringAxis_RecordsMouseTravelX()
        {
            using var session = new MouseSession(VirtualControllerType.Extended, WheelProfile);
            var sticks = session.Pad.GetSteerableSticks();
            Assert.NotEmpty(sticks);
            Assert.True(RecorderService.IsVirtualWheelSteeringRow(session.Row(sticks[0].XTarget)));
            Assert.Equal("Mouse Travel X", session.Record(sticks[0].XTarget, 0, 65535));
            // A move the other way records the same input, inverted.
            Assert.Equal("IMouse Travel X", session.Record(sticks[0].XTarget, 0, 0));
        }

        [Fact]
        public void EveryOtherRecording_OfTheMouseRecordsAsItDid()
        {
            // The wheel's other stick axis takes the mouse's speed, and so
            // does a sideways move on a gamepad's stick.
            using (var wheel = new MouseSession(VirtualControllerType.Extended, WheelProfile))
            {
                var sticks = wheel.Pad.GetSteerableSticks();
                Assert.False(RecorderService.IsVirtualWheelSteeringRow(wheel.Row(sticks[0].YTarget)));
                Assert.Equal("Axis 1", wheel.Record(sticks[0].YTarget, 1, 65535));
            }
            using (var pad = new MouseSession(VirtualControllerType.Xbox, null))
            {
                Assert.False(RecorderService.IsVirtualWheelSteeringRow(pad.Row("LeftThumbAxisX")));
                Assert.Equal("Axis 0", pad.Record("LeftThumbAxisX", 0, 65535));
            }
            // An Extended slot with a gamepad's profile has no wheel.
            using (var gamepad = new MouseSession(VirtualControllerType.Extended, "dualsense"))
            {
                var sticks = gamepad.Pad.GetSteerableSticks();
                Assert.False(RecorderService.IsVirtualWheelSteeringRow(gamepad.Row(sticks[0].XTarget)));
            }
            // A wheel whose own axis is on none of its sticks has no
            // steering row: the T80's stick reads Rx, and its wheel X.
            using (var t80 = new MouseSession(VirtualControllerType.Extended, "thrustmaster-t80"))
            {
                Assert.All(t80.Pad.Mappings, m => Assert.False(RecorderService.IsVirtualWheelSteeringRow(m)));
            }
        }
    }
}
