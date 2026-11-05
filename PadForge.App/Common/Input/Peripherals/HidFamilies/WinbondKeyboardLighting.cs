using System;
using System.Collections.Generic;
using System.Text;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Winbond gaming keyboards (#557), sold as the Pulsar PCMK and the
    /// KT108 among others, lit key by key as OpenRGB's
    /// WinbondGamingKeyboardController lights them: 0416:B23C on usage page
    /// 0xFF1B, usage 0x91 (WinbondGamingKeyboardControllerDetect.cpp:20-25
    /// and 70), 64-byte output reports with report ID 1
    /// (WinbondGamingKeyboardController.cpp:19 and 228-258).
    ///
    /// <para>Which keyboard. OpenRGB tells the size, the vendor and the logo
    /// light from the product string, "PCMK" for Pulsar, "Rockfall" or
    /// "Skyfall" for Hator, "TKL" or "60" for the size, and takes a full-size
    /// board of another vendor when the string names none of them
    /// (SetNameVendorDescription, lines 32-99; GetSize's default,
    /// WinbondGamingKeyboardController.h:84-86). A session reads the same
    /// string from the channel (<see cref="BoardOf"/>). The map before a
    /// session is that full-size board, OpenRGB's own choice when no string
    /// tells. The region comes from the device: a session asks for
    /// the version string, 01 0D, reads up to ten input reports for 150 ms
    /// each for the answer 01 0D with 0 in byte 4, and takes "UK" ahead of
    /// "CVRGB" as ISO, "JP" as JIS and anything else ahead of it as ANSI,
    /// and the board's default when the string has no "CVRGB" or nothing
    /// answers: ANSI on a Hator board and ISO on any other
    /// (SetVersionLayout and ParseVersionString, lines 101-213). The keys
    /// are OpenRGB's layout of the board's size and vendor for that region,
    /// each at its place in the color reports
    /// (InitLayouts, RGBController_WinbondGamingKeyboard.cpp:233-531; the KV
    /// value, lines 29-35), and a session publishes the region's map
    /// (<see cref="HidLightingSession.Map"/>).</para>
    ///
    /// <para>The model of the device. The session then selects the Custom
    /// mode, 01 07 00 00 00 0E 0A with brightness and speed in bytes 7 and
    /// 8, the default colors, blue and black, in bytes 9 to 14, and 1 in
    /// byte 16 for the Custom mode's full color (SetMode and setModeImpl,
    /// lines 228-258 and 374-389). OpenRGB's Custom mode starts at
    /// brightness 1 of 4 and speed 2 (RGBController_WinbondGamingKeyboard.cpp:546-558).
    /// PadForge sends brightness 4, the mode's maximum, since it dims a
    /// color itself. A picture is eight reports, 01 09 00 00, the report's
    /// number and the length of its colors, each color scaled from 0 to 255
    /// down to 0 to 0xC1 (SetLEDsData, lines 260-324). OpenRGB marks the
    /// Custom mode as one that saves by itself (MODE_FLAG_AUTOMATIC_SAVE,
    /// RGBController_WinbondGamingKeyboard.cpp:550), so each picture lands
    /// in the keyboard's memory, and a picture goes out once it has held for
    /// 150 ms and no sooner than 500 ms after the one before
    /// (<see cref="LenovoLegionLighting.HoldMs"/>).</para>
    ///
    /// <para>Hand-back. No source reads the mode the keyboard ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// keyboard keeps PadForge's last picture in its Custom mode. The Pulsar
    /// PCMK TKL's logo light is one light for its three LEDs, set through the
    /// logo mode report after each picture (lines 326-333,
    /// RGBController_WinbondGamingKeyboard.cpp:790-808). No source
    /// names a program that lights these keyboards.</para>
    /// </summary>
    internal sealed class WinbondKeyboardLighting : IHidLightingFamily
    {
        public static readonly WinbondKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "WinbondGamingKeyboardController";
        internal const ushort VendorId = 0x0416;

        /// <summary>WINBOND_HID_DATA_LEN, the report ID included (line 19).</summary>
        internal const int PacketSize = 64;
        internal const byte ReportId = 0x01;
        internal const byte VersionCommand = 0x0D;
        internal const int VersionReads = 10;
        internal const int VersionMs = 150;

        /// <summary>WINBOND_GK_MODE_CUSTOM (RGBController_WinbondGamingKeyboard.h:30).</summary>
        internal const byte CustomMode = 10;
        internal const byte Brightness = 4;
        internal const byte Speed = 2;

        /// <summary>The recipe OpenRGB builds for a full-size board of a
        /// vendor other than Hator (RGBController_WinbondGamingKeyboard.cpp:744-766).</summary>
        internal const string Recipe = "winbond_gaming_keyboard_full_layouts_other";

        /// <summary>What a product string tells of a board: its layout recipe,
        /// whether it has the logo light, and its region where the version
        /// string names none.</summary>
        internal readonly record struct Board(string Recipe, bool LogoLight, KeyboardLayout Fallback);

        /// <summary>SetNameVendorDescription (lines 32-99): "PCMK" is a Pulsar
        /// board, TKL with the logo light or 60%, and "Rockfall" or "Skyfall" a
        /// Hator board, ANSI and full size or TKL. Any other, or none, is the
        /// full-size board of another vendor.</summary>
        internal static Board BoardOf(string product)
        {
            string name = product ?? string.Empty;
            bool tkl = name.Contains("TKL", StringComparison.Ordinal);
            if (name.Contains("PCMK", StringComparison.Ordinal))
            {
                if (tkl) return new Board("winbond_gaming_keyboard_tkl_layouts_other", true, KeyboardLayout.IsoQwerty);
                if (name.Contains("60", StringComparison.Ordinal))
                    return new Board("winbond_gaming_keyboard_60_layouts_other", false, KeyboardLayout.IsoQwerty);
            }
            else if (name.Contains("Rockfall", StringComparison.Ordinal) || name.Contains("Skyfall", StringComparison.Ordinal))
            {
                return new Board(tkl ? "winbond_gaming_keyboard_tkl_layouts_hator" : "winbond_gaming_keyboard_full_layouts_hator",
                    false, KeyboardLayout.AnsiQwerty);
            }
            return new Board(Recipe, false, KeyboardLayout.IsoQwerty);
        }

        /// <summary>The logo light's value, KV(255, 0)
        /// (RGBController_WinbondGamingKeyboard.cpp:35 and 803).</summary>
        internal const int LogoValue = 255 << 8 | 1 << 16;

        /// <summary>WINBOND_GK_MODE_STATIC and the logo report's speed, 3
        /// (RGBController_WinbondGamingKeyboard.h:19,
        /// WinbondGamingKeyboardController.cpp:332).</summary>
        internal const byte StaticMode = 0, LogoSpeed = 3;

        public string Name => "Winbond";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        /// <summary>The full-size layout in a region.</summary>
        internal static LightMap Map(KeyboardLayout layout, ushort productId)
            => Map(BoardOf(null), layout, productId);

        internal static LightMap Map(Board board, KeyboardLayout layout, ushort productId)
        {
            var lights = KeyboardLedNames.FromLayout(KeyboardLayouts.Build(board.Recipe, layout));
            if (board.LogoLight) lights.Add(new LightInfo(LogoValue, LightKind.Logo, 0, LightKeySource.None));
            return new LightMap(lights, "OpenRGB RGBController_WinbondGamingKeyboard.cpp")
            {
                Product = $"winbond:{VendorId:X4}:{productId:X4}:{board.Recipe}:{layout}",
            };
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = Map(KeyboardLayout.IsoQwerty, productId),
            HoldMs = LenovoLegionLighting.HoldMs,
            RepaintMs = LenovoLegionLighting.RepaintMs,
            Begin = (io, _) => Begin(io, productId),
        };

        internal static byte[] Report(byte command)
        {
            var report = new byte[PacketSize];
            report[0] = ReportId;
            report[1] = command;
            return report;
        }

        /// <summary>setModeImpl for the key lights in the Custom mode
        /// (lines 228-258 and 374-389).</summary>
        internal static byte[] CustomModeReport()
        {
            var report = Report(0x07);
            report[5] = 0x0E;
            report[6] = CustomMode;
            report[7] = Brightness;
            report[8] = Speed;
            report[9] = 0x00;
            report[10] = 0x00;
            report[11] = 0xFF;
            report[15] = 0x00;
            report[16] = 0x01;
            return report;
        }

        /// <summary>The region a version answer names, or null when the
        /// report is not the answer or holds no string (lines 177-207 and
        /// 120-144).</summary>
        internal static KeyboardLayout? RegionOf(byte[] answer, int length, KeyboardLayout fallback = KeyboardLayout.IsoQwerty)
        {
            if (length < 5 || answer[0] != ReportId || answer[1] != VersionCommand || answer[4] != 0) return null;
            int at = 2;
            int end = Math.Min(length, PacketSize);
            while (at < end && answer[at] <= (byte)' ') at++;
            if (at >= end) return null;
            int stop = at;
            while (stop < end && answer[stop] != 0) stop++;
            string text = Encoding.ASCII.GetString(answer, at, stop - at);
            int cvrgb = text.IndexOf("CVRGB", StringComparison.Ordinal);
            if (cvrgb <= 2) return fallback;
            string lang = text.Substring(cvrgb - 2, 2);
            return lang == "UK" ? KeyboardLayout.IsoQwerty : lang == "JP" ? KeyboardLayout.Jis : KeyboardLayout.AnsiQwerty;
        }

        /// <summary>The picture's eight reports (SetLEDsData, lines 270-324).</summary>
        internal static List<byte[]> Picture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var reports = new List<byte[]>(8);
            for (int i = 0; i < 8; i++)
            {
                var report = Report(0x09);
                report[4] = (byte)i;
                report[5] = i == 7 ? (byte)0x12 : (byte)0x36;
                reports.Add(report);
            }
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int value = lights[i].Address;
                if (value == 0) continue;
                int message = (value >> 8) & 0xFF;
                int offset = (value & 0xFF) * 3 + 6;
                if (message == 0xFF) continue;
                message &= 7;
                if (offset + 2 >= PacketSize) continue;
                var report = reports[message];
                report[offset] = (byte)(((colors[i] >> 16) & 0xFF) * 0xC1 / 0xFF);
                report[offset + 1] = (byte)(((colors[i] >> 8) & 0xFF) * 0xC1 / 0xFF);
                report[offset + 2] = (byte)((colors[i] & 0xFF) * 0xC1 / 0xFF);
            }
            return reports;
        }

        /// <summary>setModeImpl for the logo (lines 228-258 and 326-333): 01 08
        /// 00 00 00 0D, the Static mode, the brightness, the speed 3 and the
        /// color with no scaling, since the logo report takes it whole.</summary>
        internal static byte[] LogoReport(int rgb)
        {
            var report = Report(0x08);
            report[5] = 0x0D;
            report[6] = StaticMode;
            report[7] = Brightness;
            report[8] = LogoSpeed;
            report[9] = (byte)(rgb >> 16);
            report[10] = (byte)(rgb >> 8);
            report[11] = (byte)rgb;
            return report;
        }

        private static HidLightingSession Begin(IHidReportChannel io, ushort productId)
        {
            if (io == null || !io.WriteOutput(Report(VersionCommand))) return null;
            var board = BoardOf(io.ProductString);
            var region = board.Fallback;
            var answer = new byte[Math.Max(PacketSize, io.InputReportLength)];
            for (int read = 0; read < VersionReads; read++)
            {
                Array.Clear(answer);
                int length = io.ReadInput(answer, VersionMs);
                if (length < 0) continue;
                if (RegionOf(answer, length, board.Fallback) is KeyboardLayout found)
                {
                    region = found;
                    break;
                }
            }
            if (!io.WriteOutput(CustomModeReport())) return null;
            return new Session(board.Recipe == Recipe && region == KeyboardLayout.IsoQwerty ? null : Map(board, region, productId));
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly LightMap _map;

            public Session(LightMap map) => _map = map;

            /// <summary>The region's map where the keyboard named one other
            /// than the model's ISO.</summary>
            public override LightMap Map => _map;

            public override bool Feature => false;

            /// <summary>Nothing undoes the Custom mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var report in Picture(lights, colors))
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                for (int i = 0; i < count; i++)
                    if (lights[i].Address == LogoValue && !io.WriteOutput(LogoReport(colors[i])))
                        return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
