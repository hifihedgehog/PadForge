using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The SteelSeries worker (#494). It lights each SteelSeries mouse and
    /// keyboard through the device's own control collection, so a device
    /// assigned to a virtual controller shows that controller's color with
    /// no SteelSeries GG running. One thread owns every collection of the
    /// family, the shape of <see cref="HidppBackend"/>, and the frames come
    /// from <see cref="SteelSeriesDirectProtocol"/>.
    ///
    /// <para>Scan. From the list the host hands over
    /// (<see cref="Rescan"/>), by product ID and descriptor
    /// (<see cref="SteelSeriesDirectProtocol.TryPlan"/>), with nothing asked
    /// of any device. A unit stays listed for as long as its collection is
    /// in the last list.</para>
    ///
    /// <para>Open. Only a unit a row links and a claim resolves on, for
    /// read and write. A collection that opens with no access right only, where
    /// Windows refused read and write (the two errors
    /// <see cref="HidReportChannel"/> names), is one Windows keeps for
    /// itself or another program holds. Such a unit is not written to.</para>
    ///
    /// <para>Paint. No color is queued. Each pass reads the color that
    /// rules the unit now and sends it when the family's next allowed time
    /// has come, one report per pass, so a slow device shows the newest
    /// color and skips the ones between. A color in flight is finished
    /// before the next one starts, and no report goes out sooner than the
    /// family's gap after the one before. Every paint sets every LED, so a
    /// paint is its own re-assert: an unchanged color goes out again once
    /// the family's interval has passed since the last paint. The claim
    /// frames go out in front of the first color of an open, again in front
    /// of the first color that starts once the interval has passed since
    /// they last went, and once more when Windows resumes from sleep: a
    /// device that lost power while its collection stayed listed shows its
    /// stored lighting again and says nothing.</para>
    ///
    /// <para>A 2.4 GHz dongle answers each report on its input report, and
    /// the worker reads that answer before the next report goes (OpenRGB
    /// SteelSeriesAeroxWirelessController.cpp:254-268). The dongle stays on
    /// the bus while its mouse is switched off or asleep and then answers
    /// anything with 40 FF: "the second byte returned is 255 (0xff) if the
    /// mouse is off or sleeping", behind a first byte of 0x40 (rivalcfg
    /// issue 187, comment 1546731122, of a Prime Wireless, and
    /// Aerox_5-LennardKittner src/lib.rs:14 and 105-107 for the Aerox 5
    /// Wireless). So the write succeeds whatever the mouse does, and only
    /// the answer tells. On 40 FF, or with no answer in
    /// <see cref="EchoTimeoutMs"/>, the color in flight is dropped and the
    /// unit is left alone for <see cref="QuietMs"/>, then the whole color
    /// is sent again from its first report. The handle stays open and the
    /// unit stays listed awake through this: a unit listed asleep has no
    /// path and so no color, and the color's first report is the only
    /// question in the lighting protocol that a mouse which woke up
    /// answers.</para>
    ///
    /// <para>Hand back. When no claim resolves on the unit or its path
    /// leaves the link table, the family's frame that returns the device to
    /// its own lighting goes out and the handle closes. A family no
    /// reference has such a frame for keeps the last color until the device
    /// loses power, and gets no parting color: black would leave a dark
    /// device, and any other color is a guess at what its owner stored.
    /// Nothing is sent when SteelSeries GG has taken the device
    /// (<see cref="LeftToVendorProgram"/>), and nothing to a dongle whose
    /// mouse is off, which wakes into its own lighting.</para>
    ///
    /// <para>Fail. A write that fails, a hand-back included, closes the
    /// unit's channel and lists the unit asleep, asks the host for a walk,
    /// and tries the unit again every <see cref="RetryMs"/> by opening it,
    /// which sends nothing. A unit that opens is listed awake, and a claim
    /// that still stands paints it as it would a new unit.</para>
    ///
    /// <para>A unit that fails while it may show a color of ours is owed
    /// its hand-back (<see cref="Unit.Owed"/>). The frames keep their
    /// place on the crash path's record, and the try that finds the unit
    /// open again sends them before the unit is listed awake. A stop sends
    /// them through a one-shot write. They are dropped unsent when
    /// SteelSeries GG runs and when the collection leaves the walk.</para>
    /// </summary>
    internal sealed class SteelSeriesDirectBackend : IDirectUnitBackend
    {
        /// <summary>How often the worker looks for a dongle's answer or a
        /// keyboard's version, and its longest wait with nothing due. The
        /// HID++ worker's two ticks.</summary>
        internal const int ActiveTickMs = HidppBackend.ActiveTickMs;
        internal const int IdleTickMs = HidppBackend.IdleTickMs;

        /// <summary>How long a new worker waits for the one a timed-out
        /// Stop left inside a write, before it opens the collections
        /// anyway.</summary>
        internal const int PredecessorJoinMs = HidppBackend.PredecessorJoinMs;

        /// <summary>How long Stop waits for the worker, and how long the
        /// crash path does.</summary>
        internal const int StopJoinMs = 3000;
        internal const int PanicJoinMs = HidppBackend.PanicJoinMs;

        /// <summary>How long an output report may take. The slowest
        /// transfer in the vendor's captured traffic took 26 ms, a per-key
        /// frame to an Apex 9 TKL (the captures of OpenRGB issue 3253), and
        /// this is the bound the HID++ crash path's one-shot write has.</summary>
        internal const int WriteTimeoutMs = 200;

        /// <summary>The bound of each one-shot write on the crash path.</summary>
        internal const int OneShotMs = 200;

        /// <summary>How long a dongle's answer is waited for, OpenRGB's
        /// limit (SteelSeriesAeroxWirelessController.h:33). In the vendor's
        /// own traffic through an Aerox 5 Wireless dongle the answer came 8
        /// to 22 ms after the report (the captures of OpenRGB issue 4048).</summary>
        internal const int EchoTimeoutMs = 250;

        /// <summary>How long a dongle whose mouse does not listen is left
        /// alone, the pause SignalRGB makes on a 40
        /// (signal-plugins Steelseries_Aerox_Wireless_Mouse.js:204-210).</summary>
        internal const int QuietMs = 1000;

        /// <summary>How long a gated keyboard's version is waited for.
        /// OpenRGB gives that reply 2 ms and the same boards' brightness
        /// reply 100 ms (SteelSeriesApexBaseController.cpp:104-112,
        /// SteelSeriesApexController.cpp:283-296 with
        /// SteelSeriesApexBaseController.h:22). The longer of the two is
        /// used, since a reply read as missing sends a board of the third
        /// generation the older frames.</summary>
        internal const int QueryTimeoutMs = 100;

        /// <summary>How long an asleep unit waits before it is tried again,
        /// the interval the HID++ worker asks a silent slot on.</summary>
        internal const int RetryMs = HidppBackend.FirstScanMs;

        /// <summary>How long a unit whose container Windows could not name
        /// yet waits before the next lookup.</summary>
        internal const int ContainerRetryMs = 1000;

        /// <summary>The most input reports read in one go. Windows keeps a
        /// ring of input reports per open handle, 32 in Wine's model of the
        /// class driver (wine dlls/hidclass.sys/device.c:81-94), so this
        /// empties it, and a device that never stops sending cannot hold
        /// the worker.</summary>
        internal const int ReadLimit = 32;

        /// <summary>The two bytes a dongle's answer starts with while its
        /// mouse is off or asleep. The battery tool for the Aerox 5 Wireless
        /// tests both (Aerox_5-LennardKittner src/lib.rs:14 and 105-107), an
        /// owner of a Prime Wireless names FF as the byte that tells
        /// (rivalcfg issue 187, comment 1546731122), and the 2031 input
        /// reports that start 40 in a capture of the vendor's traffic all
        /// continue FF (Cricetus-Imperator docs/packets/full.pcap). SignalRGB
        /// tests 40 alone (signal-plugins
        /// Steelseries_Aerox_Wireless_Mouse.js:208). A report that starts 40
        /// and continues otherwise is in no source, and is read as somebody
        /// else's: the unit then keeps its hand-back.</summary>
        internal const byte MouseOffReply = 0x40;
        internal const byte MouseOffFlag = 0xFF;

        private const int NoColor = -1;

        /// <summary>One present control collection and what the worker
        /// holds of it. Worker thread only.</summary>
        private sealed class Unit
        {
            /// <summary>The interface path, kept exactly as the first walk
            /// gave it: it is the unit's key, compared ordinally.</summary>
            public string Path;
            public ushort ProductId;
            public SteelSeriesPlan Plan;
            public Guid Container;
            public long ContainerRetryAt;
            public bool Seen;

            public bool Asleep;
            public long RetryAt;

            /// <summary>The hand-back the unit is owed, or null. Set when
            /// a unit fails while it may show a color of ours
            /// (<see cref="Fail"/>), which is the only way a unit comes to
            /// owe one, so a unit that owes is asleep and holds no handle.
            /// Its entry stays on the crash path's record meanwhile.</summary>
            public SteelSeriesFrame[] Owed;

            /// <summary>The open channel, or null. Everything below is of
            /// one open and is reset by the next.</summary>
            public IHidReportChannel Io;

            /// <summary>The version reply is still awaited, until
            /// <see cref="QueryUntil"/>.</summary>
            public bool Querying;
            public long QueryUntil;
            public bool Gen3;

            /// <summary>The hand-back is in the crash record, as this
            /// entry's frames.</summary>
            public bool Recorded;
            public byte[][] RecordedFrames;

            /// <summary>A claim or color report has gone out, or was tried
            /// and failed, so the device may show a color of ours.</summary>
            public bool Lit;
            public bool ClaimSent;

            /// <summary>When the claim last went out, for its re-assert.</summary>
            public long ClaimAt;

            /// <summary>Windows resumed from sleep: the claim and the color
            /// go out once more, whatever was sent before.</summary>
            public bool Reassert;
            public bool Announced;

            /// <summary>The reports of the paint in flight, the claim
            /// frames first, or null between paints.</summary>
            public SteelSeriesFrame[] Queue;
            public int Next;
            public int ClaimFrames;
            public int QueueRgb;

            /// <summary>The soonest the next report may go.</summary>
            public long NextAt;

            /// <summary>The last color sent whole, or <see cref="NoColor"/>,
            /// and when its first report went out.</summary>
            public int SentRgb = NoColor;
            public long PaintAt;

            /// <summary>The picture of the paint in flight and of the last
            /// one sent whole (#557): a color per light of the map, and the
            /// color of a key the lights leave out. Null colors for one
            /// color.</summary>
            public int[] QueueColors;
            public LightMap QueueMap;
            public int QueueFill;
            public int[] SentColors;
            public LightMap SentMap;
            public int SentFill;

            /// <summary>The picture a family with a hold is waiting on, and
            /// when it was first seen (<see cref="SteelSeriesFamilyInfo.HoldMs"/>).</summary>
            public bool Holding;
            public int HeldRgb;
            public int[] HeldColors;
            public LightMap HeldMap;
            public int HeldFill;
            public long HeldSince;

            /// <summary>A per-key board's SKU, which picks the keys of its
            /// map (<see cref="SteelSeriesApexLayouts"/>), or null. Asked once
            /// while the device stays: <see cref="SkuAsked"/> after the
            /// answer or the limit, and <see cref="AskingSku"/> until then,
            /// up to <see cref="SkuUntil"/>.</summary>
            public string Sku;
            public bool SkuAsked;
            public bool AskingSku;
            public byte SkuCommand;
            public long SkuUntil;

            /// <summary>A dongle's answer to the report before is still
            /// awaited, until <see cref="AwaitUntil"/>.</summary>
            public bool Awaiting;
            public byte AwaitCommand;
            public long AwaitUntil;

            /// <summary>A dongle last answered that its mouse is off.</summary>
            public bool MouseOff;
            public long QuietUntil;

            /// <summary>Where each input report read from the unit lands.
            /// The unit's own, since a worker a timed-out Stop left behind
            /// may still be reading while the next worker runs.</summary>
            public readonly byte[] Input = new byte[65];
        }

        private enum Echo
        {
            Waiting,
            Answered,
            MouseOff,
            TimedOut,
            Failed,
        }

        /// <summary>The units that may show a color of ours and have a
        /// frame that undoes it, for a hand-back from the crash path, which
        /// cannot wait on this worker. Swapped whole.</summary>
        private (string Path, byte[][] Frames)[] _recorded = Array.Empty<(string, byte[][])>();

        private readonly Func<string, IHidReportChannel> _open;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<long> _ticks;
        private readonly Func<string, byte[], int, bool> _writeOnce;
        private readonly int _activeTickMs;
        private readonly int _idleTickMs;
        private readonly int _predecessorJoinMs;
        private readonly int _stopJoinMs;
        private readonly AutoResetEvent _wake = new(false);

        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;
        private IReadOnlyList<HidCollectionInfo> _present;

        /// <summary>Each unit's light map (#557): the one its family or its
        /// SKU gives, and that one with the names Map Key Lights learned.
        /// Worker thread only, by unit path.</summary>
        private readonly Dictionary<string, (LightMap Base, LightMap Map)> _maps = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The <see cref="LearnedKeyMaps.Version"/> the maps were
        /// made at. Worker thread only.</summary>
        private int _mapsVersion = -1;
        private Thread _thread;
        // The newest worker's thread, which Stop does not forget.
        private Thread _worker;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;
        private int _waitMs;

        // Only the worker the app makes listens for Windows resuming. A test
        // says so through NoteResume.
        private readonly bool _powerEvents;
        private int _resumed;

        /// <summary>The family's workers over the engine's restarts.</summary>
        private static readonly WorkerSuccession s_workers = new();

        public SteelSeriesDirectBackend()
            : this(null, null, null, null, ActiveTickMs, IdleTickMs, powerEvents: true) { }

        /// <summary>Test seam: the channel opener, the container lookup,
        /// the clock, the one-shot writer the crash path uses, the two
        /// ticks, the wait for a predecessor worker and Stop's wait, so a
        /// bench drives the worker against scripted channels on a clock of
        /// its own.
        ///
        /// <para>The opener and the one-shot writer are the worker's only
        /// two ways to a device, and both defaults are the shared helpers
        /// of <see cref="HidReportChannel"/>, which open nothing in the test
        /// host (<see cref="RealHidAccess"/>). The container lookup reads a
        /// property Windows already holds and asks the device nothing.</para></summary>
        internal SteelSeriesDirectBackend(Func<string, IHidReportChannel> open, Func<string, Guid> containerOf,
            Func<long> ticks, Func<string, byte[], int, bool> writeOnce, int activeTickMs, int idleTickMs,
            int predecessorJoinMs = PredecessorJoinMs, int stopJoinMs = StopJoinMs, bool powerEvents = false)
        {
            _powerEvents = powerEvents;
            _open = open ?? HidReportChannel.Open;
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _ticks = ticks ?? (() => Environment.TickCount64);
            _writeOnce = writeOnce ?? HidReportChannel.WriteOutputOnce;
            _activeTickMs = activeTickMs;
            _idleTickMs = idleTickMs;
            _predecessorJoinMs = predecessorJoinMs;
            _stopJoinMs = stopJoinMs;
        }

        public OutputFamily Family => OutputFamily.SteelSeriesUnit;

        public DirectBackendSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public event Action SnapshotChanged;
        public event Action RescanRequested;

        /// <summary>Passes the worker finished, for a test that waits on
        /// passes and times nothing.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        /// <summary>The wait the worker asked for at the end of its last
        /// pass, so a test reads the tick it chose.</summary>
        internal int WaitMs => Volatile.Read(ref _waitMs);

        /// <summary>Whether the newest worker this instance started still
        /// runs. Stop forgets its thread whether or not its wait ended, so
        /// this reads the thread itself.</summary>
        internal bool WorkerAlive => Volatile.Read(ref _worker)?.IsAlive == true;

        /// <summary>The crash path's record, for a test.</summary>
        internal (string Path, byte[][] Frames)[] Recorded => Volatile.Read(ref _recorded);

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _thread != null) return;
            var run = new WorkerRun();
            _run = run;
            // A list from before this start is not this run's scan, and
            // neither is what an earlier run found: the host walks on its
            // first pass and hands a new one over, and this run has scanned
            // nothing until then.
            Volatile.Write(ref _present, null);
            Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralSteelSeries" };
            Volatile.Write(ref _worker, _thread);
            _thread.Start();
            PeripheralOutputs.LightingChanged += Wake;
            PeripheralOutputs.LinksChanged += Wake;
            LearnedKeyMaps.Changed += OnLearned;
            if (_powerEvents) SystemEvents.PowerModeChanged += OnPowerMode;
        }

        public void Stop()
        {
            PeripheralOutputs.LightingChanged -= Wake;
            PeripheralOutputs.LinksChanged -= Wake;
            LearnedKeyMaps.Changed -= OnLearned;
            if (_powerEvents) SystemEvents.PowerModeChanged -= OnPowerMode;
            if (_thread == null) return;
            _run.Stop = true;
            Wake();
            try { _thread.Join(_stopJoinMs); } catch { }
            _thread = null;
            _run = null;
        }

        public void Rescan(IReadOnlyList<HidCollectionInfo> present)
        {
            if (present == null) return;
            Volatile.Write(ref _present, present);
            Wake();
        }

        public void SignalStop()
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
        }

        /// <summary>For the crash path and the process exit. The worker
        /// stops first and its own finally hands back what it holds, the
        /// order <see cref="HidppBackend.ReleaseClaimedNow"/> keeps. A
        /// worker that does not stop in time claims nothing more, and each
        /// unit still on record gets its hand-back through a one-shot write
        /// that opens a handle of its own. Every hand-back of the family is
        /// an output report, so one writer serves them all. A unit whose path
        /// the link pass closed because GG runs or OpenRGB holds it gets
        /// nothing (<see cref="LeftToVendorProgram"/>), as in the shared
        /// worker's crash path (HidLightingBackend.ReleaseClaimedNow).</summary>
        public void ReleaseClaimedNow(long deadline)
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
            long left = deadline - _ticks();
            if (left > 0)
            {
                try { _thread?.Join((int)Math.Min(left, PanicJoinMs)); } catch { }
            }
            foreach (var (path, frames) in Interlocked.Exchange(ref _recorded, Array.Empty<(string, byte[][])>()))
            {
                if (LeftToVendorProgram(new OutputPath(OutputFamily.SteelSeriesUnit, path))) continue;
                foreach (byte[] frame in frames)
                {
                    left = deadline - _ticks();
                    if (left <= 0) return;
                    try { _writeOnce(path, frame, (int)Math.Min(left, OneShotMs)); } catch { }
                }
            }
        }

        private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) NoteResume();
        }

        private void OnLearned(object sender, EventArgs e) => Wake();

        /// <summary>Windows resumed from sleep: every open unit gets its
        /// claim and its color once more, since a device that lost power in
        /// between shows its stored lighting again while its collection
        /// never left the list.</summary>
        internal void NoteResume()
        {
            Interlocked.Exchange(ref _resumed, 1);
            Wake();
        }

        /// <summary>Brings the worker's next pass forward. Any thread.</summary>
        internal void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        private void Worker(WorkerRun run)
        {
            // A worker a timed-out Stop left inside a write still holds its
            // collections, and its finally hands its units back, so a new
            // worker waits for it before it opens anything
            // (HidppBackend.Worker). One that never comes back holds the
            // new worker only for that bound.
            s_workers.Enter(run, _predecessorJoinMs, "SteelSeries");
            var units = new List<Unit>();
            try
            {
                IReadOnlyList<HidCollectionInfo> scanned = null;
                while (!run.Stop)
                {
                    long now = _ticks();
                    bool changed = false, failed = false;
                    long due = long.MaxValue;

                    var present = Volatile.Read(ref _present);
                    if (present != null && !ReferenceEquals(present, scanned))
                    {
                        // The first scan publishes whatever it found, so
                        // the host stops waiting for this family.
                        changed = Scan(units, present, now) || scanned == null;
                        scanned = present;
                    }
                    // A unit's map is up before it is painted by, and again
                    // when Map Key Lights learned names. A board's SKU
                    // updates its own map as it comes (Learned).
                    if (changed || LearnedKeyMaps.Version != _mapsVersion) SyncMaps(units);

                    var links = PeripheralOutputs.Links;
                    bool resumed = Interlocked.Exchange(ref _resumed, 0) != 0;
                    foreach (var unit in units)
                    {
                        if (run.Stop) break;
                        if (resumed && unit.Io != null) unit.Reassert = true;
                        // Each unit is served on a clock reading of its
                        // own. A per-key frame takes a keyboard up to 26 ms,
                        // and a reading from before it would let the next
                        // unit's gap start that much early.
                        Serve(run, unit, links, _ticks(), ref changed, ref failed, ref due);
                    }

                    if (changed) Publish(units);
                    // The device may have left with no row to say so. One
                    // ask covers every unit that failed in this pass.
                    if (failed)
                    {
                        try { RescanRequested?.Invoke(); } catch { }
                    }

                    int wait = due == long.MaxValue ? _idleTickMs : (int)Math.Clamp(due - _ticks(), 1, _idleTickMs);
                    Volatile.Write(ref _waitMs, wait);
                    Interlocked.Increment(ref _passes);
                    // A Dispose whose Stop gave up its join releases the
                    // wake under a worker still inside a write, which ends
                    // here.
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL SteelSeries worker fault: " + ex.GetType().Name);
            }
            finally
            {
                // Each device goes back to its own lighting before its
                // handle closes, so stopping the engine leaves no keyboard
                // held in host lighting. Not one a later run has claimed,
                // since it may have the device open, and a hand-back would
                // land on its picture.
                foreach (var unit in units)
                {
                    if (unit.Io == null) continue;
                    long due = long.MaxValue;
                    if (s_workers.MayReach(unit.Path, run))
                    {
                        try { Release(unit, new OutputPath(OutputFamily.SteelSeriesUnit, unit.Path), _ticks(), blocking: true, ref due, out _); }
                        catch { }
                    }
                    Close(unit);
                }
                // A unit that is owed its hand-back has no handle of the
                // worker's, and gets it now through a one-shot write, as on
                // the crash path. Not where SteelSeries GG has the device,
                // and not where a later run has claimed it.
                foreach (var unit in units)
                {
                    if (unit.Owed == null) continue;
                    if (s_workers.MayReach(unit.Path, run) && !LeftToVendorProgram(new OutputPath(OutputFamily.SteelSeriesUnit, unit.Path)))
                    {
                        foreach (var frame in unit.Owed)
                        {
                            try { _writeOnce(unit.Path, frame.Buffer, OneShotMs); } catch { }
                        }
                    }
                    ClearOwed(unit);
                }
                // A worker that outlived a timed-out Stop can end after the
                // next Start's worker has scanned and painted. The record
                // and the list are that worker's by then, and stay.
                if (s_workers.Holds(run))
                {
                    Volatile.Write(ref _recorded, Array.Empty<(string, byte[][])>());
                    Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
                    foreach (string key in _maps.Keys)
                        PeripheralLightMaps.Remove(new OutputPath(OutputFamily.SteelSeriesUnit, key));
                    _maps.Clear();
                    _mapsVersion = -1;
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Scan
        // ─────────────────────────────────────────────

        /// <summary>Brings the unit list in line with a walk. A collection
        /// that passes the gate and is not listed yet becomes a unit, and a
        /// unit whose collection left the walk leaves the list, the one way
        /// a unit ever does. True when the list changed.</summary>
        private bool Scan(List<Unit> units, IReadOnlyList<HidCollectionInfo> present, long now)
        {
            bool changed = false;
            foreach (var collection in present)
            {
                if (collection?.Path == null || !SteelSeriesDirectProtocol.TryPlan(collection, out var plan)) continue;
                Unit unit = null;
                foreach (var known in units)
                {
                    if (!string.Equals(known.Path, collection.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    unit = known;
                    break;
                }
                if (unit == null)
                {
                    unit = new Unit { Path = collection.Path, ProductId = collection.ProductId, Plan = plan };
                    LookUpContainer(unit, now);
                    units.Add(unit);
                    changed = true;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL SteelSeries found {unit.ProductId:X4} {plan.Info.Family} interface={plan.Info.Interface}"
                        + $" color={plan.ColorReport}");
                }
                else if (unit.Io == null)
                {
                    unit.Plan = plan;
                }
                unit.Seen = true;
            }
            for (int i = units.Count - 1; i >= 0; i--)
            {
                var unit = units[i];
                if (unit.Seen)
                {
                    unit.Seen = false;
                    continue;
                }
                // The device is gone, so there is nothing to hand back to,
                // and nothing owed to it either.
                ClearOwed(unit);
                Close(unit);
                units.RemoveAt(i);
                changed = true;
            }
            return changed;
        }

        /// <summary>Asks Windows for the unit's container. A lookup that
        /// fails is asked again: until it answers the unit matches no row.</summary>
        private bool LookUpContainer(Unit unit, long now)
        {
            Guid container;
            try { container = _containerOf(unit.Path); }
            catch { container = Guid.Empty; }
            unit.Container = container;
            unit.ContainerRetryAt = now + ContainerRetryMs;
            return container != Guid.Empty;
        }

        private void Publish(List<Unit> units)
        {
            var list = new DirectUnit[units.Count];
            for (int i = 0; i < list.Length; i++)
            {
                var unit = units[i];
                list[i] = new DirectUnit(OutputFamily.SteelSeriesUnit, unit.Path, unit.Container,
                    SteelSeriesDirectProtocol.VendorId, unit.ProductId)
                {
                    Name = SteelSeriesDirectProtocol.NameOf(unit.ProductId),
                    Asleep = unit.Asleep,
                    // A family with a hold is sent no color that keeps changing
                    // (StartAt), and its Lighting tab says so.
                    SteadyColorsOnly = unit.Plan.Info.HoldMs > 0,
                };
            }
            Volatile.Write(ref _snapshot, new DirectBackendSnapshot(list));
            try { SnapshotChanged?.Invoke(); } catch { }
        }

        // ─────────────────────────────────────────────
        //  One unit, one pass
        // ─────────────────────────────────────────────

        private static void Soonest(ref long due, long at)
        {
            if (at < due) due = at;
        }

        private void Serve(WorkerRun run, Unit unit, LinkTable links, long now, ref bool changed, ref bool failed,
            ref long due)
        {
            if (unit.Container == Guid.Empty)
            {
                if (now >= unit.ContainerRetryAt && LookUpContainer(unit, now)) changed = true;
                if (unit.Container == Guid.Empty) Soonest(ref due, unit.ContainerRetryAt);
            }

            var path = new OutputPath(OutputFamily.SteelSeriesUnit, unit.Path);
            if (unit.Asleep)
            {
                // SteelSeries GG has every device of the family once it
                // runs, an asleep one too, so what the unit is owed is
                // dropped unsent.
                if (unit.Owed != null && LeftToVendorProgram(path)) ClearOwed(unit);
                if (now >= unit.RetryAt)
                {
                    if (unit.Owed != null ? PayBack(unit) : Answers(unit))
                    {
                        unit.Asleep = false;
                        changed = true;
                        PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL SteelSeries {unit.ProductId:X4} answers again");
                    }
                    else
                    {
                        unit.RetryAt = now + RetryMs;
                    }
                }
                if (unit.Asleep)
                {
                    Soonest(ref due, unit.RetryAt);
                    return;
                }
            }

            int rgb = 0;
            PeripheralOutputs.Claimant ruler = default;
            bool wanted = links.ByPath.ContainsKey(path) && PeripheralOutputs.TryResolveColor(path, out rgb, out ruler);
            if (!wanted)
            {
                if (unit.Io != null && Release(unit, path, now, blocking: false, ref due, out bool lost))
                {
                    // A hand-back that did not go out is a failed write like
                    // any other: the device may have left with no row to
                    // say so.
                    if (lost) Fail(unit, now, "hand-back failed", ref changed, ref failed, ref due);
                    else Close(unit);
                }
                return;
            }

            // A stopping worker opens nothing, claims nothing and paints
            // nothing more. Its finally hands back what it holds.
            if (run.Stop) return;
            if (unit.Io == null)
            {
                if (!Open(unit, now))
                {
                    Fail(unit, now, "did not open for read and write", ref changed, ref failed, ref due);
                    return;
                }
                s_workers.Claim(unit.Path, run);
                if (!AskVersion(unit, now))
                {
                    Fail(unit, now, "write failed", ref changed, ref failed, ref due);
                    return;
                }
            }
            if (!Paint(run, unit, rgb, ruler.Keys, ruler.KeepsChanging, now, ref due))
                Fail(unit, now, "write failed", ref changed, ref failed, ref due);
        }

        /// <summary>Whether an asleep unit can be opened again. Nothing is
        /// sent: the scan and this are the two things done to a device
        /// nobody lights, and neither writes.</summary>
        private bool Answers(Unit unit)
        {
            IHidReportChannel io = null;
            try { io = _open(unit.Path); } catch { }
            bool answers = io != null && !io.FeatureOnly;
            try { io?.Dispose(); } catch { }
            return answers;
        }

        /// <summary>The try of an asleep unit that is owed its hand-back:
        /// the collection is opened for read and write, as a claim opens
        /// it, and sent the frames. True when all of them went out, which
        /// settles the debt and says the unit answers again. No gap is
        /// waited for in front of them: a try comes
        /// <see cref="RetryMs"/> after the unit's last report at the
        /// soonest. The gap counts from these frames for whatever follows,
        /// as after any hand-back (<see cref="Release"/>).</summary>
        private bool PayBack(Unit unit)
        {
            IHidReportChannel io = null;
            try { io = _open(unit.Path); } catch { }
            bool sent = io != null && !io.FeatureOnly;
            if (sent)
            {
                // No frame goes out behind one that failed.
                foreach (var frame in unit.Owed) sent = sent && Send(io, frame);
                var info = unit.Plan.Info;
                unit.NextAt = _ticks() + (info.Echoes ? Math.Max(info.GapMs, EchoTimeoutMs) : info.GapMs);
            }
            try { io?.Dispose(); } catch { }
            if (!sent) return false;
            ClearOwed(unit);
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL SteelSeries {unit.ProductId:X4} lighting handed back, owed since it failed");
            return true;
        }

        /// <summary>Opens the unit's collection for read and write. False
        /// when it did not open that way.</summary>
        private bool Open(Unit unit, long now)
        {
            IHidReportChannel io = null;
            try { io = _open(unit.Path); } catch { }
            if (io == null || io.FeatureOnly)
            {
                try { io?.Dispose(); } catch { }
                return false;
            }
            unit.Io = io;
            unit.Querying = false;
            unit.Gen3 = false;
            unit.Recorded = false;
            unit.Lit = false;
            unit.ClaimSent = false;
            unit.Reassert = false;
            unit.Announced = false;
            unit.Queue = null;
            unit.Holding = false;
            // The family's gap holds across a close: no report goes out
            // sooner than the gap after the one before, a hand-back
            // included.
            unit.NextAt = Math.Max(unit.NextAt, now);
            unit.SentRgb = NoColor;
            unit.SentColors = null;
            unit.SentMap = null;
            unit.QueueColors = null;
            unit.QueueMap = null;
            unit.AskingSku = false;
            unit.Awaiting = false;
            unit.MouseOff = false;
            unit.QuietUntil = now;
            return true;
        }

        /// <summary>Asks a gated keyboard for its firmware version: once
        /// per open, in front of the claim and never between a claim and its
        /// hand-back in one open
        /// (<see cref="SteelSeriesDirectProtocol.VersionQuery"/>). A
        /// collection with no input report could not answer, and is not
        /// asked. False when the query did not go out.</summary>
        private bool AskVersion(Unit unit, long now)
        {
            if (!unit.Plan.Info.Gated || unit.Io.InputReportLength <= 0) return true;
            Drain(unit);
            if (!Send(unit, SteelSeriesDirectProtocol.VersionQuery())) return false;
            unit.Querying = true;
            unit.QueryUntil = now + QueryTimeoutMs;
            return true;
        }

        /// <summary>One pass over an open, wanted unit. False when a write
        /// or a read failed.</summary>
        private bool Paint(WorkerRun run, Unit unit, int rgb, KeyFrame keys, bool keepsChanging, long now, ref long due)
        {
            var info = unit.Plan.Info;

            if (unit.Querying)
            {
                ReadVersion(unit, now);
                if (unit.Querying)
                {
                    Soonest(ref due, now + _activeTickMs);
                    return true;
                }
            }

            // A per-key board's SKU, once the version says which query it
            // takes, and before its claim, so no lighting of ours is out
            // when it is asked. An answer that is already there is taken in
            // this pass, as the version's is.
            if (info.PerKey && !unit.SkuAsked && !unit.AskingSku && !AskSku(unit, now)) return false;
            if (unit.AskingSku) ReadSku(unit, now);
            if (unit.AskingSku)
            {
                Soonest(ref due, now + _activeTickMs);
                return true;
            }

            // The colors light by light where the claim carries key colors
            // and the unit has more than one light (#557), else one color.
            var map = MapOf(unit);
            int[] colors = null;
            int fill = rgb;
            if (map != null && keys is { IsEmpty: false })
            {
                colors = LightRenderer.Render(map, rgb, keys);
                fill = LightRenderer.Fill(rgb, keys);
            }

            if (unit.Awaiting)
            {
                switch (ReadEcho(unit, now))
                {
                    case Echo.Failed:
                        return false;
                    case Echo.Waiting:
                        Soonest(ref due, now + _activeTickMs);
                        return true;
                    case Echo.Answered:
                        unit.Awaiting = false;
                        unit.MouseOff = false;
                        // The vendor's next report follows the answer, not
                        // the report, so the gap counts from here.
                        unit.NextAt = now + info.GapMs;
                        if (unit.Queue != null && unit.Next == unit.Queue.Length) Finish(unit);
                        break;
                    case Echo.MouseOff:
                        NotListening(unit, now, mouseOff: true);
                        break;
                    default:
                        NotListening(unit, now, mouseOff: false);
                        break;
                }
            }

            if (now < unit.QuietUntil)
            {
                Soonest(ref due, unit.QuietUntil);
                return true;
            }

            if (unit.Queue == null)
            {
                // The color is read when its first report can go, so the
                // one that goes is the newest.
                NoteHeld(unit, info, rgb, map, colors, fill, now);
                long startAt = StartAt(unit, info, rgb, map, colors, fill, keepsChanging, now);
                if (now < startAt)
                {
                    Soonest(ref due, startAt);
                    return true;
                }
                // The claim goes out again on the family's interval, on a
                // clock of its own, and once after Windows resumes: a
                // device that lost power while its collection stayed listed
                // shows its stored lighting again and says nothing.
                if (unit.Reassert || (unit.ClaimSent && info.ReassertMs > 0 && now - unit.ClaimAt >= info.ReassertMs))
                    unit.ClaimSent = false;
                unit.Reassert = false;
                var claim = unit.ClaimSent ? SteelSeriesDirectProtocol.NoFrames : info.Claim(unit.Gen3);
                var color = colors == null
                    ? info.Color(unit.Plan.ColorReport, unit.Gen3, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                    : info.Paint(unit.Plan.ColorReport, unit.Gen3, map.Lights, colors, fill);
                var queue = new SteelSeriesFrame[claim.Length + color.Length];
                claim.CopyTo(queue, 0);
                color.CopyTo(queue, claim.Length);
                unit.Queue = queue;
                unit.Next = 0;
                unit.ClaimFrames = claim.Length;
                unit.QueueRgb = rgb;
                unit.QueueColors = colors;
                unit.QueueMap = map;
                unit.QueueFill = fill;
            }

            while (unit.Queue != null && !unit.Awaiting && unit.Next < unit.Queue.Length && now >= unit.NextAt)
            {
                if (run.Stop) return true;
                var frame = unit.Queue[unit.Next];
                // The crash path learns of the unit before its first frame
                // goes out, so no moment exists in which the device shows a
                // color of ours and the hand-back does not know it
                // (HidppBackend.PaintLighting).
                if (!unit.Recorded) Record(unit);
                // What a dongle has queued is not the answer to this report.
                if (info.Echoes && !Drain(unit)) return false;
                // The crash path takes the record once it has told the
                // worker to stop. An entry made after that is read by
                // nobody, so a worker told to stop sends nothing more.
                if (run.Stop) return true;
                // Said ahead of the write: one that fails may still have
                // reached the device, and the unit is then owed its
                // hand-back (Fail).
                unit.Lit = true;
                if (!Send(unit, frame)) return false;
                if (unit.Next == unit.ClaimFrames) unit.PaintAt = now;
                unit.Next++;
                if (unit.Next == unit.ClaimFrames)
                {
                    unit.ClaimSent = true;
                    unit.ClaimAt = now;
                }
                if (info.Echoes)
                {
                    unit.Awaiting = true;
                    unit.AwaitCommand = frame.Command;
                    unit.AwaitUntil = now + EchoTimeoutMs;
                    // A close drops the answer still owed, so the next open
                    // sends nothing before this answer's limit. Whatever
                    // ends the wait sets the time anew.
                    unit.NextAt = unit.AwaitUntil;
                    // An answer that is already there is taken in this pass.
                    switch (ReadEcho(unit, now))
                    {
                        case Echo.Failed:
                            return false;
                        case Echo.Answered:
                            unit.Awaiting = false;
                            unit.MouseOff = false;
                            unit.NextAt = now + info.GapMs;
                            if (unit.Next == unit.Queue.Length) Finish(unit);
                            break;
                        case Echo.MouseOff:
                            NotListening(unit, now, mouseOff: true);
                            break;
                    }
                }
                else
                {
                    unit.NextAt = now + info.GapMs;
                    if (unit.Next == unit.Queue.Length) Finish(unit);
                }
            }

            if (now < unit.QuietUntil) Soonest(ref due, unit.QuietUntil);
            else if (unit.Awaiting) Soonest(ref due, now + _activeTickMs);
            else if (unit.Queue != null) Soonest(ref due, unit.NextAt);
            else Soonest(ref due, StartAt(unit, info, rgb, map, colors, fill, keepsChanging, now));
            return true;
        }

        /// <summary>The soonest the next paint of <paramref name="rgb"/>,
        /// or of <paramref name="colors"/> light by light, may start, or
        /// long.MaxValue when none is owed. The first color of an open, and
        /// the one after a dongle's pause, go at once. A new picture waits
        /// out the family's fastest repaint, and an unchanged one its
        /// re-assert, where the family has one. None starts inside the gap
        /// after the report before. A family with a hold also waits until the
        /// new picture has stood that long (<see cref="NoteHeld"/>), and is sent
        /// no color whose mode keeps changing it, which a stored picture cannot
        /// follow (#559).</summary>
        private static long StartAt(Unit unit, SteelSeriesFamilyInfo info, int rgb, LightMap map, int[] colors, int fill,
            bool keepsChanging, long now)
        {
            if (info.HoldMs > 0 && keepsChanging) return long.MaxValue;
            long at = unit.SentRgb == NoColor || unit.Reassert ? now
                : !Showing(unit, rgb, map, colors, fill)
                    ? Math.Max(unit.PaintAt + info.RepaintMs, unit.Holding ? unit.HeldSince + info.HoldMs : 0)
                : info.ReassertMs > 0 ? unit.PaintAt + info.ReassertMs
                : long.MaxValue;
            return at == long.MaxValue ? at : Math.Max(at, unit.NextAt);
        }

        /// <summary>Times how long the picture asked for has stood, for a
        /// family with a hold. A picture that differs from the one held starts
        /// the time again, and the one showing ends it.</summary>
        private static void NoteHeld(Unit unit, SteelSeriesFamilyInfo info, int rgb, LightMap map, int[] colors, int fill, long now)
        {
            if (info.HoldMs <= 0) return;
            if (Showing(unit, rgb, map, colors, fill))
            {
                unit.Holding = false;
                return;
            }
            bool same = unit.Holding
                        && (colors == null
                            ? unit.HeldColors == null && rgb == unit.HeldRgb
                            : unit.HeldColors != null && ReferenceEquals(unit.HeldMap, map) && fill == unit.HeldFill
                              && LightRenderer.Same(colors, unit.HeldColors));
            if (same) return;
            unit.Holding = true;
            unit.HeldRgb = rgb;
            unit.HeldColors = colors;
            unit.HeldMap = map;
            unit.HeldFill = fill;
            unit.HeldSince = now;
        }

        /// <summary>Whether the last picture sent whole is the one asked for
        /// now: one color, or <paramref name="colors"/> light by light on
        /// the map they were painted by (HidppBackend.Showing).</summary>
        private static bool Showing(Unit unit, int rgb, LightMap map, int[] colors, int fill)
            => colors == null
                ? unit.SentColors == null && rgb == unit.SentRgb
                : unit.SentColors != null && ReferenceEquals(unit.SentMap, map) && fill == unit.SentFill
                  && LightRenderer.Same(colors, unit.SentColors);

        /// <summary>The last report of a color is out, and answered where
        /// the family answers.</summary>
        private static void Finish(Unit unit)
        {
            unit.SentRgb = unit.QueueRgb;
            unit.SentColors = unit.QueueColors;
            unit.SentMap = unit.QueueMap;
            unit.SentFill = unit.QueueFill;
            unit.Queue = null;
            if (unit.Announced) return;
            unit.Announced = true;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL SteelSeries {unit.ProductId:X4} {unit.Plan.Info.Family} lit gen3={unit.Gen3}");
        }

        /// <summary>A dongle said its mouse is off, or said nothing in
        /// time. The color in flight is dropped, the unit is left alone for
        /// <see cref="QuietMs"/>, and the next paint sends the whole color.
        /// A mouse that is off shows none of ours and wakes into its own
        /// lighting, so the crash path has nothing to undo there.</summary>
        private void NotListening(Unit unit, long now, bool mouseOff)
        {
            unit.Awaiting = false;
            unit.Queue = null;
            unit.SentRgb = NoColor;
            unit.SentColors = null;
            unit.NextAt = now;
            unit.QuietUntil = now + QuietMs;
            unit.MouseOff = mouseOff;
            if (mouseOff) Unrecord(unit);
        }

        /// <summary>Reads what a gated keyboard answered to the version
        /// query. A report that is no version reply is skipped. With no
        /// reply by <see cref="Unit.QueryUntil"/> the board gets the frames
        /// of its first reading, and is not asked again.</summary>
        private void ReadVersion(Unit unit, long now)
        {
            for (int i = 0; i < ReadLimit; i++)
            {
                int read = unit.Io.ReadInput(unit.Input, 0);
                // Nothing queued, or nothing to read from: the limit decides.
                if (read <= 0) break;
                var reply = unit.Input.AsSpan(1, read - 1);
                if (!SteelSeriesDirectProtocol.IsVersionReply(reply)) continue;
                unit.Gen3 = SteelSeriesDirectProtocol.IsGen3(reply);
                unit.Querying = false;
                return;
            }
            if (now < unit.QueryUntil) return;
            unit.Gen3 = false;
            unit.Querying = false;
        }

        /// <summary>
        /// Asks a per-key board for what picks its keys, as OpenRGB asks it
        /// once at detection (SteelSeriesApexController::GetSerial,
        /// SteelSeriesApexController.cpp:351-419): a board of the first
        /// generation for its serial, whose first five characters are the
        /// SKU, the wired Apex Pro TKL Gen 3 for its region. A board on
        /// firmware 1.19.7 or later takes the third generation's path,
        /// which reads no serial. Every other board's SKU is fixed by its
        /// product, and a collection with no input report is not asked.
        /// False when the query did not go out.
        /// </summary>
        private bool AskSku(Unit unit, long now)
        {
            var info = unit.Plan.Info;
            ushort pid = unit.ProductId;
            byte command = SteelSeriesApexLayouts.AsksRegion(pid) ? SteelSeriesApexLayouts.RegionCommand
                : SteelSeriesApexLayouts.AsksSerial(pid) && !(info.Gated && unit.Gen3) ? SteelSeriesApexLayouts.SerialCommand
                : (byte)0;
            if (command == 0 || unit.Io.InputReportLength <= 0)
            {
                Learned(unit, SteelSeriesApexLayouts.FixedSku(pid)
                              ?? (info.Gated && unit.Gen3 ? SteelSeriesApexLayouts.TklUsSku : null));
                return true;
            }
            Drain(unit);
            if (!Send(unit, SteelSeriesDirectProtocol.SkuQuery(command))) return false;
            unit.AskingSku = true;
            unit.SkuCommand = command;
            unit.SkuUntil = now + QueryTimeoutMs;
            return true;
        }

        /// <summary>Reads the answer to <see cref="AskSku"/>. The serial is
        /// the report's first characters, and the region the third byte
        /// behind an echoed F5. A report that is neither is skipped, and a
        /// board that does not answer by <see cref="Unit.SkuUntil"/> keeps
        /// the keys of its product's form.</summary>
        private void ReadSku(Unit unit, long now)
        {
            for (int i = 0; i < ReadLimit; i++)
            {
                int read = unit.Io.ReadInput(unit.Input, 0);
                if (read <= 0) break;
                var reply = unit.Input.AsSpan(1, read - 1);
                if (unit.SkuCommand == SteelSeriesApexLayouts.RegionCommand)
                {
                    if (reply.Length == 0 || reply[0] != SteelSeriesApexLayouts.RegionCommand) continue;
                    Learned(unit, SteelSeriesApexLayouts.SkuOfRegion(reply));
                    return;
                }
                if (SteelSeriesApexLayouts.SkuOfSerial(reply) is not string sku) continue;
                Learned(unit, sku);
                return;
            }
            if (now < unit.SkuUntil) return;
            Learned(unit, null);
        }

        /// <summary>The SKU query is done: the unit's map follows
        /// <paramref name="sku"/>, or its product's form for none, from this
        /// pass's paint on.</summary>
        private void Learned(Unit unit, string sku)
        {
            unit.AskingSku = false;
            unit.SkuAsked = true;
            unit.Sku = sku;
            SyncMap(unit, relearn: false);
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL SteelSeries {unit.ProductId:X4} keys of SKU {sku ?? "none"}"
                + (SteelSeriesApexLayouts.Knows(sku) ? "" : $", {SteelSeriesApexLayouts.FormOf(unit.ProductId)} layout"));
        }

        // ─────────────────────────────────────────────
        //  Light maps (#557)
        // ─────────────────────────────────────────────

        /// <summary>The map a unit is painted by light by light, or null for
        /// one painted one color.</summary>
        private LightMap MapOf(Unit unit)
            => _maps.TryGetValue(unit.Path, out var entry) && entry.Map.PerLight ? entry.Map : null;

        /// <summary>
        /// Publishes the light map of every unit with more than one light,
        /// and takes down the maps of units that left (HidppBackend.SyncMaps).
        /// A unit found again with the same lights keeps its map, so the
        /// Lighting tab does not redraw on every scan. The names Map Key
        /// Lights learned go over each map, again whenever they change.
        /// </summary>
        private void SyncMaps(List<Unit> units)
        {
            int version = LearnedKeyMaps.Version;
            bool relearn = version != _mapsVersion;
            _mapsVersion = version;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in units)
                if (SyncMap(unit, relearn)) seen.Add(unit.Path);
            List<string> gone = null;
            foreach (string key in _maps.Keys)
                if (!seen.Contains(key)) (gone ??= new List<string>()).Add(key);
            if (gone == null) return;
            foreach (string key in gone)
            {
                _maps.Remove(key);
                PeripheralLightMaps.Remove(new OutputPath(OutputFamily.SteelSeriesUnit, key));
            }
        }

        /// <summary>Brings one unit's published map in line with its lights,
        /// with the learned names again where <paramref name="relearn"/>.
        /// True when the unit has more than one light.</summary>
        private bool SyncMap(Unit unit, bool relearn)
        {
            var fresh = LightsOf(unit);
            if (fresh == null) return false;
            var path = new OutputPath(OutputFamily.SteelSeriesUnit, unit.Path);
            if (_maps.TryGetValue(unit.Path, out var known) && SameLights(known.Base, fresh))
            {
                if (!relearn) return true;
                var again = LearnedKeyMaps.Apply(known.Base);
                if (SameLights(again, known.Map)) return true;
                _maps[unit.Path] = (known.Base, again);
                PeripheralLightMaps.Publish(path, again);
                return true;
            }
            var map = LearnedKeyMaps.Apply(fresh);
            _maps[unit.Path] = (fresh, map);
            PeripheralLightMaps.Publish(path, map);
            return true;
        }

        /// <summary>A unit's lights as its family or its SKU gives them, or
        /// null for a family painted one color.</summary>
        private static LightMap LightsOf(Unit unit)
        {
            var info = unit.Plan.Info;
            string product = $"steelseries:{SteelSeriesDirectProtocol.VendorId:X4}:{unit.ProductId:X4}";
            if (info.PerKey)
            {
                string sku = unit.Sku ?? SteelSeriesApexLayouts.FixedSku(unit.ProductId);
                var form = SteelSeriesApexLayouts.FormOf(unit.ProductId);
                var lights = SteelSeriesApexLayouts.Lights(SteelSeriesApexLayouts.Grid(sku, form));
                string source = SteelSeriesApexLayouts.Knows(sku)
                    ? "OpenRGB SteelSeriesApexRegions.h, SKU " + sku
                    : "OpenRGB SteelSeriesApexRegions.h, " + form + " layout";
                return new LightMap(lights, source) { Product = product };
            }
            if (info.Lights is not { Count: > 1 } own) return null;
            return new LightMap(own, "rivalcfg and OpenRGB SteelSeriesController") { Product = product };
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

        /// <summary>Looks for a dongle's answer to the report before,
        /// without waiting. Byte 0 of what Windows delivers is the report
        /// ID position, so the answer starts at byte 1. A report that
        /// starts with the command just sent is the answer, and no
        /// reference reads the byte behind it. One that starts 40 FF says
        /// the mouse is off. Any other is somebody else's, the
        /// vendor program's own queries and the mouse's status reports
        /// share the endpoint, and is skipped, as the battery tool for the
        /// Aerox 5 Wireless skips them (Aerox_5-LennardKittner
        /// src/lib.rs:82-110).</summary>
        private Echo ReadEcho(Unit unit, long now)
        {
            for (int i = 0; i < ReadLimit; i++)
            {
                int read = unit.Io.ReadInput(unit.Input, 0);
                if (read < 0) return Echo.Failed;
                if (read == 0) break;
                if (read < 2) continue;
                if (unit.Input[1] == unit.AwaitCommand) return Echo.Answered;
                if (SaysMouseOff(unit, read)) return Echo.MouseOff;
            }
            return now >= unit.AwaitUntil ? Echo.TimedOut : Echo.Waiting;
        }

        /// <summary>Whether the input report just read is a dongle's 40 FF.</summary>
        private static bool SaysMouseOff(Unit unit, int read)
            => read >= 3 && unit.Input[1] == MouseOffReply && unit.Input[2] == MouseOffFlag;

        /// <summary>Discards the input reports a collection has queued, as
        /// two references do in front of a question (Aerox_5-LennardKittner
        /// src/lib.rs:83-85, signal-plugins
        /// Steelseries_Aerox_Wireless_Mouse.js:253). False when the read
        /// failed.</summary>
        private static bool Drain(Unit unit)
        {
            for (int i = 0; i < ReadLimit; i++)
            {
                int read = unit.Io.ReadInput(unit.Input, 0);
                if (read < 0) return false;
                if (read == 0) break;
            }
            return true;
        }

        private static bool Send(Unit unit, SteelSeriesFrame frame) => Send(unit.Io, frame);

        private static bool Send(IHidReportChannel io, SteelSeriesFrame frame)
            => frame.Report == SteelSeriesReport.Output
                ? io.WriteOutput(frame.Buffer, WriteTimeoutMs)
                : io.SetFeature(frame.Buffer);

        // ─────────────────────────────────────────────
        //  Hand back, fail, close
        // ─────────────────────────────────────────────

        /// <summary>
        /// Lets go of an open unit nothing claims any more. True when the
        /// unit is done with and its handle can close, false when the
        /// hand-back still waits its turn and a later pass finishes it.
        ///
        /// <para>The hand-back is a report like any other, so it keeps the
        /// family's order: the answer to a dongle's report before it, then
        /// the gap. <paramref name="blocking"/> is the worker's finally,
        /// which has no later pass and waits here instead, never longer
        /// than twice one answer's limit and one gap.
        /// <paramref name="lost"/> says a hand-back was owed and did not go
        /// out: its write failed, or the answer in front of it could not be
        /// read.</para>
        /// </summary>
        private bool Release(Unit unit, OutputPath path, long now, bool blocking, ref long due, out bool lost)
        {
            lost = false;
            var info = unit.Plan.Info;
            // Whatever comes next starts over: a color in flight is not
            // finished, and a claim that comes back paints from the top.
            unit.Queue = null;
            unit.SentRgb = NoColor;
            unit.SentColors = null;

            if (LeftToVendorProgram(path))
            {
                // SteelSeries GG started and the linker took the unit's
                // path away. GG writes the device's whole configuration
                // when it connects and then streams, so a hand-back that
                // landed after its first frame would only clear a color GG
                // just set. The worker lets go without a frame.
                PadForge.Engine.SdlDiagLog.WriteLine(PeripheralOutputs.TakenByOpenRgb(path)
                    ? $"PERIPHERAL SteelSeries {unit.ProductId:X4} left to OpenRGB"
                    : $"PERIPHERAL SteelSeries {unit.ProductId:X4} left to SteelSeries GG, which started");
                return true;
            }

            // Nothing of ours went out, or no frame undoes it.
            var handBack = unit.Lit ? info.HandBack(unit.Gen3) : SteelSeriesDirectProtocol.NoFrames;
            if (handBack.Length == 0) return true;

            if (unit.Awaiting)
            {
                // The answer still owed says whether the mouse listens, so
                // it is read before the hand-back is decided: a mouse that
                // was off may have taken the report that asked.
                var echo = blocking ? WaitEcho(unit, now) : ReadEcho(unit, now);
                if (echo == Echo.Waiting)
                {
                    Soonest(ref due, now + _activeTickMs);
                    return false;
                }
                unit.Awaiting = false;
                // A collection that cannot be read is not written to.
                if (echo == Echo.Failed)
                {
                    lost = true;
                    return true;
                }
                if (echo == Echo.MouseOff) unit.MouseOff = true;
                else if (echo == Echo.Answered) unit.MouseOff = false;
                unit.NextAt = echo == Echo.Answered ? now + info.GapMs : now;
            }
            // A mouse that is off wakes into its own lighting.
            if (unit.MouseOff) return true;
            if (now < unit.NextAt)
            {
                if (!blocking)
                {
                    Soonest(ref due, unit.NextAt);
                    return false;
                }
                Thread.Sleep((int)Math.Min(unit.NextAt - now, info.GapMs));
            }

            bool sent = true;
            foreach (var frame in handBack)
            {
                if (!Send(unit, frame))
                {
                    sent = false;
                    break;
                }
            }
            // The next open keeps the gap from this report, and gives a
            // dongle the time to answer it.
            unit.NextAt = _ticks() + (info.Echoes ? Math.Max(info.GapMs, EchoTimeoutMs) : info.GapMs);
            lost = !sent;
            if (sent) PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL SteelSeries {unit.ProductId:X4} lighting handed back");
            return true;
        }

        /// <summary>The finally's wait for a dongle's answer: one bounded
        /// read per report that comes in, with half the time left after
        /// each report that is not the answer, so the wait ends inside
        /// twice the limit whatever the dongle sends.</summary>
        private Echo WaitEcho(Unit unit, long now)
        {
            int budget = (int)Math.Clamp(unit.AwaitUntil - now, 0, EchoTimeoutMs);
            while (budget > 0)
            {
                int read = unit.Io.ReadInput(unit.Input, budget);
                if (read < 0) return Echo.Failed;
                if (read == 0) break;
                if (read >= 2 && unit.Input[1] == unit.AwaitCommand) return Echo.Answered;
                if (SaysMouseOff(unit, read)) return Echo.MouseOff;
                budget /= 2;
            }
            return Echo.TimedOut;
        }

        /// <summary>Whether a lit unit's path is gone because SteelSeries
        /// GG started: no row holds the path for lighting any more, and GG
        /// runs. A path that is still linked and merely unclaimed, a switch
        /// turned off or a device unassigned, is handed back as ever. The
        /// table is read before the presence: the link pass publishes the
        /// presence first, so a table that shows the path withdrawn is
        /// never paired with a presence older than it
        /// (HidppBackend.LeftToLogitechProgram). No row holds the path of
        /// a unit listed asleep, so for a unit that is owed its hand-back
        /// GG alone decides. A path the linker closed because OpenRGB holds
        /// the device (#557, <see cref="PeripheralOutputs.TakenByOpenRgb"/>)
        /// is let go the same way.</summary>
        private static bool LeftToVendorProgram(OutputPath path)
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
            return PeripheralOutputs.Presence.SteelSeriesGGRunning || PeripheralOutputs.TakenByOpenRgb(path);
        }

        /// <summary>A write, a read or an open failed. The channel closes
        /// and the unit is listed asleep, never taken off the list: it
        /// leaves when its collection leaves the walk. The failed report is
        /// not sent again. The claim stays where the dispatcher put it and
        /// paints once the unit opens again.
        ///
        /// <para>A unit that a frame went to, or was tried on, may still
        /// show a color of ours with no channel left to undo it through.
        /// Where a hand-back would have gone to it had its claim ended
        /// instead (<see cref="Release"/>: its family has one, and its
        /// dongle did not last say the mouse is off), it is owed that
        /// hand-back from here on (<see cref="Unit.Owed"/>), and its
        /// entry stays on the record.</para></summary>
        private void Fail(Unit unit, long now, string why, ref bool changed, ref bool failed, ref long due)
        {
            var handBack = unit.Lit && !unit.MouseOff ? unit.Plan.Info.HandBack(unit.Gen3) : SteelSeriesDirectProtocol.NoFrames;
            if (handBack.Length > 0) unit.Owed = handBack;
            Close(unit);
            unit.Asleep = true;
            unit.RetryAt = now + RetryMs;
            changed = true;
            failed = true;
            Soonest(ref due, unit.RetryAt);
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL SteelSeries {unit.ProductId:X4} {why}, listed asleep");
        }

        /// <summary>Ends what a unit is owed, with no frame, and takes its
        /// entry off the crash path's record.</summary>
        private void ClearOwed(Unit unit)
        {
            if (unit.Owed == null) return;
            unit.Owed = null;
            Unrecord(unit);
        }

        private void Close(Unit unit)
        {
            // The record keeps the entry of a unit that is owed its
            // hand-back.
            if (unit.Owed == null) Unrecord(unit);
            var io = unit.Io;
            unit.Io = null;
            unit.Queue = null;
            unit.Querying = false;
            unit.AskingSku = false;
            unit.Awaiting = false;
            unit.Lit = false;
            unit.SentRgb = NoColor;
            unit.SentColors = null;
            try { io?.Dispose(); } catch { }
        }

        // ─────────────────────────────────────────────
        //  The crash path's record
        // ─────────────────────────────────────────────

        /// <summary>Puts the unit's hand-back on record. A family with no
        /// hand-back records nothing, and the crash path then writes
        /// nothing to it. Worker thread only, swapped whole.</summary>
        private void Record(Unit unit)
        {
            var handBack = unit.Plan.Info.HandBack(unit.Gen3);
            if (handBack.Length == 0) return;
            var frames = new byte[handBack.Length][];
            for (int i = 0; i < frames.Length; i++) frames[i] = handBack[i].Buffer;
            var current = Volatile.Read(ref _recorded);
            var next = new (string, byte[][])[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = (unit.Path, frames);
            Volatile.Write(ref _recorded, next);
            unit.Recorded = true;
            unit.RecordedFrames = frames;
        }

        /// <summary>Takes the unit's own entry off the record. Not every
        /// entry of its path: a worker a timed-out Stop left behind closes
        /// its units while the next worker holds the same collections.</summary>
        private void Unrecord(Unit unit)
        {
            if (!unit.Recorded) return;
            unit.Recorded = false;
            var frames = unit.RecordedFrames;
            unit.RecordedFrames = null;
            var current = Volatile.Read(ref _recorded);
            var next = new List<(string, byte[][])>(current.Length);
            foreach (var entry in current)
                if (!ReferenceEquals(entry.Frames, frames)) next.Add(entry);
            Volatile.Write(ref _recorded, next.ToArray());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }
}
