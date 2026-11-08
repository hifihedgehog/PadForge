using System;
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
    /// Recording a keyboard control or Opens With (#566): one input pressed
    /// and let go, or one held and a second pressed. A gamepad records by
    /// position under the device-free "Gamepad ..." names, any other device
    /// keeps its device, and an axis records the half it was pushed toward.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class RecorderPressTests
    {
        private sealed class Session : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly MethodInfo _tick = typeof(RecorderService).GetMethod("PollTick", BindingFlags.Instance | BindingFlags.NonPublic);
            public MainViewModel ViewModel { get; } = new();
            public RecorderService Recorder { get; }
            public UserDevice Device { get; }
            public RecorderService.RecordedPress? Result { get; private set; }

            public Session(int capType = InputDeviceType.Gamepad)
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var state = new CustomInputState();
                Array.Fill(state.Axis, 32768);
                state.Axis[2] = state.Axis[5] = 0;
                Device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), ProductName = "Pad", IsOnline = true,
                    CapType = capType, InputState = state,
                };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(Device);
                ViewModel.Pads[0].MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = Device.InstanceGuid, Name = "Pad", IsOnline = true });
                Recorder = new RecorderService(ViewModel);
                Recorder.StartRecordingPress(0, p => Result = p);
                Assert.True(Recorder.IsRecording);
            }

            public CustomInputState State => Device.InputState;

            public void Tick(int times = 1)
            {
                for (int i = 0; i < times; i++) _tick.Invoke(Recorder, new object[] { null, EventArgs.Empty });
            }

            public void Dispose()
            {
                if (Recorder.IsRecording) Recorder.CancelRecording();
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
            }
        }

        [Fact]
        public void AGamepadButton_RecordsByPosition_OnceLetGo()
        {
            using var s = new Session();
            s.State.Buttons[2] = true;
            s.Tick();
            // It waits: a second input would make a combination.
            Assert.Null(s.Result);
            Assert.True(s.Recorder.IsRecording);
            s.State.Buttons[2] = false;
            s.Tick();
            Assert.Equal(new RecorderService.RecordedPress("Gamepad ButtonX", "", "", ""), s.Result);
            Assert.False(s.Recorder.IsRecording);
        }

        [Fact]
        public void ASecondInputPressedWhileTheFirstIsHeld_MakesACombination()
        {
            using var s = new Session();
            s.State.Buttons[4] = true;
            s.Tick();
            Assert.Null(s.Result);
            s.State.Buttons[0] = true;
            s.Tick();
            Assert.Equal(new RecorderService.RecordedPress("Gamepad LeftShoulder", "", "Gamepad ButtonA", ""), s.Result);
        }

        [Fact]
        public void AnotherDevicesInput_KeepsItsDevice()
        {
            using var s = new Session(InputDeviceType.Joystick);
            s.State.Buttons[3] = true;
            s.Tick();
            s.State.Buttons[3] = false;
            s.Tick();
            Assert.Equal(new RecorderService.RecordedPress("Button 3", s.Device.InstanceGuid.ToString().ToLowerInvariant(), "", ""), s.Result);
        }

        [Fact]
        public void AStickPushed_RecordsTheHalfItWasPushedToward()
        {
            using var s = new Session();
            s.State.Axis[0] = 0;
            s.Tick(6);
            Assert.Null(s.Result);
            s.State.Axis[0] = 32768;
            s.Tick();
            Assert.Equal(new RecorderService.RecordedPress("IHGamepad LeftStickX", "", "", ""), s.Result);
        }

        [Fact]
        public void ATriggerPulled_RecordsTheWholeTrigger()
        {
            using var s = new Session();
            s.State.Axis[5] = 65535;
            s.Tick(6);
            s.State.Axis[5] = 0;
            s.Tick();
            Assert.Equal(new RecorderService.RecordedPress("Gamepad RightTrigger", "", "", ""), s.Result);
        }
    }
}
