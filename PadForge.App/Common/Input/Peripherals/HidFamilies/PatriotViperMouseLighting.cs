using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Patriot Viper V550 mouse (#557), lit as OpenRGB's
    /// PatriotViperMouseController lights it: 0C45:7E18 on interface 2,
    /// usage page 0xFF18, usage 1 (PatriotViperMouseControllerDetect.cpp:19-40).
    /// Its seven lights are OpenRGB's zones Left (three), Right (three) and
    /// Mousewheel (one), LED 1 to LED 7 in that order
    /// (RGBController_PatriotViperMouse.cpp:53-87).
    ///
    /// <para>The model of the device. OpenRGB's controller sends a 64-byte
    /// feature report, ID 0x01, of fixed bytes when it is made
    /// (PatriotViperMouseController.cpp:14-22), and a session sends it when
    /// it begins. A picture is OpenRGB's Direct mode: seven feature reports
    /// of 64 bytes, one per light, 01 13, the light's index, FF, red, green
    /// and blue, and a check byte at 63: the exclusive or of bytes 0 to 62,
    /// plus 1 where that is even and minus 1 where it is odd
    /// (SetRGB, lines 50-85). OpenRGB does not support the mouse's saving:
    /// its header says "@save :o:" (RGBController_PatriotViperMouse.cpp:18),
    /// which is "Not currently supported by OpenRGB"
    /// (RGBController_Dummy.cpp:25-32), and its Direct mode carries no save
    /// flag (line 39).</para>
    ///
    /// <para>Hand-back. No source reads the colors the mouse showed before,
    /// and OpenRGB sends nothing when it closes, so the mouse keeps
    /// PadForge's last picture. Patriot lights the mouse through its Viper
    /// gaming mouse software (<see cref="VendorPrograms.PatriotViperMouse"/>),
    /// and PadForge leaves the mouse to it while it runs.</para>
    /// </summary>
    internal sealed class PatriotViperMouseLighting : IHidLightingFamily
    {
        public static readonly PatriotViperMouseLighting Instance = new();

        internal const string OpenRgbFamily = "PatriotViperMouseController";
        internal const ushort VendorId = 0x0C45;

        internal const int ReportSize = 64;
        internal const int Leds = 7;

        /// <summary>The report the controller sends when it is made
        /// (PatriotViperMouseController.cpp:20).</summary>
        internal static readonly byte[] InitReport =
        {
            0x01, 0x00, 0x12, 0x12, 0x00, 0x00, 0x00, 0x00, 0x70, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0x50, 0xDE, 0x8D, 0x77, 0x09, 0xDF, 0x8D, 0x77, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x30, 0x58, 0x7C, 0x77, 0x78, 0x81, 0x43, 0x00, 0x30, 0x58, 0x7C, 0x77,
            0x8C, 0x5D, 0x9B, 0x77, 0x00, 0x00, 0x3D, 0x00, 0x98, 0xF5, 0x19, 0x08, 0x00, 0x00, 0x00, 0xEE,
        };

        public string Name => "Patriot Viper mice";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength < ReportSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get(registrations[0].Name + pid, () => Model(registrations[0].Name, pid));
        }

        /// <summary>LED 1 to 3 in zone 1 (Left), LED 4 to 6 in zone 2
        /// (Right), LED 7 in zone 3 (Mousewheel), each at its index.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[Leds];
            for (int i = 0; i < Leds; i++)
                lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i < 3 ? 1 : i < 6 ? 2 : 3 };
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_PatriotViperMouse.cpp")
            {
                Product = $"patriotviper:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = AocMouseLighting.RepaintMs,
            Programs = VendorPrograms.PatriotViper,
            Begin = (io, _) => io != null && io.SetFeature((byte[])InitReport.Clone()) ? new Session() : null,
        };

        /// <summary>One light's report of SetRGB (lines 55-83).</summary>
        internal static byte[] LedFrame(int index, int rgb)
        {
            var report = new byte[ReportSize];
            report[0] = 0x01;
            report[1] = 0x13;
            report[2] = (byte)index;
            report[3] = 0xFF;
            report[4] = (byte)(rgb >> 16);
            report[5] = (byte)(rgb >> 8);
            report[6] = (byte)rgb;
            byte check = 0;
            for (int i = 0; i < ReportSize - 1; i++) check ^= report[i];
            report[ReportSize - 1] = check % 2 == 0 ? (byte)(check + 1) : (byte)(check - 1);
            return report;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing to undo: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var byAddress = KeyboardLedNames.ByAddress(lights, colors, Leds);
                for (int i = 0; i < Leds; i++)
                    if (!io.SetFeature(LedFrame(i, byAddress[i]))) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
