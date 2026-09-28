using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// ATTACK SHARK RY5088 boards over feature reports, HallJoy's release
    /// "play" worker (attackshark_pro_diagnostic.cpp:301-366) and its model
    /// headers (attackshark_pro_diagnostic_model.h, attackshark_pro_native_model.h).
    ///
    /// <para>Every exchange waits, writes a 65-byte feature request, waits
    /// again and reads the 65-byte answer (attackshark_pro_diagnostic.cpp:237-268).
    /// Start sends 8F twice per attempt, up to three attempts, and admits the
    /// board only when both answers carry the same board ID and that ID has a
    /// profile for the collection's product ID. Then two 80 exchanges read the
    /// RF version, which picks the unit scale when it is nonzero. A pass reads
    /// one of the four 32-value depth pages (E5 FE 01 page), first discarding
    /// one answer whenever the page changes, because answers carry no page
    /// tag and can be one request late. Only 8F, 80 and E5 FE 01 pages 0 to 3
    /// can be built. The stream command 1B and the calibration commands 1C
    /// and 1E never are, and nothing changes the keyboard's state, so Stop
    /// sends nothing (attackshark_pro_diagnostic_model.h:18-25).</para>
    ///
    /// <para>Depths publish the way HallJoy's parent process reads them
    /// (attackshark_pro_diagnostic.cpp:91-150): a page stays fresh for 150 ms
    /// at the 1 ms delay (200 and 300 ms after the delay escalates), a key on
    /// an expired page reads 0, and every key reads 0 while the Fn key's page
    /// is not fresh. A key picks the Fn layer when Fn is down at the moment
    /// its depth leaves 0 and keeps that layer until it returns to 0.</para>
    /// </summary>
    public sealed class AttackSharkSession : AnalogKeyboardSession
    {
        public const int ReportLength = 65;
        public const int IdentityDelayMs = 10;
        public const int IdentityAttempts = 3;
        /// <summary>Depth pages start at a 1 ms delay (attackshark_pro_diagnostic.cpp:326).</summary>
        public const int PageDelayMs = 1;
        /// <summary>Invalid answers in a row that raise the delay, and that
        /// end the session once it is at 10 ms (attackshark_pro_diagnostic.cpp:345-347).</summary>
        public const int InvalidStreakLimit = 3;
        /// <summary>Loop iterations between identity rechecks (attackshark_pro_diagnostic.cpp:357).</summary>
        public const int RecheckCycles = 1024;
        /// <summary>HallJoy's plausibility bound on a depth value, not the
        /// full-scale value (attackshark_pro_diagnostic_model.h:34).</summary>
        public const int PlausibleRawLimit = 4096;
        /// <summary>Page freshness at the 1 ms delay (attackshark_pro_native_model.h:164).</summary>
        public const int FreshMs = 150;
        /// <summary>The board HallJoy also admits on 3151:5029: an R68 HE whose
        /// catalog entry says 3151:502D (attackshark_pro_diagnostic_model.h:10-12).</summary>
        public const uint R68AliasBoard = 3650;
        public const ushort R68AliasProductId = 0x5029;

        private readonly ushort _productId;
        private readonly Func<long> _clock;
        private readonly Action<int> _wait;
        private readonly RongYuanPreciseDelay _precise;
        private readonly byte[] _reply = new byte[ReportLength];
        private readonly int[] _values = new int[32];
        private readonly AttackSharkPages _pages = new();
        private AttackSharkProfile _profile;
        private int[] _keyOrder;
        private uint _identity;
        private int _usbVersion;
        private int _rfVersion;
        private int _units;
        private int _delay = IdentityDelayMs;
        private int _invalidStreak;
        private int _previousPage = 4;
        private uint _cycle;

        /// <param name="productId">The collection's USB product ID, which the
        /// board ID must agree with.</param>
        /// <param name="clock">Milliseconds, GetTickCount's scale. Tests pass
        /// their own.</param>
        /// <param name="wait">The exchange delay. Null waits on a
        /// high-resolution timer as HallJoy does.</param>
        public AttackSharkSession(ushort productId, Func<long> clock = null, Action<int> wait = null)
        {
            _productId = productId;
            _clock = clock ?? (() => Environment.TickCount64);
            if (wait == null)
            {
                _precise = new RongYuanPreciseDelay();
                _wait = _precise.Wait;
            }
            else
            {
                _wait = wait;
            }
        }

        public AttackSharkProfile Profile => _profile;
        public uint BoardId => _identity;
        public int UsbVersion => _usbVersion;
        public int RfVersion => _rfVersion;
        public int UnitsPerMillimeter => _units;
        public int DelayMs => _delay;
        public int FreshBudgetMs => FreshBudget(_delay);
        public uint Cycle => _cycle;

        public override string ModelName => _profile?.Name;
        public override int[] KeyOrder => _keyOrder;

        // ── Protocol helpers (attackshark_pro_diagnostic_model.h, attackshark_pro_native_model.h) ──

        /// <summary>The request buffer, report ID 0 first: the command, for E5
        /// the bytes FE 01 and the page, and at byte 8 the checksum 255 minus
        /// the sum of bytes 1 to 7. Any other command, or a page past 3,
        /// builds an all-zero buffer that is never sent
        /// (attackshark_pro_diagnostic_model.h:18-25).</summary>
        public static byte[] Request(int command, int page = 0)
        {
            var r = new byte[ReportLength];
            if (command != 0x8F && command != 0x80 && command != 0xE5) return r;
            if (command == 0xE5 && (page < 0 || page >= 4)) return r;
            r[1] = (byte)command;
            if (command == 0xE5)
            {
                r[2] = 0xFE;
                r[3] = 1;
                r[4] = (byte)page;
            }
            int sum = 0;
            for (int i = 1; i < 8; i++) sum += r[i];
            r[8] = (byte)((255 - sum) & 0xFF);
            return r;
        }

        /// <summary>The board ID of an 8F answer, little-endian at bytes 2 to
        /// 5, or 0 when the buffer is not one (attackshark_pro_diagnostic_model.h:26-29).</summary>
        public static uint Identity(ReadOnlySpan<byte> reply)
        {
            if (reply.Length < 6 || reply[0] != 0 || reply[1] != 0x8F) return 0;
            return (uint)(reply[2] | reply[3] << 8 | reply[4] << 16 | reply[5] << 24);
        }

        /// <summary>A depth page: 32 little-endian values after the report ID.
        /// False for a nonzero report ID, for the echo of an E5 request, and
        /// for any value past 4096 (attackshark_pro_diagnostic_model.h:30-36).</summary>
        public static bool Decode(ReadOnlySpan<byte> reply, Span<int> values)
        {
            if (reply.Length < ReportLength || values.Length < 32 || reply[0] != 0) return false;
            if (reply[1] == 0xE5 && reply[2] == 0xFE && reply[3] == 1) return false;
            for (int i = 0; i < 32; i++)
            {
                int v = reply[1 + i * 2] | reply[2 + i * 2] << 8;
                if (v > PlausibleRawLimit) return false;
                values[i] = v;
            }
            return true;
        }

        /// <summary>Whether a board ID belongs on a product ID: its profile's,
        /// or 3151:5029 for board 3650 (attackshark_pro_diagnostic_model.h:8-13).</summary>
        public static bool Known(uint id, ushort productId)
        {
            var p = RongYuanCatalog.FindAttackShark(id);
            return p != null && (p.ProductId == productId || (id == R68AliasBoard && productId == R68AliasProductId));
        }

        /// <summary>Travel units per millimeter for a firmware version
        /// (attackshark_pro_native_model.h:169).</summary>
        public static int Units(int version) => version >= 0x500 ? 200 : version >= 0x300 ? 100 : 10;

        /// <summary>Depth in thousandths against a fixed 3.5 mm full travel,
        /// integer math (attackshark_pro_native_model.h:170).</summary>
        public static int Milli(int raw, int units)
            => units > 0 && raw > 0 ? (int)Math.Min(1000L, raw * 10000L / (units * 35L)) : 0;

        /// <summary>How long a page stays fresh at an exchange delay: 150, 200
        /// or 300 ms at 1, 5 or 10 ms (attackshark_pro_native_model.h:171).</summary>
        public static int FreshBudget(int delay) => Math.Max(FreshMs, Math.Min(10, delay) * 20 + 100);

        /// <summary>The page cycle <paramref name="cycle"/> reads
        /// (attackshark_pro_native_model.h:174-179). With keys in slots 96 to
        /// 127 the pages go Fn page, 0, the other page, 3. Otherwise page 3
        /// comes once every 128 cycles and the rest follow Fn page, 0, 0, 0, 0,
        /// Fn page, other, other.</summary>
        public static int Page(uint cycle, int fnPage, bool fourthPage)
        {
            int other = fnPage == 1 ? 2 : 1;
            if (fourthPage)
            {
                return (cycle % 4) switch
                {
                    0 => fnPage,
                    1 => 0,
                    2 => other,
                    _ => 3,
                };
            }
            if (cycle % 128 == 0) return 3;
            return ((cycle - 1) % 8) switch
            {
                0 or 5 => fnPage,
                6 or 7 => other,
                _ => 0,
            };
        }

        // ── Session ──

        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (!Identify(io, out int usb))
            {
                _precise?.Dispose();
                return false;
            }
            _profile = RongYuanCatalog.FindAttackShark(_identity);
            _usbVersion = usb;
            // Playable only with a profile and a nonzero USB version
            // (attackshark_pro_native_model.h:168). Otherwise no depth
            // command is sent (attackshark_pro_diagnostic.cpp:317-320).
            if (_profile == null || usb == 0)
            {
                _precise?.Dispose();
                return false;
            }

            // RF version: the second of two 80 exchanges, still at the 10 ms
            // delay. A failed read leaves it 0 (attackshark_pro_diagnostic.cpp:323).
            var version = new byte[ReportLength];
            if (Exchange(io, 0x80, 0, version) && Exchange(io, 0x80, 0, version) && version[1] == 0x80)
                _rfVersion = version[2] | version[3] << 8;
            _units = Units(_rfVersion != 0 ? _rfVersion : usb);

            var both = new int[256];
            Array.Copy(_profile.Factory, both, 128);
            Array.Copy(_profile.Fn, 0, both, 128, 128);
            _keyOrder = AnalogKeyboardData.KeysOf(both);

            _delay = PageDelayMs;
            _previousPage = 4;
            _cycle = 0;
            _invalidStreak = 0;
            _pages.Clear();
            return true;
        }

        /// <summary>Session::Identify (attackshark_pro_diagnostic.cpp:269-281).
        /// A failed exchange only fails its attempt.</summary>
        private bool Identify(IAnalogKeyboardTransport io, out int usb)
        {
            usb = 0;
            var a = new byte[ReportLength];
            var b = new byte[ReportLength];
            for (int attempt = 0; attempt < IdentityAttempts; attempt++)
            {
                _delay = IdentityDelayMs;
                if (!Exchange(io, 0x8F, 0, a) || !Exchange(io, 0x8F, 0, b)) continue;
                uint id = Identity(a);
                if (id == Identity(b) && Known(id, _productId))
                {
                    _identity = id;
                    usb = b[8] | b[9] << 8;
                    return true;
                }
            }
            return false;
        }

        /// <summary>One exchange (attackshark_pro_diagnostic.cpp:237-268):
        /// wait, write the request, wait, read the answer into
        /// <paramref name="reply"/>. HallJoy fills the answer buffer with FF
        /// before the read, so bytes the driver did not write fail the depth
        /// check. The transport clears its own buffer instead, so the bytes
        /// past the count the driver reports are set to FF here, which keeps
        /// that check. An unnumbered report's count covers the payload after
        /// the report ID byte.</summary>
        private bool Exchange(IAnalogKeyboardTransport io, int command, int page, byte[] reply)
        {
            var request = Request(command, page);
            if (request[1] == 0) return false;
            _wait(_delay);
            if (!io.SetFeature(request)) return false;
            _wait(_delay);
            Array.Fill(reply, (byte)0xFF);
            reply[0] = 0;
            int n = io.GetFeature(reply);
            if (n < 0) return false;
            for (int i = Math.Max(n + 1, 1); i < reply.Length; i++) reply[i] = 0xFF;
            return true;
        }

        /// <summary>One iteration of HallJoy's play loop
        /// (attackshark_pro_diagnostic.cpp:328-360), then the key set its
        /// parent would publish.</summary>
        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_profile == null) return AnalogPollResult.Failed;

            int page = Page(_cycle, _profile.FnSlot / 32, _profile.FourthPage);
            // Answers carry no page tag: after a page change the first answer
            // is discarded and the page asked again (lines 331-334). A failed
            // exchange ends the worker (exit 6).
            bool flush = page != _previousPage;
            if ((flush && !Exchange(io, 0xE5, page, _reply)) || !Exchange(io, 0xE5, page, _reply))
                return AnalogPollResult.Failed;
            _previousPage = page;

            uint id = Identity(_reply);
            if (id != 0 && Known(id, _productId))
            {
                // A late identity answer is not a depth page (lines 336-337).
                _invalidStreak++;
            }
            else if (Decode(_reply, _values))
            {
                _invalidStreak = 0;
                _pages.Publish(page, _values, _clock());
            }
            else
            {
                _invalidStreak++;
            }

            // Three invalid answers in a row raise the delay from 1 to 5 to
            // 10 ms, and three more at 10 ms end the worker (exit 7). The
            // digital-press trigger beside it never fires in release, where
            // press counting is compiled out (lines 343-348, 729-731).
            if (_invalidStreak >= InvalidStreakLimit)
            {
                if (_delay < 10)
                {
                    _delay = _delay < 5 ? 5 : 10;
                    _invalidStreak = 0;
                }
                else
                {
                    return AnalogPollResult.Failed;
                }
            }

            // Every 1024 iterations the board must still answer 8F with its
            // ID at the 10 ms delay, and the next page read flushes (exit 8
            // on a mismatch, line 357).
            if (++_cycle % RecheckCycles == 0)
            {
                _previousPage = 4;
                int depthDelay = _delay;
                _delay = IdentityDelayMs;
                var proof = new byte[ReportLength];
                bool valid = Exchange(io, 0x8F, 0, proof) && Exchange(io, 0x8F, 0, proof)
                             && Identity(proof) == _identity;
                _delay = depthDelay;
                if (!valid) return AnalogPollResult.Failed;
            }

            _pages.Compose(_profile, _units, FreshBudget(_delay), _clock(), output);
            return AnalogPollResult.Ok;
        }

        /// <summary>HallJoy sends nothing at shutdown: no command it sends
        /// changes the keyboard (attackshark_pro_diagnostic.cpp:361).</summary>
        public override void Stop(IAnalogKeyboardTransport io) => _precise?.Dispose();
    }

    /// <summary>
    /// The four depth pages of one ATTACK SHARK board and the key set they
    /// publish, HallJoy's PumpNative and SharkGet (attackshark_pro_diagnostic.cpp:91-150,
    /// 776-779). Each slot drives two channels, its factory key and its Fn
    /// layer key, and a key reads the larger of every channel bound to it.
    /// </summary>
    public sealed class AttackSharkPages
    {
        private readonly int[] _raw = new int[128];
        private readonly bool[] _seen = new bool[4];
        private readonly long[] _stamp = new long[4];
        private readonly bool[] _expired = new bool[4];
        private readonly AttackSharkLayerLatch _latch = new();

        public void Clear()
        {
            Array.Clear(_raw);
            Array.Clear(_seen);
            Array.Clear(_stamp);
            Array.Clear(_expired);
            _latch.Clear();
        }

        /// <summary>Stores one decoded page received at <paramref name="now"/>
        /// (PublishPage, attackshark_pro_diagnostic.cpp:151-156). Slot =
        /// page * 32 + i (line 110).</summary>
        public void Publish(int page, ReadOnlySpan<int> values, long now)
        {
            if (page < 0 || page >= 4 || values.Length < 32) return;
            for (int i = 0; i < 32; i++) _raw[page * 32 + i] = values[i];
            _seen[page] = true;
            _stamp[page] = now;
            _expired[page] = false;
        }

        /// <summary>Writes the key set into <paramref name="output"/>,
        /// replacing it. A page not refreshed within <paramref name="budgetMs"/>
        /// expires and stays expired until it arrives again (lines 113-115).
        /// Nothing publishes while the Fn page is not fresh (lines 118-130).</summary>
        public void Compose(AttackSharkProfile profile, int units, int budgetMs, long now, AnalogKeyInputState output)
        {
            for (int page = 0; page < 4; page++)
                if (_seen[page] && !_expired[page] && now - _stamp[page] > budgetMs) _expired[page] = true;

            int fnPage = profile.FnSlot / 32;
            bool fnFresh = _seen[fnPage] && !_expired[fnPage];
            bool fnDown = fnFresh && _raw[profile.FnSlot] > 0;
            output.ResetForReuse();
            for (int slot = 0; slot < 128; slot++)
            {
                int page = slot / 32;
                bool fresh = fnFresh && _seen[page] && !_expired[page];
                int raw = fresh ? _raw[slot] : 0;
                int routed = _latch.Route(slot, raw, fnDown, profile);
                int milli = AttackSharkSession.Milli(raw, units);
                if (milli == 0) continue;
                // The base channel gets the depth when the routed key is the
                // factory key, the Fn channel when it is the Fn key (lines 137-138).
                int factory = profile.Factory[slot];
                int fn = profile.Fn[slot];
                if (factory != 0 && routed == factory) Raise(output, factory, milli);
                if (fn != 0 && routed == fn) Raise(output, fn, milli);
            }
        }

        private static void Raise(AnalogKeyInputState output, int code, int milli)
        {
            float depth = milli / 1000f;
            if (depth > output.Get(code)) output.Set(code, depth);
        }
    }

    /// <summary>
    /// The per-slot layer choice, HallJoy's LayerState (attackshark_pro_native_model.h:188-196):
    /// a slot picks the Fn layer when Fn is down and the slot has an Fn key
    /// at the moment its depth leaves 0, and keeps its layer until the depth
    /// returns to 0. Fn released first keeps the F-key, and a key held before
    /// Fn stays itself.
    /// </summary>
    public sealed class AttackSharkLayerLatch
    {
        private readonly byte[] _selected = new byte[128];

        public void Clear() => Array.Clear(_selected);

        /// <summary>The key code slot <paramref name="slot"/> drives at
        /// depth <paramref name="raw"/>.</summary>
        public int Route(int slot, int raw, bool fnDown, AttackSharkProfile profile)
        {
            if (slot < 0 || slot >= 128) return 0;
            if (raw == 0)
            {
                _selected[slot] = 0;
                return profile.Factory[slot];
            }
            if (_selected[slot] == 0) _selected[slot] = (byte)(fnDown && profile.Fn[slot] != 0 ? 2 : 1);
            return _selected[slot] == 2 ? profile.Fn[slot] : profile.Factory[slot];
        }
    }
}
