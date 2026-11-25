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
    /// (#597): what a game writes to a virtual controller, and the rumble
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
        private static readonly int[] s_keys = new int[Slots * KindsPerSlot];
        private static readonly ulong[] s_hashes = new ulong[Slots * KindsPerSlot];
        private static readonly long[] s_second = new long[Slots];
        private static readonly int[] s_lines = new int[Slots];
        private static readonly int[] s_dropped = new int[Slots];
        private static readonly Dictionary<string, int> s_written = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forgets what was last seen, so the first report and the
        /// first write of a new capture are in it.</summary>
        internal static void Reset()
        {
            lock (s_sync)
            {
                Array.Clear(s_keys);
                Array.Clear(s_hashes);
                Array.Clear(s_second);
                Array.Clear(s_lines);
                Array.Clear(s_dropped);
                s_written.Clear();
            }
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
            if (RecordingCapture.Active == null || (uint)slot >= Slots) return;

            ulong hash = 14695981039346656037UL;
            hash = (hash ^ (uint)data.Length) * 1099511628211UL;
            int hashed = Math.Min(data.Length, MostBytes);
            for (int i = 0; i < hashed; i++) hash = (hash ^ data[i]) * 1099511628211UL;

            int key = (((int)source + 1) << 8) | reportId;
            int dropped;
            lock (s_sync)
            {
                int from = slot * KindsPerSlot, at = -1;
                for (int i = from; i < from + KindsPerSlot; i++)
                {
                    if (s_keys[i] == key) { at = i; break; }
                    if (s_keys[i] == 0 && at < 0) at = i;
                }
                // More kinds than places: the first place is taken over.
                if (at < 0) at = from;
                if (s_keys[at] == key && s_hashes[at] == hash) return;
                s_keys[at] = key;
                s_hashes[at] = hash;

                long second = Environment.TickCount64 / 1000;
                if (second != s_second[slot])
                {
                    s_second[slot] = second;
                    s_lines[slot] = 0;
                }
                if (++s_lines[slot] > MostLinesASecond)
                {
                    s_dropped[slot]++;
                    return;
                }
                dropped = s_dropped[slot];
                s_dropped[slot] = 0;
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
            ProblemRecorder.Instance.TakeTyped(RecordingCodec.LayerFeedback, slot, sb.ToString());
        }

        /// <summary>The rumble PadForge's own writer just sent a DualSense or
        /// a DualShock 4, which SDL does not write for. Written when the two
        /// motors differ from the last write to that pad.</summary>
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
