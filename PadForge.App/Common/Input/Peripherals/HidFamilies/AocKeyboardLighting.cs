using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The AOC GK500 keyboard (#557), lit key by key as OpenRGB's
    /// AOCKeyboardController lights it: 3938:1178 and 3938:1229 on usage
    /// page 0xFF19, usage 0xFF19 (AOCKeyboardControllerDetect.cpp:19-21 and
    /// 41-42). Its keys are OpenRGB's aoc_keyboard_offset_values, a full-size
    /// ANSI layout, each at its LED value (RGBController_AOCKeyboard.cpp:29-48
    /// and 265-296).
    ///
    /// <para>The model of the device. A session selects the Custom mode, the
    /// one OpenRGB's Direct mode is (RGBController_AOCKeyboard.cpp:245-253),
    /// as OpenRGB does when that mode is chosen: the start output report 09
    /// 21, 5 ms, the lighting configuration in a 117-byte feature report
    /// with ID 0x14, the mode 9 in byte 6, the mode's color, random flag,
    /// direction, speed and brightness at 7 plus 9 times the mode, and a
    /// checksum of 0x4A9E plus bytes 0 to 114 in bytes 115 and 116, 5 ms, the
    /// end output report 09 22 and 10 ms (SetLightingConfig and
    /// SendLightingConfigPacket, AOCKeyboardController.cpp:55-73 and 169-216;
    /// DeviceUpdateMode, RGBController_AOCKeyboard.cpp:327-354, with the
    /// Direct mode's brightness 3, speed 0, direction clockwise, one color
    /// and black for the first key's color, PadForge having drawn none yet).
    /// A picture is the start report, the 361-byte feature report with ID
    /// 0x20 that carries the reds, the greens and the blues of the 120 LED
    /// values in three runs, and the end report, each followed by 5 ms
    /// (SetCustom and SendCustomPacket, lines 75-88 and 136-167). OpenRGB
    /// marks none of this as saving (@save :x:, RGBController_AOCKeyboard.cpp:19;
    /// the Direct mode's flags, line 248).</para>
    ///
    /// <para>Hand-back. No source reads the mode the keyboard ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// keyboard stays in its Custom mode with PadForge's last picture. No
    /// source names a program that lights it.</para>
    /// </summary>
    internal sealed class AocKeyboardLighting : IHidLightingFamily
    {
        public static readonly AocKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "AOCKeyboardController";
        internal const ushort VendorId = 0x3938;

        internal const int CommandSize = 64;
        internal const int ConfigSize = 117;
        internal const int CustomSize = 361;
        internal const int Leds = 120;

        /// <summary>AOC_KEYBOARD_MODE_CUSTOM and AOC_KEYBOARD_BRIGHTNESS_HIGH
        /// (AOCKeyboardController.h:31 and 48).</summary>
        internal const byte CustomMode = 0x09;
        internal const byte Brightness = 0x03;

        internal const int GapMs = 5;
        internal const int EndMs = 10;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "AOC";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength != 0 && collection.FeatureReportLength < CustomSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get(registrations[0].Name + pid, () => Model(registrations[0].Name, pid));
        }

        internal static List<LightInfo> Lights()
            => KeyboardLedNames.FromLayout(KeyboardLayouts.Build("aoc_keyboard_offset_values", KeyboardLayout.AnsiQwerty));

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_AOCKeyboard.cpp")
            {
                Product = $"aoc:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = RepaintMs,
            Begin = (io, wait) => Begin(io, wait),
        };

        /// <summary>SendStartPacket and SendEndPacket (lines 94-134).</summary>
        internal static byte[] Command(bool end)
        {
            var report = new byte[CommandSize];
            report[0] = 0x09;
            report[1] = end ? (byte)0x22 : (byte)0x21;
            return report;
        }

        /// <summary>SendLightingConfigPacket (lines 169-216).</summary>
        internal static byte[] Config(byte mode, byte random, byte brightness, byte speed, byte direction, int color)
        {
            var report = new byte[ConfigSize];
            report[0] = 0x14;
            report[1] = 0x01;
            report[6] = mode;
            int at = 7 + 9 * mode;
            report[at] = (byte)(color >> 16);
            report[at + 1] = (byte)(color >> 8);
            report[at + 2] = (byte)color;
            report[at + 3] = random;
            report[at + 4] = direction;
            report[at + 5] = speed;
            report[at + 6] = brightness;
            ushort checksum = 0x4A9E;
            for (int i = 0; i < 115; i++) checksum += report[i];
            report[115] = (byte)checksum;
            report[116] = (byte)(checksum >> 8);
            return report;
        }

        /// <summary>SendCustomPacket (lines 136-167).</summary>
        internal static byte[] Custom(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var byValue = KeyboardLedNames.ByAddress(lights, colors, Leds);
            var report = new byte[CustomSize];
            report[0] = 0x20;
            for (int i = 0; i < Leds; i++)
            {
                report[i + 1] = (byte)(byValue[i] >> 16);
                report[i + 121] = (byte)(byValue[i] >> 8);
                report[i + 241] = (byte)byValue[i];
            }
            return report;
        }

        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait)
        {
            if (io == null || !io.WriteOutput(Command(false))) return null;
            wait(GapMs);
            if (!io.SetFeature(Config(CustomMode, 0, Brightness, 0, 0, 0))) return null;
            wait(GapMs);
            if (!io.WriteOutput(Command(true))) return null;
            wait(EndMs);
            return new Session();
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing undoes the Custom mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null || !io.WriteOutput(Command(false))) return HidLightingPaint.Failed;
                wait(GapMs);
                if (!io.SetFeature(Custom(lights, colors))) return HidLightingPaint.Failed;
                wait(GapMs);
                if (!io.WriteOutput(Command(true))) return HidLightingPaint.Failed;
                wait(GapMs);
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
