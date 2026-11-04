using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Trust GXT 114 and GXT 180 mice (#557), lit as OpenRGB's
    /// TrustController family lights them: 145F:026D on usage page 0xFF00
    /// and 145F:0248 on usage page 0xFFA0, each on interface 1, usage 1
    /// (TrustControllerDetect.cpp:73-74). Each mouse has one light
    /// (TrustGXT114Controller.h:19, TrustGXT180Controller.h:19).
    ///
    /// <para>The GXT 114. OpenRGB makes a controller only when the mouse
    /// answers a read of feature report 0x04, 154 bytes
    /// (TrustControllerDetect.cpp:29-53, Test, TrustGXT114Controller.cpp:51-59),
    /// and a session reads it the same way before it writes. The mouse has
    /// no Direct mode ("@direct :x:", RGBController_TrustGXT114.cpp:16-25),
    /// so a picture is its Static mode: one feature report of 154 bytes,
    /// ID 0x04, with the mode 0x28 at 93, the brightness at 96 (Static
    /// takes its brightness there, its default the highest, 0xA2) and red,
    /// green and blue at 103, among the constant bytes SetMode writes
    /// (TrustGXT114Controller.cpp:61-125, RGBController_TrustGXT114.cpp:38-49
    /// and 124-127).</para>
    ///
    /// <para>The GXT 180. Its Static mode is per light: one feature report
    /// of 64 bytes, ID 0x03, 06 BB AA 2A 00 0A, red, green and blue at 8,
    /// the mode 0x01 at 11, the brightness at 13 (Static's default, 5) and
    /// the speed at 14, which Static does not set and OpenRGB's mode leaves
    /// at 0 (TrustGXT180Controller.cpp:51-81, RGBController_TrustGXT180.cpp:38-46,
    /// RGBControllerInterface.h:325-343).</para>
    ///
    /// <para>The model of the device. Every mode of both mice carries
    /// MODE_FLAG_AUTOMATIC_SAVE (RGBController_TrustGXT114.cpp:41,
    /// RGBController_TrustGXT180.cpp:41), so the mouse keeps each color
    /// it is sent, though the file headers say "@save :x:". A picture is
    /// held before it goes out and pictures are spaced, the bounds the
    /// stored writes of <see cref="SinowealthLighting"/> keep.</para>
    ///
    /// <para>Hand-back. No reference reads the mode the mouse ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// mouse keeps the last color in its Static mode. No source names the
    /// processes of Trust's software, so no program is named.</para>
    /// </summary>
    internal sealed class TrustLighting : IHidLightingFamily
    {
        public static readonly TrustLighting Instance = new();

        internal const string OpenRgbFamily = "TrustController";
        internal const ushort VendorId = 0x145F;

        internal const string Gxt114Detector = "DetectTrustGXT114Controllers";
        internal const string Gxt180Detector = "DetectTrustGXT180Controllers";

        /// <summary>TRUST_GXT_114_REPORT_SIZE and _REPORT_ID
        /// (TrustGXT114Controller.h:18-20), and the Static mode with its
        /// default brightness (lines 22-33).</summary>
        internal const int Gxt114Size = 154;
        internal const byte Gxt114ReportId = 0x04;
        internal const byte Gxt114Static = 0x28;
        internal const byte Gxt114Brightness = 0xA2;

        /// <summary>TRUST_GXT_180_REPORT_SIZE and _REPORT_ID
        /// (TrustGXT180Controller.h:18-20), and the Static mode with its
        /// default brightness (lines 22-34).</summary>
        internal const int Gxt180Size = 64;
        internal const byte Gxt180ReportId = 0x03;
        internal const byte Gxt180Static = 0x01;
        internal const byte Gxt180Brightness = 0x05;

        /// <summary>The bytes SetMode sets to 0xFF (TrustGXT114Controller.cpp:93-102).</summary>
        internal static readonly int[] Gxt114FfBytes =
        {
            101, 108, 109, 113, 114, 115, 117, 118, 119, 124, 127, 128,
            131, 134, 135, 138, 139, 141, 142, 143, 144,
        };

        public string Name => "Trust";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            foreach (var registration in OpenRgbRegistrations.Match(OpenRgbFamily, collection))
            {
                bool gxt114 = registration.Detector == Gxt114Detector;
                if (!gxt114 && registration.Detector != Gxt180Detector) continue;
                int size = gxt114 ? Gxt114Size : Gxt180Size;
                if (collection.FeatureReportLength != 0 && collection.FeatureReportLength < size) return null;
                ushort pid = collection.ProductId;
                return _models.Get(registration.Name + pid, () => Model(registration.Name, pid, gxt114));
            }
            return null;
        }

        /// <summary>One light, OpenRGB's one zone "Mouse".</summary>
        internal static IReadOnlyList<LightInfo> Lights()
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        private static HidLightingModel Model(string name, ushort productId, bool gxt114) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(),
                gxt114 ? "OpenRGB RGBController_TrustGXT114.cpp" : "OpenRGB RGBController_TrustGXT180.cpp")
            {
                Product = $"trust:{VendorId:X4}:{productId:X4}",
            },
            HoldMs = SinowealthLighting.StoredHoldMs,
            RepaintMs = SinowealthLighting.StoredRepaintMs,
            Begin = gxt114 ? (io, _) => BeginGxt114(io) : (io, _) => io == null ? null : new Session(false),
        };

        /// <summary>SetMode for the Static mode (TrustGXT114Controller.cpp:61-125).</summary>
        internal static byte[] Gxt114Frame(int rgb)
        {
            var report = new byte[Gxt114Size];
            report[0] = Gxt114ReportId;
            report[93] = Gxt114Static;
            report[94] = 0x00;
            report[95] = 0x00;
            report[96] = Gxt114Brightness;
            report[103] = (byte)(rgb >> 16);
            report[104] = (byte)(rgb >> 8);
            report[105] = (byte)rgb;
            for (int i = 79; i <= 89; i++) report[i] = 0x80;
            foreach (int i in Gxt114FfBytes) report[i] = 0xFF;
            report[8] = 0x4C;
            report[71] = 0x11;
            report[72] = 0xB0;
            report[74] = 0x89;
            report[75] = 0x0E;
            report[76] = 0x9D;
            report[77] = 0xA7;
            report[78] = 0xB7;
            report[148] = 0x58;
            report[149] = 0x30;
            report[150] = 0x31;
            report[151] = 0x30;
            report[152] = 0x31;
            report[153] = 0x30;
            return report;
        }

        /// <summary>SetMode for the Static mode (TrustGXT180Controller.cpp:51-81).</summary>
        internal static byte[] Gxt180Frame(int rgb)
        {
            var report = new byte[Gxt180Size];
            report[0] = Gxt180ReportId;
            report[1] = 0x06;
            report[2] = 0xBB;
            report[3] = 0xAA;
            report[4] = 0x2A;
            report[6] = 0x0A;
            report[8] = (byte)(rgb >> 16);
            report[9] = (byte)(rgb >> 8);
            report[10] = (byte)rgb;
            report[11] = Gxt180Static;
            report[13] = Gxt180Brightness;
            report[14] = 0x00;
            return report;
        }

        /// <summary>The detector's Test: report 0x04 read
        /// (TrustGXT114Controller.cpp:51-59). Null when it is not
        /// answered.</summary>
        private static HidLightingSession BeginGxt114(IHidReportChannel io)
        {
            if (io == null) return null;
            var answer = new byte[Math.Max(Gxt114Size, io.FeatureReportLength)];
            answer[0] = Gxt114ReportId;
            return io.GetFeature(Gxt114ReportId, answer) ? new Session(true) : null;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly bool _gxt114;

            public Session(bool gxt114) => _gxt114 = gxt114;

            public override bool Feature => true;

            /// <summary>Nothing undoes the Static mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
                return io.SetFeature(_gxt114 ? Gxt114Frame(rgb) : Gxt180Frame(rgb))
                    ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
