using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        /// <summary>The parts of a pass the heartbeat tells apart, in the
        /// order a pass runs them.</summary>
        private enum PollStep
        {
            /// <summary>SDL reads every device it holds.</summary>
            Sdl,
            /// <summary>The sweep for devices that came or went, every two
            /// seconds.</summary>
            Sweep,
            /// <summary>Each device's state is copied out of its reader.</summary>
            Read,
            /// <summary>The gyro, trigger-route and haptic-mirror engage
            /// states, and the motion snapshots.</summary>
            Engage,
            /// <summary>The mappings: each slot's rows evaluated, and the
            /// devices of a slot combined.</summary>
            Map,
            /// <summary>The macros, the On-Screen Keyboard and the keys the
            /// hooks hold back.</summary>
            Macros,
            /// <summary>The motion rows, and the motion sent to DSU clients.</summary>
            Motion,
            /// <summary>The virtual controllers are fed.</summary>
            Virtual,
            /// <summary>Their feedback is read back, and the rumble lane.</summary>
            Output,
        }

        /// <summary>
        /// What the poll loop's passes cost, summed between two heartbeats and
        /// written on the heartbeat's line. The loop's rate is a setting, and
        /// what a rate costs is a pass's cost times the rate, so the line says
        /// what a pass costs and where it goes, in time and in memory handed
        /// to the collector, and how long after their times the passes
        /// started.
        ///
        /// <para>Poll thread only. A pass pays for one clock read and one
        /// read of the thread's allocation count at each part's end, about a
        /// third of a microsecond in all. The processor share is the thread's
        /// own cycle count, which Windows keeps exactly, where its thread
        /// times are sampled on the clock's tick and miss a thread that wakes
        /// and sleeps between two ticks.</para>
        /// </summary>
        private sealed class PollLoad
        {
            private static readonly string[] s_names = Enum.GetNames<PollStep>();

            private readonly long[] _ticks = new long[s_names.Length];
            private readonly long[] _bytes = new long[s_names.Length];
            private readonly StringBuilder _line = new(512);

            // The same sums kept apart for a problem recording (#600), which
            // takes and clears them once a second.
            private readonly long[] _recordedTicks = new long[s_names.Length];
            private long _recordedPasses, _recordedSleeps, _recordedWork, _recordedLongest, _recordedLate, _recordedLatest;
            private long _start, _passes, _sleeps, _work, _longest, _late, _latest;
            private long _passStart, _last, _lastBytes;
            private ulong _cycles;
            private int _gen0, _gen1, _gen2;
            private TimeSpan _gcPause;

            /// <summary>Starts a window at <paramref name="now"/>.</summary>
            internal void Begin(long now)
            {
                Array.Clear(_ticks);
                Array.Clear(_bytes);
                _passes = _sleeps = _work = _longest = _late = _latest = 0;
                _start = now;
                QueryThreadCycleTime(GetCurrentThread(), out _cycles);
                _gen0 = GC.CollectionCount(0);
                _gen1 = GC.CollectionCount(1);
                _gen2 = GC.CollectionCount(2);
                _gcPause = GC.GetTotalPauseDuration();
            }

            /// <summary>A pass starts, <paramref name="late"/> ticks after
            /// it was due (<see cref="PollSchedule.Due"/>).</summary>
            internal void BeginPass(long now, long late)
            {
                _passStart = _last = now;
                _lastBytes = GC.GetAllocatedBytesForCurrentThread();
                if (late > 0)
                {
                    _late += late;
                    if (late > _latest) _latest = late;
                    _recordedLate += late;
                    if (late > _recordedLatest) _recordedLatest = late;
                }
            }

            /// <summary>A part of the pass just ended.</summary>
            internal void Mark(PollStep step)
            {
                long now = Stopwatch.GetTimestamp();
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                _ticks[(int)step] += now - _last;
                _recordedTicks[(int)step] += now - _last;
                _bytes[(int)step] += bytes - _lastBytes;
                _last = now;
                _lastBytes = bytes;
            }

            /// <summary>The pass ended with its last part.</summary>
            internal void EndPass()
            {
                _passes++;
                long work = _last - _passStart;
                _work += work;
                if (work > _longest) _longest = work;
                _recordedPasses++;
                _recordedWork += work;
                if (work > _recordedLongest) _recordedLongest = work;
            }

            /// <summary>Hands the sums since the last take to a problem
            /// recording as one second's timing, and clears them. With no
            /// recording to take them they are cleared alone, which a
            /// recording's first pass asks for.</summary>
            internal void TakeRecorded(Recording.RecordingCapture recording, long now, int rateHz)
            {
                recording?.Second(now, rateHz, _recordedPasses, _recordedSleeps, _recordedWork, _recordedLongest,
                    _recordedLate, _recordedLatest, _recordedTicks);
                Array.Clear(_recordedTicks);
                _recordedPasses = _recordedSleeps = _recordedWork = _recordedLongest = _recordedLate = _recordedLatest = 0;
            }

            /// <summary>The thread slept between two passes.</summary>
            internal void Slept()
            {
                _sleeps++;
                _recordedSleeps++;
            }

            /// <summary>The heartbeat's line took <paramref name="ticks"/>
            /// to write. The next line says so.</summary>
            internal void LineWritten(long ticks) => _lineTicks = ticks;

            private long _lineTicks;

            /// <summary>The window's figures as the heartbeat writes them, or
            /// nothing for a window with no pass. Starts the next window.</summary>
            internal string Report(long now)
            {
                if (_passes == 0)
                {
                    Begin(now);
                    return string.Empty;
                }
                double seconds = Math.Max(1e-3, (now - _start) / (double)Stopwatch.Frequency);
                double microseconds = 1e6 / Stopwatch.Frequency / _passes;
                QueryThreadCycleTime(GetCurrentThread(), out ulong cycles);
                double rate = CyclesPerSecond;
                long bytes = 0;
                foreach (long part in _bytes) bytes += part;

                var culture = CultureInfo.InvariantCulture;
                _line.Clear();
                _line.Append(culture, $" load passes={_passes / seconds:F0}/s sleeps={_sleeps / seconds:F0}/s");
                _line.Append(culture, $" work={_work * microseconds:F1}us longest={_longest * 1e6 / Stopwatch.Frequency:F0}us");
                // How long after its time a pass started: the mean, and the
                // worst one.
                _line.Append(culture, $" late={_late * microseconds:F0}us latest={_latest * 1e6 / Stopwatch.Frequency:F0}us");
                // The collections the whole program ran in the window, by
                // generation, and how long they held every thread: a pass
                // due while one runs starts when it ends.
                // CollectionCount(0) counts every collection and (1) the
                // generation 1 and 2 ones, so each is told apart here.
                int gen2 = GC.CollectionCount(2) - _gen2;
                int gen1 = GC.CollectionCount(1) - _gen1 - gen2;
                int gen0 = GC.CollectionCount(0) - _gen0 - gen1 - gen2;
                _line.Append(culture, $" gc={gen0}/{gen1}/{gen2} pause={(GC.GetTotalPauseDuration() - _gcPause).TotalMilliseconds:F1}ms");
                // What the line before this one took to write, which the
                // window's first pass waited out.
                _line.Append(culture, $" line={_lineTicks * 1e6 / Stopwatch.Frequency:F0}us");
                if (rate > 0) _line.Append(culture, $" thread={100.0 * (cycles - _cycles) / (rate * seconds):F2}%");
                _line.Append(culture, $" alloc={bytes / (double)_passes:F0}B");
                // Each part: its microseconds a pass, and its bytes a pass
                // where it handed the collector any.
                for (int i = 0; i < s_names.Length; i++)
                {
                    _line.Append(culture, $" {s_names[i]}={_ticks[i] * microseconds:F1}");
                    if (_bytes[i] != 0) _line.Append(culture, $"/{_bytes[i] / (double)_passes:F0}B");
                }
                Begin(now);
                return _line.ToString();
            }

            /// <summary>The processor's cycles in a second, as Windows
            /// records its rated speed, or 0 where it does not say.</summary>
            private static double CyclesPerSecond => s_cyclesPerSecond ??= ReadCyclesPerSecond();

            private static double? s_cyclesPerSecond;

            private static double ReadCyclesPerSecond()
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    return key?.GetValue("~MHz") is int megahertz && megahertz > 0 ? megahertz * 1e6 : 0;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
                {
                    return 0;
                }
            }
        }

        private readonly PollLoad _pollLoad = new();
    }
}
