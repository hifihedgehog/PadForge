using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The mice built on Instant Microelectronics' chip, the Advanced GTA
    /// 250, the two Anko mice and the AntEsports GM600, lit as OpenRGB's
    /// InstantMouseController lights them (#557). The collections are
    /// OpenRGB's registrations, interface 1, usage page 0xFF01, usage 1
    /// (InstantMouseControllerDetect.cpp:35-38), with feature reports of 8
    /// bytes, report ID 7 included (INSTANT_MOUSE_REPORT_ID and _SIZE,
    /// InstantMouseController.h:18-19).
    ///
    /// <para>The mouse has one light. A session's first picture selects
    /// OpenRGB's Direct mode, 07 13 FF, the mode 0x0A in the high half of
    /// byte 3 and the speed 0 in the low, the direction 0 and the LED mask
    /// 0x0B in byte 4, 0x0F less the brightness 7 in byte 5, and the mode's
    /// high half in byte 6 (SetMode, InstantMouseController.cpp:83-116, the
    /// Direct mode's value and brightness, RGBController_InstantMouse.cpp:39-47,
    /// and OpenRGB's mode defaults of 0, RGBControllerInterface.h:325-343).
    /// Each picture then sends the color as OpenRGB's SendColor does: each
    /// channel as 0x0F less its top four bits, green with a slot number in
    /// byte 2 and red and blue in byte 3, once for each slot 0, 2, 4, 6, 8
    /// and 0xA (lines 57-81, "X is the DPI slot").</para>
    ///
    /// <para>OpenRGB lists no save for these mice ("@save :x:",
    /// RGBController_InstantMouse.cpp:19) and no reference says whether the
    /// mouse keeps the mode and the slot colors in its memory. They are
    /// settings of the mouse's own, so a changed picture is held and
    /// pictures are spaced as the Lenovo Gen 10 keyboard's stored writes
    /// are (<see cref="LenovoLegionLighting"/>).</para>
    ///
    /// <para>Hand-back: none. OpenRGB reads nothing back, and no other
    /// reference drives these mice, so the mouse keeps PadForge's last color
    /// in Direct mode until the user or the maker's software sets another.
    /// No source names that software's processes, so none is in the
    /// vendor-program table.</para>
    /// </summary>
    internal sealed class InstantMouseLighting : IHidLightingFamily
    {
        public static readonly InstantMouseLighting Instance = new();

        internal const string OpenRgbFamily = "InstantMouseController";

        /// <summary>INSTANT_MICROELECTRONICS_VID (InstantMouseDevices.h:15).</summary>
        internal const ushort InstantVid = 0x30FA;

        /// <summary>INSTANT_MOUSE_REPORT_ID, _REPORT_SIZE, _SET_MODE and
        /// _SET_COLOR (InstantMouseController.h:18-21).</summary>
        internal const byte ReportId = 0x07;
        internal const int ReportSize = 8;
        internal const byte SetModeCommand = 0x13;
        internal const byte SetColorCommand = 0x14;

        /// <summary>INSTANT_MOUSE_DIRECT_MODE and INSTANT_MOUSE_BRIGHTNESS_MAX
        /// (InstantMouseController.h:25 and 46).</summary>
        internal const byte DirectMode = 0x0A;
        internal const byte BrightnessMax = 0x07;

        /// <summary>The bounds of a mouse whose writes may be stored.</summary>
        internal const int HoldMs = LenovoLegionLighting.HoldMs;
        internal const int RepaintMs = LenovoLegionLighting.RepaintMs;

        public string Name => "Instant mice";

        private readonly Dictionary<int, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != InstantVid) return null;
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
            // One zone, "Mouse" (RGBController_InstantMouse.cpp:168-184).
            Map = new LightMap(SinowealthLighting.OneLight(), "OpenRGB RGBController_InstantMouse.cpp")
            {
                Product = $"instant:{InstantVid:X4}:{productId:X4}",
            },
            HoldMs = HoldMs,
            RepaintMs = RepaintMs,
            Begin = (io, wait) => io == null ? null : new Session(),
        };

        /// <summary>SetMode for the Direct mode: speed 0, brightness 7,
        /// direction 0 (InstantMouseController.cpp:83-116).</summary>
        internal static byte[] DirectModeFrame()
        {
            const byte speed = 0, direction = 0, ledMask = 0x0B;
            return new byte[]
            {
                ReportId, SetModeCommand, 0xFF,
                (byte)(DirectMode << 4 | speed & 0xF),
                (byte)(direction << 4 | ledMask),
                (byte)(0xF - (BrightnessMax & 0xF)),
                (byte)(DirectMode & 0xF0),
                0x00,
            };
        }

        /// <summary>SendColor's six reports (InstantMouseController.cpp:57-81).</summary>
        internal static byte[][] ColorFrames(int rgb)
        {
            int red = 0xF - ((rgb >> 16) & 0xFF) / 16;
            int green = 0xF - ((rgb >> 8) & 0xFF) / 16;
            int blue = 0xF - (rgb & 0xFF) / 16;
            var frames = new List<byte[]>();
            for (int slot = 0; slot <= 0xA; slot += 2)
            {
                var frame = new byte[ReportSize];
                frame[0] = ReportId;
                frame[1] = SetColorCommand;
                frame[2] = (byte)(slot << 4 | green & 0xF);
                frame[3] = (byte)(red << 4 | blue & 0xF);
                frames.Add(frame);
            }
            return frames.ToArray();
        }

        internal sealed class Session : HidLightingSession
        {
            private bool _direct;

            public override bool Feature => true;

            /// <summary>Nothing hands the mouse back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                if (!_direct)
                {
                    if (!io.SetFeature(DirectModeFrame())) return HidLightingPaint.Failed;
                    _direct = true;
                }
                foreach (var frame in ColorFrames(CoolerMasterLighting.ByAddress(lights, colors, 1)[0]))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
