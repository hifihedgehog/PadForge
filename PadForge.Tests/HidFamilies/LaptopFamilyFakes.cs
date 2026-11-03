using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>A HID channel that logs every exchange in order and answers
    /// each feature read from a script (#557). A log entry is "F" and the
    /// bytes for a feature report sent, "O" and the bytes for an output
    /// report, "G" and the report ID for a feature read, "W" and the
    /// milliseconds for a wait the session took through <see cref="Wait"/>.</summary>
    internal sealed class LoggingHidChannel : IHidReportChannel
    {
        public readonly List<(string Kind, byte[] Bytes)> Log = new();

        /// <summary>The answers to feature reads, in turn. A read with none
        /// left answers from <see cref="Echo"/>, or fails without it.</summary>
        public readonly Queue<byte[]> Answers = new();

        /// <summary>Answers a feature read from the last feature report sent,
        /// when set and no scripted answer is left.</summary>
        public Func<byte[], byte[]> Echo;

        public bool FailWrites;
        public int FailAfter = -1;
        public bool Disposed;

        public string Path { get; init; } = @"\\?\hid#test";
        public bool FeatureOnly => false;
        public int InputReportLength { get; init; }
        public int OutputReportLength { get; init; } = 65;
        public int FeatureReportLength { get; init; } = 64;

        private int _writes;

        private bool Fails()
        {
            if (FailWrites) return true;
            return FailAfter >= 0 && _writes++ >= FailAfter;
        }

        public bool SetFeature(byte[] report)
        {
            if (Fails()) return false;
            Log.Add(("F", (byte[])report.Clone()));
            return true;
        }

        public bool GetFeature(byte reportId, byte[] buffer)
        {
            Log.Add(("G", new[] { reportId }));
            byte[] answer = Answers.Count > 0 ? Answers.Dequeue() : null;
            if (answer == null && Echo != null)
            {
                var last = Log.LastOrDefault(e => e.Kind == "F").Bytes;
                answer = last == null ? null : Echo(last);
            }
            if (answer == null) return false;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return true;
        }

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (Fails()) return false;
            Log.Add(("O", (byte[])report.Clone()));
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs) => -1;

        public void Dispose() => Disposed = true;

        /// <summary>A wait that is logged and takes no time.</summary>
        public void Wait(int ms) => Log.Add(("W", BitConverter.GetBytes(ms)));

        public IEnumerable<byte[]> Sent(string kind) => Log.Where(e => e.Kind == kind).Select(e => e.Bytes);

        /// <summary>The log as lines, "F 02 01 ..." with the bytes up to
        /// <paramref name="count"/>, "W 60" for a wait.</summary>
        public List<string> Lines(int count = 16)
            => Log.Select(e => e.Kind == "W"
                    ? "W " + BitConverter.ToInt32(e.Bytes, 0).ToString(CultureInfo.InvariantCulture)
                    : e.Kind + " " + LaptopFamilyTest.Hex(e.Bytes, count))
                .ToList();
    }

    /// <summary>What the family tests share (#557).</summary>
    internal static class LaptopFamilyTest
    {
        public static string Hex(byte[] bytes, int count = int.MaxValue)
            => BitConverter.ToString(bytes, 0, Math.Min(count, bytes.Length)).Replace('-', ' ');

        public static byte[] Bytes(string hex)
            => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

        /// <summary>A buffer of <paramref name="length"/> bytes that starts
        /// with <paramref name="head"/> and is zero after it.</summary>
        public static byte[] Padded(string head, int length)
        {
            var bytes = new byte[length];
            Bytes(head).CopyTo(bytes, 0);
            return bytes;
        }

        /// <summary>A USB collection a registration answers to, as hidapi
        /// would list it: the IDs, the usage page and usage (0xFF00 and 1
        /// where the registration takes any), and the interface in the
        /// path where the registration names one.</summary>
        public static HidCollectionInfo CollectionOf(OpenRgbRegistration r, ushort feature = 0, ushort output = 0)
        {
            int iface = r.Interface;
            string mi = iface >= 0 ? "&mi_" + iface.ToString("X2", CultureInfo.InvariantCulture) : "";
            return new HidCollectionInfo
            {
                Path = $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}{mi}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = (ushort)r.VendorId,
                ProductId = (ushort)r.ProductId,
                UsagePage = (ushort)(r.UsagePage == OpenRgbRegistration.Any ? 0xFF00 : r.UsagePage),
                Usage = (ushort)(r.Usage == OpenRgbRegistration.Any ? 1 : r.Usage),
                InterfaceNumber = iface,
                FeatureReportLength = feature,
                OutputReportLength = output,
            };
        }

        /// <summary>The lights' colors: the light at index n gets
        /// <paramref name="color"/>(n).</summary>
        public static int[] Colors(int count, Func<int, int> color) => Enumerable.Range(0, count).Select(color).ToArray();
    }
}
