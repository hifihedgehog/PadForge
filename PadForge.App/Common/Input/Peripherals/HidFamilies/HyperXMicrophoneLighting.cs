using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// HyperX's QuadCast S and DuoCast microphones, lit as OpenRGB's
    /// HyperXMicrophoneController lights them (#557).
    ///
    /// <para>The collections. OpenRGB registers each product on interface 0
    /// alone through its wrapped HID pass
    /// (HyperXMicrophoneControllerDetect.cpp:52-59). The wrapper exists for
    /// Linux, where interface 0 has no interrupt IN endpoint and only
    /// hidapi's libusb backend finds it, and on Windows "hidapi finds and
    /// interacts with it just fine" (RGBController_HyperXMicrophone.cpp:19-40),
    /// through the default wrapper, which is hidapi itself
    /// (DetectionManager.cpp:86-101 and 1199). The bytes on the wire are
    /// those of hid_send_feature_report. The registration names no usage
    /// page, so the family takes the collection of interface 0 with feature
    /// reports of 65 bytes, report ID 0 included, the length every frame
    /// has (HYPERX_QUADCAST_S_PACKET_SIZE, HyperXMicrophoneController.h:18).</para>
    ///
    /// <para>The lights. The top and the bottom of the capsule, the two LEDs
    /// of OpenRGB's "Microphone" zone, in that order
    /// (RGBController_HyperXMicrophone.cpp:90-112), each a zone
    /// here.</para>
    ///
    /// <para>The frames. A picture is OpenRGB's Direct mode (SendDirect,
    /// HyperXMicrophoneController.cpp:138-174): a feature report of 0x81 and
    /// the top's color, 0x81 and the bottom's, then 15 ms, then the apply
    /// register write, 04 F2 with 00 01 at bytes 8 and 9 (SendToRegister,
    /// lines 193-206), then 15 ms. The save transaction, register 0x53 and
    /// the frames after it (SaveColors, lines 61-136), goes out only for
    /// OpenRGB's manual Save, and never here. The microphone keeps the
    /// colors only while they keep coming: the controller sends the last
    /// picture again whenever none went out for 50 ms, checked every 15 ms
    /// (KeepaliveThread, RGBController_HyperXMicrophone.cpp:141-151), which
    /// the worker does as the session's keep-alive.</para>
    ///
    /// <para>Hand-back: none. OpenRGB's Shutdown sends nothing, and
    /// SignalRGB's plugin sends a last color the user picks
    /// (signal-plugins HyperX_Quadcast_S.js). When the frames stop the
    /// microphone is taken to go back to the lighting it stores, as the
    /// keep-alive implies. That has not been seen on a device.</para>
    /// </summary>
    internal sealed class HyperXMicrophoneLighting : IHidLightingFamily
    {
        public static readonly HyperXMicrophoneLighting Instance = new();

        internal const string OpenRgbFamily = "HyperXMicrophoneController";

        /// <summary>HYPERX_VID and HYPERX_HP_VID
        /// (HyperXMicrophoneControllerDetect.cpp:20-21).</summary>
        internal const ushort KingstonVid = 0x0951;
        internal const ushort HpVid = 0x03F0;

        /// <summary>HYPERX_QUADCAST_S_PACKET_SIZE (HyperXMicrophoneController.h:18).</summary>
        internal const int ReportSize = 65;

        /// <summary>The wait after each report
        /// (HyperXMicrophoneController.cpp:169 and 205).</summary>
        internal const int ReportWaitMs = 15;

        /// <summary>The keep-alive (RGBController_HyperXMicrophone.cpp:145).</summary>
        internal const int KeepAliveMs = 50;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it beyond the keep-alive, so the bound is PadForge's.</summary>
        internal const int RepaintMs = HyperXKeyboardLighting.RepaintMs;

        public string Name => "HyperX microphones";

        private readonly Dictionary<int, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || (collection.VendorId != KingstonVid && collection.VendorId != HpVid)) return null;
            if (collection.FeatureReportLength < ReportSize) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            lock (_models)
            {
                int key = collection.VendorId << 16 | collection.ProductId;
                if (!_models.TryGetValue(key, out var model))
                    _models[key] = model = Model(registration, collection.ProductId);
                return model;
            }
        }

        internal static HidLightingModel Model(OpenRgbRegistration registration, ushort productId) => new()
        {
            Family = Instance,
            Name = registration.Name,
            Map = new LightMap(Lights(), "OpenRGB HyperXMicrophoneController")
            {
                Product = $"hyperx:{registration.VendorId:X4}:{productId:X4}",
            },
            Programs = new[] { VendorPrograms.Ngenuity },
            RepaintMs = RepaintMs,
            KeepAliveMs = KeepAliveMs,
            KeepAliveIdleMs = KeepAliveMs,
            Begin = (io, wait) => io == null ? null : new Session(),
        };

        /// <summary>The top, then the bottom.</summary>
        internal static IReadOnlyList<LightInfo> Lights() => new[]
        {
            new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
            new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
        };

        /// <summary>The color report: 00 81, the top's color, 81, the
        /// bottom's (HyperXMicrophoneController.cpp:155-164).</summary>
        internal static byte[] ColorFrame(int top, int bottom)
        {
            var frame = new byte[ReportSize];
            frame[0x01] = 0x81;
            frame[0x02] = (byte)(top >> 16);
            frame[0x03] = (byte)(top >> 8);
            frame[0x04] = (byte)top;
            frame[0x05] = 0x81;
            frame[0x06] = (byte)(bottom >> 16);
            frame[0x07] = (byte)(bottom >> 8);
            frame[0x08] = (byte)bottom;
            return frame;
        }

        /// <summary>SendToRegister(0xF2, 0, 1), the apply: 00 04 F2, 00 at
        /// byte 8 and 01 at byte 9 (HyperXMicrophoneController.cpp:171 and
        /// 193-206).</summary>
        internal static byte[] ApplyFrame()
        {
            var frame = new byte[ReportSize];
            frame[0x01] = 0x04;
            frame[0x02] = 0xF2;
            frame[0x08] = 0x00;
            frame[0x09] = 0x01;
            return frame;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing hands a microphone back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                var map = CoolerMasterLighting.ByAddress(lights, colors, 2);
                if (!io.SetFeature(ColorFrame(map[0], map[1]))) return HidLightingPaint.Failed;
                wait(ReportWaitMs);
                if (!io.SetFeature(ApplyFrame())) return HidLightingPaint.Failed;
                wait(ReportWaitMs);
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
