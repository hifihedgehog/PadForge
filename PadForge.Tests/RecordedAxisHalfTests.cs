using System;
using System.Collections.Generic;
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
    /// A recorded stick direction has to be off while the stick rests. A stick
    /// rests at the middle of its range, so the recorder records the half the
    /// user pushed. A gamepad trigger rests at 0 and stays a full-axis entry
    /// (the #443 rule, InputManager.AxisRestsAtZero). The recorder used to write
    /// every axis full-axis with a 50% threshold, which sits on a stick's center
    /// line, so a macro recorded as "push right" fired with the stick at rest.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class RecordedAxisHalfTests : IDisposable
    {
        private static readonly Guid DevGuid = new("0b1e4f5a-3c2d-4e6f-8a9b-1c2d3e4f5a6b");
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public RecordedAxisHalfTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static MacroItem.TriggerInputEntry Record(int cap, bool forceRaw, int axisIndex, int pushed)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var state = new CustomInputState();
            for (int i = 0; i < state.Axis.Length; i++) state.Axis[i] = 32768;
            if (cap == InputDeviceType.Gamepad && !forceRaw)
            {
                state.Axis[2] = 0;   // an SDL gamepad's triggers rest at 0
                state.Axis[5] = 0;
            }
            var ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Pad",
                IsOnline = true,
                InputState = state,
                CapType = cap,
                ForceRawJoystickMode = forceRaw,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = DevGuid, MapTo = 0 });

            var vm = new MainViewModel();
            var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
            var macro = new MacroItem { Name = "rec", PadIndex = 0, TriggerSource = MacroTriggerSource.InputDevice };
            svc.StartMacroTriggerRecording(macro, 0);

            state.Axis[axisIndex] = pushed;
            var scan = typeof(InputService).GetMethod("DetectPerDeviceAxisDeflections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < 4; i++) scan.Invoke(svc, null);

            var entries = (List<MacroItem.TriggerInputEntry>)typeof(InputService)
                .GetField("_recordedPerDeviceAxisEntries", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(svc);
            Assert.Single(entries);
            return entries[0];
        }

        [Fact]
        public void AStickPushedRight_RecordsThePositiveHalf()
        {
            var e = Record(InputDeviceType.Gamepad, false, 0, 60000);
            Assert.Equal(MacroAxisTarget.LeftStickX, e.AxisTarget);
            Assert.True(e.HalfAxis);
            Assert.False(e.Invert);
        }

        [Fact]
        public void AStickPushedLeft_RecordsTheNegativeHalf()
        {
            var e = Record(InputDeviceType.Gamepad, false, 0, 5000);
            Assert.True(e.HalfAxis);
            Assert.True(e.Invert);
        }

        [Fact]
        public void AGamepadTrigger_StaysAFullAxisEntry()
        {
            var e = Record(InputDeviceType.Gamepad, false, 2, 50000);
            Assert.Equal(MacroAxisTarget.LeftTrigger, e.AxisTarget);
            Assert.False(e.HalfAxis);
            Assert.False(e.Invert);
        }

        private static UserDevice Device(int cap, bool forceRaw = false)
            => new UserDevice { InstanceGuid = DevGuid, CapType = cap, ForceRawJoystickMode = forceRaw };

        [Fact]
        public void OnlyTriggersSlidersAndVrGrips_RestAtZero()
        {
            Assert.True(InputManager.AxisRestsAtZero("Axis 2", Device(InputDeviceType.Gamepad)));
            Assert.True(InputManager.AxisRestsAtZero("Axis 5", Device(InputDeviceType.Gamepad)));
            Assert.False(InputManager.AxisRestsAtZero("Axis 0", Device(InputDeviceType.Gamepad)));
            Assert.False(InputManager.AxisRestsAtZero("Axis 2", Device(InputDeviceType.Gamepad, forceRaw: true)));
            Assert.False(InputManager.AxisRestsAtZero("Axis 2", Device(InputDeviceType.Joystick)));
            Assert.True(InputManager.AxisRestsAtZero("Slider 0", Device(InputDeviceType.Joystick)));
            Assert.False(InputManager.AxisRestsAtZero("Axis 2", null));
        }

        /// <summary>The mapping-grid recorder's rule: a centered axis on a
        /// button-like row is stored as a half, a trigger and an axis row are
        /// not, and a mouse keeps its #200 behavior.</summary>
        [Fact]
        public void TheMappingRecorder_StoresAHalfOnlyForACenteredAxisOnAButtonRow()
        {
            var method = typeof(RecorderService).GetMethod("RecordsAHalf", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var mapTypeParam = method.GetParameters()[2].ParameterType;
            object axis = Enum.Parse(mapTypeParam, "Axis");
            object slider = Enum.Parse(mapTypeParam, "Slider");
            var button = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            var stickRow = new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick);
            var pad = Device(InputDeviceType.Gamepad);
            bool Half(MappingItem m, string d, object type, UserDevice dev, bool mouse, bool inv, string kind = null)
                => (bool)method.Invoke(null, new object[] { m, d, type, dev, mouse, inv, kind });

            Assert.True(Half(button, "Axis 0", axis, pad, false, false));
            Assert.True(Half(button, "Axis 0", axis, pad, false, true));
            Assert.False(Half(button, "Axis 2", axis, pad, false, false));
            Assert.False(Half(button, "Slider 0", slider, pad, false, false));
            Assert.False(Half(stickRow, "Axis 0", axis, pad, false, false));
            Assert.False(Half(button, "Axis 0", axis, Device(InputDeviceType.Mouse), true, false));
            Assert.True(Half(button, "Axis 0", axis, Device(InputDeviceType.Mouse), true, true));

            // A Toggle or Rapid Trigger source on a trigger row is a press: a
            // stick recorded there takes the half, or its pull at rest sits on
            // the 50 percent line and the trigger presses with nothing touched.
            // A Direct trigger keeps the full axis it always had, a stick row
            // never takes a half, and a gamepad trigger as the source still
            // rests at 0.
            var trigger = new MappingItem("LT", "LeftTrigger", MappingCategory.Triggers);
            Assert.True(Half(trigger, "Axis 3", axis, pad, false, false, "RapidTrigger"));
            Assert.True(Half(trigger, "Axis 3", axis, pad, false, true, "Toggle"));
            Assert.False(Half(trigger, "Axis 3", axis, pad, false, false, "Direct"));
            Assert.False(Half(trigger, "Axis 3", axis, pad, false, false));
            Assert.False(Half(trigger, "Axis 2", axis, pad, false, false, "RapidTrigger"));
            Assert.False(Half(stickRow, "Axis 3", axis, pad, false, false, "Toggle"));
            var oneWay = new MappingItem("Throttle", "RawAxis2", MappingCategory.Triggers);
            Assert.True(Half(oneWay, "Axis 3", axis, pad, false, false, "Toggle"));

            // The ten button pressure rows (discussion #476) are read by the
            // trigger lane too, so the same rule holds on each.
            foreach (string target in PadForge.Engine.Data.MappingSetMigrator.PressureTargets)
            {
                var pressure = new MappingItem(target, target, MappingCategory.Buttons);
                Assert.True(Half(pressure, "Axis 3", axis, pad, false, false, "Toggle"));
                Assert.True(Half(pressure, "Axis 3", axis, pad, false, true, "RapidTrigger"));
                Assert.False(Half(pressure, "Axis 3", axis, pad, false, false, "Direct"));
                Assert.False(Half(pressure, "Axis 2", axis, pad, false, false, "Toggle"));
            }
        }

        /// <summary>A raw joystick's axis 2 is not a trigger: it rests centered.</summary>
        [Theory]
        [InlineData(InputDeviceType.Joystick, false)]
        [InlineData(InputDeviceType.Gamepad, true)]
        public void AxisTwoOfARawJoystick_RecordsAHalf(int cap, bool forceRaw)
        {
            var e = Record(cap, forceRaw, 2, 60000);
            Assert.True(e.HalfAxis);
        }
    }
}
