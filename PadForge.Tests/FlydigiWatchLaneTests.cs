using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Flydigi service scan walks every process on the PC, and on a PC
    /// with about 480 processes and 10,000 threads that took 230 to 290 ms.
    /// The device sweep ran it on the polling thread every 10 s, so the
    /// 1000 Hz loop stopped for that long each time: the polling rate read
    /// 700 to 750 Hz and the virtual controller hung for the length of the
    /// scan. These pin that the sweep only starts the scan, on a worker, one
    /// at a time, and that what the scan finds still reaches the rows.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class FlydigiWatchLaneTests
    {
        private static bool Running(InputManager im)
            => (bool)typeof(InputManager).GetField("_flydigiWatchRunning", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);

        private static void SetSeen(InputManager im, string value)
            => typeof(InputManager).GetField("_flydigiRunningSeen", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(im, value);

        [Fact]
        public void TheSweepDoesNotWaitForTheScan()
        {
            using var gate = new ManualResetEventSlim(false);
            int scans = 0;
            var im = new InputManager { FlydigiWatchIntervalMs = 0 };
            im.FlydigiWatchScan = () => { Interlocked.Increment(ref scans); gate.Wait(10_000); };
            try
            {
                var clock = Stopwatch.StartNew();
                im.FlydigiWatchTick(Environment.TickCount64, out _);
                Assert.True(clock.ElapsedMilliseconds < 1_000, $"the tick waited {clock.ElapsedMilliseconds} ms for the scan");
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref scans) == 1, 5_000), "the scan never started");
            }
            finally { gate.Set(); }
            Assert.True(SpinWait.SpinUntil(() => !Running(im), 5_000));
        }

        [Fact]
        public void OneScanRunsAtATime()
        {
            using var gate = new ManualResetEventSlim(false);
            int scans = 0;
            var im = new InputManager { FlydigiWatchIntervalMs = 0 };
            im.FlydigiWatchScan = () => { Interlocked.Increment(ref scans); gate.Wait(10_000); };
            try
            {
                im.FlydigiWatchTick(Environment.TickCount64, out _);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref scans) == 1, 5_000));
                for (int i = 0; i < 20; i++) im.FlydigiWatchTick(Environment.TickCount64, out _);
                Thread.Sleep(200);
                Assert.Equal(1, Volatile.Read(ref scans));
            }
            finally { gate.Set(); }
            // The next tick after the scan ends starts another. Each tick
            // from here on starts one more as soon as the last has ended, so
            // the count is two or more when it is looked at: a third scan
            // can start between the tick and the look. Asking for exactly
            // two failed once in a full run (2026-11-11), after waiting its
            // five seconds for a count that had passed two.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                im.FlydigiWatchTick(Environment.TickCount64, out _);
                return Volatile.Read(ref scans) >= 2;
            }, 5_000));
            Assert.True(SpinWait.SpinUntil(() => !Running(im), 5_000));
        }

        [Fact]
        public void ScansAreTenSecondsApart()
        {
            int scans = 0;
            var im = new InputManager();
            Assert.Equal(10_000, im.FlydigiWatchIntervalMs);
            im.FlydigiWatchScan = () => Interlocked.Increment(ref scans);

            im.FlydigiWatchTick(100_000, out _);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref scans) == 1 && !Running(im), 5_000));
            im.FlydigiWatchTick(109_999, out _);
            Thread.Sleep(100);
            Assert.Equal(1, Volatile.Read(ref scans));
            im.FlydigiWatchTick(110_000, out _);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref scans) == 2 && !Running(im), 5_000));
        }

        [Fact]
        public void ARunningSetTheRowsHaveNotHadIsReportedOnce()
        {
            var im = new InputManager();
            im.FlydigiWatchScan = () => { };
            SetSeen(im, "a set no scan reports");

            Assert.True(im.FlydigiWatchTick(Environment.TickCount64, out string running));
            Assert.Equal(FlydigiServiceWatch.Running, running);
            Assert.False(im.FlydigiWatchTick(Environment.TickCount64, out _));
            Assert.True(SpinWait.SpinUntil(() => !Running(im), 5_000));
        }

        [Fact]
        public void TheArrivalRecordWaitsForItsScanOnAWorker()
        {
            using var gate = new ManualResetEventSlim(false);
            var im = new InputManager();
            im.FlydigiWatchScan = () => gate.Wait(10_000);
            string path = "flydigi-lane-" + Guid.NewGuid().ToString("N");
            try
            {
                var clock = Stopwatch.StartNew();
                im.RecordFlydigiArrival(0x37D7, 0x2401, 7, "hidapi", path, "hidhide=test");
                Assert.True(clock.ElapsedMilliseconds < 1_000, $"the arrival waited {clock.ElapsedMilliseconds} ms for the scan");
                Thread.Sleep(100);
                Assert.DoesNotContain(path, SdlDiagLog.Snapshot());
            }
            finally { gate.Set(); }
            Assert.True(SpinWait.SpinUntil(() => SdlDiagLog.Snapshot().Contains(path), 5_000), "the arrival record was never written");
            string snapshot = SdlDiagLog.Snapshot();
            string line = snapshot.Substring(snapshot.IndexOf("FLYDIGI arrival 37D7:2401 sdl=7", StringComparison.Ordinal));
            Assert.Contains("hidhide=test", line.Substring(0, line.IndexOf('\n') < 0 ? line.Length : line.IndexOf('\n')));
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        /// <summary>The idle branch timed SDL and not the device sweep, so the
        /// 250 ms process walk wrote no STALL line until a slot came online.
        /// The idle sweep now writes one past 25 ms, as the active sweep
        /// does.</summary>
        [Fact]
        public void TheIdleBranchTimesItsDeviceSweep()
        {
            string loop = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.cs")).Replace("\r\n", "\n");
            int idle = loop.IndexOf("if (BeginIdlePoll())", StringComparison.Ordinal);
            Assert.True(idle > 0);
            int active = loop.IndexOf("cycleTimer.Restart();", idle, StringComparison.Ordinal);
            Assert.True(active > idle);
            string branch = loop.Substring(idle, active - idle);
            Assert.Contains("long tsIdleEnum = Stopwatch.GetTimestamp();\n                                UpdateDevices();\n", branch);
            Assert.Contains("if (idleEnumMs >= 25)\n                                    Engine.SdlDiagLog.WriteLine($\"STALL idle enum={idleEnumMs}ms\");", branch);
            // The active sweep keeps its own timing at the same bar.
            Assert.Contains("if (sdlMs >= 25 || enumMs >= 25 || cycleMs >= 50)", loop);
        }

        /// <summary>The heartbeat tells a live loop from a dead one, so every
        /// branch of the loop writes it. The idle and focus-suspended branches
        /// went back to the loop's top before the active branch's write, so an
        /// idle engine logged no heartbeat at all and read as a dead
        /// loop.</summary>
        [Fact]
        public void EveryBranchOfThePollLoopWritesTheHeartbeat()
        {
            string loop = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.cs")).Replace("\r\n", "\n");
            int idle = loop.IndexOf("if (BeginIdlePoll())", StringComparison.Ordinal);
            int focus = loop.IndexOf("if (ApplyFocusSuspension())", Math.Max(idle, 0), StringComparison.Ordinal);
            int active = loop.IndexOf("cycleTimer.Restart();", Math.Max(focus, 0), StringComparison.Ordinal);
            int end = loop.IndexOf("// Frequency measurement.", Math.Max(active, 0), StringComparison.Ordinal);
            Assert.True(idle > 0 && focus > idle && active > focus && end > active, "the poll loop moved");
            Assert.Contains("WritePollHeartbeat((Stopwatch.GetTimestamp() - tsIdleCycle) * 1000 / Stopwatch.Frequency);\n                        Thread.Sleep(50);",
                loop.Substring(idle, focus - idle));
            Assert.Contains("WritePollHeartbeat(0);\n                        Thread.Sleep(100);", loop.Substring(focus, active - focus));
            Assert.Contains("WritePollHeartbeat(cycleMs);", loop.Substring(active, end - active));
        }
    }
}
