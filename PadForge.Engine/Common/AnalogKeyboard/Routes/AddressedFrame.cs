using System;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>
    /// The 64-byte "09" frame the AULA HERO, IPI and generic Addressed routes
    /// share (issue #468), ported from HallJoy (AGPL-3.0, commit 378f9fe).
    /// One builder and one validator serve all three, so the three routes put
    /// the same bytes on the wire for the same request.
    ///
    /// <para>Layout, request and answer alike: byte 0 the report ID 09, byte 1
    /// the command, byte 2 the subcommand, bytes 3 to 5 <c>00 01 00</c>, byte
    /// 6 the data length, data from byte 7, and byte 63 a checksum that makes
    /// the 8-bit sum of all 64 bytes 0xFF, report ID included
    /// (ipi_protocol.h:9-29, aula_hero84he_diagnostic_protocol.cpp:30-59).
    /// A request names up to nine keys as big-endian 16-bit IDs from byte 7.
    /// HallJoy's IPI and generic builders write each ID at byte 8 + 2i with a
    /// zero after it (ipi_protocol.h:43-47, addressed_analog_backend.cpp:387-396),
    /// and its HERO builder writes the big-endian ID at byte 7 + 2i
    /// (aula_hero84he_diagnostic_protocol.cpp:51-55). For IDs below 256 both
    /// give the same bytes, which HallJoy's firmware emulation checked against
    /// every IPI image (docs/current/IPI_FIRMWARE_REVERSE_2026-09-14.md:96-100).
    /// An answer carries one 6-byte record per key from byte 7: the ID
    /// big-endian, then four bytes that depend on the command.</para>
    ///
    /// <para>Only the requests HallJoy sends can be built here: the identity
    /// read <c>82 01</c>, the key map read <c>83 00</c> (with IDs, or empty for
    /// the generic route's discovery), the stored calibration read <c>94 05</c>
    /// (IPI only), the sample read <c>94 02</c>, and the Addressed session
    /// start <c>98 02</c>. HallJoy's static validator forbids <c>94 00</c> and
    /// its builders cannot make <c>94 01</c>, <c>94 03</c> or <c>94 04</c>
    /// (validate_addressed_protocol_backend.py:61-65, ipi_protocol.h:40-41).</para>
    /// </summary>
    public static class AddressedFrame
    {
        public const int Length = 64;
        public const byte ReportId = 0x09;

        /// <summary>Keys per request: nine IDs and nine answer records fit in
        /// the 63-byte payload (ipi_protocol.h:13, aula_hero84he_diagnostic_protocol.h:19-22).</summary>
        public const int MaxKeys = 9;

        /// <summary>Longest data HallJoy builds or accepts (ipi_protocol.h:28,
        /// addressed_analog_backend.cpp:373).</summary>
        public const int MaxDataLength = 54;

        public const int DataOffset = 7;
        public const int RecordLength = 6;

        public const byte IdentityCommand = 0x82, IdentitySubcommand = 0x01;
        public const byte MapCommand = 0x83, MapSubcommand = 0x00;
        public const byte SampleCommand = 0x94, SampleSubcommand = 0x02;
        public const byte CalibrationCommand = 0x94, CalibrationSubcommand = 0x05;
        public const byte SessionCommand = 0x98, SessionSubcommand = 0x02;

        // ── Requests ──

        /// <summary>The identity read, <c>82 01</c> with data length 6 and no
        /// data (ipi::UuidRequest, ipi_protocol.h:30-32, and HERO's
        /// BuildIdentityRead, aula_hero84he_diagnostic_protocol.cpp:82-86).</summary>
        public static byte[] IdentityRequest() => Build(IdentityCommand, IdentitySubcommand, 6, default);

        /// <summary>The key map read <c>83 00</c> for 1 to 9 keys: layer 0 on
        /// the HERO boards (aula_hero84he_backend.cpp:344), the base map on IPI
        /// (ipi_protocol.h:38-49). Null for an ID list HallJoy refuses.</summary>
        public static byte[] MapRequest(ReadOnlySpan<ushort> ids) => Request(MapCommand, MapSubcommand, ids);

        /// <summary><see cref="MapRequest(ReadOnlySpan{ushort})"/> for the
        /// 8-bit IPI key IDs.</summary>
        public static byte[] MapRequest(ReadOnlySpan<byte> ids) => Request(MapCommand, MapSubcommand, ids);

        /// <summary>The empty <c>83 00</c> the generic route sends to discover
        /// a key map, data length 0 (addressed_analog_backend.cpp:711).</summary>
        public static byte[] EmptyMapRequest() => Build(MapCommand, MapSubcommand, 0, default);

        /// <summary>The stored calibration read <c>94 05</c>, IPI only
        /// (ipi_protocol.h:38-49, addressed_analog_backend.cpp:521). The HERO
        /// firmware treats the same command differently, and HallJoy never
        /// sends it there.</summary>
        public static byte[] CalibrationRequest(ReadOnlySpan<byte> ids)
            => Request(CalibrationCommand, CalibrationSubcommand, ids);

        /// <summary>The sample read <c>94 02</c> for 1 to 9 keys: the
        /// admission probe and every poll (addressed_analog_backend.cpp:387-396,
        /// aula_hero84he_diagnostic_protocol.cpp:96-102).</summary>
        public static byte[] SampleRequest(ReadOnlySpan<ushort> ids) => Request(SampleCommand, SampleSubcommand, ids);

        /// <summary><see cref="SampleRequest(ReadOnlySpan{ushort})"/> for
        /// 8-bit key IDs, the form the Addressed poll plan carries.</summary>
        public static byte[] SampleRequest(ReadOnlySpan<byte> ids) => Request(SampleCommand, SampleSubcommand, ids);

        private static byte[] Request(byte command, byte subcommand, ReadOnlySpan<byte> ids)
        {
            if (ids.Length > MaxKeys) return null;
            Span<ushort> wide = stackalloc ushort[MaxKeys];
            for (int i = 0; i < ids.Length; i++) wide[i] = ids[i];
            return Request(command, subcommand, wide.Slice(0, ids.Length));
        }

        /// <summary>The Addressed session start <c>98 02</c>, data length 0,
        /// which HallJoy sends once when a session begins and never repeats
        /// or reverses (addressed_analog_backend.cpp:1332). Per HallJoy's
        /// owner-confirmed decision D-085 it turns off the firmware's legacy
        /// last-key diagnostic mode before the <c>94 02</c> polling
        /// (docs/v1.4/DECISIONS.md:1844-1858).</summary>
        public static byte[] SessionStart() => Build(SessionCommand, SessionSubcommand, 0, default);

        private static byte[] Request(byte command, byte subcommand, ReadOnlySpan<ushort> ids)
            => UniqueIds(ids) ? Build(command, subcommand, ids.Length * 2, ids) : null;

        /// <summary>True for 1 to 9 IDs, none 0 or 0xFFFF and none repeated,
        /// the rule both HallJoy builders enforce (ipi_protocol.h:40-46,
        /// aula_hero84he_diagnostic_protocol.cpp:18-28).</summary>
        public static bool UniqueIds(ReadOnlySpan<ushort> ids)
        {
            if (ids.Length == 0 || ids.Length > MaxKeys) return false;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == 0 || ids[i] == 0xFFFF) return false;
                for (int j = 0; j < i; j++)
                    if (ids[i] == ids[j]) return false;
            }
            return true;
        }

        private static byte[] Build(byte command, byte subcommand, int length, ReadOnlySpan<ushort> ids)
        {
            var frame = new byte[Length];
            frame[0] = ReportId;
            frame[1] = command;
            frame[2] = subcommand;
            frame[4] = 0x01;
            frame[6] = (byte)length;
            for (int i = 0; i < ids.Length; i++)
            {
                frame[DataOffset + i * 2] = (byte)(ids[i] >> 8);
                frame[DataOffset + i * 2 + 1] = (byte)ids[i];
            }
            Finish(frame);
            return frame;
        }

        /// <summary>Writes byte 63 so the 8-bit sum of the 64 bytes is 0xFF
        /// (ipi_protocol.h:21-24).</summary>
        public static void Finish(Span<byte> frame)
        {
            uint sum = 0;
            for (int i = 0; i < Length - 1; i++) sum += frame[i];
            frame[Length - 1] = (byte)(0xFF - (sum & 0xFF));
        }

        // ── Validation ──

        /// <summary>True when the 64 bytes sum to 0xFF modulo 256
        /// (addressed_analog_backend.cpp:348-354).</summary>
        public static bool ChecksumValid(ReadOnlySpan<byte> frame)
        {
            if (frame.Length != Length) return false;
            uint sum = 0;
            foreach (byte b in frame) sum += b;
            return (sum & 0xFF) == 0xFF;
        }

        /// <summary>An answer's fixed header: report ID 09, a valid checksum,
        /// the command and subcommand echoed, and bytes 3 to 5
        /// <c>00 01 00</c> (ipi_protocol.h:25-28,
        /// aula_hero84he_diagnostic_protocol.cpp:111-118).</summary>
        public static bool Header(ReadOnlySpan<byte> frame, byte command, byte subcommand)
            => frame.Length == Length && frame[0] == ReportId && ChecksumValid(frame)
               && frame[1] == command && frame[2] == subcommand
               && frame[3] == 0x00 && frame[4] == 0x01 && frame[5] == 0x00;

        /// <summary>The big-endian ID of record <paramref name="index"/>.</summary>
        public static int RecordId(ReadOnlySpan<byte> frame, int index)
            => (frame[DataOffset + index * RecordLength] << 8) | frame[DataOffset + index * RecordLength + 1];

        /// <summary>
        /// The IPI record check, ipi::Records (ipi_protocol.h:50-64): the
        /// header, a data length of six bytes per requested key, and record
        /// IDs that are exactly the requested set in any order, each nonzero
        /// and at most 255. Nothing is stored unless every record passes.
        /// </summary>
        public static bool Records(ReadOnlySpan<byte> frame, byte command, byte subcommand, ReadOnlySpan<byte> ids)
        {
            int count = ids.Length;
            if (count == 0 || count > MaxKeys || !Header(frame, command, subcommand)
                || frame[6] != count * RecordLength)
                return false;
            Span<bool> expected = stackalloc bool[256];
            Span<bool> seen = stackalloc bool[256];
            foreach (byte id in ids)
            {
                if (id == 0 || expected[id]) return false;
                expected[id] = true;
            }
            for (int i = 0; i < count; i++)
            {
                int id = RecordId(frame, i);
                if (id == 0 || id > 255 || !expected[id] || seen[id]) return false;
                seen[id] = true;
            }
            return true;
        }

        /// <summary>
        /// The HERO record check (aula_hero84he_diagnostic_protocol.cpp:61-79):
        /// the header, a data length of six bytes per requested position that
        /// ends before the checksum, and record positions equal to the
        /// requested ones in the same order.
        /// </summary>
        public static bool OrderedRecords(ReadOnlySpan<byte> frame, byte command, byte subcommand,
            ReadOnlySpan<ushort> positions)
        {
            if (!UniqueIds(positions) || !Header(frame, command, subcommand)) return false;
            int length = frame[6];
            if (length != positions.Length * RecordLength || DataOffset + length > Length - 1) return false;
            for (int i = 0; i < positions.Length; i++)
                if (RecordId(frame, i) != positions[i]) return false;
            return true;
        }

        /// <summary>
        /// The six-byte model UUID of an identity answer, big-endian, or 0
        /// when the frame is not one. The IPI parser (ipi::ParseUuid,
        /// ipi_protocol.h:33-37) and the HERO parser (ParseIdentityResponse,
        /// aula_hero84he_diagnostic_protocol.cpp:120-125, read big-endian at
        /// aula_hero84he_backend.cpp:309) agree: header <c>09 82 01 00 01 00</c>,
        /// data length 6, the UUID in bytes 7 to 12.
        /// </summary>
        public static ulong ParseUuid(ReadOnlySpan<byte> frame)
        {
            if (!Header(frame, IdentityCommand, IdentitySubcommand) || frame[6] != RecordLength) return 0;
            ulong value = 0;
            for (int i = DataOffset; i < DataOffset + RecordLength; i++) value = (value << 8) | frame[i];
            return value;
        }

        /// <summary>
        /// Whether an answer passes the Addressed admission probe
        /// (ProbeAddressedResponse, addressed_analog_backend.cpp:616-642):
        /// <c>09 94 02</c>, a 16-bit length (byte 6 low, byte 7 high) of
        /// exactly six bytes per requested key, each record's low ID byte one
        /// of the requested IDs and none repeated, and at least one record
        /// whose value bits 14 to 0 are nonzero. Bytes 3 to 5 are not checked
        /// here, as HallJoy does not check them. The checksum was checked when
        /// the frame was located.
        /// </summary>
        public static bool ProbeAccepts(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> ids)
        {
            if (frame.Length != Length || frame[0] != ReportId || frame[1] != SampleCommand
                || frame[2] != SampleSubcommand)
                return false;
            int length = frame[6] | (frame[7] << 8);
            if (length != ids.Length * RecordLength) return false;
            Span<bool> expected = stackalloc bool[256];
            Span<bool> seen = stackalloc bool[256];
            foreach (byte id in ids) expected[id] = true;
            int plausible = 0;
            for (int off = 0; off + 5 < length; off += RecordLength)
            {
                byte id = frame[8 + off];
                if (!expected[id] || seen[id]) return false;
                seen[id] = true;
                int raw = ((frame[9 + off] & 0x7F) << 8) | frame[10 + off];
                if (raw > 0) plausible++;
            }
            return plausible > 0;
        }

        // ── Windows buffers ──

        /// <summary>
        /// The Windows output buffer for a frame, HallJoy's Transport layout
        /// (addressed_analog_backend.cpp:414-431): max(64, OutputReportByteLength)
        /// bytes, the frame at offset 0 when the collection's output report is
        /// 64 bytes (its 09 is then the report ID), else at offset 1 behind a
        /// zero report ID.
        /// </summary>
        public static byte[] Wire(byte[] frame, int outputLength)
        {
            int size = Math.Max(Length, outputLength);
            var wire = new byte[size];
            Array.Copy(frame, 0, wire, size > Length ? 1 : 0, Length);
            return wire;
        }

        /// <summary>
        /// Where the frame starts in a received Windows buffer: 0 for a
        /// collection that numbers its reports, 1 for a longer unnumbered
        /// report behind its zero report ID, -1 when neither window holds a
        /// frame. HallJoy's FindPayload64 (addressed_analog_backend.cpp:356-366)
        /// takes the first window whose checksum holds, and its callers then
        /// require byte 0 to be 09. When a frame's checksum byte is 0x00 the
        /// window at offset 0 of a 65-byte report also sums to 0xFF, so HallJoy
        /// picks it and drops a valid frame. This takes the first window where
        /// both hold, which accepts every frame HallJoy accepts plus that one.
        /// </summary>
        public static int Locate(ReadOnlySpan<byte> buffer)
        {
            for (int offset = 0; offset <= 1; offset++)
            {
                if (offset + Length > buffer.Length) break;
                var window = buffer.Slice(offset, Length);
                if (window[0] == ReportId && ChecksumValid(window)) return offset;
            }
            return -1;
        }
    }
}
