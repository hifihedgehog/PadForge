using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The recording file of #600: controls by stable names, the parts a
    /// file holds, 256 devices, the user's own temp folder, and the zips that
    /// wait for their review.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecordingFormatTwoTests
    {
        // ── Names ──

        [Fact]
        public void EveryControlName_ReadsBackAsItsControl_ForEveryKind()
        {
            var kinds = new[] { RecordingControlKind.Generic, RecordingControlKind.Gamepad, RecordingControlKind.Mouse, RecordingControlKind.Keyboard };
            var fields = new (RecordingField Field, int Count)[]
            {
                (RecordingField.Axis, CustomInputState.MaxAxis), (RecordingField.Slider, CustomInputState.MaxSliders),
                (RecordingField.Hat, CustomInputState.MaxPovs), (RecordingField.Button, CustomInputState.MaxButtons),
            };
            foreach (var kind in kinds)
                foreach (var (field, count) in fields)
                    for (int i = 0; i < count; i++)
                    {
                        string name = RecordingControlNames.Input(kind, field, i);
                        Assert.Matches("^[A-Z][A-Za-z0-9]*$", name);
                        Assert.True(RecordingControlNames.TryInput(name, out var read, out int index), name);
                        Assert.Equal(field, read);
                        Assert.Equal(i, index);
                    }
        }

        [Fact]
        public void TheNames_AreFixedEnglish_AndTheGamepadOnesFollowTheEngine()
        {
            // The order SdlDeviceWrapper.GetGamepadState fills the state in.
            Assert.Equal(new[] { "LeftX", "LeftY", "LeftTrigger", "RightX", "RightY", "RightTrigger" }, RecordingControlNames.GamepadAxes);
            Assert.Equal("A", RecordingControlNames.Input(RecordingControlKind.Gamepad, RecordingField.Button, 0));
            Assert.Equal("Guide", RecordingControlNames.Input(RecordingControlKind.Gamepad, RecordingField.Button, 10));
            Assert.Equal("Touchpad", RecordingControlNames.Input(RecordingControlKind.Gamepad, RecordingField.Button, 16));
            Assert.Equal("DPad", RecordingControlNames.Input(RecordingControlKind.Gamepad, RecordingField.Hat, 0));
            Assert.Equal("Button40", RecordingControlNames.Input(RecordingControlKind.Gamepad, RecordingField.Button, 40));
            Assert.Equal("KeyEnter", RecordingControlNames.Input(RecordingControlKind.Keyboard, RecordingField.Button, 0x0D));
            Assert.Equal("KeyTyped", RecordingControlNames.Input(RecordingControlKind.Keyboard, RecordingField.Button, RecordingCapture.TypedKey));
            Assert.Equal("Vk7", RecordingControlNames.Input(RecordingControlKind.Keyboard, RecordingField.Button, 7));
            Assert.Equal("MouseLeft", RecordingControlNames.Input(RecordingControlKind.Mouse, RecordingField.Button, 0));
            Assert.Equal("Wheel", RecordingControlNames.Input(RecordingControlKind.Mouse, RecordingField.Axis, 2));
            Assert.Equal("Axis7", RecordingControlNames.Input(RecordingControlKind.Generic, RecordingField.Axis, 7));
            Assert.False(RecordingControlNames.TryInput("Axis07", out _, out _));
            Assert.False(RecordingControlNames.TryInput("Axis24", out _, out _));
            Assert.False(RecordingControlNames.TryInput("Nope", out _, out _));
        }

        [Fact]
        public void AnXInputButtonWord_IsWrittenAsItsButtons_AndReadBackExactly()
        {
            for (int word = 0; word <= 0xFFFF; word += 37)
            {
                var sb = new StringBuilder();
                RecordingControlNames.AppendXInputButtons(sb, word);
                Assert.True(RecordingControlNames.TryXInputButtons(sb.ToString(), out int read), sb.ToString());
                Assert.Equal(word, read);
            }
            var a = new StringBuilder();
            RecordingControlNames.AppendXInputButtons(a, Gamepad.A | Gamepad.START | 0x0800);
            Assert.Equal("Start+Touchpad+A", a.ToString());
            Assert.True(RecordingControlNames.TryXInputButtons("Touchpad", out int bit));
            Assert.Equal(0x0800, bit);
            // Every bit has its name, so no writer wrote a bit by number.
            Assert.False(RecordingControlNames.TryXInputButtons("Bit11", out _));
            Assert.True(RecordingControlNames.TryXInputButtons("0x1010", out int old));
            Assert.Equal(Gamepad.A | Gamepad.START, old);
        }

        [Fact]
        public void BothFormats_ReadToTheSameState()
        {
            string Head(string first) => first + "\r\nStatus: finished. Test.\r\n== Devices ==\r\nD1 name=Pad type=Gamepad\r\n== Events ==\r\n";
            string one = Head("PadForge problem recording, format 1") + "0.000000 I D1 full online a0=40000 a2=65535 h0=9000 b0+ b9+ gy=1.5,0,-2 bat=80,1\r\n0.500000 O C1 full type=Xbox360 bt=0x1010 lx=-1200\r\n";
            string two = Head(RecordingCodec.FirstLine) + "0.000000 I D1 full online LeftX=40000 LeftTrigger=65535 DPad=9000 A=1 RightStick=1 Gyro=1.5,0,-2 Battery=80,charging\r\n0.500000 O C1 full type=Xbox360 Buttons=Start+A LeftX=-1200\r\n";
            var a = ReadState(one);
            var b = ReadState(two);
            RecorderTestKit.AssertSameState(a.Input, b.Input);
            RecorderTestKit.AssertSameState(a.Output, b.Output);
            Assert.Equal(40000, b.Input.Axis[0]);
            Assert.True(b.Input.Buttons[9]);
            Assert.True(b.Input.BatteryCharging);
            Assert.Equal(Gamepad.A | Gamepad.START, b.Output.Buttons);
            Assert.Equal(0, b.Unknown);
        }

        private static (CustomInputState Input, RecordedOutputState Output, int Unknown) ReadState(string text)
        {
            using var reader = new RecordingReader(new StringReader(text));
            int unknown = 0;
            while (reader.Read()) unknown += reader.UnknownTokens;
            return (reader.DeviceByNumber(1).State, reader.ControllerByNumber(1).Output, unknown);
        }

        // ── Parts ──

        [Fact]
        public void APartLeftOut_IsNotInTheFile_AndTheFileSaysWhichItHolds()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad("Wireless Controller");
            host.DeviceList.Add(pad);
            host.System.Add("Windows build 26200");
            host.SettingsPart.Add("assign device=D1 controller=C1");
            host.Lines.Add((System.Diagnostics.Stopwatch.GetTimestamp(), "HEARTBEAT before"));
            var recorder = new ProblemRecorder(host)
            {
                FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp(),
                Parts = RecordingParts.All & ~(RecordingParts.Input | RecordingParts.SettingsAndDevices | RecordingParts.DiagnosticsLines),
            };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());
            capture.OutGamepad(0, new Gamepad { ThumbLX = 500 });
            recorder.Mark("here");
            string path = recorder.Stop(wait: true).Path;

            var lines = RecorderTestKit.Lines(path);
            Assert.Equal("Holds: output, feedback, timing, window and errors, activity.", lines[2]);
            var events = RecorderTestKit.Events(path);
            Assert.DoesNotContain(events, e => e.Layer is 'I' or 'L');
            Assert.Contains(events, e => e.Layer == 'O' && e.Payload.Contains("LeftX=500", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Layer == 'M' && e.Payload == "mark here");
            // The settings part says it was left out, the system part keeps
            // only its first line, and the device keeps its shape alone.
            Assert.DoesNotContain("Windows build 26200", lines);
            Assert.Contains(ProblemRecorder.LeftOut, lines);
            string device = lines.Single(l => l.StartsWith("D1 ", StringComparison.Ordinal));
            Assert.DoesNotContain("name=", device);
            Assert.Contains("controls=gamepad", device);
            using var reader = RecordingReader.Open(path);
            Assert.Equal(RecordingParts.All & ~(RecordingParts.Input | RecordingParts.SettingsAndDevices | RecordingParts.DiagnosticsLines), reader.Parts);
        }

        [Fact]
        public void APartLeftOut_CostsThePollThreadNothing()
        {
            var capture = new RecordingCapture(1 << 10) { Parts = RecordingParts.None };
            var pad = RecorderTestKit.Pad();
            var state = RecorderTestKit.Rest();
            state.Axis[0] = 1;
            capture.Input(pad, state);
            capture.OutGamepad(0, new Gamepad { ThumbLX = 1 });
            capture.GameRumble(0, new Vibration(5, 5));
            capture.Stall(100, 100, 100);
            capture.EndPass();
            Assert.Equal(0, capture.Ring.Drain(new RecordingEntry[1 << 10]));
        }

        [Fact]
        public void ThePartWords_ReadBack()
        {
            foreach (var part in RecordingPartWords.Order)
                Assert.Equal(part, RecordingPartWords.ParseHolds(RecordingPartWords.Holds(part) + "."));
            Assert.Equal(RecordingParts.All, RecordingPartWords.ParseHolds(RecordingPartWords.Holds(RecordingParts.All)));
            Assert.Equal("only this header", RecordingPartWords.Holds(RecordingParts.None));
            Assert.Equal(RecordingParts.All, RecordingPartWords.ParseHolds(null));
        }

        [Fact]
        public void TheStartedLine_GivesUtcAndTheLocalZone()
        {
            var at = new DateTimeOffset(2026, 11, 24, 12, 31, 2, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 24, 12, 31, 2)));
            string line = ProblemRecorder.StartedLine(at);
            Assert.StartsWith("Started " + at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC, 12:31:02 local time (", line);
            Assert.Contains(", UTC", line);
        }

        // ── Devices ──

        [Fact]
        public void ACaptureFollows256Devices_AndCountsThe257th()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var capture = new RecordingCapture(1 << 12);
            long made = GC.GetAllocatedBytesForCurrentThread() - before;
            // Every track is made when the capture is, never on the poll
            // thread. 256 of them stay under 8 MB.
            Assert.InRange(made, 1, 8L * 1024 * 1024);
            var drain = new RecordingEntry[1 << 12];
            for (int i = 0; i < 257; i++)
            {
                capture.Input(RecorderTestKit.Pad("Pad " + i), RecorderTestKit.Rest());
                capture.Ring.Drain(drain);
            }
            Assert.Equal(256, capture.DeviceCount);
            Assert.Equal(1, capture.Untracked);
        }

        // ── The folder and the zips that wait ──

        [Fact]
        public void TheRecordingsFolder_IsFenced_AndAStartWithNoFolderIsRefused()
        {
            Assert.Null(RecordingFolder.Get());
            Assert.Null(RecordingFolder.Downloads());
            var host = new NoFolderHost();
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            var result = recorder.Start();
            Assert.False(result.Ok);
            Assert.Equal(RecorderRefusal.File, result.Refusal);
            Assert.Contains("temp folder", result.Detail);
        }

        /// <summary>A host with no temp folder to write into.</summary>
        private sealed class NoFolderHost : RecorderHost
        {
            public override string Folder => null;
            public override bool AttachesToEngine => false;
            public override PrivacyEnvironment Environment() => new() { UserName = RecorderTestHost.User, PcName = RecorderTestHost.Pc };
            public override List<PadForge.Engine.Data.UserDevice> Devices() => new();
            public override void LiveValues(List<(PrivateKind Kind, string Value)> into) { }
            public override void SystemLines(PrivacyScrubber scrubber, List<string> into) => into.Add("PadForge test");
            public override void ControllerLines(PrivacyScrubber scrubber, List<string> into) { }
            public override void SettingsLines(PrivacyScrubber scrubber, List<string> into) { }
            public override List<(long Ticks, string Line)> History() => new();
            public override bool Front(out string path, out bool named, out string profile)
            {
                path = profile = null;
                named = false;
                return false;
            }
        }

        [Fact]
        public void TheZipsThatWait_AreListedNewestFirst_AndOnlyPadForgesOwnAreDeleted()
        {
            using var host = new RecorderTestHost();
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            string first = recorder.Stop(wait: true).Path;
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddHours(-1));
            Assert.True(recorder.Start().Ok);
            string second = recorder.Stop(wait: true).Path;

            // A zip of another program, named as a recording.
            string planted = Path.Combine(host.Dir, ProblemRecorder.FilePrefix + "20260101-000000.zip");
            using (var zip = ZipFile.Open(planted, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("x.log").Open())) w.Write("not a recording");
            string other = Path.Combine(host.Dir, "notes.txt");
            File.WriteAllText(other, "mine");

            var waiting = RecordingStore.Waiting(host.Dir);
            Assert.Equal(new[] { second, first }, waiting.Where(w => w.Path != planted).Select(w => w.Path));
            Assert.False(RecordingStore.Delete(planted));
            Assert.True(File.Exists(planted));
            Assert.True(RecordingStore.Delete(first));
            Assert.False(File.Exists(first));
            Assert.True(File.Exists(other));
        }

        [Fact]
        public void ARecordingThatWaitedSevenDays_IsDeleted_AndNothingElse()
        {
            using var host = new RecorderTestHost();
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            string old = recorder.Stop(wait: true).Path;
            Assert.True(recorder.Start().Ok);
            string fresh = recorder.Stop(wait: true).Path;
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
            string halfMade = old + ".0123456789abcdef0123456789abcdef" + ProblemRecorder.TempSuffix;
            File.WriteAllText(halfMade, "x");
            File.SetLastWriteTimeUtc(halfMade, DateTime.UtcNow.AddDays(-8));
            string other = Path.Combine(host.Dir, ProblemRecorder.FilePrefix + "notes.txt");
            File.WriteAllText(other, "mine");
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-30));

            Assert.Equal(2, RecordingStore.Sweep(host.Dir, DateTime.UtcNow, ProblemRecorder.KeptUnsaved));
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(halfMade));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(other));
            Assert.Equal(TimeSpan.FromDays(7), ProblemRecorder.KeptUnsaved);
        }

        [Fact]
        public void AFinishedZip_NeverWritesOverAFileOfThatName()
        {
            using var host = new RecorderTestHost();
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            var started = recorder.Start();
            Assert.True(started.Ok);
            string zip = ProblemRecorder.ZipPathOf(started.Path);
            // Something put a file where the zip goes while it ran.
            File.WriteAllText(zip, "planted");
            recorder.Stop(wait: true);
            Assert.Equal("planted", File.ReadAllText(zip));
            // The text stays for the next start to finish, and no half-made
            // zip is left beside it.
            Assert.True(File.Exists(started.Path));
            Assert.Empty(Directory.GetFiles(host.Dir, "*" + ProblemRecorder.TempSuffix));
        }
    }
}
