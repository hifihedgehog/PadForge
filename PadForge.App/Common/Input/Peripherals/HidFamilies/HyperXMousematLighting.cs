using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// HyperX's lit mousepads, the Fury Ultra, the Pulsefire Mat and the
    /// Pulsefire Mat RGB Mouse Pad XL, lit as OpenRGB's
    /// HyperXMousematController lights them (#557).
    ///
    /// <para>The collections. OpenRGB registers these through its wrapped
    /// HID pass (REGISTER_HID_WRAPPED_DETECTOR_I and _IPU,
    /// HyperXMousematControllerDetect.cpp:48-55). On Windows the wrapper is
    /// hidapi itself (default_hidapi_wrapper, DetectionManager.cpp:86-101,
    /// handed to the wrapped pass at line 1199). The libusb wrapper is
    /// loaded on Linux alone (lines 1468-1506), so the bytes on the wire are
    /// those of hid_send_feature_report. The Fury Ultra's registration names
    /// interface 0 and no usage page, and the collection that takes the
    /// frames is the one with feature reports of 65 bytes, report ID 0
    /// included, so the family takes that one.</para>
    ///
    /// <para>The lights. The Fury Ultra and the Pulsefire Mat have an
    /// underglow of 15 LEDs and a strip of 5, the XL an underglow of 2
    /// (HyperXMousematControllerDetect.cpp:36-37,
    /// RGBController_HyperXMousemat.cpp:70-114), every one an edge
    /// light.</para>
    ///
    /// <para>The frames. A picture is OpenRGB's Direct mode, three feature
    /// reports of 65 bytes: 00 04 F2 with 02 at byte 9, then two reports
    /// of sixteen entries of 0x81 and a color, the first 16 LEDs and the
    /// next 16 (SendDirect, HyperXMousematController.cpp:56-124). OpenRGB
    /// reads all 32 colors from a buffer that holds only the mat's 20 or 2,
    /// so the entries past them carry whatever memory follows. Here they
    /// carry black. Nothing is saved: the mat keeps the colors only while
    /// they keep coming, and the controller sends the last picture again
    /// whenever none went out for 50 ms (KeepaliveThread,
    /// RGBController_HyperXMousemat.cpp:116-151), which the worker does as
    /// the session's keep-alive.</para>
    ///
    /// <para>Hand-back: none. OpenRGB's Shutdown sends nothing, and
    /// SignalRGB's plugin sends a last color the user picks
    /// (signal-plugins HyperX_Fury_Ultra.js:70-72). When the frames stop the
    /// mat is taken to go back to its own lighting, as the keep-alive
    /// implies. That has not been seen on a device.</para>
    /// </summary>
    internal sealed class HyperXMousematLighting : IHidLightingFamily
    {
        public static readonly HyperXMousematLighting Instance = new();

        internal const string OpenRgbFamily = "HyperXMousematController";

        /// <summary>HYPERX_VID, HYPERX_VID_2 and HYPERX_FURY_A_XL_PID
        /// (HyperXMousematControllerDetect.cpp:20-25).</summary>
        internal const ushort KingstonVid = 0x0951;
        internal const ushort HpVid = 0x03F0;
        internal const ushort XlPid = 0x1741;

        /// <summary>The 65-byte feature reports, report ID 0 included
        /// (HyperXMousematController.cpp:61 and 80).</summary>
        internal const int ReportSize = 65;

        /// <summary>The colors a picture carries, sixteen to a report
        /// (lines 92-121).</summary>
        internal const int Entries = 32;

        /// <summary>The keep-alive (RGBController_HyperXMousemat.cpp:144).</summary>
        internal const int KeepAliveMs = 50;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it beyond the keep-alive, so the bound is PadForge's.</summary>
        internal const int RepaintMs = HyperXKeyboardLighting.RepaintMs;

        public string Name => "HyperX mousepads";

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
            Map = new LightMap(Lights(productId), "OpenRGB HyperXMousematController")
            {
                Product = $"hyperx:{registration.VendorId:X4}:{productId:X4}",
            },
            Programs = new[] { VendorPrograms.Ngenuity },
            RepaintMs = RepaintMs,
            KeepAliveMs = KeepAliveMs,
            KeepAliveIdleMs = KeepAliveMs,
            Begin = (io, wait) => io == null ? null : new Session(),
        };

        /// <summary>The number of LEDs: 2 on the XL, 15 and 5 on the others
        /// (HyperXMousematControllerDetect.cpp:36-37).</summary>
        internal static int LedCount(ushort productId) => productId == XlPid ? 2 : 20;

        /// <summary>Every LED an edge light, in OpenRGB's order.</summary>
        internal static IReadOnlyList<LightInfo> Lights(ushort productId)
        {
            var lights = new LightInfo[LedCount(productId)];
            for (int i = 0; i < lights.Length; i++) lights[i] = new LightInfo(i, LightKind.Edge, 0, LightKeySource.None);
            return lights;
        }

        /// <summary>The three reports of a picture (SendDirect,
        /// HyperXMousematController.cpp:56-124), from 32 colors by
        /// address.</summary>
        internal static byte[][] Frames(int[] colors)
        {
            var start = new byte[ReportSize];
            start[0x01] = 0x04;
            start[0x02] = 0xF2;
            start[0x09] = 0x02;
            var frames = new[] { start, new byte[ReportSize], new byte[ReportSize] };
            for (int half = 0; half < 2; half++)
                for (int i = 0; i < 16; i++)
                {
                    int color = colors[half * 16 + i];
                    var frame = frames[1 + half];
                    frame[i * 4 + 1] = 0x81;
                    frame[i * 4 + 2] = (byte)(color >> 16);
                    frame[i * 4 + 3] = (byte)(color >> 8);
                    frame[i * 4 + 4] = (byte)color;
                }
            return frames;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing hands a mat back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var frame in Frames(CoolerMasterLighting.ByAddress(lights, colors, Entries)))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
