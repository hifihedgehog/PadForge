using System;
using System.Threading;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>One travel-buffer entry (hex80_protocol.h:26-34).</summary>
    public readonly struct Hex80TravelEntry
    {
        public Hex80TravelEntry(int slot, int hid, int adc, int travel, int status, int milli)
        {
            Slot = slot;
            Hid = hid;
            Adc = adc;
            Travel = travel;
            Status = status;
            Milli = milli;
        }

        public int Slot { get; }
        public int Hid { get; }
        /// <summary>Unused for output. Its meaning is undocumented
        /// (docs/hex80-reference/PROTOCOL.md:244).</summary>
        public int Adc { get; }
        public int Travel { get; }
        /// <summary>Unused for output, like <see cref="Adc"/>.</summary>
        public int Status { get; }
        public int Milli { get; }
    }

    /// <summary>
    /// ATK x QK Hex80, HallJoy's 0x96 matrix polling (hex80_protocol.h,
    /// hex80_protocol.cpp, hex80_backend.cpp). The pure pieces: identification,
    /// the command payloads, reply matching, decoding and normalization. The
    /// session is <see cref="AtkHex80Session"/>.
    ///
    /// <para>A command is a 128-byte payload, byte 0 the operation (02 GET,
    /// 03 SET), byte 1 the vendor group 0x96 and byte 2 the subcommand, sent
    /// after report ID 0 and cut to the output report length. GET 0x24 reads
    /// the travel scale, GET 0x1C reads up to four slots of the 104-slot
    /// travel buffer, and SET 0x19 leaves calibration mode.</para>
    ///
    /// <para>02 96 1C is VIA's id_get_keyboard_value with the vendor's custom
    /// ID 0x96 and its adcTripCompStatusBuffer, as the vendor driver names
    /// them (AnalogKeys docs/protocol.md:142-148). Soup's getActiveKeysMadlions
    /// (Soup/soup/AnalogueKeyboard.cpp:1140-1145, 1181-1200) and AnalogSense.js's
    /// AsProviderMadlions (JavaScript-SDK/AnalogSense.js:804-811, 821, 840)
    /// send the same request to the MADLIONS VIA boards and read each travel
    /// at the same payload offset, 10 + 5 per slot.</para>
    /// </summary>
    public static class AtkHex80Protocol
    {
        public const ushort VendorId = 0x373B;
        public const ushort UsagePage = 0xFF60;
        public const ushort Usage = 0x0061;
        public const int PayloadBytes = 128;
        public const int MinPayloadBytes = 32;
        public const int TotalSlots = 104;
        public const int ChunkSize = 4;
        public const int RawDeadzone = 8;
        public const int DefaultTravelMax = 3300;
        public const byte GetValue = 0x02;
        public const byte SetValue = 0x03;
        public const byte CustomCommand = 0x96;
        public const byte CalibrationFinish = 0x19;
        public const byte TravelBuffer = 0x1C;
        public const byte TravelInfo = 0x24;

        /// <summary>The name HallJoy shows for all three product IDs
        /// (keyboard_subpages.cpp:302, hex80_backend.cpp:974).</summary>
        public const string ModelName = "ATK x QK Hex80";

        private static readonly Lazy<int[]> _productIds =
            new(() => AnalogKeyboardData.Table(MadlionsRoutes.DataFile, "hex80ProductIds"));

        private static readonly Lazy<int[]> _slotToHid =
            new(() => AnalogKeyboardData.Table(MadlionsRoutes.DataFile, "hex80Keys"));

        /// <summary>1176, 1177 and 1250 (hex80_protocol.h:10).</summary>
        public static int[] ProductIds() => (int[])_productIds.Value.Clone();

        /// <summary>Slot to key code, row * 17 + column plus the two padding
        /// slots, Fn at slot 96 as 0x409 and the Mute key unmapped
        /// (kSlotToHid, hex80_protocol.h:36-54).</summary>
        public static int HidOfSlot(int slot) => slot >= 0 && slot < TotalSlots ? _slotToHid.Value[slot] : 0;

        /// <summary>The 87 mapped keys in slot order.</summary>
        public static int[] KeyOrder() => AnalogKeyboardData.KeysOf(_slotToHid.Value);

        public static bool IsKnownProductId(ushort productId)
            => Array.IndexOf(_productIds.Value, (int)productId) >= 0;

        /// <summary>HallJoy's enumerator (hex80_backend.cpp:285-303): VID
        /// 373B, one of the three PIDs, usage FF60:0061, and input and output
        /// reports of at least 33 bytes. Strings, interface number and
        /// bcdDevice are not checked.</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == VendorId
               && IsKnownProductId(info.ProductId)
               && info.UsagePage == UsagePage
               && info.Usage == Usage
               && info.InputReportLength >= MinPayloadBytes + 1
               && info.OutputReportLength >= MinPayloadBytes + 1;

        private static byte[] BuildBase(byte operation, byte subcommand)
        {
            var payload = new byte[PayloadBytes];
            payload[0] = operation;
            payload[1] = CustomCommand;
            payload[2] = subcommand;
            return payload;
        }

        /// <summary>03 96 19 (hex80_protocol.cpp:20-23).</summary>
        public static byte[] BuildCalibrationFinishPayload() => BuildBase(SetValue, CalibrationFinish);

        /// <summary>02 96 24 (hex80_protocol.cpp:25-28).</summary>
        public static byte[] BuildTravelInfoPayload() => BuildBase(GetValue, TravelInfo);

        /// <summary>02 96 1C with the big-endian offset at bytes 5 and 6 and
        /// the count at byte 7 (hex80_protocol.cpp:30-38).</summary>
        public static byte[] BuildTravelBufferPayload(int offset, int size)
        {
            var payload = BuildBase(GetValue, TravelBuffer);
            payload[5] = (byte)((offset >> 8) & 0xFF);
            payload[6] = (byte)(offset & 0xFF);
            payload[7] = (byte)size;
            return payload;
        }

        /// <summary>The output report: report ID 0, then the payload cut to
        /// the report length. Null when the report is shorter than 33 bytes or
        /// a nonzero payload byte would be cut (EncodeOutputReport,
        /// hex80_protocol.cpp:40-50).</summary>
        public static byte[] EncodeOutputReport(byte[] payload, int reportBytes)
        {
            if (payload == null || reportBytes < MinPayloadBytes + 1) return null;
            int count = Math.Min(payload.Length, reportBytes - 1);
            for (int i = count; i < payload.Length; i++)
                if (payload[i] != 0) return null;
            var report = new byte[reportBytes];
            Array.Copy(payload, 0, report, 1, count);
            return report;
        }

        /// <summary>Travel onto 0 to 1000 with a dead zone of 8 counts,
        /// rounded (NormalizeTravelToMilli, hex80_protocol.cpp:52-64).</summary>
        public static int NormalizeTravelToMilli(int travel, int travelMax)
        {
            if (travel <= RawDeadzone || travelMax <= RawDeadzone) return 0;
            if (travel >= travelMax) return 1000;
            int numerator = (travel - RawDeadzone) * 1000;
            int denominator = travelMax - RawDeadzone;
            return Math.Min((numerator + denominator / 2) / denominator, 1000);
        }

        /// <summary>Where the payload starts, with or without the report ID
        /// byte, or -1 (FindPayload, hex80_protocol.cpp:66-86).</summary>
        public static int FindPayload(ReadOnlySpan<byte> data, byte operation, byte subcommand, out int payloadBytes)
        {
            payloadBytes = 0;
            if (data.Length < 3) return -1;
            if (data[0] == operation && data[1] == CustomCommand && data[2] == subcommand)
            {
                payloadBytes = data.Length;
                return 0;
            }
            if (data.Length >= 4 && data[0] == 0 && data[1] == operation
                && data[2] == CustomCommand && data[3] == subcommand)
            {
                payloadBytes = data.Length - 1;
                return 1;
            }
            return -1;
        }

        /// <summary>Whether a reply answers the outstanding GET: a travel info
        /// reply of at least 5 bytes, or a chunk that echoes the offset and
        /// count and is long enough for its entries (MatchesRequest,
        /// hex80_protocol.cpp:88-100). Late replies to earlier requests fail
        /// this and are skipped.</summary>
        public static bool MatchesRequest(byte[] request, ReadOnlySpan<byte> data)
        {
            if (request[0] != GetValue || request[1] != CustomCommand
                || (request[2] != TravelInfo && request[2] != TravelBuffer))
                return false;
            int at = FindPayload(data, request[0], request[2], out int length);
            if (at < 0) return false;
            var reply = data.Slice(at);
            if (request[2] == TravelInfo) return length >= 5;
            return length >= 8 + request[7] * 5
                && request[7] > 0 && request[7] <= ChunkSize
                && reply[5] == request[5] && reply[6] == request[6] && reply[7] == request[7];
        }

        /// <summary>The travel scale, a big-endian value in payload bytes 3
        /// and 4, accepted from 256 to 20000 (DecodeTravelInfo,
        /// hex80_protocol.cpp:102-116). There is no fallback value.</summary>
        public static bool DecodeTravelInfo(ReadOnlySpan<byte> data, out int travelMax)
        {
            travelMax = 0;
            int at = FindPayload(data, GetValue, TravelInfo, out int payloadBytes);
            if (at < 0 || payloadBytes < 5) return false;
            var payload = data.Slice(at);
            int value = (payload[3] << 8) | payload[4];
            if (value < 256 || value > 20000) return false;
            travelMax = value;
            return true;
        }

        /// <summary>
        /// One travel-buffer chunk (DecodeTravelChunk, hex80_protocol.cpp:118-167):
        /// the echoed offset and count must match the request, each entry is a
        /// big-endian ADC, a big-endian travel and a status byte, and one
        /// travel above twice max(travelMax, 3300) rejects the whole chunk.
        /// </summary>
        public static bool DecodeTravelChunk(ReadOnlySpan<byte> data, int expectedOffset, int expectedSize,
            int travelMax, Span<Hex80TravelEntry> entries, out int count)
        {
            count = 0;
            if (expectedSize == 0 || expectedSize > ChunkSize || expectedOffset < 0
                || expectedOffset >= TotalSlots || expectedOffset + expectedSize > TotalSlots)
                return false;
            int at = FindPayload(data, GetValue, TravelBuffer, out int payloadBytes);
            if (at < 0 || payloadBytes < 8) return false;
            var payload = data.Slice(at);

            int returnedOffset = (payload[5] << 8) | payload[6];
            int returnedSize = payload[7];
            if (returnedOffset != expectedOffset || returnedSize != expectedSize) return false;
            if (payloadBytes < 8 + returnedSize * 5) return false;
            if (entries.Length < returnedSize) return false;

            int plausibleLimit = Math.Min(0xFFFF, Math.Max(travelMax, DefaultTravelMax) * 2);
            Span<Hex80TravelEntry> staged = stackalloc Hex80TravelEntry[ChunkSize];
            int cursor = 8;
            for (int index = 0; index < returnedSize; index++, cursor += 5)
            {
                int slot = returnedOffset + index;
                int adc = (payload[cursor] << 8) | payload[cursor + 1];
                int travel = (payload[cursor + 2] << 8) | payload[cursor + 3];
                if (travel > plausibleLimit) return false;
                staged[index] = new Hex80TravelEntry(slot, HidOfSlot(slot), adc, travel, payload[cursor + 4],
                    NormalizeTravelToMilli(travel, travelMax));
            }
            staged.Slice(0, returnedSize).CopyTo(entries);
            count = returnedSize;
            return true;
        }
    }

    /// <summary>
    /// One Hex80 session, HallJoy's RunSession (hex80_backend.cpp:490-592).
    /// <see cref="Start"/> proves the collection with the two GETs, the
    /// travel scale within 120 ms and chunk 0 of four slots within 120 ms,
    /// then sends 03 96 19 once without awaiting a reply. Each pass requests
    /// one chunk of four slots, offsets 0 to 100, and waits up to 30 ms for
    /// its reply, so a matrix cycle is 26 passes, with a thread yield after
    /// each cycle and no fixed period. A key reads 0 once its last sample is
    /// older than 500 ms. Eight failed chunks in a row end the session.
    /// HallJoy sends nothing at shutdown, so neither does <see cref="Stop"/>.
    /// </summary>
    public sealed class AtkHex80Session : AnalogKeyboardSession
    {
        /// <summary>hex80_backend.cpp:38.</summary>
        public const int ProbeReadTimeoutMs = 120;
        /// <summary>hex80_backend.cpp:39.</summary>
        public const int PollReadTimeoutMs = 30;
        /// <summary>hex80_backend.cpp:42.</summary>
        public const int MaxConsecutiveFailures = 8;
        /// <summary>hex80_protocol.h:57.</summary>
        public const int FreshMs = 500;

        private const int HidCount = 0x410;

        private readonly Func<long> _clock;
        private readonly int[] _keys = AtkHex80Protocol.KeyOrder();
        private readonly int[] _milli = new int[HidCount];
        private readonly int[] _travel = new int[HidCount];
        private readonly long[] _sampleMs = new long[HidCount];
        private readonly Hex80TravelEntry[] _entries = new Hex80TravelEntry[AtkHex80Protocol.ChunkSize];
        private readonly AnalogKeyInputState _scratch = new();
        private int _travelMax;
        private int _offset;
        private int _consecutiveFailures;

        public AtkHex80Session(Func<long> clock = null)
        {
            _clock = clock ?? (() => Environment.TickCount64);
        }

        public override string ModelName => AtkHex80Protocol.ModelName;

        public override int[] KeyOrder => (int[])_keys.Clone();

        /// <summary>The travel scale the keyboard reported.</summary>
        public int TravelMax => _travelMax;

        /// <summary>The slot offset the next pass requests.</summary>
        public int NextOffset => _offset;

        /// <summary>GET travel info, GET chunk 0 of four, then SET 0x19
        /// (hex80_backend.cpp:496-519).</summary>
        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (!Request(io, AtkHex80Protocol.BuildTravelInfoPayload(), ProbeReadTimeoutMs, out int n)
                || !AtkHex80Protocol.DecodeTravelInfo(Buffer.AsSpan(0, n), out _travelMax))
                return false;
            if (!Request(io, AtkHex80Protocol.BuildTravelBufferPayload(0, AtkHex80Protocol.ChunkSize), ProbeReadTimeoutMs, out n)
                || !AtkHex80Protocol.DecodeTravelChunk(Buffer.AsSpan(0, n), 0, AtkHex80Protocol.ChunkSize, _travelMax,
                    _entries, out int count)
                || count != AtkHex80Protocol.ChunkSize)
                return false;
            // 03 96 19 is an idempotent exit from calibration mode, sent only
            // after both GET proofs passed (hex80_backend.cpp:515-519).
            var finish = AtkHex80Protocol.EncodeOutputReport(AtkHex80Protocol.BuildCalibrationFinishPayload(), io.OutputLength);
            return finish != null && io.Send(finish);
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            int offset = _offset;
            int size = Math.Min(AtkHex80Protocol.ChunkSize, AtkHex80Protocol.TotalSlots - offset);
            bool received = Request(io, AtkHex80Protocol.BuildTravelBufferPayload(offset, size), PollReadTimeoutMs, out int n);
            long now = _clock();
            int count = 0;
            bool valid = received && AtkHex80Protocol.DecodeTravelChunk(Buffer.AsSpan(0, n), offset, size, _travelMax,
                _entries, out count);

            _offset = offset + AtkHex80Protocol.ChunkSize < AtkHex80Protocol.TotalSlots
                ? offset + AtkHex80Protocol.ChunkSize
                : 0;

            if (valid)
            {
                _consecutiveFailures = 0;
                for (int i = 0; i < count; i++)
                {
                    var entry = _entries[i];
                    if (entry.Hid == 0 || entry.Hid >= HidCount) continue;
                    _travel[entry.Hid] = entry.Travel;
                    _milli[entry.Hid] = entry.Milli;
                    _sampleMs[entry.Hid] = now;
                }
            }
            else if (++_consecutiveFailures >= MaxConsecutiveFailures)
            {
                return AnalogPollResult.Failed;
            }

            // SwitchToThread after each full matrix cycle (hex80_backend.cpp:589).
            if (_offset == 0) Thread.Yield();

            bool changed = Publish(output, now);
            return valid || changed ? AnalogPollResult.Ok : AnalogPollResult.Idle;
        }

        /// <summary>Session::Request (hex80_backend.cpp:383-417): write one
        /// request, then read until the deadline, skipping reports that are
        /// not the reply to it. The reply stays in <see cref="AnalogKeyboardSession.Buffer"/>.
        /// A read that returns nothing has waited out the deadline.</summary>
        private bool Request(IAnalogKeyboardTransport io, byte[] payload, int timeoutMs, out int length)
        {
            length = 0;
            var report = AtkHex80Protocol.EncodeOutputReport(payload, io.OutputLength);
            if (report == null || !io.Send(report)) return false;
            long deadline = _clock() + timeoutMs;
            while (true)
            {
                long remaining = deadline - _clock();
                if (remaining <= 0) return false;
                int got = io.Receive(Buffer, (int)remaining);
                if (got <= 0) return false;
                var data = Buffer.AsSpan(0, got);
                if (AtkHex80Protocol.FindPayload(data, payload[0], payload[2], out _) < 0) continue;
                if (!AtkHex80Protocol.MatchesRequest(payload, data)) continue;
                length = got;
                return true;
            }
        }

        /// <summary>Every mapped key with a sample no older than 500 ms
        /// (Hex80_GetMilli, hex80_backend.cpp:882-889). A stale key reads 0
        /// instead of falling back to a full-depth press. True when the output
        /// changed.</summary>
        private bool Publish(AnalogKeyInputState output, long now)
        {
            _scratch.ResetForReuse();
            foreach (int code in _keys)
            {
                long stamp = _sampleMs[code];
                bool fresh = stamp != 0 && now >= stamp && now - stamp <= FreshMs;
                if (fresh && _milli[code] > 0) _scratch.Set(code, _milli[code] / 1000f);
            }
            if (_scratch.SameAs(output)) return false;
            _scratch.CopyInto(output);
            return true;
        }
    }
}
