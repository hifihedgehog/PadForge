using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HIDMaestro;
using PadForge.Engine.Recording;
using PadForge.Services;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// The problem recorder's taps on threads other than the poll thread
    /// (#600): what a game writes to a virtual controller, and the rumble
    /// PadForge's own writer sends a Sony pad. Each does nothing while
    /// nothing is being captured, and otherwise queues one line when what it
    /// saw differs from the last time, so a game that repeats one report
    /// every frame costs one line.
    /// </summary>
    internal static class RecordingTaps
    {
        private const int Slots = 16, KindsPerSlot = 8;
        private const int MostBytes = 64;
        private const int MostLinesASecond = 100;

        private static readonly object s_sync = new();
        private static readonly GameReports s_reports = new();
        private static readonly Dictionary<string, int> s_written = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forgets what was last seen, so the first report and the
        /// first write of a new capture are in it.</summary>
        internal static void Reset()
        {
            s_reports.Reset();
            lock (s_sync) s_written.Clear();
        }

        /// <summary>
        /// One report a game sent a virtual controller: rumble, lights or a
        /// trigger effect, as its bytes. Written when it differs from the
        /// last report of its kind on that controller, at most a hundred
        /// times a second for one controller, with what went unwritten
        /// counted on the next line.
        /// </summary>
        internal static void GameReport(int slot, HMOutputSource source, byte reportId, ReadOnlySpan<byte> data)
        {
            if (RecordingCapture.Active == null) return;
            string line = s_reports.Line(slot, source, reportId, data, Environment.TickCount64 / 1000);
            if (line != null) ProblemRecorder.Instance.TakeTyped(RecordingCodec.LayerFeedback, slot, line);
        }

        /// <summary>
        /// What the game reports of each controller came to, and the cap on
        /// their lines. The last report of each kind is kept with whether it
        /// went unwritten: a report the cap dropped is kept apart from the
        /// one the file shows, so the first report after the cap lifts that
        /// differs from the file's is written, the dropped one coming again
        /// among them, and the file ends on the report the game last sent.
        /// </summary>
        internal sealed class GameReports
        {
            private readonly object _sync = new();
            private readonly int[] _keys = new int[Slots * KindsPerSlot];
            private readonly ulong[] _hashes = new ulong[Slots * KindsPerSlot];
            private readonly bool[] _unwritten = new bool[Slots * KindsPerSlot];
            private readonly long[] _second = new long[Slots];
            private readonly int[] _lines = new int[Slots];
            private readonly int[] _dropped = new int[Slots];

            internal void Reset()
            {
                lock (_sync)
                {
                    Array.Clear(_keys);
                    Array.Clear(_hashes);
                    Array.Clear(_unwritten);
                    Array.Clear(_second);
                    Array.Clear(_lines);
                    Array.Clear(_dropped);
                }
            }

            /// <summary>The line a report sent in the second
            /// <paramref name="second"/> gets, or null when it gets
            /// none.</summary>
            internal string Line(int slot, HMOutputSource source, byte reportId, ReadOnlySpan<byte> data, long second)
            {
                if ((uint)slot >= Slots) return null;

                ulong hash = 14695981039346656037UL;
                hash = (hash ^ (uint)data.Length) * 1099511628211UL;
                int hashed = Math.Min(data.Length, MostBytes);
                for (int i = 0; i < hashed; i++) hash = (hash ^ data[i]) * 1099511628211UL;

                int key = (((int)source + 1) << 8) | reportId;
                int dropped;
                lock (_sync)
                {
                    int from = slot * KindsPerSlot, at = -1;
                    for (int i = from; i < from + KindsPerSlot; i++)
                    {
                        if (_keys[i] == key) { at = i; break; }
                        if (_keys[i] == 0 && at < 0) at = i;
                    }
                    // More kinds than places: the first place is taken over.
                    if (at < 0) at = from;
                    bool repeat = _keys[at] == key && _hashes[at] == hash;
                    // The report the file shows, again.
                    if (repeat && !_unwritten[at]) return null;
                    _keys[at] = key;
                    _hashes[at] = hash;

                    if (second != _second[slot])
                    {
                        _second[slot] = second;
                        _lines[slot] = 0;
                    }
                    if (++_lines[slot] > MostLinesASecond)
                    {
                        // A report left unwritten counts once, however often
                        // it comes while the cap holds.
                        if (!repeat) _dropped[slot]++;
                        _unwritten[at] = true;
                        return null;
                    }
                    _unwritten[at] = false;
                    dropped = _dropped[slot];
                    _dropped[slot] = 0;
                }

                var sb = new StringBuilder(48 + hashed * 3);
                sb.Append("report src=").Append(source.ToString())
                  .Append(" id=0x").Append(reportId.ToString("X2", CultureInfo.InvariantCulture))
                  .Append(" len=").Append(data.Length.ToString(CultureInfo.InvariantCulture));
                if (dropped > 0) sb.Append(" unwritten=").Append(dropped.ToString(CultureInfo.InvariantCulture));
                sb.Append(" bytes=");
                for (int i = 0; i < hashed; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        /// <summary>A write to a device on a thread of its own, watched for
        /// a recording (#600): the capture it began in and its slot, or a slot
        /// of 0 for a write that is not watched.</summary>
        internal readonly record struct WriteCall(RecordingCapture Capture, int Slot);

        /// <summary>A Sony pad's report write begins (#600). The report
        /// carries the pad's rumble, so the write is a rumble write, named
        /// with its pad: one that takes long is written, and one that does
        /// not return is said while it hangs. Nothing while nothing records.</summary>
        internal static WriteCall BeginWrite(string devicePath) => BeginWrite(RecordingCapture.Active, devicePath);

        internal static WriteCall BeginWrite(RecordingCapture capture, string devicePath)
        {
            if (capture == null) return default;
            int slot = capture.BeginOtherCall(capture.TrackOfPath(devicePath), RecordingCodec.CallKind.Rumble);
            return slot > 0 ? new WriteCall(capture, slot) : default;
        }

        /// <summary>The write ends: one that took long is written with its
        /// pad.</summary>
        internal static void EndWrite(WriteCall call)
        {
            if (call.Capture != null) EndWrite(call, ProblemRecorder.Instance);
        }

        internal static void EndWrite(WriteCall call, ProblemRecorder recorder)
        {
            var capture = call.Capture;
            if (capture == null || call.Slot <= 0) return;
            var (track, kind, _) = capture.CallIn(call.Slot);
            long ms = capture.EndOtherCall(call.Slot);
            if (track >= 0) recorder.TakeSlowCall(capture.DeviceOfTrack(track), kind, ms);
        }

        /// <summary>The rumble PadForge's own writer just sent a DualSense or
        /// a DualShock 4, which SDL does not write for. Written when the two
        /// motors differ from the last write to that pad.</summary>
        internal static void SonyWrite(string devicePath, IReadOnlyDictionary<string, object> fields)
        {
            if (RecordingCapture.Active == null || string.IsNullOrEmpty(devicePath) || fields == null) return;
            if (!fields.TryGetValue("leftMotor", out object left) || left is not byte leftMotor) return;
            if (!fields.TryGetValue("rightMotor", out object right) || right is not byte rightMotor) return;
            // The pad's motors take a byte each. The file writes every
            // device's rumble on the one scale of 0 to 65535.
            int packed = (leftMotor * 257) | ((rightMotor * 257) << 16);
            lock (s_sync)
            {
                if (s_written.TryGetValue(devicePath, out int last) && last == packed) return;
                s_written[devicePath] = packed;
            }
            ProblemRecorder.Instance.TakeDeviceWrite(devicePath, packed);
        }
    }
}
