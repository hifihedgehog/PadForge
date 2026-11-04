using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// MSI's Vigor GK30 keyboard (0DB0:0B30), lit by OpenRGB's
    /// MSIVigorController (#557). The collection is OpenRGB's registration
    /// (MSIVigorControllerDetect.cpp:44), interface 1, usage page 0xFF01,
    /// with feature reports of <see cref="PacketSize"/> bytes.
    ///
    /// <para>The keyboard has six lights and seven colors: each light shows
    /// one of red, yellow, green, cyan, blue, magenta and white, picked by
    /// an index (MSIVigorGK30Controller.cpp:76-95). A picture goes out as
    /// OpenRGB's Custom mode sends it, the mode that colors each light:
    /// one report with the six indexes, two to a byte, and the brightness,
    /// and a wait of 10 ms after it (SetMode, lines 97-227, and the mode,
    /// RGBController_MSIVigorGK30.cpp:38-46). A picture in which every
    /// light is black goes out as the Off mode, since black is no index.
    /// The index of a color is OpenRGB's: each of red, green and blue
    /// counts when it is the largest of the three, with integer division,
    /// so a black light among others shows red, as OpenRGB shows it.</para>
    ///
    /// <para>OpenRGB marks every mode as one the keyboard saves on its own
    /// ("@save :robot:" and MODE_FLAG_AUTOMATIC_SAVE,
    /// RGBController_MSIVigorGK30.cpp:20 and 41), so every picture lands in
    /// the keyboard's memory. A changed picture goes out once it has held
    /// for <see cref="HoldMs"/>, and no sooner than <see cref="RepaintMs"/>
    /// after the one before.</para>
    ///
    /// <para>Hand-back. Nothing goes out: no reference reads the keyboard's
    /// mode back. It keeps PadForge's last picture, saved, until MSI's
    /// program or the user sets another. No source names a program of MSI's
    /// for this keyboard's process, so none is listed.</para>
    /// </summary>
    internal sealed class MsiVigorLighting : IHidLightingFamily
    {
        public static readonly MsiVigorLighting Instance = new();

        internal const string OpenRgbFamily = "MSIVigorController";

        /// <summary>MSI_VIGOR_GK30_REPORT_SIZE, MSI_VIGOR_GK30_LEDS_COUNT
        /// and MSI_VIGOR_GK30_REPORT_ID (MSIVigorGK30Controller.h:18-20).</summary>
        internal const int PacketSize = 8;
        internal const int LedCount = 6;
        internal const byte ReportId = 0x07;

        /// <summary>The Off and Custom modes (MSIVigorGK30Controller.h:24
        /// and 31).</summary>
        internal const byte OffMode = 0x00;
        internal const byte CustomMode = 0x70;

        /// <summary>MSI_VIGOR_GK30_BRIGHTNESS_MAX, the Custom mode's
        /// brightness (MSIVigorGK30Controller.h:37,
        /// RGBController_MSIVigorGK30.cpp:42).</summary>
        internal const byte BrightnessMax = 0x03;

        /// <summary>The wait after each report (MSIVigorGK30Controller.cpp:226).</summary>
        internal const int ReportWaitMs = 10;

        /// <summary>How long a changed picture holds and the shortest time
        /// between two, the bounds of a keyboard that stores every write
        /// (<see cref="LenovoLegionLighting.HoldMs"/>,
        /// <see cref="LenovoLegionLighting.RepaintMs"/>).</summary>
        internal const int HoldMs = LenovoLegionLighting.HoldMs;
        internal const int RepaintMs = LenovoLegionLighting.RepaintMs;

        public string Name => "MSI Vigor";

        private static readonly Lazy<HidLightingModel> s_model = new(() => new HidLightingModel
        {
            Family = Instance,
            Name = "MSI Vigor GK30",
            Map = new LightMap(Lights(), "OpenRGB MSIVigorController") { Product = "msi:0DB0:0B30" },
            HoldMs = HoldMs,
            RepaintMs = RepaintMs,
            Begin = (_, _) => new Session(),
        });

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.FeatureReportLength < PacketSize) return null;
            return OpenRgbRegistrations.Match(OpenRgbFamily, collection).Count > 0 ? s_model.Value : null;
        }

        /// <summary>The six lights of OpenRGB's "Keyboard" zone, "LED 0" to
        /// "LED 5" (RGBController_MSIVigorGK30.cpp:146-166), each a zone of
        /// the keyboard.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[LedCount];
            for (int i = 0; i < LedCount; i++)
                lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
            return lights;
        }

        /// <summary>argb_colour_index_data by red, green and blue, each 0 or
        /// 1 (MSIVigorGK30Controller.cpp:17-23).</summary>
        private static readonly byte[,,] s_index =
        {
            { { 0x00, 0x04 }, { 0x02, 0x03 } },
            { { 0x00, 0x05 }, { 0x01, 0x06 } },
        };

        /// <summary>GetColourIndex (MSIVigorGK30Controller.cpp:60-95): each
        /// channel divided by the largest, at least 1, in integers.</summary>
        internal static byte ColorIndex(int rgb)
        {
            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            int largest = r > g ? (r > b ? r : b) : (g > b ? g : b);
            if (largest == 0) largest = 1;
            return s_index[r / largest, g / largest, b / largest];
        }

        /// <summary>The report of a picture: 07 FF, the Custom mode, the six
        /// indexes from byte 3, the first of each two in the high half of
        /// its byte, and the brightness in byte 6. Every light black: 07 FF
        /// and the Off mode alone.</summary>
        internal static byte[] Frame(IReadOnlyList<int> colors)
        {
            var frame = new byte[PacketSize];
            frame[0] = ReportId;
            frame[1] = 0xFF;
            bool black = true;
            for (int i = 0; i < LedCount; i++)
                if (Color(colors, i) != 0) black = false;
            if (black)
            {
                frame[2] = OffMode;
                return frame;
            }
            frame[2] = CustomMode;
            for (int i = 0; i < LedCount; i++)
            {
                int index = ColorIndex(Color(colors, i));
                if (i % 2 == 0) index <<= 4;
                frame[3 + i / 2] += (byte)index;
            }
            frame[6] = BrightnessMax;
            return frame;
        }

        private static int Color(IReadOnlyList<int> colors, int i)
            => colors != null && i < colors.Count ? colors[i] & 0xFFFFFF : 0;

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!io.SetFeature(Frame(colors))) return HidLightingPaint.Failed;
                wait(ReportWaitMs);
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
