using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What the HID++ worker has found, swapped whole so the linker
    /// never reads half a scan.</summary>
    internal sealed class HidppSnapshot
    {
        public static readonly HidppSnapshot Empty = new(Array.Empty<HidppUnit>(), Array.Empty<Guid>(), scanned: false);

        public IReadOnlyList<HidppUnit> Units { get; }

        /// <summary>Containers with a slot still to be asked: a device there
        /// may yet answer, so a row there keeps the outputs it had.</summary>
        public IReadOnlyCollection<Guid> PendingContainers { get; }

        /// <summary>False until the worker's first scan has run, so nothing
        /// it has not looked for reads as missing.</summary>
        public bool Scanned { get; }

        public HidppSnapshot(IReadOnlyList<HidppUnit> units, IReadOnlyCollection<Guid> pending, bool scanned = true)
        {
            Units = units;
            PendingContainers = pending;
            Scanned = scanned;
        }

        /// <summary>True when both hold equal units in the same order, the
        /// same pending containers and the same scan state, so publishing one
        /// raises a status change only when a tab would read something new.
        /// The worker builds a new snapshot on every scan and liveness check,
        /// most of them the same as the last, as the direct backends'
        /// <see cref="DirectSnapshot.SameAs"/> expects of its own.</summary>
        public bool SameAs(HidppSnapshot other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other == null || other.Scanned != Scanned
                || other.Units.Count != Units.Count
                || other.PendingContainers.Count != PendingContainers.Count) return false;
            for (int i = 0; i < Units.Count; i++)
                if (!SameUnit(Units[i], other.Units[i])) return false;
            foreach (var container in PendingContainers)
                if (!other.PendingContainers.Contains(container)) return false;
            return true;
        }

        /// <summary>A record compares its arrays by reference, and every scan
        /// builds new ones, so the zones and their locations, the per-key
        /// zones, the model IDs and the key types are compared by value.</summary>
        private static bool SameUnit(HidppUnit a, HidppUnit b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.KeyTypes.Length != b.KeyTypes.Length) return false;
            for (int i = 0; i < a.KeyTypes.Length; i++)
                if (!a.KeyTypes[i].SameAs(b.KeyTypes[i])) return false;
            return a.Zones.AsSpan().SequenceEqual(b.Zones)
                   && a.ZoneLocations.AsSpan().SequenceEqual(b.ZoneLocations)
                   && a.PerKeyZones.AsSpan().SequenceEqual(b.PerKeyZones)
                   && a.ModelIds.AsSpan().SequenceEqual(b.ModelIds)
                   && (a with
                   {
                       Zones = b.Zones, ZoneLocations = b.ZoneLocations, PerKeyZones = b.PerKeyZones,
                       ModelIds = b.ModelIds, KeyTypes = b.KeyTypes,
                   }) == b;
        }
    }

    /// <summary>
    /// The Logitech HID++ worker (#494). One thread owns every HID++
    /// collection, so a probe, a haptic pulse and a color never interleave
    /// on one channel. The first build's worker shape (6b0eeb3c removed its
    /// global lane), with the targets taken from the link table instead of
    /// every device found:
    ///
    /// <para>A unit plays rumble when a row linked to it has a level, the
    /// strongest when two rows link it. The shaper picks the waveform and
    /// the repeat interval (<see cref="HapticRumbleShaper"/>), and silence
    /// resets the interval so the first pulse of new rumble plays at once.</para>
    ///
    /// <para>A unit with a light plan (<see cref="HidppLightPlan"/>) shows
    /// the color its path resolves to
    /// (<see cref="PeripheralOutputs.TryResolveColor(OutputPath, out int)"/>),
    /// held at the level the ruling claim asks for and no firmer than the
    /// unit offers. The first color takes the lighting from the device's
    /// own effect and paints it, a new color paints again no oftener than
    /// the plan allows, and the claim is held again every
    /// <see cref="ReassertMs"/>, since a device that comes back from sleep
    /// boots its onboard profile and drops the claim without a word
    /// (OpenRGB LogitechHIDPP20Controller.cpp:6102-6110). When nothing
    /// claims the path any more, the plan's hand-back returns the lighting.
    /// A zone-only 0x8070 unit gets the frames the first build sent it,
    /// SetSWControl [01 01], the static effect on each zone, [00 00], back
    /// to back and unawaited. A claim at level 2 reads the device's on-board
    /// memory mode first, switches only a device that was in it, and writes
    /// back at the hand-back exactly what it read. A level that changes
    /// while the unit is lit hands back at the old level and claims at the
    /// new one in the same pass.</para>
    ///
    /// <para>The frames of a claim and of a paint wait in a short queue per
    /// unit. A plan that spaces its frames sends one per pass, on a tick of
    /// its own while any queue holds a frame, so the spacing is never a
    /// sleep on the thread that also carries the pulses. A scan and a
    /// liveness check wait for the queues to empty. The frame that shows
    /// what a per-key plan staged is awaited: unanswered, the color is not
    /// taken as shown and the next pass paints again, and after
    /// <see cref="HidppLightPlan.FailedPaintLimit"/> such paints in a row
    /// the unit waits for its next keep-alive.</para>
    ///
    /// <para>The crash path keeps the hand-back frames of every claimed
    /// unit themselves (<see cref="_claimed"/>), because one feature's
    /// release is another feature's power mode call
    /// (<see cref="HidppUnitProtocol"/>). The linker gives a lighting path
    /// only while G HUB is not running, which drives the same devices
    /// through the LED SDK. When G HUB starts while a unit is lit, its path
    /// is withdrawn and the worker forgets the claim without a frame,
    /// whatever its plan and its level, the one case with no hand-back
    /// (<see cref="LeftToLogitechProgram"/>).</para>
    ///
    /// <para>While a unit is linked for rumble, discovery waits for a second
    /// with nothing playing, so a probe does not run into rumble that comes
    /// in bursts. Rumble that starts while a request is still unanswered
    /// does not wait for it. A request waits in slices of the worker's
    /// active tick, and between slices the pulses that came due go out
    /// beside it (<see cref="HidppWaiter"/>, which gives the reasons a pulse
    /// may), so a receiver's pairing read that goes unanswered
    /// (<see cref="HidppReceiverProtocol.RegisterTimeoutMs"/>) and the
    /// request timeout of each slot that stays silent hold a pulse up by one
    /// tick and no more. The probe asks what it asks either way. Discovery
    /// finds any device that answers, with no device list, and asks a
    /// silent slot again after 5, 10, then 15 seconds. On a Bolt, Unifying
    /// or LIGHTSPEED receiver it first reads the receiver's pairing table,
    /// so a slot with no device is never asked and never holds its rows'
    /// outputs (<see cref="HidppReceiverProtocol"/>).
    /// A found device is asked for its configuration every 30 seconds while
    /// quiet: one that does not answer went to sleep, and its slot reopens so
    /// the next scan finds it when it wakes.</para>
    ///
    /// <para>A claimed unit the worker lets go of that way, or because a
    /// write to its path failed, is owed its hand-back
    /// (<see cref="_owed"/>). A device that slept restarts on its own
    /// effect and is owed nothing in fact. One that missed a single answer
    /// still holds the claim, and at level 2 is still in host mode. The
    /// worker cannot tell the two apart, so the hand-back stays on the
    /// crash path's record and is paid when the unit is found again,
    /// before anything else goes to it, and at a stop. It ends with no
    /// frame when a Logitech program starts or the unit's collection
    /// leaves.</para>
    /// </summary>
    internal sealed class HidppBackend : IDisposable
    {
        internal const int FirstScanMs = 5000;
        internal const int RescanMs = 30000;
        internal const int CheckMs = 30000;
        internal const int QuietMs = 1000;

        /// <summary>The worker's cadence while a row linked to a unit for
        /// rumble holds a level above zero, and otherwise. A silent linked
        /// unit waits the idle tick: <see cref="PeripheralOutputs.HapticsChanged"/>
        /// wakes the worker when its rumble starts.</summary>
        internal const int ActiveTickMs = 10;
        internal const int IdleTickMs = 100;

        /// <summary>The worker's cadence while a unit's queue holds a frame:
        /// the spacing a 0x8071 plan keeps between its frames.</summary>
        internal const int FrameTickMs = ClusterPlan.ClusterGapMs;

        /// <summary>How long a new worker waits for the one a timed-out Stop
        /// left inside a probe, before it opens the collections anyway.</summary>
        internal const int PredecessorJoinMs = 5000;

        /// <summary>How often a lit unit's claim and color are sent again.</summary>
        internal const int ReassertMs = 5000;

        /// <summary>The fastest a zone unit's color is repainted, the effects
        /// dispatcher's own animation tick.</summary>
        internal const int PaintMs = 33;

        private sealed class Channel
        {
            public IHidppChannel Io;
            public HidppPathState State;
            public Guid Container;
            public readonly List<HidppUnit> Units = new();
            public readonly Dictionary<byte, (int Level, long NextPlay)> Play = new();
            public readonly Dictionary<byte, Light> Lights = new();
        }

        /// <summary>One unit's lighting as the worker last left it: its
        /// plan, whether the lighting is claimed, at what level and with
        /// what mode read before it, when the claim was last held, the
        /// color last shown and when its paint began, and the frames of the
        /// claim or the paint still to go out. The claim keeps its own
        /// time, so a color that changes faster than the re-assert interval
        /// never holds the claim back.</summary>
        private sealed class Light
        {
            public HidppLightPlan Plan;

            /// <summary>The unit the plan was made for, which says whether
            /// a hand-back owed at its slot is owed to the device found
            /// there later (<see cref="SameDevice"/>).</summary>
            public HidppUnit Unit;
            public bool Claimed;
            public int Level;
            public byte SavedMode;

            /// <summary>When the claim was last made or held, or, for a unit
            /// whose claim is still waiting on its mode read, last tried.</summary>
            public long ClaimedAt;

            /// <summary>What hands the device back from the claim as it
            /// stands, kept from before the claim's first frame.</summary>
            public HidppStep[] HandBack;

            /// <summary>A paint has been shown in full since the claim, so
            /// <see cref="Rgb"/> is what the device shows.</summary>
            public bool Painted;
            public int Rgb;
            public long SentAt;

            /// <summary>What each light of <see cref="ShownMap"/> shows after
            /// the last paint (#557), or null when the unit was last painted
            /// one color.</summary>
            public int[] Shown;
            public LightMap ShownMap;

            /// <summary>The frames in flight, the next one's place, the
            /// earliest it may go out and the color they carry, with the
            /// color of each light for a paint light by light.</summary>
            public HidppStep[] Queue;
            public int Next;
            public long NextAt;
            public int QueuedRgb;
            public int[] QueuedColors;
            public LightMap QueuedMap;

            /// <summary>The claim's first paint is not out yet. The claim is
            /// logged once it is.</summary>
            public bool Claiming;

            /// <summary>Paints in a row whose awaited frame went
            /// unanswered, or, before a claim, mode reads in a row that
            /// did.</summary>
            public int FailedPaints;
        }

        /// <summary>One claimed unit as the crash path needs it: where to
        /// write, and the frames that hand it back.</summary>
        private readonly record struct ClaimRecord(string Path, byte DeviceIndex, byte[][] HandBack);

        /// <summary>The units holding a claim now, each with its hand-back
        /// frames, for the crash path, which cannot wait on this worker. The
        /// frames themselves and never an index to build one from: a
        /// 0x8070 release sent to a 0x8071 index sets the power mode and
        /// releases nothing. Swapped whole.</summary>
        private ClaimRecord[] _claimed = Array.Empty<ClaimRecord>();

        /// <summary>The hand-backs owed to units the worker let go of while
        /// their devices may still hold the claim, by channel path and
        /// device index. Each is the claim as it stood: its plan, its level,
        /// the mode read before it and the frames that hand it back. Worker
        /// thread only.</summary>
        private readonly Dictionary<string, Dictionary<byte, Light>> _owed = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The bound on each write that opens a handle of its own:
        /// the crash path's, and the stop path's for what is owed.</summary>
        internal const int OneShotMs = 200;

        private readonly Func<IReadOnlyList<VendorHidCollection>> _enumerate;
        private readonly Func<VendorHidCollection, VendorHidCollection, VendorHidCollection, IHidppChannel> _open;
        private readonly Func<string, Guid> _containerOf;
        private readonly int _activeTickMs;
        private readonly int _idleTickMs;
        private readonly int _reassertMs;
        private readonly Func<string, byte[], int, bool> _writeOnce;
        private readonly int _predecessorJoinMs;
        private readonly Func<long> _ticks;
        private readonly AutoResetEvent _wake = new(false);

        /// <summary>Each present path's container ID, looked up once per
        /// appearance that answers it. Worker thread only.</summary>
        private readonly Dictionary<string, Guid> _containers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The light map of each unit painted light by light (#557),
        /// by unit key: the map from the board's zones, and that map with
        /// the names Map Key Lights learned, the one published. Worker
        /// thread only.</summary>
        private readonly Dictionary<string, (LightMap Base, LightMap Map)> _maps = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The <see cref="LearnedKeyMaps.Version"/> the maps were
        /// last given their names at.</summary>
        private int _mapsVersion = -1;

        /// <summary>A plan fell back to fewer lights, so the maps are owed
        /// a sync on the next pass. Worker thread only.</summary>
        private bool _mapsDirty;

        private HidppSnapshot _snapshot = HidppSnapshot.Empty;
        private Thread _thread;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _scans;
        private int _passes;
        private int _turnsBesideARequest;
        private int _waitMs;
        private long _scanQuietMs;

        /// <summary>The newest worker thread of any instance, which the next
        /// one waits for. Static because the collections it holds are the
        /// machine's.</summary>
        private static Thread s_lastWorker;

        /// <summary>The worker's clock: milliseconds from the
        /// high-resolution counter. Environment.TickCount64 steps by 10 to
        /// 16 ms on Windows, which cannot keep two frames 8 ms apart.</summary>
        private static readonly Stopwatch s_clock = Stopwatch.StartNew();

        public HidppBackend()
            : this(null, null, null, ActiveTickMs, IdleTickMs) { }

        /// <summary>Test seam: the enumeration (every vendor collection, the
        /// long ones and their short siblings), the channel opener (a long
        /// collection and its receiver's short one, or null, with
        /// <paramref name="openWithVeryLong"/> in its place for a test of a
        /// keyboard's very long collection), the container
        /// lookup, the lighting re-assert interval, the one-shot writer the
        /// crash path uses, the wait for a predecessor worker and the
        /// worker's clock, so a bench drives the worker against scripted
        /// channels and moves its time by hand.</summary>
        internal HidppBackend(Func<IReadOnlyList<VendorHidCollection>> enumerate,
            Func<VendorHidCollection, VendorHidCollection, IHidppChannel> open, Func<string, Guid> containerOf,
            int activeTickMs, int idleTickMs, int reassertMs = ReassertMs,
            Func<string, byte[], int, bool> writeOnce = null, int predecessorJoinMs = PredecessorJoinMs,
            Func<long> ticks = null,
            Func<VendorHidCollection, VendorHidCollection, VendorHidCollection, IHidppChannel> openWithVeryLong = null)
        {
            _enumerate = enumerate ?? VendorHidRuntime.Enumerate;
            _open = openWithVeryLong
                    ?? (open != null ? (c, s, v) => open(c, s) : (c, s, v) => HidppChannel.Open(c, s, v));
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _activeTickMs = activeTickMs;
            _idleTickMs = idleTickMs;
            _reassertMs = reassertMs;
            _writeOnce = writeOnce ?? RawHidOutput.WriteOnce;
            _predecessorJoinMs = predecessorJoinMs;
            _ticks = ticks ?? (() => s_clock.ElapsedMilliseconds);
        }

        public HidppSnapshot Snapshot => Volatile.Read(ref _snapshot);

        /// <summary>Raised from the worker after it publishes a new snapshot,
        /// so the host's link pass picks up a unit or a charge at once. Any
        /// thread. Subscribers must not block.</summary>
        public event Action SnapshotChanged;

        /// <summary>Scans this worker ran.</summary>
        internal int ScanCount => Volatile.Read(ref _scans);

        /// <summary>How long nothing had played when the last scan started,
        /// so the test of the quiet second reads what the worker measured
        /// and times nothing.</summary>
        internal long ScanQuietMs => Volatile.Read(ref _scanQuietMs);

        /// <summary>Passes the worker made, for the test of its cadence.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        /// <summary>Turns the worker took beside a request that was waiting
        /// for its answer, so a test can wait on one and time nothing.</summary>
        internal int TurnsBesideARequest => Volatile.Read(ref _turnsBesideARequest);

        /// <summary>The wait the worker asked for at the end of its last
        /// pass, so the test of its cadence reads the tick it chose and
        /// times nothing.</summary>
        internal int WaitMs => Volatile.Read(ref _waitMs);

        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>What the crash path would write now, unit by unit: the
        /// path and the hand-back frames on record. For tests.</summary>
        internal (string Path, byte[][] HandBack)[] ClaimedRecords
        {
            get
            {
                var claimed = Volatile.Read(ref _claimed);
                var records = new (string, byte[][])[claimed.Length];
                for (int i = 0; i < claimed.Length; i++) records[i] = (claimed[i].Path, claimed[i].HandBack);
                return records;
            }
        }

        /// <summary>A HID++ long collection with the 20-byte report every
        /// frame here is, on a usage OpenRGB's detector drives. Mice and
        /// receivers sit on usage page 0xFF00 usage 2, keyboards on 0xFF43
        /// usage 0x0602 (LogitechControllerDetect.cpp:162-166 and 1530), and
        /// on 0xFF43 usage 0x0202 are the headsets OpenRGB registers over USB
        /// (1553 and 1763-1768) and the devices paired over Bluetooth LE
        /// (OpenLogi openlogi-hid transport.rs:181-183 and 198-202,
        /// HIDPP_LONG_COLLECTIONS), a page OpenRGB's HID++ 2.0 detector takes
        /// over Bluetooth with any usage (1783-1784 and 1795). The 64-byte
        /// usage 0x0604 and the Centurion page 0xFFA0, whose frames this
        /// channel does not speak, stay out.</summary>
        internal static bool IsHidppLong(VendorHidCollection c)
            => c.VendorId == HidppHapticProtocol.VendorId
               && c.InputReportLength == HidppHapticProtocol.LongReportLength
               && ((c.UsagePage == HidppHapticProtocol.UsagePage && c.Usage == HidppHapticProtocol.LongUsage)
                   || (c.UsagePage == 0xFF43 && (c.Usage == 0x0602 || c.Usage == 0x0202)));

        public void Start()
        {
            if (_thread != null) return;
            var run = new WorkerRun();
            _run = run;
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralHidpp" };
            _thread.Start();
            PeripheralOutputs.HapticsChanged += Wake;
            PeripheralOutputs.LightingChanged += Wake;
            LearnedKeyMaps.Changed += OnLearned;
        }

        public void Stop()
        {
            PeripheralOutputs.HapticsChanged -= Wake;
            PeripheralOutputs.LightingChanged -= Wake;
            LearnedKeyMaps.Changed -= OnLearned;
            if (_thread == null) return;
            _run.Stop = true;
            Wake();
            try { _thread.Join(3000); } catch { }
            _thread = null;
            _run = null;
        }

        private void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        /// <summary>A name learned with Map Key Lights (#557) wakes the
        /// worker, which gives its maps the names on its next pass.</summary>
        private void OnLearned(object sender, EventArgs e) => Wake();

        private void Worker(WorkerRun run)
        {
            // A worker a timed-out Stop left inside a probe still holds the
            // collections, and its finally hands its units back, so a new
            // worker waits for it before it opens anything: the bounded join
            // SensaHapticsService makes for its predecessor and the orphan
            // wait LedSdkBackend makes. A predecessor that never comes back
            // holds the new worker only for that bound.
            var previous = Interlocked.Exchange(ref s_lastWorker, Thread.CurrentThread);
            if (previous != null && previous != Thread.CurrentThread && previous.IsAlive
                && !previous.Join(_predecessorJoinMs))
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL HID++ previous worker still running after {_predecessorJoinMs} ms, starting anyway");
            var channels = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
            var states = new Dictionary<string, HidppPathState>(StringComparer.OrdinalIgnoreCase);
            try
            {
                long start = _ticks();
                long nextScan = start, nextCheck = start + CheckMs, quietSince = start;

                // What a request does while it waits for its answer: the
                // pulses that came due go out beside it, so a slot that
                // never answers costs rumble one tick and not its timeout.
                // Pulses only: nothing here asks a question, so it cannot
                // run inside itself, and a channel a pulse fails on is left
                // for the pass that owns the channels to close.
                bool letting = false;
                var waiter = new HidppWaiter(_ticks, _activeTickMs, () =>
                {
                    if (letting || run.Stop) return;
                    letting = true;
                    try
                    {
                        long at = _ticks();
                        long unused = 0;
                        PlayHaptics(channels, states, PeripheralOutputs.Links, at, out bool played, out _, ref unused,
                            besideARequest: true);
                        if (played) quietSince = at;
                        Interlocked.Increment(ref _turnsBesideARequest);
                    }
                    finally { letting = false; }
                });

                while (!run.Stop)
                {
                    Interlocked.Increment(ref _passes);
                    long now = _ticks();
                    var links = PeripheralOutputs.Links;
                    bool active = PlayHaptics(channels, states, links, now, out bool playing, out bool linked, ref nextScan);
                    if (playing) quietSince = now;
                    if (_mapsDirty || LearnedKeyMaps.Version != _mapsVersion) SyncMaps(channels);
                    bool queued = PaintLighting(run, channels, states, links, now, ref nextScan);
                    bool quiet = now - quietSince >= QuietMs;

                    // While a unit is linked for rumble, a scan waits for a
                    // second with nothing playing, so a probe does not run
                    // into rumble that comes in bursts. It also waits for a
                    // claim or a paint in flight, which would otherwise
                    // stand half sent for as long as the scan takes.
                    if (!queued && now >= nextScan && (!linked || quiet))
                    {
                        Volatile.Write(ref _scanQuietMs, now - quietSince);
                        bool dropped = Scan(run, channels, states, now, waiter);
                        // A slot still waiting for its device, one asleep at
                        // launch or one that went to sleep, is asked again on
                        // its own backoff, so the scan comes as often as that
                        // can matter. A lit unit this scan dropped is owed its
                        // hand-back, so its path is asked again as soon. With
                        // neither, a slow rescan finds what gets plugged in or
                        // paired.
                        nextScan = _ticks() + (dropped || AnyWaiting(states) ? FirstScanMs : RescanMs);
                    }
                    if (!queued && now >= nextCheck && quiet)
                    {
                        // A device that went to sleep is asked for again on
                        // the fast cadence, so it is found soon after it wakes.
                        if (Check(run, channels, states))
                            nextScan = Math.Min(nextScan, _ticks() + FirstScanMs);
                        nextCheck = _ticks() + CheckMs;
                    }

                    int wait = active ? _activeTickMs : _idleTickMs;
                    if (queued) wait = Math.Min(wait, FrameTickMs);
                    Volatile.Write(ref _waitMs, wait);
                    // A Dispose whose Stop gave up its join releases the wake
                    // under a worker still finishing a probe, which ends here.
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL HID++ worker fault: " + ex.GetType().Name);
            }
            finally
            {
                foreach (var channel in channels.Values)
                {
                    // Lighting goes back to the device's own effect before
                    // the channel closes, so stopping the engine leaves no
                    // mouse frozen on the last color.
                    try { ReleaseLighting(channel); } catch { }
                    try { channel.Io.Dispose(); } catch { }
                }
                // What is still owed goes out through a handle of its own,
                // as on the crash path: its unit was not found again, so
                // the worker has no channel that is known to reach it.
                try { PayOwedNow(); } catch { }
                _owed.Clear();
                foreach (string key in _maps.Keys) PeripheralLightMaps.Remove(new OutputPath(OutputFamily.HidppUnit, key));
                _maps.Clear();
                _mapsVersion = -1;
                Volatile.Write(ref _claimed, Array.Empty<ClaimRecord>());
                Volatile.Write(ref _snapshot, HidppSnapshot.Empty);
            }
        }

        /// <summary>One pass of colors over every unit with a light plan.
        /// True while any unit still has a frame waiting to go out.</summary>
        private bool PaintLighting(WorkerRun run, Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states,
            LinkTable links, long now, ref long nextScan)
        {
            List<string> dead = null;
            bool claims = false, queued = false;
            // A Logitech program that runs has every Logitech device, the
            // ones not found again among them. A hand-back that landed
            // after its claim would clear that claim, so what is owed ends
            // here with no frame.
            if (_owed.Count > 0 && PeripheralOutputs.Presence.LogitechGHubRunning)
            {
                _owed.Clear();
                claims = true;
            }
            foreach (var pair in channels)
            {
                var channel = pair.Value;
                foreach (var unit in channel.Units)
                {
                    if (!channel.Lights.TryGetValue(unit.DeviceIndex, out var light))
                    {
                        var plan = HidppLightPlan.For(unit);
                        if (plan == null) continue;
                        channel.Lights[unit.DeviceIndex] = light = new Light { Plan = plan, Unit = unit };
                    }
                    var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
                    int rgb = 0;
                    PeripheralOutputs.Claimant ruler = default;
                    bool wanted = links.ByPath.ContainsKey(path) && PeripheralOutputs.TryResolveColor(path, out rgb, out ruler);
                    bool ok = true;
                    if (Owed(pair.Key, unit.DeviceIndex) is Light debt)
                    {
                        if (!SameDevice(debt.Unit, unit))
                        {
                            // Another device answers at the slot. The frames
                            // on the books name the features of the one that
                            // left, at its indexes, and would mean something
                            // else here, so nothing is sent.
                            Settle(pair.Key, unit.DeviceIndex);
                            claims = true;
                            PadForge.Engine.SdlDiagLog.WriteLine(
                                $"PERIPHERAL HID++ '{unit.Name}' is another device than the one owed a hand-back at its slot, which is dropped");
                        }
                        else if (PeripheralOutputs.TakenByOpenRgb(path))
                        {
                            // OpenRGB holds the device (#557), and a
                            // hand-back would land on its lighting, so what
                            // is owed ends with no frame, as when G HUB runs.
                            Settle(pair.Key, unit.DeviceIndex);
                            claims = true;
                            PadForge.Engine.SdlDiagLog.WriteLine(
                                $"PERIPHERAL HID++ '{unit.Name}' is held by OpenRGB, so the hand-back it was owed is dropped");
                        }
                        else
                        {
                            // The unit is back, and its hand-back is owed
                            // from before the worker let go of it. It is
                            // paid before anything else goes to the unit, a
                            // new claim included, which then starts from the
                            // device's own mode. A stopping worker leaves it
                            // to its finally.
                            if (run.Stop) return false;
                            ok = PayBack(channel.Io, debt, light);
                            if (ok)
                            {
                                Settle(pair.Key, unit.DeviceIndex);
                                claims = true;
                                PadForge.Engine.SdlDiagLog.WriteLine(
                                    $"PERIPHERAL HID++ '{unit.Name}' lighting handed back, owed since the unit was let go");
                            }
                        }
                    }
                    if (ok && wanted)
                    {
                        // A stopping worker claims nothing more and paints
                        // nothing more. Its finally hands back what it holds.
                        if (run.Stop) return false;
                        // The level travels with the ruling claim, and a unit
                        // is never held more firmly than it offers.
                        int level = Math.Clamp(Math.Min(ruler.Level, unit.MaxClaimLevel), 0, light.Plan.MaxLevel);
                        var map = LightsFor(unit, light);
                        int[] colors = map != null && ruler.Keys is { IsEmpty: false } keys
                            ? LightRenderer.Render(map, rgb, keys)
                            : null;
                        ok = Drive(channel, unit, light, rgb, map, colors, level, now, ref claims);
                    }
                    else if (light.Claimed && LeftToLogitechProgram(path))
                    {
                        // G HUB or Logitech Gaming Software started and the
                        // linker took the unit's path away. The program
                        // lights the device from here on, and a hand-back
                        // that landed after its own claim would clear that
                        // claim, and at level 2 undo the mode it just set,
                        // so the worker lets go without a frame.
                        Forget(light);
                        claims = true;
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL HID++ '{unit.Name}' lighting left to the Logitech program that started");
                    }
                    else if (light.Claimed)
                    {
                        // A hand-back whose write failed is still owed: the
                        // path is dropped below with the claim on its books.
                        ok = HandBack(channel.Io, light);
                        if (ok)
                        {
                            Forget(light);
                            claims = true;
                            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HID++ '{unit.Name}' lighting handed back");
                        }
                    }
                    if (light.Queue != null) queued = true;
                    if (!ok)
                    {
                        (dead ??= new List<string>()).Add(pair.Key);
                        break;
                    }
                }
            }
            if (dead != null) DropDead(channels, states, dead, now, ref nextScan);
            else if (claims) PublishClaimed(channels);
            return queued;
        }

        /// <summary>The light map a unit is painted by (#557), or null for a
        /// unit painted one color: a plan that paints one color, or a board
        /// with no map of more than one light.</summary>
        private LightMap LightsFor(HidppUnit unit, Light light)
            => light.Plan.PaintsLights && _maps.TryGetValue(unit.Key, out var entry) && entry.Map.PerLight ? entry.Map : null;

        /// <summary>Whether the unit shows what the claim asks for now: one
        /// color, or <paramref name="colors"/> light by light on the map
        /// they were painted by.</summary>
        private static bool Showing(Light light, int rgb, LightMap map, int[] colors)
            => colors == null
                ? light.Shown == null && rgb == light.Rgb
                : light.Shown != null && ReferenceEquals(light.ShownMap, map) && LightRenderer.Same(colors, light.Shown);

        /// <summary>The frames of a paint after the claim's own: one color,
        /// or light by light. A paint light by light sends the lights whose
        /// color changed when the unit shows a picture on the same map, and
        /// the whole picture otherwise.</summary>
        private static HidppStep[] PaintSteps(Light light, int rgb, LightMap map, int[] colors)
        {
            bool full = !light.Painted;
            if (colors == null) return light.Plan.Paint(rgb, full);
            int[] shown = !full && light.Shown != null && ReferenceEquals(light.ShownMap, map) ? light.Shown : null;
            return light.Plan.PaintLights(map, colors, shown, full);
        }

        /// <summary>One lit unit's turn. False when a write failed, which
        /// means the collection is gone.</summary>
        private bool Drive(Channel channel, HidppUnit unit, Light light, int rgb, LightMap map, int[] colors, int level,
            long now, ref bool claims)
        {
            // A claim at another level is a claim to make over: the device
            // goes back at the level it was held at, and the same pass
            // claims it at the new one.
            if (light.Claimed && light.Level != level)
            {
                if (!HandBack(channel.Io, light)) return false;
                Forget(light);
                claims = true;
            }

            if (light.Queue != null) return Send(channel.Io, unit, light, now, ref claims);
            if (!light.Claimed) return Claim(channel, unit, light, rgb, map, colors, level, now, ref claims);

            if (now - light.ClaimedAt >= _reassertMs)
                return KeepAlive(channel, unit, light, rgb, map, colors, now, ref claims);

            // A new color, or a color whose paint was not answered. A paint
            // that failed is made again at once, since the floor between
            // paints passed while it waited, until too many have failed in a
            // row. Then the keep-alive is the next try.
            if (light.Painted && Showing(light, rgb, map, colors)) return true;
            if (light.FailedPaints >= HidppLightPlan.FailedPaintLimit) return true;
            if (light.FailedPaints == 0 && now - light.SentAt < light.Plan.PaintMs) return true;
            Enqueue(light, PaintSteps(light, rgb, map, colors), rgb, map, colors, now);
            return Send(channel.Io, unit, light, now, ref claims);
        }

        /// <summary>Takes a unit's lighting and queues its first color.</summary>
        private bool Claim(Channel channel, HidppUnit unit, Light light, int rgb, LightMap map, int[] colors, int level,
            long now, ref bool claims)
        {
            var plan = light.Plan;
            byte savedMode = 0;
            byte[] modeRead = level >= 2 ? plan.ModeRead : null;
            if (modeRead != null)
            {
                // A read that keeps going unanswered is made again with the
                // keep-alive's patience and no oftener.
                if (light.FailedPaints >= HidppLightPlan.FailedPaintLimit && now - light.ClaimedAt < _reassertMs) return true;

                // The mode is read before it is changed, so a device its
                // owner keeps in host mode stays there and the hand-back
                // writes back exactly what was read. libratbag reads before
                // it writes (hidpp20.c:2260-2271), where OpenRGB and Solaar
                // restore the on-board mode unread.
                var reply = Ask(channel.Io, modeRead, HidppHapticProtocol.TimeoutMs(channel.Io.Bluetooth));
                if (reply.Kind == HidppReplyKind.WriteFailed) return false;
                if (reply.Kind == HidppReplyKind.Answer)
                {
                    savedMode = plan.ModeOf(reply);
                }
                else if (HidppUnitProbe.Missed(reply))
                {
                    // No answer, no claim at this level: with no mode on
                    // record nothing could be put back. The read is made
                    // again.
                    light.FailedPaints++;
                    light.ClaimedAt = now;
                    return true;
                }
                else
                {
                    // The device refuses the read. Its mode stays unknown,
                    // so it is not switched and nothing is owed it at the
                    // hand-back: the claim goes ahead as level 1 makes it.
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL HID++ '{unit.Name}' refused the read of its on-board memory mode"
                        + $" (error 0x{reply.ErrorCode:X2}), claiming without the switch");
                }
            }

            // The crash path learns of a claim before the claim goes out,
            // so no moment exists in which the device is claimed and the
            // hand-back does not know it. A claim that then fails is handed
            // back for nothing.
            light.HandBack = plan.HandBack(level, savedMode);
            light.Level = level;
            light.SavedMode = savedMode;
            light.Claimed = true;
            light.ClaimedAt = now;
            light.FailedPaints = 0;
            NoteClaimed(channel.Io.Path, unit.DeviceIndex, HidppLightPlan.Frames(light.HandBack));

            EnqueueClaim(light, rgb, map, colors, now);
            return Send(channel.Io, unit, light, now, ref claims);
        }

        /// <summary>Queues the frames of a unit's claim, at the level and
        /// with the mode on its books, and its first paint behind them, one
        /// color or light by light.</summary>
        private static void EnqueueClaim(Light light, int rgb, LightMap map, int[] colors, long now)
        {
            var claim = light.Plan.Claim(light.Level, light.SavedMode);
            var paint = colors == null ? light.Plan.Paint(rgb, full: true) : light.Plan.PaintLights(map, colors, null, full: true);
            var steps = new HidppStep[claim.Length + paint.Length];
            claim.CopyTo(steps, 0);
            paint.CopyTo(steps, claim.Length);
            Enqueue(light, steps, rgb, map, colors, now);
            light.Painted = false;
            light.Claiming = true;
        }

        /// <summary>Holds a claim on its interval. A plan that reads its
        /// claim first (<see cref="HidppLightPlan.ClaimRead"/>) repeats
        /// nothing that would take the display from its per-key frames, and
        /// claims in full again only when the read shows the claim gone.</summary>
        private bool KeepAlive(Channel channel, HidppUnit unit, Light light, int rgb, LightMap map, int[] colors, long now,
            ref bool claims)
        {
            var plan = light.Plan;
            light.ClaimedAt = now;
            // A read is made over Bluetooth too, with that link's own
            // patience: one that goes unanswered there costs this keep-alive
            // its frames and no more, and with no read a keyboard that slept
            // and woke would never be claimed again.
            int patience = channel.Io.Bluetooth ? HidppHapticProtocol.TimeoutMs(true) : HidppLightPlan.AckTimeoutMs;
            byte[] claimRead = plan.ClaimRead;
            if (claimRead == null && plan.SwitchesMode(light.Level, light.SavedMode))
            {
                // A plan with no claim read holds its claim by repeating
                // it, which takes the lighting again on a device that slept
                // and woke. The mode is another matter: the device woke in
                // its on-board memory mode, and the mode frame is sent once
                // per claim. So the mode is read, and a device found back
                // in the mode the claim switched it from is claimed again
                // as it was first claimed, switch included.
                var mode = Ask(channel.Io, plan.ModeRead, patience);
                if (mode.Kind == HidppReplyKind.WriteFailed) return false;
                if (mode.Kind == HidppReplyKind.Answer && plan.ModeOf(mode) == light.SavedMode)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL HID++ '{unit.Name}' was back in its on-board memory mode, claiming again");
                    light.FailedPaints = 0;
                    EnqueueClaim(light, rgb, map, colors, now);
                    return Send(channel.Io, unit, light, now, ref claims);
                }
            }
            if (claimRead != null)
            {
                var reply = Ask(channel.Io, claimRead, patience);
                if (reply.Kind == HidppReplyKind.WriteFailed) return false;
                // Asleep or away behind its receiver: every frame now would
                // be one more wait, and the next keep-alive asks again.
                if (HidppUnitProbe.Missed(reply)) return true;
                if (reply.Kind == HidppReplyKind.Answer && !plan.ClaimStands(reply))
                {
                    // The device slept and woke, or was let go by someone
                    // else. The claim is made again as it was first made,
                    // with the mode read then. That read still says what
                    // the device is owed at the hand-back, whatever mode it
                    // is in now, so no new read is made and the crash path's
                    // record does not change.
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HID++ '{unit.Name}' lighting claim was gone, claiming again");
                    light.FailedPaints = 0;
                    EnqueueClaim(light, rgb, map, colors, now);
                    return Send(channel.Io, unit, light, now, ref claims);
                }
            }
            var keep = colors == null
                ? plan.KeepAlive(light.Level, rgb, light.Painted)
                : plan.KeepAliveLights(light.Level, map, colors, light.Painted);
            Enqueue(light, keep, rgb, map, colors, now);
            return Send(channel.Io, unit, light, now, ref claims);
        }

        /// <summary>Puts the frames of one claim or one paint in a unit's
        /// queue, in place of anything still in it. The floor between paints
        /// counts from here, where the paint begins.</summary>
        private static void Enqueue(Light light, HidppStep[] steps, int rgb, LightMap map, int[] colors, long now)
        {
            light.Queue = steps;
            light.Next = 0;
            light.QueuedRgb = rgb;
            light.QueuedMap = colors == null ? null : map;
            light.QueuedColors = colors;
            light.SentAt = now;
        }

        /// <summary>Sends what a unit has waiting: every frame at once for a
        /// plan that spaces none, and one frame for a plan that does, the
        /// next following on the worker's frame tick. False when a write
        /// failed.</summary>
        private bool Send(IHidppChannel io, HidppUnit unit, Light light, long now, ref bool claims)
        {
            while (light.Queue != null)
            {
                if (now < light.NextAt) return true;
                var step = light.Queue[light.Next];
                var outcome = Put(io, step);
                if (outcome == Outcome.Failed) return false;
                // The next frame to the unit keeps its distance from this
                // one, whatever became of this one. The time is read after
                // the frame, since an awaited one took its answer's time.
                int gap = light.Plan.FrameGapMs;
                if (gap > 0) light.NextAt = _ticks() + gap;
                switch (outcome)
                {
                    case Outcome.Unanswered:
                        // The device did not say the frame landed, so the
                        // color is not taken as shown and a later pass
                        // paints again.
                        light.Queue = null;
                        light.FailedPaints++;
                        return true;
                    case Outcome.Refused when step.Kind == HidppStepKind.Select:
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL HID++ '{unit.Name}' refused the Host streaming effect, painting on");
                        break;
                    case Outcome.Refused when step.Kind == HidppStepKind.Fill:
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL HID++ '{unit.Name}' refused a key type fill, painting on");
                        break;
                    case Outcome.Refused:
                        var fallback = light.Plan.FallsBackOn(step.Kind) ? light.Plan.WithoutPerKey : null;
                        if (fallback == null)
                        {
                            light.Queue = null;
                            light.FailedPaints++;
                            return true;
                        }
                        // The device will not take a per-key frame. It is
                        // painted one color in full again, its clusters too,
                        // since choosing Host streaming may have taken the
                        // display from them, and it stays on one color from
                        // here on.
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL HID++ '{unit.Name}' refused a per-key frame, lighting it one color");
                        light.Plan = fallback;
                        Enqueue(light, fallback.Paint(light.QueuedRgb, full: true), light.QueuedRgb, null, null, now);
                        // Its keys can no longer be painted one by one, so
                        // the Lighting tab stops offering them, and offers
                        // the zones of the plan it falls back to where that
                        // plan has them (#557).
                        if (_maps.Remove(unit.Key))
                            PeripheralLightMaps.Remove(new OutputPath(OutputFamily.HidppUnit, unit.Key));
                        _mapsDirty = true;
                        return true;
                }
                if (++light.Next < light.Queue.Length)
                {
                    if (gap == 0) continue;
                    return true;
                }
                light.Queue = null;
                light.Rgb = light.QueuedRgb;
                light.Shown = light.QueuedColors;
                light.ShownMap = light.QueuedMap;
                light.Painted = true;
                light.FailedPaints = 0;
                if (light.Claiming)
                {
                    light.Claiming = false;
                    claims = true;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL HID++ '{unit.Name}' lighting claimed at level {light.Level}");
                }
            }
            return true;
        }

        /// <summary>What became of one frame of a plan.</summary>
        private enum Outcome
        {
            Sent,
            /// <summary>The write itself failed: the collection is gone.</summary>
            Failed,
            /// <summary>An awaited frame the device did not answer, or
            /// answered busy to twice.</summary>
            Unanswered,
            /// <summary>An awaited frame the device answered with an error.</summary>
            Refused,
        }

        /// <summary>Sends one frame of a plan. A frame the plan awaits goes
        /// out as a request with the short wait of
        /// <see cref="HidppLightPlan.AckTimeoutMs"/>. Over Bluetooth it is
        /// written and not awaited: an idle Bluetooth LE link loses answers
        /// (OpenLogi issue 1000, where two requests of five timed out), and
        /// a wait there would stall the paint, not confirm it.</summary>
        private static Outcome Put(IHidppChannel io, HidppStep step)
        {
            if (step.Kind == HidppStepKind.Write || io.Bluetooth)
                return io.Write(step.Frame) ? Outcome.Sent : Outcome.Failed;
            var reply = Ask(io, step.Frame, HidppLightPlan.AckTimeoutMs);
            if (reply.Kind == HidppReplyKind.Answer) return Outcome.Sent;
            if (reply.Kind == HidppReplyKind.WriteFailed) return Outcome.Failed;
            return HidppUnitProbe.Missed(reply) ? Outcome.Unanswered : Outcome.Refused;
        }

        /// <summary>A frame as a request: the same bytes, long or very long,
        /// with the channel waiting for the answer that repeats its header.</summary>
        private static HidppReply Ask(IHidppChannel io, byte[] frame, int timeoutMs) => io.RequestFrame(frame, timeoutMs);

        /// <summary>Hands one claimed unit back to its own lighting, now,
        /// with whatever was queued for it dropped. A plan that sends its
        /// frames back to back hands back the same way, and waits only for
        /// the frame it always waits for.
        ///
        /// <para>A plan that spaces its frames cannot wait a spacing out
        /// here, on the thread that carries the pulses and at a stop. So
        /// each frame of its hand-back goes out as a request. The answer
        /// shows the frame landed and keeps the next one off its heels, as
        /// it does for the programs that send the release and the mode as
        /// requests (OpenRGB LogitechHIDPP20Controller.cpp:3837-3877). A
        /// frame that goes unanswered may have been lost, close behind a
        /// color frame or on a link that dropped it, so it is written once
        /// more, which changes nothing a second time. Logitech's guideline
        /// for requests over the air is the same, "The request has to be
        /// repeated if the acknowledge (response) is missing" (x8081
        /// document, p.3), and OpenRGB sends a claim frame again when its
        /// answer is missing (LogitechHIDPP20Controller.cpp:4169-4172). A
        /// claim made after the hand-back keeps the plan's distance from
        /// its last frame.</para>
        ///
        /// <para>False when a write failed.</para></summary>
        private bool HandBack(IHidppChannel io, Light light)
        {
            light.Queue = null;
            var steps = light.HandBack;
            if (steps == null) return true;
            int gap = light.Plan.FrameGapMs;
            foreach (var step in steps)
            {
                if (gap == 0 && step.Kind == HidppStepKind.Write)
                {
                    if (!io.Write(step.Frame)) return false;
                    continue;
                }
                var reply = Ask(io, step.Frame, HidppLightPlan.AckTimeoutMs);
                if (reply.Kind == HidppReplyKind.WriteFailed) return false;
                if (gap > 0 && HidppUnitProbe.Missed(reply) && !io.Write(step.Frame)) return false;
            }
            if (gap > 0) light.NextAt = _ticks() + gap;
            return true;
        }

        /// <summary>Puts a claimed unit's hand-back on the books as owed,
        /// when the worker lets go of the unit with no frame: it stopped
        /// answering, or a write to its path failed. The claim is kept as
        /// it stood, so what is paid later is the hand-back of that claim,
        /// with the mode it read. True when the unit held a claim to owe
        /// for.</summary>
        private bool Owe(string path, byte deviceIndex, Light light)
        {
            if (!light.Claimed || light.HandBack == null) return false;
            light.Queue = null;
            if (!_owed.TryGetValue(path, out var units)) _owed[path] = units = new Dictionary<byte, Light>();
            units[deviceIndex] = light;
            return true;
        }

        /// <summary>Drops the paths a write failed on, a color's or a
        /// pulse's alike. Each claimed unit there is owed its hand-back,
        /// the channel closes, what the path answered is forgotten, and
        /// the scan that looks for it again is due at once. The snapshot
        /// and the crash path's record follow.</summary>
        private void DropDead(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states,
            List<string> dead, long now, ref long nextScan)
        {
            foreach (var path in dead) Drop(channels, states, path);
            nextScan = now;
            Publish(channels, states);
            PublishClaimed(channels);
        }

        /// <summary>Lets go of one path a write failed on, a color's, a
        /// pulse's or a scan's question alike: each claimed unit there is
        /// owed its hand-back, the channel closes and what the path
        /// answered is forgotten. True when a unit was owed, a lit device
        /// to find again soon. The crash path's record needs no rewrite
        /// for it: it holds that unit's frames already, as its claim's.</summary>
        private bool Drop(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states, string path)
        {
            bool owed = false;
            if (channels.TryGetValue(path, out var channel))
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL HID++ write failed, dropping " + path);
                foreach (var unit in channel.Units)
                    if (channel.Lights.TryGetValue(unit.DeviceIndex, out var light)) owed |= Owe(path, unit.DeviceIndex, light);
            }
            Close(channels, states, path, forget: true);
            return owed;
        }

        /// <summary>Whether the unit found at a slot is the device a
        /// hand-back is owed to there, as far as its frames go: the same
        /// name, with the effect, per-key and mode features the frames are
        /// addressed to at the same indexes. A receiver's slot can be given
        /// to another device while the first is away.</summary>
        private static bool SameDevice(HidppUnit owed, HidppUnit found)
            => owed != null && found != null && owed.Name == found.Name
               && owed.RgbFeatureId == found.RgbFeatureId && owed.RgbIndex == found.RgbIndex
               && owed.EffectFeatureId == found.EffectFeatureId && owed.EffectIndex == found.EffectIndex
               && owed.PerKeyFeatureId == found.PerKeyFeatureId && owed.PerKeyIndex == found.PerKeyIndex
               && owed.ModeFeatureId == found.ModeFeatureId && owed.ModeIndex == found.ModeIndex;

        private Light Owed(string path, byte deviceIndex)
            => _owed.TryGetValue(path, out var units) && units.TryGetValue(deviceIndex, out var light) ? light : null;

        private void Settle(string path, byte deviceIndex)
        {
            if (!_owed.TryGetValue(path, out var units)) return;
            units.Remove(deviceIndex);
            if (units.Count == 0) _owed.Remove(path);
        }

        /// <summary>
        /// Pays the hand-back a unit is owed, once the unit is found again.
        /// The release goes out as any hand-back's does. A device that
        /// slept since has restarted with no claim on it, and a release
        /// changes nothing there. The mode is not written blind. A device
        /// that restarted is back in the mode the claim found, and the
        /// mode call is the one that costs a keyboard its held modifiers
        /// (<see cref="ClusterPlan"/>, the G515 in Solaar issue 3310). So
        /// the mode is read first, and written back only to a device that
        /// is not in it. A read that goes unanswered leaves the write in,
        /// since a device still in host mode is the worse of the two to
        /// leave. <paramref name="light"/> is the unit's light now, which
        /// keeps the plan's distance from the last frame sent here. False
        /// when a write failed.
        /// </summary>
        private bool PayBack(IHidppChannel io, Light debt, Light light)
        {
            var plan = debt.Plan;
            if (plan.SwitchesMode(debt.Level, debt.SavedMode))
            {
                var mode = Ask(io, plan.ModeRead, HidppHapticProtocol.TimeoutMs(io.Bluetooth));
                if (mode.Kind == HidppReplyKind.WriteFailed) return false;
                if (mode.Kind == HidppReplyKind.Answer && plan.ModeOf(mode) == debt.SavedMode)
                    debt.HandBack = plan.HandBack(1, debt.SavedMode);
            }
            if (!HandBack(io, debt)) return false;
            light.NextAt = Math.Max(light.NextAt, debt.NextAt);
            return true;
        }

        /// <summary>The stop path's share of what is owed: each unit's
        /// frames in their order through a write that opens its own handle,
        /// bounded, and a unit is left at its first failed write, as on the
        /// crash path (<see cref="ReleaseClaimedNow"/>).</summary>
        private void PayOwedNow()
        {
            foreach (var pair in _owed)
            {
                foreach (var debt in pair.Value.Values)
                {
                    foreach (var step in debt.HandBack)
                    {
                        bool written;
                        try { written = _writeOnce(pair.Key, step.Frame, OneShotMs); }
                        catch { written = false; }
                        if (!written) break;
                    }
                }
            }
        }

        /// <summary>Drops a unit's claim from the worker's own books, with
        /// no frame. The plan stays, a fallen-back one included: a device
        /// that refused its per-key frames is not offered them again until
        /// it is found anew.</summary>
        private static void Forget(Light light)
        {
            light.Claimed = false;
            light.Level = 0;
            light.SavedMode = 0;
            light.HandBack = null;
            light.Painted = false;
            light.Shown = null;
            light.ShownMap = null;
            light.Queue = null;
            light.Claiming = false;
            light.FailedPaints = 0;
        }

        /// <summary>Whether a claimed unit's lighting path is gone because a
        /// Logitech program started: no row is linked to the path for
        /// lighting any more, and G HUB or Logitech Gaming Software runs. A
        /// path that is still linked and merely unclaimed, a switch turned
        /// off or a device unassigned, is handed back as ever. The table is
        /// read before the presence: the link pass publishes the presence
        /// first, so a table that shows the path withdrawn is never paired
        /// with a presence older than it. A path the linker closed because
        /// OpenRGB holds the device (#557,
        /// <see cref="PeripheralOutputs.TakenByOpenRgb"/>) is let go the same
        /// way.</summary>
        private static bool LeftToLogitechProgram(OutputPath path)
        {
            var links = PeripheralOutputs.Links;
            if (links.ByPath.TryGetValue(path, out var devices))
            {
                foreach (var device in devices)
                {
                    var row = links.For(device);
                    if (row != null && Array.IndexOf(row.Lighting, path) >= 0) return false;
                }
            }
            return PeripheralOutputs.Presence.LogitechGHubRunning || PeripheralOutputs.TakenByOpenRgb(path);
        }

        /// <summary>Puts one unit and its hand-back frames on the crash
        /// path's record ahead of its claim, in place of any frames the
        /// record held for it. Worker thread only, swapped whole.</summary>
        private void NoteClaimed(string path, byte deviceIndex, byte[][] handBack)
        {
            var current = Volatile.Read(ref _claimed);
            var next = new List<ClaimRecord>(current.Length + 1);
            foreach (var entry in current)
                if (entry.DeviceIndex != deviceIndex || !string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                    next.Add(entry);
            next.Add(new ClaimRecord(path, deviceIndex, handBack));
            Volatile.Write(ref _claimed, next.ToArray());
        }

        /// <summary>Records which units hold a claim and which are owed a
        /// hand-back, each with the frames that hand it back, for the crash
        /// path.</summary>
        private void PublishClaimed(Dictionary<string, Channel> channels)
        {
            var claimed = new List<ClaimRecord>();
            foreach (var channel in channels.Values)
                foreach (var unit in channel.Units)
                    if (channel.Lights.TryGetValue(unit.DeviceIndex, out var light) && light.Claimed && light.HandBack != null)
                        claimed.Add(new ClaimRecord(channel.Io.Path, unit.DeviceIndex, HidppLightPlan.Frames(light.HandBack)));
            foreach (var pair in _owed)
                foreach (var debt in pair.Value)
                    claimed.Add(new ClaimRecord(pair.Key, debt.Key, HidppLightPlan.Frames(debt.Value.HandBack)));
            Volatile.Write(ref _claimed, claimed.ToArray());
        }

        /// <summary>For the crash path and the process exit: hands every
        /// claimed unit back to its own effect. A crash dialog keeps the
        /// process, and this worker, alive, and a worker left running would
        /// claim the device again on its next re-assert, so the worker stops
        /// first and its own finally hands back what it holds, OpenRGB's
        /// teardown order (its sender stops before SetSWControl(0, 0),
        /// LogitechHIDPP20Controller.cpp:3843-3856). A worker that does not
        /// stop within <see cref="PanicJoinMs"/> claims nothing more, and each
        /// unit still recorded goes back through one-shot writes of the
        /// frames on its record, in their order, the release first and at
        /// level 2 the mode it was switched away from last. Each write
        /// opens its own handle with a short timeout, so nothing here waits
        /// on the worker's channel, and a unit whose write fails is left
        /// there: the frames behind it would wait out the same timeout on
        /// the same path. The path is the one the channel's own write sends
        /// on, HidppChannel.Write going through RawHidOutput.Write.</summary>
        public void ReleaseClaimedNow()
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
            try { _thread?.Join(PanicJoinMs); } catch { }
            foreach (var record in Interlocked.Exchange(ref _claimed, Array.Empty<ClaimRecord>()))
            {
                foreach (byte[] frame in record.HandBack)
                {
                    bool written;
                    try { written = _writeOnce(record.Path, frame, OneShotMs); }
                    catch { written = false; }
                    if (!written) break;
                }
            }
        }

        /// <summary>How long the crash path waits for the worker to stop and
        /// hand its devices back on its own channel.</summary>
        internal const int PanicJoinMs = 250;

        /// <summary>Hands every claimed unit on a channel back to its own
        /// effect.</summary>
        private void ReleaseLighting(Channel channel)
        {
            foreach (var unit in channel.Units)
            {
                if (!channel.Lights.TryGetValue(unit.DeviceIndex, out var light) || !light.Claimed) continue;
                if (!HandBack(channel.Io, light)) break;
            }
            channel.Lights.Clear();
        }

        /// <summary>One pass of pulses. <paramref name="linked"/> is set when
        /// any unit is linked for rumble, which makes a scan wait for a quiet
        /// second, and <paramref name="playing"/> when any unit plays a
        /// pulse. True when a row linked to a unit holds a level above zero,
        /// which keeps the worker on its fast tick: a level below the shaper's
        /// first step plays nothing yet, and when it climbs into a pulse it
        /// raises no <see cref="PeripheralOutputs.HapticsChanged"/>, so the
        /// tick has to be fast there too.
        ///
        /// <para><paramref name="besideARequest"/> is the pass a request
        /// lets out while it waits for its answer
        /// (<see cref="HidppWaiter"/>). It plays the same pulses on the same
        /// intervals and closes nothing: a channel whose write fails is
        /// passed over, and the next pass of the worker's own finds it
        /// failed and drops it.</para></summary>
        private bool PlayHaptics(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states,
            LinkTable links, long now, out bool playing, out bool linked, ref long nextScan, bool besideARequest = false)
        {
            playing = false;
            linked = false;
            bool active = false;
            List<string> dead = null;
            foreach (var pair in channels)
            {
                var channel = pair.Value;
                foreach (var unit in channel.Units)
                {
                    if (!unit.HasHaptics) continue;
                    var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
                    if (!links.ByPath.TryGetValue(path, out var devices)) continue;
                    linked = true;
                    float amplitude = 0f;
                    foreach (var device in devices)
                    {
                        var row = links.For(device);
                        if (row == null || Array.IndexOf(row.Haptics, path) < 0) continue;
                        float a = PeripheralOutputs.AmplitudeOf(device);
                        if (a > amplitude) amplitude = a;
                    }
                    if (amplitude > 0f) active = true;
                    channel.Play.TryGetValue(unit.DeviceIndex, out var play);
                    int level = HapticRumbleShaper.Level(amplitude, play.Level);
                    if (level == 0)
                    {
                        channel.Play[unit.DeviceIndex] = (0, 0);
                        continue;
                    }
                    playing = true;
                    if (now < play.NextPlay)
                    {
                        channel.Play[unit.DeviceIndex] = (level, play.NextPlay);
                        continue;
                    }
                    byte? waveform = HapticRumbleShaper.Waveform(level, unit.WaveformMask);
                    if (waveform == null) continue;
                    if (!channel.Io.Write(HidppHapticProtocol.Play(unit.DeviceIndex, unit.HapticIndex, waveform.Value)))
                    {
                        if (!besideARequest) (dead ??= new List<string>()).Add(pair.Key);
                        break;
                    }
                    channel.Play[unit.DeviceIndex] = (level, now + HapticRumbleShaper.IntervalMs(amplitude));
                }
            }
            if (dead != null) DropDead(channels, states, dead, now, ref nextScan);
            return active;
        }

        /// <summary>One walk of the present HID++ collections: a path that
        /// left takes its claims along, and each path with a question to
        /// ask is asked it. True when the walk dropped a path with a lit
        /// unit on it, which is then asked again on the fast interval.</summary>
        private bool Scan(WorkerRun run, Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states, long now,
            HidppWaiter waiter)
        {
            Interlocked.Increment(ref _scans);
            var all = _enumerate();
            if (all == null) return false;
            var collections = all.Where(IsHidppLong).ToList();

            var present = new HashSet<string>(collections.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
            // A collection that left takes its claims and what was owed on
            // it along: nothing reaches a device that is not there, so the
            // crash path's record lets go of both.
            bool left = false;
            foreach (var path in _owed.Keys.Where(p => !present.Contains(p)).ToList())
            {
                _owed.Remove(path);
                left = true;
            }
            foreach (var path in states.Keys.Where(p => !present.Contains(p)).ToList())
            {
                Close(channels, states, path, forget: true);
                _containers.Remove(path);
                left = true;
            }
            if (left) PublishClaimed(channels);

            bool dropped = false;
            foreach (var collection in collections)
            {
                if (run.Stop) return dropped;
                if (!states.TryGetValue(collection.Path, out var state))
                {
                    states[collection.Path] = state = new HidppPathState
                    {
                        ReceiverKind = collection.UsagePage == HidppHapticProtocol.UsagePage
                            ? HidppReceiverProtocol.KindOf(collection.ProductId)
                            : HidppReceiverKind.None,
                    };
                }
                if (!_containers.TryGetValue(collection.Path, out var container))
                {
                    try { container = _containerOf(collection.Path); }
                    catch { container = Guid.Empty; }
                    // A failed lookup is not kept: the next scan asks again,
                    // and the units already found on the path take the
                    // container once it answers. Kept, it would leave every
                    // unit there unmatched for as long as the path stays.
                    if (container != Guid.Empty)
                    {
                        _containers[collection.Path] = container;
                        if (channels.TryGetValue(collection.Path, out var known) && known.Container == Guid.Empty)
                        {
                            known.Container = container;
                            for (int i = 0; i < known.Units.Count; i++)
                                known.Units[i] = known.Units[i] with { ContainerId = container };
                        }
                    }
                }
                // A known receiver's pairing table is read at every scan, so a
                // device paired since the last one is found.
                bool pairing = state.ReceiverKind != HidppReceiverKind.None
                               && state.PairingMisses < HidppUnitProbe.PairingGiveUp;
                if (!pairing && !HasQuestions(state, now, HidppHapticProtocol.IsBluetoothPath(collection.Path))) continue;

                bool open = channels.TryGetValue(collection.Path, out var channel);
                var io = open ? channel.Io
                    : _open(collection, state.ReceiverKind != HidppReceiverKind.None
                        ? HidppReceiverProtocol.ShortSibling(collection, all)
                        : null, HidppReceiverProtocol.VeryLongSibling(collection, all));
                if (io == null)
                {
                    HidppUnitProbe.MissOpenSlots(state, now, HidppHapticProtocol.IsBluetoothPath(collection.Path));
                    continue;
                }
                // Every request on the channel lets the pulses out while it
                // waits, from its first one.
                io.Waiter = waiter;
                var found = HidppUnitProbe.Probe(io, state, container, now);
                // The charge for the Battery lighting mode, read once here and
                // again with each liveness check.
                for (int i = 0; i < found.Count; i++)
                {
                    if (found[i].BatteryFeatureId == 0) continue;
                    found[i] = found[i] with { BatteryPercent = HidppUnitProbe.ReadBattery(io, found[i]) };
                }
                if (found.Count > 0)
                {
                    if (!open)
                    {
                        channel = new Channel { Io = io, State = state, Container = container };
                        channels[collection.Path] = channel;
                    }
                    channel.Units.AddRange(found);
                    foreach (var unit in found)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL HID++ found '{unit.Name}' index=0x{unit.DeviceIndex:X2} type={unit.DeviceType}"
                            + $" haptic=0x{unit.HapticIndex:X2} mask=0x{unit.WaveformMask:X8}"
                            + $" rgb={unit.RgbFeatureId:X4} zones={unit.Zones.Length} battery={unit.BatteryFeatureId:X4}"
                            + $" charge={unit.BatteryPercent}"
                            + $" effect={unit.EffectFeatureId:X4} perkey={unit.PerKeyFeatureId:X4}"
                            + $" keys=0x{unit.PerKeyMin:X2}-0x{unit.PerKeyMax:X2} streaming={unit.HostStreamingIndex}"
                            + $" mode={unit.ModeFeatureId:X4} levels={unit.MaxClaimLevel}");
                }
                else if (!open)
                {
                    io.Dispose();
                }

                // A write failed while the path was asked. It is dropped as
                // after a color's or a pulse's failed write, with each lit
                // unit on it owed its hand-back (DropDead).
                if (state.Dead) dropped |= Drop(channels, states, collection.Path);
            }
            // A slot that settled changes what the linker may conclude even
            // with nothing found, so every scan publishes.
            Publish(channels, states);
            return dropped;
        }

        /// <summary>Whether a scan has anything to ask this path: a slot not
        /// yet answered whose backoff is over.</summary>
        private static bool HasQuestions(HidppPathState state, long now, bool bluetooth)
        {
            int first = state.Receiver ? HidppHapticProtocol.FirstReceiverIndex : 0;
            int last = state.Direct || bluetooth ? 0 : HidppHapticProtocol.LastReceiverIndex;
            for (int slot = first; slot <= last; slot++)
                if (state.Open(slot) && now >= state.RetryAt[slot]) return true;
            return false;
        }

        /// <summary>Whether any present path has a slot still waiting for
        /// its device.</summary>
        private static bool AnyWaiting(Dictionary<string, HidppPathState> states)
        {
            foreach (var pair in states)
                if (!pair.Value.Dead && HidppUnitProbe.Pending(pair.Value, HidppHapticProtocol.IsBluetoothPath(pair.Key)))
                    return true;
            return false;
        }

        /// <summary>Asks each found unit something cheap: a haptic unit its
        /// configuration, any other its name feature. One that does not
        /// answer went to sleep or away. It leaves the list and its slot
        /// reopens, so the next scan finds it again once it answers. A unit
        /// that answers and reports a charge has it read again. True when a
        /// slot reopened.
        ///
        /// <para>A lit unit that leaves this way gets no frame: a device
        /// that slept has lost the claim by itself, and frames to it would
        /// be waits. Its hand-back is owed all the same
        /// (<see cref="Owe"/>), because a device that missed one answer
        /// still holds the claim, and a keyboard switched to host mode
        /// still has its keys reporting nothing its profile would have
        /// them report. It is paid when the unit is found again, and stays
        /// on the crash path's record until then.</para></summary>
        private bool Check(WorkerRun run, Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states)
        {
            bool changed = false;
            bool reopened = false;
            bool claims = false;
            foreach (var path in channels.Keys.ToList())
            {
                if (run.Stop) return reopened;
                var channel = channels[path];
                for (int i = channel.Units.Count - 1; i >= 0; i--)
                {
                    var unit = channel.Units[i];
                    bool? alive;
                    if (unit.HapticIndex != 0)
                    {
                        bool? enabled = HidppHapticProbe.ReadFeedbackEnabled(channel.Io, unit.DeviceIndex, unit.HapticIndex);
                        alive = enabled != null;
                        if (enabled != null && enabled.Value != unit.FeedbackEnabled)
                        {
                            channel.Units[i] = unit with { FeedbackEnabled = enabled.Value };
                            changed = true;
                        }
                    }
                    else
                    {
                        var reply = channel.Io.Request(unit.DeviceIndex, 0x00, 0,
                            new byte[] { 0x00, 0x05 }, HidppHapticProtocol.TimeoutMs(channel.Io.Bluetooth));
                        // A receiver's HID++ 1.0 error means the device is
                        // out of reach, as in ReadFeedbackEnabled.
                        alive = reply.Kind == HidppReplyKind.Answer
                                || (reply.Kind == HidppReplyKind.Error && !reply.Hidpp10);
                    }
                    if (alive == true)
                    {
                        if (unit.BatteryFeatureId == 0) continue;
                        int percent = HidppUnitProbe.ReadBattery(channel.Io, channel.Units[i]);
                        if (percent < 0 || percent == channel.Units[i].BatteryPercent) continue;
                        channel.Units[i] = channel.Units[i] with { BatteryPercent = percent };
                        changed = true;
                        continue;
                    }
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HID++ '{unit.Name}' stopped answering");
                    if (channel.Lights.TryGetValue(unit.DeviceIndex, out var light) && light.Claimed)
                    {
                        // Asleep, or awake and one answer short. Nothing
                        // says which, so nothing is sent and the hand-back
                        // is owed.
                        Owe(path, unit.DeviceIndex, light);
                        claims = true;
                    }
                    channel.Units.RemoveAt(i);
                    channel.Play.Remove(unit.DeviceIndex);
                    channel.Lights.Remove(unit.DeviceIndex);
                    channel.State.Reopen(unit.DeviceIndex);
                    changed = true;
                    reopened = true;
                }
                if (channel.Units.Count == 0)
                {
                    Close(channels, states, path, forget: false);
                    changed = true;
                }
            }
            if (claims) PublishClaimed(channels);
            if (changed) Publish(channels, states);
            return reopened;
        }

        /// <summary>Closes a path's channel. Forgetting also drops what the
        /// path answered, for one that vanished or failed.</summary>
        private static void Close(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states,
            string path, bool forget)
        {
            if (channels.Remove(path, out var channel))
            {
                try { channel.Io.Dispose(); } catch { }
            }
            if (forget) states.Remove(path);
        }

        /// <summary>
        /// Publishes the light map of every unit painted light by light
        /// (#557), the map its plan paints by (<see cref="HidppLightPlan.Lights"/>),
        /// and takes down the maps of units that left or fell back to one
        /// color. A unit found again with the same lights keeps its map, so
        /// the Lighting tab does not redraw on every scan. The names Map Key
        /// Lights learned go over each map, again whenever they change.
        /// </summary>
        private void SyncMaps(Dictionary<string, Channel> channels)
        {
            _mapsDirty = false;
            int version = LearnedKeyMaps.Version;
            bool relearn = version != _mapsVersion;
            _mapsVersion = version;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in channels.Values)
                foreach (var unit in channel.Units)
                {
                    var plan = channel.Lights.TryGetValue(unit.DeviceIndex, out var light) ? light.Plan : HidppLightPlan.For(unit);
                    var fresh = plan?.Lights;
                    if (fresh == null) continue;
                    seen.Add(unit.Key);
                    var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
                    if (_maps.TryGetValue(unit.Key, out var known) && SameLights(known.Base, fresh))
                    {
                        if (!relearn) continue;
                        var again = LearnedKeyMaps.Apply(known.Base);
                        if (SameLights(again, known.Map)) continue;
                        _maps[unit.Key] = (known.Base, again);
                        PeripheralLightMaps.Publish(path, again);
                        continue;
                    }
                    var map = LearnedKeyMaps.Apply(fresh);
                    _maps[unit.Key] = (fresh, map);
                    PeripheralLightMaps.Publish(path, map);
                }
            List<string> gone = null;
            foreach (string key in _maps.Keys)
                if (!seen.Contains(key)) (gone ??= new List<string>()).Add(key);
            if (gone == null) return;
            foreach (string key in gone)
            {
                _maps.Remove(key);
                PeripheralLightMaps.Remove(new OutputPath(OutputFamily.HidppUnit, key));
            }
        }

        /// <summary>Whether two maps hold the same lights for the same
        /// model.</summary>
        private static bool SameLights(LightMap a, LightMap b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Product != b.Product || a.Lights.Count != b.Lights.Count) return false;
            for (int i = 0; i < a.Lights.Count; i++)
                if (a.Lights[i] != b.Lights[i]) return false;
            return true;
        }

        private void Publish(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states)
        {
            SyncMaps(channels);
            var units = new List<HidppUnit>();
            foreach (var channel in channels.Values) units.AddRange(channel.Units);
            var pending = new HashSet<Guid>();
            foreach (var pair in states)
            {
                if (pair.Value.Dead) continue;
                if (!HidppUnitProbe.HasOpenSlot(pair.Value, HidppHapticProtocol.IsBluetoothPath(pair.Key))) continue;
                if (_containers.TryGetValue(pair.Key, out var container) && container != Guid.Empty)
                    pending.Add(container);
            }
            Volatile.Write(ref _snapshot, new HidppSnapshot(units, pending));
            try { SnapshotChanged?.Invoke(); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }
}
