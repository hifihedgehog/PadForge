using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The AOC AGON AMM700 mousepad (#557), lit as OpenRGB's
    /// AOCMousematController lights it: 3938:1162 on interface 1, usage
    /// page 0xFF19, usage 0xFF19 (AOCMousematControllerDetect.cpp:19-40).
    /// Its one light is the mat's edge, OpenRGB's one zone "Mousemat"
    /// (RGBController_AOCMousemat.cpp:121-136). A mousepad has no input row,
    /// so it gets the shared worker's unit row.
    ///
    /// <para>The model of the device. A picture is OpenRGB's Direct mode,
    /// the mat's static single-color mode 0x00 with the highest brightness,
    /// the medium speed and the clockwise direction, in one feature report
    /// of 32 bytes, ID 0x20: 20, the brightness, the speed, the direction,
    /// 01, the mode, red, green and blue at 6, and the constant bytes
    /// SendPacket writes (SendDirect and SendPacket,
    /// AOCMousematController.cpp:55-117, AOCMousematController.h:22-53;
    /// DeviceUpdateMode, RGBController_AOCMousemat.cpp:153-158). OpenRGB
    /// marks nothing as saving ("@save :x:", line 18, and the Direct mode's
    /// flags, line 39).</para>
    ///
    /// <para>Hand-back. No source reads the mode the mat ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// mat keeps PadForge's last color. No source names the processes of
    /// AOC's software, so no program is named.</para>
    /// </summary>
    internal sealed class AocMousematLighting : IHidLightingFamily
    {
        public static readonly AocMousematLighting Instance = new();

        internal const string OpenRgbFamily = "AOCMousematController";
        internal const ushort VendorId = 0x3938;

        internal const int ReportSize = 32;

        /// <summary>AOC_MOUSEMAT_MODE_STATIC_SINGLE_COLOR, _BRIGHTNESS_HIGH,
        /// _SPEED_MEDIUM and _DIRECTION_CLOCKWISE (AOCMousematController.h:22-53).</summary>
        internal const byte StaticSingleColor = 0x00;
        internal const byte BrightnessHigh = 0x03;
        internal const byte SpeedMedium = 0x02;
        internal const byte Clockwise = 0x00;

        public string Name => "AOC mousepads";

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

        internal static IReadOnlyList<LightInfo> Lights()
            => new[] { new LightInfo(0, LightKind.Edge, 0, LightKeySource.None) };

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_AOCMousemat.cpp") { Product = $"aocmat:{VendorId:X4}:{productId:X4}" },
            RepaintMs = AocMouseLighting.RepaintMs,
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>SendPacket (AOCMousematController.cpp:67-117).</summary>
        internal static byte[] Frame(byte mode, byte brightness, byte speed, byte direction, int rgb)
        {
            var report = new byte[ReportSize];
            report[0x00] = 0x20;
            report[0x01] = brightness;
            report[0x02] = speed;
            report[0x03] = direction;
            report[0x04] = 0x01;
            report[0x05] = mode;
            report[0x09] = 0xFF;
            report[0x0C] = 0xFF;
            report[0x0D] = 0x3F;
            report[0x0F] = 0xFF;
            report[0x10] = 0xFF;
            report[0x13] = 0xFF;
            report[0x17] = 0xFF;
            report[0x19] = 0xFF;
            report[0x1A] = 0xFF;
            report[0x1B] = 0xFF;
            report[0x1D] = 0xFF;
            report[0x1E] = 0x32;
            report[0x1F] = 0x32;
            report[0x06] = (byte)(rgb >> 16);
            report[0x07] = (byte)(rgb >> 8);
            report[0x08] = (byte)rgb;
            return report;
        }

        /// <summary>SendDirect (lines 55-65).</summary>
        internal static byte[] Direct(int rgb) => Frame(StaticSingleColor, BrightnessHigh, SpeedMedium, Clockwise, rgb);

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing undoes the Direct mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
                return io.SetFeature(Direct(rgb)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
