using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Mice on the N5312A chip (#557), lit as OpenRGB's N5312AController
    /// lights them: 4E53:5406 on interface 1, usage page 0xFF01, usage 1
    /// (N5312AControllerDetect.cpp:19-44), among them the ANT Esports KM540
    /// and the Marvo M115 (RGBController_N5312A.cpp:24-26). Each has one
    /// light (lines 93-113).
    ///
    /// <para>The model of the device. Every report is a feature report of
    /// 8 bytes with ID 0x07 (N5312AController.h:18-23). OpenRGB's controller
    /// sends 07 A0 when it is made (SendInit, N5312AController.cpp:17-24 and
    /// 54-64), and a session sends it when it begins. Choosing the Direct
    /// mode, 0x02 with the highest brightness, 0x64, and the speed 0 OpenRGB's
    /// mode leaves it at, sends the color 07 0B 01 R G B and then the mode
    /// 07 0A 02 01 01 01 00 64 (SetMode and SetColor, lines 66-104;
    /// RGBController_N5312A.cpp:40-48 and 130-134, RGBControllerInterface.h:325-343),
    /// and every picture after is the color report alone
    /// (DeviceUpdateZoneLEDs, RGBController_N5312A.cpp:120-123). A session
    /// sends the mode with its first picture, then that picture, the
    /// reports OpenRGB sends when a client picks Direct and then sets its
    /// color. OpenRGB marks nothing as saving ("@save :x:", line 20).</para>
    ///
    /// <para>Hand-back. No source reads the mode the mouse ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// mouse keeps PadForge's last color. The chip is in several makers'
    /// mice and no source names a program for them.</para>
    /// </summary>
    internal sealed class N5312ALighting : IHidLightingFamily
    {
        public static readonly N5312ALighting Instance = new();

        internal const string OpenRgbFamily = "N5312AController";
        internal const ushort VendorId = 0x4E53;

        /// <summary>N5312A_REPORT_ID, _PACKET_DATA_LENGTH, _INIT_BYTE,
        /// _SET_MODE_BYTE and _SET_COLOR_BYTE (N5312AController.h:18-23), the
        /// Direct mode and the highest brightness (lines 25-39).</summary>
        internal const byte ReportId = 0x07;
        internal const int ReportSize = 8;
        internal const byte InitByte = 0xA0;
        internal const byte SetModeByte = 0x0A;
        internal const byte SetColorByte = 0x0B;
        internal const byte DirectMode = 0x02;
        internal const byte BrightnessMax = 0x64;

        public string Name => "N5312A";

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
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_N5312A.cpp") { Product = $"n5312a:{VendorId:X4}:{productId:X4}" },
            RepaintMs = AocMouseLighting.RepaintMs,
            Begin = (io, _) => io != null && io.SetFeature(Init()) ? new Session() : null,
        };

        /// <summary>SendInit (N5312AController.cpp:54-64).</summary>
        internal static byte[] Init() => new byte[] { ReportId, InitByte, 0, 0, 0, 0, 0, 0 };

        /// <summary>SetColor (lines 66-81).</summary>
        internal static byte[] Color(int rgb)
            => new byte[] { ReportId, SetColorByte, 0x01, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0, 0 };

        /// <summary>SetMode's mode report (lines 83-104).</summary>
        internal static byte[] Mode(byte mode, byte brightness, byte speed)
            => new byte[] { ReportId, SetModeByte, mode, 0x01, 0x01, 0x01, speed, brightness };

        internal sealed class Session : HidLightingSession
        {
            private bool _selected;

            public override bool Feature => true;

            /// <summary>Nothing undoes the Direct mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
                if (!_selected)
                {
                    // SetMode: the color, then the mode.
                    if (!io.SetFeature(Color(rgb)) || !io.SetFeature(Mode(DirectMode, BrightnessMax, 0)))
                        return HidLightingPaint.Failed;
                    _selected = true;
                }
                return io.SetFeature(Color(rgb)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
