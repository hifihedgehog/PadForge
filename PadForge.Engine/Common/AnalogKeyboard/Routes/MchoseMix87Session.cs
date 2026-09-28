using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The MCHOSE Mix 87 III (3837:300D, stock firmware 1.22) protocol,
    /// HallJoy's mchose_mix87_protocol.h. Every transfer is a 65-byte buffer
    /// on the collection 0001:0000: report ID 00, then a 64-byte packet. A
    /// request packet is 55, the opcode, 00, a checksum, a length of at most
    /// 56, a little-endian 24-bit flash address and the payload. An answer
    /// starts AA and repeats the request's opcode, length and address. The
    /// checksum is the low byte of the sum of the length, the address bytes
    /// and the payload (mchose_mix87_protocol.h:14-19). Opcode 03 answers the
    /// firmware information, E0 reads flash, 06 writes one settings byte.
    ///
    /// <para>Analog depth comes as pushed A0 events, one changed key each,
    /// and only while the "debug" flag, bit 3 of settings byte
    /// profile * 64 + 7, is set. That flag lives in the keyboard's flash:
    /// setting or clearing it makes the firmware erase and rewrite its 8 KiB
    /// settings page (mchose_mix87_protocol.h:67-70). HallJoy sets it when a
    /// session starts and clears it when the session ends, as its maintainer
    /// decided in docs/current/MCHOSE_MIX87_LOG35_2026-09-27.md:3-31: two
    /// flash writes for a keyboard whose flag was off.</para>
    /// </summary>
    public static class MchoseMix87Protocol
    {
        public const ushort VendorId = 0x3837;
        public const ushort ProductId = 0x300D;
        public const ushort UsagePage = 0x0001;
        public const ushort Usage = 0x0000;

        /// <summary>Windows length of every transfer: the unnumbered 64-byte
        /// reports plus report ID 0 (mchose_mix87_backend.cpp:71-73).</summary>
        public const int WireLength = 65;
        public const int PacketLength = 64;

        /// <summary>Most bytes one request carries (mchose_mix87_protocol.h:16, 22).</summary>
        public const int MaxChunk = 56;

        /// <summary>End of the addressable flash (mchose_mix87_protocol.h:22).</summary>
        public const uint FlashEnd = 0x80000;

        /// <summary>The profile block and the settings page
        /// (mchose_mix87_protocol.h:8-13).</summary>
        public const uint BaseAddress = 0x2A000;
        public const uint SettingsAddress = 0x2C000;
        public const int BaseLength = 8;
        public const int SettingsLength = 256;
        public const int SettingsPageLength = 8192;

        /// <summary>The debug flag: bit 3 of settings byte profile * 64 + 7
        /// (mchose_mix87_backend.cpp:169, mchose_mix87_protocol.h:38-39).</summary>
        public const int ProfileStride = 64;
        public const int FlagOffset = 7;
        public const byte FlagBit = 0x08;

        public const byte RequestMarker = 0x55;
        public const byte ReplyMarker = 0xAA;
        public const byte RejectMarker = 0xAB;
        public const byte GetInfoOp = 0x03;
        public const byte ReadOp = 0xE0;
        public const byte WriteSettingOp = 0x06;

        /// <summary>Length the firmware information request asks for
        /// (mchose_mix87_backend.cpp:148).</summary>
        public const int GetInfoLength = 31;

        public const byte AnalogEvent = 0xA0;

        /// <summary>The key triplet type of an ordinary key
        /// (mchose_mix87_protocol.h:44).</summary>
        public const byte KeyType = 0x10;

        /// <summary>The depth of a key at the bottom, and the maximum every
        /// A0 event must carry (mchose_mix87_protocol.h:55).</summary>
        public const int FullScale = 341;

        /// <summary>The factory key descriptors: 92 triplets at 0x15DA6
        /// holding 86 distinct keys (mchose_mix87_backend.cpp:154-161).</summary>
        public const uint KeySetAddress = 0x15DA6;
        public const int KeySetTriplets = 92;
        public const int KeyCount = 86;

        /// <summary>HallJoy's name for the keyboard (mchose_mix87_backend.cpp:281).</summary>
        public const string ModelName = "MCHOSE Mix87 III";

        /// <summary>The collection shape HallJoy admits: exact VID and PID,
        /// usage 0001:0000, 65-byte input and output reports
        /// (mchose_mix87_backend.cpp:67-73).</summary>
        public static bool SupportedCollection(AnalogKeyboardDeviceInfo info)
            => info != null && info.VendorId == VendorId && info.ProductId == ProductId
               && info.UsagePage == UsagePage && info.Usage == Usage
               && info.InputReportLength == WireLength && info.OutputReportLength == WireLength;

        /// <summary>
        /// The route's metadata test. HallJoy runs no session unless exactly
        /// one collection of the whole system has the admitted shape
        /// (mchose_mix87_backend.cpp:250-253). A collection sees only its own
        /// keyboard's other collections, so this rejects a keyboard with two
        /// such collections. A second keyboard is kept out by
        /// <see cref="MchoseMix87Session"/>, which runs one session at a time.
        /// </summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
        {
            if (!SupportedCollection(info)) return false;
            foreach (var sibling in info.Siblings ?? Array.Empty<AnalogKeyboardDeviceInfo>())
                if (SupportedCollection(sibling)) return false;
            return true;
        }

        /// <summary>One flash region the handshake hashes: the command,
        /// service, report, flash-helper, descriptor and switch, and key map
        /// code of stock firmware 1.22 (mchose_mix87_backend.cpp:126-136,
        /// MCHOSE_MIX87_LOG35_2026-09-27.md:91-94).</summary>
        public readonly struct Fingerprint
        {
            public Fingerprint(uint address, int size, string sha256)
            {
                Address = address;
                Size = size;
                Sha256 = sha256;
            }

            public uint Address { get; }
            public int Size { get; }

            /// <summary>Lowercase hex SHA-256 of the region's bytes.</summary>
            public string Sha256 { get; }
        }

        private static readonly Lazy<IReadOnlyList<Fingerprint>> _fingerprints = new(LoadFingerprints);

        /// <summary>The seven regions and their digests, in HallJoy's order.
        /// HallJoy carries the hashes only, never the vendor's bytes
        /// (mchose_mix87_backend.cpp:127).</summary>
        public static IReadOnlyList<Fingerprint> Fingerprints => _fingerprints.Value;

        private static IReadOnlyList<Fingerprint> LoadFingerprints()
        {
            var list = new List<Fingerprint>();
            var root = AnalogKeyboardData.File(NeoApexMixRoutes.DataFile);
            foreach (var entry in root.GetProperty("mchose_mix87_fingerprints").EnumerateArray())
                list.Add(new Fingerprint(entry.GetProperty("address").GetUInt32(),
                    entry.GetProperty("size").GetInt32(), entry.GetProperty("sha256").GetString()));
            return list.ToArray();
        }

        /// <summary>True when <paramref name="bytes"/> hash to
        /// <paramref name="sha256"/> (mchose_mix87_backend.cpp:137-146).</summary>
        public static bool MatchesDigest(byte[] bytes, string sha256)
            => string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha256, StringComparison.Ordinal);

        /// <summary>The packet checksum: the low byte of the sum of byte 4
        /// (the length) and bytes 5 up to 8 + length. 0 for a length past 56
        /// (mchose_mix87_protocol.h:14-19).</summary>
        public static byte Checksum(ReadOnlySpan<byte> packet)
        {
            int length = packet[4];
            if (length > MaxChunk) return 0;
            int sum = length;
            for (int i = 5; i < 8 + length; i++) sum += packet[i];
            return (byte)sum;
        }

        /// <summary>The firmware information request, length 31:
        /// <c>55 03 00 1F 1F 00 00 00</c> (mchose_mix87_backend.cpp:148).</summary>
        public static byte[] GetInfo()
        {
            var p = new byte[PacketLength];
            p[0] = RequestMarker;
            p[1] = GetInfoOp;
            p[4] = GetInfoLength;
            p[3] = Checksum(p);
            return p;
        }

        /// <summary>True for stock firmware 1.22: answer bytes 8 and 9 are
        /// 22 01 (mchose_mix87_backend.cpp:149).</summary>
        public static bool KnownFirmware(ReadOnlySpan<byte> answer)
            => answer.Length >= 10 && answer[8] == 0x22 && answer[9] == 0x01;

        /// <summary>A flash read of <paramref name="size"/> bytes at
        /// <paramref name="address"/>. All zeros for a size past 56 or a
        /// range past the flash (mchose_mix87_protocol.h:20-26).</summary>
        public static byte[] Read(uint address, int size)
        {
            var p = new byte[PacketLength];
            if (size < 0 || size > MaxChunk || address >= FlashEnd || address + (uint)size > FlashEnd) return p;
            p[0] = RequestMarker;
            p[1] = ReadOp;
            p[4] = (byte)size;
            p[5] = (byte)address;
            p[6] = (byte)(address >> 8);
            p[7] = (byte)(address >> 16);
            p[3] = Checksum(p);
            return p;
        }

        /// <summary>True when <paramref name="reply"/> answers
        /// <paramref name="request"/>: AA, the same opcode, 00, the same
        /// length (at most 56) and address, and a valid checksum
        /// (mchose_mix87_protocol.h:27-31).</summary>
        public static bool Reply(ReadOnlySpan<byte> request, ReadOnlySpan<byte> reply)
            => reply[0] == ReplyMarker && reply[1] == request[1] && reply[2] == 0
               && reply[4] <= MaxChunk && reply[4] == request[4]
               && reply.Slice(5, 3).SequenceEqual(request.Slice(5, 3)) && reply[3] == Checksum(reply);

        /// <summary>The active profile slot from the profile block: byte 1
        /// is the profile count (1 to 4), byte 0 the active index below it,
        /// and byte 2 + index the slot, below 4 (mchose_mix87_protocol.h:32-35).</summary>
        public static bool Profile(ReadOnlySpan<byte> block, out int profile)
        {
            profile = 0;
            if (block.Length < BaseLength || block[1] == 0 || block[1] > 4 || block[0] >= block[1]
                || block[2 + block[0]] >= 4)
                return false;
            profile = block[2 + block[0]];
            return true;
        }

        /// <summary>The one settings write HallJoy sends: 06, length 1, the
        /// flag byte's offset, and the previous byte with only bit 3 changed.
        /// All zeros for a profile past 3 (mchose_mix87_protocol.h:36-41).</summary>
        public static byte[] FlagWrite(int profile, byte previous, bool enabled)
        {
            var p = new byte[PacketLength];
            if (profile < 0 || profile >= 4) return p;
            p[0] = RequestMarker;
            p[1] = WriteSettingOp;
            p[4] = 1;
            p[5] = (byte)(profile * ProfileStride + FlagOffset);
            p[8] = enabled ? (byte)(previous | FlagBit) : (byte)(previous & ~FlagBit);
            p[3] = Checksum(p);
            return p;
        }

        /// <summary>A key triplet (type, modifier, code) as a HID usage, 0
        /// when it is none: type 10 only, a plain code 04 to 73, or a
        /// modifier with a single bit and code 0 for Left Ctrl to Right GUI
        /// (mchose_mix87_protocol.h:42-49). The factory descriptor, not the
        /// user's remapped assignment.</summary>
        public static int Hid(byte type, byte modifier, byte code)
        {
            if (type != KeyType) return 0;
            if (modifier == 0) return code >= 0x04 && code <= 0x73 ? code : 0;
            if (code != 0 || (modifier & (modifier - 1)) != 0) return 0;
            int bit = 0;
            while ((modifier >> bit) != 1) bit++;
            return 0xE0 + bit;
        }

        /// <summary>Decodes the 92 triplets of the key descriptor region into
        /// the allowed set and a table of triplet index to usage (0 for an
        /// empty triplet). False when a usage appears twice
        /// (mchose_mix87_backend.cpp:154-159).</summary>
        public static bool DecodeKeySet(ReadOnlySpan<byte> region, bool[] allowed, int[] table)
        {
            if (region.Length < KeySetTriplets * 3) return false;
            for (int i = 0; i < KeySetTriplets; i++)
            {
                int hid = Hid(region[i * 3], region[i * 3 + 1], region[i * 3 + 2]);
                table[i] = hid;
                if (hid == 0) continue;
                if (allowed[hid]) return false;
                allowed[hid] = true;
            }
            return true;
        }

        /// <summary>
        /// One A0 event: the key triplet at bytes 1-3, the depth big-endian
        /// at 6-7 and the maximum at 14-15. Accepted when the key is in the
        /// allowed set, the maximum is 341 and the depth does not exceed it.
        /// The depth becomes HallJoy's 0..1000, rounded to nearest
        /// (mchose_mix87_protocol.h:50-57).
        /// </summary>
        public static bool Decode(ReadOnlySpan<byte> packet, bool[] allowed, out int hid, out int milli)
        {
            hid = 0;
            milli = 0;
            if (packet.Length < PacketLength || packet[0] != AnalogEvent) return false;
            int key = Hid(packet[1], packet[2], packet[3]);
            int depth = (packet[6] << 8) | packet[7];
            int maximum = (packet[14] << 8) | packet[15];
            if (key == 0 || !allowed[key] || maximum != FullScale || depth > maximum) return false;
            hid = key;
            milli = (depth * 1000 + maximum / 2) / maximum;
            return true;
        }

        /// <summary>Packets that mean the vendor's configurator or a profile
        /// change is talking to the keyboard, which may stop the analog
        /// stream without a disconnect (mchose_mix87_backend.cpp:235-239).</summary>
        public static bool IsConfiguratorPacket(byte first)
            => first is 0xA2 or 0xA3 or ReplyMarker or RejectMarker;

        /// <summary>How one flag transaction ended (mchose_mix87_protocol.h:58).</summary>
        public enum ChangeResult { Verified, Unchanged, StaleProfile, ReadFailed, ReservedData, Uncertain }

        /// <summary>
        /// One guarded change of the debug flag, HallJoy's ChangeFlag
        /// (mchose_mix87_protocol.h:59-88). <paramref name="read"/> fills its
        /// buffer from flash at an address, <paramref name="exchange"/> sends
        /// a request packet and is true when its matching answer arrived.
        /// Both must use the same open handle.
        /// <list type="number">
        /// <item>Read the profile block and the whole 8 KiB settings page.</item>
        /// <item>The block must equal the one read at admission and name a
        /// valid profile, else StaleProfile.</item>
        /// <item>The page past the 256 settings bytes must be all FF (erased)
        /// or all 00 (factory padding), else ReservedData: the firmware
        /// erases the whole page and rewrites only the settings.</item>
        /// <item>A flag already as asked is Unchanged, with no write.</item>
        /// <item>Read the block again, which must be unchanged.</item>
        /// <item>Send exactly one 06 write with only bit 3 changed. No retry.</item>
        /// <item>Read the page and the block again. Verified only when the
        /// write was acknowledged, the settings equal the old ones with just
        /// the flag byte changed, every padding byte is unchanged or FF, and
        /// the block is unchanged. Anything else is Uncertain.</item>
        /// </list>
        /// </summary>
        public static ChangeResult ChangeFlag(byte[] consentBase, bool enabled, Func<uint, byte[], bool> read,
            Func<byte[], byte[], bool> exchange)
        {
            var block = new byte[BaseLength];
            var before = new byte[SettingsPageLength];
            var after = new byte[SettingsPageLength];
            if (!read(BaseAddress, block) || !read(SettingsAddress, before)) return ChangeResult.ReadFailed;
            if (consentBase == null || !block.AsSpan().SequenceEqual(consentBase) || !Profile(block, out int profile))
                return ChangeResult.StaleProfile;

            var tail = before.AsSpan(SettingsLength);
            bool erased = !tail.ContainsAnyExcept((byte)0xFF);
            bool factoryPadding = !tail.ContainsAnyExcept((byte)0x00);
            if (!erased && !factoryPadding) return ChangeResult.ReservedData;

            int offset = profile * ProfileStride + FlagOffset;
            if (((before[offset] & FlagBit) != 0) == enabled) return ChangeResult.Unchanged;

            var check = new byte[BaseLength];
            if (!read(BaseAddress, check)) return ChangeResult.ReadFailed;
            if (!check.AsSpan().SequenceEqual(block)) return ChangeResult.StaleProfile;

            var request = FlagWrite(profile, before[offset], enabled);
            var ack = new byte[PacketLength];
            bool accepted = exchange(request, ack); // exactly one write attempt
            if (!read(SettingsAddress, after) || !read(BaseAddress, check)) return ChangeResult.Uncertain;

            before[offset] = request[8];
            bool settingsMatch = before.AsSpan(0, SettingsLength).SequenceEqual(after.AsSpan(0, SettingsLength));
            bool paddingSafe = true;
            for (int i = SettingsLength; i < SettingsPageLength; i++)
                if (after[i] != before[i] && after[i] != 0xFF) paddingSafe = false;
            return accepted && settingsMatch && paddingSafe && check.AsSpan().SequenceEqual(block)
                ? ChangeResult.Verified
                : ChangeResult.Uncertain;
        }
    }

    /// <summary>
    /// One MCHOSE Mix 87 III read the way HallJoy's mchose_mix87 route reads
    /// it (mchose_mix87_backend.cpp:188-246).
    ///
    /// <para>The handshake only reads until the keyboard is proven: the
    /// firmware information must say 1.22, seven flash regions read with E0
    /// must hash to HallJoy's digests, and the key descriptor region must
    /// decode to exactly 86 distinct keys. Then it reads the profile block,
    /// the settings and the block again. A flag that is off is turned on with
    /// one guarded flash write (<see cref="MchoseMix87Protocol.ChangeFlag"/>).
    /// Once the flag is on, or an enable was attempted, the session owes the
    /// keyboard a disable: <see cref="Stop"/> clears the flag with a second
    /// guarded write, also after an enable whose outcome was uncertain, and
    /// also when the flag was already on before the session began
    /// (mchose_mix87_backend.cpp:177-187 and 199-207, MCHOSE_MIX87_LOG35_2026-09-27.md:10-16).
    /// A handshake that fails after an enable attempt runs that disable
    /// before it returns, since the app stops only sessions that started.</para>
    ///
    /// <para>At most one automatic enable is attempted per generation, so a
    /// fault never turns into a loop of flash writes: a later session that
    /// finds the flag off refuses the keyboard without writing
    /// (mchose_mix87_backend.cpp:44-46, 202). HallJoy starts a generation
    /// when its backend starts, at launch and on resume. Here the process
    /// start is one, and <see cref="BeginGeneration"/> starts another.</para>
    ///
    /// <para>One session runs at a time, and a handshake waits up to 3 s for
    /// another session's teardown, the way HallJoy's single worker runs one
    /// session after another (mchose_mix87_backend.cpp:247-255).</para>
    ///
    /// <para>A pass waits up to 50 ms for one pushed packet. Silence is
    /// normal: a held key stays held (mchose_mix87_backend.cpp:232). An A0
    /// event updates its one key. Configurator packets (A2, A3, AA, AB), an
    /// A0 key event that fails validation, including a key outside the 86
    /// read from the keyboard, and a report that is not a 65-byte report 0
    /// end the session (mchose_mix87_backend.cpp:231-244). Other packets are
    /// ignored.</para>
    /// </summary>
    public sealed class MchoseMix87Session : AnalogKeyboardSession
    {
        /// <summary>Write timeout of a request (mchose_mix87_backend.cpp:101).</summary>
        public const int WriteTimeoutMs = 300;

        /// <summary>How long a request's answer is waited for, and the wait
        /// of each read inside it (mchose_mix87_backend.cpp:102-104).</summary>
        public const int ReplyWindowMs = 800;
        public const int ReadSliceMs = 50;

        /// <summary>Deadline of one flag transaction
        /// (mchose_mix87_backend.cpp:172, 222).</summary>
        public const int TransactionMs = 10000;

        /// <summary>Longest wait of one pass (mchose_mix87_backend.cpp:231).</summary>
        public const int StreamWaitMs = 50;

        /// <summary>How long a handshake waits for another Mix87 session to
        /// finish its teardown. HallJoy's one worker never starts a session
        /// before the last one's cleanup returned
        /// (mchose_mix87_backend.cpp:247-255), and the app gives a stopping
        /// reader 1.5 s plus 1 s after cancelling its I/O.</summary>
        public const int DefaultSlotWaitMs = 3000;

        private static readonly object s_lock = new();
        private static bool s_autoAttempted;
        private static MchoseMix87Session s_active;

        private readonly IReadOnlyList<MchoseMix87Protocol.Fingerprint> _fingerprints;
        private readonly bool[] _allowed = new bool[256];
        private readonly int[] _keyTable = new int[MchoseMix87Protocol.KeySetTriplets];
        private readonly byte[] _answer = new byte[MchoseMix87Protocol.PacketLength];
        private byte[] _base;
        private bool _armed;

        /// <summary>The millisecond clock deadlines are measured on,
        /// GetTickCount64 as in HallJoy. Tests substitute a scripted one.</summary>
        internal Func<long> Clock = () => Environment.TickCount64;

        /// <summary>The wait for another session's teardown. Tests shorten it.</summary>
        internal int SlotWaitMs = DefaultSlotWaitMs;

        public MchoseMix87Session()
            : this(MchoseMix87Protocol.Fingerprints)
        {
        }

        /// <summary>Test seam: the regions and digests to admit. The
        /// firmware's own bytes are not in any repository, so tests hash a
        /// synthetic image.</summary>
        internal MchoseMix87Session(IReadOnlyList<MchoseMix87Protocol.Fingerprint> fingerprints)
        {
            _fingerprints = fingerprints;
        }

        public override string ModelName => MchoseMix87Protocol.ModelName;

        /// <summary>The 86 keys read from the keyboard, in descriptor order.</summary>
        public override int[] KeyOrder => AnalogKeyboardData.KeysOf(_keyTable);

        /// <summary>The active profile slot the handshake read, -1 before.</summary>
        public int Profile { get; private set; } = -1;

        /// <summary>True while the session owes the keyboard a disable.</summary>
        public bool DisableOwed => _armed;

        /// <summary>The automatic enable's outcome, null when none ran.</summary>
        public MchoseMix87Protocol.ChangeResult? EnableResult { get; private set; }

        /// <summary>The closing disable's outcome, null when none ran.</summary>
        public MchoseMix87Protocol.ChangeResult? CleanupResult { get; private set; }

        /// <summary>
        /// Starts a new generation: the next handshake that finds the flag
        /// off may enable it once more. HallJoy does this when its backend
        /// starts, at launch and when the user resumes after a pause
        /// (mchose_mix87_backend.cpp:261). The app's counterpart is analog
        /// keyboard input being turned back on. A session already running is
        /// not affected.
        /// </summary>
        public static void BeginGeneration()
        {
            lock (s_lock) s_autoAttempted = false;
        }

        /// <summary>Test seam: a new generation and no session holding the
        /// slot.</summary>
        internal static void ResetGeneration()
        {
            lock (s_lock)
            {
                s_autoAttempted = false;
                s_active = null;
                Monitor.PulseAll(s_lock);
            }
        }

        public override bool Start(IAnalogKeyboardTransport io)
        {
            // mchose_mix87_backend.cpp:247-255: one session at a time, the
            // next one only after the last one's cleanup.
            lock (s_lock)
            {
                long until = Environment.TickCount64 + SlotWaitMs;
                while (s_active != null && s_active != this)
                {
                    long left = until - Environment.TickCount64;
                    if (left <= 0) return false;
                    Monitor.Wait(s_lock, (int)left);
                }
                s_active = this;
            }
            bool started = false;
            try
            {
                // mchose_mix87_backend.cpp:194: read-only admission.
                if (!Verify(io)) return false;

                // mchose_mix87_backend.cpp:197-199: the saved mode. A flag
                // already on arms the disable at once.
                if (!LoadMode(io, out var block, out bool enabled, out int profile)) return false;
                _base = block;
                Profile = profile;
                _armed = enabled;

                // mchose_mix87_backend.cpp:200-208: one automatic enable per
                // generation, armed before the attempt so that an uncertain
                // or failed enable is still followed by a disable.
                if (!enabled)
                {
                    lock (s_lock)
                    {
                        if (s_autoAttempted) return false;
                        s_autoAttempted = true;
                    }
                    _armed = true;
                    var result = ChangeMode(io, true);
                    EnableResult = result;
                    if (result != MchoseMix87Protocol.ChangeResult.Verified
                        && result != MchoseMix87Protocol.ChangeResult.Unchanged)
                        return false;
                }

                // mchose_mix87_backend.cpp:209-210: a pre-enabled keyboard
                // also uses up the generation's enable.
                lock (s_lock) s_autoAttempted = true;
                started = true;
                return true;
            }
            finally
            {
                if (!started) Teardown(io);
            }
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            int n = io.Receive(Buffer, StreamWaitMs);
            if (n == 0) return AnalogPollResult.Idle;
            if (n != MchoseMix87Protocol.WireLength || Buffer[0] != 0) return AnalogPollResult.Failed;
            var packet = Buffer.AsSpan(1, MchoseMix87Protocol.PacketLength);
            if (MchoseMix87Protocol.IsConfiguratorPacket(packet[0])) return AnalogPollResult.Failed;
            if (MchoseMix87Protocol.Decode(packet, _allowed, out int hid, out int milli))
            {
                output.Set(hid, milli / 1000f);
                return AnalogPollResult.Ok;
            }
            return packet[0] == MchoseMix87Protocol.AnalogEvent && packet[1] == MchoseMix87Protocol.KeyType
                ? AnalogPollResult.Failed
                : AnalogPollResult.Idle;
        }

        /// <summary>Clears the flag the session set or found set, HallJoy's
        /// ModeLease (mchose_mix87_backend.cpp:177-187). The app has already
        /// released every key.</summary>
        public override void Stop(IAnalogKeyboardTransport io) => Teardown(io);

        private void Teardown(IAnalogKeyboardTransport io)
        {
            try
            {
                if (_armed)
                {
                    _armed = false;
                    CleanupResult = ChangeMode(io, false);
                }
            }
            finally
            {
                lock (s_lock)
                {
                    if (s_active == this)
                    {
                        s_active = null;
                        Monitor.PulseAll(s_lock);
                    }
                }
            }
        }

        /// <summary>Firmware 1.22, the seven digests and the 86-key set
        /// (mchose_mix87_backend.cpp:147-162).</summary>
        private bool Verify(IAnalogKeyboardTransport io)
        {
            if (!Exchange(io, MchoseMix87Protocol.GetInfo(), _answer) || !MchoseMix87Protocol.KnownFirmware(_answer))
                return false;
            Array.Clear(_allowed);
            Array.Clear(_keyTable);
            foreach (var region in _fingerprints)
            {
                var bytes = new byte[region.Size];
                if (!ReadBytes(io, region.Address, bytes, long.MaxValue)) return false;
                if (!MchoseMix87Protocol.MatchesDigest(bytes, region.Sha256)) return false;
                if (region.Address == MchoseMix87Protocol.KeySetAddress
                    && !MchoseMix87Protocol.DecodeKeySet(bytes, _allowed, _keyTable))
                    return false;
            }
            int keys = 0;
            foreach (bool allowed in _allowed)
                if (allowed) keys++;
            return keys == MchoseMix87Protocol.KeyCount;
        }

        /// <summary>The profile block, the 256 settings bytes and the block
        /// again, which must not have changed (mchose_mix87_backend.cpp:163-170).</summary>
        private bool LoadMode(IAnalogKeyboardTransport io, out byte[] block, out bool enabled, out int profile)
        {
            block = new byte[MchoseMix87Protocol.BaseLength];
            enabled = false;
            profile = 0;
            var settings = new byte[MchoseMix87Protocol.SettingsLength];
            if (!ReadBytes(io, MchoseMix87Protocol.BaseAddress, block, long.MaxValue)
                || !MchoseMix87Protocol.Profile(block, out profile)
                || !ReadBytes(io, MchoseMix87Protocol.SettingsAddress, settings, long.MaxValue))
                return false;
            var check = new byte[MchoseMix87Protocol.BaseLength];
            if (!ReadBytes(io, MchoseMix87Protocol.BaseAddress, check, long.MaxValue)
                || !check.AsSpan().SequenceEqual(block))
                return false;
            enabled = (settings[profile * MchoseMix87Protocol.ProfileStride + MchoseMix87Protocol.FlagOffset]
                       & MchoseMix87Protocol.FlagBit) != 0;
            return true;
        }

        /// <summary>One flag transaction against the block read at
        /// admission, all its reads inside one 10 s deadline
        /// (mchose_mix87_backend.cpp:171-176).</summary>
        private MchoseMix87Protocol.ChangeResult ChangeMode(IAnalogKeyboardTransport io, bool enabled)
        {
            long deadline = Clock() + TransactionMs;
            return MchoseMix87Protocol.ChangeFlag(_base, enabled,
                (address, target) => Clock() < deadline && ReadBytes(io, address, target, deadline),
                (request, reply) => Exchange(io, request, reply));
        }

        /// <summary>Reads <paramref name="target"/>'s length from flash in
        /// E0 requests of at most 56 bytes, each one checked against
        /// <paramref name="deadline"/> first (mchose_mix87_backend.cpp:115-124).</summary>
        private bool ReadBytes(IAnalogKeyboardTransport io, uint address, byte[] target, long deadline)
        {
            for (int pos = 0; pos < target.Length;)
            {
                if (Clock() >= deadline) return false;
                int count = Math.Min(MchoseMix87Protocol.MaxChunk, target.Length - pos);
                if (!Exchange(io, MchoseMix87Protocol.Read(address + (uint)pos, count), _answer)) return false;
                Array.Copy(_answer, 8, target, pos, count);
                pos += count;
            }
            return true;
        }

        /// <summary>
        /// One request and its answer, HallJoy's Exchange
        /// (mchose_mix87_backend.cpp:93-114): the packet goes out as report 0,
        /// and a write slower than 300 ms fails, as HallJoy cancels it. Then
        /// reads of up to 50 ms each until 800 ms have passed. The first
        /// matching answer wins and lands in <paramref name="reply"/>. An AA
        /// or AB packet that does not match fails, as does a read that is
        /// not a 65-byte report 0. Anything else, such as an A0 event queued
        /// before the request, is skipped.
        /// </summary>
        private bool Exchange(IAnalogKeyboardTransport io, byte[] request, byte[] reply)
        {
            var wire = new byte[MchoseMix87Protocol.WireLength];
            request.AsSpan(0, MchoseMix87Protocol.PacketLength).CopyTo(wire.AsSpan(1));
            long sent = Clock();
            if (!io.Send(wire)) return false;
            if (Clock() - sent > WriteTimeoutMs) return false;
            long end = Clock() + ReplyWindowMs;
            while (Clock() < end)
            {
                int n = io.Receive(Buffer, ReadSliceMs);
                if (n == 0) continue;
                if (n != MchoseMix87Protocol.WireLength || Buffer[0] != 0) return false;
                Buffer.AsSpan(1, MchoseMix87Protocol.PacketLength).CopyTo(reply);
                if (MchoseMix87Protocol.Reply(request, reply)) return true;
                if (reply[0] == MchoseMix87Protocol.ReplyMarker || reply[0] == MchoseMix87Protocol.RejectMarker)
                    return false;
            }
            return false;
        }
    }
}
