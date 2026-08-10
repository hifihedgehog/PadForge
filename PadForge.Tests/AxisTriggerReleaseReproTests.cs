using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Owner bug report 2026-08-10: a Repeat Button While Held macro
    /// triggered by an Assigned-Devices LeftTrigger axis entry (deadzone
    /// 20%, no pressure scaling) engages on press and never stops after
    /// release. This repro mirrors the reported PadForge.xml macro exactly,
    /// with the trigger entry parsed from the same serialized spec string
    /// (in:&lt;guid&gt;:ax:LeftTrigger:0:0:20:0).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AxisTriggerReleaseReproTests : IDisposable
    {
        private static readonly Guid DevGuid = new("eedce9cc-fa46-f6aa-4357-c36ab01f5fdc");
        private const int Slot = 0;

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public AxisTriggerReleaseReproTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static CustomInputState ArrangeSlotDevice()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            var state = new CustomInputState();
            var ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "DualShock 3",
                IsOnline = true,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(
                    new UserSetting { InstanceGuid = DevGuid, MapTo = Slot });
            return state;
        }

        private static MacroItem ReportedMacro()
        {
            var m = new MacroItem
            {
                Name = "Macro 1",
                IsEnabled = true,
                PadIndex = Slot,
                TriggerSource = MacroTriggerSource.InputDevice,
                TriggerMode = MacroTriggerMode.WhileHeld,
                ConsumeTriggerButtons = true,
                RepeatMode = MacroRepeatMode.FixedCount,
                RepeatCount = 1,
                TriggerAxisThreshold = 50,
                // The exact serialized entry from the report.
                TriggerInputs = "in:eedce9cc-fa46-f6aa-4357-c36ab01f5fdc:ax:LeftTrigger:0:0:20:0",
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                ButtonFlags = 4096,
                IntervalMs = 100,
                PressureScaledRate = false,
            });
            return m;
        }

        [Fact]
        public void AxisTriggerEntry_ReleaseStopsTheRepeat()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();
            var macro = ReportedMacro();
            var macros = new[] { macro };

            // Rest: trigger released (device-store rest for a trigger is 0).
            state.Axis[2] = 0;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons & 4096);

            // Pull the trigger: the turbo engages (first phase is ON).
            state.Axis[2] = 65535;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(4096, gp.Buttons & 4096);

            // Release: back to rest. The repeat must STOP.
            state.Axis[2] = 0;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons & 4096);

            // And stay stopped on subsequent frames.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons & 4096);
            Assert.False(macro.IsExecuting);
        }
    }
}
