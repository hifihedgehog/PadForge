using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>How a MAD 68 Pro R command leaves the host
    /// (mad68pr_backend.cpp:101-106, 892-946).</summary>
    public enum Mad68SendTransport
    {
        /// <summary>WriteFile of max(9, output report length) bytes, report ID 0 first.</summary>
        InterruptCaps,
        /// <summary>WriteFile of the bare 64-byte payload with no report ID byte.</summary>
        InterruptRaw64,
        /// <summary>HidD_SetOutputReport of the same buffer as <see cref="InterruptCaps"/>.</summary>
        ControlCaps,
    }

    /// <summary>One entry of HallJoy's activation strategy list
    /// (mad68pr_backend.cpp:108-134).</summary>
    public sealed class Mad68Strategy
    {
        public Mad68Strategy(string name, Mad68SendTransport transport, byte framing, byte xorKey,
            bool requireAck, int interCommandDelayMs)
        {
            Name = name;
            Transport = transport;
            Framing = framing;
            XorKey = xorKey;
            RequireAck = requireAck;
            InterCommandDelayMs = interCommandDelayMs;
        }

        public string Name { get; }
        public Mad68SendTransport Transport { get; }
        public byte Framing { get; }
        public byte XorKey { get; }
        public bool RequireAck { get; }
        public int InterCommandDelayMs { get; }
    }

    /// <summary>What a control reply says (mad68pr_protocol.h:121-127).</summary>
    public enum Mad68ControlKind
    {
        NotControl,
        Valid,
        ChecksumError,
        Invalid,
    }

    /// <summary>A decoded control reply (mad68pr_protocol.h:129-138).</summary>
    public readonly struct Mad68ControlResponse
    {
        public Mad68ControlResponse(Mad68ControlKind kind, byte header, byte opcode, byte xorKey,
            byte length, byte checksum, byte expectedChecksum)
        {
            Kind = kind;
            Header = header;
            Opcode = opcode;
            XorKey = xorKey;
            Length = length;
            Checksum = checksum;
            ExpectedChecksum = expectedChecksum;
        }

        public Mad68ControlKind Kind { get; }
        public byte Header { get; }
        public byte Opcode { get; }
        public byte XorKey { get; }
        public byte Length { get; }
        public byte Checksum { get; }
        public byte ExpectedChecksum { get; }
    }

    /// <summary>One decoded A0 report (mad68pr_protocol.h:108-119).</summary>
    public readonly struct Mad68KeySample
    {
        public Mad68KeySample(int keyIndex, int hid, int raw, int milli, int threshold, int baseline, int state)
        {
            KeyIndex = keyIndex;
            Hid = hid;
            Raw = raw;
            Milli = milli;
            Threshold = threshold;
            Baseline = baseline;
            State = state;
        }

        /// <summary>HallJoy's key index, the descriptor's rank in scanner
        /// order (kKeyDescriptors, mad68pr_protocol.h:37-106).</summary>
        public int KeyIndex { get; }
        /// <summary>The key's HID usage, 0 for Fn.</summary>
        public int Hid { get; }
        public int Raw { get; }
        public int Milli { get; }
        public int Threshold { get; }
        public int Baseline { get; }
        public int State { get; }
    }

    /// <summary>
    /// MADLIONS MAD 68 Pro R, HallJoy's native A0 stream (mad68pr_protocol.h,
    /// mad68pr_protocol.cpp, mad68pr_backend.cpp). Pure pieces: identification,
    /// frame builders, reply decoders and the key tables. The session that
    /// drives them is <see cref="Mad68ProRSession"/>.
    ///
    /// <para>The vendor collection (interface 1, 64-byte input and output,
    /// unnumbered) takes a 64-byte command: byte 0 the framing (0x55, or 0x5F
    /// raw), byte 1 the opcode, byte 2 an XOR key and byte 3 a checksum. Only
    /// A8 (arm the analog stream) and A9 (restore ordinary input) ever leave
    /// HallJoy. The keyboard answers AA with the opcode and a checksum, AB on
    /// a checksum error, and pushes one A0 report per changed key: a 3-byte
    /// key descriptor, the live value 0 to 1600, the key's actuation
    /// threshold and its baseline.</para>
    ///
    /// <para>AnalogKeys, written against the vendor driver's Nano 68 Pro
    /// (docs/protocol.md at abaf1e4), agrees on the channel and the framing:
    /// interface 1, usage 0001:0000, 65-byte reports on Windows (lines 17-27),
    /// 55 commands, AA replies and A0 key events with the rest ADC in bytes 18
    /// and 19 (lines 35-45, 92-100). The vendor driver names A8 and A9
    /// StartCalibration and EndCalibration and sends A9 when it connects
    /// (lines 60, 175). That board's firmware numbers its keys differently and
    /// enables its stream through a settings write, so only HallJoy's audited
    /// field map and A8/A9 activation are used here.</para>
    /// </summary>
    public static class Mad68ProRProtocol
    {
        public const ushort VendorId = 0x373B;
        public const ushort ProductId = 0x1109;
        public const ushort AuditedBcdDevice = 0x0102;
        public const int PayloadBytes = 64;
        public const int PhysicalKeyCount = 68;
        public const int PublishedKeyCount = 67;
        public const int AnalogFullScale = 1600;
        public const int SteadyProofMinDelta = 64;
        public const byte NormalRequestHeader = 0x55;
        public const byte RawRequestHeader = 0x5F;
        public const byte NormalResponseHeader = 0xAA;
        public const byte ChecksumErrorHeader = 0xAB;
        public const byte StreamHeader = 0xA0;
        public const byte ArmOpcode = 0xA8;
        public const byte RestoreInputOpcode = 0xA9;

        /// <summary>HallJoy's usage for the vendor collection, 0001:0000,
        /// accepted beside any collection on interface 1
        /// (mad68pr_backend.cpp:41-42, 586-590).</summary>
        public const ushort ExpectedUsagePage = 0x0001;
        public const ushort ExpectedUsage = 0x0000;

        /// <summary>64-byte payload plus the report ID byte
        /// (mad68pr_backend.cpp:587-588).</summary>
        public const int ReportLength = PayloadBytes + 1;

        /// <summary>The model HallJoy names for PID 1109 (README.md:78 and the
        /// product string in docs/protocols/MAD68_PRO_R_FIRMWARE_FINAL_AUDIT.md:88).</summary>
        public const string ModelName = "MADLIONS MAD 68 Pro R";

        /// <summary>The HID usages of W, A, S and D, in the order HallJoy
        /// counts them (mad68pr_backend.cpp:1168-1169).</summary>
        public static readonly int[] WasdHids = { 0x1A, 0x04, 0x16, 0x07 };

        /// <summary>HallJoy's thirteen activation strategies, tried in order
        /// (kStrategies, mad68pr_backend.cpp:118-134). Strategy 1 is the
        /// hardware-confirmed transaction and 2 its clean retry.</summary>
        public static readonly Mad68Strategy[] Strategies =
        {
            new("interrupt-caps-normal-strict", Mad68SendTransport.InterruptCaps, NormalRequestHeader, 0x00, true, 0),
            new("interrupt-caps-normal-strict-retry", Mad68SendTransport.InterruptCaps, NormalRequestHeader, 0x00, true, 0),
            new("interrupt-caps-normal-xor-strict", Mad68SendTransport.InterruptCaps, NormalRequestHeader, 0x5A, true, 0),
            new("interrupt-caps-raw5f-strict", Mad68SendTransport.InterruptCaps, RawRequestHeader, 0x00, true, 0),
            new("interrupt-raw64-normal-strict", Mad68SendTransport.InterruptRaw64, NormalRequestHeader, 0x00, true, 0),
            new("interrupt-raw64-normal-xor-strict", Mad68SendTransport.InterruptRaw64, NormalRequestHeader, 0x5A, true, 0),
            new("interrupt-raw64-raw5f-strict", Mad68SendTransport.InterruptRaw64, RawRequestHeader, 0x00, true, 0),
            new("control-caps-normal-strict", Mad68SendTransport.ControlCaps, NormalRequestHeader, 0x00, true, 0),
            new("control-caps-normal-xor-strict", Mad68SendTransport.ControlCaps, NormalRequestHeader, 0x5A, true, 0),
            new("control-caps-raw5f-strict", Mad68SendTransport.ControlCaps, RawRequestHeader, 0x00, true, 0),
            new("interrupt-caps-normal-delayed", Mad68SendTransport.InterruptCaps, NormalRequestHeader, 0x00, false, 250),
            new("interrupt-caps-raw5f-delayed", Mad68SendTransport.InterruptCaps, RawRequestHeader, 0x00, false, 250),
            new("interrupt-raw64-normal-delayed", Mad68SendTransport.InterruptRaw64, NormalRequestHeader, 0x00, false, 250),
        };

        private sealed class KeyTable
        {
            public int[] HidByKey;
            public int[] DescriptorByKey;
            public Dictionary<int, int> KeyByDescriptor;
            public Dictionary<int, int> KeyByHid;
            public int[] KeyOrder;
        }

        private static readonly Lazy<KeyTable> _table = new(LoadTable);

        /// <summary>The 68 descriptors, from madlions.json. The file lists them
        /// by layout position (HallJoy's internal key id, Esc first). HallJoy's
        /// key index is the descriptor's rank in scanner-slot order, the order
        /// kKeyDescriptors lists them in (mad68pr_protocol.h:37-106), and the
        /// order the forced sweep emits them in.</summary>
        private static KeyTable LoadTable()
        {
            int[] keys = AnalogKeyboardData.Table(MadlionsRoutes.DataFile, "mad68ProRKeys");
            int[] descriptors = AnalogKeyboardData.Table(MadlionsRoutes.DataFile, "mad68ProRDescriptors");
            int[] slots = AnalogKeyboardData.Table(MadlionsRoutes.DataFile, "mad68ProRScannerSlots");
            var positions = new int[PhysicalKeyCount];
            for (int i = 0; i < positions.Length; i++) positions[i] = i;
            Array.Sort(positions, (a, b) => slots[a].CompareTo(slots[b]));
            var table = new KeyTable
            {
                HidByKey = new int[PhysicalKeyCount],
                DescriptorByKey = new int[PhysicalKeyCount],
                KeyByDescriptor = new Dictionary<int, int>(),
                KeyByHid = new Dictionary<int, int>(),
                KeyOrder = AnalogKeyboardData.KeysOf(keys),
            };
            for (int k = 0; k < PhysicalKeyCount; k++)
            {
                int position = positions[k];
                table.HidByKey[k] = keys[position];
                table.DescriptorByKey[k] = descriptors[position];
                table.KeyByDescriptor[descriptors[position]] = k;
                if (keys[position] != 0) table.KeyByHid[keys[position]] = k;
            }
            return table;
        }

        /// <summary>The HID usage of HallJoy's key index, 0 for Fn.</summary>
        public static int HidOfKey(int keyIndex) => _table.Value.HidByKey[keyIndex];

        /// <summary>The 24-bit descriptor of HallJoy's key index.</summary>
        public static int DescriptorOfKey(int keyIndex) => _table.Value.DescriptorByKey[keyIndex];

        /// <summary>The key index of a HID usage, or -1 when the keyboard does
        /// not publish it (KeyIndexFromHid, mad68pr_protocol.cpp:29-35).</summary>
        public static int KeyIndexFromHid(int hid)
            => hid != 0 && _table.Value.KeyByHid.TryGetValue(hid, out int k) ? k : -1;

        /// <summary>The key index of a 3-byte descriptor, or -1
        /// (KeyIndexFromDescriptor, mad68pr_protocol.cpp:37-47).</summary>
        public static int KeyIndexFromDescriptor(byte b0, byte b1, byte b2)
            => _table.Value.KeyByDescriptor.TryGetValue((b0 << 16) | (b1 << 8) | b2, out int k) ? k : -1;

        public static bool IsPublishedHid(int hid) => KeyIndexFromHid(hid) >= 0;

        public static bool IsWasdHid(int hid) => hid == 0x1A || hid == 0x04 || hid == 0x16 || hid == 0x07;

        /// <summary>The 67 published keys in layout order, Esc first. Fn has
        /// no HID and is never published (mad68pr_protocol.h:45).</summary>
        public static int[] KeyOrder() => (int[])_table.Value.KeyOrder.Clone();

        /// <summary>The row's name before and after the handshake: HallJoy
        /// names PID 1109. Another admitted PID keeps its product string.</summary>
        public static string ModelNameFor(ushort productId) => productId == ProductId ? ModelName : null;

        /// <summary>
        /// HallJoy's admission from metadata alone. The enumerator requires
        /// VID 373B, 65-byte input and output reports, and either usage
        /// 0001:0000 or a path naming interface 1 (EnumerateBrandCandidates,
        /// mad68pr_backend.cpp:539-600). Routing then admits PID 1109 at any
        /// bcdDevice, or any PID whose manufacturer and product strings name
        /// the MAD68 family (Mad68ProR_PrepareProtocolRouting,
        /// mad68pr_backend.cpp:2257-2295). HallJoy checks the strings of any
        /// candidate with the same PID. The collection's own strings and its
        /// siblings' are the ones a metadata test can see.
        /// </summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
        {
            if (info == null || !IsCandidateShape(info)) return false;
            if (info.ProductId == ProductId) return true;
            if (LooksLikeMad68Family(info.ManufacturerString, info.ProductString)) return true;
            foreach (var sibling in info.Siblings ?? Array.Empty<AnalogKeyboardDeviceInfo>())
                if (sibling != null && sibling.ProductId == info.ProductId && IsCandidateShape(sibling)
                    && LooksLikeMad68Family(sibling.ManufacturerString, sibling.ProductString))
                    return true;
            return false;
        }

        /// <summary>The enumerator's shape test (mad68pr_backend.cpp:575, 586-590).</summary>
        public static bool IsCandidateShape(AnalogKeyboardDeviceInfo info)
        {
            if (info.VendorId != VendorId) return false;
            if (info.InputReportLength != ReportLength || info.OutputReportLength != ReportLength) return false;
            bool exactUsage = info.UsagePage == ExpectedUsagePage && info.Usage == ExpectedUsage;
            return exactUsage || PathLooksLikeInterface1(info.Path);
        }

        /// <summary>"&amp;mi_01" or "mi_01#" in the lower-cased path
        /// (PathLooksLikeInterface1, mad68pr_backend.cpp:501-506).</summary>
        public static bool PathLooksLikeInterface1(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string lower = path.ToLowerInvariant();
            return lower.Contains("&mi_01", StringComparison.Ordinal) || lower.Contains("mi_01#", StringComparison.Ordinal);
        }

        /// <summary>Manufacturer, a space, then product, lower-cased, holding
        /// "mad68", "mad 68" or "mad-68" (LooksLikeMad68Family,
        /// mad68pr_backend.cpp:508-523).</summary>
        public static bool LooksLikeMad68Family(string manufacturer, string product)
        {
            string identity = ((manufacturer ?? string.Empty) + " " + (product ?? string.Empty)).ToLowerInvariant();
            return identity.Contains("mad68", StringComparison.Ordinal)
                || identity.Contains("mad 68", StringComparison.Ordinal)
                || identity.Contains("mad-68", StringComparison.Ordinal);
        }

        /// <summary>A zero-payload command (MakeZeroPayloadRequest,
        /// mad68pr_protocol.cpp:7-27). With 0x55 framing byte 2 is the XOR key
        /// and byte 3 the checksum, 0 for an empty payload, and a nonzero key
        /// XORs bytes 3 to 7.</summary>
        public static byte[] MakeZeroPayloadRequest(byte opcode, byte framing = NormalRequestHeader, byte xorKey = 0)
        {
            var packet = new byte[PayloadBytes];
            packet[0] = framing;
            packet[1] = opcode;
            if (framing == NormalRequestHeader)
            {
                packet[2] = xorKey;
                packet[3] = 0;
                if (xorKey != 0)
                    for (int i = 3; i <= 7; i++) packet[i] ^= xorKey;
            }
            return packet;
        }

        /// <summary>The Windows buffer for the caps transports: max(9, output
        /// report length) bytes, report ID 0, the payload from byte 1
        /// (SendInterrupt and SendControl, mad68pr_backend.cpp:892-946). Null
        /// when fewer than 8 payload bytes would fit.</summary>
        public static byte[] CapsReport(byte[] payload, int outputReportLength)
        {
            var wire = new byte[Math.Max(9, outputReportLength)];
            int copy = Math.Min(payload.Length, wire.Length - 1);
            if (copy < 8) return null;
            Array.Copy(payload, 0, wire, 1, copy);
            return wire;
        }

        private static bool Plausible(byte header)
            => header == StreamHeader || header == NormalResponseHeader
                || header == ChecksumErrorHeader || header == RawRequestHeader;

        /// <summary>Finds the payload in a received report and copies up to 64
        /// bytes of it into <paramref name="normalized"/>, zero-filled
        /// (NormalizePayload, mad68pr_backend.cpp:628-649). A leading 0 before
        /// a plausible header is the report ID. False for anything else.</summary>
        public static bool NormalizePayload(ReadOnlySpan<byte> wire, byte[] normalized)
        {
            if (wire.Length == 0) return false;
            int offset = -1;
            if (wire.Length >= 2 && wire[0] == 0 && Plausible(wire[1])) offset = 1;
            else if (Plausible(wire[0])) offset = 0;
            else if (wire.Length >= 2 && Plausible(wire[1])) offset = 1;
            if (offset < 0 || offset >= wire.Length) return false;
            Array.Clear(normalized, 0, PayloadBytes);
            int available = Math.Min(PayloadBytes, wire.Length - offset);
            wire.Slice(offset, available).CopyTo(normalized);
            return true;
        }

        /// <summary>
        /// Checks a control reply against the request (DecodeControlResponse,
        /// mad68pr_protocol.cpp:130-192). Raw framing needs 5F and the opcode.
        /// Otherwise AA or AB, byte 2 the XOR key over bytes 3 to 7 and the
        /// payload, byte 4 a length of at most 0x38, and byte 3 the low byte
        /// of the sum of bytes 4 through 7 + length.
        /// </summary>
        public static Mad68ControlResponse DecodeControlResponse(ReadOnlySpan<byte> payload,
            byte expectedRequestHeader, byte expectedOpcode)
        {
            if (payload.Length < PayloadBytes)
                return new Mad68ControlResponse(Mad68ControlKind.NotControl, 0, 0, 0, 0, 0, 0);
            byte header = payload[0];
            byte opcode = payload[1];

            if (expectedRequestHeader == RawRequestHeader)
            {
                if (header != RawRequestHeader)
                    return new Mad68ControlResponse(Mad68ControlKind.NotControl, header, opcode, 0, 0, 0, 0);
                return new Mad68ControlResponse(opcode == expectedOpcode ? Mad68ControlKind.Valid : Mad68ControlKind.Invalid,
                    header, opcode, 0, 0, 0, 0);
            }

            if (header != NormalResponseHeader && header != ChecksumErrorHeader)
                return new Mad68ControlResponse(Mad68ControlKind.NotControl, header, opcode, 0, 0, 0, 0);

            Span<byte> decoded = stackalloc byte[PayloadBytes];
            payload.Slice(0, PayloadBytes).CopyTo(decoded);
            byte xorKey = decoded[2];
            if (xorKey != 0)
                for (int i = 3; i <= 7; i++) decoded[i] ^= xorKey;

            byte length = decoded[4];
            if (length > 0x38)
                return new Mad68ControlResponse(Mad68ControlKind.Invalid, header, opcode, xorKey, length, 0, 0);

            if (xorKey != 0)
                for (int i = 8; i < 8 + length; i++) decoded[i] ^= xorKey;

            int sum = 0;
            for (int i = 4; i < 8 + length; i++) sum += decoded[i];
            byte checksum = decoded[3];
            byte expected = (byte)(sum & 0xFF);
            byte decodedOpcode = decoded[1];

            if (header == ChecksumErrorHeader)
                return new Mad68ControlResponse(Mad68ControlKind.ChecksumError, header, decodedOpcode, xorKey, length, checksum, expected);
            var kind = decodedOpcode == expectedOpcode && checksum == expected
                ? Mad68ControlKind.Valid
                : Mad68ControlKind.Invalid;
            return new Mad68ControlResponse(kind, header, decodedOpcode, xorKey, length, checksum, expected);
        }

        /// <summary>0 to 1600 onto 0 to 1000, rounded
        /// (mad68pr_protocol.cpp:119-121).</summary>
        public static int ToMilli(int raw) => (raw * 1000 + AnalogFullScale / 2) / AnalogFullScale;

        /// <summary>
        /// Decodes one A0 report (DecodeKeySample, mad68pr_protocol.cpp:101-128):
        /// at least 20 bytes, header A0, a known descriptor in bytes 1 to 3, a
        /// big-endian value of at most 1600 in bytes 4 and 5, the key state in
        /// byte 10, the threshold in bytes 14 and 15 and the baseline in bytes
        /// 18 and 19. Bytes 11 to 13 hold the key's calibration scale and are
        /// never an axis (docs/protocols/MAD68_PRO_R_FIRMWARE_FINAL_AUDIT.md:25-27).
        /// </summary>
        public static bool DecodeKeySample(ReadOnlySpan<byte> payload, out Mad68KeySample sample)
        {
            sample = default;
            if (payload.Length < 20 || payload[0] != StreamHeader) return false;
            int keyIndex = KeyIndexFromDescriptor(payload[1], payload[2], payload[3]);
            if (keyIndex < 0) return false;
            int raw = (payload[4] << 8) | payload[5];
            if (raw > AnalogFullScale) return false;
            sample = new Mad68KeySample(keyIndex, HidOfKey(keyIndex), raw, ToMilli(raw),
                (payload[14] << 8) | payload[15], (payload[18] << 8) | payload[19], payload[10]);
            return true;
        }

        /// <summary>Whether a value agrees with a digital press or release
        /// (AnalogTransitionMatchesDigital, mad68pr_protocol.cpp:64-80).</summary>
        public static bool AnalogTransitionMatchesDigital(bool expectedDown, int threshold, int rawAtEvent, int currentRaw)
        {
            int pressFloor = Math.Max(16, threshold / 4);
            int releaseCeiling = Math.Max(32, threshold / 2);
            if (expectedDown)
            {
                int changedFloor = Math.Min(AnalogFullScale, rawAtEvent + 8);
                return currentRaw >= pressFloor || currentRaw >= changedFloor;
            }
            return currentRaw <= releaseCeiling || currentRaw + 8 <= rawAtEvent;
        }

        /// <summary>A change of at least 64 that crosses the key's threshold
        /// (IsPostSweepAnalogProof, mad68pr_protocol.cpp:82-99).</summary>
        public static bool IsPostSweepAnalogProof(int previousRaw, int currentRaw, int threshold)
        {
            if (threshold == 0 || threshold > AnalogFullScale
                || previousRaw > AnalogFullScale || currentRaw > AnalogFullScale)
                return false;
            int delta = Math.Abs(previousRaw - currentRaw);
            if (delta < SteadyProofMinDelta) return false;
            bool pressedAcross = previousRaw < threshold && currentRaw >= threshold;
            bool releasedAcross = previousRaw >= threshold && currentRaw < threshold;
            return pressedAcross || releasedAcross;
        }
    }

    /// <summary>Which keys HallJoy serves from the stream
    /// (mad68pr_backend.cpp:94-99, 1039-1044).</summary>
    public enum Mad68PublishMode
    {
        None = 0,
        /// <summary>W, A, S and D only.</summary>
        EmergencyWasd = 1,
        /// <summary>All 67 published keys.</summary>
        Full = 2,
    }

    /// <summary>
    /// One MAD 68 Pro R session, HallJoy's RunSession and RunStrategy
    /// (mad68pr_backend.cpp:1572-2145) cut into passes of one read slice or
    /// one command each, so a stop request is seen within 25 ms and the
    /// closing A9 always goes out.
    ///
    /// <para><see cref="Start"/> is HallJoy's routing proof: PID 1109 at the
    /// audited bcdDevice 0102 is accepted with no traffic, anything else must
    /// acknowledge A9 within 450 ms. The passes then listen for 1400 ms and,
    /// unless all 68 descriptors already streamed, run the strategy list: A9
    /// and its acknowledgment, all keys up for 500 ms, A8 and its
    /// acknowledgment (or at least 12 fresh descriptors), A9 again, then up to
    /// 7 s for fresh W, A, S and D or all 68. The keyboard publishes W, A, S
    /// and D first and all 67 keys once a post-sweep edge proves its steady
    /// state. HallJoy's per-key ownership gate, recovery watchdog and rate
    /// limit are ported as written.</para>
    ///
    /// <para>HallJoy's release feeds its Raw Input key edges from every
    /// keyboard into this gate (app.cpp:1726-1728). Here the edges come from
    /// <c>isHeld</c> for this keyboard's own 67 keys only, polled at the start
    /// of every pass. Windows merges key state across keyboards, so a key
    /// held on another keyboard still reads as held.</para>
    /// </summary>
    public sealed class Mad68ProRSession : AnalogKeyboardSession
    {
        // mad68pr_backend.cpp:43-74 unless noted.
        public const int ReadSliceMs = 25;
        public const int CommandAckMs = 650;
        public const int ValidationMs = 7000;
        public const int PassiveListenMs = 1400;
        public const int DigitalAnalogDeadlineMs = 1000;
        public const int ForcedSweepGraceMs = 4500;
        public const int ForcedSweepDigitalDeadlineMs = 1600;
        public const int GlobalStreamAliveMs = 350;
        public const int SchedulerStarvationDeadlineMs = 3000;
        public const int DigitalLeadToleranceMs = 250;
        public const int AllReleasedStableMs = 500;
        public const int StreamSalvageAgeMs = 2500;
        public const int RecoveryWindowMs = 60000;
        public const int MaxRecoveryCyclesPerWindow = 2;
        public const int A8SemanticEvidenceMinFresh = 12;
        /// <summary>Routing probe window (mad68pr_backend.cpp:969).</summary>
        public const int ProbeWindowMs = 450;
        /// <summary>Pause between closing and reopening before a strategy
        /// (mad68pr_backend.cpp:742).</summary>
        public const int ReopenPauseMs = 80;
        /// <summary>Wait between strategies (mad68pr_backend.cpp:2001).</summary>
        public const int NextStrategyDelayMs = 300;
        /// <summary>Release-wait read slice (mad68pr_backend.cpp:1441).</summary>
        public const int ReleaseReadMs = 50;
        /// <summary>Pumps after each restore A9 (mad68pr_backend.cpp:1558, 1568).</summary>
        public const int RestorePumpMs = 200;
        /// <summary>Pump after the closing A9 (mad68pr_backend.cpp:2132).</summary>
        public const int ClosePumpMs = 150;
        /// <summary>Consecutive read errors that end the session
        /// (mad68pr_backend.cpp:1944-1948).</summary>
        public const int ReadErrorLimit = 3;

        private enum Phase
        {
            Main,
            InitialAck,
            InitialPump,
            WaitRelease,
            ArmAck,
            ArmPump,
            FinalAck,
            FinalPump,
            Validate,
            RestorePump1,
            RestorePump2,
        }

        private const int KeyCount = Mad68ProRProtocol.PhysicalKeyCount;

        private readonly ushort _productId;
        private readonly ushort _versionNumber;
        private readonly Func<long> _clock;
        private readonly Action<int> _sleep;
        private readonly int[] _hidByKey = new int[KeyCount];
        private readonly byte[] _packet = new byte[Mad68ProRProtocol.PayloadBytes];
        private readonly AnalogKeyInputState _scratch = new();

        // Per descriptor, by HallJoy's key index (g_descriptorRaw, g_descriptorSeq).
        private readonly int[] _descriptorRaw = new int[KeyCount];
        private readonly uint[] _descriptorSeq = new uint[KeyCount];

        // Per HID usage (g_raw, g_milli, g_threshold, g_baseline, g_keyState,
        // g_sampleMs, g_sampleSeq).
        private readonly int[] _raw = new int[256];
        private readonly int[] _milli = new int[256];
        private readonly int[] _threshold = new int[256];
        private readonly int[] _baseline = new int[256];
        private readonly int[] _keyState = new int[256];
        private readonly long[] _sampleMs = new long[256];
        private readonly uint[] _sampleSeq = new uint[256];

        // The digital side (g_physicalDown, g_digitalSeq, g_digitalMs,
        // g_digitalDown, g_sampleSeqAtDigitalEvent, g_rawAtDigitalEvent).
        private readonly bool[] _physicalDown = new bool[256];
        private readonly uint[] _digitalSeq = new uint[256];
        private readonly long[] _digitalMs = new long[256];
        private readonly bool[] _digitalDown = new bool[256];
        private readonly uint[] _sampleSeqAtDigitalEvent = new uint[256];
        private readonly int[] _rawAtDigitalEvent = new int[256];

        // DigitalWatch (mad68pr_backend.cpp:986-998).
        private readonly uint[] _watchSeenSeq = new uint[256];
        private readonly bool[] _watchPending = new bool[256];
        private readonly bool[] _watchExpectedDown = new bool[256];
        private readonly long[] _watchEventMs = new long[256];
        private readonly uint[] _watchSampleSeqAtEvent = new uint[256];
        private readonly int[] _watchRawAtEvent = new int[256];
        private readonly int[] _watchFailureStreak = new int[256];
        private readonly int[] _watchDeadlineMs = new int[256];
        private readonly bool[] _watchLiveStreamExtension = new bool[256];

        // Session-wide state (mad68pr_backend.cpp:201-227).
        private Mad68PublishMode _publishMode;
        private long _lastA0Ms;
        private long _forcedSweepGraceUntilMs;
        private int _coverage;
        private bool _steadyStateConfirmed;
        private uint _orderedSweepCycles;
        private uint _orderedSweepPosition;
        private long _activationEpochMs;
        private long _lastOrderedSweepMs;
        private long _rawInputEdges;
        private bool _recoveryRequested;
        private int _recoveryHid;

        // RunSession's loop state (mad68pr_backend.cpp:1922-1933).
        private bool _begun;
        private int _nextStrategy;
        private bool _strategySucceeded;
        private bool _recoveryCycle;
        private int _recoveryCycles;
        private long _recoveryWindowStartMs;
        private readonly uint[] _activeActivationBaseline = new uint[KeyCount];
        private bool _activeActivationBaselineValid;
        private bool _backgroundFullUpgradePending;
        private long _passiveDeadline;
        private int _consecutiveReadErrors;
        private bool _endedBySession;

        // RunStrategy's state.
        private Phase _phase = Phase.Main;
        private int _strategyIndex = -1;
        private long _phaseDeadline;
        private long _allReleasedSince;
        private readonly uint[] _beforeA8 = new uint[KeyCount];

        public Mad68ProRSession(ushort productId, ushort versionNumber,
            Func<long> clock = null, Action<int> sleep = null)
        {
            _productId = productId;
            _versionNumber = versionNumber;
            _clock = clock ?? (() => Environment.TickCount64);
            _sleep = sleep ?? Thread.Sleep;
            for (int k = 0; k < KeyCount; k++) _hidByKey[k] = Mad68ProRProtocol.HidOfKey(k);
        }

        public override string ModelName => Mad68ProRProtocol.ModelNameFor(_productId);

        public override int[] KeyOrder => Mad68ProRProtocol.KeyOrder();

        /// <summary>Which keys the stream is serving now.</summary>
        public Mad68PublishMode PublishMode => _publishMode;

        /// <summary>True once a post-sweep edge proved the steady state.</summary>
        public bool SteadyStateConfirmed => _steadyStateConfirmed;

        /// <summary>Descriptors seen since the last reset, of 68.</summary>
        public int Coverage => _coverage;

        /// <summary>The strategy running or last run, 0-based, or -1.</summary>
        public int StrategyIndex => _strategyIndex;

        /// <summary>True while a strategy runs.</summary>
        public bool Activating => _phase != Phase.Main;

        /// <summary>Strategies tried so far this session.</summary>
        public int StrategiesTried => _nextStrategy;

        // ─── Start: the routing proof ───

        /// <summary>HallJoy's routing proof (mad68pr_backend.cpp:2262-2281).
        /// The audited identity needs no traffic. Anything else must answer A9
        /// sent with strategy 1 with a valid acknowledgment within 450 ms,
        /// read in 25 ms slices, and a checksum error or a mismatched reply
        /// fails it at once (ProbeNativeControlProtocol,
        /// mad68pr_backend.cpp:959-984).</summary>
        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (_productId == Mad68ProRProtocol.ProductId && _versionNumber == Mad68ProRProtocol.AuditedBcdDevice)
                return true;

            var strategy = Mad68ProRProtocol.Strategies[0];
            if (!Send(io, strategy, Mad68ProRProtocol.RestoreInputOpcode)) return false;
            long deadline = _clock() + ProbeWindowMs;
            while (_clock() < deadline)
            {
                int n = io.Receive(Buffer, ReadSliceMs);
                // A vanished device fails the probe, which HallJoy reaches by
                // reading until the window closes.
                if (n < 0) return false;
                if (n == 0) continue;
                if (!Mad68ProRProtocol.NormalizePayload(Buffer.AsSpan(0, n), _packet)) continue;
                var response = Mad68ProRProtocol.DecodeControlResponse(_packet, strategy.Framing,
                    Mad68ProRProtocol.RestoreInputOpcode);
                if (response.Kind == Mad68ControlKind.Valid) return true;
                if (response.Kind == Mad68ControlKind.ChecksumError || response.Kind == Mad68ControlKind.Invalid)
                    return false;
            }
            return false;
        }

        // ─── Passes ───

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            long now = _clock();
            if (!_begun)
            {
                // RunSession opens the handles, then listens passively
                // (mad68pr_backend.cpp:1894-1930).
                _begun = true;
                ResetSessionPublished();
                _passiveDeadline = now + PassiveListenMs;
            }

            PollHeldKeys(isHeld, now);

            bool alive = _phase switch
            {
                Phase.Main => MainStep(io),
                Phase.InitialAck or Phase.ArmAck or Phase.FinalAck => AckStep(io),
                Phase.InitialPump or Phase.ArmPump or Phase.FinalPump or Phase.RestorePump1 or Phase.RestorePump2 => PumpStep(io),
                Phase.WaitRelease => WaitReleaseStep(io),
                Phase.Validate => ValidateStep(io),
                _ => true,
            };
            if (!alive)
            {
                _endedBySession = true;
                return AnalogPollResult.Failed;
            }
            return Publish(output) ? AnalogPollResult.Ok : AnalogPollResult.Idle;
        }

        /// <summary>
        /// Puts the keyboard back to ordinary input the way HallJoy's session
        /// ends (mad68pr_backend.cpp:2125-2133): A9 through strategy 1. A stop
        /// that lands inside a strategy first runs that strategy's restore, A9
        /// through the strategy and then through strategy 1 when they differ
        /// (BestEffortRestore, mad68pr_backend.cpp:1554-1570), which is where
        /// HallJoy's stop flag sends a running strategy. The closing pump runs
        /// only when the session ended on its own, since HallJoy's pump
        /// returns at once on a stop request. A session stopped before its
        /// first pass sends nothing, as RunSession returns before its loop
        /// (mad68pr_backend.cpp:1918).
        /// </summary>
        public override void Stop(IAnalogKeyboardTransport io)
        {
            if (!_begun) return;
            var primary = Mad68ProRProtocol.Strategies[0];
            if (_phase != Phase.Main && _strategyIndex >= 0)
            {
                var strategy = Mad68ProRProtocol.Strategies[_strategyIndex];
                switch (_phase)
                {
                    case Phase.RestorePump1:
                        if (_strategyIndex != 0) Send(io, primary, Mad68ProRProtocol.RestoreInputOpcode);
                        break;
                    case Phase.RestorePump2:
                        break;
                    default:
                        Send(io, strategy, Mad68ProRProtocol.RestoreInputOpcode);
                        if (_strategyIndex != 0) Send(io, primary, Mad68ProRProtocol.RestoreInputOpcode);
                        break;
                }
                _phase = Phase.Main;
            }

            if (Send(io, primary, Mad68ProRProtocol.RestoreInputOpcode) && _endedBySession)
            {
                long deadline = _clock() + ClosePumpMs;
                while (true)
                {
                    long remaining = deadline - _clock();
                    if (remaining <= 0) break;
                    int n = io.Receive(Buffer, (int)Math.Min(ReadSliceMs, remaining));
                    if (n < 0) break;
                }
            }
        }

        /// <summary>One iteration of RunSession's loop
        /// (mad68pr_backend.cpp:1935-2123). False ends the session.</summary>
        private bool MainStep(IAnalogKeyboardTransport io)
        {
            if (ReadPayload(io, ReadSliceMs)) ProcessPayload(_clock());
            ObserveDigitalEvents(_clock());
            if (_consecutiveReadErrors >= ReadErrorLimit) return false;

            long now = _clock();
            if (!_strategySucceeded && now >= _passiveDeadline)
            {
                if (HasCompleteSnapshot())
                {
                    // Passive 68/68: nothing is sent, W, A, S and D publish
                    // until a post-sweep edge proves the steady state.
                    _strategySucceeded = true;
                    _backgroundFullUpgradePending = true;
                    SetPublishMode(Mad68PublishMode.EmergencyWasd);
                }
                else if (_nextStrategy < Mad68ProRProtocol.Strategies.Length)
                {
                    // HallJoy's worker only runs routed paths, so the session
                    // is always protocol-validated and its unvalidated branch
                    // (mad68pr_backend.cpp:1972-1981) never runs here.
                    BeginStrategy(io, _nextStrategy);
                    return true;
                }
                else
                {
                    if (HasWasdSnapshot()) SetPublishMode(Mad68PublishMode.EmergencyWasd);
                    _strategySucceeded = true;
                }
            }
            FinishIteration();
            return true;
        }

        /// <summary>The rest of RunSession's iteration after the strategy
        /// decision: recovery requests, the background upgrade to all keys and
        /// the recovery bookkeeping (mad68pr_backend.cpp:2015-2070).</summary>
        private void FinishIteration()
        {
            if (_recoveryRequested)
            {
                _recoveryRequested = false;
                long recoveryNow = _clock();
                if (_recoveryWindowStartMs == 0 || recoveryNow - _recoveryWindowStartMs >= RecoveryWindowMs)
                {
                    _recoveryWindowStartMs = recoveryNow;
                    _recoveryCycles = 0;
                }
                if (_recoveryCycles < MaxRecoveryCyclesPerWindow)
                {
                    _recoveryCycles++;
                    ResetSessionPublished();
                    _activeActivationBaselineValid = false;
                    _backgroundFullUpgradePending = false;
                    _strategySucceeded = false;
                    _recoveryCycle = true;
                    _nextStrategy = 0;
                    _passiveDeadline = _clock();
                }
            }

            if (_backgroundFullUpgradePending
                && _publishMode == Mad68PublishMode.EmergencyWasd
                && _activeActivationBaselineValid
                && _steadyStateConfirmed
                && FreshCoverage(_activeActivationBaseline) == KeyCount)
            {
                SetPublishMode(Mad68PublishMode.Full);
                _backgroundFullUpgradePending = false;
            }

            if (_recoveryCycle && _strategySucceeded && _publishMode != Mad68PublishMode.None)
                _recoveryCycle = false;
        }

        // ─── One strategy (RunStrategy, mad68pr_backend.cpp:1572-1647) ───

        private Mad68Strategy CurrentStrategy => Mad68ProRProtocol.Strategies[_strategyIndex];

        private void BeginStrategy(IAnalogKeyboardTransport io, int index)
        {
            _strategyIndex = index;
            SynchroniseDigitalWatch(clearFailureStreak: true);

            // Reopen (mad68pr_backend.cpp:738-744, 1580): HallJoy closes its
            // handles, waits 80 ms and opens them again. This session keeps its
            // one handle, so the pause is followed by discarding whatever
            // arrived meanwhile, which a fresh handle would not have seen.
            _sleep(ReopenPauseMs);
            io.DiscardStale();
            _consecutiveReadErrors = 0;

            if (!Send(io, CurrentStrategy, Mad68ProRProtocol.RestoreInputOpcode))
            {
                BeginRestore(io);
                return;
            }
            EnterCommandWait(Phase.InitialAck, Phase.InitialPump);
        }

        /// <summary>SendCommand after the send (mad68pr_backend.cpp:1405-1409):
        /// await the acknowledgment for 650 ms, or pump the strategy's delay.</summary>
        private void EnterCommandWait(Phase ackPhase, Phase pumpPhase)
        {
            var strategy = CurrentStrategy;
            if (strategy.RequireAck)
            {
                _phase = ackPhase;
                _phaseDeadline = _clock() + CommandAckMs;
            }
            else
            {
                _phase = pumpPhase;
                _phaseDeadline = _clock() + strategy.InterCommandDelayMs;
            }
        }

        private byte PhaseOpcode => _phase is Phase.ArmAck or Phase.ArmPump
            ? Mad68ProRProtocol.ArmOpcode
            : Mad68ProRProtocol.RestoreInputOpcode;

        /// <summary>One slice of WaitForAck (mad68pr_backend.cpp:1356-1394).
        /// A0 reports that arrive meanwhile are processed as usual.</summary>
        private bool AckStep(IAnalogKeyboardTransport io)
        {
            long remaining = _phaseDeadline - _clock();
            if (remaining <= 0)
            {
                CommandFinished(io, false);
                return true;
            }
            if (!ReadPayload(io, (int)Math.Min(ReadSliceMs, remaining)))
                return _consecutiveReadErrors < ReadErrorLimit;
            var response = Mad68ProRProtocol.DecodeControlResponse(_packet, CurrentStrategy.Framing, PhaseOpcode);
            ProcessPayload(_clock());
            if (response.Kind == Mad68ControlKind.Valid) CommandFinished(io, true);
            else if (response.Kind == Mad68ControlKind.ChecksumError) CommandFinished(io, false);
            return true;
        }

        /// <summary>One slice of PumpFor (mad68pr_backend.cpp:1339-1354).</summary>
        private bool PumpStep(IAnalogKeyboardTransport io)
        {
            long remaining = _phaseDeadline - _clock();
            if (remaining <= 0)
            {
                PumpFinished(io);
                return true;
            }
            if (ReadPayload(io, (int)Math.Min(ReadSliceMs, remaining))) ProcessPayload(_clock());
            return _consecutiveReadErrors < ReadErrorLimit;
        }

        private void PumpFinished(IAnalogKeyboardTransport io)
        {
            switch (_phase)
            {
                case Phase.RestorePump1:
                    RestoreSecond(io);
                    break;
                case Phase.RestorePump2:
                    FinishStrategy(false);
                    break;
                default:
                    // A command without an awaited acknowledgment counts as
                    // sent once its delay has been pumped.
                    CommandFinished(io, true);
                    break;
            }
        }

        private void CommandFinished(IAnalogKeyboardTransport io, bool ok)
        {
            switch (_phase)
            {
                case Phase.InitialAck:
                case Phase.InitialPump:
                    if (!ok)
                    {
                        BeginRestore(io);
                        return;
                    }
                    _phase = Phase.WaitRelease;
                    _allReleasedSince = 0;
                    return;
                case Phase.ArmAck:
                case Phase.ArmPump:
                    AfterArm(io, ok);
                    return;
                case Phase.FinalAck:
                case Phase.FinalPump:
                    if (!ok)
                    {
                        BeginRestore(io);
                        return;
                    }
                    _phase = Phase.Validate;
                    _phaseDeadline = _clock() + ValidationMs;
                    return;
            }
        }

        /// <summary>One slice of WaitForAllReleased (mad68pr_backend.cpp:1412-1447):
        /// every key up for 500 ms before A8, since A8 takes each key's
        /// current Hall sample as its new baseline
        /// (docs/protocols/MAD68_PRO_R_FIRMWARE_FINAL_AUDIT.md:219-221).</summary>
        private bool WaitReleaseStep(IAnalogKeyboardTransport io)
        {
            long now = _clock();
            if (!AnyKeyDown())
            {
                if (_allReleasedSince == 0) _allReleasedSince = now;
                if (now - _allReleasedSince >= AllReleasedStableMs)
                {
                    Arm(io);
                    return true;
                }
            }
            else
            {
                _allReleasedSince = 0;
            }
            if (ReadPayload(io, ReleaseReadMs)) ProcessPayload(_clock());
            return _consecutiveReadErrors < ReadErrorLimit;
        }

        /// <summary>Snapshot, reset the steady-state tracking and send A8
        /// (mad68pr_backend.cpp:1599-1609).</summary>
        private void Arm(IAnalogKeyboardTransport io)
        {
            CaptureSnapshotBaseline(_beforeA8);
            _steadyStateConfirmed = false;
            _rawInputEdges = 0;
            _orderedSweepCycles = 0;
            _orderedSweepPosition = 0;
            _lastOrderedSweepMs = 0;
            _activationEpochMs = _clock();

            if (!Send(io, CurrentStrategy, Mad68ProRProtocol.ArmOpcode))
            {
                AfterArm(io, false);
                return;
            }
            EnterCommandWait(Phase.ArmAck, Phase.ArmPump);
        }

        /// <summary>A missing A8 acknowledgment still counts when at least 12
        /// descriptors streamed since the snapshot, the firmware's forced
        /// sweep. Then the grace window opens and A9 goes out
        /// (mad68pr_backend.cpp:1610-1639).</summary>
        private void AfterArm(IAnalogKeyboardTransport io, bool acked)
        {
            if (!acked && FreshCoverage(_beforeA8) < A8SemanticEvidenceMinFresh)
            {
                BeginRestore(io);
                return;
            }
            _forcedSweepGraceUntilMs = _clock() + ForcedSweepGraceMs;
            if (!Send(io, CurrentStrategy, Mad68ProRProtocol.RestoreInputOpcode))
            {
                BeginRestore(io);
                return;
            }
            EnterCommandWait(Phase.FinalAck, Phase.FinalPump);
        }

        /// <summary>One slice of ValidateStreamAfterActivation
        /// (mad68pr_backend.cpp:1480-1552).</summary>
        private bool ValidateStep(IAnalogKeyboardTransport io)
        {
            if (_clock() < _phaseDeadline)
            {
                if (ReadPayload(io, ReadSliceMs)) ProcessPayload(_clock());
                if (_consecutiveReadErrors >= ReadErrorLimit) return false;
                int fresh = FreshCoverage(_beforeA8);
                int freshWasd = FreshWasdCoverage(_beforeA8);
                if (freshWasd == 4 && _publishMode == Mad68PublishMode.None)
                    SetPublishMode(Mad68PublishMode.EmergencyWasd);
                if (fresh == KeyCount)
                {
                    SetPublishMode(Mad68PublishMode.EmergencyWasd);
                    FinishStrategy(true);
                }
                return true;
            }

            if (FreshWasdCoverage(_beforeA8) == 4)
            {
                SetPublishMode(Mad68PublishMode.EmergencyWasd);
                FinishStrategy(true);
                return true;
            }

            // Salvage from this session's own data while the stream is recent.
            long now = _clock();
            long lastA0 = _lastA0Ms;
            bool recentA0 = lastA0 != 0 && now >= lastA0 && now - lastA0 <= StreamSalvageAgeMs;
            if (recentA0 && (HasCompleteSnapshot() || HasWasdSnapshot()))
            {
                SetPublishMode(Mad68PublishMode.EmergencyWasd);
                FinishStrategy(true);
                return true;
            }
            BeginRestore(io);
            return true;
        }

        /// <summary>BestEffortRestore (mad68pr_backend.cpp:1554-1570): A9
        /// through the failing strategy and a 200 ms pump, then A9 through
        /// strategy 1 and another pump when the strategy was not strategy 1.</summary>
        private void BeginRestore(IAnalogKeyboardTransport io)
        {
            if (Send(io, CurrentStrategy, Mad68ProRProtocol.RestoreInputOpcode))
            {
                _phase = Phase.RestorePump1;
                _phaseDeadline = _clock() + RestorePumpMs;
                return;
            }
            RestoreSecond(io);
        }

        private void RestoreSecond(IAnalogKeyboardTransport io)
        {
            if (_strategyIndex != 0
                && Send(io, Mad68ProRProtocol.Strategies[0], Mad68ProRProtocol.RestoreInputOpcode))
            {
                _phase = Phase.RestorePump2;
                _phaseDeadline = _clock() + RestorePumpMs;
                return;
            }
            FinishStrategy(false);
        }

        /// <summary>RunSession after RunStrategy returns
        /// (mad68pr_backend.cpp:1986-2001), then the rest of that
        /// iteration.</summary>
        private void FinishStrategy(bool success)
        {
            _phase = Phase.Main;
            _nextStrategy++;
            _strategySucceeded = success;
            if (success)
            {
                Array.Copy(_beforeA8, _activeActivationBaseline, KeyCount);
                _activeActivationBaselineValid = true;
                _backgroundFullUpgradePending = _publishMode == Mad68PublishMode.EmergencyWasd;
            }
            else
            {
                _activeActivationBaselineValid = false;
                _backgroundFullUpgradePending = false;
                ResetSessionPublished();
            }
            _passiveDeadline = _clock() + NextStrategyDelayMs;
            FinishIteration();
        }

        // ─── Transport ───

        /// <summary>Session::Send (mad68pr_backend.cpp:799-831). A8 and A9
        /// are the only opcodes that ever leave. The raw 64-byte transport
        /// writes a 64-byte buffer to a collection whose output report is 65
        /// bytes, which Windows refuses (hidapi's windows/hid.c:1096-1101
        /// pads every write to the output report length for that reason), so
        /// HallJoy's WriteFile fails and nothing reaches the keyboard. This
        /// transport always pads to the report length, so a raw 64-byte send
        /// fails here the same way, before any I/O.</summary>
        private bool Send(IAnalogKeyboardTransport io, Mad68Strategy strategy, byte opcode)
        {
            if (opcode != Mad68ProRProtocol.ArmOpcode && opcode != Mad68ProRProtocol.RestoreInputOpcode)
                return false;
            var payload = Mad68ProRProtocol.MakeZeroPayloadRequest(opcode, strategy.Framing, strategy.XorKey);
            switch (strategy.Transport)
            {
                case Mad68SendTransport.InterruptCaps:
                {
                    var wire = Mad68ProRProtocol.CapsReport(payload, io.OutputLength);
                    return wire != null && io.Send(wire);
                }
                case Mad68SendTransport.ControlCaps:
                {
                    var wire = Mad68ProRProtocol.CapsReport(payload, io.OutputLength);
                    return wire != null && io.SendOutputReport(wire);
                }
                default:
                    return false;
            }
        }

        /// <summary>One read slice (ReadPayload, mad68pr_backend.cpp:746-797):
        /// true with the payload in <see cref="_packet"/>. A read error counts
        /// toward the limit, a completed read clears the count, and a timeout
        /// does neither.</summary>
        private bool ReadPayload(IAnalogKeyboardTransport io, int timeoutMs)
        {
            int n = io.Receive(Buffer, Math.Max(1, timeoutMs));
            if (n < 0)
            {
                _consecutiveReadErrors++;
                return false;
            }
            if (n == 0) return false;
            _consecutiveReadErrors = 0;
            return Mad68ProRProtocol.NormalizePayload(Buffer.AsSpan(0, n), _packet);
        }

        // ─── Stream processing (ProcessPayload, mad68pr_backend.cpp:1183-1337) ───

        private void ProcessPayload(long now)
        {
            if (_packet[0] != Mad68ProRProtocol.StreamHeader) return;
            _lastA0Ms = now;
            if (!Mad68ProRProtocol.DecodeKeySample(_packet, out var sample)) return;

            int keyIndex = sample.KeyIndex;
            TrackOrderedStartupSweep(keyIndex, now);
            uint descriptorBefore = _descriptorSeq[keyIndex]++;
            int previousDescriptorRaw = _descriptorRaw[keyIndex];
            _descriptorRaw[keyIndex] = sample.Raw;

            int hid = sample.Hid;
            if (hid != 0 && hid < 256)
            {
                _raw[hid] = sample.Raw;
                _milli[hid] = sample.Milli;
                _threshold[hid] = sample.Threshold;
                _baseline[hid] = sample.Baseline;
                _keyState[hid] = sample.State;
                _sampleMs[hid] = now;
                _sampleSeq[hid]++;
            }

            if (descriptorBefore == 0) _coverage++;
            else MaybeConfirmSteadyStateFromAnalogOnlyEdge(now, previousDescriptorRaw, sample);

            if (hid != 0 && hid < 256 && _watchPending[hid] && _sampleSeq[hid] > _watchSampleSeqAtEvent[hid])
            {
                bool valueMatches = AnalogMatchesDigital(hid, _watchExpectedDown[hid], _watchRawAtEvent[hid], sample.Raw);
                int edgeDelta = Math.Abs(_watchRawAtEvent[hid] - sample.Raw);
                MaybeConfirmSteadyStateFromPhysicalEdge(_watchEventMs[hid], now, valueMatches || edgeDelta >= 8);
                _watchPending[hid] = false;
                _watchFailureStreak[hid] = 0;
            }
        }

        /// <summary>Counts ordered 68-report sweeps in key order, the forced
        /// sweeps A8 starts (TrackOrderedStartupSweep,
        /// mad68pr_backend.cpp:1115-1141).</summary>
        private void TrackOrderedStartupSweep(int keyIndex, long now)
        {
            if (_activationEpochMs == 0) return;
            uint pos = _orderedSweepPosition;
            if (keyIndex == pos)
            {
                pos++;
                if (pos == KeyCount)
                {
                    pos = 0;
                    _orderedSweepCycles++;
                    _lastOrderedSweepMs = now;
                }
                _orderedSweepPosition = pos;
                return;
            }
            _orderedSweepPosition = keyIndex == 0 ? 1u : 0u;
        }

        /// <summary>ConfirmSteadyState (mad68pr_backend.cpp:1046-1067).</summary>
        private void ConfirmSteadyState()
        {
            if (!_steadyStateConfirmed)
            {
                _steadyStateConfirmed = true;
                _activationEpochMs = 0;
                _orderedSweepPosition = 0;
            }
            if (HasCompleteSnapshot() && _publishMode == Mad68PublishMode.EmergencyWasd)
                SetPublishMode(Mad68PublishMode.Full);
        }

        /// <summary>MaybeConfirmSteadyStateFromPhysicalEdge
        /// (mad68pr_backend.cpp:1069-1084).</summary>
        private void MaybeConfirmSteadyStateFromPhysicalEdge(long eventMs, long sampleMs, bool correlatedValue)
        {
            long forcedUntil = _forcedSweepGraceUntilMs;
            long lastOrdered = _lastOrderedSweepMs;
            bool startupSweepQuiet = lastOrdered == 0 || sampleMs < lastOrdered || sampleMs - lastOrdered >= 150;
            if (eventMs >= forcedUntil && sampleMs >= eventMs && startupSweepQuiet && correlatedValue)
                ConfirmSteadyState();
        }

        /// <summary>MaybeConfirmSteadyStateFromAnalogOnlyEdge
        /// (mad68pr_backend.cpp:1086-1113).</summary>
        private void MaybeConfirmSteadyStateFromAnalogOnlyEdge(long sampleMs, int previousRaw, Mad68KeySample sample)
        {
            if (_steadyStateConfirmed) return;
            if (_orderedSweepCycles < 3) return;
            if (!HasCompleteSnapshot()) return;
            long forcedUntil = _forcedSweepGraceUntilMs;
            if (forcedUntil == 0 || sampleMs < forcedUntil) return;
            long lastOrdered = _lastOrderedSweepMs;
            if (lastOrdered != 0 && sampleMs >= lastOrdered && sampleMs - lastOrdered < 150) return;
            if (!Mad68ProRProtocol.IsPostSweepAnalogProof(previousRaw, sample.Raw, sample.Threshold)) return;
            ConfirmSteadyState();
        }

        private bool AnalogMatchesDigital(int hid, bool expectedDown, int rawAtEvent, int currentRaw)
            => Mad68ProRProtocol.AnalogTransitionMatchesDigital(expectedDown, _threshold[hid], rawAtEvent, currentRaw);

        private void SetPublishMode(Mad68PublishMode mode) => _publishMode = mode;

        private static bool ModeOwnsHid(Mad68PublishMode mode, int hid) => mode switch
        {
            Mad68PublishMode.Full => Mad68ProRProtocol.IsPublishedHid(hid),
            Mad68PublishMode.EmergencyWasd => Mad68ProRProtocol.IsWasdHid(hid),
            _ => false,
        };

        private bool HasCompleteSnapshot() => _coverage == KeyCount;

        private bool HasWasdSnapshot()
        {
            foreach (int hid in Mad68ProRProtocol.WasdHids)
            {
                int k = Mad68ProRProtocol.KeyIndexFromHid(hid);
                if (k < 0 || _descriptorSeq[k] == 0) return false;
            }
            return true;
        }

        private void CaptureSnapshotBaseline(uint[] baseline) => Array.Copy(_descriptorSeq, baseline, KeyCount);

        private int FreshCoverage(uint[] baseline)
        {
            int fresh = 0;
            for (int k = 0; k < KeyCount; k++)
                if (_descriptorSeq[k] > baseline[k]) fresh++;
            return fresh;
        }

        private int FreshWasdCoverage(uint[] baseline)
        {
            int fresh = 0;
            foreach (int hid in Mad68ProRProtocol.WasdHids)
            {
                int k = Mad68ProRProtocol.KeyIndexFromHid(hid);
                if (k >= 0 && _descriptorSeq[k] > baseline[k]) fresh++;
            }
            return fresh;
        }

        /// <summary>ResetSessionPublished (mad68pr_backend.cpp:1860-1890).
        /// The digital side survives it, as in HallJoy.</summary>
        private void ResetSessionPublished()
        {
            _publishMode = Mad68PublishMode.None;
            _lastA0Ms = 0;
            _forcedSweepGraceUntilMs = 0;
            _coverage = 0;
            _steadyStateConfirmed = false;
            _orderedSweepCycles = 0;
            _orderedSweepPosition = 0;
            _activationEpochMs = 0;
            _lastOrderedSweepMs = 0;
            Array.Clear(_raw);
            Array.Clear(_milli);
            Array.Clear(_threshold);
            Array.Clear(_baseline);
            Array.Clear(_keyState);
            Array.Clear(_sampleMs);
            Array.Clear(_sampleSeq);
            Array.Clear(_descriptorRaw);
            Array.Clear(_descriptorSeq);
            Array.Clear(_sampleSeqAtDigitalEvent);
            Array.Clear(_rawAtDigitalEvent);
        }

        // ─── The digital side ───

        private bool AnyKeyDown()
        {
            for (int k = 0; k < KeyCount; k++)
            {
                int hid = _hidByKey[k];
                if (hid != 0 && _physicalDown[hid]) return true;
            }
            return false;
        }

        /// <summary>Turns this keyboard's held keys into the edges HallJoy's
        /// Raw Input callback delivers, one edge per change seen.</summary>
        private void PollHeldKeys(Func<int, bool> isHeld, long now)
        {
            if (isHeld == null) return;
            for (int k = 0; k < KeyCount; k++)
            {
                int hid = _hidByKey[k];
                if (hid == 0) continue;
                bool held;
                try { held = isHeld(hid); }
                catch { held = false; }
                if (held != _physicalDown[hid]) NotifyKeyboardEvent(hid, held, now);
            }
        }

        /// <summary>Mad68ProR_NotifyKeyboardEvent (mad68pr_backend.cpp:2446-2469):
        /// the analog snapshot at the edge, then the digital sequence.</summary>
        private void NotifyKeyboardEvent(int hid, bool isKeyDown, long eventMs)
        {
            uint sampleSeqAtEdge = _sampleSeq[hid];
            int rawAtEdge = _raw[hid];
            bool previous = _physicalDown[hid];
            _physicalDown[hid] = isKeyDown;
            if (previous == isKeyDown) return;
            if (!Mad68ProRProtocol.IsPublishedHid(hid)) return;
            _rawInputEdges++;
            _digitalDown[hid] = isKeyDown;
            _digitalMs[hid] = eventMs;
            _sampleSeqAtDigitalEvent[hid] = sampleSeqAtEdge;
            _rawAtDigitalEvent[hid] = rawAtEdge;
            _digitalSeq[hid]++;
        }

        /// <summary>SynchroniseDigitalWatch (mad68pr_backend.cpp:1000-1015).</summary>
        private void SynchroniseDigitalWatch(bool clearFailureStreak)
        {
            for (int k = 0; k < KeyCount; k++)
            {
                int hid = _hidByKey[k];
                if (hid == 0 || hid >= 256) continue;
                _watchSeenSeq[hid] = _digitalSeq[hid];
                _watchPending[hid] = false;
                _watchExpectedDown[hid] = false;
                _watchEventMs[hid] = 0;
                _watchSampleSeqAtEvent[hid] = _sampleSeq[hid];
                _watchRawAtEvent[hid] = _raw[hid];
                _watchDeadlineMs[hid] = 0;
                _watchLiveStreamExtension[hid] = false;
                if (clearFailureStreak) _watchFailureStreak[hid] = 0;
            }
        }

        /// <summary>
        /// ObserveDigitalEvents (mad68pr_backend.cpp:1649-1858): after an edge
        /// on a served key a fresh A0 is due within 1000 ms (1600 ms inside
        /// the grace window), or 3000 ms while the stream as a whole is alive.
        /// A W, A, S or D miss while no A0 at all arrived for 3000 ms stops
        /// serving and asks for a new activation. HallJoy's reset on a Raw
        /// Input device change of the target keyboard has no counterpart
        /// here, since the session ends when the keyboard leaves.
        /// </summary>
        private void ObserveDigitalEvents(long now)
        {
            var mode = _publishMode;
            if (mode == Mad68PublishMode.None)
            {
                SynchroniseDigitalWatch(clearFailureStreak: false);
                return;
            }

            for (int k = 0; k < KeyCount; k++)
            {
                int hid = _hidByKey[k];
                if (hid == 0 || hid >= 256 || !ModeOwnsHid(mode, hid)) continue;

                uint seq = _digitalSeq[hid];
                if (seq != _watchSeenSeq[hid])
                {
                    _watchSeenSeq[hid] = seq;
                    bool down = _digitalDown[hid];
                    long eventMs = _digitalMs[hid];
                    int rawAtEvent = _rawAtDigitalEvent[hid];
                    uint sampleSeqAtEvent = _sampleSeqAtDigitalEvent[hid];
                    long sampleMs = _sampleMs[hid];
                    uint sampleSeq = _sampleSeq[hid];

                    bool alreadyPostEdgeSample = sampleSeq > sampleSeqAtEvent && sampleMs >= eventMs;
                    bool recentLeadingSample = sampleMs != 0 && eventMs >= sampleMs
                        && eventMs - sampleMs <= DigitalLeadToleranceMs;
                    bool leadingValueMatches = recentLeadingSample
                        && AnalogMatchesDigital(hid, down, rawAtEvent, rawAtEvent);
                    if (alreadyPostEdgeSample)
                    {
                        _watchPending[hid] = false;
                        _watchFailureStreak[hid] = 0;
                        int postEdgeRaw = _raw[hid];
                        bool postEdgeValueMatches = AnalogMatchesDigital(hid, down, rawAtEvent, postEdgeRaw);
                        int postEdgeDelta = Math.Abs(rawAtEvent - postEdgeRaw);
                        MaybeConfirmSteadyStateFromPhysicalEdge(eventMs, sampleMs,
                            postEdgeValueMatches || postEdgeDelta >= 8);
                    }
                    else if (leadingValueMatches)
                    {
                        _watchPending[hid] = false;
                        _watchFailureStreak[hid] = 0;
                    }
                    else
                    {
                        _watchPending[hid] = true;
                        _watchExpectedDown[hid] = down;
                        _watchEventMs[hid] = eventMs;
                        _watchSampleSeqAtEvent[hid] = sampleSeqAtEvent;
                        _watchRawAtEvent[hid] = rawAtEvent;
                        _watchDeadlineMs[hid] = eventMs < _forcedSweepGraceUntilMs
                            ? ForcedSweepDigitalDeadlineMs
                            : DigitalAnalogDeadlineMs;
                        _watchLiveStreamExtension[hid] = false;
                    }
                }

                if (!_watchPending[hid]) continue;
                uint currentSampleSeq = _sampleSeq[hid];
                int currentRaw = _raw[hid];
                if (currentSampleSeq > _watchSampleSeqAtEvent[hid])
                {
                    bool valueMatches = AnalogMatchesDigital(hid, _watchExpectedDown[hid], _watchRawAtEvent[hid], currentRaw);
                    int edgeDelta = Math.Abs(_watchRawAtEvent[hid] - currentRaw);
                    MaybeConfirmSteadyStateFromPhysicalEdge(_watchEventMs[hid], _sampleMs[hid],
                        valueMatches || edgeDelta >= 8);
                    _watchPending[hid] = false;
                    _watchFailureStreak[hid] = 0;
                    continue;
                }

                int baseDeadline = _watchDeadlineMs[hid] != 0 ? _watchDeadlineMs[hid] : DigitalAnalogDeadlineMs;
                long elapsed = now >= _watchEventMs[hid] ? now - _watchEventMs[hid] : 0;
                if (!_watchLiveStreamExtension[hid] && elapsed >= baseDeadline)
                {
                    long lastA0 = _lastA0Ms;
                    bool globalStreamAlive = lastA0 != 0 && now >= lastA0 && now - lastA0 <= GlobalStreamAliveMs;
                    if (globalStreamAlive) _watchLiveStreamExtension[hid] = true;
                }

                int effectiveDeadline = _watchLiveStreamExtension[hid]
                    ? Math.Max(baseDeadline, SchedulerStarvationDeadlineMs)
                    : baseDeadline;
                if (elapsed < effectiveDeadline) continue;

                _watchFailureStreak[hid] = Math.Min(255, _watchFailureStreak[hid] + 1);
                _watchPending[hid] = false;
                _watchLiveStreamExtension[hid] = false;
                if (!Mad68ProRProtocol.IsWasdHid(hid)) continue;
                long last = _lastA0Ms;
                bool globalStreamDead = last == 0 || now < last || now - last > SchedulerStarvationDeadlineMs;
                if (globalStreamDead)
                {
                    SetPublishMode(Mad68PublishMode.None);
                    _recoveryHid = hid;
                    _recoveryRequested = true;
                }
            }
        }

        /// <summary>Mad68ProR_OwnsHid (mad68pr_backend.cpp:2568-2601): served
        /// by the mode, seen at least once, and either no edge ever, an A0
        /// after the latest edge, or an A0 that led the edge by at most 250 ms
        /// and already agrees with it.</summary>
        private bool OwnsHid(int hid)
        {
            if (hid == 0 || hid >= 256 || !ModeOwnsHid(_publishMode, hid)) return false;
            uint sampleSeq = _sampleSeq[hid];
            if (sampleSeq == 0) return false;
            if (_digitalSeq[hid] == 0) return true;
            if (sampleSeq > _sampleSeqAtDigitalEvent[hid]) return true;
            long sampleMs = _sampleMs[hid];
            long digitalMs = _digitalMs[hid];
            if (sampleMs > digitalMs) return false;
            if (digitalMs - sampleMs > DigitalLeadToleranceMs) return false;
            int raw = _raw[hid];
            return AnalogMatchesDigital(hid, _digitalDown[hid], raw, raw);
        }

        /// <summary>The served keys at their depths (Mad68ProR_GetMilli,
        /// mad68pr_backend.cpp:2603-2607). True when that changed the output.</summary>
        private bool Publish(AnalogKeyInputState output)
        {
            _scratch.ResetForReuse();
            for (int k = 0; k < KeyCount; k++)
            {
                int hid = _hidByKey[k];
                if (hid != 0 && OwnsHid(hid)) _scratch.Set(hid, _milli[hid] / 1000f);
            }
            if (_scratch.SameAs(output)) return false;
            _scratch.CopyInto(output);
            return true;
        }
    }
}
