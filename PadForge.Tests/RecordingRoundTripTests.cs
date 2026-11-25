using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Recording;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A problem recording's file (#597), written by the recorder and read
    /// back by <see cref="RecordingReader"/>: every field of a device's state
    /// comes back to the bit, only what changed is written, a full frame
    /// stands every five seconds, the hash on the last line shows a cut, and
    /// a recording stops by itself at its length and at its size.
    /// </summary>
    public class RecordingRoundTripTests
    {
        // ── A state with something in every field ──

        private static int[] Ints(int[] a, int seed) { for (int i = 0; i < a.Length; i++) a[i] = seed * 31 + i * 7 - 40; return a; }
        private static float[] Floats(float[] a, int seed) { for (int i = 0; i < a.Length; i++) a[i] = (seed + 1) * 0.37f - i * 1.13f; return a; }
        private static bool[] Bools(bool[] a, int seed) { for (int i = 0; i < a.Length; i++) a[i] = (i + seed) % 3 == 0; return a; }
        private static byte[] Bytes(byte[] a, int seed) { for (int i = 0; i < a.Length; i++) a[i] = (byte)((seed * 5 + i * 3) % 128); return a; }

        /// <summary>Fills every public field by reflection, so a field added
        /// to the state and not to the recorder fails the round trip the day
        /// it lands.</summary>
        private static void Populate(object target, int seed)
        {
            foreach (var field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = field.GetValue(target);
                var type = field.FieldType;
                if (type == typeof(int)) field.SetValue(target, seed * 13 + 5);
                else if (type == typeof(float)) field.SetValue(target, seed * 0.61f + 0.125f);
                else if (type == typeof(bool)) field.SetValue(target, seed % 2 == 0);
                else if (type == typeof(int[])) Ints((int[])value, seed);
                else if (type == typeof(float[])) Floats((float[])value, seed);
                else if (type == typeof(bool[])) Bools((bool[])value, seed);
                else if (type == typeof(byte[])) Bytes((byte[])value, seed);
                else if (type == typeof(WiiIrState))
                    field.SetValue(target, new WiiIrState { X = seed * 0.01f - 0.5f, Y = 0.25f - seed * 0.02f, Detected = seed % 2 == 1, Calibrated = seed % 3 == 0 });
                else if (type == typeof(TouchpadInputState[]))
                {
                    foreach (var pad in (TouchpadInputState[])value)
                    {
                        if (pad == null) continue;
                        int fingers = pad.MaxFingers;
                        Populate(pad, seed + 3);
                        pad.MaxFingers = fingers;
                    }
                }
                else if (type == typeof(MidiInputState)) Populate(value, seed + 1);
                else if (type == typeof(AnalogKeyInputState))
                {
                    var keys = (AnalogKeyInputState)value;
                    keys.ResetForReuse();
                    keys.Set(4 + seed % 20, 0.25f + seed % 5 * 0.1f);
                    keys.Set(40, 1f);
                    if (seed % 2 == 0) keys.Set(0x6A0, 0.5f);
                }
                else Assert.Fail($"Populate does not know field type {type.Name} ({field.Name}). Teach it, and the recorder, the new field.");
            }
        }

        private static CustomInputState Full(int seed)
        {
            var state = new CustomInputState
            {
                // A touchpad a device lists and has not made stands between two it has.
                Touchpads = new[] { new TouchpadInputState(2), null, new TouchpadInputState(5) },
                CapSense = new bool[4],
                NfcTag = new bool[6],
                Midi = new MidiInputState(),
                AnalogKeys = new AnalogKeyInputState(),
            };
            Populate(state, seed);
            return state;
        }

        [Fact]
        public void EveryFieldOfADevicesState_ComesBackToTheBit()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;

            var expected = new List<CustomInputState>();
            for (int seed = 1; seed <= 60; seed++)
            {
                var state = Full(seed);
                capture.Input(pad, state);
                expected.Add(state.Clone());
                capture.Advance(0.001);
            }
            // Odd values a float can hold.
            var odd = Full(61);
            odd.Gyro[0] = float.NaN;
            odd.Gyro[1] = float.NegativeInfinity;
            odd.Gyro[2] = -0f;
            odd.Accel[0] = float.Epsilon;
            odd.Accel[1] = float.MaxValue;
            odd.Accel[2] = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC12345));
            capture.Input(pad, odd);
            expected.Add(odd.Clone());

            string path = recorder.Stop(wait: true).Path;
            var frames = RecorderTestKit.PassFrames(path, 1);
            Assert.Equal(expected.Count, frames.Count);
            for (int i = 0; i < expected.Count; i++)
                RecorderTestKit.AssertSameState(expected[i], frames[i].State, "pass " + i);
            Assert.True(frames[0].Full, "a device's first line is a full frame");
        }

        [Fact]
        public void AShapeThatChangesWhileRecording_IsFollowed()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;

            var expected = new List<CustomInputState>();
            void Pass(CustomInputState state)
            {
                capture.Input(pad, state);
                expected.Add(state.Clone());
                capture.Advance(0.001);
            }
            var plain = RecorderTestKit.Rest();
            Pass(plain);
            // An NFC reader arms, a touchpad appears, a MIDI part comes and goes.
            var armed = RecorderTestKit.Rest();
            armed.NfcTag = new bool[3];
            armed.NfcTag[2] = true;
            armed.Touchpads = new[] { new TouchpadInputState(2) };
            armed.Touchpads[0].FingerDown[1] = true;
            armed.Touchpads[0].FingerContactId[1] = 7;
            armed.Touchpads[0].FingerX[1] = 0.25f;
            armed.Midi = new MidiInputState();
            armed.Midi.Notes[60] = true;
            Pass(armed);
            var grown = armed.Clone();
            grown.NfcTag = new bool[5];
            grown.NfcTag[2] = true;
            grown.NfcTag[4] = true;
            grown.Midi = null;
            Pass(grown);
            Pass(plain);

            string path = recorder.Stop(wait: true).Path;
            var frames = RecorderTestKit.PassFrames(path, 1);
            Assert.Equal(expected.Count, frames.Count);
            for (int i = 0; i < expected.Count; i++)
                RecorderTestKit.AssertSameState(expected[i], frames[i].State, "pass " + i);
        }

        // ── Only what changed, and a full frame every five seconds ──

        [Fact]
        public void OnlyWhatChangedIsWritten_AndAFullFrameStandsEveryFiveSeconds()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;

            var state = RecorderTestKit.Rest();
            capture.Input(pad, state);
            capture.Advance(0.5);
            state.Axis[0] = 40000;
            capture.Input(pad, state);
            capture.Advance(0.5);
            // A pass with nothing new writes nothing.
            capture.Input(pad, state);
            capture.Advance(4.3);
            state.Buttons[3] = true;
            capture.Input(pad, state);
            // Twelve seconds with no report: the frames still come, as the
            // recorder's thread goes round.
            recorder.WaitForFlush();
            capture.Advance(12);
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            state.Buttons[3] = false;
            capture.Input(pad, state);

            string path = recorder.Stop(wait: true).Path;
            var input = RecorderTestKit.Events(path).Where(e => e.Layer == 'I' && e.Subject == "D1").ToList();
            var full = input.Where(e => e.Payload.StartsWith("full", StringComparison.Ordinal)).ToList();
            var changes = input.Where(e => !e.Payload.StartsWith("full", StringComparison.Ordinal)).ToList();

            Assert.Equal(new[] { "a0=40000", "b3+", "b3-" }, changes.Select(e => e.Payload));
            Assert.Equal("full online a0=32768 a1=32768 a2=32768 a3=32768", full[0].Payload);
            Assert.Equal(4, full.Count);
            for (int i = 1; i < full.Count; i++)
                Assert.Equal(RecordingCodec.KeyframeSeconds, full[i].Seconds - full[i - 1].Seconds, 6);
            // The frame due after the axis moved says so, and the one after
            // the button went down has it down.
            Assert.Equal("full online a0=40000 a1=32768 a2=32768 a3=32768", full[1].Payload);
            Assert.Equal("full online a0=40000 a1=32768 a2=32768 a3=32768 b3+", full[2].Payload);
            // Lines stand in the order of their times.
            Assert.Equal(input.OrderBy(e => e.Seconds).Select(e => e.Payload), input.Select(e => e.Payload));
        }

        [Fact]
        public void AFullFrame_SaysWhatTheLinesBeforeItAddUpTo()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;

            var random = new Random(597);
            var state = Full(3);
            for (int pass = 0; pass < 400; pass++)
            {
                state.Axis[random.Next(6)] = random.Next(65536);
                state.Buttons[random.Next(20)] = random.Next(2) == 0;
                state.Gyro[random.Next(3)] = (float)random.NextDouble();
                state.Touchpads[0].FingerX[random.Next(2)] = (float)random.NextDouble();
                state.MouseRawDX = pass % 9 == 0 ? random.Next(-9, 10) : 0;
                capture.Input(pad, state);
                capture.Advance(0.05);
            }
            string path = recorder.Stop(wait: true).Path;

            // Each full frame, read alone, against the state the lines up to
            // it add up to. The counts of one pass belong to their own line.
            var shadow = new CustomInputState();
            RecordingCodec.Reset(shadow);
            int checkedFrames = 0;
            foreach (var e in RecorderTestKit.Events(path).Where(e => e.Layer == 'I' && e.Subject == "D1"))
            {
                bool full = e.Payload.StartsWith("full", StringComparison.Ordinal);
                var into = shadow;
                if (full)
                {
                    into = new CustomInputState();
                    RecordingCodec.Reset(into);
                }
                foreach (string token in e.Payload.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token is "full" or "online") continue;
                    Assert.True(RecordingCodec.TryParseToken(token, out var field, out int index, out int a, out int b, out int c), token);
                    RecordingCodec.Apply(into, field, index, a, b, c);
                }
                if (!full || checkedFrames++ == 0) { if (full) into.CopyInto(shadow); continue; }
                shadow.MouseRawDX = shadow.MouseRawDY = 0;
                shadow.MouseRawWheel = shadow.MouseRawWheelH = 0;
                shadow.JoyCon2MouseDX = shadow.JoyCon2MouseDY = 0f;
                RecorderTestKit.AssertSameState(shadow, into, "full frame at " + e.Seconds);
            }
            Assert.True(checkedFrames >= 4, "the recording holds too few full frames to judge: " + checkedFrames);
        }

        // ── The hash ──

        private static (bool Ended, bool HasHash, bool Matches, long Events) ReadEnd(string text)
        {
            using var reader = new RecordingReader(new StringReader(text));
            while (reader.Read()) { }
            return (reader.Ended, reader.HasHash, reader.HashMatches, reader.EventLines);
        }

        [Fact]
        public void TheLastLineIsAHashOfTheBody_AndACutFileShows()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            var state = RecorderTestKit.Rest();
            for (int i = 0; i < 50; i++)
            {
                state.Axis[1] = 30000 + i * 100;
                capture.Input(pad, state);
                capture.Advance(0.01);
            }
            string path = recorder.Stop(wait: true).Path;
            string whole = RecorderTestKit.Text(path);
            var lines = whole.Split("\r\n").ToList();
            Assert.StartsWith(RecordingCodec.HashPrefix, lines[^2]);
            Assert.Equal(string.Empty, lines[^1]);

            var end = ReadEnd(whole);
            Assert.True(end.Ended && end.HasHash && end.Matches, "the file as written does not check out");

            // One line gone from the middle.
            var cut = new List<string>(lines);
            cut.RemoveAt(lines.Count / 2);
            Assert.False(ReadEnd(string.Join("\r\n", cut)).Matches);
            // One digit changed.
            int at = whole.IndexOf("a1=30100", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.False(ReadEnd(whole.Remove(at + 3, 1).Insert(at + 3, "4")).Matches);
            // The end gone: no hash, and no end line.
            var headOnly = ReadEnd(string.Join("\r\n", lines.Take(lines.Count - 8)));
            Assert.False(headOnly.HasHash);
            Assert.False(headOnly.Ended);
            Assert.True(headOnly.Events > 0);
            // A line added after the hash.
            Assert.False(ReadEnd(whole + "0.000000 M - mark\r\n").Matches);
            // Line ends changed by a program that carried the file do not count.
            Assert.True(ReadEnd(whole.Replace("\r\n", "\n")).Matches);
        }

        // ── A recording stops by itself ──

        private static string WaitForTheEnd(ProblemRecorder recorder)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until)
            {
                var state = recorder.State;
                if (!state.Recording && !state.Finishing && state.LastFile != null) return state.LastFile;
                System.Threading.Thread.Sleep(20);
            }
            Assert.Fail("the recording did not stop by itself");
            return null;
        }

        [Fact]
        public void ARecordingStopsByItselfAtItsLength_AndSaysWhich()
        {
            using var host = new RecorderTestHost();
            host.DeviceList.Add(RecorderTestKit.Pad());
            var recorder = new ProblemRecorder(host) { MaxDuration = TimeSpan.FromMilliseconds(400) };
            Assert.True(recorder.Start().Ok);
            string path = WaitForTheEnd(recorder);
            Assert.Equal("Status: finished. It stopped by itself at 0.4 seconds.", RecorderTestKit.Lines(path)[1]);
            Assert.Contains(RecorderTestKit.Events(path), e => e.Layer == 'M' && e.Payload == "stop it stopped by itself at 0.4 seconds");
            Assert.False(recorder.State.Recording);
        }

        [Fact]
        public void ARecordingStopsByItselfAtItsSize_AndSaysWhich()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { MaxBytes = 40 * 1024, FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            var state = RecorderTestKit.Rest();
            var until = DateTime.UtcNow.AddSeconds(20);
            for (int i = 0; recorder.State.Recording && DateTime.UtcNow < until; i++)
            {
                state.Axis[0] = i % 65536;
                state.Axis[1] = (i * 7) % 65536;
                capture.Input(pad, state);
                if (i % 500 == 0) System.Threading.Thread.Sleep(5);
            }
            string path = WaitForTheEnd(recorder);
            Assert.Equal("Status: finished. It stopped by itself at 40 KB compressed.", RecorderTestKit.Lines(path)[1]);
            // The limit is on the zip, which the text far outgrows: what
            // was counted is what the zip holds, give or take the summary
            // on top and the last block written.
            long zipped = new FileInfo(path).Length, plain = System.Text.Encoding.UTF8.GetByteCount(RecorderTestKit.Text(path));
            Assert.InRange(zipped, 36 * 1024, 40 * 1024 + 256 * 1024);
            Assert.True(plain > 2 * 40 * 1024, plain + " bytes of text");
        }

        [Fact]
        public void TheLimitsAreTenMinutesAndTwentyMegabytes()
        {
            using var host = new RecorderTestHost();
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.Equal(TimeSpan.FromMinutes(10), recorder.MaxDuration);
            Assert.Equal(20L * 1024 * 1024, recorder.MaxBytes);
            Assert.Equal(TimeSpan.FromSeconds(60), recorder.Kept);
            Assert.Equal("10 minutes", ProblemRecorder.Words(recorder.MaxDuration));
            Assert.Equal("20 MB", ProblemRecorder.Words(recorder.MaxBytes));
        }

        // ── The parts of the file ──

        [Fact]
        public void TheFileHasItsPartsInOrder_AndADeviceLineAStandInDeviceCanBeMadeFrom()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad("DualSense Wireless Controller");
            pad.HasGyro = pad.HasAccel = true;
            pad.CapTouchpadCount = 1;
            pad.CapTouchpadFingerCounts = new[] { 2 };
            pad.DisplayName = "Sam's pad";
            host.DeviceList.Add(pad);
            host.Controllers.Add("C1 type=Xbox enabled=1");
            host.SettingsPart.Add("assign device=D1 controller=C1");
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            string path = recorder.Start().Path;
            Assert.StartsWith(ProblemRecorder.FilePrefix, Path.GetFileName(path));
            Assert.EndsWith(".log.partial", path);
            Assert.Equal(host.Dir, Path.GetDirectoryName(path));
            // While it runs the status line says so.
            using (var running = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                Assert.Equal(RecordingCodec.FirstLine, running.ReadLine());
                Assert.Equal(RecordingCodec.StatusRecording, running.ReadLine());
            }
            recorder.CaptureForTest.Input(pad, RecorderTestKit.Rest());
            path = recorder.Stop(wait: true).Path;

            var lines = RecorderTestKit.Lines(path).ToList();
            Assert.Equal(RecordingCodec.FirstLine, lines[0]);
            Assert.Equal("Status: finished. The user stopped it.", lines[1]);
            Assert.Contains("Every key and button pressed on every device while this recorded is in this file, keyboards and mice included.", lines[2]);
            var parts = lines.Where(l => l.StartsWith("== ", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[]
            {
                RecordingCodec.SummaryHeader, RecordingCodec.ScrubHeader, RecordingCodec.SystemHeader, RecordingCodec.DevicesHeader,
                RecordingCodec.ControllersHeader, RecordingCodec.SettingsHeader, RecordingCodec.EventsHeader, RecordingCodec.EndHeader,
            }, parts);

            using var reader = RecordingReader.Open(path);
            var device = Assert.Single(reader.Devices);
            Assert.Equal("D1", device.Alias);
            Assert.Equal("DualSense Wireless Controller", device.ProductName);
            Assert.Equal((ushort)0x054C, device.VendorId);
            Assert.Equal((ushort)0x0CE6, device.ProductId);
            Assert.Equal("030000004c050000e60c000000006800", device.SdlGuid);
            Assert.Equal("Gamepad", device.Type);
            Assert.Equal("usb", device.Fact("bus"));
            Assert.Equal(6, device.Count("axes"));
            Assert.Equal(17, device.Count("buttons"));
            Assert.Equal(1, device.Count("hats"));
            Assert.Equal(0, device.Count("sliders"));
            Assert.Equal("1", device.Fact("gyro"));
            Assert.Equal("1", device.Fact("accel"));
            Assert.Equal(1, device.Count("touchpads"));
            Assert.Equal("2", device.Fact("fingers"));
            Assert.False(device.OpenedAsGamepad);
            Assert.Equal(@"HID\VID_054C&PID_0CE6&MI_03\<i 1>", device.Fact("path"));
            Assert.Equal("<bt 1>", device.Fact("serial"));
            Assert.Equal("<device-name 1>", device.Fact("label"));
            var controller = Assert.Single(reader.Controllers);
            Assert.Equal("Xbox", controller.Fact("type"));
            Assert.Equal(new[] { "assign device=D1 controller=C1" }, reader.Settings);
            Assert.Equal(new[] { "PadForge test" }, reader.SystemPart);
            Assert.NotEmpty(reader.Summary);
            Assert.NotEmpty(reader.ScrubCheck);
        }

        [Fact]
        public void AFinishedRecording_IsOneZipOfOneLog_ThatReadsAsTheLogItHolds()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            string partial = recorder.Start().Path;
            var capture = recorder.CaptureForTest;
            var state = RecorderTestKit.Rest();
            for (int i = 0; i < 300; i++)
            {
                state.Axis[0] = 20000 + i * 37;
                capture.Input(pad, state);
                capture.Advance(0.004);
            }
            string zip = recorder.Stop(wait: true).Path;

            Assert.Equal(ProblemRecorder.ZipPathOf(partial), zip);
            Assert.EndsWith(".zip", zip);
            Assert.Equal(new[] { zip }, Directory.GetFiles(host.Dir));
            Assert.Equal(zip, recorder.State.LastFile);
            string text;
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
            {
                var entry = Assert.Single(archive.Entries);
                Assert.Equal(Path.GetFileNameWithoutExtension(zip) + ".log", entry.FullName);
                using var reader = new StreamReader(entry.Open());
                text = reader.ReadToEnd();
            }
            Assert.StartsWith(RecordingCodec.FirstLine + "\r\n" + "Status: finished.", text);

            // The zip and the .log it holds, unzipped, read the same, to the
            // integer and to the hash.
            string log = Path.Combine(host.Dir, "unzipped.log");
            File.WriteAllText(log, text);
            var fromZip = RecorderTestKit.PassFrames(zip, 1);
            var fromLog = RecorderTestKit.PassFrames(log, 1);
            Assert.Equal(300, fromZip.Count);
            Assert.Equal(fromZip.Count, fromLog.Count);
            for (int i = 0; i < fromZip.Count; i++) RecorderTestKit.AssertSameState(fromLog[i].State, fromZip[i].State, "pass " + i);
            foreach (string file in new[] { zip, log })
            {
                using var reader = RecordingReader.Open(file);
                while (reader.Read()) { }
                Assert.True(reader.Ended && reader.HasHash && reader.HashMatches, file);
            }
        }

        [Fact]
        public void ADeviceThatConnectsWhileRecording_SaysWhatItIsAmongTheEvents()
        {
            using var host = new RecorderTestHost();
            var first = RecorderTestKit.Pad("First Pad");
            host.DeviceList.Add(first);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(first, RecorderTestKit.Rest());
            capture.Advance(1);
            var late = RecorderTestKit.Pad("Late Pad", 0x045E, 0x0B13);
            capture.Input(late, RecorderTestKit.Rest());
            string path = recorder.Stop(wait: true).Path;

            using var reader = RecordingReader.Open(path);
            Assert.Single(reader.Devices);
            while (reader.Read()) { }
            Assert.Equal(2, reader.Devices.Count);
            var second = reader.DeviceByNumber(2);
            Assert.Equal("Late Pad", second.ProductName);
            Assert.Equal((ushort)0x045E, second.VendorId);
            Assert.True(second.HasFullFrame);
            Assert.Equal(32768, second.State.Axis[0]);
        }

        [Fact]
        public void AnEventLine_IsItsTimeItsLayerItsSubjectAndItsPayload()
        {
            Assert.True(RecordingCodec.TrySplitEvent("12.345678 I D1 a0=-1234 a1=88 b3+", out long micros, out char layer, out var subject, out var payload));
            Assert.Equal(12_345_678, micros);
            Assert.Equal('I', layer);
            Assert.Equal("D1", subject.ToString());
            Assert.Equal("a0=-1234 a1=88 b3+", payload.ToString());
            Assert.True(RecordingCodec.TrySplitEvent("-3.250000 L - HEARTBEAT poll", out micros, out layer, out subject, out payload));
            Assert.Equal(-3_250_000, micros);
            Assert.Equal("-", subject.ToString());
            Assert.False(RecordingCodec.TrySplitEvent("== End ==", out _, out _, out _, out _));
            Assert.False(RecordingCodec.TrySplitEvent("12.3 I D1 a0=1", out _, out _, out _, out _));

            var sb = new StringBuilder();
            RecordingCodec.AppendTime(sb, 600_000_001);
            Assert.Equal("600.000001", sb.ToString());
            sb.Clear();
            RecordingCodec.AppendTime(sb, -5);
            Assert.Equal("-0.000005", sb.ToString());
            Assert.Equal("IOSFTWELM", string.Concat(RecordingCodec.LayerInput, RecordingCodec.LayerOutput, RecordingCodec.LayerSent,
                RecordingCodec.LayerFeedback, RecordingCodec.LayerTiming, RecordingCodec.LayerWindow, RecordingCodec.LayerError,
                RecordingCodec.LayerLine, RecordingCodec.LayerMarker));
        }

        [Theory]
        [InlineData(0.25f, "0.25")]
        [InlineData(-0.5f, "-0.5")]
        [InlineData(0f, "0")]
        [InlineData(-0f, "-0")]
        [InlineData(9.80665f, "9.80665")]
        [InlineData(0.012345678f, "0.012345678")]
        [InlineData(1.5e-7f, "1.5E-07")]
        [InlineData(float.NaN, "xFFC00000")]
        [InlineData(float.PositiveInfinity, "x7F800000")]
        public void AFraction_IsItsShortestDecimal_OrItsBitsWhenNoNumber_AndReadsBackToTheBit(float value, string written)
        {
            var sb = new StringBuilder("gy=");
            int bits = BitConverter.SingleToInt32Bits(value);
            RecordingCodec.AppendFloat(sb, bits);
            Assert.Equal("gy=" + written, sb.ToString());
            sb.Append(",0,0");
            Assert.True(RecordingCodec.TryParseToken(sb.ToString(), out var field, out _, out int a, out _, out _));
            Assert.Equal(RecordingField.Gyro, field);
            Assert.Equal(bits, a);
        }

        [Fact]
        public void EveryFloatOfAWideSweep_ReadsBackToTheBit()
        {
            var random = new Random(597);
            var sb = new StringBuilder();
            for (int i = 0; i < 200_000; i++)
            {
                int bits = random.Next(int.MinValue, int.MaxValue);
                sb.Clear().Append("ji=");
                RecordingCodec.AppendFloat(sb, bits);
                Assert.True(RecordingCodec.TryParseToken(sb.ToString(), out _, out _, out int a, out _, out _), sb.ToString());
                Assert.Equal(bits, a);
            }
        }

        [Fact]
        public void EveryFieldThatMakesAState_HasATokenThatReadsBack()
        {
            foreach (RecordingField field in Enum.GetValues<RecordingField>())
            {
                if (field == RecordingField.None) continue;
                char layer = RecordingCodec.LayerOf(field);
                if (layer is not ('I' or 'O')) continue;
                int index = field is RecordingField.TouchContact or RecordingField.TouchPosition ? (1 << 4) | 2 : field == RecordingField.Shape ? RecordingCodec.Shape.Nfc : 1;
                bool flag = field is RecordingField.Button or RecordingField.TouchClick or RecordingField.MidiNote or RecordingField.MidiCcUp
                    or RecordingField.MidiCcDown or RecordingField.CapSense or RecordingField.NfcTag or RecordingField.MidiOutNote
                    or RecordingField.OutTouchClick or RecordingField.Online;
                int a = flag ? 1 : field == RecordingField.OutType ? (int)VirtualControllerType.PlayStation : 5;
                var sb = new StringBuilder();
                RecordingCodec.AppendToken(sb, field, index, a, 0, 0);
                Assert.True(RecordingCodec.TryParseToken(sb.ToString(), out var read, out int readIndex, out int readA, out _, out _), field + ": " + sb);
                Assert.Equal(field, read);
                Assert.Equal(a, readA);
                if (field != RecordingField.Online && field != RecordingField.OutType) Assert.True(index == readIndex || readIndex == 0, field.ToString());
            }
            Assert.False(RecordingCodec.TryParseToken("zz9=1", out _, out _, out _, out _, out _));
            Assert.False(RecordingCodec.TryParseToken("full", out _, out _, out _, out _, out _));
        }
    }
}
