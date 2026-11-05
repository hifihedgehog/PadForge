using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Glorious Model I mouse (#557), lit as OpenRGB's
    /// LaviewTechnologyController lights it: 22D4:1503 on interface 1, usage
    /// page 0xFF01, usage 2 (LaviewTechnologyDetector.cpp:17-38). It has one
    /// light (RGBController_LaviewTechnology.cpp:158-179).
    ///
    /// <para>The model of the device. The mouse has no Direct mode ("@direct
    /// :x:", RGBController_LaviewTechnology.cpp:14-23). OpenRGB colors it
    /// through its Custom mode, the static mode 0x01, in one feature report
    /// of 64 bytes, ID 0xA1: A1 0C, profile 1 at 5, the mode at 16, red,
    /// green and blue at 17, the brightness at 56 and the speed at 58
    /// (SetMode, LaviewTechnologyController.cpp:58-102,
    /// LaviewTechnologyController.h:18-45; DeviceUpdateMode,
    /// RGBController_LaviewTechnology.cpp:196-201). Custom's brightness is
    /// the top of its range, 100, and its speed OpenRGB's mode default, 0
    /// (RGBController_LaviewTechnology.cpp:37-45, RGBControllerInterface.h:325-343).
    /// SetMode passes each through std::clamp(0u, value, 64u) (lines 68-69),
    /// its bounds out of order, which the C++ standard leaves undefined for a
    /// value above 64. The MSVC library OpenRGB's Windows builds use
    /// (scripts/build-windows.bat) returns 100 for 100, so that build's slider
    /// at its top sends the byte sent here. libstdc++ returns 64 for it. No
    /// source names the mouse's own ceiling.</para>
    ///
    /// <para>The mouse saves each mode it is sent ("@save :robot:", line 18:
    /// automatic and not to be turned off), so a picture is held before it
    /// goes out and pictures are spaced, the bounds the stored writes of
    /// <see cref="SinowealthLighting"/> keep.</para>
    ///
    /// <para>Hand-back. No source reads the mode the mouse ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// mouse keeps the last color in its Custom mode. Glorious lights the
    /// mouse through Glorious CORE (<see cref="VendorPrograms.GloriousCore"/>),
    /// and PadForge leaves the mouse to it while it runs.</para>
    /// </summary>
    internal sealed class LaviewTechnologyLighting : IHidLightingFamily
    {
        public static readonly LaviewTechnologyLighting Instance = new();

        internal const string OpenRgbFamily = "LaviewTechnologyController";
        internal const ushort VendorId = 0x22D4;

        /// <summary>LAVIEW_TECHNOLOGY_REPORT_SIZE and the static mode
        /// (LaviewTechnologyController.h:18-23).</summary>
        internal const int ReportSize = 64;
        internal const byte StaticMode = 0x01;

        /// <summary>Custom's most brightness, LAVIEW_TECHNOLOGY_BRIGHTNESS_FAST,
        /// since PadForge dims a color itself, where OpenRGB's default is 50
        /// (LaviewTechnologyController.h:40-45,
        /// RGBController_LaviewTechnology.cpp:40-42, and the class comment for
        /// what line 68 does with it), and its speed, OpenRGB's mode
        /// default.</summary>
        internal const byte Brightness = 100;
        internal const byte Speed = 0;

        public string Name => "Laview Technology";

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
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_LaviewTechnology.cpp")
            {
                Product = $"laview:{VendorId:X4}:{productId:X4}",
            },
            HoldMs = SinowealthLighting.StoredHoldMs,
            RepaintMs = SinowealthLighting.StoredRepaintMs,
            Programs = VendorPrograms.Glorious,
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>SetMode for the static mode (LaviewTechnologyController.cpp:58-79).</summary>
        internal static byte[] Frame(int rgb)
        {
            var report = new byte[ReportSize];
            report[0] = 0xA1;
            report[1] = 0x0C;
            report[5] = 0x01;
            report[16] = StaticMode;
            report[56] = Brightness;
            report[58] = Speed;
            report[60] = 0;
            report[17] = (byte)(rgb >> 16);
            report[18] = (byte)(rgb >> 8);
            report[19] = (byte)rgb;
            return report;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing undoes the Custom mode: see the class comment.</summary>
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
