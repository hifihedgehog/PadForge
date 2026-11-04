using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Logitech X56 Rhino HOTAS that OpenRGB's LogitechX56Controller
    /// lights (#557): the joystick, 0738:2221, and the throttle, 0738:A221,
    /// each a device of its own. The vendor ID is Mad Catz's, so the X56 is
    /// no HID++ device either (LogitechControllerDetect.cpp:1787), and
    /// PadForge reads it as a game controller, whose row the linker never
    /// lights, so each half gets a row of its own
    /// (PeripheralLinker.Orphans). The collection is OpenRGB's registration:
    /// interface 2, usage page 0xFF00, usage 1 on older units and 3 on newer
    /// ones (LogitechControllerDetect.cpp:135-137 and 1555-1561).
    ///
    /// <para>A picture is OpenRGB's Direct mode, two 64-byte feature reports
    /// with the report ID 09: 09 00 02 and the brightness, then 09 00 03 and
    /// the color (LogitechX56Controller.cpp:51-84, LogitechX56Controller.h:18).
    /// The brightness is OpenRGB's default and its top, 0x64
    /// (RGBController_LogitechX56.cpp:36-43): PadForge carries brightness in
    /// its colors. The save report, 01 01 (lines 86-102), stores the color,
    /// and OpenRGB sends it only when its user saves
    /// (MODE_FLAG_MANUAL_SAVE, RGBController_LogitechX56.cpp:38 and
    /// 101-104). It is never sent here. The one light is OpenRGB's "X56"
    /// zone (lines 55-73).</para>
    ///
    /// <para>Hand-back. Nothing goes out: no reference reads the stick's
    /// color or returns it to its stored one. The stick shows PadForge's
    /// last color until it loses power, and then its stored one. No source
    /// names a program that lights it, so none is listed.</para>
    /// </summary>
    internal sealed class LogitechX56Lighting : IHidLightingFamily
    {
        public static readonly LogitechX56Lighting Instance = new();

        /// <summary>OpenRGB's registrations (LogitechControllerDetect.cpp:135-137
        /// and 1558-1561).</summary>
        internal const ushort VendorId = 0x0738;
        internal const ushort JoystickProductId = 0x2221;
        internal const ushort ThrottleProductId = 0xA221;
        internal const int Interface = 2;
        internal const ushort UsagePage = 0xFF00;
        internal const ushort OlderUsage = 1;
        internal const ushort NewerUsage = 3;

        /// <summary>X56_CONTROLLER_PACKET_SIZE and the report ID of both
        /// reports (LogitechX56Controller.h:18, LogitechX56Controller.cpp:65
        /// and 72).</summary>
        internal const int PacketSize = 64;
        internal const byte ReportId = 0x09;

        /// <summary>Direct's brightness, its default and its top
        /// (RGBController_LogitechX56.cpp:40-42).</summary>
        internal const byte Brightness = 0x64;

        /// <summary>The shortest time between two pictures, PadForge's
        /// bound for a mode that saves nothing
        /// (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Logitech X56";

        private static readonly Lazy<HidLightingModel> s_joystick = new(() => Model(
            "Logitech X56 Rhino Hotas Joystick", "logitech:0738:2221"));

        private static readonly Lazy<HidLightingModel> s_throttle = new(() => Model(
            "Logitech X56 Rhino Hotas Throttle", "logitech:0738:A221"));

        private static HidLightingModel Model(string name, string product) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB LogitechX56Controller") { Product = product },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new Session(),
        };

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            if (collection.UsagePage != UsagePage || (collection.Usage != OlderUsage && collection.Usage != NewerUsage)) return null;
            if (collection.FeatureReportLength < PacketSize) return null;
            int bus = OpenRgbRegistrations.BusOf(collection);
            if (OpenRgbRegistrations.InterfaceOf(collection, bus) != Interface) return null;
            return collection.ProductId switch
            {
                JoystickProductId => s_joystick.Value,
                ThrottleProductId => s_throttle.Value,
                _ => null,
            };
        }

        /// <summary>The one light, a zone at address 0.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        /// <summary>SetColor's two reports: the brightness, then the
        /// color.</summary>
        internal static byte[][] Frames(int rgb)
        {
            var brightness = new byte[PacketSize];
            brightness[0] = ReportId;
            brightness[2] = 0x02;
            brightness[3] = Brightness;
            var color = new byte[PacketSize];
            color[0] = ReportId;
            color[2] = 0x03;
            color[3] = (byte)(rgb >> 16);
            color[4] = (byte)(rgb >> 8);
            color[5] = (byte)rgb;
            return new[] { brightness, color };
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (colors == null || colors.Count == 0) return HidLightingPaint.Sent;
                foreach (var frame in Frames(colors[0]))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
