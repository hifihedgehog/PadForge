using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The three-zone keyboard of older MSI laptops (1770:FF00) that
    /// OpenRGB's MSI3ZoneController lights (#557), from bparker06's
    /// msi-keyboard reverse engineering (MSI3ZoneController.cpp:53-54). The
    /// collection is OpenRGB's registration (MSI3ZoneControllerDetect.cpp:38)
    /// with feature reports of <see cref="PacketSize"/> bytes.
    ///
    /// <para>The lights are OpenRGB's: the keyboard's left, middle and right
    /// zones, and the "Aux" zone (RGBController_MSI3Zone.cpp:53-94). A
    /// picture goes out as OpenRGB's Direct mode, its only mode, sends it:
    /// seven reports, the first four with the four colors, the last three
    /// with the Aux color again, since the buffer is never cleared between
    /// them (SetLEDs, MSI3ZoneController.cpp:51-100). OpenRGB marks the mode
    /// as one that saves nothing ("@save :x:", RGBController_MSI3Zone.cpp:18).</para>
    ///
    /// <para>Hand-back. Nothing goes out: no reference reads the keyboard's
    /// state or returns it to its own colors. The keyboard shows PadForge's
    /// last colors until MSI's program sets others or the laptop restarts.
    /// No source names a program of MSI's for these keyboards' process, so
    /// none is listed.</para>
    /// </summary>
    internal sealed class Msi3ZoneLighting : IHidLightingFamily
    {
        public static readonly Msi3ZoneLighting Instance = new();

        internal const string OpenRgbFamily = "MSI3ZoneController";

        /// <summary>The report's length and its first three bytes, its
        /// report ID 1 among them, and its last byte
        /// (MSI3ZoneController.cpp:55-64).</summary>
        internal const int PacketSize = 8;
        internal const byte ReportId = 0x01;

        /// <summary>The shortest time between two pictures, PadForge's
        /// bound for a mode that saves nothing
        /// (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "MSI 3-Zone";

        private static readonly Lazy<HidLightingModel> s_model = new(() => new HidLightingModel
        {
            Family = Instance,
            Name = "MSI 3-Zone Laptop",
            Map = new LightMap(Lights(), "OpenRGB MSI3ZoneController") { Product = "msi:1770:FF00" },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new Session(),
        });

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.FeatureReportLength < PacketSize) return null;
            return OpenRgbRegistrations.Match(OpenRgbFamily, collection).Count > 0 ? s_model.Value : null;
        }

        /// <summary>Left, middle and right, then Aux, each a zone, the
        /// address its place in the colors SetLEDs takes.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[4];
            for (int i = 0; i < 4; i++)
                lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
            return lights;
        }

        /// <summary>SetLEDs' seven reports: 01 02 40, the region from 1 to
        /// 7, red, green, blue and 0xEC (236), regions 1 to 4 with the four
        /// colors and 5 to 7 with the fourth color again.</summary>
        internal static List<byte[]> Frames(IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>(7);
            int count = colors?.Count ?? 0;
            for (int region = 1; region <= 7; region++)
            {
                int index = Math.Min(Math.Min(region, 4), count) - 1;
                int rgb = index < 0 ? 0 : colors[index];
                frames.Add(new byte[] { ReportId, 0x02, 0x40, (byte)region, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0xEC });
            }
            return frames;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (colors == null || colors.Count == 0) return HidLightingPaint.Sent;
                foreach (var frame in Frames(colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
