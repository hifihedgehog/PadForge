using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Areson-made mice, the ZET GAMING Edge Air Pro and Edge Air Elit
    /// and the Redragon M914 NIX, each wired and on its receiver, lit as
    /// OpenRGB's AresonController lights them (#557). The collections are
    /// OpenRGB's registrations, interface 1, usage page 0xFF02, usage 2
    /// (AresonControllerDetect.cpp:49-54), with feature reports of 17 bytes,
    /// report ID 8 included (ARESON_PACKET_SIZE and ARESON_REPORT_ID,
    /// AresonController.h:18-19).
    ///
    /// <para>The mouse has one light. OpenRGB has no Direct mode for it and
    /// colors it through the Static mode, one feature report per picture:
    /// 08 07 00 00 A0 07, the mode 0x02, the color, the speed 0, the
    /// brightness, a check byte of 0x55 less bytes 6 to 11, and 0x4A at byte
    /// 16 (SetMode, AresonController.cpp:79-140). The Static mode's
    /// brightness is OpenRGB's default, the highest of ten, whose value is
    /// 0xFF (lines 116-124, RGBController_Areson.cpp:38-46), and its speed
    /// has no value (GetSpeedValue, AresonController.cpp:51-77).</para>
    ///
    /// <para>OpenRGB marks every mode as one the mouse saves on its own
    /// ("@save :x:" for a Save button it lacks, and
    /// MODE_FLAG_AUTOMATIC_SAVE on every mode,
    /// RGBController_Areson.cpp:16-121), so every picture lands in the
    /// mouse's memory. A changed picture is held and pictures are spaced as
    /// the Lenovo Gen 10 keyboard's stored writes are
    /// (<see cref="LenovoLegionLighting"/>).</para>
    ///
    /// <para>Hand-back: none. OpenRGB reads nothing back, and no other
    /// reference drives these mice, so the mouse keeps PadForge's last
    /// color, stored, until the user or the maker's software sets another.
    /// No source names ZET's or Redragon's software processes, so none is
    /// in the vendor-program table.</para>
    /// </summary>
    internal sealed class AresonLighting : IHidLightingFamily
    {
        public static readonly AresonLighting Instance = new();

        internal const string OpenRgbFamily = "AresonController";

        /// <summary>ARESON_VID (AresonControllerDetect.cpp:19).</summary>
        internal const ushort AresonVid = 0x25A7;

        /// <summary>ARESON_PACKET_SIZE, ARESON_REPORT_ID and ARESON_PACKET_END
        /// (AresonController.h:18-20).</summary>
        internal const int ReportSize = 17;
        internal const byte ReportId = 0x08;
        internal const byte PacketEnd = 0x4A;

        /// <summary>STATIC_MODE_VALUE (AresonController.h:26).</summary>
        internal const byte StaticMode = 0x02;

        /// <summary>The brightness value of ARESON_BRIGHTNESS_MAX, the tenth
        /// of brightness_values (AresonController.cpp:118-123).</summary>
        internal const byte FullBrightness = 0xFF;

        /// <summary>The bounds of a mouse that stores every picture.</summary>
        internal const int HoldMs = LenovoLegionLighting.HoldMs;
        internal const int RepaintMs = LenovoLegionLighting.RepaintMs;

        public string Name => "Areson mice";

        private readonly Dictionary<int, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != AresonVid) return null;
            if (collection.FeatureReportLength < ReportSize) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            lock (_models)
            {
                if (!_models.TryGetValue(collection.ProductId, out var model))
                    _models[collection.ProductId] = model = Model(registrations[0].Name, collection.ProductId);
                return model;
            }
        }

        internal static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            // One zone, "Mouse" (RGBController_Areson.cpp:133-152).
            Map = new LightMap(SinowealthLighting.OneLight(), "OpenRGB RGBController_Areson.cpp")
            {
                Product = $"areson:{AresonVid:X4}:{productId:X4}",
            },
            HoldMs = HoldMs,
            RepaintMs = RepaintMs,
            Begin = (io, wait) => io == null ? null : new Session(),
        };

        /// <summary>SetMode's report for the Static mode in
        /// <paramref name="rgb"/> (AresonController.cpp:79-140).</summary>
        internal static byte[] Frame(int rgb)
        {
            var frame = new byte[ReportSize];
            frame[0x00] = ReportId;
            frame[0x01] = 0x07;
            frame[0x04] = 0xA0;
            frame[0x05] = 0x07;
            frame[0x06] = StaticMode;
            frame[0x07] = (byte)(rgb >> 16);
            frame[0x08] = (byte)(rgb >> 8);
            frame[0x09] = (byte)rgb;
            frame[0x0A] = 0x00;
            frame[0x0B] = FullBrightness;
            frame[0x0C] = (byte)(0x55 - frame[0x06] - frame[0x07] - frame[0x08] - frame[0x09] - frame[0x0A] - frame[0x0B]);
            frame[0x10] = PacketEnd;
            return frame;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing hands the mouse back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                return io.SetFeature(Frame(CoolerMasterLighting.ByAddress(lights, colors, 1)[0]))
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
