using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Dream Cheeky Webmail Notifier (#557), lit as OpenRGB's
    /// DreamCheekyController lights it: 1D34:0004
    /// (DreamCheekyControllerDetect.cpp:19-24 and 44). One light
    /// (RGBController_DreamCheeky.cpp:42-57).
    ///
    /// <para>The frames. Before its first color the notifier takes four
    /// 9-byte output reports with report ID 0, 00 1F 02 00 5F 00 00 1F 03,
    /// 00 00 02 00 5F 00 00 1F 04, 00 00 00 00 00 00 00 1F 05 and
    /// 00 00 00 00 00 00 00 00 01, which OpenRGB sends when it opens the
    /// device (DreamCheekyController.cpp:16-35) and the session sends once
    /// before its first picture. A color is 00, red, green and blue each
    /// shifted right by two with 1 added to 255, 00 00 00 1F 05
    /// (SetColor, lines 65-89), since the notifier's channels run from 0 to
    /// 64. The device saves nothing (DeviceSaveMode, RGBController_DreamCheeky.cpp:83-88).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back, so the notifier keeps PadForge's last color until it is
    /// unplugged or another program sets one. No source names a process of
    /// Dream Cheeky's software, so the model names no program.</para>
    /// </summary>
    internal sealed class DreamCheekyLighting : IHidLightingFamily
    {
        public static readonly DreamCheekyLighting Instance = new();

        internal const string OpenRgbFamily = "DreamCheekyController";

        /// <summary>usb_buf[9] (line 67).</summary>
        internal const int PacketSize = 9;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        /// <summary>init_0 to init_3 (lines 26-29).</summary>
        internal static readonly byte[][] Opening =
        {
            new byte[] { 0x00, 0x1F, 0x02, 0x00, 0x5F, 0x00, 0x00, 0x1F, 0x03 },
            new byte[] { 0x00, 0x00, 0x02, 0x00, 0x5F, 0x00, 0x00, 0x1F, 0x04 },
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F, 0x05 },
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 },
        };

        public string Name => "Dream Cheeky";

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
            Map = new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } },
                "OpenRGB RGBController_DreamCheeky.cpp") { Product = $"dreamcheeky:{vendorId:X4}:{productId:X4}" },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new ReportsSession(false, 1, colors => new[] { Color(colors[0]) }, Opening),
        };

        /// <summary>One channel scaled to the notifier's range (lines 72-81).</summary>
        internal static byte Scale(byte value) => (byte)((value >> 2) + (value == 255 ? 1 : 0));

        /// <summary>SetColor's report.</summary>
        internal static byte[] Color(int rgb) => new byte[]
        {
            0x00, Scale(LightReports.Red(rgb)), Scale(LightReports.Green(rgb)), Scale(LightReports.Blue(rgb)),
            0x00, 0x00, 0x00, 0x1F, 0x05,
        };
    }
}
