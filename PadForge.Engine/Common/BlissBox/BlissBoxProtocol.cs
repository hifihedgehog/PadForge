using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.BlissBox
{
    /// <summary>
    /// The Bliss-Box API (issue #469): the USB feature reports a Bliss-Box
    /// 4-Play, Gamer-Pro, Gamer-Pro jr. or their Advanced (GPA) successors
    /// answer on the same HID interface that carries the joystick report.
    ///
    /// <para>Every byte here is read from Bliss-Box LLC's API Tool (SVN r63:
    /// BBAPI.cs, and the C command-line tool cli/Bliss-BoxAPI/main-original.c)
    /// and from DeviceBuddy (bliss_box_api.js, commit 9356407), and checked
    /// against the GPA 4.86 and 4-Play/Gamer-Pro 3.0 build 034 firmware
    /// images. Word addresses into those images are cited where the firmware
    /// settles something the tools leave open.</para>
    ///
    /// <para>One port is one USB device, product 0x0D03 plus the player
    /// number. A feature report read comes back with the device's own first
    /// byte where the report ID went, which hidapi, WebHID and the C tool
    /// all see alike, so every parser here reads from byte 0. The reads that
    /// carry a player byte are thrown away when it does not match, the check
    /// BBAPI.cs makes on every read.</para>
    /// </summary>
    public static class BlissBoxProtocol
    {
        public const ushort VendorId = 0x16D0;

        /// <summary>Player 1's product ID. Players 2 to 4 follow it
        /// (BBAPI.cs constructor, DeviceBuddy's udev example).</summary>
        public const ushort PlayerOneProductId = 0x0D04;

        public const int MaxPlayer = 4;

        /// <summary>True for a port in normal operation. The bootloaders
        /// (0x0A5F, 0x04FB), the 1.x firmware (0x0A60) and the "4-Play Fix"
        /// (0x0A61 to 0x0A64) are never matched, so nothing here ever talks
        /// to an adapter mid-update.</summary>
        public static bool IsPort(ushort vendorId, ushort productId)
            => vendorId == VendorId
               && productId >= PlayerOneProductId
               && productId < PlayerOneProductId + MaxPlayer;

        /// <summary>The player number a port's product ID carries, 1 to 4.</summary>
        public static int PlayerOf(ushort productId) => productId - (PlayerOneProductId - 1);

        /// <summary>The byte a port puts in its answers: the player plus 3
        /// (BBAPI.cs <c>devicePlayer + 3</c>, main-original.c
        /// <c>commandRequestData[3]-3</c>).</summary>
        public static byte PlayerByte(int player) => (byte)(player + 3);

        // Feature report IDs (BBAPI.cs, bliss_box_api.js).
        public const byte ReportInfo = 17;
        public const byte ReportCommand = 18;
        public const byte ReportScreen = 20;
        public const byte ReportPressure = 21;
        public const byte ReportNative = 22;
        public const byte ReportScreenRead = 24;

        // Commands on report 18, and the screen command on report 20.
        public const byte CommandLargeMotor = 0x04;
        public const byte CommandSmallMotor = 0x05;
        public const byte CommandPlayer = 0x08;
        public const byte CommandNative = 0x25;
        public const byte CommandScreen = 0x24;

        /// <summary>A command report: report ID, command, two zero bytes and
        /// up to five parameters, nine bytes in all (BBAPI.cs
        /// <c>send(8 - 4)</c>).</summary>
        public const int CommandReportLength = 9;

        /// <summary>The screen report: report ID, command 0x24, two zero bytes,
        /// the 192-byte picture and one spare byte (BBAPI.cs
        /// <c>send(192)</c>, 192 + 1 + 4).</summary>
        public const int ScreenReportLength = 197;

        public const int ScreenBytes = 192;
        public const int PressureCount = 12;

        /// <summary>The native channel's "use" byte for talking to the
        /// controller (talk.cs <c>1/*native*/</c>).</summary>
        public const byte NativeUse = 1;

        /// <summary>Data bytes one native chunk carries.</summary>
        public const int NativeChunkBytes = 5;

        /// <summary>Report 17's mode byte: set while the port looks for a
        /// controller (main-original.c "Searching...", Form1.cs populateInfo).</summary>
        public const byte FlagSearching = 0x01;

        /// <summary>Builds a command report. Parameters past the fifth are an
        /// error, not a truncation.</summary>
        public static byte[] Command(byte command, params byte[] parameters)
        {
            if (parameters != null && parameters.Length > 5)
                throw new ArgumentException("A command carries at most five parameters.", nameof(parameters));
            var report = new byte[CommandReportLength];
            report[0] = ReportCommand;
            report[1] = command;
            if (parameters != null)
                Array.Copy(parameters, 0, report, 4, parameters.Length);
            return report;
        }

        /// <summary>
        /// One motor's command. Strength 0 sends type 0, off. Anything else
        /// sends type 1, on at that strength with the loop count at 0xFF,
        /// which is what main-original.c's <c>set rumble 1</c> sends and what
        /// the 3.0 firmware sets itself for type 1 (0x0A87 to 0x0A97). The
        /// large motor is command 4 and the small one command 5 (rumble.cs
        /// Strong_Click and Week_Click, firmware 0x0A98 and 0x0AA5).
        /// </summary>
        public static byte[] Rumble(bool largeMotor, byte strength)
        {
            byte command = largeMotor ? CommandLargeMotor : CommandSmallMotor;
            return strength == 0
                ? Command(command, 0, 0, 0)
                : Command(command, 1, strength, 0xFF);
        }

        /// <summary>Sets the port's player number and resets the port, which
        /// then comes back as <c>0x0D03 + player</c> (BBAPI.cs setPlayer, the
        /// API Tool's player wizard calling <c>setPlayer(n, 1)</c>).</summary>
        public static byte[] SetPlayer(int player)
        {
            if (player < 1 || player > MaxPlayer)
                throw new ArgumentOutOfRangeException(nameof(player));
            return Command(CommandPlayer, PlayerByte(player), 1);
        }

        /// <summary>The screen report for a picture already in wire order
        /// (<see cref="BlissBoxScreen.ToWire"/>): BBAPI.cs sendLCD.</summary>
        public static byte[] Screen(ReadOnlySpan<byte> wire)
        {
            if (wire.Length != ScreenBytes)
                throw new ArgumentException("The screen takes 192 bytes.", nameof(wire));
            var report = new byte[ScreenReportLength];
            report[0] = ReportScreen;
            report[1] = CommandScreen;
            wire.CopyTo(report.AsSpan(4));
            return report;
        }

        /// <summary>
        /// The command reports that hand a message to the controller through
        /// the native channel. A header carries the size, the use and the
        /// first two bytes. Five-byte chunks carry the rest, each at its
        /// position, and the last is marked 0xFF.
        ///
        /// <para>The two firmware generations place the 0xFF chunk
        /// differently. The 3.0 firmware puts it at the previous chunk's
        /// position plus five, from RAM it never resets between messages
        /// (0x0967 to 0x0983), and copies five bytes, so a 0xFF chunk must
        /// follow a positioned one. GPA 4.86 puts a 0xFF chunk that follows
        /// the header at position 2, and one that follows a positioned chunk
        /// at that chunk's position plus five, and copies the message size
        /// minus that position (0x2D47 to 0x2DAF). After a positioned chunk
        /// at 2, a message of 3 to 6 bytes makes that count negative, and the
        /// copy runs over the adapter's RAM. So a message whose data after the
        /// header fits one chunk goes to a GPA as a lone 0xFF chunk, as
        /// DeviceBuddy sends it (bliss_box_api.js), and to a 3.x adapter as a
        /// positioned chunk and an empty 0xFF one, as BBAPI.cs sends it. A
        /// longer message ends with its last data in a 0xFF chunk after the
        /// positioned ones, which both generations read alike. BBAPI.cs's
        /// count sends a chunk too few for messages of 28 to 30 bytes and
        /// every 25 bytes after, and a chunk too many for some others, which a
        /// GPA reads as a negative count. The count here is exact.</para>
        /// </summary>
        /// <param name="advanced">A GPA, firmware 4 and up
        /// (<see cref="BlissBoxInfo.IsAdvanced"/>).</param>
        public static List<byte[]> NativeReports(ReadOnlySpan<byte> message, bool advanced, byte use = NativeUse)
        {
            if (message.Length == 0 || message.Length > 255)
                throw new ArgumentException("A native message is 1 to 255 bytes.", nameof(message));
            int length = message.Length;
            var reports = new List<byte[]>();

            var header = new byte[CommandReportLength];
            header[0] = ReportCommand;
            header[1] = CommandNative;
            header[2] = 0;                  // 0 marks the header
            header[3] = 0;                  // size, high byte
            header[4] = (byte)length;       // size, low byte
            header[5] = use;
            header[6] = message[0];
            if (length > 1) header[7] = message[1];
            reports.Add(header);
            if (length <= 2) return reports;

            int remaining = length - 2;
            int chunks = (remaining + NativeChunkBytes - 1) / NativeChunkBytes;
            // One chunk: a GPA takes it alone as the 0xFF chunk, a 3.x
            // adapter positioned and then closed by an empty 0xFF chunk.
            bool lone = chunks == 1 && advanced;
            for (int i = 0; i < chunks; i++)
            {
                bool last = i == chunks - 1 && (chunks > 1 || lone);
                int start = 2 + i * NativeChunkBytes;
                var chunk = new byte[CommandReportLength];
                chunk[0] = ReportCommand;
                chunk[1] = CommandNative;
                chunk[2] = last ? (byte)0xFF : (byte)start;
                for (int b = 0; b < NativeChunkBytes && start + b < length; b++)
                    chunk[3 + b] = message[start + b];
                reports.Add(chunk);
            }
            if (chunks == 1 && !lone)
            {
                var terminator = new byte[CommandReportLength];
                terminator[0] = ReportCommand;
                terminator[1] = CommandNative;
                terminator[2] = 0xFF;
                reports.Add(terminator);
            }
            return reports;
        }

        /// <summary>Report 17, parsed: controller type, mode flags, firmware
        /// major and minor. Null when the player byte (byte 3) does not match,
        /// the check BBAPI.cs getInfo makes, or the read is short.</summary>
        public static BlissBoxInfo ParseInfo(ReadOnlySpan<byte> data, int player)
        {
            if (data.Length < 5 || data[3] != PlayerByte(player)) return null;
            return new BlissBoxInfo(data[0], data[1], data[2], data[4], player);
        }

        /// <summary>Report 21: the player byte, then twelve pressure bytes in
        /// <see cref="BlissBoxControllers.PressureNames"/> order. False when
        /// the player byte does not match (BBAPI.cs getPressure).</summary>
        public static bool TryParsePressure(ReadOnlySpan<byte> data, int player, Span<byte> pressure)
        {
            if (data.Length < 1 + PressureCount || pressure.Length < PressureCount) return false;
            if (data[0] != PlayerByte(player)) return false;
            data.Slice(1, PressureCount).CopyTo(pressure);
            return true;
        }

        /// <summary>Report 24: the player byte, then the stored picture in wire
        /// order (BBAPI.cs getLCD, dLCD.cs <c>Skip(1)</c>).</summary>
        public static bool TryParseScreen(ReadOnlySpan<byte> data, int player, Span<byte> wire)
        {
            if (data.Length < 1 + ScreenBytes || wire.Length < ScreenBytes) return false;
            if (data[0] != PlayerByte(player)) return false;
            data.Slice(1, ScreenBytes).CopyTo(wire);
            return true;
        }

        /// <summary>Report 22's three outcomes.</summary>
        public enum NativeReplyState
        {
            /// <summary>Another player's answer, or none yet: read again.</summary>
            NotReady,
            /// <summary>This port answered with no data: the controller gave
            /// no reply (BBAPI.cs getData, "use is 0 and ID matches").</summary>
            NoReply,
            /// <summary>The controller's answer is in the reply.</summary>
            Ready,
        }

        /// <summary>Report 22: the player byte, the use, the size, then the
        /// controller's answer (BBAPI.cs getData, bliss_box_api.js
        /// <c>reply.slice(3)</c>).</summary>
        public static NativeReplyState ParseNativeReply(ReadOnlySpan<byte> data, int player, byte use, out byte[] reply)
        {
            reply = null;
            if (data.Length < 3 || data[0] != PlayerByte(player)) return NativeReplyState.NotReady;
            if (data[1] == use && data[2] > 0)
            {
                int size = Math.Min(data[2], data.Length - 3);
                reply = data.Slice(3, size).ToArray();
                return NativeReplyState.Ready;
            }
            if (data[1] == 0) return NativeReplyState.NoReply;
            return NativeReplyState.NotReady;
        }
    }

    /// <summary>Report 17: what a port reads and which firmware it runs.</summary>
    public sealed class BlissBoxInfo
    {
        public BlissBoxInfo(byte type, byte flags, byte major, byte minor, int player)
        {
            Type = type;
            Flags = flags;
            Major = major;
            Minor = minor;
            Player = player;
        }

        /// <summary>The attached controller's type code
        /// (<see cref="BlissBoxControllers"/>).</summary>
        public byte Type { get; }

        public byte Flags { get; }
        public byte Major { get; }
        public byte Minor { get; }
        public int Player { get; }

        /// <summary>No controller is attached yet.</summary>
        public bool Searching => (Flags & BlissBoxProtocol.FlagSearching) != 0;

        /// <summary>A GPA-generation adapter: firmware 4 and up (BBAPI.cs
        /// getInfo <c>if (inputReportBuffer[2] == 4) GPA = true</c>).</summary>
        public bool IsAdvanced => Major >= 4;

        public override bool Equals(object obj)
            => obj is BlissBoxInfo o && o.Type == Type && o.Flags == Flags && o.Major == Major
               && o.Minor == Minor && o.Player == Player;

        public override int GetHashCode() => HashCode.Combine(Type, Flags, Major, Minor, Player);
    }
}
