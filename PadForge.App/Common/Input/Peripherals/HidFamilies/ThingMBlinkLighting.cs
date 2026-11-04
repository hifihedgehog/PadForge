using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// ThingM's blink(1) mk2 (#557), lit as OpenRGB's BlinkController
    /// lights it: 27B8:01ED on usage page 0xFF00, usage 1
    /// (ThingMControllerDetect.cpp:17-18 and 38). Its two lights, LED A and
    /// LED B, are numbered 1 and 2 on the device
    /// (RGBController_BlinkController.cpp:77-97).
    ///
    /// <para>The frames. OpenRGB's Direct mode sends each light in a 9-byte
    /// feature report with report ID 1: 0x63, the "c" fade command, red,
    /// green, blue, the fade time in two bytes, which the Direct mode
    /// leaves at 0, the light's number and a zero (SendUpdate,
    /// BlinkController.cpp:64-85, BLINK_PACKET_SIZE, BlinkController.h:18,
    /// as DeviceUpdateLEDs sends it for every light in turn,
    /// RGBController_BlinkController.cpp:99-119). ThingM's own library
    /// builds the same report for blink1_fadeToRGBN: ID 1, 'c', the color,
    /// the fade time in tens of milliseconds and the light's number
    /// (blink1-tool blink1-lib.c:239-259, blink1_fadeToRGBN). The device saves nothing
    /// ("@save :x:", RGBController_BlinkController.cpp:18).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back, so the blink(1) keeps PadForge's last colors until it is
    /// unplugged or Blink1Control sets others. The model names that
    /// program, so PadForge lets go of the device while it runs.</para>
    /// </summary>
    internal sealed class ThingMBlinkLighting : IHidLightingFamily
    {
        public static readonly ThingMBlinkLighting Instance = new();

        internal const string OpenRgbFamily = "ThingMController";

        internal const int PacketSize = 9;
        internal const byte ReportId = 0x01;
        internal const byte FadeCommand = 0x63;
        internal const int LedCount = 2;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "ThingM blink(1)";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var r = LightReports.Registered(OpenRgbFamily, collection);
            if (r == null) return null;
            if (collection.FeatureReportLength != 0 && collection.FeatureReportLength < PacketSize) return null;
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        internal static IReadOnlyList<LightInfo> Lights() => new[]
        {
            new LightInfo(0, LightKind.Other, 0, LightKeySource.None),
            new LightInfo(1, LightKind.Other, 0, LightKeySource.None),
        };

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_BlinkController.cpp") { Product = $"thingm:{vendorId:X4}:{productId:X4}" },
            Programs = new[] { VendorPrograms.Blink1Control },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new ReportsSession(true, LedCount, Frames),
        };

        /// <summary>SendUpdate with no fade time for a light numbered from 1.</summary>
        internal static byte[] Update(byte led, int rgb) => new byte[]
        {
            ReportId, FadeCommand, LightReports.Red(rgb), LightReports.Green(rgb), LightReports.Blue(rgb), 0x00, 0x00, led, 0x00,
        };

        internal static IReadOnlyList<byte[]> Frames(int[] colors)
            => new[] { Update(1, colors.Length > 0 ? colors[0] : 0), Update(2, colors.Length > 1 ? colors[1] : 0) };
    }
}
