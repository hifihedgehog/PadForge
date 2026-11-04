using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Creative Sound BlasterX G6 (#557), lit as OpenRGB's
    /// CreativeSoundBlasterXG6Controller lights it: 041E:3256 on interface 4
    /// (CreativeSoundBlasterXG6ControllerDetect.cpp:18-23 and 46). One light,
    /// its logo (RGBController_CreativeSoundBlasterXG6.cpp:54-69).
    ///
    /// <para>The model of the device. OpenRGB has one mode for it, Direct,
    /// which saves nothing ("@save :x:", line 16). A picture is three
    /// 64-byte output reports with report ID 0: 5A 3A 02 06 01, then
    /// 5A 3A 06 04 00 03 01 00 01, then 5A 3A 09 0A 00 03 01 01, the
    /// brightness and the color as blue, green, red (SetLedColor,
    /// CreativeSoundBlasterXG6Controller.cpp:34-100). The brightness is the
    /// mode's, 0xFF (XG6_BRIGHTNESS_MAX, RGBController_CreativeSoundBlasterXG6.h:16,
    /// and line 41).</para>
    ///
    /// <para>The family's other device, the Sound Blaster AE-5, is a PCIe
    /// card inside the PC, reached through its driver
    /// (CreativeSoundBlasterAE5Controller_Windows.cpp:66-94), and is not
    /// built.</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back to the device's own lighting, so the logo shows PadForge's
    /// last color until Creative's software sets another or the device is
    /// unplugged. No source names the processes of that software, Sound
    /// Blaster Command, so the model names no program.</para>
    /// </summary>
    internal sealed class CreativeSoundBlasterLighting : IHidLightingFamily
    {
        public static readonly CreativeSoundBlasterLighting Instance = new();

        internal const string OpenRgbFamily = "CreativeController";

        /// <summary>The length OpenRGB writes (usb_buf[64], line 39).</summary>
        internal const int PacketSize = 64;

        /// <summary>XG6_BRIGHTNESS_MAX (RGBController_CreativeSoundBlasterXG6.h:16).</summary>
        internal const byte Brightness = 0xFF;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Creative";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var r = LightReports.Registered(OpenRgbFamily, collection);
            if (r == null) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < PacketSize) return null;
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(new[] { new LightInfo(0, LightKind.Logo, 0, LightKeySource.None) },
                "OpenRGB RGBController_CreativeSoundBlasterXG6.cpp") { Product = $"creative:{vendorId:X4}:{productId:X4}" },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new ReportsSession(false, 1, colors => Frames(colors[0])),
        };

        /// <summary>SetLedColor's three reports for one color.</summary>
        internal static IReadOnlyList<byte[]> Frames(int rgb) => new[]
        {
            LightReports.Report(PacketSize, 0x00, 0x5A, 0x3A, 0x02, 0x06, 0x01),
            LightReports.Report(PacketSize, 0x00, 0x5A, 0x3A, 0x06, 0x04, 0x00, 0x03, 0x01, 0x00, 0x01),
            LightReports.Report(PacketSize, 0x00, 0x5A, 0x3A, 0x09, 0x0A, 0x00, 0x03, 0x01, 0x01, Brightness,
                LightReports.Blue(rgb), LightReports.Green(rgb), LightReports.Red(rgb)),
        };
    }
}
