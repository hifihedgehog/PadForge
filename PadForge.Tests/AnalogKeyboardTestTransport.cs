using System;
using System.Collections.Generic;
using PadForge.Engine.Common.AnalogKeyboard;

namespace PadForge.Tests
{
    /// <summary>
    /// A scripted analog keyboard for route tests (issue #468). Each write
    /// the route makes is logged, and the matching script function may queue
    /// replies: input reports for <see cref="Receive"/>, feature reports for
    /// <see cref="GetFeature"/>. Buffers carry the report ID in byte 0, as
    /// Windows hands them over, 0 for an unnumbered report.
    /// </summary>
    internal sealed class AnalogKeyboardTestTransport : IAnalogKeyboardTransport
    {
        private readonly Queue<byte[]> _input = new();
        private readonly Queue<byte[]> _features = new();

        public int InputLength { get; set; } = 65;
        public int OutputLength { get; set; } = 65;
        public int FeatureLength { get; set; } = 65;

        /// <summary>The device went away: writes fail and reads say so.</summary>
        public bool Gone { get; set; }

        /// <summary>Every write in order: "out" for WriteFile, "ctl" for
        /// HidD_SetOutputReport, "setf" for a feature write, "getf" for a
        /// feature read (its data is the request buffer).</summary>
        public readonly List<(string Kind, byte[] Data)> Log = new();

        public int Discards { get; private set; }

        /// <summary>Replies to a WriteFile output report, queued as input.</summary>
        public Func<byte[], IEnumerable<byte[]>> OnSend { get; set; } = _ => Array.Empty<byte[]>();

        /// <summary>Replies to a control-transfer output report, queued as input.</summary>
        public Func<byte[], IEnumerable<byte[]>> OnSendOutputReport { get; set; }

        /// <summary>Replies to a feature write, queued for the feature reads
        /// that follow.</summary>
        public Func<byte[], IEnumerable<byte[]>> OnSetFeature { get; set; } = _ => Array.Empty<byte[]>();

        /// <summary>A feature read with nothing queued: the answer for the
        /// requested report ID, or null to fail the read.</summary>
        public Func<byte, byte[]> OnGetFeature { get; set; } = _ => null;

        /// <summary>The byte count a feature read reports for an answer, by
        /// default the answer's own length.</summary>
        public Func<byte[], int> FeatureTransferred { get; set; } = answer => answer.Length;

        public void QueueInput(params byte[][] reports)
        {
            foreach (var r in reports) _input.Enqueue(r);
        }

        public void QueueFeature(params byte[][] answers)
        {
            foreach (var a in answers) _features.Enqueue(a);
        }

        public int PendingInput => _input.Count;

        public bool Send(byte[] report)
        {
            if (Gone) return false;
            Log.Add(("out", (byte[])report.Clone()));
            foreach (var r in OnSend(report)) _input.Enqueue(r);
            return true;
        }

        public bool SendOutputReport(byte[] report)
        {
            if (Gone) return false;
            Log.Add(("ctl", (byte[])report.Clone()));
            foreach (var r in (OnSendOutputReport ?? OnSend)(report)) _input.Enqueue(r);
            return true;
        }

        public int Receive(byte[] buffer, int timeoutMs)
        {
            if (Gone) return -1;
            if (_input.Count == 0) return 0;
            var r = _input.Dequeue();
            int n = Math.Min(r.Length, buffer.Length);
            Array.Copy(r, buffer, n);
            return n;
        }

        public void DiscardStale() => Discards++;

        public bool SetFeature(byte[] report)
        {
            if (Gone) return false;
            Log.Add(("setf", (byte[])report.Clone()));
            foreach (var a in OnSetFeature(report)) _features.Enqueue(a);
            return true;
        }

        public int GetFeature(byte[] buffer)
        {
            if (Gone) return -1;
            Log.Add(("getf", (byte[])buffer.Clone()));
            byte[] answer = _features.Count > 0 ? _features.Dequeue() : OnGetFeature(buffer[0]);
            if (answer == null) return -1;
            Array.Clear(buffer);
            int n = Math.Min(answer.Length, buffer.Length);
            Array.Copy(answer, buffer, n);
            return FeatureTransferred(answer);
        }

        /// <summary>The writes of one kind, in order.</summary>
        public List<byte[]> Writes(string kind)
        {
            var list = new List<byte[]>();
            foreach (var (k, data) in Log)
                if (k == kind) list.Add(data);
            return list;
        }
    }
}
