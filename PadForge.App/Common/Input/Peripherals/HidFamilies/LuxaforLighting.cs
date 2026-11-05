using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Luxafor Flag (#557), lit as OpenRGB's LuxaforController lights
    /// it: 04D8:F372 (LuxaforControllerDetect.cpp:19-24 and 44). Its six
    /// lights are OpenRGB's two zones, the three of the flag as zone 1 and
    /// the three at the rear as zone 2, numbered 1 to 6 on the device
    /// (RGBController_Luxafor.cpp:125-173).
    ///
    /// <para>The frames. OpenRGB's Direct mode sends each light in a 9-byte
    /// output report with report ID 0: the mode 01, the light's number,
    /// red, green, blue, then 100 (0x64), 00 and 00 (SendPacket,
    /// LuxaforController.cpp:51-83 and 134, as DeviceUpdateSingleLED sends
    /// it for every light in turn, RGBController_Luxafor.cpp:175-201). The
    /// device saves nothing (DeviceSaveMode, lines 227-232).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back, so the flag keeps PadForge's last colors until it is
    /// unplugged or Luxafor's app sets others. The model names that app,
    /// so PadForge lets go of the flag while it runs.</para>
    /// </summary>
    internal sealed class LuxaforLighting : IHidLightingFamily
    {
        public static readonly LuxaforLighting Instance = new();

        internal const string OpenRgbFamily = "LuxaforController";

        /// <summary>usb_buf[9] (line 53).</summary>
        internal const int PacketSize = 9;

        /// <summary>LUXAFOR_MODE_DIRECT and LUXAFOR_LED_FIRST
        /// (LuxaforController.h:19 and 25).</summary>
        internal const byte DirectMode = 0x01;
        internal const byte FirstLed = 0x01;
        internal const int LedCount = 6;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Luxafor";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var r = LightReports.Registered(OpenRgbFamily, collection);
            if (r == null) return null;
            if (collection.OutputReportLength < PacketSize) return null;
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[LedCount];
            for (int i = 0; i < LedCount; i++)
                lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i < 3 ? 1 : 2 };
            return lights;
        }

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_Luxafor.cpp") { Product = $"luxafor:{vendorId:X4}:{productId:X4}" },
            Programs = new[] { VendorPrograms.LuxaforApp },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new ReportsSession(false, LedCount, Frames),
        };

        /// <summary>SendPacket in Direct for one light, numbered from 1.</summary>
        internal static byte[] Direct(byte led, int rgb) => new byte[]
        {
            0x00, DirectMode, led, LightReports.Red(rgb), LightReports.Green(rgb), LightReports.Blue(rgb), 100, 0x00, 0x00,
        };

        /// <summary>Every light in turn, as DeviceUpdateLEDs sends them.</summary>
        internal static IReadOnlyList<byte[]> Frames(int[] colors)
        {
            var frames = new byte[LedCount][];
            for (int i = 0; i < LedCount; i++) frames[i] = Direct((byte)(FirstLed + i), i < colors.Length ? colors[i] : 0);
            return frames;
        }
    }
}
