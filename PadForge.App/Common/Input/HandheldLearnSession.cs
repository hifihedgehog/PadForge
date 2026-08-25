using System;
using System.Collections.Generic;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>
    /// One Learn Button pass (issue #343). The dialog drives three timed
    /// phases: hands off (idle baseline), press and hold, release. Reports
    /// from every open vendor collection are bucketed by collection and
    /// report id per phase, and the chord engine's capture runs alongside,
    /// so whichever path the firmware uses for the button is caught, or
    /// both are. <see cref="Finish"/> runs the pure learner over the
    /// buckets and returns the candidates for the user to name.
    /// </summary>
    internal sealed class HandheldLearnSession
    {
        public enum Phase { Idle = 0, Press = 1, Release = 2, Done = 3 }

        public const int IdleMs = 1000;
        public const int PressMs = 2500;
        public const int ReleaseMs = 1000;

        /// <summary>Samples kept per bucket per phase. Enough to build a
        /// noise mask from a 1 kHz stream, small enough to stay cheap.</summary>
        private const int MaxSamplesPerBucket = 256;

        public sealed class Candidate
        {
            public string Collection;
            public string CollectionName;
            public byte ReportId;
            public int ByteIndex;
            public byte Mask;
            public byte Value;
            public VendorButtonKind Kind;

            public string Describe() => Kind == VendorButtonKind.Bit
                ? $"{CollectionName}: report {ReportId:X2}, byte {ByteIndex}, bit 0x{Mask:X2}"
                : $"{CollectionName}: report {ReportId:X2}, byte {ByteIndex} = 0x{Value:X2}";
        }

        private readonly object _lock = new();
        private readonly Dictionary<(string Key, byte ReportId), List<byte[]>>[] _phases =
        {
            new(), new(), new(),
        };
        // Last report seen per bucket before the press phase, the baseline
        // for event-style collections that stay silent while idle (the
        // ROG Ally's 0x5A report exists only while a key is down).
        private readonly Dictionary<(string Key, byte ReportId), byte[]> _lastBefore = new();
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

        private volatile Phase _phase = Phase.Idle;
        private int[] _chordKeys;

        public Phase Current => _phase;

        /// <summary>Codes the chord capture recorded, or null when no chord
        /// arrived during the session.</summary>
        public int[] ChordKeys { get { lock (_lock) return _chordKeys; } }

        public void SetPhase(Phase phase) => _phase = phase;

        public void OnChordCaptured(int[] keys)
        {
            lock (_lock)
            {
                if (keys != null && keys.Length > 0) _chordKeys = keys;
            }
        }

        /// <summary>Report callback from any open vendor reader (reader
        /// thread). Copies the bytes into the current phase's bucket.</summary>
        public void OnReport(string collectionKey, string collectionName, byte[] buffer, int length)
        {
            if (length <= 0 || buffer == null) return;
            var phase = _phase;
            if (phase == Phase.Done) return;
            var copy = new byte[length];
            Array.Copy(buffer, copy, length);
            var bucket = (collectionKey, copy[0]);
            lock (_lock)
            {
                _names[collectionKey] = collectionName;
                if (phase == Phase.Idle) _lastBefore[bucket] = copy;
                var dict = _phases[(int)phase];
                if (!dict.TryGetValue(bucket, out var list))
                    dict[bucket] = list = new List<byte[]>();
                if (list.Count < MaxSamplesPerBucket) list.Add(copy);
            }
        }

        /// <summary>Runs the learner over every bucket that received a
        /// report during the press phase.</summary>
        public List<Candidate> Finish()
        {
            _phase = Phase.Done;
            var result = new List<Candidate>();
            lock (_lock)
            {
                var idleAll = _phases[(int)Phase.Idle];
                var pressAll = _phases[(int)Phase.Press];
                var releaseAll = _phases[(int)Phase.Release];
                foreach (var kv in pressAll)
                {
                    var bucket = kv.Key;
                    var press = kv.Value;
                    if (press.Count == 0) continue;
                    idleAll.TryGetValue(bucket, out var idle);
                    releaseAll.TryGetValue(bucket, out var release);

                    byte[] baseline;
                    byte[] noise;
                    if (idle != null && idle.Count > 0)
                    {
                        baseline = idle[0];
                        noise = VendorReportLearner.NoiseMask(idle);
                    }
                    else if (_lastBefore.TryGetValue(bucket, out var last))
                    {
                        baseline = last;
                        noise = new byte[last.Length];
                    }
                    else
                    {
                        // Event-style: nothing while idle, so every byte of
                        // the press report is compared against zero.
                        baseline = new byte[press[0].Length];
                        noise = new byte[baseline.Length];
                    }
                    var found = VendorReportLearner.Learn(baseline, noise, press, release ?? new List<byte[]>());
                    string name = _names.TryGetValue(bucket.Key, out var n) ? n : bucket.Key;
                    foreach (var c in found)
                    {
                        // Byte 0 is the report id; a "button" there is the
                        // id itself flipping between report layouts.
                        if (c.ByteIndex == 0) continue;
                        result.Add(new Candidate
                        {
                            Collection = bucket.Key,
                            CollectionName = name,
                            ReportId = bucket.ReportId,
                            ByteIndex = c.ByteIndex,
                            Mask = c.Mask,
                            Value = c.Value,
                            Kind = c.Kind,
                        });
                    }
                }
            }
            return result;
        }
    }
}
