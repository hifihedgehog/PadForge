using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Recording;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// What a problem recording (#597) costs the poll thread. Two facts hold
    /// on any machine and are tested: the capture's side of a pass allocates
    /// nothing, and it takes no lock. What a pass costs in time and whether
    /// the writer keeps up under load depend on the machine, so they are
    /// measured and printed by <see cref="MeasureTheCost"/>, which runs only
    /// when PADFORGE_RECORDER_MEASURE is set, as the page pictures run only
    /// when their folder is named. The allocation windows run in the
    /// collection that runs alone, as the other allocation measurements do.
    /// </summary>
    [Collection("AllocationMeasurements")]
    public class ProblemRecorderCostTests
    {
        private readonly ITestOutputHelper _output;

        public ProblemRecorderCostTests(ITestOutputHelper output) => _output = output;

        /// <summary>What one device reports on each of its reports.</summary>
        internal enum Kind { DualSense, Mouse, Keyboard, Everything }

        /// <summary>
        /// Devices and virtual controllers that change on their own reports,
        /// driven one poll pass at a time through the capture's calls, in the
        /// order the engine makes them. A DualSense's motion is made the way
        /// SDL makes it (SDL_hidapi_ps5.c, HIDAPI_DriverPS5_ApplyCalibrationData):
        /// a 16-bit reading less its bias, times its sensitivity, over 1,024
        /// counts a degree a second or 8,192 counts a g, in radians a second
        /// and meters a second squared.
        /// </summary>
        internal sealed class Bench
        {
            internal readonly RecordingCapture Capture;
            internal readonly UserDevice[] Devices;
            private readonly Kind[] _kinds;
            private readonly CustomInputState[] _states;
            private readonly int _controllers;
            private readonly long[] _steps = new long[RecordingCodec.StepNames.Length];
            private readonly RecordingEntry[] _drained;
            private readonly bool _drain;
            private readonly Random _noise = new(597);
            private Gamepad _pad;
            private TouchpadState _touch;
            private MotionSnapshot _motion;
            private KbmRawState _kbm;
            private RawHidState _raw = RawHidState.Create(8, 32, 1);
            private MidiRawState _midi = MidiRawState.Create(16, 16);
            private VrRawState _vr;
            private readonly Vibration[] _rumble = { new() { LeftMotorSpeed = 0 }, new() { LeftMotorSpeed = 40000, RightMotorSpeed = 20000 } };
            private int _pass;

            /// <summary>The passes between two reports of a DualSense: 1 at
            /// 1,000 reports a second on a 1,000 Hz poll.</summary>
            internal int DualSenseEvery = 1;

            internal Bench(RecordingCapture capture, Kind[] kinds, int controllers, bool drain)
            {
                Capture = capture;
                _kinds = kinds;
                _controllers = controllers;
                _drain = drain;
                _drained = new RecordingEntry[capture.Ring.Capacity];
                Devices = new UserDevice[kinds.Length];
                _states = new CustomInputState[kinds.Length];
                for (int d = 0; d < kinds.Length; d++)
                {
                    Devices[d] = kinds[d] switch
                    {
                        Kind.Mouse => RecorderTestKit.Pad("Bench Mouse " + d, 0x046D, 0xC077, InputDeviceType.Mouse),
                        Kind.Keyboard => RecorderTestKit.Keyboard("Bench Keyboard " + d),
                        _ => RecorderTestKit.Pad("Bench Pad " + d),
                    };
                    Devices[d].ForceFeedbackState = new ForceFeedbackState();
                    capture.Prepare(Devices[d]);
                    var s = kinds[d] is Kind.DualSense or Kind.Everything ? RecorderTestKit.Rest() : new CustomInputState();
                    if (kinds[d] is Kind.DualSense or Kind.Everything)
                        s.Touchpads = new[] { new TouchpadInputState(2) };
                    if (kinds[d] == Kind.Everything)
                    {
                        s.Midi = new MidiInputState();
                        s.AnalogKeys = new AnalogKeyInputState();
                        s.CapSense = new bool[2];
                        s.NfcTag = new bool[2];
                    }
                    s.BatteryPercent = 80;
                    _states[d] = s;
                }
            }

            private float Gyro(double t, double hz, double amplitude, int bias, float sensitivity)
            {
                short raw = (short)Math.Clamp(amplitude * Math.Sin(2 * Math.PI * hz * t) + _noise.Next(-3, 4), short.MinValue, short.MaxValue);
                float result = (raw - bias) * sensitivity;
                return (result / 1024.0f) * MathF.PI / 180.0f;
            }

            private float Accel(double t, double hz, double center, double amplitude, int bias, float sensitivity)
            {
                short raw = (short)Math.Clamp(center + amplitude * Math.Sin(2 * Math.PI * hz * t) + _noise.Next(-6, 7), short.MinValue, short.MaxValue);
                float result = (raw - bias) * sensitivity;
                return (result / 8192.0f) * 9.80665f;
            }

            private void Report(int d, double t)
            {
                var s = _states[d];
                switch (_kinds[d])
                {
                    case Kind.DualSense:
                        // Both sticks swept, the triggers squeezed, the pad
                        // turned and tilted in the hands.
                        s.Axis[0] = 32768 + (int)(24000 * Math.Sin(2 * Math.PI * 0.7 * t + d));
                        s.Axis[1] = 32768 + (int)(24000 * Math.Cos(2 * Math.PI * 0.7 * t + d));
                        s.Axis[2] = 32768 + (int)(16000 * Math.Sin(2 * Math.PI * 0.4 * t));
                        s.Axis[3] = 32768 + (int)(16000 * Math.Cos(2 * Math.PI * 0.4 * t));
                        s.Axis[4] = (int)(32767 * (1 + Math.Sin(2 * Math.PI * 0.3 * t)));
                        s.Axis[5] = (int)(32767 * (1 + Math.Cos(2 * Math.PI * 0.3 * t)));
                        s.Gyro[0] = Gyro(t, 0.6, 6000, 3, 1.0137f);
                        s.Gyro[1] = Gyro(t + 0.2, 0.5, 9000, -5, 0.9921f);
                        s.Gyro[2] = Gyro(t + 0.4, 0.4, 3000, 1, 1.0042f);
                        s.Accel[0] = Accel(t, 0.6, 400, 2500, 12, 1.0018f);
                        s.Accel[1] = Accel(t, 0.5, 8192, 1200, -20, 0.9987f);
                        s.Accel[2] = Accel(t, 0.4, 600, 2000, 7, 1.0009f);
                        break;
                    case Kind.Mouse:
                        s.MouseRawDX = (int)(6 * Math.Sin(2 * Math.PI * t)) | 1;
                        s.MouseRawDY = (int)(4 * Math.Cos(2 * Math.PI * t));
                        s.Buttons[0] = ((int)(t * 2) & 1) == 1;
                        break;
                    case Kind.Keyboard:
                        s.Buttons[0x41 + ((int)(t * 3) % 26)] = ((int)(t * 6) & 1) == 1;
                        break;
                    default:
                        int phase = _pass & 1;
                        for (int i = 0; i < 6; i++) s.Axis[i] = 30000 + phase * 1000 + d + i;
                        s.Gyro[0] = phase * 0.5f; s.Gyro[1] = -phase; s.Accel[2] = 1f + phase * 0.01f;
                        s.Touchpads[0].FingerDown[0] = phase == 1;
                        s.Touchpads[0].FingerX[0] = phase * 0.25f;
                        s.Midi.Cc[7] = (byte)(phase * 100);
                        if (phase == 1) s.AnalogKeys.Set(4, 0.5f); else s.AnalogKeys.ResetForReuse();
                        s.CapSense[1] = phase == 1;
                        s.Buttons[d % 64] = phase == 1;
                        s.MouseRawDX = phase == 1 ? 3 : -3;
                        break;
                }
            }

            /// <summary>One poll pass.</summary>
            internal void Pass()
            {
                double t = _pass / 1000.0;
                var capture = Capture;
                for (int d = 0; d < Devices.Length; d++)
                {
                    if (_kinds[d] != Kind.DualSense || _pass % DualSenseEvery == 0) Report(d, t);
                    capture.Input(Devices[d], _states[d]);
                }
                // The virtual controllers follow the sticks and the motion,
                // so their frames change on every pass.
                _pad.ThumbLX = (short)(20000 * Math.Sin(2 * Math.PI * 0.7 * t));
                _pad.ThumbLY = (short)(20000 * Math.Cos(2 * Math.PI * 0.7 * t));
                _pad.ThumbRX = (short)(15000 * Math.Sin(2 * Math.PI * 0.4 * t));
                _pad.ThumbRY = (short)(15000 * Math.Cos(2 * Math.PI * 0.4 * t));
                _pad.LeftTrigger = (ushort)(127 * (1 + Math.Sin(2 * Math.PI * 0.3 * t)));
                _pad.Buttons = (ushort)(((_pass >> 8) & 1) * Gamepad.A);
                _motion.GyroPitch = (float)Math.Sin(t); _motion.GyroYaw = (float)Math.Cos(t); _motion.AccelZ = 1f + (float)(0.01 * Math.Sin(3 * t));
                _touch.Down0 = ((_pass >> 9) & 1) == 1; _touch.X0 = (float)(0.5 + 0.4 * Math.Sin(t));
                for (int slot = 0; slot < _controllers; slot++)
                {
                    int lane = slot % 8;
                    if (lane < 4)
                    {
                        capture.OutGamepad(slot, in _pad);
                        capture.OutTouch(slot, in _touch);
                        capture.OutMotion(slot, in _motion);
                    }
                    else if (lane == 4)
                    {
                        _kbm.MouseDeltaX = (short)(_pass % 7 - 3); _kbm.MouseFlickX = _pass % 5 - 2; _kbm.Keys0 = (ulong)((_pass >> 6) & 1) << 5;
                        capture.OutKbm(slot, in _kbm);
                    }
                    else if (lane == 5)
                    {
                        _raw.Axes[0] = _pad.ThumbLX; _raw.Buttons[0] = (uint)(_pass & 1);
                        capture.OutRaw(slot, in _raw);
                    }
                    else if (lane == 6)
                    {
                        _midi.CcValues[3] = (byte)(_pass & 127);
                        capture.OutMidi(slot, in _midi);
                    }
                    else
                    {
                        _vr.Left.StickX = _pad.ThumbLX; _vr.Left.Trigger = (short)_pad.LeftTrigger;
                        capture.OutVr(slot, in _vr);
                    }
                    capture.Sent(slot, RecordingCodec.SentState.Ok, (int)VirtualControllerType.PlayStation);
                    capture.GameRumble(slot, _rumble[(_pass >> 4) & 1]);
                }
                capture.Rest(0);
                if ((_pass & 255) == 0)
                {
                    capture.Stall(30, 0, 55);
                    capture.Gap(Stopwatch.Frequency / 50, 1000);
                    capture.Mark();
                }
                long now = capture.Ticks();
                if (capture.SecondPhase(now) == 2) capture.Second(now, 1000, 1000, 0, 1000, 10, 0, 0, _steps);
                capture.EndPass();
                _pass++;
                if (_drain && (_pass & 31) == 0) capture.Ring.Drain(_drained);
            }
        }

        private static Kind[] Every(int count) => Enumerable.Repeat(Kind.Everything, count).ToArray();

        [Fact]
        public void TheCapturesSideOfAPass_AllocatesNothing()
        {
            var bench = new Bench(new RecordingCapture(), Every(8), 8, drain: true);
            var clean = AllocationWindow.Clean(bench.Pass, 256);
            Assert.Equal(new long[] { 0, 0, 0 }, clean);
            Assert.Equal(0, bench.Capture.Ring.Lost);

            // The positive control: the same window sees an allocation.
            var counted = AllocationWindow.Clean(() => { bench.Pass(); AllocationWindow.Kept = new object(); }, 256);
            Assert.All(counted, bytes => Assert.True(bytes >= 256 * 24, bytes + " bytes"));
        }

        [Fact]
        public void TheCapturesSideOfAPass_TakesNoLock()
        {
            // Source text: the capture and the engine's taps never lock,
            // wait or sleep. The diagnostics ring's lock, which the taker of
            // its lines runs under, is the ring's own and was there before.
            foreach (string file in new[] { "Common/Recording/RecordingCapture.cs", "Common/Input/InputManager.Recording.cs" })
            {
                string text = ExtrasPageTests.RepoText(new[] { "PadForge.App" }.Concat(file.Split('/')).ToArray());
                foreach (string word in new[] { "lock (", "Monitor.", "Mutex", "SemaphoreSlim", ".Wait(", "Thread.Sleep", "SpinLock" })
                    Assert.DoesNotContain(word, text);
            }
        }

        // ─────────────────────────────────────────────
        //  Measurements, printed and never judged
        // ─────────────────────────────────────────────

        /// <summary>One paced run: <paramref name="seconds"/> of passes at
        /// <paramref name="rateHz"/> on a thread of the poll thread's
        /// priority while the recorder writes, with
        /// <paramref name="load"/> threads of normal priority spinning
        /// beside it.</summary>
        private void Paced(string name, Kind[] kinds, int controllers, int rateHz, int seconds, int load, int dualSenseEvery = 1)
        {
            using var host = new RecorderTestHost();
            // The 20 MB limit would end the busiest runs within seconds.
            var recorder = new ProblemRecorder(host) { MaxBytes = 1L << 31, MaxDuration = TimeSpan.FromHours(1) };
            Assert.True(recorder.Start().Ok);
            var bench = new Bench(recorder.CaptureForTest, kinds, controllers, drain: false) { DualSenseEvery = dualSenseEvery };

            using var stop = new CancellationTokenSource();
            var spinners = new List<Thread>();
            for (int i = 0; i < load; i++)
            {
                var spinner = new Thread(() => { double x = 1; while (!stop.IsCancellationRequested) x = Math.Sqrt(x + 1); GC.KeepAlive(x); })
                { IsBackground = true, Priority = ThreadPriority.Normal, Name = "load " + i };
                spinners.Add(spinner);
                spinner.Start();
            }

            long period = Stopwatch.Frequency / rateHz;
            int total = rateHz * seconds;
            double worst = 0, sum = 0;
            var poll = new Thread(() =>
            {
                long next = Stopwatch.GetTimestamp();
                for (int i = 0; i < total; i++)
                {
                    while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(8);
                    long before = Stopwatch.GetTimestamp();
                    bench.Pass();
                    double spent = Stopwatch.GetElapsedTime(before).TotalMicroseconds;
                    sum += spent;
                    if (spent > worst) worst = spent;
                    next += period;
                }
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "bench poll" };
            long began = Stopwatch.GetTimestamp();
            poll.Start();
            poll.Join();
            double ran = Stopwatch.GetElapsedTime(began).TotalSeconds;
            stop.Cancel();
            foreach (var spinner in spinners) spinner.Join();
            long lost = bench.Capture.Ring.Lost;
            string path = recorder.Stop(wait: true).Path;
            long bytes = EventBytes(path);
            _output.WriteLine($"{name}: {rateHz} Hz, {kinds.Length} devices, {controllers} controllers, {load} spinning threads: "
                + $"{total} passes in {ran:F2} s, capture {sum / total:F2} us a pass on average and {worst:F1} us at most, "
                + $"{lost} changes left out, events {bytes / ran / 1024.0 / 1024.0 * 60:F2} MB a minute");
        }

        /// <summary>The bytes of a file's events part.</summary>
        private static long EventBytes(string path)
        {
            long bytes = 0;
            bool events = false;
            using var text = RecordingReader.OpenText(path);
            string line;
            while ((line = text.ReadLine()) != null)
            {
                if (line == RecordingCodec.EventsHeader) { events = true; continue; }
                if (line == RecordingCodec.EndHeader) break;
                if (events) bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 2;
            }
            return bytes;
        }

        /// <summary>A minute of a session, written as fast as the capture
        /// takes it, with time moved on by the test so the bytes are the
        /// file's own: the size a minute makes, plain and through deflate at
        /// its fastest and its optimal level.</summary>
        private void Minute(string name, Kind[] kinds, int controllers, int dualSenseEvery)
        {
            using var host = new RecorderTestHost();
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp(), MaxBytes = 1L << 31, MaxDuration = TimeSpan.FromHours(1) };
            Assert.True(recorder.Start().Ok);
            var bench = new Bench(recorder.CaptureForTest, kinds, controllers, drain: false) { DualSenseEvery = dualSenseEvery };
            for (int i = 0; i < 60_000; i++)
            {
                bench.Pass();
                recorder.CaptureForTest.Advance(0.001);
                if (i % 1_000 == 999) recorder.WaitForFlush();
            }
            string path = recorder.Stop(wait: true).Path;
            byte[] plain = System.Text.Encoding.UTF8.GetBytes(RecorderTestKit.Text(path));
            static long Deflated(byte[] plain, System.IO.Compression.CompressionLevel level, out double ms)
            {
                var counted = new ByteCounter();
                long began = Stopwatch.GetTimestamp();
                using (var deflate = new System.IO.Compression.DeflateStream(counted, level, leaveOpen: true))
                    deflate.Write(plain, 0, plain.Length);
                ms = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                return counted.Length;
            }
            foreach (var level in new[] { System.IO.Compression.CompressionLevel.Fastest, System.IO.Compression.CompressionLevel.Optimal, System.IO.Compression.CompressionLevel.SmallestSize })
            {
                long size = Deflated(plain, level, out double ms);
                _output.WriteLine($"{name}, one minute: plain {plain.Length / 1048576.0:F2} MB, {level} {size / 1048576.0:F2} MB "
                    + $"({(double)plain.Length / size:F1}x, {ms:F0} ms)");
            }
        }

        /// <summary>A session recorded with the real limits until one ends
        /// it, with time moved on by the test: how long it records, which
        /// limit ended it, and the zip it leaves.</summary>
        private void UntilALimit(string name, Kind[] kinds, int controllers, int dualSenseEvery)
        {
            using var host = new RecorderTestHost();
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            var bench = new Bench(capture, kinds, controllers, drain: false) { DualSenseEvery = dualSenseEvery };
            int passes = 0;
            while (recorder.State.Recording && passes < 11 * 60_000)
            {
                bench.Pass();
                capture.Advance(0.001);
                if (++passes % 1_000 == 0) recorder.WaitForFlush();
            }
            recorder.WaitForFlush();
            var until = DateTime.UtcNow.AddMinutes(2);
            while ((recorder.State.Recording || recorder.State.Finishing) && DateTime.UtcNow < until) Thread.Sleep(20);
            string zip = recorder.State.LastFile;
            string status = RecorderTestKit.Lines(zip)[1];
            _output.WriteLine($"{name}: recorded {passes / 1000.0 / 60:F2} minutes, zip {new FileInfo(zip).Length / 1048576.0:F2} MB, "
                + $"text {System.Text.Encoding.UTF8.GetByteCount(RecorderTestKit.Text(zip)) / 1048576.0:F2} MB, {status}");
        }

        [Fact]
        public void MeasureTheCost()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PADFORGE_RECORDER_MEASURE"))) return;

            var bench = new Bench(new RecordingCapture(), Every(8), 8, drain: true);
            for (int i = 0; i < 5_000; i++) bench.Pass();
            const int passes = 50_000;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < passes; i++) bench.Pass();
            double on = Stopwatch.GetElapsedTime(start).TotalMicroseconds / passes;
            int seen = 0;
            start = Stopwatch.GetTimestamp();
            for (int i = 0; i < passes; i++)
                for (int tap = 0; tap < 16; tap++)
                    if (RecordingCapture.Active != null) seen++;
            double off = Stopwatch.GetElapsedTime(start).TotalMicroseconds / passes;
            _output.WriteLine($"tight loop, eight devices and eight controllers changing every field every pass: {on:F2} us a pass recording, {off * 1000:F1} ns a pass not");
            GC.KeepAlive(seen);

            var heavy = new[] { Kind.DualSense, Kind.DualSense, Kind.Mouse, Kind.Keyboard };
            int load = Environment.ProcessorCount;
            Paced("heavy session", heavy, 4, 1_000, 10, 0);
            Paced("heavy session under load", heavy, 4, 1_000, 10, load);
            Paced("heavy session under load", heavy, 4, 1_000, 10, 4);
            Paced("every field of 16 devices and 16 controllers", Every(16), 16, 10_000, 3, 0);

            UntilALimit("heavy session until a limit", heavy, 4, 1);
            UntilALimit("one moving DualSense at 1000 reports a second until a limit", new[] { Kind.DualSense }, 0, 1);
            UntilALimit("one moving DualSense at 250 reports a second until a limit", new[] { Kind.DualSense }, 0, 4);
            Minute("heavy session", heavy, 4, 1);
            Minute("one moving DualSense, 1000 reports a second", new[] { Kind.DualSense }, 0, 1);
            Minute("one moving DualSense, 250 reports a second", new[] { Kind.DualSense }, 0, 4);
        }
    }
}
