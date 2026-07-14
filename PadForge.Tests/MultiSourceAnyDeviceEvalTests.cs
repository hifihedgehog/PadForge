using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Owner report 2026-07-14: on an imported community profile, a Steam
    /// Controller sharing slot 0 with an Xbox pad had none of its face /
    /// Start / Back / paddle buttons fire, while its sticks / shoulders /
    /// D-pad worked. Ground truth from PadForge.xml: every dead target was a
    /// MULTI-source row (a paddle or touchpad-click OR'd with the passthrough,
    /// both empty-guid "any device"); every working target was single-source.
    ///
    /// A multi-source row is evaluated ONCE per slot (the
    /// _multiSourceEvaluatedTargetsBySlot de-dup), and BuildCustomContribsForButton
    /// read an empty-guid source only from the ONE device being evaluated when
    /// the row was first reached. With two devices on the slot, the Xbox pad
    /// (evaluated first) claimed every multi-source row and read it from
    /// itself, so the Steam Controller's presses were dropped. Single-source
    /// rows escaped because they evaluate per-device and Step 4 OR-merges.
    ///
    /// The fix makes an empty-guid source inside a multi-source row span every
    /// device on the slot (GetSlotDeviceStates), OR-ing the button read. These
    /// tests drive the shared multi-source path (TryEvaluateMappingSetButton →
    /// BuildCustomContribsForButton) with two registered devices and assert the
    /// row fires from the NON-evaluated device.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MultiSourceAnyDeviceEvalTests : IDisposable
    {
        private static readonly Guid DevFirst = new("11111111-1111-1111-1111-111111111111");
        private static readonly Guid DevSecond = new("22222222-2222-2222-2222-222222222222");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public MultiSourceAnyDeviceEvalTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        // Registers two online devices on slot 0 with the given per-device
        // "Button 0" (canonical of "Gamepad ButtonA") state, and returns the
        // first device's state (the one the evaluator is driven with).
        private static CustomInputState ArrangeTwoDeviceSlot(bool firstButtonA, bool secondButtonA)
        {
            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            var sFirst = new CustomInputState(); sFirst.Buttons[0] = firstButtonA;
            var sSecond = new CustomInputState(); sSecond.Buttons[0] = secondButtonA;

            Add(DevFirst, sFirst);
            Add(DevSecond, sSecond);
            return sFirst;

            static void Add(Guid guid, CustomInputState state)
            {
                var ud = new UserDevice
                {
                    InstanceGuid = guid,
                    CapType = InputDeviceType.Gamepad,
                    CapButtonCount = 16,
                    IsOnline = true,
                    InputState = state,
                };
                lock (SettingsManager.UserDevices.SyncRoot)
                    SettingsManager.UserDevices.Items.Add(ud);
                var us = new UserSetting { InstanceGuid = guid, MapTo = 0 };
                lock (SettingsManager.UserSettings.SyncRoot)
                    SettingsManager.UserSettings.Items.Add(us);
            }
        }

        // The imported row shape: ButtonA <- (Gamepad Paddle1 OR Gamepad ButtonA),
        // both empty-guid. Paddle1 is Button 12, ButtonA is Button 0.
        private static MappingSet ButtonAMultiSourceRow()
        {
            var ms = new MappingSet();
            var row = new MappingRow { Target = "ButtonA", LayerMask = "Base", CombineMode = "OR" };
            row.Sources.Add(new MappingSource { Kind = "Direct", DeviceGuid = "", Descriptor = "Gamepad Paddle1" });
            row.Sources.Add(new MappingSource { Kind = "Direct", DeviceGuid = "", Descriptor = "Gamepad ButtonA" });
            ms.Rows.Add(row);
            return ms;
        }

        [Fact]
        public void SecondDevicePress_FiresMultiSourceAnyDeviceRow()
        {
            // First device (evaluated) has nothing pressed; the SECOND device
            // has A. Before the fix this returned false (empty-guid read only
            // the first device). After the fix it spans the slot and fires.
            var first = ArrangeTwoDeviceSlot(firstButtonA: false, secondButtonA: true);
            var ms = ButtonAMultiSourceRow();

            bool handled = InputManager.TryEvaluateMappingSetButton(
                first, ms, DevFirst.ToString(), 0, "ButtonA", 50, out bool v);

            Assert.True(handled);
            Assert.True(v, "ButtonA must fire from the second device on the slot");
        }

        [Fact]
        public void NeitherDevicePressed_RowStaysOff()
        {
            var first = ArrangeTwoDeviceSlot(firstButtonA: false, secondButtonA: false);
            var ms = ButtonAMultiSourceRow();

            InputManager.TryEvaluateMappingSetButton(
                first, ms, DevFirst.ToString(), 0, "ButtonA", 50, out bool v);

            Assert.False(v, "no device pressing the source must leave the row off");
        }

        [Fact]
        public void FirstDevicePress_StillFires()
        {
            // Positive control: the evaluated device itself pressing A must
            // still fire (the single-device behaviour is unchanged).
            var first = ArrangeTwoDeviceSlot(firstButtonA: true, secondButtonA: false);
            var ms = ButtonAMultiSourceRow();

            InputManager.TryEvaluateMappingSetButton(
                first, ms, DevFirst.ToString(), 0, "ButtonA", 50, out bool v);

            Assert.True(v, "ButtonA must fire from the evaluated device");
        }
    }
}
