using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
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
            var ex = Assert.Throws<InvalidOperationException>(() => new MidiBackendLegacy(fake).OpenOutputPort("loopMIDI Port"));
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
