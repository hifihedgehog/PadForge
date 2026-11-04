using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// HyperX's Pulsefire mice, Kingston's and HP's, lit as OpenRGB's
    /// HyperXMouseController family lights them (#557). The collections are
    /// OpenRGB's Windows registrations (HyperXMouseControllerDetect.cpp:130-145),
    /// matched by OpenRGB's rules, and the detector picks the protocol.
    ///
    /// <para>The Surge, the FPS Pro and Core and the Haste are driven in
    /// OpenRGB's Direct mode alone, as feature reports, and keep their
    /// colors only while they keep coming: each RGBController sends the
    /// last picture again whenever none went out for 50 ms, checked every
    /// 10 ms, and each picture, a resend included, starts the wait again
    /// (KeepaliveThread and DeviceUpdateLEDs,
    /// RGBController_HyperXPulsefireSurge.cpp:107-149,
    /// RGBController_HyperXPulsefireFPSPro.cpp:99-141,
    /// RGBController_HyperXPulsefireHaste.cpp:99-141). The Raid's wait is a
    /// second ("This devices requires a keepalive thread or it will reset
    /// to default", RGBController_HyperXPulsefireRaid.cpp:51-56 and
    /// 121-135). The worker sends the last picture again on the same
    /// terms, as the session's keep-alive
    /// (<see cref="HidLightingModel.KeepAliveMs"/>). None of these frames
    /// is a save: the Surge's controller has select-profile and brightness
    /// packets OpenRGB never sends (HyperXPulsefireSurgeController.cpp:55-108),
    /// and the others have no other packet. When the frames stop, the mouse
    /// is taken to go back to its own lighting, as the keep-alive implies.
    /// That has not been seen on a device.</para>
    ///
    /// <para>The Dart, wired or on its dongle, takes output reports of 65
    /// bytes: D2, the LED (0x10 the wheel, 0x00 the logo), the static mode
    /// 0x00, 08, the color twice, the brightness 0x64 and the speed 0, then
    /// 2 ms, the wheel first (SendDirect, HyperXPulsefireDartController.cpp:55-96,
    /// and DeviceUpdateMode with the Direct mode, RGBController_HyperXPulsefireDart.cpp:36-45
    /// and 149-160). OpenRGB runs no keep-alive for it. The save, DE FF
    /// (lines 98-122), goes out only for OpenRGB's manual Save, and never
    /// here, so the static color lives in the mouse's working state until
    /// it powers off.</para>
    ///
    /// <para>Hand-back: none. OpenRGB's Shutdown sends nothing for any of
    /// them, and SignalRGB's plugins send a last color the user picks
    /// (signal-plugins HyperX_Pulsefire_Core.js:51-53). The mice with a
    /// keep-alive are taken to return to their own lighting once the
    /// frames stop. A Dart shows the last picture until it powers off or
    /// NGENUITY sets another.</para>
    /// </summary>
    internal sealed class HyperXMouseLighting : IHidLightingFamily
    {
        public static readonly HyperXMouseLighting Instance = new();

        internal const string OpenRgbFamily = "HyperXMouseController";

        /// <summary>HYPERX_VID and HYPERX_VID_2
        /// (HyperXMouseControllerDetect.cpp:25-26).</summary>
        internal const ushort KingstonVid = 0x0951;
        internal const ushort HpVid = 0x03F0;

        /// <summary>The keep-alive of the Surge, the FPS Pro and the Haste:
        /// a picture goes out again once none went out for this long.</summary>
        internal const int KeepAliveMs = 50;

        /// <summary>The Raid's keep-alive
        /// (RGBController_HyperXPulsefireRaid.cpp:127).</summary>
        internal const int RaidKeepAliveMs = 1000;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it beyond the keep-alive, so the bound is PadForge's, as
        /// the HyperX keyboards'.</summary>
        internal const int RepaintMs = HyperXKeyboardLighting.RepaintMs;

        /// <summary>The length of the 264-byte feature reports of the Surge,
        /// the FPS Pro and the Raid, report ID 7 included
        /// (HyperXPulsefireSurgeController.cpp:115, HYPERX_PULSFIRE_RAID_PACKET_DATA_LENGTH,
        /// HyperXPulsefireRaidController.h:18).</summary>
        internal const int LongReport = 264;

        /// <summary>The Haste's feature reports and the Dart's output
        /// reports: report ID 0 and 64 bytes (HyperXPulsefireHasteController.cpp:66,
        /// HYPERX_PULSEFIRE_DART_PACKET_SIZE, HyperXPulsefireDartController.h:21).</summary>
        internal const int ShortReport = 65;

        /// <summary>The waits after a report: 10 ms after the Raid's
        /// (HyperXPulsefireRaidController.cpp:74-78), 2 ms after each of the
        /// Dart's (HyperXPulsefireDartController.cpp:95).</summary>
        internal const int RaidWaitMs = 10;
        internal const int DartWaitMs = 2;

        public string Name => "HyperX mice";

        /// <summary>The protocol a detector drives.</summary>
        internal enum Protocol
        {
            Surge,
            FpsPro,
            Haste,
            Dart,
            Raid,
        }

        internal static Protocol? ProtocolOf(string detector) => detector switch
        {
            "DetectHyperXPulsefireSurgeControllers" => Protocol.Surge,
            "DetectHyperXPulsefireFPSProControllers" => Protocol.FpsPro,
            "DetectHyperXPulsefireHasteControllers" => Protocol.Haste,
            "DetectHyperXPulsefireDartControllers" => Protocol.Dart,
            "DetectHyperXPulsefireRaidControllers" => Protocol.Raid,
            _ => null,
        };

        private readonly Dictionary<(string Detector, int ProductId), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || (collection.VendorId != KingstonVid && collection.VendorId != HpVid)) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            if (ProtocolOf(registration.Detector) is not Protocol protocol) return null;
            // The registration names an interface and a usage page, and the
            // collection that takes the frames is the one with reports long
            // enough for them.
            if (protocol == Protocol.Dart
                    ? collection.OutputReportLength < ShortReport
                    : collection.FeatureReportLength < (protocol == Protocol.Haste ? ShortReport : LongReport))
                return null;
            lock (_models)
            {
                var key = (registration.Detector, (int)collection.ProductId);
                if (!_models.TryGetValue(key, out var model))
                    _models[key] = model = Model(registration, protocol, collection.ProductId);
                return model;
            }
        }

        internal static HidLightingModel Model(OpenRgbRegistration registration, Protocol protocol, ushort productId)
        {
            int keepAlive = protocol switch
            {
                Protocol.Raid => RaidKeepAliveMs,
                Protocol.Dart => 0,
                _ => KeepAliveMs,
            };
            return new HidLightingModel
            {
                Family = Instance,
                Name = registration.Name,
                Map = new LightMap(Lights(protocol), "OpenRGB HyperXMouseController")
                {
                    Product = $"hyperx:{registration.VendorId:X4}:{productId:X4}",
                },
                Programs = new[] { VendorPrograms.Ngenuity },
                RepaintMs = RepaintMs,
                KeepAliveMs = keepAlive,
                KeepAliveIdleMs = keepAlive,
                Begin = (io, wait) => io == null ? null : new Session(protocol),
            };
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>The lights in OpenRGB's LED order, each light's address
        /// its place in it: the Surge's 32-LED strip around the mouse and
        /// its logo (RGBController_HyperXPulsefireSurge.cpp:68-105), the
        /// logo alone of the FPS Pro, the Core and the Haste
        /// (RGBController_HyperXPulsefireFPSPro.cpp:68-97,
        /// RGBController_HyperXPulsefireHaste.cpp:68-97), and the scroll
        /// wheel and the logo of the Raid and the Dart
        /// (RGBController_HyperXPulsefireRaid.cpp:70-93,
        /// RGBController_HyperXPulsefireDart.cpp:96-125).</summary>
        internal static IReadOnlyList<LightInfo> Lights(Protocol protocol)
        {
            switch (protocol)
            {
                case Protocol.Surge:
                {
                    var lights = new LightInfo[33];
                    for (int i = 0; i < 32; i++) lights[i] = new LightInfo(i, LightKind.Edge, 0, LightKeySource.None);
                    lights[32] = new LightInfo(32, LightKind.Logo, 0, LightKeySource.None);
                    return lights;
                }
                case Protocol.Raid:
                case Protocol.Dart:
                    return new[]
                    {
                        new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
                        new LightInfo(1, LightKind.Logo, 0, LightKeySource.None),
                    };
                default:
                    return new[] { new LightInfo(0, LightKind.Logo, 0, LightKeySource.None) };
            }
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        private static void Put(byte[] frame, int at, int color)
        {
            frame[at] = (byte)(color >> 16);
            frame[at + 1] = (byte)(color >> 8);
            frame[at + 2] = (byte)color;
        }

        /// <summary>The Surge's direct report: 07 14 00 A0, the strip's 32
        /// reds from byte 0x08, greens from 0x28 and blues from 0x48, the
        /// logo's color at 0x6C, 264 bytes (SendDirect,
        /// HyperXPulsefireSurgeController.cpp:110-152).</summary>
        internal static byte[] SurgeFrame(int[] colors)
        {
            var frame = new byte[LongReport];
            frame[0x00] = 0x07;
            frame[0x01] = 0x14;
            frame[0x03] = 0xA0;
            for (int i = 0; i < 32; i++)
            {
                frame[0x08 + i] = (byte)(colors[i] >> 16);
                frame[0x28 + i] = (byte)(colors[i] >> 8);
                frame[0x48 + i] = (byte)colors[i];
            }
            Put(frame, 0x6C, colors[32]);
            return frame;
        }

        /// <summary>The FPS Pro's and the Core's direct report: 07 0A, the
        /// color, 0xA0 at byte 8, 264 bytes (SendDirect,
        /// HyperXPulsefireFPSProController.cpp:55-83).</summary>
        internal static byte[] FpsProFrame(int color)
        {
            var frame = new byte[LongReport];
            frame[0x00] = 0x07;
            frame[0x01] = 0x0A;
            Put(frame, 0x02, color);
            frame[0x08] = 0xA0;
            return frame;
        }

        /// <summary>The Haste's two reports: the setup, 00 04 F2 with 02 at
        /// byte 8, then the color, 00 81, the color, 02 at byte 8, 65 bytes
        /// each (SendDirectSetup and SendDirectColor,
        /// HyperXPulsefireHasteController.cpp:51-116).</summary>
        internal static byte[][] HasteFrames(int color)
        {
            var setup = new byte[ShortReport];
            setup[0x01] = 0x04;
            setup[0x02] = 0xF2;
            setup[0x08] = 0x02;
            var paint = new byte[ShortReport];
            paint[0x01] = 0x81;
            Put(paint, 0x02, color);
            paint[0x08] = 0x02;
            return new[] { setup, paint };
        }

        /// <summary>The Raid's report: 07 0A, the wheel's color, the logo's,
        /// 0xA0 at byte 8, 264 bytes (SendColors,
        /// HyperXPulsefireRaidController.cpp:53-71).</summary>
        internal static byte[] RaidFrame(int wheel, int logo)
        {
            var frame = new byte[LongReport];
            frame[0] = 0x07;
            frame[1] = 0x0A;
            Put(frame, 2, wheel);
            Put(frame, 5, logo);
            frame[8] = 0xA0;
            return frame;
        }

        /// <summary>HYPERX_PULSEFIRE_DART_LED_LOGO and _SCROLL
        /// (HyperXPulsefireDartController.h:36-37).</summary>
        internal const byte DartLogo = 0x00;
        internal const byte DartScroll = 0x10;

        /// <summary>One of the Dart's reports: 00 D2, the LED, the static
        /// mode 0x00, 08, the color twice, the brightness 0x64 and the speed
        /// 0, 65 bytes (SendDirect, HyperXPulsefireDartController.cpp:55-96,
        /// the Direct mode's value, speed and brightness,
        /// RGBController_HyperXPulsefireDart.cpp:36-45).</summary>
        internal static byte[] DartFrame(byte led, int color)
        {
            var frame = new byte[ShortReport];
            frame[0x01] = 0xD2;
            frame[0x02] = led;
            frame[0x03] = 0x00;
            frame[0x04] = 0x08;
            Put(frame, 0x05, color);
            Put(frame, 0x08, color);
            frame[0x0B] = 0x64;
            frame[0x0C] = 0x00;
            return frame;
        }

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        internal sealed class Session : HidLightingSession
        {
            public Session(Protocol protocol) => Protocol = protocol;

            public Protocol Protocol { get; }

            /// <summary>The Dart takes output reports (hid_write), every
            /// other mouse feature reports.</summary>
            public override bool Feature => Protocol != Protocol.Dart;

            /// <summary>Nothing hands one of these mice back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            /// <summary>The reports of a picture in the order they go out.</summary>
            internal byte[][] PictureFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                switch (Protocol)
                {
                    case Protocol.Surge:
                        return new[] { SurgeFrame(CoolerMasterLighting.ByAddress(lights, colors, 33)) };
                    case Protocol.FpsPro:
                        return new[] { FpsProFrame(CoolerMasterLighting.ByAddress(lights, colors, 1)[0]) };
                    case Protocol.Haste:
                        return HasteFrames(CoolerMasterLighting.ByAddress(lights, colors, 1)[0]);
                    case Protocol.Raid:
                    {
                        var map = CoolerMasterLighting.ByAddress(lights, colors, 2);
                        return new[] { RaidFrame(map[0], map[1]) };
                    }
                    default:
                    {
                        var map = CoolerMasterLighting.ByAddress(lights, colors, 2);
                        return new[] { DartFrame(DartScroll, map[0]), DartFrame(DartLogo, map[1]) };
                    }
                }
            }

            /// <summary>The wait after each report, in ms.</summary>
            internal int WaitMs => Protocol switch
            {
                Protocol.Raid => RaidWaitMs,
                Protocol.Dart => DartWaitMs,
                _ => 0,
            };

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var frame in PictureFrames(lights, colors))
                {
                    if (!(Feature ? io.SetFeature(frame) : io.WriteOutput(frame))) return HidLightingPaint.Failed;
                    if (WaitMs > 0) wait(WaitMs);
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
