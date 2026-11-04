using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The HYTE Keeb TKL keyboard (#557), lit light by light as OpenRGB's
    /// HYTEKeyboardController lights it: 3402:0300 on usage page 0xFF11,
    /// usage 0xF0 (HYTEKeyboardControllerDetect.cpp:20-25 and 45). Its keys
    /// are OpenRGB's hyte_keeb_tkl_layout with its edits, each at its LED
    /// value, and its 63 underglow lights follow as edge lights at
    /// <see cref="UnderglowBase"/> plus their place (RGBController_HYTEKeyboard.cpp:18-58
    /// and 87-138).
    ///
    /// <para>The model of the device. OpenRGB has only a Direct mode for it
    /// and sends nothing ahead of it (lines 70-75). A picture streams the
    /// keys and then the underglow: for the keys, the feature report 00 04
    /// F0 of 9 bytes and six 65-byte output reports with report ID 0, each
    /// carrying 64 bytes of red, green and blue for the LED values 0 to 127
    /// in order; for the underglow, 00 04 F1 and three such reports for its
    /// 63 lights (LEDStreaming_Keyboard and LEDStreaming_Surround,
    /// HYTEKeyboardController.cpp:55-201; DeviceUpdateLEDs, RGBController_HYTEKeyboard.cpp:140-165).
    /// The last color of each stream, past OpenRGB's lights, is whatever
    /// OpenRGB's memory holds there, and black here. The Direct mode does
    /// not save (its flags, line 73).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back to the keyboard's own lighting, so the keyboard shows
    /// PadForge's last picture until it or another program sets one. No
    /// source names a program that lights it.</para>
    /// </summary>
    internal sealed class HyteKeyboardLighting : IHidLightingFamily
    {
        public static readonly HyteKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "HYTEKeyboardController";
        internal const ushort VendorId = 0x3402;

        internal const int FeatureSize = 9;
        internal const int PacketSize = 65;

        /// <summary>The address of the first underglow light: past the 128
        /// colors of the key stream.</summary>
        internal const int UnderglowBase = 128;
        internal const int UnderglowLights = 63;

        /// <summary>The colors each stream carries: 6 and 3 reports of 64
        /// bytes (lines 59-122 and 136-196).</summary>
        internal const int KeyPages = 6;
        internal const int UnderglowPages = 3;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "HYTE";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        /// <summary>The keys of hyte_keeb_tkl_layout (line 89-90: the
        /// default layout at the TKL size with the edit list), then the
        /// underglow.</summary>
        internal static List<LightInfo> Lights()
        {
            var lights = KeyboardLedNames.FromLayout(KeyboardLayouts.Build("hyte_keeb_tkl_layout", KeyboardLayout.Default));
            for (int i = 0; i < UnderglowLights; i++)
                lights.Add(new LightInfo(UnderglowBase + i, LightKind.Edge, 0, LightKeySource.None));
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_HYTEKeyboard.cpp")
            {
                Product = $"hyte:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new Session(),
        };

        /// <summary>The feature report that opens a stream: 00 04 F0 for the
        /// keys, 00 04 F1 for the underglow.</summary>
        internal static byte[] Opening(bool underglow)
        {
            var report = new byte[FeatureSize];
            report[1] = 0x04;
            report[2] = underglow ? (byte)0xF1 : (byte)0xF0;
            return report;
        }

        /// <summary>A stream's output reports: the colors as red, green and
        /// blue in order, 64 bytes to a report.</summary>
        internal static List<byte[]> Pages(int[] colors, int pages)
        {
            var data = new byte[pages * 64];
            for (int i = 0; i < colors.Length && i * 3 + 2 < data.Length; i++)
            {
                data[i * 3] = (byte)(colors[i] >> 16);
                data[i * 3 + 1] = (byte)(colors[i] >> 8);
                data[i * 3 + 2] = (byte)colors[i];
            }
            var reports = new List<byte[]>(pages);
            for (int p = 0; p < pages; p++)
            {
                var report = new byte[PacketSize];
                Array.Copy(data, p * 64, report, 1, 64);
                reports.Add(report);
            }
            return reports;
        }

        /// <summary>The colors of each stream by place: the keys by LED
        /// value, the underglow by its place.</summary>
        internal static (int[] Keys, int[] Underglow) Streams(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var all = KeyboardLedNames.ByAddress(lights, colors, UnderglowBase + UnderglowPages * 64 / 3);
            var keys = new int[KeyPages * 64 / 3];
            var underglow = new int[UnderglowPages * 64 / 3];
            Array.Copy(all, 0, keys, 0, keys.Length);
            Array.Copy(all, UnderglowBase, underglow, 0, UnderglowLights);
            return (keys, underglow);
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                var (keys, underglow) = Streams(lights, colors);
                foreach (bool under in new[] { false, true })
                {
                    if (!io.SetFeature(Opening(under))) return HidLightingPaint.Failed;
                    foreach (var report in Pages(under ? underglow : keys, under ? UnderglowPages : KeyPages))
                        if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
