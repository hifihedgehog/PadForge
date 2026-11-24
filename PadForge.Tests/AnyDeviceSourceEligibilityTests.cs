using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
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
        [InlineData(InputDeviceType.Program, false, false)]
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

        /// <summary>A pad SDL opened as its gamepad, the handle a Force Raw
        /// pad keeps, since the flag changes the read and not the open. SDL
        /// opens some wheels, flight sticks and instrument pads through the
        /// same API and types each for what it is
        /// (SdlDeviceWrapper.InputDeviceTypeFor), the class
        /// <see cref="DeviceType"/> stands in for.</summary>
        private sealed class OpenedPad : WebControllerDevice, ISdlInputDevice
        {
            public OpenedPad() : base(Guid.NewGuid().ToString(), "Opened Pad") { }
            public new IntPtr GamepadHandle { get; init; } = new(1);
            public int DeviceType { get; init; } = InputDeviceType.Gamepad;
            public new int GetInputDeviceType() => DeviceType;
        }

        /// <summary>Force Raw Joystick Mode reads the pad's own button
        /// numbers, so "Gamepad A" must not read raw button 0, whatever that
        /// button is on the pad. The numbered "(Any Device)" read still
        /// reaches it. The same pad without the flag answers both, the
        /// control that shows the handle alone made it answer.</summary>
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void ForceRawPadAnswersNoGamepadNamesButStillAnswersTheWildcard(bool forceRaw, bool answersGamepadNames)
        {
            var state = CenteredState();
            state.Buttons[0] = true;
            state.Buttons[1] = true;
            AddDevice(RowGuid, InputDeviceType.Gamepad, state);
            var row = SettingsManager.UserDevices.Items.First(d => d.InstanceGuid == RowGuid);
            row.Device = new OpenedPad();
            row.ForceRawJoystickMode = forceRaw;
            AddRow("ButtonA", Any("Gamepad ButtonA"));
            AddRow("ButtonB", Any("Button 1"));

            var gp = Pass(state, RowGuid);
            Assert.Equal(answersGamepadNames, gp.IsButtonPressed(Gamepad.A));
            Assert.True(gp.IsButtonPressed(Gamepad.B));
        }

        private static readonly MethodInfo BuildExposed = typeof(PadForge.Services.InputService)
            .GetMethod("BuildExposedDevices", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>A row as it reaches a peer over Remote Link: its entry in
        /// the device list the owner's PC builds
        /// (InputService.BuildExposedDevices, the one builder behind the
        /// handshake and every later push), taken through the list's wire
        /// encoding, the entry the peer registers its copy from.</summary>
        private static RemotePeerDeviceInfo Sent(UserDevice owner)
        {
            Assert.NotNull(BuildExposed);
            owner.IsOnline = true;
            SettingsManager.UserDevices.Items.Add(owner);
            try
            {
                var service = new PadForge.Services.InputService(new PadForge.ViewModels.MainViewModel());
                var list = (IReadOnlyList<RemotePeerDeviceInfo>)BuildExposed.Invoke(service, null);
                var received = LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(list, ""));
                var entry = received.Single(d => d.PeerLocalDeviceId == owner.InstanceGuid.ToString("N"));
                entry.PeerFingerprintHex = "0123456789abcdef0123456789abcdef";
                return entry;
            }
            finally { SettingsManager.UserDevices.Items.Remove(owner); }
        }

        /// <summary>The peer's row for a copy, on the slot, holding raw
        /// buttons 0 and 1, with one row on a Gamepad name and one on a
        /// numbered read.</summary>
        private Gamepad PassOfCopy(RemotePeerDevice copy)
            => PassOfRow(copy, copy.GetInputDeviceType());

        /// <summary>One pass of a new row for <paramref name="device"/> on
        /// the slot, holding raw buttons 0 and 1, against one mapping on a
        /// Gamepad name and one on a numbered read.</summary>
        private Gamepad PassOfRow(ISdlInputDevice device, int type, bool forceRaw = false)
        {
            var id = Guid.NewGuid();
            var state = CenteredState();
            state.Buttons[0] = true;
            state.Buttons[1] = true;
            AddDevice(id, type, state);
            var row = SettingsManager.UserDevices.Items.First(d => d.InstanceGuid == id);
            row.Device = device;
            row.ForceRawJoystickMode = forceRaw;
            if (_set.Rows.Count == 0)
            {
                AddRow("ButtonA", Any("Gamepad ButtonA"));
                AddRow("ButtonB", Any("Button 1"));
            }
            return Pass(state, id);
        }

        /// <summary>A Remote Link copy of a pad its owner reads raw arrives
        /// typed a joystick, so on the peer, as on the owner, "Gamepad A"
        /// does not read the raw stream's button 0, while the numbered read
        /// still reaches it. Typed a gamepad, the copy answered the Gamepad
        /// names. The pad without the flag goes out a gamepad and its copy
        /// answers both, the control. The type is the one in the list the
        /// owner sends, so a list that stops asking the rule fails here.</summary>
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void AForceRawPadsRemoteCopyAnswersNoGamepadNames(bool forceRaw, bool answersGamepadNames)
        {
            var sent = Sent(new UserDevice { InstanceGuid = Guid.NewGuid(), Device = new OpenedPad(), ForceRawJoystickMode = forceRaw });
            Assert.Equal(forceRaw ? InputDeviceType.Joystick : InputDeviceType.Gamepad, sent.InputDeviceType);

            using var copy = new RemotePeerDevice(sent);
            var gp = PassOfCopy(copy);
            Assert.Equal(answersGamepadNames, gp.IsButtonPressed(Gamepad.A));
            Assert.True(gp.IsButtonPressed(Gamepad.B));
        }

        /// <summary>The flag retypes only a device that would go out as a
        /// gamepad. SDL opens some wheels, flight sticks and instrument pads
        /// through its gamepad API and types each for what it is. One of
        /// those goes out under its own type, read raw or not, the type the
        /// owner's Devices page lists it under. Read raw, its copy answers
        /// no Gamepad name under its own type, so a retype fixed no read:
        /// it listed a wheel read raw as a joystick on the peer and gave it
        /// another product GUID, which is hashed from the type.</summary>
        [Theory]
        [InlineData(InputDeviceType.Driving)]
        [InlineData(InputDeviceType.Flight)]
        [InlineData(InputDeviceType.Supplemental)]
        public void AWheelStickOrInstrumentReadRawGoesOutUnderItsOwnType(int type)
        {
            UserDevice Owner(bool forceRaw) => new()
            {
                InstanceGuid = Guid.NewGuid(), ForceRawJoystickMode = forceRaw, Device = new OpenedPad { DeviceType = type },
            };
            var plain = Sent(Owner(forceRaw: false));
            var raw = Sent(Owner(forceRaw: true));
            Assert.Equal(type, plain.InputDeviceType);
            Assert.Equal(type, raw.InputDeviceType);

            using var plainCopy = new RemotePeerDevice(plain);
            using var rawCopy = new RemotePeerDevice(raw);
            Assert.Equal(plainCopy.ProductGuid, rawCopy.ProductGuid);

            // No Gamepad name reads the raw copy, and the numbered read
            // reaches it.
            var gp = PassOfCopy(rawCopy);
            Assert.False(gp.IsButtonPressed(Gamepad.A));
            Assert.True(gp.IsButtonPressed(Gamepad.B));
        }

        /// <summary>SDL opens some wheels, flight sticks, arcade sticks and
        /// instrument pads through its gamepad API. One of them answers the
        /// "(Any Device)" Gamepad names on its owner's PC, where its state
        /// is in SDL's layout, and its Remote Link copy answered none: a
        /// copy has no gamepad handle, and its type is not a gamepad's. The
        /// list now carries the layout apart from the type, so the copy
        /// answers where the device does. Read raw by its owner, neither
        /// answers.</summary>
        [Theory]
        [InlineData(InputDeviceType.Driving)]
        [InlineData(InputDeviceType.Flight)]
        [InlineData(InputDeviceType.Joystick)]
        [InlineData(InputDeviceType.Supplemental)]
        public void ADeviceSdlOpenedAsItsGamepadAnswersTheGamepadNamesThroughItsCopyToo(int type)
        {
            foreach (bool forceRaw in new[] { false, true })
            {
                var onOwner = PassOfRow(new OpenedPad { DeviceType = type }, type, forceRaw);
                Assert.Equal(!forceRaw, onOwner.IsButtonPressed(Gamepad.A));
                Assert.True(onOwner.IsButtonPressed(Gamepad.B));

                var sent = Sent(new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), ForceRawJoystickMode = forceRaw, Device = new OpenedPad { DeviceType = type },
                });
                Assert.Equal(type, sent.InputDeviceType);
                using var copy = new RemotePeerDevice(sent);
                var onPeer = PassOfCopy(copy);
                Assert.Equal(!forceRaw, onPeer.IsButtonPressed(Gamepad.A));
                Assert.True(onPeer.IsButtonPressed(Gamepad.B));
            }
        }

        /// <summary>The other way round. A device typed a gamepad whose
        /// owner reads it outside SDL's layout answers no Gamepad name on
        /// the owner's PC, and its copy, a gamepad by type, answered them:
        /// a web pad with Force Raw Joystick Mode on, and a joystick SDL
        /// types a gamepad and has no mapping for, which the owner reads by
        /// its own button numbers. With the layout in the list the copy
        /// answers none either. The web pad with the flag off answers on
        /// both, the control.</summary>
        [Fact]
        public void AGamepadItsOwnerReadsOutsideSdlsLayoutAnswersNoGamepadNameThroughItsCopy()
        {
            foreach (bool forceRaw in new[] { false, true })
            {
                var web = new OpenedPad { GamepadHandle = IntPtr.Zero };
                Assert.Equal(!forceRaw, PassOfRow(web, InputDeviceType.Gamepad, forceRaw).IsButtonPressed(Gamepad.A));
                var sent = Sent(new UserDevice { InstanceGuid = Guid.NewGuid(), ForceRawJoystickMode = forceRaw, Device = web });
                Assert.Equal(InputDeviceType.Gamepad, sent.InputDeviceType);
                using var copy = new RemotePeerDevice(sent);
                var onPeer = PassOfCopy(copy);
                Assert.Equal(!forceRaw, onPeer.IsButtonPressed(Gamepad.A));
                Assert.True(onPeer.IsButtonPressed(Gamepad.B));
            }

            using var unmapped = new SdlDeviceWrapper();
            typeof(SdlDeviceWrapper).GetProperty(nameof(SdlDeviceWrapper.JoystickType))
                .SetValue(unmapped, SDL3.SDL.SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD);
            Assert.Equal(InputDeviceType.Gamepad, unmapped.GetInputDeviceType());
            Assert.Equal(IntPtr.Zero, unmapped.GamepadHandle);
            Assert.False(PassOfRow(unmapped, InputDeviceType.Gamepad).IsButtonPressed(Gamepad.A));
            var raw = Sent(new UserDevice { InstanceGuid = Guid.NewGuid(), Device = unmapped });
            Assert.Equal(InputDeviceType.Gamepad, raw.InputDeviceType);
            using var rawCopy = new RemotePeerDevice(raw);
            var gp = PassOfCopy(rawCopy);
            Assert.False(gp.IsButtonPressed(Gamepad.A));
            Assert.True(gp.IsButtonPressed(Gamepad.B));
        }

        private static RemotePeerDeviceInfo Entry(string id, int type, bool? layout) => new()
        {
            PeerLocalDeviceId = id, Name = "Pad", NumAxes = 6, NumButtons = 22,
            InputDeviceType = type, GamepadLayout = layout, HasNfcReader = true, TouchpadClickSupported = true,
        };

        /// <summary>The layout rides one bit of a byte the list already
        /// carries for each device, set only where the layout is not what
        /// the type says. A build from before the bit reads that byte's
        /// other seven and nothing else of it, so the list it receives is
        /// the list it received before, and a device its type describes
        /// costs no bit at all. A list with no such byte, an older sender's
        /// or a list cut to its records, leaves the layout to the
        /// type.</summary>
        [Fact]
        public void TheLayoutCrossesTheLinkInOneBitAnOlderBuildNeverReads()
        {
            RemotePeerDeviceInfo[] List(bool? wheel) => new[]
            {
                Entry("wheel", InputDeviceType.Driving, wheel),
                Entry("pad", InputDeviceType.Gamepad, true),
                Entry("rawpad", InputDeviceType.Gamepad, false),
                Entry("stick", InputDeviceType.Joystick, false),
                Entry("unsaid", InputDeviceType.Driving, null),
            };
            byte[] with = LinkConnection.EncodeDeviceList(List(true), "");
            byte[] without = LinkConnection.EncodeDeviceList(List(null), "");
            Assert.Equal(without.Length, with.Length);
            var changed = Enumerable.Range(0, with.Length).Where(i => with[i] != without[i]).ToArray();
            int at = Assert.Single(changed);
            Assert.Equal(0x80, with[at] ^ without[at]);

            var read = LinkConnection.DecodeDeviceList(with);
            Assert.Equal(new bool?[] { true, null, false, null, null }, read.Select(d => d.GamepadLayout));
            // The bits the byte already carried arrive as they did.
            Assert.All(read, d => Assert.True(d.HasNfcReader && d.TouchpadClickSupported == true));
            Assert.Equal(new[] { InputDeviceType.Driving, InputDeviceType.Gamepad, InputDeviceType.Gamepad,
                InputDeviceType.Joystick, InputDeviceType.Driving }, read.Select(d => d.InputDeviceType));

            Assert.All(LinkConnection.DecodeDeviceList(LinkConnection.EncodeBasicDeviceList(List(true))),
                d => Assert.Null(d.GamepadLayout));

            // A list cut inside that tail, after the wheel's byte, costs the
            // layout with the tail's other flags, the wheel's included.
            var cut = LinkConnection.DecodeDeviceList(with.Take(at + 1).ToArray());
            Assert.Equal(5, cut.Count);
            Assert.All(cut, d => Assert.True(d.GamepadLayout == null && !d.HasNfcReader));
        }

        /// <summary>A copy the list says nothing about answers by its type,
        /// as every copy did before the bit: an older owner's gamepad copy
        /// answers the Gamepad names and its wheel copy does not. And the
        /// peer keeps a layout that changes on the row it already has, with
        /// the other facts its owner refreshes.</summary>
        [Fact]
        public void ACopyTheListSaysNothingAboutAnswersByItsType()
        {
            foreach (var (type, answers) in new[] { (InputDeviceType.Gamepad, true), (InputDeviceType.Driving, false) })
            {
                var sent = LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(new[] { Entry("d", type, null) }, "")).Single();
                sent.PeerFingerprintHex = "0123456789abcdef0123456789abcdef";
                Assert.Null(sent.GamepadLayout);
                using var copy = new RemotePeerDevice(sent);
                Assert.Equal(answers, PassOfCopy(copy).IsButtonPressed(Gamepad.A));

                // The answer follows the entry the copy holds, live.
                sent.GamepadLayout = !answers;
                Assert.Equal(!answers, PassOfCopy(copy).IsButtonPressed(Gamepad.A));
            }

            string server = System.IO.File.ReadAllText(RepoPath("PadForge.Engine", "RemoteLink", "LinkServer.cs"));
            int reconcile = server.IndexOf("private void ReconcileRemoteDevices(", StringComparison.Ordinal);
            int end = server.IndexOf("private const string ListTooLarge", reconcile, StringComparison.Ordinal);
            Assert.True(reconcile >= 0 && end > reconcile, "the reconcile moved");
            Assert.Contains("existing.Info.GamepadLayout = info.GamepadLayout;", server[reconcile..end]);
        }

        private static string RepoPath(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }

        /// <summary>The flag changes the state a device reports only where
        /// SDL opened it as its gamepad (SdlDeviceWrapper.GetCurrentState).
        /// A gamepad with no such handle, a web pad here, sends the same
        /// stream with the flag on, so the flag leaves its type alone.</summary>
        [Fact]
        public void AGamepadWithNoGamepadHandleGoesOutAGamepadWithTheFlagOn()
        {
            var sent = Sent(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), ForceRawJoystickMode = true, Device = new OpenedPad { GamepadHandle = IntPtr.Zero },
            });
            Assert.Equal(InputDeviceType.Gamepad, sent.InputDeviceType);
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
