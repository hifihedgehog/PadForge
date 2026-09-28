using System;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>
    /// The SparkPlayJoy RM 6x21 protocol (issue #468), HallJoy's "WIN 60 HE"
    /// backend: aula_win60he_protocol.h/.cpp and the answer checks of
    /// aula_win60he_client.cpp. Pure functions over protocol bytes.
    ///
    /// <para>Requests use the 5C frame of <see cref="JingTaiFrames"/>: the
    /// same head, length, command, checksum and payload layout, and the same
    /// checksum rule (aula_win60he_protocol.cpp:50-79 against
    /// mg75_pro_protocol.h:37-52). Answers are read the RM way, which differs
    /// from the JingTai reassembler: the number of reports is known from the
    /// request, the first report must match the request before any other is
    /// read, and the frame is then parsed over the whole 64-byte-per-report
    /// stream (aula_win60he_client.cpp:140-207).</para>
    ///
    /// <para>Offsets named "payload" count from frame byte 4. A "block" is one
    /// report's 64 protocol bytes, the Windows report ID byte removed.</para>
    /// </summary>
    public static class AulaRmProtocol
    {
        public const ushort UsagePage = 0xFFA0;   // aula_win60he_protocol.h:14
        public const ushort Usage = 0x0001;       // aula_win60he_protocol.h:15
        public const ushort AulaVendorId = 0x1CA2; // aula_win60he_protocol.h:12

        /// <summary>The Windows length of every report: no report ID is
        /// declared, so byte 0 is 0 and 64 protocol bytes follow
        /// (aula_win60he_protocol.h:321-323).</summary>
        public const int WindowsReportBytes = 65;
        public const int WireReportBytes = 64;
        public const int MaxResponseReports = 3;   // aula_win60he_protocol.h:156
        public const int MaxResponseBytes = 192;   // aula_win60he_protocol.h:157-158

        public const int Rows = 6;
        public const int Columns = 21;
        public const int Positions = 126;
        public const int RowsPerHalf = 3;
        public const int ValuesPerHalf = 63;

        public const byte CommandApi = 0x00;            // aula_win60he_protocol.h:197
        public const byte CommandSync = 0x01;
        public const byte CommandMatrix = 0x12;
        public const byte CommandKeyFunctions = 0x23;
        public const byte CommandDefaultKeys = 0x2B;
        public const byte ReadOperation = 0x00;
        public const byte OrderPrecisionStroke = 0x25;
        public const byte SelectorTravel = 0x02;
        public const byte LayoutFn0 = 0x00;             // aula_win60he_protocol.h:206

        public const int SyncPayloadBytes = 60;         // aula_win60he_protocol.h:173
        public const int PrecisionPayloadBytes = 7;
        public const int DefaultKeyPayloadBytes = 45;
        public const int KeyFunctionRecords = 14;       // aula_win60he_protocol.h:211
        public const int KeyFunctionPayloadBytes = 57;
        public const int TravelPayloadBytes = 128;

        /// <summary>Sync payload offsets (aula_win60he_protocol.h:174-185).</summary>
        public const int SyncSerialLengthOffset = 8;
        public const int SyncSerialOffset = 9;
        public const int SyncAppLengthOffset = 25;
        public const int SyncAppVersionOffset = 26;
        public const int SyncAppVersionBytes = 10;
        public const int SyncBuildLengthOffset = 42;
        public const int SyncBuildDescriptorOffset = 43;
        public const int SyncBuildLabelBytes = 7;
        public const int SyncTrailerOffset = 59;
        public const int SyncDescriptorBytes = 16;

        /// <summary>SparkPlayJoy's base-layer function for the physical Fn
        /// key (aula_win60he_protocol.cpp:517-521).</summary>
        public const int FnFunction = 0xF001;

        /// <summary>The HID product string that tells a WIN 60 HE PRO from a
        /// MAX, compared exactly (aula_win60he_backend.cpp:1570-1571).</summary>
        public const string Win60ProProduct = "WIN 60 HE PRO";
        public const string Win60ProName = "Aula WIN 60 HE PRO";

        // ── Requests (aula_win60he_protocol.cpp:81-120) ─────────────────────

        /// <summary><c>5C 06 01 97 01 02 03 04 FF FF</c>. HallJoy names
        /// only the command byte. The fixed bytes come from the official
        /// SparkPlayJoy package.</summary>
        public static byte[] SyncRequest()
            => JingTaiFrames.Request(CommandSync, stackalloc byte[] { 0x01, 0x02, 0x03, 0x04, 0xFF, 0xFF });

        /// <summary><c>5C 03 00 93 25 FF FF</c>: API command 00, order 25.</summary>
        public static byte[] PrecisionRequest()
            => JingTaiFrames.Request(CommandApi, stackalloc byte[] { OrderPrecisionStroke, 0xFF, 0xFF });

        /// <summary><c>5C 03 2B cs 00 r r+1</c>: two rows of the factory
        /// (default) map.</summary>
        public static byte[] DefaultKeyRequest(int firstRow, int secondRow)
            => JingTaiFrames.Request(CommandDefaultKeys,
                stackalloc byte[] { ReadOperation, (byte)firstRow, (byte)secondRow });

        /// <summary>The Fn0 key-function read of fourteen factory identifiers:
        /// a read operation byte, then records of key, layout, 0, 0. The last
        /// payload byte is always 0, so the checksum is always ED
        /// (aula_win60he_protocol.cpp:99-114).</summary>
        public static byte[] KeyFunctionRequest(ReadOnlySpan<byte> keys, byte layout)
        {
            Span<byte> payload = stackalloc byte[KeyFunctionPayloadBytes];
            payload.Clear();
            payload[0] = ReadOperation;
            for (int i = 0; i < KeyFunctionRecords && i < keys.Length; i++)
            {
                payload[1 + i * 4] = keys[i];
                payload[2 + i * 4] = layout;
            }
            return JingTaiFrames.Request(CommandKeyFunctions, payload);
        }

        /// <summary><c>5C 04 12 A6 02 half FF FF</c>, the byte-identical
        /// JingTai travel read.</summary>
        public static byte[] TravelRequest(int half)
            => JingTaiFrames.Request(CommandMatrix, stackalloc byte[] { SelectorTravel, (byte)half, 0xFF, 0xFF });

        // ── Answers ─────────────────────────────────────────────────────────

        /// <summary>Reports an answer of <paramref name="payloadBytes"/>
        /// spans, ceil((4 + L) / 64) (aula_win60he_protocol.cpp:211-215).</summary>
        public static int ResponseReportCount(int payloadBytes) => (4 + payloadBytes + WireReportBytes - 1) / WireReportBytes;

        /// <summary>The check of the first report before any continuation is
        /// read (FirstReportMatches, aula_win60he_client.cpp:26-62):
        /// <c>5C</c>, the answer command, a length whose report count is the
        /// expected one, status 0, and the request's own echo: the order byte
        /// for the precision read, both row numbers for a default-map read, the
        /// first record's layout for a key-function read, the selector for a
        /// travel read.</summary>
        public static bool FirstReportMatches(ReadOnlySpan<byte> block, byte requestCommand, int expectedReports,
            byte selector, byte index)
        {
            if (block.Length < WireReportBytes || block[0] != JingTaiFrames.Head
                || block[2] != (byte)(requestCommand | 0x80) || ResponseReportCount(block[1]) != expectedReports)
                return false;
            switch (requestCommand)
            {
                case CommandSync:
                    return block[1] == SyncPayloadBytes && block[4] == 0;
                case CommandApi:
                    return block[1] == PrecisionPayloadBytes && block[4] == 0 && block[5] == selector;
                case CommandDefaultKeys:
                    return block[1] == DefaultKeyPayloadBytes && block[4] == 0 && block[5] == index
                        && block[27] == (byte)(index + 1);
                case CommandKeyFunctions:
                    return block[1] == KeyFunctionPayloadBytes && block[4] == 0 && block[6] == selector;
                case CommandMatrix:
                    // HallJoy also accepts the selector-3 status answer here.
                    // No request for it is ever sent, so only travel is kept.
                    return selector == SelectorTravel && block[1] == TravelPayloadBytes
                        && block[4] == 0 && block[5] == SelectorTravel;
                default:
                    return false;
            }
        }

        /// <summary>Parses the frame at the start of a response stream
        /// (ParseResponseFrame, aula_win60he_protocol.cpp:183-209):
        /// <c>5C</c>, the answer command, 4 + length within the stream and
        /// within 192 bytes, and the checksum. The payload is
        /// <c>stream[4 .. 4 + length)</c>.</summary>
        public static bool ParseResponseFrame(ReadOnlySpan<byte> stream, byte requestCommand, out int payloadBytes)
        {
            payloadBytes = 0;
            if (stream.Length < 4 || stream[0] != JingTaiFrames.Head) return false;
            int length = stream[1];
            int frameBytes = 4 + length;
            if (stream[2] != (byte)(requestCommand | 0x80) || frameBytes > stream.Length || frameBytes > MaxResponseBytes)
                return false;
            if (stream[3] != JingTaiFrames.Checksum(stream)) return false;
            payloadBytes = length;
            return true;
        }

        /// <summary>Decodes the 60-byte sync payload (DecodeSyncInfo,
        /// aula_win60he_protocol.cpp:217-242): status 0, the board ID as a
        /// little-endian u32 in bytes 1 to 4, an app descriptor starting
        /// "App V" at byte 26 and a nonzero first build-label byte at 43.</summary>
        public static bool DecodeSync(byte command, ReadOnlySpan<byte> payload, out uint boardId)
        {
            boardId = 0;
            if (command != (CommandSync | 0x80) || payload.Length != SyncPayloadBytes || payload[0] != 0)
                return false;
            boardId = (uint)(payload[1] | (payload[2] << 8) | (payload[3] << 16) | (payload[4] << 24));
            return HasAppPrefix(payload) && payload[SyncBuildDescriptorOffset] != 0;
        }

        private static bool HasAppPrefix(ReadOnlySpan<byte> payload)
            => payload[SyncAppVersionOffset] == (byte)'A' && payload[SyncAppVersionOffset + 1] == (byte)'p'
                && payload[SyncAppVersionOffset + 2] == (byte)'p' && payload[SyncAppVersionOffset + 3] == (byte)' '
                && payload[SyncAppVersionOffset + 4] == (byte)'V';

        /// <summary>The structured descriptors every accepted sync carries
        /// (HasStructured6x21SyncDescriptors, aula_win60he_protocol.cpp:417-444):
        /// status 0, a nonzero board, descriptor lengths of 16 at bytes 8, 25
        /// and 42, <c>FF</c> at byte 59, "App V", and printable ASCII or 0 in
        /// the 10 app-version and 7 build-label bytes, at least one printable
        /// byte in each.</summary>
        public static bool IsStructuredSync(ReadOnlySpan<byte> payload, uint boardId)
        {
            if (payload.Length != SyncPayloadBytes) return false;
            return payload[0] == 0 && boardId != 0
                && payload[SyncSerialLengthOffset] == SyncDescriptorBytes
                && payload[SyncAppLengthOffset] == SyncDescriptorBytes
                && payload[SyncBuildLengthOffset] == SyncDescriptorBytes
                && payload[SyncTrailerOffset] == 0xFF
                && HasAppPrefix(payload)
                && PrintableAscii(payload.Slice(SyncAppVersionOffset, SyncAppVersionBytes))
                && PrintableAscii(payload.Slice(SyncBuildDescriptorOffset, SyncBuildLabelBytes));
        }

        private static bool PrintableAscii(ReadOnlySpan<byte> text)
        {
            bool meaningful = false;
            foreach (byte value in text)
            {
                if (value == 0) continue;
                if (value < 0x20 || value > 0x7E) return false;
                meaningful = true;
            }
            return meaningful;
        }

        /// <summary>A board in the known table: the sync's board must be that
        /// board and the descriptors structured. Bytes 5 to 7 are platform
        /// fields and are not checked, since the physical V75 reports 00 04 00
        /// where AULA reports C0 01 00 (IsExactKnownBoard6x21Firmware,
        /// aula_win60he_protocol.cpp:447-457).</summary>
        public static bool IsKnownBoardFirmware(ReadOnlySpan<byte> payload, uint boardId, uint expectedBoardId)
            => expectedBoardId != 0 && boardId == expectedBoardId && IsStructuredSync(payload, boardId);

        /// <summary>A keyboard outside the table keeps the narrower check:
        /// structured descriptors plus the AULA platform bytes C0 01 00
        /// (IsAula6x21FamilyFirmware, aula_win60he_protocol.cpp:459-468).</summary>
        public static bool IsFamilyFirmware(ReadOnlySpan<byte> payload, uint boardId)
            => IsStructuredSync(payload, boardId) && payload[5] == 0xC0 && payload[6] == 0x01 && payload[7] == 0x00;

        /// <summary>Decodes the precision answer (DecodePrecisionStroke,
        /// aula_win60he_protocol.cpp:244-261): status 0, the order echo 25,
        /// precision, then the minimum and maximum travel as little-endian u16,
        /// all micrometers. False unless 1 &lt;= precision &lt;= 100,
        /// minimum &lt;= maximum and 500 &lt;= maximum &lt;= 10000.</summary>
        public static bool DecodePrecision(byte command, ReadOnlySpan<byte> payload,
            out int precisionUm, out int minimumUm, out int maximumUm)
        {
            precisionUm = minimumUm = maximumUm = 0;
            if (command != (CommandApi | 0x80) || payload.Length != PrecisionPayloadBytes
                || payload[0] != 0 || payload[1] != OrderPrecisionStroke)
                return false;
            precisionUm = payload[2];
            minimumUm = payload[3] | (payload[4] << 8);
            maximumUm = payload[5] | (payload[6] << 8);
            return IsFamilyPrecision(precisionUm, minimumUm, maximumUm);
        }

        /// <summary>IsAula6x21FamilyPrecision (aula_win60he_protocol.cpp:470-476).</summary>
        public static bool IsFamilyPrecision(int precisionUm, int minimumUm, int maximumUm)
            => precisionUm >= 1 && precisionUm <= 100 && minimumUm <= maximumUm
                && maximumUm >= 500 && maximumUm <= 10000;

        /// <summary>Decodes a default-map answer into <paramref name="map"/>
        /// (DecodeDefaultKeyRows, aula_win60he_protocol.cpp:263-285): status
        /// 0, the first row number, its 21 identifiers, the second row number
        /// and its 21 identifiers.</summary>
        public static bool DecodeDefaultRows(byte command, ReadOnlySpan<byte> payload, int firstRow, int secondRow,
            Span<byte> map)
        {
            if (command != (CommandDefaultKeys | 0x80) || payload.Length != DefaultKeyPayloadBytes || payload[0] != 0
                || firstRow < 0 || firstRow >= Rows || secondRow < 0 || secondRow >= Rows || firstRow == secondRow
                || map.Length < Positions)
                return false;
            if (payload[1] != firstRow || payload[23] != secondRow) return false;
            for (int c = 0; c < Columns; c++)
            {
                map[firstRow * Columns + c] = payload[2 + c];
                map[secondRow * Columns + c] = payload[24 + c];
            }
            return true;
        }

        /// <summary>A default map any family keyboard must show
        /// (IsAula6x21FamilyDefaultMap, aula_win60he_protocol.cpp:478-500): no
        /// identifier twice, 2 to 126 occupied positions, and at least one
        /// keyboard usage 04 to E7 among them.</summary>
        public static bool IsFamilyDefaultMap(ReadOnlySpan<byte> map)
        {
            Span<bool> seen = stackalloc bool[256];
            seen.Clear();
            int physical = 0, publishable = 0;
            foreach (byte key in map)
            {
                if (key == 0) continue;
                if (seen[key]) return false;
                seen[key] = true;
                physical++;
                if (IsPublishableUsage(key)) publishable++;
            }
            return physical >= 2 && physical <= Positions && publishable >= 1;
        }

        /// <summary>A keyboard-page usage HallJoy publishes, 04 to E7
        /// (aula_win60he_protocol.cpp:502-505).</summary>
        public static bool IsPublishableUsage(int usage) => usage >= 0x04 && usage <= 0xE7;

        /// <summary>A base-layer function as a key code: a usage 04 to E7 as
        /// itself, F001 as <see cref="AnalogKeyCodes.Fn"/>, anything else
        /// (disabled, macros, internal functions) as 0
        /// (PublishedKeyCodeForFunction, aula_win60he_protocol.cpp:512-523).</summary>
        public static int KeyCodeForFunction(int function)
        {
            if (function <= 0xFF && IsPublishableUsage(function)) return function;
            return function == FnFunction ? AnalogKeyCodes.Fn : 0;
        }

        /// <summary>A factory identifier as a key code: 01 is the physical Fn,
        /// the rest as <see cref="KeyCodeForFunction"/>
        /// (sparkplayjoy_layout::Factory, sparkplayjoy_layout.h:23-30).</summary>
        public static int FactoryKeyCode(byte identifier)
            => identifier == 1 ? AnalogKeyCodes.Fn : KeyCodeForFunction(identifier);

        /// <summary>Decodes a key-function answer (DecodeKeyFunctionReadResponse,
        /// aula_win60he_protocol.cpp:287-323): status 0, then fourteen records
        /// of key, layout and a little-endian function. Each key must echo the
        /// request and be nonzero, each layout must be the requested one, and
        /// every repeat of the padding key must report the same function.</summary>
        public static bool DecodeKeyFunctions(byte command, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> query,
            byte layout, Span<ushort> functions)
        {
            if (command != (CommandKeyFunctions | 0x80) || payload.Length != KeyFunctionPayloadBytes || payload[0] != 0
                || query.Length < KeyFunctionRecords || functions.Length < KeyFunctionRecords)
                return false;
            for (int i = 0; i < KeyFunctionRecords; i++)
            {
                int offset = 1 + i * 4;
                byte key = payload[offset];
                if (key != query[i] || payload[offset + 1] != layout || key == 0) return false;
                ushort value = (ushort)(payload[offset + 2] | (payload[offset + 3] << 8));
                for (int previous = 0; previous < i; previous++)
                    if (query[previous] == key && functions[previous] != value) return false;
                functions[i] = value;
            }
            return true;
        }

        /// <summary>Files each record's function under the one position whose
        /// factory identifier is the record's key (ApplyKeyFunctionBatch and
        /// FindUniqueFactoryKey, aula_win60he_protocol.cpp:21-47, 558-575).
        /// False when a key is absent from the map or sits on it twice.</summary>
        public static bool ApplyKeyFunctions(ReadOnlySpan<byte> defaultMap, ReadOnlySpan<byte> query,
            ReadOnlySpan<ushort> values, Span<ushort> functionMap)
        {
            for (int i = 0; i < KeyFunctionRecords; i++)
            {
                int position = FindUniqueFactoryKey(defaultMap, query[i]);
                if (position < 0) return false;
                functionMap[position] = values[i];
            }
            return true;
        }

        private static int FindUniqueFactoryKey(ReadOnlySpan<byte> map, byte key)
        {
            if (key == 0) return -1;
            int found = -1;
            for (int i = 0; i < Positions && i < map.Length; i++)
            {
                if (map[i] != key) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }

        /// <summary>The active key map: each occupied position's function as a
        /// key code (BuildPublishableActiveKeyMap,
        /// aula_win60he_protocol.cpp:577-595).</summary>
        public static void BuildActiveKeyMap(ReadOnlySpan<byte> defaultMap, ReadOnlySpan<ushort> functionMap,
            Span<int> keyMap)
        {
            for (int i = 0; i < Positions; i++)
                keyMap[i] = defaultMap[i] == 0 ? 0 : KeyCodeForFunction(functionMap[i]);
        }

        /// <summary>The distinct key codes a map publishes
        /// (CountMappedKeyCodes, aula_win60he_protocol.cpp:535-547).</summary>
        public static int CountMappedKeyCodes(ReadOnlySpan<int> keyMap)
        {
            Span<bool> seen = stackalloc bool[AnalogKeyInputState.CodeCount];
            seen.Clear();
            int count = 0;
            foreach (int code in keyMap)
            {
                if (code <= 0 || code >= AnalogKeyInputState.CodeCount || seen[code]) continue;
                seen[code] = true;
                count++;
            }
            return count;
        }

        /// <summary>Decodes one travel half (DecodeTravelHalf,
        /// aula_win60he_protocol.cpp:325-344): status 0, the selector echo 02,
        /// then 63 little-endian values, row by row, in micrometers. The half
        /// number is not echoed.</summary>
        public static bool DecodeTravelHalf(byte command, ReadOnlySpan<byte> payload, Span<ushort> values)
        {
            if (command != (CommandMatrix | 0x80) || payload.Length != TravelPayloadBytes || payload[0] != 0
                || payload[1] != SelectorTravel || values.Length < ValuesPerHalf)
                return false;
            for (int k = 0; k < ValuesPerHalf; k++)
                values[k] = (ushort)(payload[2 + 2 * k] | (payload[3 + 2 * k] << 8));
            return true;
        }

        /// <summary>Every occupied position of the half must read at most the
        /// maximum travel plus max(8 x precision, 200) (TravelValuesPlausible,
        /// aula_win60he_client.cpp:614-638). Empty positions are not checked.</summary>
        public static bool TravelPlausible(ReadOnlySpan<ushort> half, ReadOnlySpan<byte> defaultMap, int firstRow,
            int precisionUm, int minimumUm, int maximumUm)
        {
            if (!IsFamilyPrecision(precisionUm, minimumUm, maximumUm) || firstRow + RowsPerHalf > Rows) return false;
            int tolerated = maximumUm + Math.Max(precisionUm * 8, 200);
            for (int k = 0; k < ValuesPerHalf; k++)
            {
                if (defaultMap[firstRow * Columns + k] == 0) continue;
                if (half[k] > tolerated) return false;
            }
            return true;
        }

        /// <summary>Travel in micrometers as HallJoy's 0 to 1000 depth: 0 for
        /// no travel or no maximum, 1000 at or past the maximum, else
        /// (travel * 1000 + max / 2) / max in integer math
        /// (NormalizeTravelToMilli, aula_win60he_protocol.cpp:597-609). The
        /// minimum-travel field is not used.</summary>
        public static int NormalizeTravel(int travelUm, int maximumUm)
        {
            if (travelUm <= 0 || maximumUm <= 0) return 0;
            if (travelUm >= maximumUm) return 1000;
            return (int)Math.Min(1000L, ((long)travelUm * 1000 + maximumUm / 2) / maximumUm);
        }

        // ── Discovery (aula_win60he_protocol.h:64-151, aula_win60he_backend.cpp:682-746)

        private static char FoldAscii(char c) => c >= 'A' && c <= 'Z' ? (char)(c - 'A' + 'a') : c;

        private static bool TokenAt(string path, int offset, string token)
        {
            if (offset > path.Length || token.Length > path.Length - offset) return false;
            for (int i = 0; i < token.Length; i++)
                if (FoldAscii(path[offset + i]) != token[i]) return false;
            return true;
        }

        private static int HexNibble(char c)
        {
            c = FoldAscii(c);
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return -1;
        }

        private static bool ReadHex4(string path, int offset, out ushort value)
        {
            value = 0;
            if (offset > path.Length || 4 > path.Length - offset) return false;
            int v = 0;
            for (int i = 0; i < 4; i++)
            {
                int nibble = HexNibble(path[offset + i]);
                if (nibble < 0) return false;
                v = (v << 4) | nibble;
            }
            value = (ushort)v;
            return true;
        }

        /// <summary>The first "vid_XXXX&amp;pid_YYYY" in a device path, four
        /// hex digits each and contiguous, compared ASCII-case-insensitively
        /// (TryReadUsbIdentityFromPath, aula_win60he_protocol.h:113-142).</summary>
        public static bool TryReadUsbIdentityFromPath(string path, out ushort vendorId, out ushort productId)
        {
            vendorId = productId = 0;
            if (string.IsNullOrEmpty(path) || path.Length < 17) return false;
            for (int offset = 0; offset <= path.Length - 17; offset++)
            {
                if (!TokenAt(path, offset, "vid_") || !TokenAt(path, offset + 8, "&pid_")) continue;
                if (!ReadHex4(path, offset + 4, out ushort vid) || !ReadHex4(path, offset + 13, out ushort pid)) continue;
                vendorId = vid;
                productId = pid;
                return true;
            }
            return false;
        }

        /// <summary>True when the path's USB identity is in the known table
        /// (PathContainsKnownUsbIdentity, aula_win60he_protocol.h:144-151).</summary>
        public static bool PathContainsKnownUsbIdentity(string path)
            => TryReadUsbIdentityFromPath(path, out ushort vid, out ushort pid)
               && JingTaiRoutes.FindAulaBoard(vid, pid) != null;

        /// <summary>True when the path holds "vid_1ca2" in any case
        /// (PathContainsAulaVendor, aula_win60he_backend.cpp:736-746).</summary>
        public static bool PathContainsAulaVendor(string path)
            => !string.IsNullOrEmpty(path) && LowerAscii(path).Contains("vid_1ca2", StringComparison.Ordinal);

        /// <summary>True when a string holds "aula", "sparkplayjoy" or
        /// "spark play joy" in any case (ContainsFamilyToken,
        /// aula_win60he_backend.cpp:682-692).</summary>
        public static bool ContainsFamilyToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string lower = LowerAscii(value);
            return lower.Contains("aula", StringComparison.Ordinal)
                || lower.Contains("sparkplayjoy", StringComparison.Ordinal)
                || lower.Contains("spark play joy", StringComparison.Ordinal);
        }

        /// <summary>HallJoy's towlower in the C locale, which folds ASCII
        /// letters only.</summary>
        private static string LowerAscii(string value)
        {
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++) chars[i] = FoldAscii(chars[i]);
            return new string(chars);
        }

        /// <summary>The HID fingerprint (IsAulaFamilyIdentity,
        /// aula_win60he_backend.cpp:694-708): brand evidence, meaning a known
        /// VID and PID, AULA's VID 1CA2, or a family token in the HID
        /// manufacturer or product string, on a collection of usage page FFA0,
        /// usage 1, with 65-byte input and output reports.</summary>
        public static bool IsFamilyIdentity(ushort vendorId, ushort productId, string manufacturer, string product,
            ushort usagePage, ushort usage, int inputLength, int outputLength)
        {
            bool brand = JingTaiRoutes.FindAulaBoard(vendorId, productId) != null
                || vendorId == AulaVendorId
                || ContainsFamilyToken(manufacturer)
                || ContainsFamilyToken(product);
            return brand && usagePage == UsagePage && usage == Usage
                && inputLength == WindowsReportBytes && outputLength == WindowsReportBytes;
        }
    }
}
