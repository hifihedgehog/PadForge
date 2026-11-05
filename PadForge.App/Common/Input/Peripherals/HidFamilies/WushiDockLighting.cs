using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The JSAUX RGB Docking Station (#557), lit as OpenRGB's
    /// WushiL50USBController lights it: 306F:1234
    /// (WushiL50USBDetect.cpp:18-23 and 43). Its four lights are the
    /// dock's four zones (RGBController_WushiL50USB.cpp:99-120,
    /// WUSHI_L50_NUM_LEDS, WushiL50USBController.h:22).
    ///
    /// <para>The detector is a wrapped one, which OpenRGB runs in a pass of
    /// its own through its hidapi wrapper (DetectionManager.cpp:1631-1730).
    /// The wrapper changes nothing on the wire on Windows: there it is
    /// hidapi's own functions (default_hidapi_wrapper, lines 83-101, passed
    /// at line 1199), and the libusb hidapi it loads in its place is
    /// Linux's (#ifdef __linux__, lines 1469-1509). The controller sends its report through
    /// the wrapper's hid_send_feature_report (WushiL50USBController.cpp:87),
    /// and on Windows the report starts with its ID, 0xCC (lines 62-67).</para>
    ///
    /// <para>The frames. A picture is one 65-byte feature report: CC, 16,
    /// the effect, the speed, the brightness, the four zones' red, green
    /// and blue, a zero, and the two wave directions (setMode, lines
    /// 50-88). OpenRGB's Direct mode sends the Static effect 01, the speed
    /// its state resets to, 01, its brightness 02, and both directions 0
    /// (DeviceUpdateMode, RGBController_WushiL50USB.cpp:149-183, with
    /// Direct at lines 23-31 and WushiL50State, WushiL50USBController.h:54-93).
    /// The dock saves nothing (DeviceSaveMode, lines 185-190).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back, so the dock keeps PadForge's last colors until it is
    /// unplugged or another program sets them. No source names a program
    /// of JSAUX's, so the model names none.</para>
    /// </summary>
    internal sealed class WushiDockLighting : IHidLightingFamily
    {
        public static readonly WushiDockLighting Instance = new();

        internal const string OpenRgbFamily = "WushiController";

        /// <summary>WUSHI_L50_HID_PACKET_SIZE (WushiL50USBController.h:21).</summary>
        internal const int PacketSize = 65;
        internal const byte ReportId = 0xCC;
        internal const byte Command = 0x16;
        internal const int ZoneCount = 4;

        /// <summary>WUSHI_L50_EFFECT_STATIC, WUSHI_L50_SPEED_SLOWEST and
        /// WUSHI_L50_BRIGHTNESS_HIGH (WushiL50USBController.h:26, 42 and 37).</summary>
        internal const byte StaticEffect = 0x01;
        internal const byte Speed = 0x01;
        internal const byte Brightness = 0x02;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "JSAUX";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var r = LightReports.Registered(OpenRgbFamily, collection);
            if (r == null) return null;
            if (collection.FeatureReportLength < PacketSize) return null;
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[ZoneCount];
            for (int i = 0; i < ZoneCount; i++) lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
            return lights;
        }

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_WushiL50USB.cpp") { Product = $"jsaux:{vendorId:X4}:{productId:X4}" },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new ReportsSession(true, ZoneCount, colors => new[] { Report(colors) }),
        };

        /// <summary>setMode's report in the Direct mode.</summary>
        internal static byte[] Report(int[] colors)
        {
            var report = LightReports.Report(PacketSize, ReportId, Command, StaticEffect, Speed, Brightness);
            for (int i = 0; i < ZoneCount; i++)
            {
                int rgb = i < colors.Length ? colors[i] : 0;
                report[5 + i * 3] = LightReports.Red(rgb);
                report[6 + i * 3] = LightReports.Green(rgb);
                report[7 + i * 3] = LightReports.Blue(rgb);
            }
            // Bytes 18 and 19, wave_ltr and wave_rtl (lines 81-82 with the
            // Windows offset), stay 0.
            return report;
        }
    }
}
