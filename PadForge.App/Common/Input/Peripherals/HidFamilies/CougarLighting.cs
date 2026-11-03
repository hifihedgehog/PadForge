using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Cougar's 700K EVO keyboard and Revenger ST mouse, lit as OpenRGB's
    /// CougarController family lights them (#557).
    ///
    /// <para>The 700K EVO: 113 keys, named by OpenRGB's led_names table
    /// (RGBController_CougarKeyboard.cpp:31-152). Each picture is OpenRGB's
    /// Direct mode, the keyboard's Customize mode 0x0B: output reports of
    /// 14 2C 0B 00 FF 64 00, a flag (1 for the first report, 2 after, 3 for
    /// the last), then up to fourteen entries of the key's code from
    /// keyvalue_map and its color, each report followed by OpenRGB's 1 ms
    /// wait (SetLedsDirect, CougarKeyboardController.cpp:18-55 and
    /// 158-202). OpenRGB fills one buffer for every report and never clears
    /// it, so the last report carries the entries of the report before it
    /// past its own, and this port sends the same bytes. None of it is the
    /// save command (12, Save, lines 204-209).</para>
    ///
    /// <para>The Revenger ST: the bottom, the wheel and the logo, each set
    /// by OpenRGB's Direct mode in feature reports of 9 bytes. A session
    /// starts by setting each zone's mode to Direct, as OpenRGB's controller
    /// does when it opens (CougarRevengerSTController.cpp:16-25 and
    /// 186-200), and each picture sets each zone's red, green, blue and
    /// brightness at the addresses of DIRECT_MODE (SetDirect and
    /// SendColourPacket, lines 61-86 and 178-184, CougarRevengerSTController.h).</para>
    ///
    /// <para>Hand-back: none. OpenRGB reads neither device's mode and sends
    /// nothing when it closes, and no other reference drives them, so each
    /// shows the last picture PadForge sent until the user picks a mode on
    /// the device or in Cougar's software. No source names that software's
    /// process, so none is in the vendor-program table.</para>
    /// </summary>
    internal sealed class CougarLighting : IHidLightingFamily
    {
        public static readonly CougarLighting Instance = new();

        internal const string OpenRgbFamily = "CougarController";

        /// <summary>COUGAR_VID_2 and COUGAR_VID (CougarControllerDetect.cpp).</summary>
        internal const ushort KeyboardVid = 0x060B;
        internal const ushort MouseVid = 0x12CF;

        /// <summary>The shortest time between two pictures. No reference bounds
        /// it, so the bound is PadForge's, as the Cooler Master family's.</summary>
        internal const int RepaintMs = 50;

        /// <summary>keyvalue_map, each key's code in a 700K EVO report
        /// (CougarKeyboardController.cpp:18-55).</summary>
        internal static readonly byte[] KeyCodes =
        {
            0, 9, 18, 27, 36, 45, 54, 63, 72, 81,
            90, 99, 108, 117, 126, 15, 135, 140, 141, 142,
            1, 10, 19, 28, 37, 46, 55, 64, 73, 82,
            91, 100, 109, 127, 128, 24, 33, 42, 51, 60,
            69, 2, 11, 20, 29, 38, 47, 56, 65, 74,
            83, 92, 101, 110, 119, 129, 78, 87, 96, 105,
            16, 25, 3, 12, 21, 30, 39, 48, 57, 66,
            75, 84, 93, 102, 120, 34, 43, 52, 4, 22,
            31, 40, 49, 58, 67, 76, 85, 94, 103, 121,
            130, 70, 79, 88, 106, 5, 14, 23, 50, 86,
            95, 104, 113, 122, 131, 132, 133, 97, 147, 148,
            149, 150, 151,
        };

        /// <summary>DIRECT_MODE's addresses for the bottom, the wheel and the
        /// logo: the mode, the mode byte, red (green and blue follow), and
        /// the brightness (CougarRevengerSTController.h, DIRECT_MODE).</summary>
        internal static readonly byte[] ModeAddress = { 0x77, 0x55, 0xE6 };
        internal static readonly byte[] ModeByte = { 0x01, 0x00, 0x00 };
        internal static readonly byte[] RgbAddress = { 0x79, 0x57, 0xE8 };
        internal static readonly byte[] BrightnessAddress = { 0x78, 0x56, 0xE7 };

        /// <summary>PACKET_START, ACTION_SET and DIRECT_MODE_VALUE
        /// (CougarRevengerSTController.h).</summary>
        internal const byte PacketStart = 0xC4;
        internal const byte ActionSet = 0x0F;
        internal const byte DirectMode = 0x01;

        public string Name => "Cougar";

        private HidLightingModel _keyboard, _mouse;

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || (collection.VendorId != KeyboardVid && collection.VendorId != MouseVid)) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            return registration.Detector switch
            {
                "DetectCougar700kEvo" => _keyboard ??= KeyboardModel(registration.Name, collection.ProductId),
                "DetectCougarRevengerSTControllers" => _mouse ??= MouseModel(registration.Name, collection.ProductId),
                _ => null,
            };
        }

        private static HidLightingModel KeyboardModel(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(HyperXKeyboardLighting.Named(OpenRgbLedNames.CougarKeyboard),
                "OpenRGB RGBController_CougarKeyboard.cpp") { Product = $"cougar:{KeyboardVid:X4}:{productId:X4}" },
            RepaintMs = RepaintMs,
            Begin = (io, wait) => io == null ? null : new KeyboardSession(),
        };

        /// <summary>The bottom, the wheel and the logo, in OpenRGB's zone
        /// order (RGBController_CougarRevengerST.cpp:115-140).</summary>
        private static HidLightingModel MouseModel(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(new[]
            {
                new LightInfo(0, LightKind.Edge, 0, LightKeySource.None),
                new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
                new LightInfo(2, LightKind.Logo, 0, LightKeySource.None),
            }, "OpenRGB RGBController_CougarRevengerST.cpp") { Product = $"cougar:{MouseVid:X4}:{productId:X4}" },
            RepaintMs = RepaintMs,
            Begin = (io, wait) => BeginMouse(io),
        };

        // ─────────────────────────────────────────────
        //  700K EVO
        // ─────────────────────────────────────────────

        /// <summary>The 700K EVO's reports for a picture, one buffer carried
        /// from report to report as SetLedsDirect carries it.</summary>
        internal static List<byte[]> KeyboardFrames(int[] colors)
        {
            var frames = new List<byte[]>();
            var buffer = new byte[65];
            new byte[] { 0x00, 0x14, 0x2C, 0x0B, 0x00, 0xFF, 0x64, 0x00, 0x01 }.CopyTo(buffer, 0);
            int max = 14, remaining = colors.Length;
            for (int sent = 0; sent < remaining; sent += max)
            {
                if (sent + max > remaining)
                {
                    max = remaining - sent;
                    buffer[8] = 3;
                }
                for (int i = 0; i < max; i++)
                {
                    int at = 9 + i * 4, led = sent + i;
                    buffer[at] = led < KeyCodes.Length ? KeyCodes[led] : (byte)0;
                    buffer[at + 1] = (byte)(colors[led] >> 16);
                    buffer[at + 2] = (byte)(colors[led] >> 8);
                    buffer[at + 3] = (byte)colors[led];
                }
                frames.Add((byte[])buffer.Clone());
                buffer[8] = 2;
            }
            return frames;
        }

        internal sealed class KeyboardSession : HidLightingSession
        {
            public override bool Feature => false;
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var frame in KeyboardFrames(CoolerMasterLighting.ByAddress(lights, colors, lights.Count)))
                {
                    if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                    wait(1);
                }
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  Revenger ST
        // ─────────────────────────────────────────────

        private static byte[] MouseReport(byte address, byte modeByte, byte value)
            => new byte[] { 0x00, PacketStart, ActionSet, 0x00, address, modeByte, value, 0x00, 0x00 };

        /// <summary>ActivateMode(zone, DIRECT_MODE_VALUE)
        /// (CougarRevengerSTController.cpp:186-200).</summary>
        internal static byte[] ActivateDirect(int zone) => MouseReport(ModeAddress[zone], ModeByte[zone], DirectMode);

        /// <summary>SetDirect for a zone at full brightness, OpenRGB's Direct
        /// mode's default (RGBController_CougarRevengerST.cpp, Direct.brightness
        /// MAX_BRIGHTNESS): red, green and blue, then the brightness
        /// (CougarRevengerSTController.cpp:61-86).</summary>
        internal static IEnumerable<byte[]> ZoneColor(int zone, int color)
        {
            yield return MouseReport(RgbAddress[zone], ModeByte[zone], (byte)(color >> 16));
            yield return MouseReport((byte)(RgbAddress[zone] + 1), ModeByte[zone], (byte)(color >> 8));
            yield return MouseReport((byte)(RgbAddress[zone] + 2), ModeByte[zone], (byte)color);
            yield return MouseReport(BrightnessAddress[zone], ModeByte[zone], 0xFF);
        }

        private static HidLightingSession BeginMouse(IHidReportChannel io)
        {
            if (io == null) return null;
            for (int zone = 0; zone < 3; zone++)
                if (!io.SetFeature(ActivateDirect(zone))) return null;
            return new MouseSession();
        }

        internal sealed class MouseSession : HidLightingSession
        {
            public override bool Feature => true;
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                var map = CoolerMasterLighting.ByAddress(lights, colors, 3);
                for (int zone = 0; zone < 3; zone++)
                    foreach (var frame in ZoneColor(zone, map[zone]))
                        if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
