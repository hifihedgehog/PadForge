using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The light bar of Clevo laptops on ITE's 048D:7001 controller (#557),
    /// lit as OpenRGB's ClevoLightbarController lights it: usage page
    /// 0xFF03, usage 2 (ClevoLightbarControllerDetect.cpp:21-26 and 46).
    /// One light, the bar (RGBController_ClevoLightbar.cpp:129-144).
    ///
    /// <para>The frames are 8-byte feature reports whose first byte is the
    /// command, sent as the report ID (WriteControl,
    /// ClevoLightbarController.cpp:54-57, CLEVO_LIGHTBAR_REPORT_SIZE,
    /// ClevoLightbarController.h:18). A picture is the color, 14 00 01,
    /// red, green, blue, 00 00 (SetColor, lines 95-111), and then the
    /// Direct mode again, 08 22 01, the speed, the brightness 0x64 and 01
    /// (SetMode, lines 132-157), as DeviceUpdateLEDs sends them
    /// (RGBController_ClevoLightbar.cpp:146-162). The Direct mode has no
    /// speed, so its speed of 0 goes out inverted as 11, 0x0B (lines
    /// 137-146 of the controller, Direct at RGBController_ClevoLightbar.cpp:37-45,
    /// and a mode's speed of 0, RGBControllerInterface.h:339), and its brightness is
    /// CLEVO_LIGHTBAR_BRIGHTNESS_MAX, 100. OpenRGB marks the bar's modes as
    /// saving nothing ("@save :x:", line 18).</para>
    ///
    /// <para>Hand-back. No reference reads the bar's mode back and OpenRGB
    /// sends nothing when it closes, so the bar keeps PadForge's last
    /// color until the laptop's own program sets another or the laptop
    /// restarts, as the Clevo keyboard family's keyboard does
    /// (<see cref="ClevoLighting"/>). No source names that program's
    /// process, so the model names none.</para>
    /// </summary>
    internal sealed class ClevoLightbarLighting : IHidLightingFamily
    {
        public static readonly ClevoLightbarLighting Instance = new();

        internal const string OpenRgbFamily = "ClevoLightbarController";

        internal const int PacketSize = 8;

        /// <summary>CLEVO_LIGHTBAR_MODE_DIRECT, CLEVO_LIGHTBAR_BRIGHTNESS_MAX,
        /// CLEVO_LIGHTBAR_SPEED_MIN and _MAX (ClevoLightbarController.h:19-27).</summary>
        internal const byte DirectMode = 0x01;
        internal const byte Brightness = 100;
        internal const int SpeedMin = 1;
        internal const int SpeedMax = 10;

        /// <summary>The Direct mode's speed as SetMode inverts it: (10 + 1) - 0.</summary>
        internal const byte DirectSpeed = SpeedMax + SpeedMin - 0;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other laptop families of the shared worker keep.</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "CLEVO Lightbar";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var r = LightReports.Registered(OpenRgbFamily, collection);
            if (r == null) return null;
            if (collection.FeatureReportLength < PacketSize) return null;
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(new[] { new LightInfo(0, LightKind.Edge, 0, LightKeySource.None) },
                "OpenRGB RGBController_ClevoLightbar.cpp") { Product = $"clevo:{vendorId:X4}:{productId:X4}:lightbar" },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new ReportsSession(true, 1, colors => Frames(colors[0])),
        };

        /// <summary>SetColor, then SetMode in Direct.</summary>
        internal static IReadOnlyList<byte[]> Frames(int rgb) => new[]
        {
            new byte[] { 0x14, 0x00, 0x01, LightReports.Red(rgb), LightReports.Green(rgb), LightReports.Blue(rgb), 0x00, 0x00 },
            new byte[] { 0x08, 0x22, DirectMode, DirectSpeed, Brightness, 0x01, 0x00, 0x00 },
        };
    }
}
