using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Grip (#392) on the macro trigger's hat reads. The recorder stores
    /// the held-frame angle and the three live reads (device-bound entry,
    /// device-free entry, legacy single-device list) rotate the raw hat
    /// the same way, so a stored Up fires from the press that points up
    /// on a sideways remote and not from the physical Up, which is Left
    /// in that hold. Runs the REAL slot evaluator with a device on the
    /// slot, the AxisTriggerReleaseRepro harness.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroPovTriggerGripTests : IDisposable
    {
        private static readonly Guid DevGuid = new("bbbb2222-3333-4444-5555-666677778888");
        private const int Slot = 0;

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly Func<string, int, SourceCoercion.GyroTuning> _savedTuning;

        public MacroPovTriggerGripTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedTuning = SourceCoercion.GyroTuningProvider;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SourceCoercion.GyroTuningProvider = _savedTuning;
        }

        private static CustomInputState ArrangeSlotDevice()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            var state = new CustomInputState();
            var ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Wii Remote",
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

        private static MacroItem HoldMacro()
        {
            var m = new MacroItem
            {
                Name = "Hat Up",
                IsEnabled = true,
                PadIndex = Slot,
                TriggerSource = MacroTriggerSource.InputDevice,
                TriggerMode = MacroTriggerMode.WhileHeld,
                ConsumeTriggerButtons = true,
                RepeatMode = MacroRepeatMode.FixedCount,
                RepeatCount = 1,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.RepeatVcButtonWhileHeld,
                ButtonFlags = 4096,
                IntervalMs = 100,
            });
            return m;
        }

        /// <summary>Stored Up (0 centidegrees) under Sideways: the physical
        /// Right press (9000) is the held-frame Up and fires. The physical
        /// Up press is the held-frame Left and does not.</summary>
        private static void AssertHeldFrame(MacroItem macro, CustomInputState state)
        {
            SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning { Grip = "Sideways" };
            var im = new InputManager();
            var macros = new[] { macro };

            state.Povs[0] = 9000;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(4096, gp.Buttons & 4096);

            state.Povs[0] = -1;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons & 4096);

            state.Povs[0] = 0;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, gp.Buttons & 4096);
            Assert.False(macro.IsExecuting);

            // Pointing: the physical Up is Up.
            SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning { Grip = "Pointing" };
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(4096, gp.Buttons & 4096);
        }

        [Fact]
        public void DeviceBoundEntry_FiresFromTheHeldFrame()
        {
            var state = ArrangeSlotDevice();
            var macro = HoldMacro();
            macro.TriggerInputs = $"in:{DevGuid}:pov:0:0";
            AssertHeldFrame(macro, state);
        }

        [Fact]
        public void DeviceFreeEntry_FiresFromTheHeldFrame()
        {
            var state = ArrangeSlotDevice();
            var macro = HoldMacro();
            macro.TriggerInputs = $"in:{Guid.Empty}:pov:0:0";
            AssertHeldFrame(macro, state);
        }

        [Fact]
        public void LegacyPovList_FiresFromTheHeldFrame()
        {
            var state = ArrangeSlotDevice();
            var macro = HoldMacro();
            macro.TriggerDeviceGuid = DevGuid;
            macro.TriggerPovs = new[] { "0:0" };
            AssertHeldFrame(macro, state);
        }
    }
}
