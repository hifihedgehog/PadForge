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
    }
}
