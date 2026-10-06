using System;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every on/off input reads as an Up or Down key of an Incremental or Ramp
    /// source, as an Invert on Hold modifier, and as a macro trigger: MIDI
    /// notes, CCs and encoder detents, and IR Offscreen among them. The key
    /// reader had no branch for either family, so both read released forever,
    /// and the Record button refused MIDI for a key on that account.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class OnOffKeyTests
    {
        private const int Slot = 3;

        private static CustomInputState MidiState()
            => new() { Midi = new MidiInputState() };

        private static MappingSource Incremental(string up, string device) => new()
        {
            Kind = "Incremental", DeviceGuid = device, ParamUp = up, ParamDown = "",
            ParamRate = 1.0, ParamMin = 0, ParamMax = 1, ParamSticky = true,
        };

        private static double Tick(SourceKindRuntime rt, MappingSource src, CustomInputState state)
        {
            rt.FrameSeq++;
            return rt.TickIncremental(Slot, "LeftTrigger", 0, src, state, 0.05);
        }

        // ── The key reader ──

        private static void Hold(CustomInputState state, string key, bool held)
        {
            switch (key)
            {
                case "Midi Note 60": state.Midi.Notes[60] = held; break;
                case "Midi CC 7 Up": state.Midi.CcUp[7] = held; break;
                default: state.Midi.CcDown[7] = held; break;
            }
        }

        /// <summary>The key reads its own input and no other: with every
        /// other note and detent held it stays released.</summary>
        [Theory]
        [InlineData("Midi Note 60")]
        [InlineData("Midi CC 7 Up")]
        [InlineData("Midi CC 7 Down")]
        public void AMidiNoteOrEncoderDetentStepsTheValue(string key)
        {
            var rt = new SourceKindRuntime();
            var src = Incremental(key, Guid.NewGuid().ToString());
            var state = MidiState();
            Array.Fill(state.Midi.Notes, true);
            Array.Fill(state.Midi.CcUp, true);
            Array.Fill(state.Midi.CcDown, true);
            Hold(state, key, false);

            Assert.Equal(0.0, Tick(rt, src, state), 6);
            Hold(state, key, true);
            Assert.Equal(0.05, Tick(rt, src, state), 6);
            Assert.Equal(0.1, Tick(rt, src, state), 6);
        }

        /// <summary>A CC reads as held from 64, where MIDI's on/off
        /// controllers turn on: a sustain pedal sends 0 up and 127 down.</summary>
        [Fact]
        public void ACcKeyIsHeldFrom64()
        {
            var rt = new SourceKindRuntime();
            var src = Incremental("Midi CC 64", Guid.NewGuid().ToString());
            var state = MidiState();

            state.Midi.Cc[64] = 63;
            Assert.Equal(0.0, Tick(rt, src, state), 6);
            state.Midi.Cc[64] = 64;
            Assert.Equal(0.05, Tick(rt, src, state), 6);
            state.Midi.Cc[64] = 127;
            Assert.Equal(0.1, Tick(rt, src, state), 6);
            state.Midi.Cc[64] = 0;
            Assert.Equal(0.1, Tick(rt, src, state), 6);
        }

        /// <summary>The key read of a CC agrees with a Button row's default
        /// read at every value.</summary>
        [Fact]
        public void ACcKeyReadsAsAButtonRowDoes()
        {
            var state = MidiState();
            var row = new MappingSource { Kind = "Direct", Descriptor = "Midi CC 11" };
            for (int v = 0; v <= 127; v++)
            {
                state.Midi.Cc[11] = (byte)v;
                Assert.Equal(SourceCoercion.EvaluateForButtonTarget(state, row, 50),
                    SourceCoercion.ReadHardwareBoolDescriptor(state, "Midi CC 11"));
            }
            Assert.Equal(64, SourceCoercion.MidiCcOnValue);
        }

        [Fact]
        public void PitchBendIsNoKey()
        {
            var rt = new SourceKindRuntime();
            var src = Incremental("Midi Pitch Bend", Guid.NewGuid().ToString());
            var state = MidiState();
            state.Midi.PitchBend = 65535;
            Assert.Equal(0.0, Tick(rt, src, state), 6);
            state.Midi.PitchBend = 0;
            Assert.Equal(0.0, Tick(rt, src, state), 6);
            Assert.False(SourceKindRuntime.ReadsAsKey("Midi Pitch Bend"));
        }

        /// <summary>A device with no MIDI state reads a MIDI key as
        /// released.</summary>
        [Fact]
        public void AMidiKeyOnAPadWithoutMidiReadsReleased()
        {
            var rt = new SourceKindRuntime();
            var src = Incremental("Midi Note 60", Guid.NewGuid().ToString());
            Assert.Equal(0.0, Tick(rt, src, new CustomInputState()), 6);
        }

        /// <summary>IR Offscreen drives a Ramp through the debounced read a
        /// Button row takes: held while the remote has lost the screen, and
        /// not again until it has been gone for 150 ms.</summary>
        [Fact]
        public void AnIrOffscreenKeyDrivesTheRamp()
        {
            var rt = new SourceKindRuntime();
            var src = new MappingSource
            {
                Kind = "Ramped", DeviceGuid = Guid.NewGuid().ToString(),
                ParamUp = "IR Offscreen", ParamDown = "",
                ParamAttackTime = 0.5, ParamReleaseTime = 0.5, ParamAutocenter = true,
            };
            var state = new CustomInputState { Ir = new WiiIrState { Detected = false } };

            // Never on the screen: offscreen at once.
            rt.FrameSeq++;
            Assert.Equal(0.1, rt.TickRamped(Slot, "LeftThumbAxisX", 0, src, state, 0.05), 6);

            // On the screen: the key lets go and the axis eases back.
            state.Ir = new WiiIrState { Detected = true };
            rt.FrameSeq++;
            Assert.Equal(0.0, rt.TickRamped(Slot, "LeftThumbAxisX", 0, src, state, 0.05), 6);

            // Lost again a moment later: the debounce holds the key released.
            state.Ir = new WiiIrState { Detected = false };
            rt.FrameSeq++;
            Assert.Equal(0.0, rt.TickRamped(Slot, "LeftThumbAxisX", 0, src, state, 0.05), 6);
        }

        // ── The Invert on Hold modifier's mirror ──

        [Fact]
        public void AMidiModifierInvertsTheButton()
        {
            var src = new MappingSource
            {
                Kind = "InvertOnHold", Descriptor = "Button 0", DeviceGuid = Guid.NewGuid().ToString(),
                ParamModifier = "Midi Note 60",
            };
            var state = MidiState();
            state.Buttons[0] = true;

            Assert.True(SourceEvaluator.EvaluateForButtonTarget(state, src, 50, Slot, "ButtonA", 0, null, 0));
            state.Midi.Notes[60] = true;
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(state, src, 50, Slot, "ButtonA", 0, null, 0));
        }

        [Fact]
        public void AnIrOffscreenModifierInvertsTheButton()
        {
            var src = new MappingSource
            {
                Kind = "InvertOnHold", Descriptor = "Button 0", DeviceGuid = Guid.NewGuid().ToString(),
                ParamModifier = "IR Offscreen",
            };
            var state = new CustomInputState { Ir = new WiiIrState { Detected = false } };
            state.Buttons[0] = true;

            // Never on the screen: offscreen, so the held button reads
            // inverted.
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(state, src, 50, Slot, "ButtonA", 0, null, 0));
            state.Ir = new WiiIrState { Detected = true };
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(state, src, 50, Slot, "ButtonA", 0, null, 0));
        }

        // ── Macro triggers ──

        [Theory]
        [InlineData("Midi Note 60")]
        [InlineData("Midi CC 64")]
        [InlineData("Midi CC 7 Up")]
        [InlineData("Midi CC 7 Down")]
        [InlineData("IR Offscreen")]
        public void AnOnOffInputConvertsToAMacroTrigger(string descriptor)
        {
            string device = Guid.NewGuid().ToString();
            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = descriptor, DeviceGuid = device }, out var entry));
            Assert.Equal(descriptor, entry.SourceDescriptor);
            Assert.Equal(Guid.Parse(device), entry.DeviceGuid);
            Assert.True(entry.RawButton < 0);
        }

        [Fact]
        public void PitchBendIsNoMacroTrigger()
            => Assert.False(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Midi Pitch Bend", DeviceGuid = Guid.NewGuid().ToString() }, out _));

        /// <summary>A MIDI trigger fires through the read the macro engine
        /// takes of a descriptor entry (CheckDescriptorTrigger): the button
        /// read at the entry's threshold, 64 for a CC unless one is
        /// stamped.</summary>
        [Fact]
        public void AMidiTriggerFiresWhenItsInputIsHeld()
        {
            string device = Guid.NewGuid().ToString();
            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Midi CC 64", DeviceGuid = device }, out var entry));
            var state = MidiState();
            state.Midi.Cc[64] = 63;
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, entry.DescriptorSource, 50, Slot, device));
            state.Midi.Cc[64] = 127;
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, entry.DescriptorSource, 50, Slot, device));
        }

        // ── The Record button ──

        private sealed class MidiSession : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly MethodInfo _tick = typeof(RecorderService).GetMethod("PollTick", BindingFlags.Instance | BindingFlags.NonPublic);
            public MainViewModel ViewModel { get; } = new();
            public RecorderService Recorder { get; }
            public UserDevice Device { get; }
            public PadViewModel Pad => ViewModel.Pads[0];

            public MidiSession()
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var state = new CustomInputState { Midi = new MidiInputState() };
                Array.Fill(state.Axis, 32768);
                Device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), ProductName = "MIDI Keys", IsOnline = true,
                    CapType = InputDeviceType.Midi, InputState = state,
                };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(Device);
                Pad.OutputType = VirtualControllerType.PlayStation;
                Pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = Device.InstanceGuid, Name = "MIDI Keys", IsOnline = true });
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

        private static (MidiSession Session, MappingSourceItem Source) RecordUp()
        {
            var session = new MidiSession();
            var row = session.Row("LeftTrigger");
            var source = new MappingSourceItem { Kind = "Incremental" };
            row.ExtraSources.Add(source);
            session.Recorder.StartRecordingExtraSourceParam(row, source, 0, RecorderService.ParamTarget.Up);
            Assert.True(session.Recorder.IsRecording);
            return (session, source);
        }

        [Theory]
        [InlineData("note", "Midi Note 60")]
        [InlineData("detent", "Midi CC 7 Up")]
        [InlineData("pedal", "Midi CC 64")]
        public void RecordTakesAMidiKey(string press, string expected)
        {
            var (session, source) = RecordUp();
            using (session)
            {
                var midi = session.Device.InputState.Midi;
                switch (press)
                {
                    case "note": midi.Notes[60] = true; break;
                    case "detent": midi.CcUp[7] = true; break;
                    default: midi.Cc[64] = 127; break;
                }
                session.Tick();
                Assert.False(session.Recorder.IsRecording);
                Assert.Equal(expected, source.ParamUp);
            }
        }

        /// <summary>A key never records pitch bend or a stick, and a
        /// mapping row still records pitch bend.</summary>
        [Fact]
        public void RecordRefusesPitchBendAndAxesForAKey()
        {
            var (session, source) = RecordUp();
            using (session)
            {
                session.Device.InputState.Midi.PitchBend = 65535;
                session.Device.InputState.Axis[0] = 0;
                for (int i = 0; i < 5; i++) session.Tick();
                Assert.True(session.Recorder.IsRecording);
                Assert.True(string.IsNullOrEmpty(source.ParamUp), $"the key took '{source.ParamUp}'");
            }

            using var rowSession = new MidiSession();
            var row = rowSession.Row("LeftThumbAxisX");
            rowSession.Recorder.StartRecording(row, 0, rowSession.Device.InstanceGuid);
            rowSession.Device.InputState.Midi.PitchBend = 65535;
            rowSession.Tick();
            Assert.False(rowSession.Recorder.IsRecording);
            Assert.Equal("Midi Pitch Bend", row.SourceDescriptor);
        }
    }
}
