using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Gigabyte Aorus M2 mouse (#557), lit as OpenRGB's
    /// GigabyteAorusMouseController lights it: 1044:7A40 on interface 3,
    /// usage page 0xFF01, usage 1 (GigabyteAorusMouseControllerDetect.cpp:20-45).
    /// It has one light (RGBController_GigabyteAorusMouse.cpp:129-145).
    ///
    /// <para>The model of the device. OpenRGB's Direct mode takes the
    /// mouse's brightness from a hardware-mode report, since the Direct
    /// report has no brightness: choosing Direct sends the Static mode
    /// 0x01 with Direct's brightness, the highest (0x64), speed 0 and the
    /// first light's color, CC 01 64 R G B 00 00 in an 8-byte feature
    /// report, and every picture after it is CD 00 R G B 00 00 00
    /// (SetMode and SendDirect, GigabyteAorusMouseController.cpp:57-85,
    /// GigabyteAorusMouseController.h:18-38; DeviceUpdateMode and
    /// DeviceUpdateLEDs, RGBController_GigabyteAorusMouse.cpp:37-45,
    /// 147-150 and 162-170). A session sends the Static report with its
    /// first picture's color before that picture, the two reports OpenRGB
    /// sends when a client picks Direct and then sets its color. OpenRGB
    /// marks nothing as saving ("@save :x:", line 20).</para>
    ///
    /// <para>Hand-back. No source reads the mode the mouse ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// mouse keeps PadForge's last color. Gigabyte lights the M2 through
    /// AORUS ENGINE, which carries RGB Fusion 2.0 (Gigabyte's support page
    /// for the M2 lists "AORUS ENGINE (RGB Fusion 2.0 included)"), and
    /// PadForge leaves the mouse to either while it runs
    /// (<see cref="VendorPrograms.AorusEngine"/>,
    /// <see cref="VendorPrograms.RgbFusion"/>).</para>
    /// </summary>
    internal sealed class GigabyteAorusMouseLighting : IHidLightingFamily
    {
        public static readonly GigabyteAorusMouseLighting Instance = new();

        internal const string OpenRgbFamily = "GigabyteAorusMouseController";
        internal const ushort VendorId = 0x1044;

        /// <summary>GIGABYTE_AORUS_MOUSE_REPORT_SIZE, _DIRECT_CMD and
        /// _HARDWARE_CMD (GigabyteAorusMouseController.h:18-20).</summary>
        internal const int ReportSize = 8;
        internal const byte DirectCommand = 0xCD;
        internal const byte HardwareCommand = 0xCC;

        /// <summary>The Static mode and the highest brightness
        /// (GigabyteAorusMouseController.h:22-38).</summary>
        internal const byte StaticMode = 0x01;
        internal const byte BrightnessMax = 0x64;

        public string Name => "Gigabyte Aorus mice";

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
            Map = new LightMap(Lights(), "OpenRGB RGBController_GigabyteAorusMouse.cpp")
            {
                Product = $"aorusmouse:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = AocMouseLighting.RepaintMs,
            Programs = VendorPrograms.GigabyteMice,
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>SetMode (GigabyteAorusMouseController.cpp:57-71).</summary>
        internal static byte[] ModeFrame(int rgb, byte mode, byte brightness, byte speed)
            => new byte[] { HardwareCommand, mode, brightness, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, speed, 0x00 };

        /// <summary>SendDirect (lines 73-85).</summary>
        internal static byte[] DirectFrame(int rgb)
            => new byte[] { DirectCommand, 0x00, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0x00, 0x00, 0x00 };

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
                    if (!io.SetFeature(ModeFrame(rgb, StaticMode, BrightnessMax, 0))) return HidLightingPaint.Failed;
                    _selected = true;
                }
                return io.SetFeature(DirectFrame(rgb)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
