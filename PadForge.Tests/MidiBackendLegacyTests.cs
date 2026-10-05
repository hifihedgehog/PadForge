using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The legacy WinMM backend, over a stand-in for winmm.dll that records
    /// every call. The sequences under test follow RtMidi (MidiInWinMM,
    /// MidiOutWinMM), PortMidi (pmwinmm.c) and NAudio (MidiIn, MidiOut):
    /// output opens with no callback and closes without midiOutReset, input
    /// opens with a function callback, starts, and closes with stop, reset,
    /// close.
    /// </summary>
    [Collection("MidiBackendStatics")]
    public class MidiBackendLegacyTests : IDisposable
    {
        public MidiBackendLegacyTests()
        {
            MidiEndpointJanitor.SweepDisabledForTest = true;
            MidiInputRuntime.Shutdown();
        }

        public void Dispose()
        {
            MidiInputRuntime.Shutdown();
            MidiVirtualController.UseBackendFactoryForTest(null);
            MidiEndpointJanitor.SweepDisabledForTest = false;
        }

        internal sealed class FakeWinMm : IWinMmMidi
        {
            public List<string> Inputs = new();
            public List<string> Outputs = new();
            public int OpenResult;
            public int StartResult;
            public int SendResult;
            public readonly List<string> Calls = new();
            public readonly List<uint> Sent = new();
            public WinMmMidi.MidiInProc Callback;
            private int _nextHandle = 0x100;

            public int InputCount() => Inputs.Count;
            public int OutputCount() => Outputs.Count;
            public string InputName(int id) => Inputs[id];
            public string OutputName(int id) => Outputs[id];

            // Closes run on worker threads, so the log is locked.
            private void Log(string call) { lock (Calls) Calls.Add(call); }
            public bool Has(string call) { lock (Calls) return Calls.Contains(call); }

            public int OpenOutput(int id, out IntPtr handle)
            {
                Log($"OpenOutput {id}");
                handle = OpenResult == 0 ? (IntPtr)_nextHandle++ : IntPtr.Zero;
                return OpenResult;
            }

            public int SendShort(IntPtr handle, uint message)
            {
                Log($"Send {handle}");
                lock (Calls) Sent.Add(message);
                return SendResult;
            }

            public int CloseOutput(IntPtr handle) { Log($"CloseOutput {handle}"); return 0; }

            public int OpenInput(int id, WinMmMidi.MidiInProc callback, out IntPtr handle)
            {
                Log($"OpenInput {id}");
                Callback = callback;
                handle = OpenResult == 0 ? (IntPtr)_nextHandle++ : IntPtr.Zero;
                return OpenResult;
            }

            public int StartInput(IntPtr handle) { Log("StartInput"); return StartResult; }
            public int StopInput(IntPtr handle) { Log("StopInput"); return 0; }
            public int ResetInput(IntPtr handle) { Log("ResetInput"); return 0; }
            public int CloseInput(IntPtr handle) { Log("CloseInput"); return 0; }

            /// <summary>What the driver's thread does when a short message arrives.</summary>
            public void Deliver(uint packed, uint wMsg = WinMmMidi.MIM_DATA)
                => Callback(IntPtr.Zero, wMsg, IntPtr.Zero, (IntPtr)packed, IntPtr.Zero);
        }

        // ── Port names ──

        [Fact]
        public void Ports_AreKeyedByName_WithTheSecondOfTwoNumbered()
        {
            var keys = MidiBackendLegacy.NamePorts(new[] { "USB MIDI", "Synth", "USB MIDI", "USB MIDI" });
            Assert.Equal(new[] { "USB MIDI", "Synth", "USB MIDI (2)", "USB MIDI (3)" }, keys.Select(k => k.Key));
            Assert.Equal(new[] { 0, 1, 2, 3 }, keys.Select(k => k.Id));
        }

        /// <summary>A port Windows cannot describe gets no key, and a real
        /// name that collides with a generated one is numbered in turn, so
        /// every key names exactly one port.</summary>
        [Fact]
        public void Ports_WithNoName_AreSkipped_AndNoTwoKeysCollide()
        {
            var keys = MidiBackendLegacy.NamePorts(new[] { "Foo", null, "  ", "Foo", "Foo (2)" });
            Assert.Equal(new[] { "Foo", "Foo (2)", "Foo (2) (2)" }, keys.Select(k => k.Key));
            Assert.Equal(new[] { 0, 3, 4 }, keys.Select(k => k.Id));
        }

        // ── Messages ──

        /// <summary>Status byte low, then the two data bytes, as
        /// midiOutShortMsg takes them (RtMidi MidiOutWinMM::sendMessage).</summary>
        [Fact]
        public void AShortMessage_PacksStatusLow_ThenTheDataBytes()
        {
            Assert.Equal(0x005A3C92u, MidiBackendLegacy.PackShortMessage(Midi1Status.NoteOn, 2, 60, 90));
            Assert.Equal(0x006407B0u, MidiBackendLegacy.PackShortMessage(Midi1Status.ControlChange, 0, 7, 100));
            Assert.Equal(0x00003C82u, MidiBackendLegacy.PackShortMessage(Midi1Status.NoteOff, 2, 60, 0));
            // Data bytes keep 7 bits and the channel 4, so nothing spills
            // into the status byte.
            Assert.Equal(0x007F7FBFu, MidiBackendLegacy.PackShortMessage(Midi1Status.ControlChange, 0x1F, 0xFF, 0xFF));
        }

        /// <summary>The words the Windows MIDI Services loopback delivered
        /// for the same three messages in the 2026-10-05 hands-on run
        /// (0x20B20764, 0x20923C5A, 0x20823C00).</summary>
        [Fact]
        public void AReceivedMessage_BecomesTheSameUmpWordWindowsMidiServicesDelivers()
        {
            Assert.Equal(0x20B20764u, MidiBackendLegacy.ShortMessageToUmp(0x006407B2));
            Assert.Equal(0x20923C5Au, MidiBackendLegacy.ShortMessageToUmp(0x005A3C92));
            Assert.Equal(0x20823C00u, MidiBackendLegacy.ShortMessageToUmp(0x00003C82));
            Assert.Equal(0x20E07F00u, MidiBackendLegacy.ShortMessageToUmp(0x00007FE0));
        }

        [Fact]
        public void ADataByteWhereTheStatusBelongs_OrASystemMessage_IsDropped()
        {
            Assert.Equal(0u, MidiBackendLegacy.ShortMessageToUmp(0x0000643C));
            Assert.Equal(0u, MidiBackendLegacy.ShortMessageToUmp(0x000000F8));
            Assert.Equal(0u, MidiBackendLegacy.ShortMessageToUmp(0x000000FE));
            Assert.Equal(0u, MidiBackendLegacy.ShortMessageToUmp(0x00000000));
        }

        /// <summary>Program change and channel pressure carry one data
        /// byte, so whatever sits in the third byte is not passed on.</summary>
        [Fact]
        public void AOneDataByteMessage_ClearsTheSecondDataByte()
        {
            Assert.Equal(0x20C00500u, MidiBackendLegacy.ShortMessageToUmp(0x007705C0));
            Assert.Equal(0x20D04000u, MidiBackendLegacy.ShortMessageToUmp(0x003340D0));
        }

        [Fact]
        public void TheWordsAMidiInputDeviceGets_DecodeToTheSameState()
        {
            var dev = new MidiInputDevice(MidiBackendLegacy.EndpointPrefix + "Keys", "Keys");
            dev.OnUmp(MidiBackendLegacy.ShortMessageToUmp(0x006407B2), 0);
            dev.OnUmp(MidiBackendLegacy.ShortMessageToUmp(0x005A3C92), 0);
            var s = dev.GetCurrentState().Midi;
            Assert.Equal(100, s.Cc[7]);
            Assert.True(s.Notes[60]);
            dev.OnUmp(MidiBackendLegacy.ShortMessageToUmp(0x00003C82), 0);
            Assert.False(dev.GetCurrentState().Midi.Notes[60]);
        }

        // ── Output ──

        [Fact]
        public void NoPortPicked_FailsTheSlotWithTheReason()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            var ex = Assert.Throws<InvalidOperationException>(() => new MidiBackendLegacy(fake).OpenOutputPort(""));
            Assert.Contains("Pick an output port", ex.Message);
            Assert.Empty(fake.Calls);
        }

        [Fact]
        public void APortThatIsGone_FailsTheSlotWithTheReason()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            // Its own type: Step 5 retries a slot that failed this way once a
            // port listing shows the port again.
            var ex = Assert.Throws<MidiPortNotConnectedException>(() => new MidiBackendLegacy(fake).OpenOutputPort("loopMIDI Port"));
            Assert.Contains("is not connected", ex.Message);
            Assert.Empty(fake.Calls);
        }

        [Fact]
        public void APortAnotherProgramHolds_FailsTheSlotWithTheReason()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" }, OpenResult = WinMmMidi.MMSYSERR_ALLOCATED };
            var ex = Assert.Throws<InvalidOperationException>(() => new MidiBackendLegacy(fake).OpenOutputPort("Synth"));
            Assert.Contains("in use by another program", ex.Message);
        }

        /// <summary>The port is found by name at each open, so a device
        /// that came or went and shifted the indexes still gets the right
        /// port. Sends go to the handle that open returned, and close never
        /// resets the port: the interface does not even carry
        /// midiOutReset.</summary>
        [Fact]
        public void APort_OpensByName_SendsToItsHandle_AndClosesOnce()
        {
            var fake = new FakeWinMm { Outputs = { "New Device", "Synth", "Synth" } };
            var ep = new MidiBackendLegacy(fake).OpenOutputPort("Synth (2)");
            Assert.Equal("OpenOutput 2", fake.Calls[0]);

            ep.Send(Midi1Status.ControlChange, 0, 7, 100);
            Assert.Equal(new[] { 0x006407B0u }, fake.Sent);

            ep.DisconnectConnection();
            ep.CloseSession();
            ep.CloseSession();
            Assert.Equal(1, fake.Calls.Count(c => c.StartsWith("CloseOutput", StringComparison.Ordinal)));

            // A send after the close goes nowhere.
            ep.Send(Midi1Status.NoteOn, 0, 60, 127);
            Assert.Single(fake.Sent);
        }

        [Fact]
        public void AFailedSend_Throws_SoTheControllerDropsIt()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" }, SendResult = 5 };
            var ep = new MidiBackendLegacy(fake).OpenOutputPort("Synth");
            Assert.Throws<InvalidOperationException>(() => ep.Send(Midi1Status.NoteOn, 0, 60, 127));
        }

        [Fact]
        public void TheLegacyApi_NeverCreatesAPort()
        {
            Assert.Throws<NotSupportedException>(() =>
                new MidiBackendLegacy(new FakeWinMm()).CreateVirtualEndpoint("PadForge MIDI 1", "PADFORGE_MIDI_1_X", 0));
        }

        // ── Input ──

        [Fact]
        public void InputPorts_AreListedUnderTheirOwnPrefix()
        {
            var fake = new FakeWinMm { Inputs = { "Keys", "Keys" } };
            var list = new MidiBackendLegacy(fake).EnumerateNormalEndpoints();
            Assert.Equal(new[] { ("winmm:Keys", "Keys"), ("winmm:Keys (2)", "Keys (2)") }, list);
        }

        [Fact]
        public void AnInputPort_Opens_Starts_Delivers_AndClosesStopResetClose()
        {
            var fake = new FakeWinMm { Inputs = { "Other", "Keys" } };
            var words = new List<uint>();
            using var session = new MidiBackendLegacy(fake).CreateInputSession("test");
            Assert.Null(session.CreateConnection("MIDIU_APPPUB_SOMETHING", (a, b) => words.Add(a)));

            var conn = session.CreateConnection("winmm:Keys", (a, b) => words.Add(a));
            Assert.True(conn.Open());
            Assert.Equal(new[] { "OpenInput 1", "StartInput" }, fake.Calls);

            fake.Deliver(0x005A3C92);
            fake.Deliver(0x005A3C92, wMsg: 0x3C1); // MIM_OPEN carries no message
            Assert.Equal(new[] { 0x20923C5Au }, words);

            conn.Detach();
            fake.Deliver(0x006407B2);
            Assert.Single(words);

            fake.Calls.Clear();
            conn.Disconnect();
            conn.Disconnect();
            Assert.Equal(new[] { "StopInput", "ResetInput", "CloseInput" }, fake.Calls);
        }

        /// <summary>PortMidi's failed-start path: reset, then close.</summary>
        [Fact]
        public void AnInputThatWillNotStart_IsResetAndClosed()
        {
            var fake = new FakeWinMm { Inputs = { "Keys" }, StartResult = 7 };
            using var session = new MidiBackendLegacy(fake).CreateInputSession("test");
            var conn = session.CreateConnection("winmm:Keys", (a, b) => { });
            Assert.False(conn.Open());
            Assert.Equal(new[] { "OpenInput 0", "StartInput", "ResetInput", "CloseInput" }, fake.Calls);
        }

        [Fact]
        public void DisposingTheSession_ClosesEveryOpenPort_AndRefusesNewOnes()
        {
            var fake = new FakeWinMm { Inputs = { "Keys", "Pads" } };
            var session = new MidiBackendLegacy(fake).CreateInputSession("test");
            var a = session.CreateConnection("winmm:Keys", (x, y) => { });
            var b = session.CreateConnection("winmm:Pads", (x, y) => { });
            Assert.True(a.Open());
            Assert.True(b.Open());
            fake.Calls.Clear();

            session.Dispose();
            Assert.Equal(2, fake.Calls.Count(c => c == "CloseInput"));

            fake.Calls.Clear();
            var late = session.CreateConnection("winmm:Keys", (x, y) => { });
            Assert.False(late.Open());
            Assert.Contains("CloseInput", fake.Calls);
        }

        // ── The probe and the controller ──

        private sealed class RefusingBackend : IMidiBackend
        {
            public int Stops;
            public MidiApiKind Kind => MidiApiKind.AppSdk;
            public bool Start() => false;
            public void Stop(bool skipDispose) => Stops++;
            public IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex) => throw new InvalidOperationException();
            public IMidiInputSession CreateInputSession(string name) => null;
            public List<(string Id, string Name)> EnumerateNormalEndpoints() => new();
        }

        /// <summary>Legacy API mode makes EnsureServiceAvailable return
        /// false. The probe releases that API and takes the legacy one.</summary>
        [Fact]
        public void AWindowsMidiServicesApiThatWillNotStart_FallsBackToTheLegacyApi()
        {
            var refusing = new RefusingBackend();
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            MidiVirtualController.UseBackendFactoryForTest(() => refusing, () => new MidiBackendLegacy(fake));

            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
            Assert.False(MidiVirtualController.ProbeFailed);
            Assert.Equal(1, refusing.Stops);
        }

        [Fact]
        public void NoWindowsMidiServicesApi_FallsBackToTheLegacyApi()
        {
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(new FakeWinMm()));
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
        }

        [Fact]
        public void ALegacyMidiSlot_OpensItsPickedPort_AndRecordsTheApi()
        {
            var fake = new FakeWinMm { Outputs = { "Synth", "loopMIDI Port" } };
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
            Assert.True(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(0, 0, 1) { OutputPort = "loopMIDI Port", CcNumbers = new[] { 1 }, NoteNumbers = new[] { 60 } };
            vc.Connect();
            Assert.True(vc.IsConnected);
            Assert.Equal(MidiApiKind.Legacy, vc.ApiKind);
            Assert.Equal("OpenOutput 1", fake.Calls[0]);

            vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 10 }, Notes = new[] { true } });
            Assert.Equal(new[] { 0x000A01B0u, 0x007F3C90u }, fake.Sent);

            // Disconnect sends a note off for the held note, then closes.
            vc.Dispose();
            Assert.Equal(0x00003C80u, fake.Sent.Last());
            Assert.Contains(fake.Calls, c => c.StartsWith("CloseOutput", StringComparison.Ordinal));
        }

        /// <summary>A channel edit on a running legacy slot keeps its port
        /// open. The held note ends on the old channel, and the next submit
        /// presses it on the new one.</summary>
        [Fact]
        public void ALegacyMidiSlot_TakesAChannelEdit_OnTheSamePort()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
            Assert.True(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(0, 0, 1) { OutputPort = "Synth" };
            vc.ApplyLayout(channel: 0, startCc: 1, ccCount: 0, startNote: 60, noteCount: 1, velocity: 127);
            vc.Connect();
            var held = new MidiRawState { CcValues = Array.Empty<byte>(), Notes = new[] { true } };
            vc.SubmitMidiRawState(held);

            vc.ApplyLayout(channel: 9, startCc: 1, ccCount: 0, startNote: 60, noteCount: 1, velocity: 127);
            vc.SubmitMidiRawState(held);

            // Status byte low: note on and off on channel 1, then note on on channel 10.
            Assert.Equal(new[] { 0x007F3C90u, 0x00003C80u, 0x007F3C99u }, fake.Sent);
            Assert.Equal(1, fake.Calls.Count(c => c.StartsWith("OpenOutput", StringComparison.Ordinal)));
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("CloseOutput", StringComparison.Ordinal));
            vc.Dispose();
        }

        [Fact]
        public void ALegacyMidiSlotWithNoPort_FailsToConnect()
        {
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(new FakeWinMm { Outputs = { "Synth" } }));
            Assert.True(MidiVirtualController.IsAvailable());
            var vc = new MidiVirtualController(0, 0, 1);
            var ex = Assert.Throws<InvalidOperationException>(() => vc.Connect());
            Assert.Contains("Pick an output port", ex.Message);
            Assert.False(vc.IsConnected);
        }

        /// <summary>The legacy sweep closes a port whose slot assignment
        /// went and opens it again when one comes. A close returns the
        /// state to rest, so a held note does not survive into the next
        /// open.</summary>
        [Fact]
        public void AnInputDevice_ClosesToRest_AndOpensAgain()
        {
            var fake = new FakeWinMm { Inputs = { "Keys" } };
            MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));

            var dev = new MidiInputDevice("winmm:Keys", "Keys");
            Assert.Equal(MidiInputDevice.InstanceGuidFor("winmm:Keys"), dev.InstanceGuid);
            Assert.False(dev.IsOpen);
            Assert.True(dev.Open());
            Assert.True(dev.IsOpen);
            fake.Deliver(0x005A3C92);
            Assert.True(dev.GetCurrentState().Midi.Notes[60]);

            dev.Close();
            Assert.False(dev.IsOpen);
            Assert.False(dev.GetCurrentState().Midi.Notes[60]);
            Assert.True(SpinWaitFor(() => fake.Has("CloseInput")));

            Assert.True(dev.Open());
            Assert.True(dev.IsOpen);
            dev.Dispose();
        }

        private static bool SpinWaitFor(Func<bool> condition)
            => System.Threading.SpinWait.SpinUntil(condition, 3_000);

        // ── A port that comes and goes (Step 5) ──

        private const int WatchPad = 2;
        private static readonly Guid WatchDevice = new("3c6f1b2a-8d4e-4f51-9a7b-2e0d5c6b7a81");

        /// <summary>Runs <paramref name="body"/> against an InputManager with
        /// one created, enabled MIDI slot whose device is online and whose
        /// bar names <paramref name="port"/>, on the legacy API over
        /// <paramref name="fake"/>. This collection runs with nothing beside
        /// it, so the settings statics are borrowed and put back.</summary>
        private static void WithLegacyMidiSlot(FakeWinMm fake, string port, Action<InputManager> body)
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            var savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            InputManager im = null;
            try
            {
                MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
                Assert.True(MidiVirtualController.IsAvailable());

                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
                SettingsManager.SlotCreated[WatchPad] = true;
                var ud = new UserDevice { InstanceGuid = WatchDevice, ProductName = "Port Watch Pad", IsOnline = true, InputState = new CustomInputState() };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = WatchDevice, MapTo = WatchPad });

                im = new InputManager { LegacyPortListingIntervalMs = 0 };
                im.SlotControllerTypes[WatchPad] = VirtualControllerType.Midi;
                im._midiConfigs[WatchPad] = new MidiSlotConfig { OutputPort = port };
                body(im);
            }
            finally
            {
                if (im != null)
                {
                    WaitForWork(im);
                    Slots(im)[WatchPad]?.Dispose();
                }
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
                Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
                Array.Copy(savedEnabled, SettingsManager.SlotEnabled, savedEnabled.Length);
            }
        }

        private static T PrivateField<T>(InputManager im, string name)
            => (T)typeof(InputManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);

        private static IVirtualController[] Slots(InputManager im) => PrivateField<IVirtualController[]>(im, "_virtualControllers");

        private static bool Failed(InputManager im) => PrivateField<bool[]>(im, "_createFailed")[WatchPad];

        private static bool Running(InputManager im) => Slots(im)[WatchPad] is MidiVirtualController { IsConnected: true };

        /// <summary>Waits for every worker Step 5 starts for the slot: its
        /// create or dispose, and the port listing.</summary>
        private static void WaitForWork(InputManager im)
        {
            Assert.True(System.Threading.SpinWait.SpinUntil(() =>
            {
                var connect = PrivateField<System.Threading.Tasks.Task[]>(im, "_pendingConnectTask")[WatchPad];
                var dispose = PrivateField<System.Threading.Tasks.Task[]>(im, "_pendingDisposeTask")[WatchPad];
                return (connect == null || connect.IsCompleted)
                    && (dispose == null || dispose.IsCompleted)
                    && !PrivateField<bool>(im, "_legacyPortListingRunning");
            }, 10_000), "a Step 5 worker did not finish");
        }

        private static void Step(InputManager im)
        {
            typeof(InputManager).GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(im, null);
            WaitForWork(im);
        }

        private static bool StepUntil(InputManager im, Func<bool> done)
        {
            for (int i = 0; i < 20 && !done(); i++) Step(im);
            return done();
        }

        /// <summary>A slot whose port is missing fails, then starts once the
        /// port appears, as when a synth is switched on after PadForge. The
        /// port has to show missing first, so a port another program holds
        /// does not retry in a loop.</summary>
        [Fact]
        public void ALegacySlot_StartsOnceItsPortAppears()
        {
            var fake = new FakeWinMm();
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Failed(im)));
                for (int i = 0; i < 3; i++) Step(im);
                Assert.True(Failed(im));
                Assert.Null(Slots(im)[WatchPad]);

                fake.Outputs.Add("Synth");
                Assert.True(StepUntil(im, () => Running(im)));
                Assert.False(Failed(im));
                Assert.True(fake.Has("OpenOutput 0"));
            });
        }

        /// <summary>A port that was there all along but would not open stays
        /// failed until the slot is reconfigured, with no retry loop.</summary>
        [Fact]
        public void ALegacySlotWhosePortIsHeld_DoesNotRetry()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" }, OpenResult = WinMmMidi.MMSYSERR_ALLOCATED };
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Failed(im)));
                for (int i = 0; i < 5; i++) Step(im);
                Assert.True(Failed(im));
                lock (fake.Calls) Assert.Single(fake.Calls, c => c.StartsWith("OpenOutput", StringComparison.Ordinal));
            });
        }

        /// <summary>A running slot whose port goes away closes it and fails,
        /// then reopens it by name once it is back, as with a replugged
        /// interface.</summary>
        [Fact]
        public void ALegacySlot_ReopensItsPortAfterAReplug()
        {
            var fake = new FakeWinMm { Outputs = { "Synth" } };
            WithLegacyMidiSlot(fake, "Synth", im =>
            {
                Assert.True(StepUntil(im, () => Running(im)));
                var first = Slots(im)[WatchPad];
                for (int i = 0; i < 3; i++) Step(im);
                Assert.Same(first, Slots(im)[WatchPad]);

                fake.Outputs.Remove("Synth");
                Assert.True(StepUntil(im, () => Failed(im)));
                Assert.False(first.IsConnected);
                lock (fake.Calls) Assert.Contains(fake.Calls, c => c.StartsWith("CloseOutput", StringComparison.Ordinal));

                fake.Outputs.Add("Synth");
                Assert.True(StepUntil(im, () => Running(im)));
                Assert.NotSame(first, Slots(im)[WatchPad]);
            });
        }

        // ── The saved port ──

        [Fact]
        public void TheSavedPort_IsLeftOutOfTheFile_UntilOneIsPicked()
        {
            var serializer = new XmlSerializer(typeof(MidiSlotConfigData));
            string Xml(MidiSlotConfigData d)
            {
                using var w = new StringWriter();
                serializer.Serialize(w, d);
                return w.ToString();
            }

            Assert.DoesNotContain("OutputPort", Xml(new MidiSlotConfigData { OutputPort = "" }));
            string xml = Xml(new MidiSlotConfigData { OutputPort = "loopMIDI Port" });
            Assert.Contains("OutputPort=\"loopMIDI Port\"", xml);
            var back = (MidiSlotConfigData)serializer.Deserialize(new StringReader(xml));
            Assert.Equal("loopMIDI Port", back.OutputPort);
        }

        /// <summary>A profile saved before a port was picked keeps the
        /// slot's current port. One that has a port applies it.</summary>
        [Fact]
        public void ASnapshotWithNoPort_KeepsTheSlotsPort()
        {
            var config = new MidiSlotConfig { OutputPort = "Synth" };
            new MidiSlotConfigData().ApplyOutputPortTo(config);
            Assert.Equal("Synth", config.OutputPort);
            new MidiSlotConfigData { OutputPort = "loopMIDI Port" }.ApplyOutputPortTo(config);
            Assert.Equal("loopMIDI Port", config.OutputPort);
        }

        [Fact]
        public void ResettingTheSlot_ClearsThePort()
        {
            var config = new MidiSlotConfig { OutputPort = "Synth" };
            config.ResetToDefaults();
            Assert.Equal(string.Empty, config.OutputPort);
            config.OutputPort = null;
            Assert.Equal(string.Empty, config.OutputPort);
        }
    }

    /// <summary>The runtime install's checks, off the network.</summary>
    public class MidiRuntimeInstallTests
    {
        /// <summary>Only Microsoft's bundle takes tools Microsoft no
        /// longer offers with it, so only its uninstall asks first.</summary>
        [Theory]
        [InlineData((int)DriverInstaller.MidiRuntimeOwner.MicrosoftBundle, true)]
        [InlineData((int)DriverInstaller.MidiRuntimeOwner.MicrosoftPackage, false)]
        [InlineData((int)DriverInstaller.MidiRuntimeOwner.PadForgePackage, false)]
        [InlineData((int)DriverInstaller.MidiRuntimeOwner.None, false)]
        public void OnlyMicrosoftsBundle_AsksBeforeItsUninstall(int owner, bool asks)
        {
            Assert.Equal(asks, DriverInstaller.MidiUninstallIsIrreversible((DriverInstaller.MidiRuntimeOwner)owner));
        }

        [Theory]
        [InlineData(14, 51, true)]
        [InlineData(14, 52, true)]
        [InlineData(15, 0, true)]
        [InlineData(14, 50, false)]
        [InlineData(14, 44, false)]
        [InlineData(13, 99, false)]
        public void TheVisualCppRuntime_MustBeAtLeastTheLinkersVersion(int major, int minor, bool expected)
        {
            Assert.Equal(expected, DriverInstaller.VcVersionAtLeast(major, minor, DriverInstaller.MidiRuntimeVcMinimum));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(3010, true)]
        [InlineData(1641, true)]
        [InlineData(1638, true)]
        [InlineData(1603, false)]
        [InlineData(1602, false)]
        public void TheRedistributablesExitCode_SaysWhetherTheRuntimeIsThere(int exitCode, bool expected)
        {
            Assert.Equal(expected, DriverInstaller.IsVcRedistSuccess(exitCode));
        }

        [Fact]
        public void TheHash_IsSha256InHex()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "abc");
                Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", DriverInstaller.Sha256Hex(path));
            }
            finally { File.Delete(path); }
        }

        /// <summary>The positive control is a DLL Microsoft signs inside the
        /// file. A copy with one byte changed must fail: the check verifies
        /// the signature over the content, not only who signed it.</summary>
        [Fact]
        public void OnlyAnIntactMicrosoftSignature_Passes()
        {
            string signed = Path.Combine(Environment.SystemDirectory, "msvcp140.dll");
            Assert.True(DriverInstaller.IsSignedByMicrosoft(signed));

            string copy = Path.Combine(Path.GetTempPath(), $"pf-sigtest-{Guid.NewGuid():N}.dll");
            try
            {
                byte[] bytes = File.ReadAllBytes(signed);
                bytes[bytes.Length / 3] ^= 0xFF;
                File.WriteAllBytes(copy, bytes);
                Assert.False(DriverInstaller.IsSignedByMicrosoft(copy));

                File.WriteAllText(copy, "not a signed file");
                Assert.False(DriverInstaller.IsSignedByMicrosoft(copy));
            }
            finally { File.Delete(copy); }
        }

        /// <summary>The download URLs and hashes name the published
        /// assets.</summary>
        [Fact]
        public void TheRuntimeAssets_ArePinned()
        {
            Assert.StartsWith("https://github.com/hifihedgehog/PadForge-MIDI-Runtime/releases/download/", DriverInstaller.MidiRuntimeReleaseUrl);
            Assert.EndsWith("-x64.msi", DriverInstaller.MidiRuntimeMsiX64);
            Assert.EndsWith("-arm64.msi", DriverInstaller.MidiRuntimeMsiArm64);
            Assert.Matches("^[0-9a-f]{64}$", DriverInstaller.MidiRuntimeSha256X64);
            Assert.Matches("^[0-9a-f]{64}$", DriverInstaller.MidiRuntimeSha256Arm64);
            Assert.NotEqual(DriverInstaller.MidiRuntimeSha256X64, DriverInstaller.MidiRuntimeSha256Arm64);
        }
    }
}
