using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>
    /// NuPhy's HE keyboards and Madlions' Nano 68 Pro (issue #468). Both run
    /// one OEM protocol on the collection at Usage Page 0x0001 / Usage 0x0000:
    /// 64-byte command frames that start with 0x55, replies that start with
    /// 0xAA, and unsolicited 0xA0 events that carry one key's travel each. The
    /// keyboard sends the events only while the debugMode bit of its function
    /// data is set, so a session sets that bit on start and puts it back on
    /// stop.
    ///
    /// <para>Sources, cited by file and line or by character offset:
    /// NuPhy's configurator NuPhyIO (https://drive.nuphy.io/static/js/main.23dc78ef.js,
    /// SHA-256 8b8fe5fd...27fba, "raw offset" counts characters of that file),
    /// the NuPhyIO command traffic captured on an Air75 HE in
    /// calamity-inc/Soup#156, nisayera/AnalogKeys at abaf1e4 (hardware-verified
    /// on the Nano 68 Pro 373B:106B), the two public 0xA0 captures in
    /// AnalogSense/universal-analog-plugin#31, Soup's AnalogueKeyboard.cpp and
    /// HallJoy's MAD 68 Pro R notes.</para>
    /// </summary>
    public static class NuPhyRoutes
    {
        /// <summary>The embedded data file with the model catalogs and the
        /// Nano 68 Pro key table.</summary>
        public const string DataFile = "nuphy.json";

        public const ushort NuPhyVendorId = 0x19F5;
        public const ushort MadlionsVendorId = 0x373B;

        /// <summary>Windows report length of the vendor collection: the report
        /// ID byte, then the 64-byte frame, for input and output alike
        /// (AnalogKeys docs/protocol.md:21-28). NuPhyIO writes every command as
        /// a 64-byte report with ID 0 (raw offset 592597).</summary>
        public const int WindowsReportLength = 1 + NuPhyProtocol.FrameLength;

        /// <summary>HidD_SetNumInputBuffers for the event stream: HallJoy's
        /// value for the same A0 stream (mad68pr_backend.cpp:716), room for the
        /// full scan a keyboard sends when the stream starts.</summary>
        public const int InputBufferCount = 256;

        /// <summary>Madlions product IDs other routes own: HallJoy's MAD 68 Pro
        /// R (373B:1109) and the Soup Madlions family on the VIA channel
        /// (AnalogKeyboardCatalog.MadlionsLayout). This route never claims them,
        /// whatever the data file lists.</summary>
        public static readonly IReadOnlySet<ushort> MadlionsPidsOwnedElsewhere = new HashSet<ushort>
        {
            0x1109,
            0x1053, 0x1054, 0x1055, 0x1056, 0x105D,
            0x1058, 0x1059, 0x105A, 0x105C, 0x10A7,
        };

        /// <summary>Every NuPhy HE keyboard in NuPhyIO's device catalog (raw
        /// offset 173948 onward, entries with usage 0, usage page 1 and the
        /// keyboard capability). The mechanical boards in the same catalog use
        /// another protocol class and are not listed.</summary>
        public static readonly AnalogKeyboardRoute NuPhyHe = new()
        {
            Id = "nuphy-he",
            Protocol = AnalogKeyboardProtocol.NuPhy,
            Matches = MatchesNuPhyHe,
            CreateSession = info => NuPhyHeModel.Find(info.ProductId) is { } model ? new NuPhyHeSession(model) : null,
            Writable = true,
            InputBuffers = InputBufferCount,
            // A keyboard that did not answer (a WH80 dongle whose keyboard
            // sleeps) is asked again. One that failed after a write is not.
            // Every Start and Stop writes the keyboard's function data, and
            // NuPhyIO reconnects only when its page asks, so a row that stops
            // waits the default minute rather than a short timer.
            StartRetryMs = 5000,
            StopTimeoutMs = NuPhyStreamSession.StopTimeoutMs,
            Name = info => NuPhyHeModel.Find(info.ProductId)?.Name,
        };

        /// <summary>The Madlions boards AnalogKeys reads: every product ID
        /// its catalog lists (analogkeys/protocol.py:28-61) but the MAD 68 Pro
        /// on 1109, which HallJoy's MAD 68 Pro R route owns. The seven it
        /// names Nano 68 Pro have its recorded key table. On the others
        /// AnalogKeys learns each key by having it pressed, and here each key
        /// is published by its firmware index.</summary>
        public static readonly AnalogKeyboardRoute MadlionsA0 = new()
        {
            Id = "madlions-a0",
            Protocol = AnalogKeyboardProtocol.NuPhy,
            Matches = MatchesMadlionsA0,
            CreateSession = info => MadlionsA0Model.Find(info.ProductId) is { } model ? new MadlionsA0Session(model) : null,
            Writable = true,
            InputBuffers = InputBufferCount,
            // As NuPhyHe: AnalogKeys writes the settings block at every
            // connect and exit, so a row that stops waits the default minute.
            StartRetryMs = 5000,
            StopTimeoutMs = NuPhyStreamSession.StopTimeoutMs,
            Name = info => MadlionsA0Model.Find(info.ProductId)?.Name,
            Keys = info => (int[])MadlionsA0Model.Find(info.ProductId)?.KeyOrder.Clone(),
        };

        /// <summary>The routes in priority order. NuPhyHe takes the place of
        /// the Soup NuPhy route. MadlionsA0 goes after HallJoy's Madlions
        /// routes.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All { get; } = new[] { NuPhyHe, MadlionsA0 };

        /// <summary>The vendor collection of a catalogued NuPhy HE keyboard:
        /// NuPhyIO's filter (usage page 1, usage 0), 64-byte reports without a
        /// report ID in both directions.</summary>
        public static bool MatchesNuPhyHe(AnalogKeyboardDeviceInfo info)
            => info != null && info.VendorId == NuPhyVendorId
               && NuPhyHeModel.Find(info.ProductId) != null
               && IsVendorCollection(info);

        /// <summary>The vendor collection of a Nano 68 Pro, chosen by usage the
        /// way AnalogKeys chooses it (analogkeys/device.py:27-40).</summary>
        public static bool MatchesMadlionsA0(AnalogKeyboardDeviceInfo info)
            => info != null && info.VendorId == MadlionsVendorId
               && !MadlionsPidsOwnedElsewhere.Contains(info.ProductId)
               && MadlionsA0Model.Find(info.ProductId) != null
               && IsVendorCollection(info);

        private static bool IsVendorCollection(AnalogKeyboardDeviceInfo info)
            => info.UsagePage == 0x0001 && info.Usage == 0x0000
               && info.InputReportLength == WindowsReportLength
               && info.OutputReportLength == WindowsReportLength
               && info.HasInputReport != null && info.HasInputReport(0)
               && info.HasOutputReport != null && info.HasOutputReport(0);
    }

    /// <summary>One NuPhy HE model from NuPhyIO's catalog.</summary>
    public sealed class NuPhyHeModel
    {
        private static readonly Lazy<Dictionary<ushort, NuPhyHeModel>> _byPid = new(Load);

        private NuPhyHeModel(ushort productId, string type, string name, int micronsPerCount)
        {
            ProductId = productId;
            Type = type;
            Name = name;
            MicronsPerCount = micronsPerCount;
        }

        public ushort ProductId { get; }

        /// <summary>NuPhyIO's model type name.</summary>
        public string Type { get; }

        /// <summary>The row's name, or null where NuPhyIO names no retail
        /// model (PID 0x6131, "Type2nd") and the product string names it.</summary>
        public string Name { get; }

        /// <summary>The model's defaultPrecision in micrometers: one travel
        /// count is 10 um, or 20 um on the Field75 HE and the Halo65 HE.</summary>
        public int MicronsPerCount { get; }

        public static IReadOnlyCollection<NuPhyHeModel> All => _byPid.Value.Values;

        public static NuPhyHeModel Find(ushort productId)
            => _byPid.Value.TryGetValue(productId, out var model) ? model : null;

        private static Dictionary<ushort, NuPhyHeModel> Load()
        {
            var result = new Dictionary<ushort, NuPhyHeModel>();
            foreach (var row in AnalogKeyboardData.File(NuPhyRoutes.DataFile).GetProperty("nuphyHeModels").EnumerateArray())
            {
                var name = row.GetProperty("name");
                var model = new NuPhyHeModel(
                    (ushort)row.GetProperty("pid").GetInt32(),
                    row.GetProperty("type").GetString(),
                    name.ValueKind == JsonValueKind.Null ? null : name.GetString(),
                    row.GetProperty("micronsPerCount").GetInt32());
                result[model.ProductId] = model;
            }
            return result;
        }
    }

    /// <summary>One Madlions board with an AnalogKeys key table.</summary>
    public sealed class MadlionsA0Model
    {
        private static readonly Lazy<Dictionary<ushort, MadlionsA0Model>> _byPid = new(Load);

        private MadlionsA0Model(ushort productId, string name, string tableName, int[] table, int[] keyOrder)
        {
            ProductId = productId;
            Name = name;
            TableName = tableName;
            Table = table;
            KeyOrder = keyOrder;
        }

        public ushort ProductId { get; }
        public string Name { get; }
        public string TableName { get; }

        /// <summary>Key code by the firmware key index an event carries in
        /// byte 3, from AnalogKeys' examples/keymap.example.json, or null for
        /// a board with no recorded table, whose keys publish by index.
        /// Modifiers are not in it: they arrive as a bit mask in byte 2.</summary>
        public int[] Table { get; }

        /// <summary>The modifier keys every board sends in byte 2, the key
        /// list of a board without a table until its keys are pressed.</summary>
        private static readonly int[] Modifiers =
        {
            AnalogKeyCodes.LCtrl, AnalogKeyCodes.LShift, AnalogKeyCodes.LAlt, AnalogKeyCodes.LMeta,
            AnalogKeyCodes.RCtrl, AnalogKeyCodes.RShift, AnalogKeyCodes.RAlt, AnalogKeyCodes.RMeta,
        };

        /// <summary>The board's keys in the physical order of AnalogKeys'
        /// layout (analogkeys/layout.py:28-57), modifiers included.</summary>
        public int[] KeyOrder { get; }

        public static IReadOnlyCollection<MadlionsA0Model> All => _byPid.Value.Values;

        public static MadlionsA0Model Find(ushort productId)
            => _byPid.Value.TryGetValue(productId, out var model) ? model : null;

        private static Dictionary<ushort, MadlionsA0Model> Load()
        {
            var result = new Dictionary<ushort, MadlionsA0Model>();
            foreach (var row in AnalogKeyboardData.File(NuPhyRoutes.DataFile).GetProperty("madlionsA0Models").EnumerateArray())
            {
                string tableName = row.GetProperty("table").ValueKind == System.Text.Json.JsonValueKind.String
                    ? row.GetProperty("table").GetString()
                    : null;
                int[] table = null;
                int[] order = Modifiers;
                if (tableName != null)
                {
                    table = AnalogKeyboardData.Table(NuPhyRoutes.DataFile, tableName);
                    order = AnalogKeyboardData.Table(NuPhyRoutes.DataFile, tableName + "Order");
                    if (table == null || order == null) continue;
                }
                var model = new MadlionsA0Model(
                    (ushort)row.GetProperty("pid").GetInt32(),
                    row.GetProperty("name").GetString(),
                    tableName, table, order);
                result[model.ProductId] = model;
            }
            return result;
        }
    }

    /// <summary>
    /// Frames, replies and events of the protocol. Pure functions over the
    /// 64-byte frame, the bytes after the report ID Windows puts in front.
    /// </summary>
    public static class NuPhyProtocol
    {
        public const int FrameLength = 64;

        /// <summary>Byte 0 of a frame: command (host to keyboard), reply,
        /// checksum-error reply, and event. NuPhyIO's report-type enum
        /// (raw offset 1541394) and AnalogKeys analogkeys/protocol.py:70-72.
        /// 0xAB is the checksum-error reply HallJoy's firmware audit of the MAD
        /// 68 Pro R documents (MAD68_PRO_R_FIRMWARE_FINAL_AUDIT.md:118-119).</summary>
        public const byte CommandHeader = 0x55;
        public const byte ReplyHeader = 0xAA;
        public const byte ChecksumErrorHeader = 0xAB;
        public const byte EventHeader = 0xA0;

        /// <summary>GetFunc and SetFunc, NuPhyIO's command enum (raw offset
        /// 1877596) and AnalogKeys analogkeys/protocol.py:75-76.</summary>
        public const byte GetFunc = 0x05;
        public const byte SetFunc = 0x06;

        /// <summary>Command data starts at byte 8 and holds at most 56 bytes
        /// (NuPhyIO constants, raw offset 1877500).</summary>
        public const int DataIndex = 8;
        public const int MaxDataLength = 56;

        /// <summary>Three modes of 64 function-data bytes each (raw offset
        /// 1877558).</summary>
        public const int ModeCount = 3;
        public const int ModeStride = 64;

        /// <summary>Function-data byte 7 holds debugMode in bit 3: NuPhyIO's
        /// dataToConfig reads the flag from bit 3 of that byte (raw offset
        /// 1652698), and AnalogKeys sets bit 3 of frame byte 15, the same byte
        /// (analogkeys/protocol.py:81-84).</summary>
        public const int FlagsIndex = 7;
        public const byte DebugModeBit = 0x08;

        /// <summary>The shortest event any reference parses: NuPhyIO rejects
        /// shorter records ("Invalid data length", raw offset 1742471 region),
        /// AnalogKeys too (analogkeys/protocol.py:175).</summary>
        public const int EventMinimumLength = 20;

        /// <summary>Byte 3 of a command: the low byte of the sum of bytes 4 to
        /// 63 (NuPhyIO createCommand, raw offset 1882211, and AnalogKeys
        /// analogkeys/protocol.py:87-89).</summary>
        public static byte Checksum(ReadOnlySpan<byte> frame)
        {
            int sum = 0;
            int end = Math.Min(frame.Length, FrameLength);
            for (int i = 4; i < end; i++) sum += frame[i];
            return (byte)sum;
        }

        /// <summary>A command as the Windows buffer WriteFile takes: report ID
        /// 0, then 0x55, the command, 0, the checksum, the data length, the
        /// little-endian offset and 0, then the data from byte 8. NuPhyIO's
        /// createCommand byte for byte (raw offset 1882211).</summary>
        public static byte[] Command(byte command, int length, int offset, ReadOnlySpan<byte> data = default)
        {
            if (data.Length > MaxDataLength) throw new ArgumentOutOfRangeException(nameof(data));
            var report = new byte[1 + FrameLength];
            var frame = report.AsSpan(1);
            frame[0] = CommandHeader;
            frame[1] = command;
            frame[4] = (byte)length;
            frame[5] = (byte)(offset & 0xFF);
            frame[6] = (byte)((offset >> 8) & 0xFF);
            data.CopyTo(frame.Slice(DataIndex));
            frame[3] = Checksum(frame);
            return report;
        }

        /// <summary>What a received frame is to a command that is waiting.</summary>
        public enum ReplyKind
        {
            /// <summary>Anything else: an event, or another command's reply.</summary>
            Other,
            /// <summary>The reply to the command.</summary>
            Reply,
            /// <summary>The keyboard rejected the command's checksum.</summary>
            ChecksumError,
        }

        /// <summary>
        /// Whether <paramref name="frame"/> answers the command
        /// <paramref name="command"/> at <paramref name="offset"/>. NuPhyIO
        /// routes 0xAA reports to the command in flight (raw offset 590153) and
        /// accepts one whose byte 1 is the command's (raw offset 594011).
        /// AnalogKeys also wants a full 64-byte frame
        /// (analogkeys/protocol.py:107-108). The offset in bytes 5 and 6 must
        /// match too, or a reply for another mode would pass as this one. The
        /// keyboard echoes the command's header: AnalogKeys saw it on the Nano
        /// 68 Pro (docs/protocol.md:34-38), and NuPhyIO's own simulated HE
        /// keyboard answers GetFunc and SetFunc with command, key, length and
        /// both offset bytes copied from the request (module 65843, raw offsets
        /// 1977544 and 1977805).
        /// </summary>
        public static ReplyKind ClassifyReply(ReadOnlySpan<byte> frame, byte command, int offset)
        {
            if (frame.Length < FrameLength || frame[1] != command) return ReplyKind.Other;
            if ((frame[5] | (frame[6] << 8)) != offset) return ReplyKind.Other;
            if (frame[0] == ReplyHeader) return ReplyKind.Reply;
            if (frame[0] == ChecksumErrorHeader) return ReplyKind.ChecksumError;
            return ReplyKind.Other;
        }

        /// <summary>Whether a frame is an event long enough to parse.</summary>
        public static bool IsEvent(ReadOnlySpan<byte> frame)
            => frame.Length >= EventMinimumLength && frame[0] == EventHeader;

        /// <summary>Bytes 6 and 7 of an event, big-endian: the linearized
        /// travel in counts of the model's precision. NuPhyIO's
        /// parseCalibrationStatusBuffer reads the same two bytes (raw offset
        /// 1742572), AnalogKeys the same field in 0.01 mm
        /// (analogkeys/protocol.py:190).</summary>
        public static int Travel(ReadOnlySpan<byte> frame) => (frame[6] << 8) | frame[7];

        /// <summary>Bytes 14 and 15 of an event, big-endian. No reference
        /// parser reads them. In both public NuPhy captures they hold the full
        /// travel count (330 on the Air75 HE, 341 on the WH80) in every row,
        /// and the travel field tops out at exactly that value.</summary>
        public static int FullTravel(ReadOnlySpan<byte> frame) => (frame[14] << 8) | frame[15];

        /// <summary>The key a modifier bit stands for, the HID bit order
        /// NuPhyIO's key decoder uses (module 88352, raw offset 1882749) and
        /// AnalogKeys lists (analogkeys/protocol.py:143-146). 0 for anything
        /// but a single bit.</summary>
        public static int ModifierCode(int bit) => bit switch
        {
            0x01 => AnalogKeyCodes.LCtrl,
            0x02 => AnalogKeyCodes.LShift,
            0x04 => AnalogKeyCodes.LAlt,
            0x08 => AnalogKeyCodes.LMeta,
            0x10 => AnalogKeyCodes.RCtrl,
            0x20 => AnalogKeyCodes.RShift,
            0x40 => AnalogKeyCodes.RAlt,
            0x80 => AnalogKeyCodes.RMeta,
            _ => AnalogKeyCodes.None,
        };

        /// <summary>
        /// A NuPhy event's key from bytes 1 to 3 (type, code0, code1), the way
        /// NuPhyIO's decoder reads them (module 88352): for type 0x10 a zero
        /// code0 makes code1 a keyboard-page usage, and a zero code1 makes
        /// code0 a modifier bit. Both codes set, or neither, is no key.
        /// Soup's code0 0xFF, code1 0x05 is Fn (Soup AnalogueKeyboard.cpp:1087,
        /// AnalogSense.js line 75), whatever the type byte: Soup never reads
        /// it, and NuPhyIO's key table calls 0xF0FF05 KC_FN5. Other types are
        /// consumer, mouse, macro and layer functions, not keys this route
        /// reports. Usages outside 0x04 to 0xE7 are no key either: NuPhyIO
        /// accepts only codes the board's default matrix holds.
        /// </summary>
        public static int NuPhyKeyCode(byte type, byte code0, byte code1)
        {
            if (code0 == 0xFF && code1 == 0x05) return AnalogKeyCodes.Fn;
            if (type != 0x10) return AnalogKeyCodes.None;
            if (code0 == 0 && code1 != 0)
                return code1 >= 0x04 && code1 <= 0xE7 ? code1 : AnalogKeyCodes.None;
            if (code0 != 0 && code1 == 0) return ModifierCode(code0);
            return AnalogKeyCodes.None;
        }

        /// <summary>
        /// A NuPhy event's depth. NuPhyIO's Performance page shows the travel
        /// times defaultPrecision, over the switch's full travel, capped at
        /// 100% (chunk 686.bc0a9e94, offset 23173). Bytes 14 and 15 carry the
        /// full travel in counts, so the travel over that count is the same
        /// fraction without a switch table. When they read 0 the depth falls
        /// back to the travel in millimeters over 4.0 mm, the longest switch in
        /// NuPhyIO's switch table, which keeps every switch below full scale. A
        /// travel of 0 is the key's release.
        /// </summary>
        public static float NuPhyDepth(int travel, int fullTravel, int micronsPerCount)
        {
            if (travel <= 0) return 0f;
            float depth = fullTravel > 0
                ? travel / (float)fullTravel
                : travel * micronsPerCount / FallbackFullTravelMicrons;
            return depth > 1f ? 1f : depth;
        }

        /// <summary>4.0 mm, NuPhyIO's longest switches: Gateron White 2.0 and
        /// Magnetic Coral, totalLine 4 in the switch table (raw offsets 165854
        /// and 166070).</summary>
        public const float FallbackFullTravelMicrons = 4000f;

        /// <summary>
        /// A Madlions event's key, AnalogKeys' parse_event
        /// (analogkeys/protocol.py:173-185): a nonzero byte 2 must be one
        /// modifier bit, and any other mask is a status frame, not a key.
        /// Otherwise byte 3 is the firmware key index, looked up in the board's
        /// table, or on a board without one published as a key position.
        /// Byte 1 is not read, as AnalogKeys does not read it.
        /// </summary>
        public static int MadlionsKeyCode(byte mask, byte index, int[] table)
        {
            if (mask != 0) return ModifierCode(mask);
            if (table == null) return index != 0 ? AnalogKeyCodes.PositionBase + index : AnalogKeyCodes.None;
            return index < table.Length ? table[index] : AnalogKeyCodes.None;
        }

        /// <summary>AnalogKeys' depth for a key it has not calibrated
        /// (analogkeys/calibration.py:17-40): travel in 0.01 mm less a 4-count
        /// top dead zone, over 345 less both dead zones (4 and 8), clamped to
        /// 0..1. Keys bottom out between 338 and 352 (docs/protocol.md:135-138).</summary>
        public static float MadlionsDepth(int travel)
        {
            const int nominalMax = 345, topDead = 4, bottomDead = 8;
            int span = Math.Max(1, nominalMax - bottomDead - topDead);
            float depth = (travel - topDead) / (float)span;
            return depth <= 0f ? 0f : depth > 1f ? 1f : depth;
        }
    }

    /// <summary>
    /// What both sessions share: the command exchange, and the event stream.
    /// Events that arrive while Start waits for replies (the keyboard scans
    /// every key once as the stream starts, AnalogKeys docs/protocol.md:130-131)
    /// are kept and published by the first pass, as NuPhyIO hands every
    /// non-reply report to its event parser while a command is in flight
    /// (raw offset 590153).
    /// </summary>
    public abstract class NuPhyStreamSession : AnalogKeyboardSession
    {
        /// <summary>Longest single wait of a pass, as PushedReportSession, so
        /// the reader sees a stop request.</summary>
        public const int WaitMs = PushedReportSession.WaitMs;

        /// <summary>How long Stop's reads and retries may take in all: three
        /// modes of NuPhyHe's read and write, four tries of 200 ms each, is
        /// 4.8 s when every reply is lost. A write still owed when the budget
        /// runs out is sent once anyway, since it is the undo.</summary>
        public const int StopBudgetMs = 5000;

        /// <summary>The route's stop budget: <see cref="StopBudgetMs"/>, a
        /// last write of up to 1 s for each of the three modes, and the pass
        /// in flight.</summary>
        public const int StopTimeoutMs = StopBudgetMs + 3 * 1000 + 1000;

        /// <summary>Most reports one drain reads before it gives up, so a
        /// stream that never pauses cannot hold a command back forever.</summary>
        private const int DrainLimit = 512;

        private readonly AnalogKeyInputState _early = new();
        private bool _earlyUnpublished;

        /// <summary>How an exchange ended.</summary>
        protected enum Outcome
        {
            Answered,
            Unanswered,
            Gone,
        }

        /// <summary>The key and depth an event carries, or false when the
        /// event names no key this session reports.</summary>
        protected abstract bool TryDecodeEvent(ReadOnlySpan<byte> frame, out int code, out float depth);

        /// <summary>Applies one received report to <paramref name="target"/>.
        /// False when it is not an event.</summary>
        public bool Apply(ReadOnlySpan<byte> raw, AnalogKeyInputState target)
        {
            var frame = AnalogKeyboardParsers.StripZeroReportId(raw);
            if (!NuPhyProtocol.IsEvent(frame)) return false;
            if (TryDecodeEvent(frame, out int code, out float depth)) target.Set(code, depth);
            return true;
        }

        /// <summary>One event per report, a travel of 0 releases the key, and
        /// the keys the report does not name keep their depth.</summary>
        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_earlyUnpublished)
            {
                _earlyUnpublished = false;
                for (int i = 0; i < _early.Count; i++) output.Set(_early.Codes[i], _early.Depths[i]);
                _early.ResetForReuse();
                return AnalogPollResult.Ok;
            }
            int n = io.Receive(Buffer, WaitMs);
            if (n < 0) return AnalogPollResult.Failed;
            if (n == 0) return AnalogPollResult.Idle;
            return Apply(Buffer.AsSpan(0, n), output) ? AnalogPollResult.Ok : AnalogPollResult.Idle;
        }

        /// <summary>
        /// Sends <paramref name="request"/> and waits for its reply, one
        /// command in flight at a time: the firmware holds one receive buffer
        /// and no queue (HallJoy MAD68_PRO_R_FIRMWARE_FINAL_AUDIT.md:66-70).
        /// Before each try the reports already queued are read, their events
        /// kept, so a stale reply cannot answer the new command. A try whose
        /// write fails, that gets no reply within <paramref name="replyMs"/>
        /// of the write, or that gets a checksum error is retried, up to
        /// <paramref name="attempts"/> tries in all. The first try is always
        /// sent. Later tries stop at <paramref name="deadline"/>, and so does
        /// the wait for a reply. Only a read that reports the device gone is
        /// <see cref="Outcome.Gone"/>. The reply lands in
        /// <paramref name="reply"/> (64 bytes).
        /// </summary>
        protected Outcome Exchange(IAnalogKeyboardTransport io, byte[] request, int attempts, int replyMs,
            long deadline, byte[] reply)
        {
            byte command = request[2];
            int offset = request[6] | (request[7] << 8);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0 && Environment.TickCount64 >= deadline) break;
                if (!Drain(io)) return Outcome.Gone;
                if (!io.Send(request)) continue;
                long until = Math.Min(deadline, Environment.TickCount64 + replyMs);
                while (true)
                {
                    int remaining = (int)Math.Max(0, until - Environment.TickCount64);
                    if (remaining == 0) break;
                    int n = io.Receive(Buffer, remaining);
                    if (n < 0) return Outcome.Gone;
                    if (n == 0) break;
                    var frame = AnalogKeyboardParsers.StripZeroReportId(Buffer.AsSpan(0, n));
                    var kind = NuPhyProtocol.ClassifyReply(frame, command, offset);
                    if (kind == NuPhyProtocol.ReplyKind.Reply)
                    {
                        frame.Slice(0, NuPhyProtocol.FrameLength).CopyTo(reply);
                        return Outcome.Answered;
                    }
                    if (kind == NuPhyProtocol.ReplyKind.ChecksumError) break;
                    Keep(frame);
                }
            }
            return Outcome.Unanswered;
        }

        /// <summary>Reads every report already queued, keeping the events.
        /// False when the device is gone.</summary>
        private bool Drain(IAnalogKeyboardTransport io)
        {
            for (int i = 0; i < DrainLimit; i++)
            {
                int n = io.Receive(Buffer, 0);
                if (n < 0) return false;
                if (n == 0) return true;
                Keep(AnalogKeyboardParsers.StripZeroReportId(Buffer.AsSpan(0, n)));
            }
            return true;
        }

        private void Keep(ReadOnlySpan<byte> frame)
        {
            if (!NuPhyProtocol.IsEvent(frame)) return;
            if (TryDecodeEvent(frame, out int code, out float depth)) _early.Set(code, depth);
            _earlyUnpublished = true;
        }
    }

    /// <summary>
    /// A NuPhy HE keyboard. Start does what NuPhyIO's startKeyStatusUpload
    /// does (setKeyboardDebug, raw offset 1733856): for each of the three
    /// modes, GetFunc reads the mode's function data (54 bytes at 64 times the
    /// mode, raw offset 1754171) and, when debugMode is clear, SetFunc writes
    /// function-data bytes 4 to 7 back at that offset plus 4 (configToData's
    /// chunk, raw offsets 1653092-1653154 and 1746571) with only the debugMode
    /// bit changed. NuPhyIO writes nothing for a mode whose bit is already set
    /// (setPartialKeyboardFunc, raw offset 1636647). The traffic NuPhyIO sent
    /// an Air75 HE (calamity-inc/Soup#156) is exactly these frames. Stop reads
    /// each changed mode again and puts the bit back as it was, as NuPhyIO's
    /// endKeyStatusUpload does.
    ///
    /// <para>NuPhyIO sends QuickCommStart only around its bulk reads at
    /// connect and closes that bracket with QuickCommEnd before any stream
    /// starts, and AnalogKeys sends neither, so this session sends neither.
    /// The EndCalibration (0xA9) frames in the Soup#156 capture belong to
    /// NuPhyIO's page changes, not to the stream, and are not sent.</para>
    /// </summary>
    public sealed class NuPhyHeSession : NuPhyStreamSession
    {
        /// <summary>NuPhyIO's reply wait (retryDelay, raw offset 593905) and
        /// its tries: maxRetries 3 after the first (raw offset 593887).</summary>
        public const int ReplyWaitMs = 200;
        public const int Attempts = 4;

        /// <summary>NuPhyIO's GetFunc length (raw offset 1754171).</summary>
        public const int FuncReadLength = 54;

        /// <summary>The first function-data byte SetFunc writes, and how many.</summary>
        public const int FlagsWriteOffset = 4;
        public const int FlagsWriteLength = 4;

        private readonly NuPhyHeModel _model;
        private readonly byte[][] _original = new byte[NuPhyProtocol.ModeCount][];
        private readonly bool[] _changed = new bool[NuPhyProtocol.ModeCount];
        private readonly byte[] _reply = new byte[NuPhyProtocol.FrameLength];

        public NuPhyHeSession(NuPhyHeModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public NuPhyHeModel Model => _model;

        public override string ModelName => _model.Name;

        /// <summary>Whether Start set the mode's debugMode bit and Stop has not
        /// put it back yet.</summary>
        public bool ModeChanged(int mode) => _changed[mode];

        /// <summary>GetFunc for one mode's function data.</summary>
        public static byte[] ReadFuncRequest(int mode)
            => NuPhyProtocol.Command(NuPhyProtocol.GetFunc, FuncReadLength, mode * NuPhyProtocol.ModeStride);

        /// <summary>SetFunc of function-data bytes 4 to 7 for one mode.</summary>
        public static byte[] WriteFlagsRequest(int mode, ReadOnlySpan<byte> bytes4To7)
            => NuPhyProtocol.Command(NuPhyProtocol.SetFunc, FlagsWriteLength,
                mode * NuPhyProtocol.ModeStride + FlagsWriteOffset, bytes4To7);

        /// <summary>Function-data bytes 4 to 7 out of a GetFunc reply, whose
        /// data starts at byte 8 (NuPhyIO getKeyboardFunc).</summary>
        public static byte[] FlagsOf(ReadOnlySpan<byte> reply)
            => reply.Slice(NuPhyProtocol.DataIndex + FlagsWriteOffset, FlagsWriteLength).ToArray();

        public override bool Start(IAnalogKeyboardTransport io)
        {
            for (int mode = 0; mode < NuPhyProtocol.ModeCount; mode++)
            {
                var read = Exchange(io, ReadFuncRequest(mode), Attempts, ReplyWaitMs, long.MaxValue, _reply);
                if (read != Outcome.Answered)
                {
                    NoStartRetry = Array.IndexOf(_changed, true) >= 0;
                    Restore(io);
                    return false;
                }
                var flags = FlagsOf(_reply);
                _original[mode] = flags;
                if ((flags[3] & NuPhyProtocol.DebugModeBit) != 0) continue;

                var enabled = (byte[])flags.Clone();
                enabled[3] |= NuPhyProtocol.DebugModeBit;
                // Marked before the write: a write whose reply never came may
                // still have landed, and Stop must then clear it.
                _changed[mode] = true;
                if (Exchange(io, WriteFlagsRequest(mode, enabled), Attempts, ReplyWaitMs, long.MaxValue, _reply)
                    != Outcome.Answered)
                {
                    NoStartRetry = true;
                    Restore(io);
                    return false;
                }
            }
            return true;
        }

        public override void Stop(IAnalogKeyboardTransport io) => Restore(io);

        /// <summary>
        /// Puts each changed mode's debugMode bit back as Start found it. The
        /// mode is read again first, as NuPhyIO's setPartialKeyboardFunc
        /// always reads before it writes, so a setting changed meanwhile keeps
        /// its new value. When the read goes unanswered, nothing is written
        /// for that mode: setPartialKeyboardFunc returns its failure before
        /// any write, and bytes 4 to 7 as Start read them would put back a
        /// report rate, sleep time or key lock changed since. The bit left
        /// set only keeps the 0xA0 stream on.
        /// </summary>
        private void Restore(IAnalogKeyboardTransport io)
        {
            long deadline = Environment.TickCount64 + StopBudgetMs;
            for (int mode = 0; mode < NuPhyProtocol.ModeCount; mode++)
            {
                if (!_changed[mode]) continue;
                _changed[mode] = false;
                byte wanted = (byte)(_original[mode][3] & NuPhyProtocol.DebugModeBit);

                var read = Environment.TickCount64 < deadline
                    ? Exchange(io, ReadFuncRequest(mode), Attempts, ReplyWaitMs, deadline, _reply)
                    : Outcome.Unanswered;
                if (read == Outcome.Gone) return;
                if (read != Outcome.Answered) continue;

                var flags = FlagsOf(_reply);
                if ((flags[3] & NuPhyProtocol.DebugModeBit) == wanted) continue;
                flags[3] = (byte)((flags[3] & ~NuPhyProtocol.DebugModeBit) | wanted);
                if (Exchange(io, WriteFlagsRequest(mode, flags), Attempts, ReplyWaitMs, deadline, _reply) == Outcome.Gone)
                    return;
            }
        }

        protected override bool TryDecodeEvent(ReadOnlySpan<byte> frame, out int code, out float depth)
        {
            code = NuPhyProtocol.NuPhyKeyCode(frame[1], frame[2], frame[3]);
            depth = NuPhyProtocol.NuPhyDepth(NuPhyProtocol.Travel(frame), NuPhyProtocol.FullTravel(frame),
                _model.MicronsPerCount);
            return code != AnalogKeyCodes.None;
        }
    }

    /// <summary>
    /// A Madlions Nano 68 Pro, AnalogKeys' Keyboard (analogkeys/device.py:98-122):
    /// GetFunc reads the 56-byte settings block at address 0, with up to three
    /// tries of 500 ms (device.py:129-147), and nothing is written when it
    /// cannot be read. SetFunc writes the whole block back with only the
    /// debugMode bit set, once (protocol.py:111-129, device.py:149-152). Stop
    /// clears the bit, as AnalogKeys does on exit whatever the bit was
    /// before: a process killed mid-stream leaves the bit set, and the next
    /// clean run clears it (device.py:76-80). AnalogKeys enables every board
    /// its product list names, the unverified ones included.
    ///
    /// <para>Two corrections to AnalogKeys, whose own rule is to read the
    /// block before writing it (docs/protocol.md:85-90). Stop reads the block
    /// again rather than writing back the one read at start, which would undo
    /// every lighting or lock change made on the keyboard meanwhile. It falls
    /// back to the start block only when that read goes unanswered. And the
    /// SetFunc reply is waited for, one command in flight at a time, though a
    /// missing reply does not fail Start, as AnalogKeys never reads one.</para>
    /// </summary>
    public sealed class MadlionsA0Session : NuPhyStreamSession
    {
        /// <summary>AnalogKeys' settings read: three tries of 500 ms
        /// (analogkeys/device.py:129).</summary>
        public const int ReplyWaitMs = 500;
        public const int Attempts = 3;

        /// <summary>SETTINGS_LEN, the 56-byte block at address 0
        /// (analogkeys/protocol.py:78 and 98-104).</summary>
        public const int BlockLength = 0x38;

        private readonly MadlionsA0Model _model;
        private readonly byte[] _reply = new byte[NuPhyProtocol.FrameLength];
        private byte[] _block;
        private bool _clearOnStop;

        public MadlionsA0Session(MadlionsA0Model model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public MadlionsA0Model Model => _model;

        public override string ModelName => _model.Name;

        public override int[] KeyOrder => (int[])_model.KeyOrder.Clone();

        /// <summary>Whether Stop still has the debugMode bit to clear.</summary>
        public bool ClearOnStop => _clearOnStop;

        /// <summary>build_get_func (analogkeys/protocol.py:98-104).</summary>
        public static byte[] ReadBlockRequest()
            => NuPhyProtocol.Command(NuPhyProtocol.GetFunc, BlockLength, 0);

        /// <summary>build_set_func's frame for a block (protocol.py:111-129).</summary>
        public static byte[] WriteBlockRequest(ReadOnlySpan<byte> block)
            => NuPhyProtocol.Command(NuPhyProtocol.SetFunc, BlockLength, 0, block);

        /// <summary>The 56-byte block out of a GetFunc reply.</summary>
        public static byte[] BlockOf(ReadOnlySpan<byte> reply)
            => reply.Slice(NuPhyProtocol.DataIndex, BlockLength).ToArray();

        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (Exchange(io, ReadBlockRequest(), Attempts, ReplyWaitMs, long.MaxValue, _reply) != Outcome.Answered)
                return false;
            _block = BlockOf(_reply);
            _clearOnStop = true;
            if ((_block[NuPhyProtocol.FlagsIndex] & NuPhyProtocol.DebugModeBit) != 0) return true;

            var enabled = (byte[])_block.Clone();
            enabled[NuPhyProtocol.FlagsIndex] |= NuPhyProtocol.DebugModeBit;
            // One write, as AnalogKeys sends it. Only a keyboard that is gone
            // fails Start here.
            if (Exchange(io, WriteBlockRequest(enabled), 1, ReplyWaitMs, long.MaxValue, _reply) == Outcome.Gone)
            {
                NoStartRetry = true;
                Restore(io);
                return false;
            }
            return true;
        }

        public override void Stop(IAnalogKeyboardTransport io) => Restore(io);

        private void Restore(IAnalogKeyboardTransport io)
        {
            if (!_clearOnStop) return;
            _clearOnStop = false;
            long deadline = Environment.TickCount64 + StopBudgetMs;

            var read = Exchange(io, ReadBlockRequest(), Attempts, ReplyWaitMs, deadline, _reply);
            if (read == Outcome.Gone) return;
            byte[] block;
            if (read == Outcome.Answered)
            {
                block = BlockOf(_reply);
                if ((block[NuPhyProtocol.FlagsIndex] & NuPhyProtocol.DebugModeBit) == 0) return;
            }
            else
            {
                block = (byte[])_block.Clone();
            }
            block[NuPhyProtocol.FlagsIndex] &= unchecked((byte)~NuPhyProtocol.DebugModeBit);
            Exchange(io, WriteBlockRequest(block), Attempts, ReplyWaitMs, deadline, _reply);
        }

        protected override bool TryDecodeEvent(ReadOnlySpan<byte> frame, out int code, out float depth)
        {
            code = NuPhyProtocol.MadlionsKeyCode(frame[2], frame[3], _model.Table);
            depth = NuPhyProtocol.MadlionsDepth(NuPhyProtocol.Travel(frame));
            return code != AnalogKeyCodes.None;
        }
    }
}
