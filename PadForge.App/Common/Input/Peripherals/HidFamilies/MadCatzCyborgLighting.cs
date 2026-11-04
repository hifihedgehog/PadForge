using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Mad Catz Cyborg Gaming Light (#557), lit as OpenRGB's
    /// MadCatzCyborgController lights it: 06A3:0DC5
    /// (MadCatzCyborgControllerDetect.cpp:18-19 and 41). One light
    /// (RGBController_MadCatzCyborg.cpp:56-73).
    ///
    /// <para>The frames are feature reports. Before its first color the
    /// light takes the enable report A1 00, which the detector sends
    /// (Initialize, MadCatzCyborgController.cpp:47-57, from
    /// MadCatzCyborgControllerDetect.cpp:31), and the intensity report
    /// A6 00 64, the Direct mode's brightness of 100, which the controller
    /// sends as it starts (SetIntensity, MadCatzCyborgController.cpp:83-99,
    /// from RGBController_MadCatzCyborg.cpp:37-46). The session sends both
    /// once before its first picture. A color is A2 00, red, green, blue and
    /// four zeros (SetLEDColor, MadCatzCyborgController.cpp:59-81). The
    /// light saves nothing ("@save :x:", RGBController_MadCatzCyborg.cpp:16).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back, so the light keeps PadForge's last color until the amBX
    /// software that drives it sets another. The model names that software,
    /// so PadForge lets go of the light while it runs.</para>
    /// </summary>
    internal sealed class MadCatzCyborgLighting : IHidLightingFamily
    {
        public static readonly MadCatzCyborgLighting Instance = new();

        internal const string OpenRgbFamily = "MadCatzCyborgController";

        /// <summary>The commands (MadCatzCyborgController.h:33-38).</summary>
        internal const byte Enable = 0xA1;
        internal const byte Color = 0xA2;
        internal const byte IntensityCommand = 0xA6;

        /// <summary>The Direct mode's brightness (RGBController_MadCatzCyborg.cpp:41).</summary>
        internal const byte Intensity = 100;

        /// <summary>usb_buf[9] of SetLEDColor (line 67).</summary>
        internal const int PacketSize = 9;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Mad Catz Cyborg";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength != 0 && collection.FeatureReportLength < PacketSize) return null;
            var r = registrations[0];
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        /// <summary>The enable and intensity reports.</summary>
        internal static readonly byte[][] Opening =
        {
            new byte[] { Enable, 0x00 },
            new byte[] { IntensityCommand, 0x00, Intensity },
        };

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } },
                "OpenRGB RGBController_MadCatzCyborg.cpp") { Product = $"madcatz:{vendorId:X4}:{productId:X4}" },
            Programs = new[] { VendorPrograms.AmBx },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new ReportsSession(true, 1, colors => new[] { ColorReport(colors[0]) }, Opening),
        };

        /// <summary>SetLEDColor's report.</summary>
        internal static byte[] ColorReport(int rgb) => new byte[]
        {
            Color, 0x00, LightReports.Red(rgb), LightReports.Green(rgb), LightReports.Blue(rgb), 0x00, 0x00, 0x00, 0x00,
        };
    }
}
