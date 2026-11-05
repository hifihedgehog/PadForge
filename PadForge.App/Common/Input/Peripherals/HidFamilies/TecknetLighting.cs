using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Tecknet M008 mouse (#557), lit as OpenRGB's TecknetController
    /// lights it: 04D9:FC05 on usage page 0xFFA0, usage 1, any interface
    /// (TecknetControllerDetect.cpp:17-41). Its one light is the logo
    /// (RGBController_Tecknet.cpp:75-90).
    ///
    /// <para>The model of the device. A picture is OpenRGB's Direct mode:
    /// one feature report of 16 bytes, ID 0x02, 02 04, then red, green and
    /// blue each inverted (255 minus the value), the brightness, which
    /// OpenRGB always sends as the highest, 3, and the Direct mode's speed
    /// byte, 0 (SetColor and SendUpdate, TecknetController.cpp:15-25 and
    /// 85-113, TecknetController.h:20-52). The controller starts in the
    /// Direct mode with the highest brightness (TecknetController.cpp:43-45),
    /// and DeviceUpdateMode compares the mode's index with 0xFF, which no
    /// index is, so the brightness stays the highest
    /// (RGBController_Tecknet.cpp:114-120). Choosing Direct sends the same
    /// report, so a session sends nothing before its first picture.
    /// OpenRGB marks nothing as saving ("@save :x:", line 18).</para>
    ///
    /// <para>Hand-back. No source reads the color the mouse showed before,
    /// and OpenRGB sends nothing when it closes, so the mouse keeps
    /// PadForge's last color. No source names a program of Tecknet's.</para>
    /// </summary>
    internal sealed class TecknetLighting : IHidLightingFamily
    {
        public static readonly TecknetLighting Instance = new();

        internal const string OpenRgbFamily = "TecknetController";
        internal const ushort VendorId = 0x04D9;

        /// <summary>TECKNET_PACKET_LENGTH and TECKNET_BRIGHTNESS_HIGH
        /// (TecknetController.h:20 and 38-44).</summary>
        internal const int ReportSize = 16;
        internal const byte BrightnessHigh = 0x03;

        public string Name => "Tecknet";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength < ReportSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get(registrations[0].Name + pid, () => Model(registrations[0].Name, pid));
        }

        internal static IReadOnlyList<LightInfo> Lights()
            => new[] { new LightInfo(0, LightKind.Logo, 0, LightKeySource.None) };

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_Tecknet.cpp") { Product = $"tecknet:{VendorId:X4}:{productId:X4}" },
            RepaintMs = AocMouseLighting.RepaintMs,
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>SendUpdate in the Direct mode (TecknetController.cpp:95-113).</summary>
        internal static byte[] Frame(int rgb)
        {
            var report = new byte[ReportSize];
            report[0] = 0x02;
            report[1] = 0x04;
            report[2] = (byte)(255 - (byte)(rgb >> 16));
            report[3] = (byte)(255 - (byte)(rgb >> 8));
            report[4] = (byte)(255 - (byte)rgb);
            report[5] = BrightnessHigh;
            report[6] = 0x00;
            return report;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing to undo: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
                return io.SetFeature(Frame(rgb)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
