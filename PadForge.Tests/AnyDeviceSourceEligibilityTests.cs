using System;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #431: a Head Tracker (OpenTrack) row assigned beside a
    /// gamepad held the slot's "(Any Device)" trigger rows at half pull and
    /// steered its stick rows. An empty DeviceGuid resolves to the device
    /// being evaluated, and the tracker publishes six centered pose axes
    /// through the same numbered Axis array the gamepad layout reads, so
    /// the tracker's pass read Roll as Left Trigger and Step 4's max kept
    /// it. The rows that publish their own vocabulary through that array
    /// no longer answer the wildcard. Named sources on them still read.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AnyDeviceSourceEligibilityTests : IDisposable
    {
        private const int Slot = 2;
        private static readonly Guid PadGuid = new("a4310001-0000-4000-8000-000000000001");
        private static readonly Guid TrackerGuid = new("a4310002-0000-4000-8000-000000000002");
        private static readonly Guid RowGuid = new("a4310003-0000-4000-8000-000000000003");

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();

        private readonly CustomInputState _padState = RestState();
        private readonly CustomInputState _trackerState = CenteredState();
        private readonly MappingSet _set = new();

        public AnyDeviceSourceEligibilityTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            Array.Clear(SettingsManager.SlotEnabled);
            SettingsManager.SlotCreated[Slot] = true;
            SettingsManager.SlotEnabled[Slot] = true;
            SettingsManager.SlotMappingSets[Slot] = _set;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            AddDevice(PadGuid, InputDeviceType.Gamepad, _padState);
            AddDevice(TrackerGuid, InputDeviceType.HeadTracker, _trackerState);
        }

        public void Dispose()
        {
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
        }

        /// <summary>A gamepad at rest: sticks centered, triggers released.</summary>
        private static CustomInputState RestState()
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            Array.Fill(state.Povs, -1);
            return state;
        }

        /// <summary>What a Head Tracker row publishes while it waits for a
        /// tracker: every pose axis centered, the state in the report.</summary>
        private static CustomInputState CenteredState()
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            Array.Fill(state.Povs, -1);
            return state;
        }

        private static void AddDevice(Guid id, int capType, CustomInputState state)
        {
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = true,
                ProductName = id.ToString(), InstanceName = id.ToString(),
                CapType = capType, InputState = state,
            });
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        private static MappingSource Any(string descriptor)
            => new() { DeviceGuid = "", Descriptor = descriptor };

        private static MappingSource Named(Guid id, string descriptor)
            => new() { DeviceGuid = id.ToString(), Descriptor = descriptor };

        private void AddRow(string target, params MappingSource[] sources)
            => _set.Rows.Add(new MappingRow { Target = target, LayerMask = "Base", Sources = sources.ToList() });

        private static readonly MethodInfo BeginFrame = typeof(InputManager)
            .GetMethod("BeginFrameMultiSourceTracking", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Apply = typeof(InputManager)
            .GetMethod("ApplyMappingSetToGamepad", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>One Step 3 pass for one device, the way UpdateOutputStates
        /// runs it: the slot's set evaluated against that device's state.</summary>
        private Gamepad Pass(CustomInputState state, Guid device)
        {
            Assert.NotNull(BeginFrame);
            Assert.NotNull(Apply);
            BeginFrame.Invoke(null, null);
            object[] args = { state, _set, device.ToString(), 50, Slot, new Gamepad() };
            Apply.Invoke(null, args);
            return (Gamepad)args[5];
        }

        [Fact]
        public void HeadTrackerRowRestsCenteredOnEverySourceAxis()
        {
            using var tracker = new HeadTrackerDevice(false, 0, false, 0, () => 1000, configureFirewall: _ => { });
            tracker.AttachForTest();
            var state = tracker.GetCurrentState();
            for (int i = 0; i < 6; i++) Assert.Equal(32768, state.Axis[i]);
        }

        [Fact]
        public void TrackerPassLeavesAnyDeviceGamepadRowsAtRestWhileNamedRowsRead()
        {
            AddRow("LeftTrigger", Any("Gamepad LeftTrigger"));
            AddRow("RightTrigger", Any("Gamepad RightTrigger"));
            AddRow("LeftThumbAxisX", Any("Gamepad LeftStickX"));
            AddRow("LeftThumbAxisY", Any("Gamepad LeftStickY"));
            AddRow("ButtonA", Any("Gamepad ButtonA"));
            AddRow("RightThumbAxisX", Named(TrackerGuid, "Axis 0"));
            _trackerState.Axis[0] = 65535; // yaw hard right while tracking
            _padState.Axis[2] = 65535;     // the pad's own left trigger fully pulled
            _padState.Axis[0] = 65535;     // the pad's left stick hard right

            var tracker = Pass(_trackerState, TrackerGuid);
            Assert.Equal((ushort)0, tracker.LeftTrigger);
            Assert.Equal((ushort)0, tracker.RightTrigger);
            Assert.Equal((short)0, tracker.ThumbLX);
            Assert.Equal((short)0, tracker.ThumbLY);
            Assert.False(tracker.IsButtonPressed(Gamepad.A));
            Assert.Equal((short)32767, tracker.ThumbRX); // the named tracker row still reads

            var pad = Pass(_padState, PadGuid);
            Assert.Equal((ushort)65535, pad.LeftTrigger);
            Assert.Equal((short)32767, pad.ThumbLX);
        }

        [Fact]
        public void MultiSourceAnyDeviceRowSpansOnlyRowsThatAnswerTheWildcard()
        {
            _set.Rows.Add(new MappingRow
            {
                Target = "LeftTrigger", LayerMask = "Base",
                Sources = new[] { Any("Gamepad LeftTrigger"), Any("Gamepad RightTrigger") }.ToList(),
            });
            // Pad triggers released, tracker centered: only Roll and Z
            // could show up, and they must not.
            Assert.Equal((ushort)0, Pass(_trackerState, TrackerGuid).LeftTrigger);
            Assert.Equal((ushort)0, Pass(_padState, PadGuid).LeftTrigger);
            _padState.Axis[5] = 65535;
            Assert.Equal((ushort)65535, Pass(_padState, PadGuid).LeftTrigger);
            Assert.Equal((ushort)65535, Pass(_trackerState, TrackerGuid).LeftTrigger);
        }

        [Fact]
        public void PublicEvaluatorsReadRestForRowsThatDoNotAnswerTheWildcard()
        {
            AddRow("ButtonA", Any("Gamepad ButtonA"));
            AddRow("LeftThumbAxisX", Any("Gamepad LeftStickX"));
            AddRow("LeftTrigger", Any("Gamepad LeftTrigger"));
            _trackerState.Buttons[0] = true;
            _trackerState.Axis[0] = 65535;
            string tracker = TrackerGuid.ToString();

            Assert.True(InputManager.TryEvaluateMappingSetButton(_trackerState, _set, tracker, Slot, "ButtonA", 50, out bool a));
            Assert.False(a);
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(_trackerState, _set, tracker, Slot, "LeftThumbAxisX", out short x));
            Assert.Equal((short)0, x);
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(_trackerState, _set, tracker, Slot, "LeftTrigger", out short lt));
            Assert.Equal(short.MinValue, lt);

            _padState.Buttons[0] = true;
            string pad = PadGuid.ToString();
            Assert.True(InputManager.TryEvaluateMappingSetButton(_padState, _set, pad, Slot, "ButtonA", 50, out bool padA));
            Assert.True(padA);
        }

        /// <summary>The Gamepad names follow SDL's rule: a role exists only on
        /// a device read in SDL's gamepad layout, so a keyboard's first key no
        /// longer presses A and a joystick read raw answers none of them. A
        /// numbered "(Any Device)" read is no Gamepad name, and every row that
        /// answers the wildcard still answers it.</summary>
        [Theory]
        [InlineData(InputDeviceType.HeadTracker, false, false)]
        [InlineData(InputDeviceType.Nfc, false, false)]
        [InlineData(InputDeviceType.Microphone, false, false)]
        [InlineData(InputDeviceType.HandheldButtons, false, false)]
        [InlineData(InputDeviceType.ConsumerControl, false, false)]
        [InlineData(InputDeviceType.Tablet, false, false)]
        [InlineData(InputDeviceType.Gamepad, true, true)]
        [InlineData(InputDeviceType.Joystick, true, false)]
        [InlineData(InputDeviceType.Supplemental, true, false)]
        [InlineData(InputDeviceType.Keyboard, true, false)]
        [InlineData(InputDeviceType.Mouse, true, false)]
        [InlineData(InputDeviceType.Touchpad, true, false)]
        public void OnlyRowsThatSpeakTheGamepadLayoutAnswerTheGamepadNames(int capType, bool answersWildcard,
            bool answersGamepadNames)
        {
            var state = CenteredState();
            state.Buttons[0] = true;
            state.Buttons[1] = true;
            AddDevice(RowGuid, capType, state);
            AddRow("ButtonA", Any("Gamepad ButtonA"));
            AddRow("LeftTrigger", Any("Gamepad LeftTrigger"));
            AddRow("ButtonB", Any("Button 1"));

            var gp = Pass(state, RowGuid);
            Assert.Equal(answersGamepadNames, gp.IsButtonPressed(Gamepad.A));
            Assert.Equal(answersGamepadNames ? (ushort)32768 : (ushort)0, gp.LeftTrigger);
            Assert.Equal(answersWildcard, gp.IsButtonPressed(Gamepad.B));
            Assert.Equal(answersWildcard, InputDeviceType.AnswersAnyDeviceSources(capType));
        }

        [Fact]
        public void AnyDeviceActivatorIgnoresPassesOfRowsThatDoNotAnswerIt()
        {
            var nfc = CenteredState();
            nfc.Buttons[0] = true; // "Any NFC Tag" is Button 0
            AddDevice(RowGuid, InputDeviceType.Nfc, nfc);
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Gamepad ButtonA", Mode = "Toggle", LayerMask = "Tag",
            });
            _set.Rows.Add(new MappingRow
            {
                Target = "ButtonB", LayerMask = "Tag",
                Sources = new[] { Named(PadGuid, "Button 1") }.ToList(),
            });
            _padState.Buttons[1] = true;

            // The NFC pass must not toggle the layer through its Button 0,
            // so the pad's pass stays on Base and B stays released.
            Pass(nfc, RowGuid);
            Assert.False(Pass(_padState, PadGuid).IsButtonPressed(Gamepad.B));

            // The pad's own A press toggles the layer and B follows.
            _padState.Buttons[0] = true;
            Pass(_padState, PadGuid);
            _padState.Buttons[0] = false;
            Assert.True(Pass(_padState, PadGuid).IsButtonPressed(Gamepad.B));
        }
    }
}
