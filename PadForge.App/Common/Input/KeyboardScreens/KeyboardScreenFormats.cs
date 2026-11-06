using System;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>The kinds of keyboard screen (issue #519): a screen's size,
    /// its keys and the engine that draws it. Each present screen has its
    /// own row and settings. The settings file stores the names, so none is
    /// renamed, and the values stay as numbered.</summary>
    public enum KeyboardScreenKind
    {
        /// <summary>The 160 by 43 one-bit screen of the G15, G15 v2, G510,
        /// G510s, G13, Z-10 and GamePanel.</summary>
        Monochrome = 0,
        /// <summary>The G19's 320 by 240 color screen.</summary>
        Color = 1,
        /// <summary>Logitech Arx Control on a phone.</summary>
        Arx = 2,
        /// <summary>The Mad Catz S.T.R.I.K.E. 7's V.E.N.O.M. touchscreen.</summary>
        Strike7 = 3,
        /// <summary>The Screen Overlay (#560): a window on the PC's own display,
        /// with no device behind it and any size the user gives it.</summary>
        Overlay = 4,
    }

    /// <summary>
    /// Sizes, wire formats and key layout of the keyboard screens (issue
    /// #519). A screen's picture is composed as top-down B, G, R, A pixels at
    /// the screen's own size, the order LGS's color call, Arx and the
    /// S.T.R.I.K.E. 7 all take, and converted here for the screens that take
    /// something else.
    /// </summary>
    internal static class KeyboardScreenFormats
    {
        public const int MonoWidth = 160, MonoHeight = 43;
        public const int ColorWidth = 320, ColorHeight = 240;
        /// <summary>The picture Arx Control shows, scaled by the phone to
        /// its screen.</summary>
        public const int ArxWidth = 480, ArxHeight = 320;
        /// <summary>The V.E.N.O.M. screen as both of Mad Catz's examples draw
        /// it (mapApplet.cpp, MapApplet.cs).</summary>
        public const int Strike7Width = 480, Strike7Height = 320;
        /// <summary>The longest side of a Show Keyboard Screen frame kept for
        /// the Screen Overlay. Frames live in the settings file as PNG, and the
        /// overlay scales them to its window.</summary>
        public const int OverlayFrameSide = 800;

        /// <summary>The kinds an engine draws. The Screen Overlay is drawn by its
        /// own window.</summary>
        public static readonly KeyboardScreenKind[] Kinds =
        {
            KeyboardScreenKind.Monochrome, KeyboardScreenKind.Color, KeyboardScreenKind.Arx, KeyboardScreenKind.Strike7,
        };

        public static (int Width, int Height) SizeOf(KeyboardScreenKind kind) => kind switch
        {
            KeyboardScreenKind.Monochrome => (MonoWidth, MonoHeight),
            KeyboardScreenKind.Color => (ColorWidth, ColorHeight),
            KeyboardScreenKind.Arx => (ArxWidth, ArxHeight),
            KeyboardScreenKind.Overlay => (OverlayFrameSide, OverlayFrameSide),
            _ => (Strike7Width, Strike7Height),
        };

        public static bool IsMonochrome(KeyboardScreenKind kind) => kind == KeyboardScreenKind.Monochrome;

        /// <summary>Whether a pixel of a B, G, R, A picture reads dark: its
        /// luminance below half, the VMU's Threshold rule.</summary>
        public static bool IsDark(ReadOnlySpan<byte> bgra, int pixel)
        {
            int i = pixel * 4;
            return (bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000 < 128;
        }

        // ── LGS's monochrome call ───────────────────────────────────────

        /// <summary>LGS's monochrome frame: 160 by 43 bytes, one a pixel,
        /// lit at 128 or more (LogitechLCDLib.h, logitech-lcd src/lib.rs). A
        /// dark pixel of the picture is a lit one on the screen.</summary>
        public static byte[] ToLgsMono(ReadOnlySpan<byte> bgra)
        {
            var mono = new byte[MonoWidth * MonoHeight];
            for (int p = 0; p < mono.Length; p++)
                if (IsDark(bgra, p)) mono[p] = 0xFF;
            return mono;
        }

        // ── The monochrome screens over HID ─────────────────────────────

        /// <summary>The 992-byte output report that holds one monochrome
        /// frame (libg15 G15_BUFFER_LEN 0x3E0).</summary>
        public const int G15ReportLength = 992;
        public const byte G15ReportId = 0x03;
        /// <summary>Where the pixels start (libg15 G15_LCD_OFFSET).</summary>
        public const int G15PixelOffset = 32;

        /// <summary>
        /// One monochrome frame as the G15 family takes it: byte 0 is report
        /// ID 3, bytes 1 to 31 are zero, and from byte 32 the 160 columns of
        /// each of 6 bands of 8 rows, one byte per column of a band, the band's
        /// top row in bit 0 and a set bit lighting the pixel. libg15's
        /// dumpPixmapIntoLCDFormat and writePixmapToLCD (src/libg15.c), lcdproc's
        /// g15_pixmap_to_lcd, G15Interface and ecraven/g13 build the same
        /// bytes. The last band's five rows below row 42 stay dark.
        /// </summary>
        public static byte[] ToG15Report(ReadOnlySpan<byte> lgsMono)
        {
            var report = new byte[G15ReportLength];
            report[0] = G15ReportId;
            for (int band = 0; band < 6; band++)
            {
                for (int x = 0; x < MonoWidth; x++)
                {
                    int bits = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        int y = band * 8 + k;
                        if (y < MonoHeight && lgsMono[y * MonoWidth + x] >= 128) bits |= 1 << k;
                    }
                    report[G15PixelOffset + band * MonoWidth + x] = (byte)bits;
                }
            }
            return report;
        }

        // ── The G19 over WinUSB ─────────────────────────────────────────

        public const int G19HeaderLength = 512;
        public const int G19FrameLength = G19HeaderLength + ColorWidth * ColorHeight * 2;

        /// <summary>The header's first 16 bytes. From byte 16 each byte is its
        /// own index, modulo 256 (G19USB G19Constants.LcdHeader, gnome15
        /// pylibg19 g19.py, both from libg19).</summary>
        private static readonly byte[] G19HeaderStart =
        {
            0x10, 0x0F, 0x00, 0x58, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3F, 0x01, 0xEF, 0x00, 0x0F,
        };

        /// <summary>
        /// One G19 frame for bulk OUT endpoint 0x02: the 512-byte header, then
        /// 320 by 240 RGB565 pixels, low byte first, column by column with x
        /// outer and y inner (G19USB G19Helpers.ConvertBitmapToRGB565, gnome15
        /// g19.py convert_image_to_frame, whose rgb_to_uint16 returns the low
        /// byte in its high half).
        /// </summary>
        public static byte[] ToG19Frame(ReadOnlySpan<byte> bgra)
        {
            var frame = new byte[G19FrameLength];
            G19HeaderStart.CopyTo(frame, 0);
            for (int i = G19HeaderStart.Length; i < G19HeaderLength; i++) frame[i] = (byte)i;
            int pos = G19HeaderLength;
            for (int x = 0; x < ColorWidth; x++)
            {
                for (int y = 0; y < ColorHeight; y++)
                {
                    int i = (y * ColorWidth + x) * 4;
                    int b = bgra[i], g6 = bgra[i + 1] >> 2, r = bgra[i + 2];
                    frame[pos++] = (byte)(((g6 & 0x07) << 5) | (b >> 3));
                    frame[pos++] = (byte)((r & 0xF8) | (g6 >> 3));
                }
            }
            return frame;
        }

        // ── The screen keys ─────────────────────────────────────────────

        /// <summary>A screen row's keys: the four keys under a monochrome
        /// screen and its round applet key, or the G19's eight display keys.
        /// One layout per kind whichever engine reads a key, so a saved
        /// mapping keeps its key. Arx Control and the S.T.R.I.K.E. 7 have
        /// none.</summary>
        public const int MaxKeys = 8;
        public const int MonoKey1 = 0;       // to MonoKey1 + 3, left to right
        public const int MonoAppletKey = 4;
        public const int MonoKeyCount = 5;
        public const int G19Left = 0, G19Right = 1, G19Ok = 2, G19Cancel = 3, G19Up = 4, G19Down = 5, G19Menu = 6;
        public const int G19Settings = 7;
        public const int G19KeyCount = 8;

        public static int KeyCountOf(KeyboardScreenKind kind) => kind switch
        {
            KeyboardScreenKind.Monochrome => MonoKeyCount,
            KeyboardScreenKind.Color => G19KeyCount,
            _ => 0,
        };

        /// <summary>The monochrome keys of one HID input report as the row's
        /// bits, or -1 when the report is not that device's key report. The
        /// layouts are hid-lg-g15's (lg_g15_handle_lcd_menu_keys,
        /// lg_g15_v2_event, lg_g510_event, g13_keys_for_bits) and libg15's
        /// (processKeyEvent9Byte, processKeyEvent5Byte, processKeyEventG13).
        /// The kernel does not bind the GamePanel, so its row rests on libg15,
        /// which reads it with the G15's 9-byte decoder.
        ///
        /// <para>Linux hands over each report at its own length, and both
        /// decoders check it. Windows pads every read to the longest input
        /// report of the collection, so a report here is told by its ID,
        /// which is unique within a device, and needs at least its own
        /// length.</para></summary>
        public static int DecodeMonoKeys(ushort productId, ReadOnlySpan<byte> report)
        {
            switch (productId)
            {
                case KeyboardScreenDevices.G15:
                case KeyboardScreenDevices.Z10:
                case KeyboardScreenDevices.GamePanel:
                    if (report.Length < 9 || report[0] != 0x02) return -1;
                    return Bit(report[2], 0x80, MonoKey1) | Bit(report[3], 0x80, MonoKey1 + 1)
                         | Bit(report[4], 0x80, MonoKey1 + 2) | Bit(report[5], 0x80, MonoKey1 + 3)
                         | Bit(report[8], 0x80, MonoAppletKey);
                case KeyboardScreenDevices.G15V2:
                    if (report.Length < 5 || report[0] != 0x02) return -1;
                    return FourKeysFromBit1(report[2]) | Bit(report[2], 0x80, MonoAppletKey);
                case KeyboardScreenDevices.G510:
                case KeyboardScreenDevices.G510Audio:
                    if (report.Length < 5 || report[0] != 0x03) return -1;
                    return FourKeysFromBit1(report[4]) | Bit(report[4], 0x01, MonoAppletKey);
                case KeyboardScreenDevices.G13:
                    if (report.Length < 8 || report[0] != 0x01) return -1;
                    return FourKeysFromBit1(report[6]) | Bit(report[6], 0x01, MonoAppletKey);
                default:
                    return -1;
            }
        }

        private static int FourKeysFromBit1(byte value)
            => Bit(value, 0x02, MonoKey1) | Bit(value, 0x04, MonoKey1 + 1)
             | Bit(value, 0x08, MonoKey1 + 2) | Bit(value, 0x10, MonoKey1 + 3);

        private static int Bit(byte value, int mask, int button) => (value & mask) != 0 ? 1 << button : 0;

        /// <summary>The G19's display keys from a 2-byte report on interrupt
        /// endpoint 0x81, byte 1 always 0x80 (gnome15 pylibg19 keys.py
        /// displayKeys, G19USB G19Keys.cs), or -1 for any other report.</summary>
        public static int DecodeG19Keys(ReadOnlySpan<byte> report)
        {
            if (report.Length != 2 || report[1] != 0x80) return -1;
            byte k = report[0];
            return Bit(k, 0x01, G19Settings) | Bit(k, 0x02, G19Cancel) | Bit(k, 0x04, G19Menu)
                 | Bit(k, 0x08, G19Ok) | Bit(k, 0x10, G19Right) | Bit(k, 0x20, G19Left)
                 | Bit(k, 0x40, G19Down) | Bit(k, 0x80, G19Up);
        }

        /// <summary>LGS's button flags (LogitechLCDLib.h LOGI_LCD_MONO_BUTTON_0
        /// to _3, LOGI_LCD_COLOR_BUTTON_LEFT to _MENU), the kind of screen
        /// each belongs to and its button on that screen's row. LGS keeps the
        /// round applet key and the G19's settings key for itself.</summary>
        public static readonly (int Flag, KeyboardScreenKind Kind, int Button)[] LgsButtons =
        {
            (0x0001, KeyboardScreenKind.Monochrome, MonoKey1), (0x0002, KeyboardScreenKind.Monochrome, MonoKey1 + 1),
            (0x0004, KeyboardScreenKind.Monochrome, MonoKey1 + 2), (0x0008, KeyboardScreenKind.Monochrome, MonoKey1 + 3),
            (0x0100, KeyboardScreenKind.Color, G19Left), (0x0200, KeyboardScreenKind.Color, G19Right),
            (0x0400, KeyboardScreenKind.Color, G19Ok), (0x0800, KeyboardScreenKind.Color, G19Cancel),
            (0x1000, KeyboardScreenKind.Color, G19Up), (0x2000, KeyboardScreenKind.Color, G19Down),
            (0x4000, KeyboardScreenKind.Color, G19Menu),
        };
    }

    /// <summary>The USB identities of the keyboard screens (issue #519),
    /// checked against Linux's hid-ids.h and libg15's device table.</summary>
    internal static class KeyboardScreenDevices
    {
        public const ushort LogitechVid = 0x046D;
        public const ushort G15 = 0xC222;
        public const ushort G15V2 = 0xC227;
        public const ushort G510 = 0xC22D;
        public const ushort G510Audio = 0xC22E;
        public const ushort G13 = 0xC21C;
        public const ushort Z10 = 0x0A07;
        public const ushort GamePanel = 0xC251;
        public const ushort G19 = 0xC229;
        public const ushort MadCatzVid = 0x0738;
        public const ushort Strike7 = 0xA109;

        public static readonly ushort[] MonochromePids = { G15, G15V2, G510, G510Audio, G13, Z10, GamePanel };

        public static bool IsMonochrome(ushort vid, ushort pid)
            => vid == LogitechVid && Array.IndexOf(MonochromePids, pid) >= 0;

        public static string NameOf(ushort pid) => pid switch
        {
            G15 => "G15",
            G15V2 => "G15 v2",
            G510 or G510Audio => "G510",
            G13 => "G13",
            Z10 => "Z-10",
            GamePanel => "GamePanel",
            G19 => "G19",
            _ => "Logitech " + pid.ToString("X4"),
        };
    }
}
