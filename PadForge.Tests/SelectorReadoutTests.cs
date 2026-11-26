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
    /// A readout under a picker reads the picked item, and a switch clears
    /// what the item before it left: the Gyro group under Driven by, the
    /// Force Feedback group's motor tests, and the Devices page's live
    /// previews after another row is opened.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SelectorReadoutTests : IDisposable
    {
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;

        public SelectorReadoutTests()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.UserSettings = _savedSettings;
        }

        private static UserDevice Device(Guid id, int capType, bool online, CustomInputState state)
        {
            var ud = new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = online, CapType = capType,
                ProductName = "Selector test device", InstanceName = "Selector test device",
                InputState = state,
            };
            SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        private static TouchpadInputState Pad(int maxFingers, params int[] down)
        {
            var pad = new TouchpadInputState(maxFingers);
            foreach (int f in down)
            {
                pad.FingerDown[f] = true;
                pad.FingerX[f] = 0.25f;
                pad.FingerY[f] = 0.75f;
            }
            return pad;
        }

        private static void Tick(InputService service)
            => typeof(InputService).GetMethod("UpdateDevicesRawState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, null);

        private static void Open(MainViewModel vm, Guid id)
        {
            var row = new DeviceRowViewModel { InstanceGuid = id };
            vm.Devices.Devices.Add(row);
            vm.Devices.SelectedDevice = row;
        }

        /// <summary>A DualSense moving under Driven by, then a
        /// DualShock 4 picked that has not connected this run (its gyro and
        /// accelerometer known from PadForge.xml, no reading). The Gyro
        /// group read the DualSense's last rates under the DualShock 4's
        /// name. It reads none, and none with no device picked.</summary>
        [Fact]
        public void TheGyroGroup_ShowsNoRatesForAPickedDeviceWithNoReading()
        {
            var vm = new MainViewModel();
            var service = new InputService(vm);
            var pad = vm.Pads[0];
            var moving = new CustomInputState();
            moving.Gyro[0] = 1f; moving.Gyro[1] = 0.5f; moving.Gyro[2] = -0.5f;
            moving.Accel[0] = 9.80665f;
            var dualSense = Device(Guid.NewGuid(), InputDeviceType.Gamepad, true, moving);
            dualSense.HasGyro = dualSense.HasAccel = true;
            var dualShock = Device(Guid.NewGuid(), InputDeviceType.Gamepad, false, null);
            dualShock.HasGyro = dualShock.HasAccel = true;

            service.ShowDeviceMotion(pad, 0, dualSense);
            Assert.NotEqual(0.0, pad.GyroLiveRatePitch);
            Assert.Equal(1.0, pad.AccelLiveX, 3);

            service.ShowDeviceMotion(pad, 0, dualShock);
            Assert.Equal(0.0, pad.GyroLiveRatePitch);
            Assert.Equal(0.0, pad.GyroLiveRateYaw);
            Assert.Equal(0.0, pad.GyroLiveRateRoll);
            Assert.Equal(0.0, pad.AccelLiveX);

            service.ShowDeviceMotion(pad, 0, dualSense);
            Assert.NotEqual(0.0, pad.GyroLiveRatePitch);
            service.ShowDeviceMotion(pad, 0, null);
            Assert.Equal(0.0, pad.GyroLiveRatePitch);
            Assert.Equal(0.0, pad.AccelLiveX);
        }

        /// <summary>The Test beside Left Motor and Right Motor
        /// shakes the device picked under Driven by, whose strength the row
        /// sets, as Test Rumble beside them does. They shook every device
        /// on the slot.</summary>
        [Fact]
        public void TheMotorTests_ShakeThePickedDevice()
        {
            var vm = new MainViewModel();
            var service = new InputService(vm);
            var pad = vm.Pads[0];
            var a = new PadViewModel.MappedDeviceInfo { InstanceGuid = Guid.NewGuid(), Name = "A", IsOnline = true };
            var b = new PadViewModel.MappedDeviceInfo { InstanceGuid = Guid.NewGuid(), Name = "B", IsOnline = true };
            pad.MappedDevices.Add(a);
            pad.MappedDevices.Add(b);
            pad.SelectedMappedDevice = b;
            var field = typeof(InputService).GetField("_inputManager", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var engine = new InputManager();
            field.SetValue(service, engine);
            try
            {
                service.SendTestMotor(pad, left: true);
                Assert.Equal(b.InstanceGuid, engine.TestRumbleTargetGuid[pad.PadIndex]);
                Assert.Equal(ushort.MaxValue, engine.VibrationStates[pad.PadIndex].LeftMotorSpeed);

                pad.SelectedMappedDevice = a;
                service.SendTestMotor(pad, left: false);
                Assert.Equal(a.InstanceGuid, engine.TestRumbleTargetGuid[pad.PadIndex]);
                Assert.Equal(ushort.MaxValue, engine.VibrationStates[pad.PadIndex].RightMotorSpeed);
            }
            finally
            {
                field.SetValue(service, null);
            }
        }

        /// <summary>A connected DualSense open on the Devices page
        /// with the gyro moving and a finger on the pad, then a DualShock 4
        /// row under Not Connected. Its gyro and touchpad sections showed
        /// the DualSense's numbers and dot beside axes rebuilt at rest. They
        /// read nothing.</summary>
        [Fact]
        public void ADisconnectedRow_ShowsNoneOfTheRowBeforesReadings()
        {
            var vm = new MainViewModel();
            var service = new InputService(vm);
            var moving = new CustomInputState();
            moving.Gyro[0] = 1f; moving.Accel[2] = 9.8f; moving.AccelAux[0] = 2f; moving.GyroAux[0] = 3f;
            moving.Touchpads = new[] { Pad(2, 0, 1), Pad(2, 0) };
            var dualSense = Device(Guid.NewGuid(), InputDeviceType.Gamepad, true, moving);
            dualSense.HasGyro = dualSense.HasAccel = dualSense.HasGyroAux = dualSense.HasAccelAux = true;
            dualSense.HasTouchpad = true;
            var dualShock = Device(Guid.NewGuid(), InputDeviceType.Gamepad, false, null);
            dualShock.HasGyro = dualShock.HasAccel = dualShock.HasGyroAux = dualShock.HasAccelAux = true;
            dualShock.HasTouchpad = true;

            Open(vm, dualSense.InstanceGuid);
            Tick(service);
            var d = vm.Devices;
            Assert.Equal(1.0, d.GyroX);
            Assert.True(d.TouchpadDown0);
            Assert.True(d.HasSecondTouchpadData);
            Assert.True(d.Pad2Down0);

            Open(vm, dualShock.InstanceGuid);
            Tick(service);
            Assert.True(d.HasRawData);
            Assert.True(d.HasGyroData);
            Assert.Equal(0.0, d.GyroX);
            Assert.Equal(0.0, d.AccelZ);
            Assert.Equal(0.0, d.AccelAuxX);
            Assert.Equal(0.0, d.GyroAuxX);
            Assert.False(d.TouchpadDown0);
            Assert.False(d.TouchpadDown1);
            Assert.Equal(0.0, d.TouchpadX0);
            Assert.False(d.HasSecondTouchpadData);
            Assert.False(d.Pad2Down0);
        }

        /// <summary>A dot past the pad's finger count was left as
        /// the row before drew it, and the page's comments say the writer
        /// hides it. A two-finger pad draws no third dot, a device with no
        /// second pad draws no second pad's dots, and a touchpad that reads
        /// no pads draws no dot at all.</summary>
        [Fact]
        public void ADotPastThePadsFingers_IsHidden()
        {
            var vm = new MainViewModel();
            var service = new InputService(vm);
            var state = new CustomInputState { Touchpads = new[] { Pad(2) } };
            var dualSense = Device(Guid.NewGuid(), InputDeviceType.Gamepad, true, state);
            dualSense.HasTouchpad = true;

            Open(vm, dualSense.InstanceGuid);
            Tick(service);
            var d = vm.Devices;
            // The same row on the next tick: nothing is rebuilt.
            d.TouchpadDown2 = true;
            d.TouchpadDown4 = true;
            d.Pad2Down0 = true;
            Tick(service);
            Assert.False(d.TouchpadDown2);
            Assert.False(d.TouchpadDown4);
            Assert.False(d.Pad2Down0);

            state.Touchpads = null;
            d.TouchpadDown0 = true;
            Tick(service);
            Assert.False(d.TouchpadDown0);
        }

        /// <summary>A shortcut set to a controller that then
        /// disconnected showed an empty Device box while it still answered
        /// that controller alone. The box shows it, not connected, and a
        /// device Remove Device took the record of shows as such. Another
        /// device that is not connected stays off the list.</summary>
        [Fact]
        public void AShortcutsDevice_StaysInThePickerWhileItIsNotConnected()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var online = Device(Guid.NewGuid(), InputDeviceType.Gamepad, true, new CustomInputState());
            online.InstanceName = "Connected pad";
            var stored = Device(Guid.NewGuid(), InputDeviceType.Gamepad, false, null);
            stored.InstanceName = "Stored pad";
            var other = Device(Guid.NewGuid(), InputDeviceType.Gamepad, false, null);

            var vm = new ProfileShortcutViewModel(new GlobalMacroData { TriggerDeviceGuid = stored.InstanceGuid }, null, null);
            var entry = Assert.Single(vm.DeviceChoices, c => c.DeviceGuid == stored.InstanceGuid);
            Assert.Equal(string.Format(s.Profiles_ShortcutDevice_NotConnected_Format, "Stored pad"), entry.DisplayName);
            Assert.Contains(vm.DeviceChoices, c => c.DeviceGuid == online.InstanceGuid && c.DisplayName == "Connected pad");
            Assert.DoesNotContain(vm.DeviceChoices, c => c.DeviceGuid == other.InstanceGuid);
            Assert.Equal(stored.InstanceGuid, vm.TriggerDeviceGuid);

            Guid removed = Guid.NewGuid();
            var gone = new ProfileShortcutViewModel(new GlobalMacroData { TriggerDeviceGuid = removed }, null, null);
            Assert.Equal(s.Profiles_ShortcutDevice_Removed,
                Assert.Single(gone.DeviceChoices, c => c.DeviceGuid == removed).DisplayName);

            // Any Connected Device adds nothing past the connected devices.
            var any = new ProfileShortcutViewModel(new GlobalMacroData(), null, null);
            Assert.Equal(new[] { Guid.Empty, online.InstanceGuid }, any.DeviceChoices.Select(c => c.DeviceGuid));
        }

        /// <summary>The Device picker and the keys named a device
        /// by its stored name, which for the merged keyboard row is English,
        /// while the Devices page and Driven by name it in the language in
        /// use. Both read the name those places read.</summary>
        [Fact]
        public void AShortcutNamesADevice_AsTheDevicesPageDoes()
        {
            var keyboards = Device(Guid.NewGuid(), InputDeviceType.Keyboard, true, new CustomInputState());
            keyboards.DevicePath = "aggregate://keyboards";
            keyboards.InstanceName = "Stored merged keyboards";
            string shown = InputService.LocalizedDeviceName(keyboards);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Devices_AllKeyboardsMerged, shown);

            var data = new GlobalMacroData
            {
                TriggerEntries = new[] { new TriggerButtonEntry { ButtonIndex = 4, DeviceInstanceGuid = keyboards.InstanceGuid } },
            };
            var vm = new ProfileShortcutViewModel(data, null, null);
            Assert.Equal(shown, Assert.Single(vm.DeviceChoices, c => c.DeviceGuid == keyboards.InstanceGuid).DisplayName);
            Assert.Equal(shown, Assert.Single(vm.ComboParts).Device);
        }

        /// <summary>A macro's trigger and a Custom
        /// Expression variable named their device by its stored name, so the
        /// merged keyboard row read in English in every language. They read
        /// the name the Devices page reads.</summary>
        [Fact]
        public void AMacroNamesADevice_AsTheDevicesPageDoes()
        {
            var keyboards = Device(Guid.NewGuid(), InputDeviceType.Keyboard, true, new CustomInputState());
            keyboards.DevicePath = "aggregate://keyboards";
            keyboards.InstanceName = "Stored merged keyboards";
            string shown = PadForge.Resources.Strings.Strings.Instance.Devices_AllKeyboardsMerged;

            var macro = new MacroItem { Name = "Merged", TriggerInputs = "in:" + keyboards.InstanceGuid + ":btn:4" };
            Assert.Contains("(" + shown + ")", macro.TriggerDisplayText);
            Assert.StartsWith(shown + ":", macro.TriggerInputItems.First().Label);
            Assert.DoesNotContain("Stored merged keyboards", macro.TriggerDisplayText);

            var variable = new MacroExpressionVariable
            {
                Source = MacroTriggerSource.InputDevice, DeviceGuid = keyboards.InstanceGuid, RawButton = 4,
            };
            Assert.StartsWith(shown + " ", variable.DisplaySummary);
        }

        /// <summary>The label's lifetime: the rows are built once at
        /// start, before any device connects, so the shortcut's own device
        /// reads "not connected" there. When it connects, the walk of the
        /// device list renames the entry in place, and the box keeps its
        /// pick.</summary>
        [Fact]
        public void AShortcutsDeviceEntry_FollowsTheDeviceConnecting()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var pad = Device(Guid.NewGuid(), InputDeviceType.Gamepad, false, null);
            pad.InstanceName = "Stored pad";
            var vm = new ProfileShortcutViewModel(new GlobalMacroData { TriggerDeviceGuid = pad.InstanceGuid }, null, null);
            var entry = Assert.Single(vm.DeviceChoices, c => c.DeviceGuid == pad.InstanceGuid);
            Assert.Equal(string.Format(s.Profiles_ShortcutDevice_NotConnected_Format, "Stored pad"), entry.DisplayName);

            pad.IsOnline = true;
            vm.RefreshDeviceNames();
            Assert.Same(entry, Assert.Single(vm.DeviceChoices, c => c.DeviceGuid == pad.InstanceGuid));
            Assert.Equal("Stored pad", entry.DisplayName);
            Assert.Equal(pad.InstanceGuid, vm.TriggerDeviceGuid);

            pad.IsOnline = false;
            vm.RefreshDeviceNames();
            Assert.Equal(string.Format(s.Profiles_ShortcutDevice_NotConnected_Format, "Stored pad"), entry.DisplayName);
        }
    }
}
