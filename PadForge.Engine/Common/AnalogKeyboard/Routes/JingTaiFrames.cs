using System;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>
    /// The JingTai V1 "5C" frame (issue #468): HallJoy's mg75_pro_protocol.h,
    /// which its Chilkey Slice75 route imports whole (slice75_protocol.h:38-47)
    /// and whose request layout the SparkPlayJoy RM 6x21 firmware shares
    /// (aula_win60he_protocol.cpp:50-79 computes the same bytes).
    ///
    /// <para>A request is one 65-byte Windows buffer: report ID 0 (the vendor
    /// collection numbers no reports), <c>5C</c>, the payload length, the
    /// command, a checksum, then the payload, zero-padded
    /// (mg75_pro_protocol.h:41-52). The checksum covers the three header bytes
    /// and the last payload byte only (mg75_pro_protocol.h:37-40), so a change
    /// in a middle byte passes it. An answer carries the command with bit 7
    /// set and may span several reports, each copying up to 64 frame bytes
    /// from its byte 1 (mg75_pro_protocol.h:76-99). Nothing in an answer names
    /// the request it answers, so one request is outstanding at a time and a
    /// failed or late exchange ends the session (mg75_pro_protocol.h:74-75).</para>
    ///
    /// <para>Positions are slots of a 6 by 21 matrix, slot = row * 21 + column.
    /// Travel half 1 carries slots 0 to 62 and half 2 slots 63 to 125
    /// (mg75_pro_backend.cpp:349). Key tables hold PadForge key codes: the
    /// vendor action decoded the way HallJoy's Decode does, the Fn action
    /// 0xF001 as <see cref="AnalogKeyCodes.Fn"/>.</para>
    /// </summary>
    public static class JingTaiFrames
    {
        /// <summary>Windows report length, report ID byte included
        /// (mg75_pro_protocol.h:11).</summary>
        public const int ReportBytes = 65;

        /// <summary>Matrix positions, 6 rows of 21 (mg75_pro_protocol.h:11).</summary>
        public const int Slots = 126;
        public const int Columns = 21;
        public const int ValuesPerHalf = 63;

        /// <summary>Selectors per 0x23 assignment read (mg75_pro_protocol.h:58).</summary>
        public const int LayoutKeys = 14;

        public const byte Head = 0x5C;
        public const byte CommandTravel = 0x12;
        public const byte CommandLayout = 0x23;
        public const byte CommandFactory = 0x2B;

        /// <summary>The vendor's Fn action (mg75_pro_protocol.h:26).</summary>
        public const int FnAction = 0xF001;

        /// <summary>Largest travel value a half may carry. One above it rejects
        /// the whole half (mg75_pro_protocol.h:107).</summary>
        public const int MaxTravelRaw = 6000;

        /// <summary>Longest frame an answer can announce, 4 + 252
        /// (mg75_pro_protocol.h:84).</summary>
        public const int MaxFrameBytes = 256;

        /// <summary>The checksum of a frame whose byte 0 is <c>5C</c>:
        /// 0x35 plus the head, length and command bytes plus the last payload
        /// byte, modulo 256 (mg75_pro_protocol.h:37-40). The same rule checks
        /// answers.</summary>
        public static byte Checksum(ReadOnlySpan<byte> frame)
        {
            int length = frame[1];
            return (byte)(0x35 + frame[0] + length + frame[2] + (length != 0 ? frame[3 + length] : 0));
        }

        /// <summary>A request as a Windows buffer, report ID first
        /// (mg75_pro_protocol.h:41-52). A payload over 60 bytes does not fit
        /// one report and yields an all-zero buffer, as HallJoy's builder does.</summary>
        public static byte[] Request(byte command, ReadOnlySpan<byte> payload)
        {
            var r = new byte[ReportBytes];
            if (payload.Length > 60) return r;
            r[1] = Head;
            r[2] = (byte)payload.Length;
            r[3] = command;
            payload.CopyTo(r.AsSpan(5));
            r[4] = Checksum(r.AsSpan(1));
            return r;
        }

        /// <summary>Travel half 1 or 2: command 0x12, payload
        /// <c>02 half FF FF</c> (mg75_pro_protocol.h:53-57). Any other half
        /// yields an all-zero buffer.</summary>
        public static byte[] Travel(int half)
            => half == 1 || half == 2
                ? Request(CommandTravel, stackalloc byte[] { 2, (byte)half, 0xFF, 0xFF })
                : new byte[ReportBytes];

        /// <summary>The base-layer assignment read, command 0x23: payload
        /// length 57, a zero byte, then selector i at buffer byte 6 + 4i
        /// (mg75_pro_protocol.h:60-69). The last payload byte is always 0, so
        /// the checksum is always ED. Unused selectors stay 0.</summary>
        public static byte[] Layout(ReadOnlySpan<byte> selectors)
        {
            var r = new byte[ReportBytes];
            r[1] = Head;
            r[2] = 57;
            r[3] = CommandLayout;
            for (int i = 0; i < LayoutKeys && i < selectors.Length; i++)
                r[6 + 4 * i] = selectors[i];
            r[4] = Checksum(r.AsSpan(1));
            return r;
        }

        /// <summary>The factory-row read for rows <paramref name="firstRow"/>
        /// and the next, command 0x2B, payload <c>00 row row+1</c>
        /// (mg75_pro_protocol.h:70-73).</summary>
        public static byte[] Factory(int firstRow)
            => Request(CommandFactory, stackalloc byte[] { 0, (byte)firstRow, (byte)(firstRow + 1) });

        /// <summary>A vendor action as a key code: the Fn action as
        /// <see cref="AnalogKeyCodes.Fn"/>, a byte as that HID usage, anything
        /// else as 0, unassigned (mg75_pro_protocol.h:25-27).</summary>
        public static int Decode(int action)
            => action == FnAction ? AnalogKeyCodes.Fn : action <= 0xFF ? action : 0;

        /// <summary>The selector the keyboard knows a key by: the factory
        /// action's low byte, 1 for the Fn action (mg75_pro_protocol.h:28-32).
        /// Takes the decoded code, which carries the same information.</summary>
        public static byte Selector(int code) => code == AnalogKeyCodes.Fn ? (byte)1 : (byte)code;

        /// <summary>Reads a completed travel answer into
        /// <paramref name="values"/> (63 entries): frame size 132, answer
        /// command 0x92, type byte 2, then 63 little-endian values. A value over
        /// 6000 rejects the half, and <paramref name="values"/> is written only
        /// when the whole half is valid (mg75_pro_protocol.h:100-112).</summary>
        public static bool ParseTravel(JingTaiFrame f, Span<ushort> values)
        {
            if (f == null || !f.Complete || f.Size != 132 || f.Bytes[2] != 0x92 || f.Bytes[5] != 2
                || values.Length < ValuesPerHalf)
                return false;
            Span<ushort> next = stackalloc ushort[ValuesPerHalf];
            for (int i = 0; i < ValuesPerHalf; i++)
            {
                next[i] = (ushort)(f.Bytes[6 + 2 * i] | (f.Bytes[7 + 2 * i] << 8));
                if (next[i] > MaxTravelRaw) return false;
            }
            next.CopyTo(values);
            return true;
        }

        /// <summary>Reads a completed assignment answer: frame size 61, answer
        /// command 0xA3, and for every nonzero requested selector i an echo of
        /// the selector at byte 5 + 4i, a 0 at byte 6 + 4i and the assigned
        /// action, little-endian, at bytes 7 + 4i and 8 + 4i. Records match the
        /// request by position, and the unused ones are not checked
        /// (mg75_pro_protocol.h:113-129). <paramref name="assigned"/> receives
        /// the decoded actions only when every record checks out.</summary>
        public static bool ParseLayout(JingTaiFrame f, ReadOnlySpan<byte> selectors, Span<int> assigned)
        {
            if (f == null || !f.Complete || f.Size != 61 || f.Bytes[2] != 0xA3
                || selectors.Length < LayoutKeys || assigned.Length < LayoutKeys)
                return false;
            Span<int> next = stackalloc int[LayoutKeys];
            next.Clear();
            for (int i = 0; i < LayoutKeys; i++)
            {
                if (selectors[i] == 0) continue;
                int offset = 5 + 4 * i;
                if (f.Bytes[offset] != selectors[i] || f.Bytes[offset + 1] != 0) return false;
                next[i] = Decode(f.Bytes[offset + 2] | (f.Bytes[offset + 3] << 8));
            }
            next.CopyTo(assigned);
            return true;
        }

        /// <summary>Checks a completed factory-row answer against a pinned
        /// table: frame size 49, answer command 0xAB, the row at byte 5 and the
        /// next at byte 27, then the 21 selectors of each row at bytes 6 and 28,
        /// every one equal to the table's selector, 0 at an empty slot
        /// (mg75_pro_protocol.h:130-139, slice75_protocol.h:48-57).</summary>
        public static bool MatchFactory(JingTaiFrame f, int row, int[] table)
        {
            if (row < 0 || row > 4 || row % 2 != 0 || f == null || !f.Complete || f.Size != 49
                || f.Bytes[2] != 0xAB || f.Bytes[5] != row || f.Bytes[27] != row + 1
                || table == null || table.Length < Slots)
                return false;
            for (int c = 0; c < Columns; c++)
            {
                if (f.Bytes[6 + c] != Selector(table[row * Columns + c])
                    || f.Bytes[28 + c] != Selector(table[(row + 1) * Columns + c]))
                    return false;
            }
            return true;
        }

        /// <summary>A raw travel value as HallJoy's 0 to 1000 depth:
        /// min(1000, (raw * 1000 + range / 2) / range) in integer math, 0 for a
        /// zero range. No dead zone and no calibration
        /// (mg75_pro_protocol.h:140-144, slice75_protocol.h:58-61).</summary>
        public static int Normalize(int raw, int range)
        {
            if (range <= 0) return 0;
            long milli = ((long)raw * 1000 + range / 2) / range;
            return (int)Math.Min(1000, milli);
        }

        /// <summary>
        /// Replaces <paramref name="output"/> with one depth per key.
        /// <paramref name="milli"/>[p] is the 0 to 1000 depth at position p and
        /// <paramref name="codes"/>[p] the key code there. Positions that share
        /// a code publish the largest depth, as HallJoy's alias reads do
        /// (physical_analog_state.h:39-52, aula_win60he_client.cpp:640-666).
        /// HallJoy publishes every key. The output holds at most
        /// <see cref="AnalogKeyInputState.MaxKeys"/>, fewer than a full matrix
        /// can report, so when more keys are down than it holds, the deepest
        /// are kept rather than the first in matrix order.
        /// </summary>
        public static void WriteDepths(ReadOnlySpan<int> codes, ReadOnlySpan<int> milli, AnalogKeyInputState output)
        {
            output.ResetForReuse();
            int positions = Math.Min(codes.Length, milli.Length);
            Span<int> keyCode = stackalloc int[Slots];
            Span<int> keyDepth = stackalloc int[Slots];
            int n = 0;
            for (int p = 0; p < positions; p++)
            {
                int code = codes[p], m = milli[p];
                if (code <= 0 || code >= AnalogKeyInputState.CodeCount || m <= 0) continue;
                int j = 0;
                while (j < n && keyCode[j] != code) j++;
                if (j < n)
                {
                    if (m > keyDepth[j]) keyDepth[j] = m;
                    continue;
                }
                if (n == keyCode.Length) continue;
                keyCode[n] = code;
                keyDepth[n] = m;
                n++;
            }
            if (n > AnalogKeyInputState.MaxKeys)
            {
                // Deepest first: sort by negated depth, carrying the codes.
                for (int j = 0; j < n; j++) keyDepth[j] = -keyDepth[j];
                keyDepth.Slice(0, n).Sort(keyCode.Slice(0, n));
                for (int j = 0; j < n; j++) keyDepth[j] = -keyDepth[j];
                n = AnalogKeyInputState.MaxKeys;
            }
            for (int j = 0; j < n; j++) output.Set(keyCode[j], keyDepth[j] / 1000f);
        }

        /// <summary>The checks HallJoy's InstallMap makes of a model table
        /// before any key is published: exactly <paramref name="count"/>
        /// occupied slots and no key code on two of them, since
        /// native_layout::Publish rejects a list in which two keys share a
        /// factory code (mg75_pro_backend.cpp:291-316, slice75_backend.cpp:280-303,
        /// native_layout_state.h:38-40).</summary>
        public static bool IsInstallable(int[] table, int count)
        {
            if (table == null || table.Length != Slots) return false;
            var seen = new bool[AnalogKeyInputState.CodeCount];
            int keys = 0;
            foreach (int code in table)
            {
                if (code == 0) continue;
                if (code < 0 || code >= AnalogKeyInputState.CodeCount || seen[code]) return false;
                seen[code] = true;
                keys++;
            }
            return keys == count;
        }
    }

    /// <summary>
    /// One answer frame being put together from its reports, HallJoy's
    /// mg75pro::Frame (mg75_pro_protocol.h:76-99). Every report must be 65
    /// bytes with report ID 0. The first names the frame: <c>5C</c>, a length
    /// of at most 252 and the request's command with bit 7 set. Each report
    /// then adds up to 64 bytes from its byte 1 until the frame is whole, and
    /// the bytes past the frame in the last report are padding. A whole frame
    /// must carry status 0 in byte 4 and a valid checksum in byte 3. Any
    /// violation fails the frame for good.
    /// </summary>
    public sealed class JingTaiFrame
    {
        /// <summary>The frame, <c>5C</c> first.</summary>
        public readonly byte[] Bytes = new byte[JingTaiFrames.MaxFrameBytes];

        /// <summary>Frame length announced by the first report, 4 + length.</summary>
        public int Size { get; private set; }

        public int Received { get; private set; }
        public bool Failed { get; private set; }
        public bool Complete => !Failed && Size != 0 && Received == Size;

        public void Reset()
        {
            Array.Clear(Bytes);
            Size = 0;
            Received = 0;
            Failed = false;
        }

        /// <summary>Adds one report as ReadFile returned it,
        /// <paramref name="count"/> bytes, for a request whose command is
        /// <paramref name="command"/>. False once the frame has failed.</summary>
        public bool Push(ReadOnlySpan<byte> report, int count, byte command)
        {
            if (Failed || count != JingTaiFrames.ReportBytes || report.Length < JingTaiFrames.ReportBytes
                || report[0] != 0 || (Size != 0 && Received >= Size))
                return Fail();
            if (Received == 0)
            {
                if (report[1] != JingTaiFrames.Head || report[2] > 252 || report[3] != (command | 0x80))
                    return Fail();
                Size = 4 + report[2];
                if (Size < 5) return Fail();
            }
            int take = Math.Min(64, Size - Received);
            report.Slice(1, take).CopyTo(Bytes.AsSpan(Received));
            Received += take;
            if (Received == Size && (Bytes[4] != 0 || Bytes[3] != JingTaiFrames.Checksum(Bytes)))
                return Fail();
            return true;
        }

        private bool Fail()
        {
            Failed = true;
            return false;
        }
    }
}
