using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The ZET Blade Optical keyboard (#557), lit key by key as OpenRGB's
    /// ZETKeyboardController lights it: 2EA8:2125 on interface 1, usage page
    /// 0xFF00 (ZETKeyboardControllerDetect.cpp:44), 65-byte output reports
    /// with report ID 4. Its 104 keys are OpenRGB's led_names in order
    /// (RGBController_ZETBladeOptical.cpp:46-173), each addressed by its
    /// place in that order.
    ///
    /// <para>The model of the device. OpenRGB lights it per key only in its
    /// Custom mode, and selecting that mode sends nothing
    /// (SetEffect, ZETBladeOpticalController.cpp:221-240). A picture is
    /// reports of 04 AE 01, the Custom mode 0x1C in byte 5, speed 2,
    /// brightness 4 and the separator 0xFF in bytes 6 to 8, then each key as
    /// its ID, 0x80 plus its index with the IDs of SKIP_INDICES stepped
    /// over, and its red, green and blue, twelve keys to a report, the last
    /// report ending with 0xFF, each report followed by a 12 ms wait
    /// (SetLEDDirect, lines 67-192; ZETBladeOpticalController.h:21-31).
    /// OpenRGB marks the Custom mode as one that saves by itself
    /// (MODE_FLAG_AUTOMATIC_SAVE, RGBController_ZETBladeOptical.cpp:200), so
    /// each picture lands in the keyboard's memory, and a picture goes out
    /// once it has held for 150 ms and no sooner than 500 ms after the one
    /// before (<see cref="LenovoLegionLighting.HoldMs"/>).</para>
    ///
    /// <para>Hand-back. No source reads the effect the keyboard ran before
    /// or returns it to it, and OpenRGB sends nothing when it closes, so the
    /// keyboard keeps PadForge's last picture. No source names a program
    /// that lights it.</para>
    /// </summary>
    internal sealed class ZetKeyboardLighting : IHidLightingFamily
    {
        public static readonly ZetKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "ZETKeyboardController";
        internal const ushort VendorId = 0x2EA8;

        internal const int PacketSize = 65;

        /// <summary>ZET_BLADE_OPTICAL_HEADER_LEN, _COLOR_LEN and _KEY_OFFSET
        /// (ZETBladeOpticalController.h:22-24).</summary>
        internal const int HeaderLength = 9;
        internal const int ColorLength = 4;
        internal const int KeyOffset = 0x80;

        /// <summary>ZET_BLADE_OPTICAL_MODE_CUSTOM, the speed PrepareHeader
        /// puts in, and ZET_BLADE_OPTICAL_BRIGHTNESS_DEF, the Custom mode's
        /// (lines 31 and 34; ZETBladeOpticalController.cpp:67-70;
        /// RGBController_ZETBladeOptical.cpp:204).</summary>
        internal const byte CustomMode = 0x1C;
        internal const byte Speed = 0x02;
        internal const byte Brightness = 0x04;

        /// <summary>ZET_BLADE_OPTICAL_DELAY (line 21).</summary>
        internal const int DelayMs = 12;

        public string Name => "ZET";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        internal static List<LightInfo> Lights() => KeyboardLedNames.FromNames(KeyboardLedNames.ZetNames, i => i);

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_ZETBladeOptical.cpp")
            {
                Product = $"zet:{VendorId:X4}:{productId:X4}",
            },
            HoldMs = LenovoLegionLighting.HoldMs,
            RepaintMs = LenovoLegionLighting.RepaintMs,
            Begin = (_, _) => new Session(),
        };

        /// <summary>PrepareHeader (ZETBladeOpticalController.cpp:67-84).</summary>
        internal static byte[] Header()
        {
            var report = new byte[PacketSize];
            report[0] = 0x04;
            report[1] = 0xAE;
            report[2] = 0x01;
            report[5] = CustomMode;
            report[6] = Speed;
            report[7] = Brightness;
            report[8] = 0xFF;
            return report;
        }

        /// <summary>SetLEDDirect's reports for the colors in OpenRGB's light
        /// order (lines 86-192), its loop kept step for step: the skip list
        /// wraps to its start once it is used up.</summary>
        internal static List<byte[]> Picture(IReadOnlyList<int> colors)
        {
            var reports = new List<byte[]>();
            var skips = KeyboardLedNames.ZetSkips;
            var buf = Header();
            int at = HeaderLength, index = 0, skipped = 0, skip = 0;
            bool ending = false;
            int count = colors?.Count ?? 0;
            while (index < count)
            {
                if (skips[skip] == index + skipped)
                {
                    skip++;
                    if (skip >= skips.Length) skip = 0;
                    skipped++;
                    continue;
                }
                buf[at] = (byte)(index + skipped + KeyOffset);
                buf[at + 1] = (byte)(colors[index] >> 16);
                buf[at + 2] = (byte)(colors[index] >> 8);
                buf[at + 3] = (byte)colors[index];
                at += ColorLength;
                index++;
                bool last = index == count;
                if (at + HeaderLength >= PacketSize || last)
                {
                    if (last && at + ColorLength < PacketSize)
                    {
                        buf[at] = 0xFF;
                        ending = true;
                    }
                    reports.Add(buf);
                    buf = Header();
                    at = HeaderLength;
                }
            }
            if (!ending)
            {
                buf[at] = 0xFF;
                reports.Add(buf);
            }
            return reports;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var report in Picture(KeyboardLedNames.ByAddress(lights, colors, KeyboardLedNames.ZetNames.Length)))
                {
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                    wait(DelayMs);
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
