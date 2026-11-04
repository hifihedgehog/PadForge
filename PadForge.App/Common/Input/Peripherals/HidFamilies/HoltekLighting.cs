using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Holtek's USB gaming mouse (04D9:A070) and mousemat (04D9:A1FA), lit as
    /// OpenRGB's HoltekController family lights them (#557). The collections
    /// are OpenRGB's registrations: the mouse's on interface 1, usage page
    /// 0xFF00, usage 2 (HoltekA070ControllerDetect.cpp:43), the mat's on
    /// interface 2, usage page and usage 0xFF00 (HoltekA1FAControllerDetect.cpp:43).
    /// Each has one light.
    ///
    /// <para>The mouse. A picture is OpenRGB's SET RGB command, a feature
    /// report of 8 bytes: 07 0A 00 and the color (SendCustomColor,
    /// HoltekA070Controller.cpp:55-89, which DeviceUpdateLEDs sends,
    /// RGBController_HoltekA070.cpp:79-86). Its save byte is 0 and "does
    /// not work with SET RGB", and the color "will therefore reset after a
    /// power cycle" (lines 74 and 81-83). The mode command is the one that
    /// saves (07 0B with 01 at byte 2, SendMode, lines 91-120), so it is
    /// never sent: the mouse keeps the mode it has, and in its breathing
    /// modes breathes in PadForge's color. Nothing is stored, so pictures go
    /// out with no hold.</para>
    ///
    /// <para>The mat. A picture is OpenRGB's Static mode, a feature report
    /// of 9 bytes: 00 08, the mode 0, the brightness 0x20 OpenRGB fixes
    /// ("When brightness support is added, change this"), the speed 0, no
    /// preset, and the color (SendData, HoltekA1FAController.cpp:55-75,
    /// DeviceUpdateLEDs and the Static mode's value and speed,
    /// RGBController_HoltekA1FA.cpp:36-45 and 107-118). OpenRGB lists no
    /// save for it ("@save :x:", line 18), and no reference says whether
    /// the mat keeps that mode and color in its memory, so a changed
    /// picture is held and pictures are spaced as the Lenovo Gen 10
    /// keyboard's stored writes are (<see cref="LenovoLegionLighting"/>).</para>
    ///
    /// <para>Hand-back: none. OpenRGB reads neither device's state, and no
    /// other reference drives them. The mouse shows PadForge's last color
    /// until it powers off, and the mat keeps its last picture until the
    /// user sets another. No source names a Holtek program, so none is in
    /// the vendor-program table.</para>
    /// </summary>
    internal sealed class HoltekLighting : IHidLightingFamily
    {
        public static readonly HoltekLighting Instance = new();

        internal const string OpenRgbFamily = "HoltekController";

        /// <summary>HOLTEK_VID (HoltekA070ControllerDetect.cpp:18).</summary>
        internal const ushort HoltekVid = 0x04D9;

        /// <summary>The detectors of the mouse and the mat
        /// (HoltekA070ControllerDetect.cpp:43, HoltekA1FAControllerDetect.cpp:43).</summary>
        internal const string MouseDetector = "DetectHoltekA070Controllers";
        internal const string MatDetector = "DetectHoltekA1FAControllers";

        /// <summary>The mouse's 8-byte report and the mat's 9-byte one
        /// (HoltekA070Controller.cpp:62, HoltekA1FAController.cpp:57).</summary>
        internal const int MouseReportSize = 8;
        internal const int MatReportSize = 9;

        /// <summary>The brightness OpenRGB sends the mat
        /// (RGBController_HoltekA1FA.cpp:110).</summary>
        internal const byte MatBrightness = 0x20;

        /// <summary>The mouse's shortest time between two pictures. No
        /// reference bounds it, so the bound is PadForge's, as the HyperX
        /// mice's.</summary>
        internal const int MouseRepaintMs = HyperXKeyboardLighting.RepaintMs;

        /// <summary>The mat's bounds, those of a device whose writes may be
        /// stored.</summary>
        internal const int MatHoldMs = LenovoLegionLighting.HoldMs;
        internal const int MatRepaintMs = LenovoLegionLighting.RepaintMs;

        public string Name => "Holtek";

        private readonly object _gate = new();
        private HidLightingModel _mouse, _mat;

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != HoltekVid) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            bool mat = registration.Detector == MatDetector;
            if (!mat && registration.Detector != MouseDetector) return null;
            if (collection.FeatureReportLength < (mat ? MatReportSize : MouseReportSize)) return null;
            lock (_gate)
                return mat
                    ? _mat ??= Model(registration.Name, collection.ProductId, mat: true)
                    : _mouse ??= Model(registration.Name, collection.ProductId, mat: false);
        }

        internal static HidLightingModel Model(string name, ushort productId, bool mat) => new()
        {
            Family = Instance,
            Name = name,
            // One zone, "Mouse" or "Mousemat" (RGBController_HoltekA070.cpp:62-77,
            // RGBController_HoltekA1FA.cpp:90-105).
            Map = new LightMap(SinowealthLighting.OneLight(),
                mat ? "OpenRGB RGBController_HoltekA1FA.cpp" : "OpenRGB RGBController_HoltekA070.cpp")
            {
                Product = $"holtek:{HoltekVid:X4}:{productId:X4}",
            },
            HoldMs = mat ? MatHoldMs : 0,
            RepaintMs = mat ? MatRepaintMs : MouseRepaintMs,
            Begin = (io, wait) => io == null ? null : new Session(mat),
        };

        /// <summary>SET RGB: 07 0A 00, the color, 00 00
        /// (HoltekA070Controller.cpp:62-88).</summary>
        internal static byte[] MouseFrame(int rgb) => new byte[]
        {
            0x07, 0x0A, 0x00, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0x00, 0x00,
        };

        /// <summary>The Static mode: 00 08, the mode 0, the brightness, the
        /// speed 0, the preset 0, the color (HoltekA1FAController.cpp:55-75).</summary>
        internal static byte[] MatFrame(int rgb) => new byte[]
        {
            0x00, 0x08, 0x00, MatBrightness, 0x00, 0x00, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb,
        };

        internal sealed class Session : HidLightingSession
        {
            public Session(bool mat) => Mat = mat;

            public bool Mat { get; }

            public override bool Feature => true;

            /// <summary>Nothing hands either device back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                int rgb = CoolerMasterLighting.ByAddress(lights, colors, 1)[0];
                return io.SetFeature(Mat ? MatFrame(rgb) : MouseFrame(rgb)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
