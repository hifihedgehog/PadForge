using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Wooting route (#557 item 11): while Wootility runs, PadForge
    /// lights a Wooting keyboard or keypad key by key through Wooting's RGB
    /// SDK, which it speaks itself, report for report, from the SDK's source
    /// (WootingKb/wooting-rgb-sdk at 7e091a0d, MPL-2.0, src/wooting-usb.c and
    /// src/wooting-rgb-sdk.c). No library of Wooting's is loaded.
    ///
    /// <para>Why the SDK's own path and not Wootility's service. RGB.NET
    /// drives Wooting boards two ways. Its native provider loads
    /// wooting-rgb-sdk, which writes the keyboard's configuration collection
    /// itself (RGB.NET.Devices.Wooting Native/WootingDeviceProvider.cs, and
    /// wooting-usb.c:417-506 and 541-754). Its gRPC provider asks a service
    /// on 127.0.0.1:50051 to write for it (Grpc/WootingGrpcDeviceProvider.cs:132,
    /// WootingRgb.proto), the "new background service" its author, at
    /// Wooting, named in RGB.NET pull request 427. Wooting's own pages
    /// describe only the first beside Wootility: Wootility 5.0.0-beta.7
    /// "improve[s] Wootility running at the same time as 3rd party apps that
    /// control the RGB on the keyboard" and keeps the lightbar working "when
    /// an RGB SDK App is being used", and 5.1.0 warns when such an app
    /// controls the keyboard (wooting.io/wootility/changelogs/5.0.0-beta.7
    /// and 5.1.0). No Wooting page names the service's port or calls, and
    /// it runs only once the user turns Background Service on in Wootility's
    /// settings (changelog 5.2.1). So the route writes what the SDK writes,
    /// and it needs nothing running but the keyboard.</para>
    ///
    /// <para>The reports, by the keyboard's protocol (the collection's usage
    /// page and vendor, <see cref="WootingKeyboardLighting.ProtocolOf"/>, as
    /// the SDK's walk picks it, wooting-usb.c:426-443):</para>
    /// <list type="bullet">
    /// <item>A command is an 8-byte feature report: report 0x01 and D1 DA
    /// for the third protocol, report 0 and D0 DA before it, the command,
    /// then the four parameters from the last down
    /// (wooting-usb.c:684-701). The SDK reads the keyboard's answer after
    /// each one before it goes on (lines 720-754, 805-828).</item>
    /// <item>A session opens with the color init command 33, as the SDK
    /// sends it once it finds the keyboard (lines 488-490), and hands back
    /// with the reset command 32, which "will restore all the colours to the
    /// colours that were originally on the keyboard" and which the SDK
    /// sends to every keyboard when an application closes
    /// (wooting-rgb-sdk.h:69-93, wooting-rgb-sdk.c:106-124).</item>
    /// <item>A picture is the SDK's 6 by 21 matrix of 16-bit colors, red 5,
    /// green 6 and blue 5 (wooting-rgb-sdk.c:83-91), low byte first as
    /// memcpy lays it on a little-endian PC. The second protocol sends it
    /// as report 0 of 257 bytes after 00 D0 DA 0B, or in four reports of 65
    /// bytes where the collection takes 64-byte reports (wooting-usb.c:606-654).
    /// The third sends it as report 0x05 of 2047 bytes after 05 D1 DA 0B,
    /// since report 0x04 is a byte short of the matrix (lines 656-682).
    /// The first protocol builds five LED driver buffers from it, each color
    /// through the SDK's gamma table, and sends four of them, five on a full
    /// board, as 129-byte reports with a CRC (wooting-rgb-sdk.c:204-244 and
    /// 308-362, wooting-usb.c:541-604).</item>
    /// </list>
    ///
    /// <para>The lights are the Wooting family's (<see cref="WootingKeyboardLighting.Lights"/>),
    /// each addressed by its cell in the same 6 by 21 matrix, row times 21
    /// plus column.</para>
    /// </summary>
    internal static class WootingRgbSdkRoute
    {
        /// <summary>WOOTING_COMMAND_SIZE and the command numbers
        /// (wooting-usb.c:21, wooting-usb.h:92-97).</summary>
        internal const int CommandSize = 8;
        internal const byte RawColorsReport = 11;
        internal const byte ResetAllCommand = 32;
        internal const byte ColorInitCommand = 33;

        /// <summary>WOOTING_RGB_ROWS and WOOTING_RGB_COLS
        /// (wooting-usb.h:83-84).</summary>
        internal const int Rows = 6;
        internal const int Columns = 21;

        /// <summary>The report sizes with the report ID byte
        /// (wooting-usb.c:22-24) and the small packets (lines 25-26).</summary>
        internal const int V1ReportSize = 129;
        internal const int V2ReportSize = 257;
        internal const int V3ReportSize = 2047;
        internal const int SmallPacketSize = 64;
        internal const int SmallPacketCount = 4;

        /// <summary>The third protocol's color report, "5 which corresponds
        /// to 510 byte report" (wooting-usb.c:663-664), and its command
        /// report (line 690).</summary>
        internal const byte V3ColorReport = 0x05;
        internal const byte V3CommandReport = 0x01;

        /// <summary>The answer the SDK reads after a command, by protocol
        /// (wooting-usb.c:27-29 and 703-711), and how long one read waits
        /// (line 31).</summary>
        internal const int V1ResponseSize = 128;
        internal const int V2ResponseSize = 256;
        internal const int ResponseTimeoutMs = 1000;

        /// <summary>RGB_RAW_BUFFER_SIZE (wooting-usb.h:81) and the LED
        /// limits of the first firmware's two boards, WOOTING_ONE_KEY_CODE_LIMIT
        /// and WOOTING_TWO_KEY_CODE_LIMIT (lines 88-89, set per board at
        /// wooting-usb.c:111-134).</summary>
        internal const int V1BufferSize = 96;
        internal const int OneKeyCodeLimit = 95;
        internal const int TwoKeyCodeLimit = 116;

        /// <summary>The first firmware's columns per board, WOOTING_ONE_RGB_COLS
        /// and WOOTING_TWO_RGB_COLS (wooting-usb.h:85-86): a cell past them
        /// is NOLED (wooting-rgb-sdk.c:75-80).</summary>
        internal const int OneColumns = 17;
        internal const int TwoColumns = 21;

        /// <summary>The ANSI and ISO places of Enter and the left Shift,
        /// which the SDK lights alike on the first firmware
        /// (wooting-rgb-sdk.c:20-24 and 347-357).</summary>
        internal const int LedLeftShiftAnsi = 9;
        internal const int LedLeftShiftIso = 7;
        internal const int LedEnterAnsi = 65;
        internal const int LedEnterIso = 62;
        internal const byte NoLed = 255;

        /// <summary>The SDK's gamma table for the first firmware's LED
        /// drivers, gammaFilter (wooting-rgb-sdk.c:36-54).</summary>
        internal static readonly byte[] Gamma =
        {
            0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
            2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4,
            5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8,
            9, 9, 9, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11,
            12, 12, 13, 13, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18,
            18, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22, 22, 22, 23, 24,
            24, 25, 25, 26, 26, 26, 27, 27, 28, 29, 29, 30, 31, 32, 32,
            33, 34, 35, 35, 36, 37, 38, 39, 39, 40, 41, 42, 42, 43, 43,
            44, 44, 45, 45, 46, 47, 48, 49, 50, 50, 51, 51, 52, 52, 53,
            54, 54, 55, 55, 56, 56, 57, 57, 58, 58, 59, 59, 60, 60, 61,
            61, 62, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74,
            75, 77, 78, 79, 81, 82, 83, 85, 86, 87, 89, 90, 92, 93, 95,
            96, 98, 99, 101, 102, 104, 105, 107, 109, 110, 112, 114, 115, 117, 119,
            120, 122, 124, 126, 127, 129, 131, 133, 135, 137, 138, 140, 142, 144, 146,
            148, 150, 152, 154, 156, 158, 160, 162, 164, 167, 169, 171, 173, 175, 177,
            180, 182, 184, 186, 189, 191, 193, 196, 198, 200, 203, 205, 208, 210, 213,
            215, 218, 220, 223, 225, 228, 231, 233, 236, 239, 241, 244, 247, 249, 252,
            255,
        };

        /// <summary>
        /// The route's line in the link pass: while the program holding a
        /// row's device is Wootility, each awake Wooting unit of the row's
        /// device that the shared worker lists
        /// (<see cref="WootingKeyboardLighting"/>, by its programs) goes into
        /// <paramref name="shared"/> as a path of this route, keyed by the
        /// unit's collection path.
        /// </summary>
        internal static void AddShared(List<OutputPath> shared, VendorProgram holder, IReadOnlyList<DirectUnit> direct, LinkRow row)
        {
            if (!ReferenceEquals(holder, VendorPrograms.WootingWootility)) return;
            foreach (var unit in direct)
            {
                if (unit.Family != OutputFamily.HidLightingUnit || unit.Asleep || !IsWooting(unit)) continue;
                if (!PeripheralLinker.MatchesDevice(unit, row)) continue;
                var path = new OutputPath(OutputFamily.WootingRgbSdk, unit.Key);
                if (!shared.Contains(path)) shared.Add(path);
            }
        }

        /// <summary>Whether a shared-worker unit is one of the Wooting family's,
        /// by the programs its model names.</summary>
        internal static bool IsWooting(DirectUnit unit)
        {
            if (unit?.Programs == null) return false;
            foreach (var program in unit.Programs)
                if (ReferenceEquals(program, VendorPrograms.WootingWootility)) return true;
            return false;
        }

        /// <summary>A command in the SDK's form: report 0x01 and D1 DA for
        /// the third protocol, report 0 and D0 DA before it, the command, and
        /// zero parameters (wooting-usb.c:684-701).</summary>
        internal static byte[] Command(WootingKeyboardLighting.Protocol protocol, byte command)
        {
            bool v3 = protocol == WootingKeyboardLighting.Protocol.V3;
            return new byte[] { v3 ? V3CommandReport : (byte)0, v3 ? (byte)0xD1 : (byte)0xD0, 0xDA, command, 0, 0, 0, 0 };
        }

        /// <summary>The answer the SDK waits for after a command: one report
        /// on the third protocol, else 256 bytes on the second and 128 on
        /// the first, read in as many reports as they take
        /// (wooting-usb.c:703-718 and 805-828).</summary>
        internal static int ResponseSize(WootingKeyboardLighting.Protocol protocol) => protocol switch
        {
            WootingKeyboardLighting.Protocol.V3 => 1,
            WootingKeyboardLighting.Protocol.V2 => V2ResponseSize,
            _ => V1ResponseSize,
        };

        /// <summary>encodeColor (wooting-rgb-sdk.c:83-91): 0xRRGGBB in 16
        /// bits.</summary>
        internal static ushort Encode(int rgb)
            => (ushort)((((rgb >> 16) & 0xF8) << 8) | (((rgb >> 8) & 0xFC) << 3) | ((rgb & 0xF8) >> 3));

        /// <summary>decodeColor (wooting-rgb-sdk.c:93-98).</summary>
        internal static (byte R, byte G, byte B) Decode(ushort color)
            => ((byte)((color >> 8) & 0xF8), (byte)((color >> 3) & 0xFC), (byte)((color << 3) & 0xF8));

        /// <summary>The SDK's matrix of a picture: each light's color at its
        /// cell, encoded, and 0 where no light is. A cell outside the
        /// matrix is dropped.</summary>
        internal static ushort[] Matrix(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var matrix = new ushort[Rows * Columns];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int cell = lights[n].Address;
                if ((uint)cell < (uint)matrix.Length) matrix[cell] = Encode(colors[n]);
            }
            return matrix;
        }

        /// <summary>The second and third protocols' color report: the report
        /// ID, the magic word, report 11, then the matrix, low byte first
        /// (wooting-usb.c:612-618 and 662-669).</summary>
        internal static byte[] ColorReport(WootingKeyboardLighting.Protocol protocol, ushort[] matrix)
        {
            bool v3 = protocol == WootingKeyboardLighting.Protocol.V3;
            var report = new byte[v3 ? V3ReportSize : V2ReportSize];
            report[0] = v3 ? V3ColorReport : (byte)0;
            report[1] = v3 ? (byte)0xD1 : (byte)0xD0;
            report[2] = 0xDA;
            report[3] = RawColorsReport;
            for (int i = 0; i < matrix.Length; i++)
            {
                report[4 + i * 2] = (byte)matrix[i];
                report[5 + i * 2] = (byte)(matrix[i] >> 8);
            }
            return report;
        }

        /// <summary>The second protocol's report in four, each a report 0
        /// with the next 64 bytes after the report's ID byte
        /// (wooting-usb.c:620-640).</summary>
        internal static List<byte[]> SmallPackets(byte[] report)
        {
            var packets = new List<byte[]>(SmallPacketCount);
            for (int i = 0; i < SmallPacketCount; i++)
            {
                var packet = new byte[SmallPacketSize + 1];
                Array.Copy(report, i * SmallPacketSize + 1, packet, 1, SmallPacketSize);
                packets.Add(packet);
            }
            return packets;
        }

        /// <summary>The first firmware's LED limit and columns for a board
        /// (wooting-usb.c:111-134): the One's, a tenkeyless board, else the
        /// Two's.</summary>
        internal static (int Limit, int Columns) V1Board(WootingKeyboardLighting.KeyboardType type)
            => type == WootingKeyboardLighting.KeyboardType.Tkl ? (OneKeyCodeLimit, OneColumns) : (TwoKeyCodeLimit, TwoColumns);

        /// <summary>
        /// The first protocol's reports, wooting_rgb_build_v1_buffers and
        /// wooting_rgb_array_update_keyboard (wooting-rgb-sdk.c:204-244 and
        /// 308-362): each cell's LED by get_safe_led_idex, nothing for NOLED,
        /// a cell past the board's columns or an LED past its limit; the LED's
        /// driver buffer by its index in 24s and its place by pwm_mem_map; the
        /// cell's color decoded from the matrix and put through the gamma
        /// table, red, green and blue 0x10 apart; the ANSI Enter and left
        /// Shift copied to their ISO places. Then one report per buffer, the
        /// fifth only on a full-size board (lines 237-241), each 00 D0 DA 0B,
        /// the driver and the register, the buffer, and a CRC-16/CCITT of the
        /// first 127 bytes, low byte first (wooting-usb.c:541-604).
        /// </summary>
        internal static List<byte[]> V1Reports(ushort[] matrix, WootingKeyboardLighting.KeyboardType type)
        {
            var (limit, columns) = V1Board(type);
            var buffers = new byte[5][];
            for (int b = 0; b < buffers.Length; b++) buffers[b] = new byte[V1BufferSize];
            for (int row = 0; row < Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    int led = column < columns ? WootingLayouts.V1LedIndex[row * Columns + column] : NoLed;
                    if (led > limit) continue;
                    var buffer = buffers[led >= 96 ? 4 : led >= 72 ? 3 : led >= 48 ? 2 : led >= 24 ? 1 : 0];
                    int at = WootingLayouts.V1PwmMap[led % 24];
                    var (r, g, bl) = Decode(matrix[row * Columns + column]);
                    buffer[at] = Gamma[r];
                    buffer[at + 0x10] = Gamma[g];
                    buffer[at + 0x20] = Gamma[bl];
                    if (led == LedEnterAnsi)
                    {
                        int iso = WootingLayouts.V1PwmMap[LedEnterIso - 48];
                        buffers[2][iso] = buffers[2][at];
                        buffers[2][iso + 0x10] = buffers[2][at + 0x10];
                        buffers[2][iso + 0x20] = buffers[2][at + 0x20];
                    }
                    else if (led == LedLeftShiftAnsi)
                    {
                        int iso = WootingLayouts.V1PwmMap[LedLeftShiftIso];
                        buffers[0][iso] = buffers[0][at];
                        buffers[0][iso + 0x10] = buffers[0][at + 0x10];
                        buffers[0][iso + 0x20] = buffers[0][at + 0x20];
                    }
                }
            }
            int parts = type == WootingKeyboardLighting.KeyboardType.Full ? 5 : 4;
            var reports = new List<byte[]>(parts);
            for (int part = 0; part < parts; part++)
            {
                var report = new byte[V1ReportSize];
                report[1] = 0xD0;
                report[2] = 0xDA;
                report[3] = RawColorsReport;
                report[4] = (byte)(part / 2);
                report[5] = (byte)(part % 2 == 1 ? V1BufferSize : 0);
                Array.Copy(buffers[part], 0, report, 6, V1BufferSize);
                ushort crc = WootingKeyboardLighting.Crc16Ccitt(report, V1ReportSize - 2);
                report[127] = (byte)crc;
                report[128] = (byte)(crc >> 8);
                reports.Add(report);
            }
            return reports;
        }

        /// <summary>The reports of a picture: the first protocol's five or
        /// four, the third's one, and the second's one, or four small ones on
        /// a collection whose output report is shorter than the 257 bytes
        /// (the SDK reads the same from the report descriptor,
        /// wooting-usb.c:444-482).</summary>
        internal static List<byte[]> Reports(WootingKeyboardLighting.Protocol protocol, WootingKeyboardLighting.KeyboardType type,
            int outputLength, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var matrix = Matrix(lights, colors);
            if (protocol == WootingKeyboardLighting.Protocol.V1) return V1Reports(matrix, type);
            var report = ColorReport(protocol, matrix);
            if (protocol == WootingKeyboardLighting.Protocol.V2 && outputLength > 0 && outputLength < V2ReportSize)
                return SmallPackets(report);
            return new List<byte[]> { report };
        }
    }
}
