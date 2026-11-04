using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The AOC GM500 mouse (#557), lit as OpenRGB's AOCMouseController
    /// lights it: 3938:1179 on interface 1, usage page 0xFF19, usage 0xFF19
    /// (AOCMouseControllerDetect.cpp:19-40). Its two lights are the logo and
    /// the scroll wheel, in that order (RGBController_AOCMouse.cpp:131-158).
    ///
    /// <para>The model of the device. A picture is OpenRGB's Direct mode,
    /// the mouse's static single-color mode 0x00 with the highest
    /// brightness, the medium speed and the clockwise direction, in one
    /// feature report of 60 bytes, ID 0x20: 20 03 01, the mode, the speed,
    /// the brightness and the direction, the constant bytes SendPacket
    /// writes, the logo's red, green and blue at 0x33 and the wheel's at
    /// 0x36 (SendDirect and SendPacket, AOCMouseController.cpp:55-136,
    /// AOCMouseController.h:22-54; DeviceUpdateMode,
    /// RGBController_AOCMouse.cpp:175-180). OpenRGB marks nothing as saving
    /// ("@save :x:", line 18, and the Direct mode's flags, line 39).</para>
    ///
    /// <para>Hand-back. No source reads the mode the mouse ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// mouse keeps PadForge's last picture. No source names the processes
    /// of AOC's G-Menu or G-Tools, so no program is named.</para>
    /// </summary>
    internal sealed class AocMouseLighting : IHidLightingFamily
    {
        public static readonly AocMouseLighting Instance = new();

        internal const string OpenRgbFamily = "AOCMouseController";
        internal const ushort VendorId = 0x3938;

        internal const int ReportSize = 60;

        /// <summary>AOC_MOUSE_MODE_STATIC_SINGLE_COLOR, _BRIGHTNESS_HIGH,
        /// _SPEED_MEDIUM and _DIRECTION_CLOCKWISE (AOCMouseController.h:22-54).</summary>
        internal const byte StaticSingleColor = 0x00;
        internal const byte BrightnessHigh = 0x03;
        internal const byte SpeedMedium = 0x02;
        internal const byte Clockwise = 0x00;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other families
        /// of the shared worker that store nothing keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "AOC mice";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength != 0 && collection.FeatureReportLength < ReportSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get(registrations[0].Name + pid, () => Model(registrations[0].Name, pid));
        }

        /// <summary>The logo and the scroll wheel, OpenRGB's two zones in
        /// order, each its place in the color list.</summary>
        internal static IReadOnlyList<LightInfo> Lights() => new[]
        {
            new LightInfo(0, LightKind.Logo, 0, LightKeySource.None),
            new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
        };

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_AOCMouse.cpp") { Product = $"aocmouse:{VendorId:X4}:{productId:X4}" },
            RepaintMs = RepaintMs,
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>SendPacket (AOCMouseController.cpp:67-136).</summary>
        internal static byte[] Frame(byte mode, byte brightness, byte speed, byte direction, int logo, int wheel)
        {
            var report = new byte[ReportSize];
            report[0x00] = 0x20;
            report[0x01] = 0x03;
            report[0x02] = 0x01;
            report[0x03] = mode;
            report[0x04] = speed;
            report[0x05] = brightness;
            report[0x06] = direction;
            report[0x07] = 0x01;
            report[0x08] = 0x02;
            report[0x09] = 0xFF;
            report[0x0D] = 0x01;
            report[0x0E] = 0x03;
            report[0x0F] = 0xFF;
            report[0x10] = 0x7F;
            report[0x13] = 0x01;
            report[0x14] = 0x04;
            report[0x17] = 0xFF;
            report[0x19] = 0x01;
            report[0x1A] = 0x05;
            report[0x1C] = 0xFF;
            report[0x1F] = 0x01;
            report[0x20] = 0x06;
            report[0x21] = 0xFF;
            report[0x23] = 0xFF;
            report[0x25] = 0x01;
            report[0x26] = 0x07;
            report[0x27] = 0xFF;
            report[0x28] = 0xFF;
            report[0x2C] = 0x0A;
            report[0x2D] = 0x0A;
            report[0x30] = 0x14;
            report[0x31] = 0x08;
            report[0x3A] = 0x32;
            report[0x3B] = 0x32;
            report[0x33] = (byte)(logo >> 16);
            report[0x34] = (byte)(logo >> 8);
            report[0x35] = (byte)logo;
            report[0x36] = (byte)(wheel >> 16);
            report[0x37] = (byte)(wheel >> 8);
            report[0x38] = (byte)wheel;
            return report;
        }

        /// <summary>SendDirect (lines 55-65), each light's color by its
        /// address.</summary>
        internal static byte[] Direct(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var byAddress = KeyboardLedNames.ByAddress(lights, colors, 2);
            return Frame(StaticSingleColor, BrightnessHigh, SpeedMedium, Clockwise, byAddress[0], byAddress[1]);
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing undoes the Direct mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => io.SetFeature(Direct(lights, colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
