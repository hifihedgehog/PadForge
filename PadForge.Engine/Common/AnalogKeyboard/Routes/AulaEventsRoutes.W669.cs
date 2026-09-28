using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>One W669 factory layout: its key table, HallJoy's name for the
    /// model, the firmware products that select it, and the HID product
    /// string that selects it when the firmware does not answer, empty when
    /// none does (aula_w669_protocol.cpp:184-214,
    /// aula_w669_backend.cpp:292-306).</summary>
    public sealed record AulaW669Profile(string Table, string Name, IReadOnlyList<string> Products,
        string DescriptorProduct)
    {
        /// <summary>A fresh copy of the factory map, a HID usage per matrix
        /// position, 0xFA for Fn.</summary>
        public int[] FactoryMap() => (int[])AnalogKeyboardData.Table(AulaEventsRoutes.DataFile, Table).Clone();
    }

    /// <summary>A decoded travel-info answer (21/04).</summary>
    public readonly record struct AulaW669TravelInfo(int Maximum, int UnitCode, int FormatCode);

    /// <summary>A decoded live travel event (21/01).</summary>
    public readonly record struct AulaW669LiveEvent(int Row, int Column, int Travel, int DeclaredLength);

    /// <summary>
    /// The W669 wire protocol, HallJoy's "aula-w669-adaptive" route
    /// (aula_w669_backend.cpp:700-709), as pure functions. Every report is 64
    /// bytes with report ID 1: <c>01 opcode selector fragment-high
    /// fragment-low length subtype data...</c>
    /// (docs/protocols/AULA_WIN60HE_STANDARD_PROTOCOL.md:55-70). The route
    /// uses opcode 0D device info, 18/80 key map, and opcode 21 subtypes 04
    /// travel info, 0A poll rate, 02 subscribe and 03 unsubscribe. Nothing
    /// that writes calibration, key behavior or configuration can be built
    /// here.
    /// </summary>
    public static class AulaW669Protocol
    {
        public const ushort UsagePage = 0xFF1B;
        public const ushort Usage = 0x0091;
        public const int ReportBytes = 64;
        public const int Rows = 6;
        public const int Columns = 22;
        public const int Positions = Rows * Columns;
        public const byte ReportId = 1;
        public const byte CommandDeviceInfo = 0x0D;
        public const byte CommandAnalog = 0x21;
        public const byte CommandKeyMap = 0x18;
        public const byte KeyMapSelector = 0x80;
        public const byte SubtypeLiveEvent = 0x01;
        public const byte SubtypeSubscribe = 0x02;
        public const byte SubtypeUnsubscribe = 0x03;
        public const byte SubtypeTravelInfo = 0x04;
        public const byte SubtypePollRate = 0x09;
        public const byte SubtypePollRateQuery = 0x0A;
        public const int Fragments = 10;
        public const int RecordsPerFragment = 14;
        public const int InputBuffers = 256;

        /// <summary>A proven map carries at least this many keys
        /// (aula_w669_backend.cpp:364).</summary>
        public const int MinimumMappedKeys = 20;

        /// <summary>The usage W669 maps give the Fn key
        /// (docs/current/AULA_LAYOUT_PIPELINE.md:80).</summary>
        public const int FnUsage = 0xFA;

        /// <summary>Timing, aula_w669_backend.cpp:40-42, 318-335, 369-388, 511.</summary>
        public const int IdentityTimeoutMs = 400;
        public const int IdentityReadMs = 80;
        public const int ProofTimeoutMs = 1200;
        public const int ProofReadMs = 100;
        public const int PollRateTimeoutMs = 500;
        public const int LiveReadMs = 100;

        /// <summary>
        /// The product the IROK NA87 and AJAZZ AK820 MAX route owns on this
        /// usage. HallJoy's catalog lets that route claim its collection
        /// before W669 enumerates (aula_w669_backend.cpp:195-196, irok_na87_backend.cpp:43-46),
        /// and without the claim W669's proof reaches the M484 firmware
        /// (docs/current/AJAZZ_AK820MAX_REVIEW_2026-09-20.md:255-266). This
        /// route never admits it.
        /// </summary>
        public const ushort OwnedElsewhereVendorId = 0x0416;
        public const ushort OwnedElsewhereProductId = 0x7372;

        private const string ProfilesName = "w669_profiles";

        private static readonly Lazy<IReadOnlyList<AulaW669Profile>> _profiles = new(LoadProfiles);

        /// <summary>The eight factory layouts HallJoy knows.</summary>
        public static IReadOnlyList<AulaW669Profile> Profiles => _profiles.Value;

        /// <summary>
        /// HallJoy's interface shape (aula_w669_backend.cpp:207-209): usage
        /// FF1B:0091 with input and output reports of at least 64 bytes.
        /// HallJoy filters on no VID or PID and declares a dynamic one
        /// (:705-706). PadForge keeps that and excludes the one product
        /// another route owns on the same usage.
        /// </summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.UsagePage == UsagePage
               && info.Usage == Usage
               && info.InputReportLength >= ReportBytes
               && info.OutputReportLength >= ReportBytes
               && !(info.VendorId == OwnedElsewhereVendorId && info.ProductId == OwnedElsewhereProductId);

        private static byte[] Base(byte command)
        {
            var r = new byte[ReportBytes];
            r[0] = ReportId;
            r[1] = command;
            return r;
        }

        /// <summary><c>01 0D</c> (aula_w669_protocol.cpp:27-30).</summary>
        public static byte[] DeviceInfoRequest() => Base(CommandDeviceInfo);

        /// <summary><c>01 21</c> with subtype 04 at byte 6
        /// (aula_w669_protocol.cpp:32-37).</summary>
        public static byte[] TravelInfoRequest()
        {
            var r = Base(CommandAnalog);
            r[6] = SubtypeTravelInfo;
            return r;
        }

        /// <summary><c>01 18 80</c> (aula_w669_protocol.cpp:39-44).</summary>
        public static byte[] KeyMapRequest()
        {
            var r = Base(CommandKeyMap);
            r[2] = KeyMapSelector;
            return r;
        }

        /// <summary><c>01 21</c>, length 0x18 at byte 5, subtype 02 at byte
        /// 6, the 22-byte column mask from byte 7
        /// (aula_w669_protocol.cpp:46-53).</summary>
        public static byte[] SubscriptionRequest(byte[] mask)
        {
            if (mask == null || mask.Length != Columns)
                throw new ArgumentException("The subscription mask has one byte per column.", nameof(mask));
            var r = Base(CommandAnalog);
            r[5] = 0x18;
            r[6] = SubtypeSubscribe;
            Array.Copy(mask, 0, r, 7, Columns);
            return r;
        }

        /// <summary><c>01 21</c> with subtype 0A (aula_w669_protocol.cpp:62-67).</summary>
        public static byte[] PollRateQuery()
        {
            var r = Base(CommandAnalog);
            r[6] = SubtypePollRateQuery;
            return r;
        }

        /// <summary><c>01 21</c> with subtype 03, which clears the RAM mask
        /// (aula_w669_protocol.cpp:69-74,
        /// docs/protocols/AULA_WIN60HE_STANDARD_PROTOCOL.md:153-154).</summary>
        public static byte[] UnsubscribeRequest()
        {
            var r = Base(CommandAnalog);
            r[6] = SubtypeUnsubscribe;
            return r;
        }

        /// <summary>Bit <c>row</c> of byte <c>column</c> for every mapped
        /// position (aula_w669_backend.cpp:476-478).</summary>
        public static byte[] SubscriptionMask(int[] map)
        {
            var mask = new byte[Columns];
            for (int position = 0; position < Positions && position < map.Length; position++)
                if (map[position] != 0) mask[position % Columns] |= (byte)(1 << (position / Columns));
            return mask;
        }

        /// <summary>At least 64 bytes, report ID 1 and the opcode
        /// (aula_w669_protocol.cpp:19-24).</summary>
        public static bool IsResponse(ReadOnlySpan<byte> r, byte command)
            => r.Length >= ReportBytes && r[0] == ReportId && r[1] == command;

        /// <summary>
        /// The firmware product from a 0D answer (aula_w669_protocol.cpp:232-266):
        /// byte 2 is 0 (success), byte 4 is 0, and byte 5 ends a CSV text that
        /// starts at byte 6, inclusive. The text stops at a zero byte, every
        /// byte before it must be printable ASCII, and the fifth field is the
        /// product, 1 to 31 characters. A K673 BR answers
        /// <c>W669,34,KB,FR,7272BRHEXYXK673JCARGB,V3.18.01</c>.
        /// </summary>
        public static bool TryDecodeDeviceInfo(ReadOnlySpan<byte> r, out string product)
        {
            product = null;
            if (!IsResponse(r, CommandDeviceInfo) || r[2] != 0 || r[4] != 0 || r[5] <= 5 || r[5] >= ReportBytes)
                return false;
            const int textBegin = 6;
            int textEnd = r[5] + 1;
            if (textEnd > r.Length || textEnd <= textBegin) return false;
            int field = 0;
            Span<char> text = stackalloc char[31];
            int used = 0;
            for (int i = textBegin; i < textEnd; i++)
            {
                byte value = r[i];
                if (value == 0) break;
                if (value == (byte)',')
                {
                    if (++field > 4) break;
                    continue;
                }
                if (value < 0x20 || value > 0x7E) return false;
                if (field == 4)
                {
                    if (used + 1 >= 32) return false;
                    text[used++] = (char)value;
                }
            }
            if (field < 4 || used == 0) return false;
            product = new string(text.Slice(0, used));
            return true;
        }

        /// <summary>
        /// A 21/04 answer (aula_w669_protocol.cpp:268-280): length at least 6,
        /// maximum travel <c>byte7 | byte10 &lt;&lt; 8</c> with both bytes
        /// nonzero and a value of 32 to 10000, unit at byte 8, format at byte
        /// 11. The supplied firmware answers <c>54 01 01 01 08</c>, 340 counts.
        /// </summary>
        public static bool TryDecodeTravelInfo(ReadOnlySpan<byte> r, out AulaW669TravelInfo info)
        {
            info = default;
            if (!IsResponse(r, CommandAnalog) || r[6] != SubtypeTravelInfo || r[5] < 6 || r[7] == 0 || r[10] == 0)
                return false;
            int maximum = r[7] | (r[10] << 8);
            if (maximum < 32 || maximum > 10000) return false;
            info = new AulaW669TravelInfo(maximum, r[8], r[11]);
            return true;
        }

        /// <summary>
        /// One 18/80 fragment into <paramref name="map"/>
        /// (aula_w669_protocol.cpp:282-305): a big-endian fragment index 0 to 9
        /// at bytes 3 and 4, a length of 56 (24 for the last) at byte 5, then
        /// 4-byte records from byte 6 for positions <c>fragment * 14 + i</c>.
        /// A record of class 00 or FF keeps the factory key. Any other class
        /// sets byte 1 as the key, or drops the position when byte 1 is 00 or
        /// FF. A repeated fragment applies again.
        /// </summary>
        public static bool DecodeKeyMapFragment(ReadOnlySpan<byte> r, int[] map, bool[] received)
        {
            if (map == null || received == null || !IsResponse(r, CommandKeyMap) || r[2] != KeyMapSelector)
                return false;
            int fragment = (r[3] << 8) | r[4];
            if (fragment >= received.Length || fragment >= Fragments) return false;
            int expected = fragment < Fragments - 1 ? 56 : 24;
            if (r[5] != expected || 6 + expected > r.Length) return false;
            int records = expected / 4;
            for (int i = 0; i < records; i++)
            {
                int position = fragment * RecordsPerFragment + i;
                byte functionClass = r[6 + i * 4];
                byte usage = r[7 + i * 4];
                if (functionClass != 0 && functionClass != 0xFF && position < map.Length)
                    map[position] = usage != 0 && usage != 0xFF ? usage : 0;
            }
            received[fragment] = true;
            return true;
        }

        /// <summary>
        /// A 21/01 live event (aula_w669_protocol.cpp:307-320): declared length
        /// at least 3 (3 from the normal scanner, 5 from the alternate), row
        /// below 6 at byte 7, column below 22 at byte 8, processed travel as a
        /// little-endian u16 at bytes 9 and 10. Subtype 05 is a trigger
        /// configuration answer, not an event.
        /// </summary>
        public static bool TryDecodeLiveEvent(ReadOnlySpan<byte> r, out AulaW669LiveEvent ev)
        {
            ev = default;
            if (!IsResponse(r, CommandAnalog) || r[6] != SubtypeLiveEvent || r[5] < 3 || r.Length < 11) return false;
            int row = r[7], column = r[8];
            if (row >= Rows || column >= Columns) return false;
            ev = new AulaW669LiveEvent(row, column, r[9] | (r[10] << 8), r[5]);
            return true;
        }

        /// <summary>A 21/09 poll-rate answer (aula_w669_protocol.cpp:334-354):
        /// code 0 (firmware default, no stated rate) or 1, 2, 4, 8 for 1 to 8
        /// kHz. HallJoy only logs it.</summary>
        public static bool TryDecodePollRate(ReadOnlySpan<byte> r, out int code, out int nominalHz)
        {
            code = 0;
            nominalHz = 0;
            if (!IsResponse(r, CommandAnalog) || r[6] != SubtypePollRate || r[5] < 2) return false;
            int hz;
            switch (r[7])
            {
                case 0: hz = 0; break;
                case 1: hz = 1000; break;
                case 2: hz = 2000; break;
                case 4: hz = 4000; break;
                case 8: hz = 8000; break;
                default: return false;
            }
            code = r[7];
            nominalHz = hz;
            return true;
        }

        /// <summary>Depth in thousandths, rounded, capped at 1000, 0 for a
        /// zero maximum (aula_w669_protocol.cpp:356-361).</summary>
        public static int ToMilli(int travel, int maximum)
        {
            if (maximum <= 0 || travel < 0) return 0;
            return (int)Math.Min(1000L, ((long)travel * 1000 + maximum / 2) / maximum);
        }

        /// <summary>Positions that carry a key (aula_w669_protocol.cpp:363-367).</summary>
        public static int MappedCount(int[] map)
        {
            int count = 0;
            foreach (int code in map)
                if (code != 0) count++;
            return count;
        }

        /// <summary>
        /// HallJoy's product normalization (aula_w669_protocol.cpp:184-197):
        /// ASCII whitespace dropped anywhere, ASCII letters upper-cased, at most
        /// 31 characters kept.
        /// </summary>
        public static string NormalizeProduct(string product)
        {
            if (product == null) return string.Empty;
            var chars = new char[31];
            int used = 0;
            foreach (char ch in product)
            {
                if (used >= chars.Length) break;
                if (ch == ' ' || (ch >= '\t' && ch <= '\r')) continue;
                chars[used++] = ch >= 'a' && ch <= 'z' ? (char)(ch - 32) : ch;
            }
            return new string(chars, 0, used);
        }

        /// <summary>The profile a firmware product selects, compared exactly
        /// after normalization, or null for an unknown product
        /// (aula_w669_protocol.cpp:198-213).</summary>
        public static AulaW669Profile ProfileForProduct(string product)
        {
            string normalized = NormalizeProduct(product);
            if (normalized.Length == 0) return null;
            foreach (var profile in Profiles)
                foreach (var candidate in profile.Products)
                    if (string.Equals(candidate, normalized, StringComparison.Ordinal)) return profile;
            return null;
        }

        /// <summary>The profile a HID product string selects when the firmware
        /// does not answer 0D: trimmed, upper-cased, and exactly WIN 60 HE, WIN
        /// 68 HE or KP-TE153 (aula_w669_backend.cpp:292-306). No Redragon
        /// model has a fallback.</summary>
        public static AulaW669Profile ProfileForDescriptor(string productString)
        {
            string key = (productString ?? string.Empty).Trim().ToUpperInvariant();
            if (key.Length == 0) return null;
            foreach (var profile in Profiles)
                if (!string.IsNullOrEmpty(profile.DescriptorProduct)
                    && string.Equals(profile.DescriptorProduct, key, StringComparison.Ordinal))
                    return profile;
            return null;
        }

        /// <summary>The map a proof starts from: the profile's factory map, all
        /// zero for an unknown product (aula_w669_protocol.cpp:216-230).</summary>
        public static int[] FactoryMap(AulaW669Profile profile)
            => profile?.FactoryMap() ?? new int[Positions];

        /// <summary>
        /// The code a W669 usage publishes under. HallJoy keeps the W669 Fn at
        /// 0xFA in its own layouts (docs/current/AULA_LAYOUT_PIPELINE.md:80),
        /// but in PadForge's code space Fn is <see cref="AnalogKeyCodes.Fn"/>
        /// (0x409, HallJoy's analog_key_codes.h:15) and 0xFA names no key.
        /// Every other usage passes through.
        /// </summary>
        public static int KeyCode(int usage) => usage == FnUsage ? AnalogKeyCodes.Fn : usage;

        private static IReadOnlyList<AulaW669Profile> LoadProfiles()
        {
            var list = new List<AulaW669Profile>();
            var root = AnalogKeyboardData.File(AulaEventsRoutes.DataFile);
            foreach (var entry in root.GetProperty(ProfilesName).EnumerateArray())
            {
                var products = new List<string>();
                foreach (var product in entry.GetProperty("products").EnumerateArray())
                    products.Add(product.GetString());
                list.Add(new AulaW669Profile(
                    entry.GetProperty("table").GetString(),
                    entry.GetProperty("name").GetString(),
                    products,
                    entry.GetProperty("descriptor").GetString()));
            }
            return list;
        }
    }

    /// <summary>
    /// One W669 conversation, HallJoy's Run (aula_w669_backend.cpp:446-604).
    ///
    /// <para>Start proves the keyboard, device info 0D (or the HID product
    /// string when 0D stays silent), travel info 21/04 and the key map 18/80,
    /// first with WriteFile and then with HidD_SetOutputReport, the order of
    /// HallJoy's Prove (:390-414). It then asks the poll rate (21/0A, logged
    /// only) and subscribes with 21/02 for every mapped position, a RAM mask
    /// the firmware clears on 21/03. Stop sends 21/03. After the subscription
    /// the keyboard sends a 21/01 event whenever a key's travel changes, and
    /// nothing while it rests.</para>
    ///
    /// <para>HallJoy proves the keyboard three times, in its prepare step, in
    /// Run and on Run's own handle (:449-470), because it reopens the handle
    /// in between. PadForge keeps one handle from the proof to the teardown
    /// and proves once, as the official driver does
    /// (docs/protocols/AULA_WIN60HE_STANDARD_PROTOCOL.md:34-37).</para>
    ///
    /// <para>Two HallJoy defects are corrected. Its live loop counts a read
    /// error and keeps reading, so an unplugged keyboard stays connected with
    /// its keys published until HallJoy stops (:508-523,
    /// docs/v1.4/RISK_REGISTER.md:100). Here a failed read ends the session,
    /// as HallJoy's own protocol document requires
    /// (docs/protocols/AULA_WIN60HE_STANDARD_PROTOCOL.md:202-204, 225-227),
    /// and every key is released. A pending read fails when the keyboard is
    /// removed, which is the liveness signal. No heartbeat or silence timeout
    /// is added: the stream is quiet whenever the keys rest, and HallJoy's
    /// review forbids both without hardware proof
    /// (docs/v1.4/PROTOCOL_AUDIT_RM30_W669_2026-09-06.md:37-51).</para>
    /// </summary>
    public sealed class AulaW669Session : AnalogKeyboardSession
    {
        private enum Proof { Passed, Failed, Gone }

        private readonly string _descriptorProduct;
        private readonly Func<long> _clock;
        private readonly int[] _milli = new int[AulaW669Protocol.Positions];
        private int[] _map;
        private int[] _codes;
        private int _maximum;
        private bool _control;
        private bool _subscribed;
        private AulaW669Profile _profile;
        private string _firmwareProduct;
        private string _model;
        private int[] _keyOrder;

        /// <param name="descriptorProduct">The collection's HID product
        /// string, for the identity fallback.</param>
        /// <param name="clock">Milliseconds, for tests. Defaults to
        /// <see cref="Environment.TickCount64"/>.</param>
        public AulaW669Session(string descriptorProduct, Func<long> clock = null)
        {
            _descriptorProduct = descriptorProduct ?? string.Empty;
            _clock = clock ?? (() => Environment.TickCount64);
        }

        /// <summary>HallJoy's model name for the profile, or null for an
        /// unknown product admitted on its explicit key records, which keeps
        /// the HID product string.</summary>
        public override string ModelName => _model;
        public override int[] KeyOrder => _keyOrder;

        /// <summary>The product the firmware named in its 0D answer, or null
        /// when the identity came from the HID product string.</summary>
        public string FirmwareProduct => _firmwareProduct;

        /// <summary>The factory layout in use, null for an unknown product.</summary>
        public AulaW669Profile Profile => _profile;

        /// <summary>Full travel in counts, from 21/04.</summary>
        public int MaximumTravel => _maximum;

        /// <summary>True when the keyboard answered only through
        /// HidD_SetOutputReport.</summary>
        public bool UsesOutputReports => _control;

        /// <summary>The configured poll-rate code, or -1 when it did not
        /// answer. HallJoy logs it and nothing else (:369-388).</summary>
        public int PollRateCode { get; private set; } = -1;

        /// <summary>The proven map, a HID usage per position, 0xFA for Fn.</summary>
        public int[] Map => (int[])_map?.Clone();

        public override bool Start(IAnalogKeyboardTransport io)
        {
            try
            {
                bool proven = false;
                foreach (bool control in new[] { false, true })
                {
                    // HallJoy opens a fresh handle for each write mode. Here
                    // the one handle drops what the first mode left queued.
                    if (control) io.DiscardStale();
                    _control = control;
                    var proof = Prove(io);
                    if (proof == Proof.Gone) return false;
                    if (proof == Proof.Passed)
                    {
                        proven = true;
                        break;
                    }
                }
                if (!proven) return false;

                if (QueryPollRate(io) == Proof.Gone) return false;

                // Subscribe (aula_w669_backend.cpp:476-482). HallJoy returns
                // without 21/03 when this write fails, but a write that timed
                // out may still have reached the keyboard, so the mask is
                // cleared here as a normal stop clears it.
                _subscribed = true;
                if (!Write(io, AulaW669Protocol.SubscriptionRequest(AulaW669Protocol.SubscriptionMask(_map))))
                {
                    Stop(io);
                    return false;
                }

                _model = _profile?.Name;
                _keyOrder = AnalogKeyboardData.KeysOf(_codes);
                return true;
            }
            catch
            {
                try { Stop(io); } catch { }
                return false;
            }
        }

        /// <summary>One write mode's proof (aula_w669_backend.cpp:390-414):
        /// identity, travel, then a complete map with at least 20 keys. A
        /// failed read means the keyboard left, so the proof stops there.
        /// HallJoy keeps reading until each deadline and then fails to reopen
        /// the path in its next mode.</summary>
        private Proof Prove(IAnalogKeyboardTransport io)
        {
            // Identity (ResolveFactoryProfile, :308-349). Unrelated reports
            // are skipped. When 0D cannot be sent or no answer decodes within
            // 400 ms, the HID product string decides, and an unknown string
            // leaves the profile unknown.
            _profile = null;
            _firmwareProduct = null;
            bool identified = false;
            if (Write(io, AulaW669Protocol.DeviceInfoRequest()))
            {
                long deadline = _clock() + AulaW669Protocol.IdentityTimeoutMs;
                while (_clock() < deadline)
                {
                    int n = io.Receive(Buffer, AulaW669Protocol.IdentityReadMs);
                    if (n < 0) return Proof.Gone;
                    if (n == 0 || !AulaW669Protocol.TryDecodeDeviceInfo(Buffer.AsSpan(0, n), out string product))
                        continue;
                    _firmwareProduct = product;
                    _profile = AulaW669Protocol.ProfileForProduct(product);
                    identified = true;
                    break;
                }
            }
            if (!identified) _profile = AulaW669Protocol.ProfileForDescriptor(_descriptorProduct);

            // Travel info (ReceiveTravel, :267-274).
            if (!Write(io, AulaW669Protocol.TravelInfoRequest())) return Proof.Failed;
            bool travel = false;
            long travelDeadline = _clock() + AulaW669Protocol.ProofTimeoutMs;
            while (_clock() < travelDeadline)
            {
                int n = io.Receive(Buffer, AulaW669Protocol.ProofReadMs);
                if (n < 0) return Proof.Gone;
                if (n > 0 && AulaW669Protocol.TryDecodeTravelInfo(Buffer.AsSpan(0, n), out var info))
                {
                    _maximum = info.Maximum;
                    travel = true;
                    break;
                }
            }
            if (!travel) return Proof.Failed;

            // Key map (ReceiveMap, :351-367): the factory map with all ten
            // fragments applied, in any order, between any other reports.
            var map = AulaW669Protocol.FactoryMap(_profile);
            if (!Write(io, AulaW669Protocol.KeyMapRequest())) return Proof.Failed;
            var received = new bool[AulaW669Protocol.Fragments];
            long mapDeadline = _clock() + AulaW669Protocol.ProofTimeoutMs;
            while (_clock() < mapDeadline)
            {
                int n = io.Receive(Buffer, AulaW669Protocol.ProofReadMs);
                if (n < 0) return Proof.Gone;
                if (n == 0) continue;
                AulaW669Protocol.DecodeKeyMapFragment(Buffer.AsSpan(0, n), map, received);
                if (!Array.TrueForAll(received, seen => seen)) continue;
                if (AulaW669Protocol.MappedCount(map) < AulaW669Protocol.MinimumMappedKeys) return Proof.Failed;
                _map = map;
                _codes = new int[map.Length];
                for (int position = 0; position < map.Length; position++)
                    _codes[position] = AulaW669Protocol.KeyCode(map[position]);
                return Proof.Passed;
            }
            return Proof.Failed;
        }

        /// <summary>21/0A, up to 500 ms for its answer (:369-388).</summary>
        private Proof QueryPollRate(IAnalogKeyboardTransport io)
        {
            if (!Write(io, AulaW669Protocol.PollRateQuery())) return Proof.Failed;
            long deadline = _clock() + AulaW669Protocol.PollRateTimeoutMs;
            while (_clock() < deadline)
            {
                int n = io.Receive(Buffer, AulaW669Protocol.ProofReadMs);
                if (n < 0) return Proof.Gone;
                if (n > 0 && AulaW669Protocol.TryDecodePollRate(Buffer.AsSpan(0, n), out int code, out _))
                {
                    PollRateCode = code;
                    return Proof.Passed;
                }
            }
            return Proof.Failed;
        }

        /// <summary>
        /// One read of up to 100 ms (aula_w669_backend.cpp:508-581). A live
        /// event updates its position, and the key reads the deepest of the
        /// positions bound to it, with no expiry (:416-434,
        /// physical_analog_state.h:39-52). Other reports are ignored. A
        /// position whose key is 0 drops its event.
        /// </summary>
        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_codes == null) return AnalogPollResult.Failed;
            int n = io.Receive(Buffer, AulaW669Protocol.LiveReadMs);
            if (n < 0)
            {
                Array.Clear(_milli);
                output.ResetForReuse();
                return AnalogPollResult.Failed;
            }
            if (n == 0 || !AulaW669Protocol.TryDecodeLiveEvent(Buffer.AsSpan(0, n), out var ev))
                return AnalogPollResult.Idle;
            int position = ev.Row * AulaW669Protocol.Columns + ev.Column;
            int code = _codes[position];
            if (code == 0) return AnalogPollResult.Idle;
            _milli[position] = AulaW669Protocol.ToMilli(ev.Travel, _maximum);
            int milli = 0;
            for (int p = 0; p < _codes.Length; p++)
                if (_codes[p] == code && _milli[p] > milli) milli = _milli[p];
            output.Set(code, milli / 1000f);
            return AnalogPollResult.Ok;
        }

        /// <summary>21/03 on the proven write mode, the only undo HallJoy
        /// needs: the subscription is the one keyboard state it changes
        /// (aula_w669_backend.cpp:601,
        /// docs/protocols/AULA_WIN60HE_STANDARD_PROTOCOL.md:153-154).</summary>
        public override void Stop(IAnalogKeyboardTransport io)
        {
            Array.Clear(_milli);
            if (!_subscribed) return;
            _subscribed = false;
            Write(io, AulaW669Protocol.UnsubscribeRequest());
        }

        /// <summary>WriteFile, or HidD_SetOutputReport in the second mode
        /// (aula_w669_backend.cpp:240-254).</summary>
        private bool Write(IAnalogKeyboardTransport io, byte[] report)
            => _control ? io.SendOutputReport(report) : io.Send(report);
    }
}
