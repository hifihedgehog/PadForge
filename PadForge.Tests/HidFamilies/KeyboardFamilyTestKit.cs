using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Tests
{
    /// <summary>What the keyboard family tests share (#557): a collection a
    /// registration answers to, hex for comparing frames, and a channel
    /// that records what a session sends.</summary>
    internal static class KeyboardFamilyTestKit
    {
        /// <summary>A USB collection that compares equal to
        /// <paramref name="r"/> on every field it names, with the report
        /// lengths given.</summary>
        public static HidCollectionInfo CollectionFor(OpenRgbRegistration r, ushort output = 0, ushort feature = 0,
            ushort input = 0)
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

        /// <summary>The first <paramref name="count"/> bytes, or all of them,
        /// as upper-case hex pairs.</summary>
        public static string Hex(byte[] bytes, int count = -1)
            => string.Join(" ", bytes.Take(count < 0 ? bytes.Length : count).Select(b => b.ToString("X2")));

        /// <summary>A report as hex pairs, its bytes written out one by one
        /// at the offsets given and zero elsewhere.</summary>
        public static byte[] Bytes(int length, params (int At, byte Value)[] bytes)
        {
            var report = new byte[length];
            foreach (var (at, value) in bytes) report[at] = value;
            return report;
        }

        /// <summary>The calls a channel recorded, as kind and bytes.</summary>
        public static List<(string Kind, byte[] Bytes)> Calls(FakeReportChannel io)
            => io.Calls.Select(c => (c.Kind, c.Bytes)).ToList();

        /// <summary>One color per light, each different.</summary>
        public static int[] Distinct(int count)
        {
            var colors = new int[count];
            for (int i = 0; i < count; i++) colors[i] = 0x010203 * (i + 1) & 0xFFFFFF;
            return colors;
        }

        /// <summary>A wait that records the waits a session takes.</summary>
        public static Action<int> Recorder(List<int> waits) => ms => waits.Add(ms);
    }
}
