using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.Services.Plugins;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Where two readers of one pad disagree (#600 item 32): XInput's read
    /// of a place against PadForge's reader of the pad at that place, on
    /// the buttons, the sticks, the D-pad and the triggers, said once the
    /// same difference holds for <see cref="ProblemRecorder.DisagreeFor"/>,
    /// and never while PadForge's reader reads nothing.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecorderReaderComparisonTests
    {
        /// <summary>A pad at rest as PadForge's reader gives it: sticks
        /// centered, triggers out, the D-pad centered.</summary>
        private static CustomInputState PadAtRest()
        {
            var s = new CustomInputState();
            s.Axis[0] = s.Axis[3] = 32768;
            s.Axis[1] = s.Axis[4] = 32767;
            s.Povs[0] = -1;
            return s;
        }

        [Fact]
        public void TheReaders_AreComparedOnTheDPadAndTheTriggersToo()
        {
            var state = PadAtRest();
            var rest = new XInputRead(true, 0, 0, 0, 0, 0, 0, 0);
            Assert.Null(ProblemRecorder.Difference(state, rest));

            Assert.Equal("DPad 9000 while PadForge's reader reads -1",
                ProblemRecorder.Difference(state, rest with { Buttons = Gamepad.DPAD_RIGHT }));
            state.Povs[0] = 9000;
            Assert.Null(ProblemRecorder.Difference(state, rest with { Buttons = Gamepad.DPAD_RIGHT }));
            // Up and down at once read as up, as SDL reads them.
            state.Povs[0] = 0;
            Assert.Null(ProblemRecorder.Difference(state, rest with { Buttons = (ushort)(Gamepad.DPAD_UP | Gamepad.DPAD_DOWN) }));
            state.Povs[0] = -1;

            Assert.Equal("LeftTrigger 200 while PadForge's reader reads 0",
                ProblemRecorder.Difference(state, rest with { LeftTrigger = 200 }));
            // Within a sixteenth of the travel is agreement.
            state.Axis[5] = 190 * 257;
            Assert.Null(ProblemRecorder.Difference(state, rest with { RightTrigger = 200 }));
            Assert.Equal("RightTrigger 255 while PadForge's reader reads 190",
                ProblemRecorder.Difference(state, rest with { RightTrigger = 255 }));
        }

        [Fact]
        public void ADifference_WhilePadForgesReaderReadsNothing_IsNoDisagreement()
        {
            var before = XInputReadBack.ReadSeam;
            var read = new XInputRead(true, 0, 0, 0, 0, 0, 0, 0);
            XInputReadBack.ReadSeam = place => place == 0 ? read : default;
            try
            {
                using var host = new RecorderTestHost();
                var pad = RecorderTestKit.Pad("Wireless Controller");
                pad.DevicePath = "XInput#0";
                host.DeviceList.Add(pad);
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                var held = PadAtRest();
                held.Buttons[0] = true;
                capture.Input(pad, held);

                // Suspended behind another window: Step 2 reads nothing, so
                // the reader's A is from before, and XInput reads it up.
                capture.Rest(2);
                capture.EndPass();
                recorder.WaitForFlush();
                recorder.WaitForFlush();
                for (int i = 0; i < 4; i++)
                {
                    capture.Advance(0.5);
                    capture.EndPass();
                    recorder.WaitForFlush();
                    recorder.WaitForFlush();
                }

                // Running again, the reader agrees: nothing to say.
                capture.Rest(0);
                capture.Input(pad, PadAtRest());
                capture.EndPass();
                recorder.WaitForFlush();
                recorder.WaitForFlush();

                // The positive control, in the same file: a reader that runs
                // and holds A while XInput reads it up disagrees.
                capture.Advance(0.5);
                capture.Input(pad, held);
                capture.EndPass();
                recorder.WaitForFlush();
                recorder.WaitForFlush();
                capture.Advance(0.2);
                capture.Input(pad, held);
                capture.EndPass();
                recorder.WaitForFlush();
                recorder.WaitForFlush();

                string path = recorder.Stop(wait: true).Path;
                var reader = RecorderTestKit.Events(path).Where(e => e.Layer == 'R' && e.Subject == "D1"
                    && e.Payload.StartsWith("XInput", StringComparison.Ordinal)).ToList();
                var said = Assert.Single(reader);
                Assert.Equal("XInput place 1 reads A up while PadForge's reader reads it down", said.Payload);
                Assert.True(said.Seconds >= 2.5, said.Seconds.ToString(CultureInfo.InvariantCulture));
            }
            finally { XInputReadBack.ReadSeam = before; }
        }

        // ── The window, measured ──

        /// <summary>A pad's state as XInput gives it, from the state PadForge's
        /// reader gives: the inverse of <see cref="ProblemRecorder.Difference"/>'s
        /// reading of the two.</summary>
        private static XInputRead ToXInput(CustomInputState s)
        {
            ushort buttons = 0;
            ushort[] bits = { Gamepad.A, Gamepad.B, Gamepad.X, Gamepad.Y, Gamepad.LEFT_SHOULDER, Gamepad.RIGHT_SHOULDER,
                Gamepad.BACK, Gamepad.START, Gamepad.LEFT_THUMB, Gamepad.RIGHT_THUMB };
            for (int i = 0; i < bits.Length; i++) if (s.Buttons[i]) buttons |= bits[i];
            int hat = s.Povs[0];
            if (hat >= 0)
            {
                int eighth = (hat + 2250) % 36000 / 4500;
                if (eighth is 7 or 0 or 1) buttons |= Gamepad.DPAD_UP;
                if (eighth is 1 or 2 or 3) buttons |= Gamepad.DPAD_RIGHT;
                if (eighth is 3 or 4 or 5) buttons |= Gamepad.DPAD_DOWN;
                if (eighth is 5 or 6 or 7) buttons |= Gamepad.DPAD_LEFT;
            }
            return new XInputRead(true, buttons, (byte)(s.Axis[2] >> 8), (byte)(s.Axis[5] >> 8),
                (short)(s.Axis[0] - 32768), (short)(32767 - s.Axis[1]), (short)(s.Axis[3] - 32768), (short)(32767 - s.Axis[4]));
        }

        /// <summary>
        /// The window measured, by hand: set PADFORGE_MEASURE_READER_WINDOW to
        /// a file for the results. A healthy pad at XInput place 1 runs the
        /// Test Controller's fastest sweep (Move Time 0.5 s, no rest, every
        /// button for 50 ms, the D-pad turning), read by both readers from the
        /// same motion: PadForge's reader on a poll thread of 1,000 passes a
        /// second, through the real capture, flush and watch, and XInput when
        /// the watch reads it. Every line that says they disagree is false.
        /// Then, past a mark in the same file, XInput holds Back down while
        /// the reader does not: a real disagreement, the positive control,
        /// which must be said.
        /// </summary>
        [Fact]
        public void TheWindow_MeasuredOnAFastTestControllerSweep()
        {
            string report = Environment.GetEnvironmentVariable("PADFORGE_MEASURE_READER_WINDOW");
            if (string.IsNullOrEmpty(report)) return;
            int seconds = int.TryParse(Environment.GetEnvironmentVariable("PADFORGE_MEASURE_READER_SECONDS"), out int s) ? s : 60;
            var fast = new TestControllerConfig
            {
                MoveMs = 500, RestMs = 0, PressButtons = true, PressNavigation = true, TurnDpad = true, PressMs = 50,
            };
            var results = new StringBuilder();
            results.AppendLine("window " + ProblemRecorder.DisagreeFor.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms, "
                + seconds.ToString(CultureInfo.InvariantCulture) + " s a run, " + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " processors");
            results.AppendLine(Measure("sweep", fast, busy: false, seconds));
            results.AppendLine(Measure("circle", fast with { Circle = true }, busy: false, seconds));
            results.AppendLine(Measure("sweep, every processor busy", fast, busy: true, seconds));
            File.WriteAllText(report, results.ToString());
        }

        private static string Measure(string name, TestControllerConfig config, bool busy, int seconds)
        {
            var before = XInputReadBack.ReadSeam;
            var clock = Stopwatch.StartNew();
            var xinput = new CustomInputState();
            int reads = 0;
            bool control = false;
            XInputReadBack.ReadSeam = place =>
            {
                if (place != 0) return default;
                lock (xinput)
                {
                    reads++;
                    TestControllerPattern.Fill(xinput, config, clock.ElapsedMilliseconds);
                    var x = ToXInput(xinput);
                    return Volatile.Read(ref control) ? x with { Buttons = (ushort)(x.Buttons | Gamepad.BACK) } : x;
                }
            };
            var stop = new ManualResetEventSlim();
            var load = new List<Thread>();
            try
            {
                using var host = new RecorderTestHost();
                var pad = RecorderTestKit.Pad("Healthy Pad");
                pad.DevicePath = "XInput#0";
                host.DeviceList.Add(pad);
                var recorder = new ProblemRecorder(host);
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                long passes = 0, longestGap = 0;
                var marked = new ManualResetEventSlim();
                var poll = new Thread(() =>
                {
                    var state = new CustomInputState();
                    long period = Stopwatch.Frequency / 1000, next = Stopwatch.GetTimestamp(), last = next;
                    while (!stop.IsSet)
                    {
                        // The mark goes into the ring from this thread, its
                        // only writer, as the control begins.
                        if (Volatile.Read(ref control) && !marked.IsSet)
                        {
                            capture.Mark();
                            marked.Set();
                        }
                        TestControllerPattern.Fill(state, config, clock.ElapsedMilliseconds);
                        capture.Input(pad, state);
                        capture.EndPass();
                        passes++;
                        long now = Stopwatch.GetTimestamp();
                        longestGap = Math.Max(longestGap, now - last);
                        last = now;
                        next += period;
                        while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(20);
                    }
                }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "measure poll" };
                if (busy)
                    for (int i = 0; i < Environment.ProcessorCount; i++)
                    {
                        var t = new Thread(() => { while (!stop.IsSet) Thread.SpinWait(1000); }) { IsBackground = true };
                        load.Add(t);
                        t.Start();
                    }
                poll.Start();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                Volatile.Write(ref control, true);
                Thread.Sleep(TimeSpan.FromSeconds(10));
                stop.Set();
                poll.Join();
                foreach (var t in load) t.Join();
                recorder.WaitForFlush();
                recorder.WaitForFlush();
                string path = recorder.Stop(wait: true).Path;
                var events = RecorderTestKit.Events(path);
                int mark = events.FindIndex(e => e.Layer == 'M');
                Assert.True(mark >= 0, "the control's mark is missing");
                var disagree = events.Take(mark).Where(e => e.Layer == 'R' && e.Payload.StartsWith("XInput place 1 reads", StringComparison.Ordinal)).ToList();
                int again = events.Take(mark).Count(e => e.Layer == 'R' && e.Payload.StartsWith("XInput and PadForge's reader agree again", StringComparison.Ordinal));
                var controlLines = events.Skip(mark).Where(e => e.Layer == 'R' && e.Payload.StartsWith("XInput place 1 reads", StringComparison.Ordinal)).ToList();
                var sb = new StringBuilder();
                sb.Append(name).Append(": ").Append(disagree.Count).Append(" false lines, ").Append(again).Append(" agree-again lines, ")
                  .Append(reads).Append(" XInput reads, ").Append(passes).Append(" passes, longest gap between passes ")
                  .Append((longestGap * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms. ")
                  .Append("Control, XInput holding Back for 10 s: ").Append(controlLines.Count).Append(" lines said");
                foreach (var e in disagree.Take(5)) sb.AppendLine().Append("  false: ").Append(e.Seconds.ToString("0.000", CultureInfo.InvariantCulture)).Append(' ').Append(e.Payload);
                foreach (var e in controlLines.Take(2)) sb.AppendLine().Append("  control: ").Append(e.Seconds.ToString("0.000", CultureInfo.InvariantCulture)).Append(' ').Append(e.Payload);
                return sb.ToString();
            }
            finally
            {
                stop.Set();
                XInputReadBack.ReadSeam = before;
            }
        }

        // ── Raw Input: a second reader of every other device ──

        /// <summary>An SDL device with the GUID SDL would give it, as far as
        /// the comparison asks.</summary>
        private sealed class StandIn : WebControllerDevice, ISdlInputDevice
        {
            public StandIn(string guid) : base(Guid.NewGuid().ToString(), "Stand-in") => SdlGuid = guid;
            public new string SdlGuid { get; }
        }

        private const string DirectInputStick = "030000004f0400000ab1000000000000";

        /// <summary>Readers a test feeds: a layout for each device, and the
        /// newest report values and times it sets.</summary>
        private sealed class FedReaders : IReaderSource
        {
            public readonly Dictionary<UserDevice, ReaderLayout> Layouts = new();
            public uint[] Values = Array.Empty<uint>();
            public bool[] Known = Array.Empty<bool>();
            public long Named = 1, Other;
            public int OtherCount;
            public bool Reads = true;

            public ReaderLayout Layout(UserDevice device) => Layouts.TryGetValue(device, out var l) ? l : null;

            public bool Read(ReaderLayout layout, ReaderReading into)
            {
                into.NamedTicks = Named;
                into.OtherTicks = Other;
                into.OtherCount = OtherCount;
                if (!Reads) return false;
                into.Values = (uint[])Values.Clone();
                into.Known = (bool[])Known.Clone();
                return true;
            }

            public void Watch(IReadOnlyList<ReaderLayout> layouts) { }

            public void Release(ReaderLayout layout) { }

            public void ReleaseAll() { }
        }

        /// <summary>A stick SDL reads through DirectInput: an X axis of 0 to
        /// 255 and one button, matched as SDL numbers them.</summary>
        private static ReaderLayout Stick(bool hidden = false)
        {
            var objects = new List<DiObject>
            {
                new(RecorderReaderMatch.XAxis, 0x2, 0x01, 0x30, 1, 0, true),
                new(RecorderReaderMatch.Button, 0x4, 0x09, 1, 1, 0, false),
            };
            var controls = new List<HidControl>
            {
                new(0x01, 0x30, 1, 0, false, 0, 0, 255, 8, true),
                new(0x09, 1, 1, 0, true, 1, 0, 1, 1, true),
            };
            var plan = RecorderReaderPlan.DirectInputPlan(objects, controls, null, gamepadLayout: false, extraAxes: false, touchpad: false,
                rawAxes: 1, rawButtons: 1, numAxes: 1, numHats: 0, numButtons: 1, gamepadOpened: false);
            return new ReaderLayout { Plan = plan, Hidden = hidden };
        }

        private static UserDevice StickDevice(string name = "Recorded Stick")
        {
            var device = RecorderTestKit.Pad(name, 0x044F, 0xB10A, InputDeviceType.Joystick);
            device.SdlGuid = DirectInputStick;
            device.DevicePath = @"\\?\hid#vid_044f&pid_b10a#7&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            device.Device = new StandIn(DirectInputStick);
            return device;
        }

        private static CustomInputState Centered()
        {
            var s = new CustomInputState();
            s.Axis[0] = 32768;
            return s;
        }

        /// <summary>One pass of the poll thread with this state, then the
        /// watch's look, <paramref name="seconds"/> after the last. The
        /// recorder's thread is held while the clock, the pass and
        /// <paramref name="change"/>, what Raw Input hands over from here on,
        /// move together. The look that follows a flush runs after the flush
        /// is signaled, so one left over from the pass before would read the
        /// new value at the old time, and the next would take it as
        /// held.</summary>
        private static void Pass(ProblemRecorder recorder, RecordingCapture capture, UserDevice device, CustomInputState state, double seconds,
            Action change = null)
        {
            var release = new ManualResetEventSlim(false);
            recorder.HoldWriterForTest(release);
            try
            {
                change?.Invoke();
                if (seconds > 0) capture.Advance(seconds);
                capture.Input(device, state);
                capture.EndPass();
            }
            finally { release.Set(); }
            recorder.WaitForFlush();
            recorder.WaitForFlush();
        }

        private static List<string> ReaderLines(string path, string subject = "D1")
            => RecorderTestKit.Events(path).Where(e => e.Layer == 'R' && e.Subject == subject).Select(e => e.Payload).ToList();

        [Fact]
        public void ADirectInputDevice_IsComparedWithItsOwnReports_ADifferenceThatHoldsIsSaid_AndItsEnd()
        {
            var fed = new FedReaders { Values = new uint[] { 255, 0 }, Known = new[] { true, true } };
            var before = RecorderReaders.Seam;
            RecorderReaders.Seam = fed;
            try
            {
                using var host = new RecorderTestHost();
                var stick = StickDevice();
                host.DeviceList.Add(stick);
                fed.Layouts[stick] = Stick();
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                // Raw Input reads X at its end while PadForge's reader reads it
                // centered: said once it holds through a second look.
                Pass(recorder, capture, stick, Centered(), 0);
                Pass(recorder, capture, stick, Centered(), 0.05);
                Pass(recorder, capture, stick, Centered(), 0.1);
                // The two agree again, inside a sixteenth of the travel.
                Pass(recorder, capture, stick, Centered(), 0.35, () => fed.Values = new uint[] { 129, 0 });

                var lines = ReaderLines(recorder.Stop(wait: true).Path);
                Assert.Equal("is compared with its own reports through Raw Input: Axis0 is its X Axis, Button0 its Button 1", lines[0]);
                Assert.Single(lines, l => l == "Raw Input reads Axis0 65535 while PadForge's reader reads 32768");
                var again = Assert.Single(lines, l => l.StartsWith("Raw Input and PadForge's reader agree again after ", StringComparison.Ordinal));
                Assert.Equal("Raw Input and PadForge's reader agree again after 0.5 s", again);
            }
            finally { RecorderReaders.Seam = before; }
        }

        [Fact]
        public void ADifferenceThatKeepsChanging_IsNoDisagreement_WhileOneThatHoldsInTheSameFileIs()
        {
            var fed = new FedReaders { Values = new uint[] { 0, 0 }, Known = new[] { true, true } };
            var before = RecorderReaders.Seam;
            RecorderReaders.Seam = fed;
            try
            {
                using var host = new RecorderTestHost();
                var stick = StickDevice();
                host.DeviceList.Add(stick);
                fed.Layouts[stick] = Stick();
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                // A stick in motion: Raw Input's reading moves each look.
                foreach (uint x in new uint[] { 0, 40, 80, 255, 200 })
                    Pass(recorder, capture, stick, Centered(), 0.1, () => fed.Values = new uint[] { x, 0 });
                // The positive control in the same file: a button Raw Input
                // holds down.
                Pass(recorder, capture, stick, Centered(), 0.1, () => fed.Values = new uint[] { 128, 1 });
                Pass(recorder, capture, stick, Centered(), 0.1);
                Pass(recorder, capture, stick, Centered(), 0.1);

                var lines = ReaderLines(recorder.Stop(wait: true).Path);
                var said = Assert.Single(lines, l => l.StartsWith("Raw Input reads ", StringComparison.Ordinal));
                Assert.Equal("Raw Input reads Button0 down while PadForge's reader reads it up", said);
            }
            finally { RecorderReaders.Seam = before; }
        }

        [Fact]
        public void WhileTheEngineIsSuspended_NothingIsCompared_AndTheSameDifferenceAfterIsSaid()
        {
            var fed = new FedReaders { Values = new uint[] { 255, 0 }, Known = new[] { true, true } };
            var before = RecorderReaders.Seam;
            RecorderReaders.Seam = fed;
            try
            {
                using var host = new RecorderTestHost();
                var stick = StickDevice();
                host.DeviceList.Add(stick);
                fed.Layouts[stick] = Stick();
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                capture.Input(stick, Centered());
                capture.Rest(2);
                capture.EndPass();
                recorder.WaitForFlush();
                recorder.WaitForFlush();
                for (int i = 0; i < 4; i++)
                {
                    capture.Advance(0.5);
                    capture.EndPass();
                    recorder.WaitForFlush();
                    recorder.WaitForFlush();
                }
                // Running again, the difference is said, a mark before it: the
                // positive control in the same file.
                capture.Rest(0);
                capture.Mark();
                Pass(recorder, capture, stick, Centered(), 0.1);
                Pass(recorder, capture, stick, Centered(), 0.1);
                Pass(recorder, capture, stick, Centered(), 0.1);

                var events = RecorderTestKit.Events(recorder.Stop(wait: true).Path);
                int mark = events.FindIndex(e => e.Layer == 'M');
                Assert.True(mark >= 0, "the mark is missing");
                Assert.DoesNotContain(events.Take(mark), e => e.Layer == 'R' && e.Payload.StartsWith("Raw Input reads", StringComparison.Ordinal));
                var said = Assert.Single(events.Skip(mark), e => e.Layer == 'R' && e.Payload.StartsWith("Raw Input reads", StringComparison.Ordinal));
                Assert.Equal("Raw Input reads Axis0 65535 while PadForge's reader reads 32768", said.Payload);
            }
            finally { RecorderReaders.Seam = before; }
        }

        [Fact]
        public void ReportsThatNameNoneOfItsControls_AreSaid_AndWhenTheyNameThemAgain()
        {
            var fed = new FedReaders { Values = new uint[] { 128, 0 }, Known = new[] { true, true } };
            var before = RecorderReaders.Seam;
            RecorderReaders.Seam = fed;
            try
            {
                using var host = new RecorderTestHost();
                var stick = StickDevice();
                host.DeviceList.Add(stick);
                fed.Layouts[stick] = Stick();
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                Pass(recorder, capture, stick, Centered(), 0);
                // Reports of another ID, 500 of them over two seconds past the
                // last named one: a Bluetooth PlayStation pad once SDL turns on
                // its full reports.
                Pass(recorder, capture, stick, Centered(), 0.1, () =>
                {
                    fed.Other = fed.Named + 2 * Stopwatch.Frequency;
                    fed.OtherCount = 500;
                    fed.Values = new uint[] { 255, 0 };
                });
                Pass(recorder, capture, stick, Centered(), 0.1);
                Pass(recorder, capture, stick, Centered(), 0.1);
                Pass(recorder, capture, stick, Centered(), 0.1, () =>
                {
                    fed.Named = fed.Other + 1;
                    fed.Values = new uint[] { 128, 0 };
                });

                var lines = ReaderLines(recorder.Stop(wait: true).Path);
                Assert.Single(lines, l => l == "the reports Raw Input hands over now name none of its controls");
                Assert.Single(lines, l => l == "Raw Input names its controls again");
                // What Raw Input named last is old, and no difference from it
                // is said.
                Assert.DoesNotContain(lines, l => l.StartsWith("Raw Input reads", StringComparison.Ordinal));
            }
            finally { RecorderReaders.Seam = before; }
        }

        [Fact]
        public void ASlowReportOfAnotherId_SaysNothing_WhileOnesThatKeepComingInTheSameFileAreSaid()
        {
            var fed = new FedReaders { Values = new uint[] { 128, 0 }, Known = new[] { true, true } };
            var before = RecorderReaders.Seam;
            RecorderReaders.Seam = fed;
            try
            {
                using var host = new RecorderTestHost();
                var stick = StickDevice();
                host.DeviceList.Add(stick);
                fed.Layouts[stick] = Stick();
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                Pass(recorder, capture, stick, Centered(), 0);
                // A puck at rest: two battery reports in three seconds, no
                // named report.
                Pass(recorder, capture, stick, Centered(), 0.1, () =>
                {
                    fed.Other = fed.Named + 3 * Stopwatch.Frequency;
                    fed.OtherCount = 2;
                });
                Pass(recorder, capture, stick, Centered(), 0.1);
                // The positive control in the same file, after the mark: others
                // at 250 a second for two seconds more.
                Pass(recorder, capture, stick, Centered(), 0.1, () =>
                {
                    capture.Mark();
                    fed.Other += 2 * Stopwatch.Frequency;
                    fed.OtherCount += 500;
                });
                Pass(recorder, capture, stick, Centered(), 0.1);

                var events = RecorderTestKit.Events(recorder.Stop(wait: true).Path);
                int mark = events.FindIndex(e => e.Layer == 'M');
                Assert.True(mark >= 0, "the mark is missing");
                const string unnamed = "the reports Raw Input hands over now name none of its controls";
                Assert.DoesNotContain(events.Take(mark), e => e.Layer == 'R' && e.Payload == unnamed);
                Assert.Single(events.Skip(mark), e => e.Layer == 'R' && e.Payload == unnamed);
                Assert.DoesNotContain(events, e => e.Layer == 'R' && e.Payload == "Raw Input names its controls again");
            }
            finally { RecorderReaders.Seam = before; }
        }

        [Fact]
        public void ADeviceWhoseReportsNeverNamedItsControls_IsSaidOnceOthersKeepComing()
        {
            var fed = new FedReaders { Reads = false, Named = 0 };
            var before = RecorderReaders.Seam;
            RecorderReaders.Seam = fed;
            try
            {
                using var host = new RecorderTestHost();
                var stick = StickDevice();
                host.DeviceList.Add(stick);
                fed.Layouts[stick] = Stick();
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                // The first report of another ID starts the run, at a time as
                // far from 0 as the pump's Stopwatch times are.
                Pass(recorder, capture, stick, Centered(), 0.1, () =>
                {
                    fed.Other = 1000 * Stopwatch.Frequency;
                    fed.OtherCount = 1;
                });
                Pass(recorder, capture, stick, Centered(), 0.1);
                Pass(recorder, capture, stick, Centered(), 0.1, () =>
                {
                    capture.Mark();
                    fed.Other += 3 * Stopwatch.Frequency / 2;
                    fed.OtherCount += 300;
                });
                Pass(recorder, capture, stick, Centered(), 0.1);

                var events = RecorderTestKit.Events(recorder.Stop(wait: true).Path);
                int mark = events.FindIndex(e => e.Layer == 'M');
                Assert.True(mark >= 0, "the mark is missing");
                const string unnamed = "the reports Raw Input hands over now name none of its controls";
                Assert.DoesNotContain(events.Take(mark), e => e.Layer == 'R' && e.Payload == unnamed);
                Assert.Single(events.Skip(mark), e => e.Layer == 'R' && e.Payload == unnamed);
                Assert.DoesNotContain(events, e => e.Layer == 'R' && e.Payload.StartsWith("Raw Input hands PadForge", StringComparison.Ordinal));
            }
            finally { RecorderReaders.Seam = before; }
        }

        [Fact]
        public void NoReportWhilePadForgesReaderSeesItMove_IsSaid_AndAHiddenDeviceSaysOnlyThatItIsHidden()
        {
            var fed = new FedReaders { Reads = false, Named = 0 };
            var before = RecorderReaders.Seam;
            RecorderReaders.Seam = fed;
            try
            {
                using var host = new RecorderTestHost();
                var stick = StickDevice("Open Stick");
                var hidden = StickDevice("Hidden Stick");
                hidden.HidHideEnabled = true;
                host.DeviceList.Add(stick);
                host.DeviceList.Add(hidden);
                fed.Layouts[stick] = Stick();
                fed.Layouts[hidden] = Stick(hidden: true);
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                for (int i = 0; i < 8; i++)
                {
                    var moving = Centered();
                    moving.Axis[0] = 1000 * i;
                    capture.Advance(0.2);
                    capture.Input(stick, moving);
                    capture.Input(hidden, moving);
                    capture.EndPass();
                    recorder.WaitForFlush();
                    recorder.WaitForFlush();
                }
                string path = recorder.Stop(wait: true).Path;
                Assert.Single(ReaderLines(path, "D1"), l => l == "Raw Input hands PadForge no report of it while PadForge's reader sees it move");
                var hiddenLines = ReaderLines(path, "D2");
                Assert.Single(hiddenLines, l => l == "has no second reader: hidden by HidHide");
                Assert.DoesNotContain(hiddenLines, l => l.StartsWith("Raw Input hands PadForge", StringComparison.Ordinal));
            }
            finally { RecorderReaders.Seam = before; }
        }

        [Fact]
        public void ADeviceWithNoSecondReader_SaysWhyOnce_AndAnXInputPlaceIsLeftToXInput()
        {
            var fed = new FedReaders();
            var before = RecorderReaders.Seam;
            var beforeXInput = XInputReadBack.ReadSeam;
            RecorderReaders.Seam = fed;
            XInputReadBack.ReadSeam = place => default;
            try
            {
                using var host = new RecorderTestHost();
                var guitar = StickDevice("Recorded Guitar");
                guitar.Device = new StandIn("03000000de2800000312000000006700");
                var pad = StickDevice("Recorded XInput Pad");
                pad.DevicePath = "XInput#0";
                host.DeviceList.Add(guitar);
                host.DeviceList.Add(pad);
                fed.Layouts[guitar] = new ReaderLayout { Reason = "SDL reads it through GameInput" };
                fed.Layouts[pad] = Stick();
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                for (int i = 0; i < 3; i++)
                {
                    capture.Advance(0.1);
                    capture.Input(guitar, Centered());
                    capture.Input(pad, Centered());
                    capture.EndPass();
                    recorder.WaitForFlush();
                    recorder.WaitForFlush();
                }
                string path = recorder.Stop(wait: true).Path;
                Assert.Equal(new[] { "has no second reader: SDL reads it through GameInput" }, ReaderLines(path, "D1"));
                Assert.DoesNotContain(ReaderLines(path, "D2"), l => l.StartsWith("is compared", StringComparison.Ordinal));
            }
            finally
            {
                RecorderReaders.Seam = before;
                XInputReadBack.ReadSeam = beforeXInput;
            }
        }

        [Fact]
        public void EveryDevicesHeaderLine_NamesItsSecondReader_OrWhyItHasNone()
        {
            using var host = new RecorderTestHost();
            string Facts(UserDevice d)
            {
                var sb = new StringBuilder();
                host.AppendDeviceFacts(sb, d);
                return sb.ToString();
            }
            var stick = RecorderTestKit.Pad("Stick", 0x044F, 0xB10A, InputDeviceType.Joystick);
            stick.SdlGuid = DirectInputStick;
            Assert.Equal(" held=shared second=rawinput", Facts(stick));
            var guitar = RecorderTestKit.Pad("Guitar");
            guitar.SdlGuid = "03000000de2800000312000000006700";
            Assert.Equal(" held=shared second=\"none, SDL reads it through GameInput\"", Facts(guitar));
            var bluetoothPad = RecorderTestKit.Pad("DualSense");
            bluetoothPad.SdlGuid = "050057564c050000e60c000000006800";
            Assert.Equal(" held=shared second=\"none, SDL's driver numbers its controls its own way\"", Facts(bluetoothPad));
            Assert.Equal(" held=shared second=\"none, Windows opens it for itself alone\"", Facts(RecorderTestKit.Keyboard()));
            // PadForge's own readers give no SDL GUID, a HID path or not.
            var falcon = RecorderTestKit.Pad("Own Reader");
            falcon.Device = new WebControllerDevice(Guid.NewGuid().ToString(), "Own Reader");
            Assert.Equal(" held=shared second=\"none, PadForge reads it itself\"", Facts(falcon));
            // A peer's pad, though SDL on the peer's PC reads it through DirectInput.
            using var peerDevice = new PadForge.Engine.RemoteLink.RemotePeerDevice(new PadForge.Engine.RemoteLink.RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "0123456789abcdef0123456789abcdef",
                PeerLocalDeviceId = Guid.NewGuid().ToString("N"),
                Name = "Peer Stick",
                SdlGuid = DirectInputStick,
            });
            var peer = RecorderTestKit.Pad("Peer Stick", 0x044F, 0xB10A, InputDeviceType.Joystick);
            peer.Device = peerDevice;
            Assert.Equal(" held=shared second=\"none, it is read on another PC\"", Facts(peer));
            var xinput = RecorderTestKit.Pad();
            xinput.DevicePath = "XInput#2";
            Assert.Equal(" held=shared xinput=3", Facts(xinput));
        }
    }
}
