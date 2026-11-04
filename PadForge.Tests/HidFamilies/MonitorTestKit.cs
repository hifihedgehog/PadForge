using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests.MonitorFamilies
{
    /// <summary>A scripted monitor channel (#557): it logs every exchange in
    /// order, "O" an output report, "F" a feature report, "I" an input
    /// report read (its wait in milliseconds), "R" an input report asked for
    /// over the control pipe, "W" a wait the session took, and answers the
    /// reads from queues. It reads input reports on request
    /// (<see cref="IHidInputReportReader"/>) only when
    /// <see cref="RequestedReads"/> is set.</summary>
    internal class MonitorChannel : IHidReportChannel
    {
        public readonly List<(string Kind, byte[] Bytes)> Log = new();
        /// <summary>Input reports waiting now.</summary>
        public readonly Queue<byte[]> Inputs = new();

        /// <summary>Input reports the device sends in answer, one after each
        /// output report.</summary>
        public readonly Queue<byte[]> Answers = new();

        public int FailAfter = -1;
        private int _writes;

        public string Path { get; init; } = @"\\?\hid#monitor";
        public bool FeatureOnly => false;
        public int InputReportLength { get; init; } = 65;
        public int OutputReportLength { get; init; } = 65;
        public int FeatureReportLength { get; init; } = 65;

        private bool Fails() => FailAfter >= 0 && _writes++ >= FailAfter;

        public bool SetFeature(byte[] report)
        {
            if (Fails()) return false;
            Log.Add(("F", (byte[])report.Clone()));
            return true;
        }

        public bool GetFeature(byte reportId, byte[] buffer) => false;

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (Fails()) return false;
            Log.Add(("O", (byte[])report.Clone()));
            if (Answers.Count > 0) Inputs.Enqueue(Answers.Dequeue());
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            Log.Add(("I", BitConverter.GetBytes(timeoutMs)));
            if (Inputs.Count == 0) return 0;
            var answer = Inputs.Dequeue();
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return Math.Min(answer.Length, buffer.Length);
        }

        public void Dispose() { }

        public void Wait(int ms) => Log.Add(("W", BitConverter.GetBytes(ms)));

        public List<byte[]> Sent(string kind) => Log.Where(e => e.Kind == kind).Select(e => e.Bytes).ToList();

        /// <summary>The kinds in order, a wait or a read with its
        /// milliseconds.</summary>
        public string Kinds() => string.Join(" ", Log.Select(e => e.Kind is "W" or "I"
            ? e.Kind + BitConverter.ToInt32(e.Bytes, 0).ToString(CultureInfo.InvariantCulture)
            : e.Kind));
    }

    /// <summary>A <see cref="MonitorChannel"/> that also reads input reports
    /// on request, from <see cref="Requested"/>.</summary>
    internal sealed class RequestingMonitorChannel : MonitorChannel, IHidInputReportReader
    {
        public readonly Queue<byte[]> Requested = new();

        public bool GetInputReport(byte reportId, byte[] buffer)
        {
            Log.Add(("R", new[] { reportId, (byte)buffer.Length }));
            if (Requested.Count == 0) return false;
            var answer = Requested.Dequeue();
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return true;
        }
    }

    internal static class MonitorTest
    {
        public static string Hex(byte[] bytes, int from = 0, int count = int.MaxValue)
            => BitConverter.ToString(bytes, from, Math.Min(count, bytes.Length - from)).Replace('-', ' ');

        public static byte[] Bytes(string hex)
            => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

        /// <summary>A report of <paramref name="length"/> bytes: the head,
        /// then <paramref name="fill"/> to the end.</summary>
        public static string Report(string head, int length, byte fill = 0x00)
        {
            var bytes = Enumerable.Repeat(fill, length).ToArray();
            Bytes(head).CopyTo(bytes, 0);
            return Hex(bytes);
        }

        /// <summary>A USB collection a registration answers to, with every
        /// report length given.</summary>
        public static HidCollectionInfo CollectionOf(OpenRgbRegistration r, ushort output = 512, ushort feature = 512,
            ushort input = 512)
        {
            int iface = r.Interface == OpenRgbRegistration.Any ? 0 : r.Interface;
            return new HidCollectionInfo
            {
                Path = $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}&mi_{iface:x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = (ushort)r.VendorId,
                ProductId = (ushort)r.ProductId,
                InterfaceNumber = iface,
                UsagePage = (ushort)(r.UsagePage == OpenRgbRegistration.Any ? 0xFF00 : r.UsagePage),
                Usage = (ushort)(r.Usage == OpenRgbRegistration.Any ? 0x01 : r.Usage),
                OutputReportLength = output,
                FeatureReportLength = feature,
                InputReportLength = input,
            };
        }
    }
}
