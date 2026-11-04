using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's MSIOptixController family (#557): the MSI Optix monitors
    /// behind 1462:3FA4 (MSI_OPTIX_MAG274QRF_PID) on interface 0, usage page
    /// 0xFF00, usage 1 (MSIOptixControllerDetect.cpp:19-24 and 44). The same ID pair
    /// has a collection on usage page 1, which OpenRGB's MSIMonitorController
    /// lights (<see cref="MsiMonitorLighting"/>), and OpenRGB makes a
    /// controller on each, as PadForge lists a unit on each.
    ///
    /// <para>The lights. Twelve LEDs on the back, one line, "Backside"
    /// (MSI_OPTIX_NUMBER_OF_LEDS, MSIOptixController.h:20, SetupZones,
    /// RGBController_MSIOptix.cpp:342-362), edge lights here.</para>
    ///
    /// <para>A picture is OpenRGB's Direct mode: one 168-byte feature
    /// report, report ID 72, with two identical control blocks of mode 01,
    /// white, speed 0, brightness 0x64, 00 FF 00, 80 and 00, and the twelve
    /// colors from byte 95 (SetDirect, MSIOptixController.cpp:73-132, the
    /// brightness RGBController_MSIOptix.cpp:37-45 and 364-372). The color
    /// offset is three bytes behind each block's start plus 24 colors of
    /// three, 0x0B + 0x0B + 72, and each color byte goes one past it
    /// (usb_buf[++offset]).</para>
    ///
    /// <para>What the writes store, and the hand-back. OpenRGB marks the
    /// controller as one that saves nothing ("@save :x:",
    /// RGBController_MSIOptix.cpp:20) and sends no save. No reference reads
    /// the monitor's own effect or returns it, so nothing goes out at
    /// hand-back, and the monitor shows PadForge's last colors until MSI's
    /// software or its own menu sets others. While Mystic Light runs the
    /// monitor is left to it (<see cref="VendorPrograms.MsiMysticLight"/>,
    /// as on <see cref="MsiMonitorLighting"/>).</para>
    /// </summary>
    internal sealed class MsiOptixLighting : IHidLightingFamily
    {
        public static readonly MsiOptixLighting Instance = new();

        internal const string OpenRgbFamily = "MSIOptixController";

        /// <summary>MSI_VID (MSIOptixControllerDetect.cpp:19).</summary>
        internal const ushort VendorId = 0x1462;

        /// <summary>MSI_OPTIX_REPORT_SIZE, MSI_OPTIX_NUMBER_OF_LEDS and
        /// MSI_OPTIC_REPORT_ID (MSIOptixController.h:18-22).</summary>
        internal const int ReportSize = 168;
        internal const int Leds = 12;
        internal const byte ReportId = 0x72;

        /// <summary>DIRECT_MODE_VALUE, MSI_OPTIX_BRIGHTNESS_MAX and
        /// MSI_OPTIX_MYSTERIOUS_FLAG (MSIOptixController.h:34, 53 and 64).</summary>
        internal const byte DirectMode = 0x01;
        internal const byte Brightness = 0x64;
        internal const byte Flag = 0x80;

        /// <summary>The byte before the first color: two blocks of 0x0B and
        /// MSI_OPTIX_DIRECT_COLOR_OFFSET, 24 * 3 (MSIOptixController.h:21,
        /// MSIOptixController.cpp:98-122).</summary>
        internal const int ColorOffset = 0x0B + 0x0B + 24 * 3;

        /// <summary>The shortest time between two pictures. Direct saves
        /// nothing and no reference bounds the rate, so the bound is
        /// PadForge's (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "MSI Optix";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength < ReportSize) return null;
            var registration = registrations[0];
            return _models.Get(registration.Name, () => Model(registration.Name, collection.ProductId));
        }

        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[Leds];
            for (int i = 0; i < Leds; i++) lights[i] = new LightInfo(i, LightKind.Edge, 0, LightKeySource.None);
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_MSIOptix.cpp")
            {
                Product = $"msi:{VendorId:X4}:{productId:X4}:optix",
            },
            Programs = new[] { VendorPrograms.MsiMysticLight },
            RepaintMs = RepaintMs,
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>SetDirect's report for the twelve colors in LED
        /// order.</summary>
        internal static byte[] Frame(IReadOnlyList<int> colors)
        {
            var buf = new byte[ReportSize];
            buf[0] = ReportId;
            for (int offset = 0x00; offset <= 0x0B; offset += 0x0B)
            {
                buf[offset + 0x01] = DirectMode;
                buf[offset + 0x02] = 0xFF;
                buf[offset + 0x03] = 0xFF;
                buf[offset + 0x04] = 0xFF;
                buf[offset + 0x05] = 0x00;
                buf[offset + 0x06] = Brightness;
                buf[offset + 0x07] = 0x00;
                buf[offset + 0x08] = 0xFF;
                buf[offset + 0x09] = 0x00;
                buf[offset + 0x0A] = Flag;
                buf[offset + 0x0B] = 0x00;
            }
            int at = ColorOffset;
            for (int i = 0; i < Leds; i++)
            {
                int rgb = colors != null && i < colors.Count ? colors[i] : 0;
                buf[++at] = (byte)(rgb >> 16);
                buf[++at] = (byte)(rgb >> 8);
                buf[++at] = (byte)rgb;
            }
            return buf;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                return io.SetFeature(Frame(KeyboardLedNames.ByAddress(lights, colors, Leds)))
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
