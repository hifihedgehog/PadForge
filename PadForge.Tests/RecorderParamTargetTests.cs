using System;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Ramp, Incremental or Invert On Hold key recorded by clicking that
    /// row's Record while another row is still recording. Starting the new
    /// recording cancels the old one, and the cancel used to clear the field
    /// the new recording was about to fill, so the press landed in the
    /// source's own descriptor.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class RecorderParamTargetTests
    {
        private sealed class Session : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly MethodInfo _tick = typeof(RecorderService).GetMethod("PollTick", BindingFlags.Instance | BindingFlags.NonPublic);
            public MainViewModel ViewModel { get; } = new();
            public RecorderService Recorder { get; }
            public UserDevice Device { get; }
            public PadViewModel Pad => ViewModel.Pads[0];

            public Session()
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var state = new CustomInputState();
                Array.Fill(state.Axis, 32768);
                Device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), ProductName = "Pad", IsOnline = true,
                    CapType = InputDeviceType.Gamepad, InputState = state,
                };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(Device);
                Pad.OutputType = VirtualControllerType.PlayStation;
                Pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = Device.InstanceGuid, Name = "Pad", IsOnline = true });
                Recorder = new RecorderService(ViewModel);
            }

            public MappingItem Row(string target) => Pad.Mappings.Single(m => m.TargetSettingName == target);
            public void Tick() => _tick.Invoke(Recorder, new object[] { null, EventArgs.Empty });

            public void Dispose()
            {
                if (Recorder.IsRecording) Recorder.CancelRecording();
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
            }
        }

        private static string Field(MappingSourceItem source, RecorderService.ParamTarget target) => target switch
        {
            RecorderService.ParamTarget.Up => source.ParamUp,
            RecorderService.ParamTarget.Down => source.ParamDown,
            _ => source.ParamModifier,
        };

        [Theory]
        [InlineData("Ramped", RecorderService.ParamTarget.Up)]
        [InlineData("Incremental", RecorderService.ParamTarget.Down)]
        [InlineData("InvertOnHold", RecorderService.ParamTarget.Modifier)]
        public void AKeyRecordedWhileAnotherRowRecordsLandsInItsField(string kind, RecorderService.ParamTarget target)
        {
            using var session = new Session();
            var other = session.Row("ButtonA");
            var row = session.Row("ButtonB");
            var source = new MappingSourceItem { Kind = kind };
            row.ExtraSources.Add(source);

            session.Recorder.StartRecording(other, 0, session.Device.InstanceGuid);
            Assert.True(session.Recorder.IsRecording);
            session.Recorder.StartRecordingExtraSourceParam(row, source, 0, target);
            Assert.True(session.Recorder.IsRecording);
            Assert.False(other.IsRecording);

            session.Device.InputState.Buttons[2] = true;
            session.Tick();
            Assert.False(session.Recorder.IsRecording);
            Assert.Equal("Button 2", Field(source, target));
            Assert.True(string.IsNullOrEmpty(source.Descriptor), $"the source's own descriptor took '{source.Descriptor}'");
            Assert.True(string.IsNullOrEmpty(other.SourceDescriptor));
        }
    }
}
