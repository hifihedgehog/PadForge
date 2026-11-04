using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The shared HID lighting worker (#557). It lights every device of the
    /// OpenRGB controller families in <see cref="HidLightingFamilies"/>
    /// through the device's own lighting collection, light by light. One
    /// thread owns every collection of those families, the shape of
    /// <see cref="SteelSeriesDirectBackend"/>, and each family supplies only
    /// what differs: which collections are its own, its lights, and how a
    /// session reads, writes and hands back (<see cref="IHidLightingFamily"/>).
    ///
    /// <para>Scan. From the list the host hands over
    /// (<see cref="Rescan"/>), by the families' registrations, with nothing
    /// asked of any device but the strings a model that has to tell its
    /// devices from others by them reads, once per appearance, through a
    /// handle with no access right (<see cref="HidLightingModel.Identify"/>). A unit stays listed for as long as its
    /// collection is in the last list, and its model's light map is
    /// published for the Lighting tab and Map Key Lights from the scan on,
    /// with the names Map Key Lights learned for the model
    /// (<see cref="LearnedKeyMaps"/>). A family whose devices are no HID
    /// collections (<see cref="IOwnLookFamily"/>) looks for them on its own
    /// at the same cue, and a unit it found stays listed while its look
    /// still finds it. Such a unit is opened and written through its
    /// model's own channel (<see cref="HidLightingModel.Open"/>), and its
    /// crash frames go out through its model's own one-shot writer, by the
    /// same rules as a collection's.</para>
    ///
    /// <para>Open. Only a unit a row links and a claim resolves on, for
    /// read and write, and then the model reads what its session needs
    /// (<see cref="HidLightingModel.Begin"/>). A device that talks through
    /// more collections than one is listed while each of them is in the
    /// walk, and its other collections open beside the unit's, from the
    /// same container, and close with it
    /// (<see cref="HidLightingModel.Siblings"/>). A collection that opens with
    /// no access right only is one another program holds, and is not
    /// written to, unless its model's lighting lives on a mouse or keyboard
    /// collection, which Windows opens that way for everyone and which
    /// takes feature reports on it (<see cref="HidLightingModel.HeldByWindows"/>). A unit no claim resolves on is opened only while its
    /// model owes it colors an earlier run left in the device's memory
    /// (<see cref="HidLightingModel.Owed"/>), to hand those back, from
    /// <see cref="RepairGraceMs"/> after the worker starts.</para>
    ///
    /// <para>Paint. The claim's color and key frame are rendered into one
    /// color per light (<see cref="LightRenderer.Render"/>). A picture the
    /// device already shows is not sent again. A changed one goes out once
    /// it has stayed the same for the model's hold time and the model's
    /// repaint interval has passed since the picture before, and a picture
    /// the session cannot send yet is tried again after the time it names.
    /// The session reads the device's state again on the model's recheck
    /// interval. A device that drops the host's colors when the host goes
    /// quiet gets the session's keep-alive on the model's interval while a
    /// picture shows, with no change in the picture
    /// (<see cref="HidLightingModel.KeepAliveMs"/>). The crash path's record holds the session's own frames for
    /// undoing what it wrote, kept current after every call into the
    /// session.</para>
    ///
    /// <para>Hand back. When no claim resolves on the unit or its path
    /// leaves the link table, the session returns the device to its own
    /// lighting and the handle closes. Not when the path left because a
    /// program the model names started (<see cref="HidLightingModel.Programs"/>):
    /// the worker lets go with nothing sent, and sends the device nothing,
    /// owed colors and the crash path's frames included, while the program
    /// runs.</para>
    ///
    /// <para>Fail. A write or a read that fails, a hand-back included,
    /// closes the unit's channel and lists the unit asleep, asks the host
    /// for a walk, and tries the unit again every <see cref="RetryMs"/> by
    /// opening it. What a failed hand-back leaves owed stays in the model's
    /// keeping, which the next open or the next run settles.</para>
    /// </summary>
    internal sealed class HidLightingBackend : IDirectUnitBackend
    {
        internal const int IdleTickMs = HidppBackend.IdleTickMs;
        internal const int PredecessorJoinMs = HidppBackend.PredecessorJoinMs;
        internal const int StopJoinMs = 3000;
        internal const int PanicJoinMs = HidppBackend.PanicJoinMs;

        /// <summary>The bound of each one-shot write on the crash path.</summary>
        internal const int OneShotMs = 200;

        /// <summary>How long an asleep unit waits before it is tried again,
        /// the interval the HID++ worker asks a silent slot on.</summary>
        internal const int RetryMs = HidppBackend.FirstScanMs;

        /// <summary>How long a unit whose container Windows could not name
        /// yet waits before the next lookup.</summary>
        internal const int ContainerRetryMs = 1000;

        /// <summary>How long after the worker starts a unit no claim
        /// resolves on waits before the colors an earlier run left in it go
        /// back: time for the first device scan (2000 ms,
        /// InputManager.EnumerationIntervalMs) and the link pass after it
        /// (<see cref="PeripheralOutputHost.LinkMs"/>) to resolve this
        /// run's claims, so a keyboard a claim lights again is not handed
        /// back and lit again a moment later.</summary>
        internal const int RepairGraceMs = 5000;

        /// <summary>One present lighting collection and what the worker
        /// holds of it. Worker thread only.</summary>
        private sealed class Unit
        {
            /// <summary>The interface path, kept exactly as the first walk
            /// gave it: it is the unit's key, compared ordinally.</summary>
            public string Path;

            /// <summary>The collection as the last walk gave it, or null for
            /// a unit an own look found.</summary>
            public HidCollectionInfo Info;

            public ushort VendorId;
            public ushort ProductId;
            public HidLightingModel Model;
            public Guid Container;
            public long ContainerRetryAt;
            public bool Seen;

            /// <summary>The family whose own look found the unit, or null
            /// for a HID collection of the host's walk.</summary>
            public IOwnLookFamily Own;

            public bool BuiltInKeyboard;

            /// <summary>The map a session read from the device
            /// (<see cref="HidLightingSession.Map"/>), or null for the
            /// model's. Kept while the device stays, across closes and
            /// reopens, and dropped only when the unit leaves the walk, as
            /// the SteelSeries worker keeps the SKU it learned for the
            /// device's stay (SteelSeriesDirectBackend SkuAsked).</summary>
            public LightMap SessionMap;

            /// <summary>The published map: the session's, else the model's,
            /// with the learned names applied.</summary>
            public LightMap Map;

            public bool Asleep;
            public long RetryAt;

            /// <summary>The open channel and its session, or null.
            /// Everything below is of one open and is reset by the next.
            /// <see cref="Io"/> is the channel of a HID collection, and null
            /// for a unit an own look found, whose channel is
            /// <see cref="Handle"/> alone.</summary>
            public IDisposable Handle;
            public IHidReportChannel Io;
            public HidLightingSession Session;

            /// <summary>The channels of the model's siblings, open beside
            /// <see cref="Io"/> in the model's order, or null.</summary>
            public IHidReportChannel[] SiblingIo;

            public bool Recorded;
            public byte[][] RecordedFrames;

            /// <summary>The colors the device shows from the session, the
            /// map they were rendered for, and when they went out.</summary>
            public int[] Shown;
            public LightMap ShownMap;
            public long SentAt;

            /// <summary>A changed picture waiting to go out, its map, and
            /// since when it has stayed the same.</summary>
            public int[] Pending;
            public LightMap PendingMap;
            public long PendingAt;

            /// <summary>When a picture the session could not send yet is
            /// tried again.</summary>
            public long LaterAt;

            /// <summary>When the session reads the device's state
            /// again.</summary>
            public long CheckAt;

            /// <summary>When the session's last keep-alive went out, or when
            /// the session began.</summary>
            public long KeptAt;

            public string Label => $"{Model?.Name ?? "unit"} {VendorId:X4}:{ProductId:X4}";
        }

        /// <summary>One unit's entry on the crash path's record: its key, the
        /// frames that undo what its session wrote, whether they are feature
        /// reports, its model's programs, and for a unit an own look found,
        /// the model's one-shot writer (<see cref="HidLightingModel.WriteOnce"/>).</summary>
        internal sealed record CrashRecord(string Path, byte[][] Frames, bool Feature,
            IReadOnlyList<VendorProgram> Programs, Func<string, byte[], int, bool> Write);

        /// <summary>The units whose sessions name frames that undo what they
        /// wrote, for the crash path, which cannot wait on this worker.
        /// Swapped whole.</summary>
        private CrashRecord[] _recorded = Array.Empty<CrashRecord>();

        private readonly IReadOnlyList<IHidLightingFamily> _families;
        private readonly Func<string, IHidReportChannel> _open;
        private readonly Func<string, IHidReportChannel> _query;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<long> _ticks;
        private readonly Func<string, byte[], int, bool> _setFeatureOnce;
        private readonly Func<string, byte[], int, bool> _writeOutputOnce;
        private readonly Action<int> _wait;
        private readonly int _idleTickMs;
        private readonly int _predecessorJoinMs;
        private readonly int _stopJoinMs;
        private readonly AutoResetEvent _wake = new(false);

        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;
        private IReadOnlyList<HidCollectionInfo> _present;

        /// <summary>The walk the worker last scanned, where a unit's
        /// siblings are found. Worker thread only.</summary>
        private IReadOnlyList<HidCollectionInfo> _walk = Array.Empty<HidCollectionInfo>();

        /// <summary>The containers of the walk's paths that siblings were
        /// looked for among, kept while the path stays in the walk. Only a
        /// container Windows named is kept. Worker thread only.</summary>
        private readonly Dictionary<string, Guid> _siblingContainers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What a model's identity check said of each path of the
        /// walk (<see cref="HidLightingModel.Identify"/>), kept while the path
        /// stays. A path whose strings could not be read has no entry and
        /// is asked again at the next walk. Worker thread only.</summary>
        private readonly Dictionary<string, bool> _identified = new(StringComparer.OrdinalIgnoreCase);
        private Thread _thread;
        private Thread _worker;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;
        private int _waitMs;
        private long _repairAt;

        /// <summary>The newest worker thread of any instance, which the
        /// next one waits for. Static because the collections it holds are
        /// the machine's.</summary>
        private static Thread s_lastWorker;

        public HidLightingBackend()
            : this(null, null, null, null, null, null, null, IdleTickMs) { }

        /// <summary>Test seam: the families, the channel opener, the
        /// container lookup, the clock, the crash path's two one-shot
        /// writers, the wait a session takes between an exchange and the
        /// next, the idle tick, the wait for a predecessor worker, Stop's
        /// wait, and the opener with no access right the scan reads strings
        /// through. The openers and the one-shot writers are the worker's
        /// only ways to a device, and their defaults open nothing in the
        /// test host (<see cref="RealHidAccess"/>).</summary>
        internal HidLightingBackend(IReadOnlyList<IHidLightingFamily> families, Func<string, IHidReportChannel> open,
            Func<string, Guid> containerOf, Func<long> ticks, Func<string, byte[], int, bool> setFeatureOnce,
            Func<string, byte[], int, bool> writeOutputOnce, Action<int> wait, int idleTickMs,
            int predecessorJoinMs = PredecessorJoinMs, int stopJoinMs = StopJoinMs,
            Func<string, IHidReportChannel> query = null)
        {
            _families = families ?? HidLightingFamilies.All;
            _open = open ?? HidReportChannel.Open;
            _query = query ?? HidReportChannel.OpenFeatureOnly;
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _ticks = ticks ?? (() => Environment.TickCount64);
            _setFeatureOnce = setFeatureOnce ?? HidReportChannel.SetFeatureOnce;
            _writeOutputOnce = writeOutputOnce ?? HidReportChannel.WriteOutputOnce;
            _wait = wait ?? (ms => Thread.Sleep(ms));
            _idleTickMs = idleTickMs;
            _predecessorJoinMs = predecessorJoinMs;
            _stopJoinMs = stopJoinMs;
        }

        public OutputFamily Family => OutputFamily.HidLightingUnit;

        public DirectBackendSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public event Action SnapshotChanged;
        public event Action RescanRequested;

        /// <summary>Passes the worker finished, for a test that waits on
        /// passes and times nothing.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        internal int WaitMs => Volatile.Read(ref _waitMs);

        internal bool WorkerAlive => Volatile.Read(ref _worker)?.IsAlive == true;

        /// <summary>The crash path's record, for a test.</summary>
        internal CrashRecord[] Recorded => Volatile.Read(ref _recorded);

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _thread != null) return;
            var run = new WorkerRun();
            _run = run;
            Volatile.Write(ref _repairAt, _ticks() + RepairGraceMs);
            Volatile.Write(ref _present, null);
            Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralHidLighting" };
            Volatile.Write(ref _worker, _thread);
            _thread.Start();
            PeripheralOutputs.LightingChanged += Wake;
            PeripheralOutputs.LinksChanged += Wake;
            LearnedKeyMaps.Changed += OnLearned;
        }

        public void Stop()
        {
            PeripheralOutputs.LightingChanged -= Wake;
            PeripheralOutputs.LinksChanged -= Wake;
            LearnedKeyMaps.Changed -= OnLearned;
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
        /// unit still on record gets its frames through a one-shot write on
        /// a handle of its own.</summary>
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
            foreach (var record in Interlocked.Exchange(ref _recorded, Array.Empty<CrashRecord>()))
            {
                // A program of the model runs and has the device: nothing
                // goes to it, as the Razer worker sends nothing while
                // Synapse runs (RazerDirectBackend.ReleaseClaimedNow). Nor
                // while OpenRGB holds it (#557).
                if (ProgramRuns(record.Programs)
                    || PeripheralOutputs.TakenByOpenRgb(new OutputPath(OutputFamily.HidLightingUnit, record.Path)))
                    continue;
                foreach (byte[] frame in record.Frames)
                {
                    left = deadline - _ticks();
                    if (left <= 0) return;
                    try { WriteOnce(record.Path, frame, record.Feature, record.Write, (int)Math.Min(left, OneShotMs)); } catch { }
                }
            }
        }

        /// <summary>One frame through a handle of its own: the model's writer
        /// for a unit an own look found, else a feature or an output report
        /// to the HID collection.</summary>
        private bool WriteOnce(string path, byte[] frame, bool feature, Func<string, byte[], int, bool> own, int timeoutMs)
            => own != null ? own(path, frame, timeoutMs)
                : feature ? _setFeatureOnce(path, frame, timeoutMs) : _writeOutputOnce(path, frame, timeoutMs);

        private void OnLearned(object sender, EventArgs e) => Wake();

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
            // (SteelSeriesDirectBackend.Worker).
            var previous = Interlocked.Exchange(ref s_lastWorker, Thread.CurrentThread);
            if (previous != null && previous != Thread.CurrentThread && previous.IsAlive
                && !previous.Join(_predecessorJoinMs))
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL HidLighting previous worker still running after {_predecessorJoinMs} ms, starting anyway");
            var units = new List<Unit>();
            int learned = LearnedKeyMaps.Version;
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
                        // The first scan publishes whatever it found, so the
                        // host stops waiting for these families.
                        changed = Scan(units, present, now) || scanned == null;
                        scanned = present;
                    }

                    int version = LearnedKeyMaps.Version;
                    if (version != learned)
                    {
                        learned = version;
                        foreach (var unit in units) PublishMap(unit);
                    }

                    var links = PeripheralOutputs.Links;
                    foreach (var unit in units)
                    {
                        if (run.Stop) break;
                        Serve(run, unit, links, _ticks(), ref changed, ref failed, ref due);
                    }

                    if (changed) Publish(units);
                    if (failed)
                    {
                        try { RescanRequested?.Invoke(); } catch { }
                    }

                    int wait = due == long.MaxValue ? _idleTickMs : (int)Math.Clamp(due - _ticks(), 1, _idleTickMs);
                    Volatile.Write(ref _waitMs, wait);
                    Interlocked.Increment(ref _passes);
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL HidLighting worker fault: " + ex.GetType().Name);
            }
            finally
            {
                // Each device goes back to its own lighting before its
                // handle closes, so stopping the engine leaves no keyboard
                // held in a color of ours.
                var successor = _run;
                bool ownRun = successor == null || ReferenceEquals(successor, run);
                foreach (var unit in units)
                {
                    if (unit.Handle == null) continue;
                    bool released = false;
                    try { released = Release(unit); } catch { }
                    var frames = released ? null : unit.Session?.CrashFrames;
                    bool feature = unit.Session?.Feature ?? true;
                    var own = unit.Own != null ? unit.Model.WriteOnce : null;
                    // The maps are taken down below, and a successor's are
                    // its own.
                    Close(unit);
                    // A hand-back that did not go out on the worker's
                    // channel goes out through a report of its own, as on
                    // the crash path, where the session names frames for it.
                    // Not from a worker that outlived its Stop into the next
                    // run, whose worker has the collections by then. A unit
                    // whose program runs is released with nothing to send
                    // (Release). What stays owed is in the model's keeping
                    // for the next run.
                    if (frames == null || !ownRun) continue;
                    foreach (var frame in frames)
                    {
                        try { WriteOnce(unit.Path, frame, feature, own, OneShotMs); } catch { }
                    }
                }
                var current = _run;
                if (current == null || ReferenceEquals(current, run))
                {
                    foreach (var unit in units) PeripheralLightMaps.Remove(PathOf(unit));
                    Volatile.Write(ref _recorded, Array.Empty<CrashRecord>());
                    Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
                }
            }
        }

        private static OutputPath PathOf(Unit unit) => new(OutputFamily.HidLightingUnit, unit.Path);

        // ─────────────────────────────────────────────
        //  Scan
        // ─────────────────────────────────────────────

        /// <summary>Brings the unit list in line with a walk. True when the
        /// list changed.</summary>
        private bool Scan(List<Unit> units, IReadOnlyList<HidCollectionInfo> present, long now)
        {
            bool changed = false;
            _walk = present;
            ForgetGone(present);
            foreach (var collection in present)
            {
                var model = HidLightingFamilies.Match(collection, _families);
                if (model == null) continue;
                // A device that talks through more collections is one OpenRGB
                // makes only once it found each of them
                // (RoccatControllerDetect.cpp:155-201).
                if (!SiblingsPresent(model, collection)) continue;
                if (model.Identify != null && !Identified(model, collection)) continue;
                var unit = Find(units, collection.Path);
                if (unit == null)
                {
                    unit = new Unit
                    {
                        Path = collection.Path, VendorId = collection.VendorId, ProductId = collection.ProductId,
                        Model = model,
                    };
                    LookUpContainer(unit, now);
                    Found(units, unit);
                    changed = true;
                }
                unit.Info = collection;
                unit.Seen = true;
            }

            // The families whose devices are no HID collections look on
            // their own, at the same cue (IDirectUnitBackend.Rescan).
            foreach (var family in _families)
            {
                if (family is not IOwnLookFamily own) continue;
                IReadOnlyList<OwnDevice> found;
                try { found = own.Look(); }
                catch { found = null; }
                if (found == null)
                {
                    // A look that could not be made says nothing about
                    // what is there.
                    foreach (var unit in units)
                        if (ReferenceEquals(unit.Own, own)) unit.Seen = true;
                    continue;
                }
                foreach (var device in found)
                {
                    if (device?.Key == null || device.Model == null) continue;
                    var unit = Find(units, device.Key);
                    if (unit == null)
                    {
                        unit = new Unit
                        {
                            Path = device.Key, VendorId = device.VendorId, ProductId = device.ProductId,
                            Model = device.Model, Own = own, BuiltInKeyboard = device.BuiltInKeyboard,
                            Container = device.Container,
                        };
                        // A container the look gave is taken as it is.
                        if (unit.Container == Guid.Empty) LookUpContainer(unit, now);
                        Found(units, unit);
                        changed = true;
                    }
                    unit.Seen = true;
                }
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
                // and the unit, with the map its sessions read, goes off the
                // list. A device that comes back is a new unit.
                Close(unit);
                PeripheralLightMaps.Remove(PathOf(unit));
                units.RemoveAt(i);
                changed = true;
            }
            return changed;
        }

        /// <summary>Whether each of the model's siblings of a collection is in
        /// the walk, in the collection's container where Windows names both
        /// containers. One it cannot name yet is taken as present here, and
        /// the open finds out.</summary>
        private bool SiblingsPresent(HidLightingModel model, HidCollectionInfo collection)
        {
            var siblings = model.Siblings;
            if (siblings == null || siblings.Count == 0) return true;
            Guid own = SiblingContainer(collection.Path);
            foreach (var sibling in siblings)
            {
                bool found = false;
                foreach (var candidate in _walk)
                {
                    if (!sibling.Is(collection, candidate)) continue;
                    Guid theirs = own == Guid.Empty ? Guid.Empty : SiblingContainer(candidate.Path);
                    if (own != Guid.Empty && theirs != Guid.Empty && theirs != own) continue;
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>The container of a path of the walk, looked up once while
        /// it stays, or Guid.Empty while Windows cannot name it.</summary>
        private Guid SiblingContainer(string path)
        {
            if (_siblingContainers.TryGetValue(path, out var known)) return known;
            Guid container;
            try { container = _containerOf(path); }
            catch { container = Guid.Empty; }
            if (container != Guid.Empty) _siblingContainers[path] = container;
            return container;
        }

        /// <summary>Drops what the worker kept of paths that left the
        /// walk, so a device that comes back on one is asked again.</summary>
        private void ForgetGone(IReadOnlyList<HidCollectionInfo> present)
        {
            if (_siblingContainers.Count == 0 && _identified.Count == 0) return;
            var here = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var collection in present)
                if (collection?.Path != null) here.Add(collection.Path);
            Forget(_siblingContainers, here);
            Forget(_identified, here);
        }

        private static void Forget<T>(Dictionary<string, T> kept, HashSet<string> here)
        {
            List<string> gone = null;
            foreach (var path in kept.Keys)
                if (!here.Contains(path)) (gone ??= new List<string>()).Add(path);
            if (gone != null) foreach (var path in gone) kept.Remove(path);
        }

        /// <summary>Whether a collection a model with an identity check
        /// matched is one its family lights, asked once while it stays in
        /// the walk, on a handle with no access right. False, and asked again
        /// at the next walk, when that handle did not open.</summary>
        private bool Identified(HidLightingModel model, HidCollectionInfo collection)
        {
            if (_identified.TryGetValue(collection.Path, out bool known)) return known;
            IHidReportChannel io = null;
            try { io = _query(collection.Path); } catch { }
            if (io == null) return false;
            bool ours = false;
            try { ours = model.Identify(io); } catch { }
            try { io.Dispose(); } catch { }
            _identified[collection.Path] = ours;
            if (!ours)
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL HidLighting {collection.VendorId:X4}:{collection.ProductId:X4} is not a {model.Name} by its strings");
            return ours;
        }

        /// <summary>The listed unit a key names, compared without case, or
        /// null.</summary>
        private static Unit Find(List<Unit> units, string key)
        {
            foreach (var known in units)
                if (string.Equals(known.Path, key, StringComparison.OrdinalIgnoreCase)) return known;
            return null;
        }

        /// <summary>Lists a unit a scan found and publishes its map.</summary>
        private static void Found(List<Unit> units, Unit unit)
        {
            PublishMap(unit);
            units.Add(unit);
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL HidLighting found {unit.Label} ({unit.Model.Family?.Name}), {unit.Model.Map.Lights.Count} lights");
        }

        /// <summary>Publishes the unit's map, the one a session read from
        /// the device while it stays or else the model's, with the names Map Key
        /// Lights learned for it, as the SteelSeries worker swaps a unit's
        /// map once its SKU answers (SteelSeriesDirectBackend.SyncMap).</summary>
        private static void PublishMap(Unit unit)
        {
            var source = unit.SessionMap ?? unit.Model.Map;
            var map = LearnedKeyMaps.Apply(source) ?? source;
            if (ReferenceEquals(map, unit.Map)) return;
            unit.Map = map;
            PeripheralLightMaps.Publish(PathOf(unit), map);
        }

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
                list[i] = new DirectUnit(OutputFamily.HidLightingUnit, unit.Path, unit.Container, unit.VendorId, unit.ProductId)
                {
                    Name = unit.Model.Name,
                    Asleep = unit.Asleep,
                    LitBesideWindows = unit.Model.LitBesideWindows,
                    LiveThroughLampArray = unit.Model.LiveThroughLampArray,
                    Programs = unit.Model.Programs ?? Array.Empty<VendorProgram>(),
                    BuiltInKeyboard = unit.BuiltInKeyboard,
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

            if (unit.Asleep)
            {
                if (now >= unit.RetryAt)
                {
                    if (Answers(unit))
                    {
                        unit.Asleep = false;
                        changed = true;
                        PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} answers again");
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

            var path = PathOf(unit);
            int rgb = 0;
            PeripheralOutputs.Claimant ruler = default;
            bool wanted = links.ByPath.ContainsKey(path) && PeripheralOutputs.TryResolveColor(path, out rgb, out ruler);
            if (!wanted)
            {
                if (unit.Handle != null)
                {
                    if (Release(unit)) Close(unit);
                    else Fail(unit, now, "hand-back failed", ref changed, ref failed, ref due);
                    return;
                }
                // Colors an earlier run left in the device's memory go back
                // with no claim on it, once this run's first claims have had
                // the time to resolve, and once per answer of the unit's.
                // Not while a program of the model has the device: they
                // stay owed, in the model's keeping, until it has stopped.
                // A family whose devices are no HID collections owes
                // nothing this way (HidLightingModel.Repair takes a HID
                // channel).
                if (run.Stop || unit.Own != null || ProgramRuns(unit.Model.Programs)
                    || PeripheralOutputs.TakenByOpenRgb(path) || unit.Model.Owed?.Invoke() != true) return;
                long repairAt = Volatile.Read(ref _repairAt);
                if (now < repairAt)
                {
                    Soonest(ref due, repairAt);
                    return;
                }
                if (!Repair(unit))
                    Fail(unit, now, "did not take back the colors an earlier run left", ref changed, ref failed, ref due);
                return;
            }

            // A stopping worker opens nothing and paints nothing more. Its
            // finally hands back what it holds.
            if (run.Stop) return;
            // A program of the model started. The link pass publishes the
            // presence before the table it links from it, so the path is
            // still linked for the moment between, and nothing goes out
            // meanwhile. The next table lets the unit go (Release).
            if (ProgramRuns(unit.Model.Programs)) return;
            if (unit.Handle == null && !Open(unit))
            {
                Fail(unit, now, "did not open for read and write or did not answer", ref changed, ref failed, ref due);
                return;
            }
            if (!Paint(unit, rgb, ruler.Keys, ref due))
                Fail(unit, now, "write failed", ref changed, ref failed, ref due);
        }

        /// <summary>Whether an asleep unit can be opened for read and write
        /// again. Nothing is sent.</summary>
        private bool Answers(Unit unit)
        {
            var handle = OpenHandle(unit, out _, out var siblings);
            try { handle?.Dispose(); } catch { }
            Dispose(siblings);
            return handle != null;
        }

        /// <summary>Opens a unit's channel: a HID collection for read and
        /// write through the worker's opener, or the channel of a family
        /// whose devices are no HID collections through its model's
        /// (<see cref="HidLightingModel.Open"/>). Null when it did not open
        /// that way. <paramref name="io"/> is the HID channel, null for the
        /// other kind. <paramref name="siblings"/> are the channels of the
        /// model's siblings, open the same way, or null when it has none: a
        /// sibling that does not open leaves every channel closed.</summary>
        private IDisposable OpenHandle(Unit unit, out IHidReportChannel io, out IHidReportChannel[] siblings)
        {
            io = null;
            siblings = null;
            if (unit.Own != null)
            {
                IDisposable own = null;
                try { own = unit.Model.Open?.Invoke(unit.Path); } catch { }
                return own;
            }
            try { io = _open(unit.Path); } catch { }
            if (!Usable(unit.Model, io))
            {
                try { io?.Dispose(); } catch { }
                io = null;
                return null;
            }
            var wanted = unit.Model.Siblings;
            if (wanted == null || wanted.Count == 0) return io;
            siblings = OpenSiblings(unit, wanted);
            if (siblings != null) return io;
            try { io.Dispose(); } catch { }
            io = null;
            return null;
        }

        /// <summary>Whether a unit's channel opened the way its model needs:
        /// for read and write, or with no access right for a model whose
        /// collection Windows holds.</summary>
        private static bool Usable(HidLightingModel model, IHidReportChannel io)
            => io != null && (!io.FeatureOnly || model.HeldByWindows);

        /// <summary>Opens each sibling of a unit for read and write: the
        /// collection of the last walk the sibling names, in the unit's own
        /// container, so two devices of one kind each get their own. Null,
        /// with nothing left open, when one is not found or does not open
        /// that way.</summary>
        private IHidReportChannel[] OpenSiblings(Unit unit, IReadOnlyList<HidSibling> wanted)
        {
            if (unit.Info == null || unit.Container == Guid.Empty) return null;
            var opened = new IHidReportChannel[wanted.Count];
            for (int i = 0; i < wanted.Count; i++)
            {
                string path = null;
                foreach (var candidate in _walk)
                {
                    if (!wanted[i].Is(unit.Info, candidate) || SiblingContainer(candidate.Path) != unit.Container) continue;
                    path = candidate.Path;
                    break;
                }
                IHidReportChannel channel = null;
                try { channel = path == null ? null : _open(path); } catch { }
                if (channel == null || channel.FeatureOnly)
                {
                    try { channel?.Dispose(); } catch { }
                    Dispose(opened);
                    return null;
                }
                opened[i] = channel;
            }
            return opened;
        }

        private static void Dispose(IHidReportChannel[] channels)
        {
            if (channels == null) return;
            foreach (var channel in channels)
            {
                try { channel?.Dispose(); } catch { }
            }
        }

        /// <summary>Opens a unit no claim resolves on and hands back what an
        /// earlier run left in it (<see cref="HidLightingModel.Repair"/>).
        /// True when nothing is owed any more.</summary>
        private bool Repair(Unit unit)
        {
            IHidReportChannel io = null;
            try { io = _open(unit.Path); } catch { }
            if (!Usable(unit.Model, io))
            {
                try { io?.Dispose(); } catch { }
                return false;
            }
            bool settled = false;
            try { settled = unit.Model.Repair?.Invoke(io, _wait) ?? true; } catch { }
            try { io.Dispose(); } catch { }
            if (settled)
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} colors an earlier run left handed back");
            return settled;
        }

        /// <summary>Opens the unit's collection for read and write and
        /// starts its session. False when it did not open that way or the
        /// model's first reads went unanswered.</summary>
        private bool Open(Unit unit)
        {
            var handle = OpenHandle(unit, out var io, out var siblings);
            if (handle == null) return false;
            HidLightingSession session = null;
            try
            {
                session = unit.Own != null ? unit.Model.BeginOwn?.Invoke(handle, _wait)
                    : siblings != null ? unit.Model.BeginWith?.Invoke(io, siblings, _wait)
                    : unit.Model.Begin?.Invoke(io, _wait);
            }
            catch { }
            if (session == null)
            {
                try { handle.Dispose(); } catch { }
                Dispose(siblings);
                return false;
            }
            unit.Handle = handle;
            unit.Io = io;
            unit.SiblingIo = siblings;
            unit.Session = session;
            unit.Shown = null;
            unit.ShownMap = null;
            unit.Pending = null;
            unit.PendingMap = null;
            unit.LaterAt = 0;
            unit.CheckAt = _ticks() + unit.Model.RecheckMs;
            unit.KeptAt = _ticks();
            // A session that read the device's own map lights and publishes
            // by it from now on, for as long as the device stays
            // (HidLightingSession.Map). A later session that reads none
            // leaves the map an earlier one read.
            LightMap read = null;
            try { read = session.Map; } catch { }
            if (read != null)
            {
                unit.SessionMap = read;
                PublishMap(unit);
            }
            // A session that finds colors of an earlier run's names their
            // undoing from its first moment.
            Track(unit);
            return true;
        }

        /// <summary>One pass over an open, wanted unit, with the claim's
        /// color and key frame as the pass resolved them. False when a
        /// write or a read failed.</summary>
        private bool Paint(Unit unit, int rgb, KeyFrame keys, ref long due)
        {
            // The clock after the claim resolved: a picture is never taken
            // to have held from before it was seen.
            long now = _ticks();
            var model = unit.Model;
            if (model.RecheckMs > 0)
            {
                if (now >= unit.CheckAt)
                {
                    unit.CheckAt = now + model.RecheckMs;
                    bool answered;
                    try { answered = unit.Session.Recheck(unit.Io, _wait); }
                    catch { answered = false; }
                    Track(unit);
                    if (!answered) return false;
                }
                Soonest(ref due, unit.CheckAt);
            }
            var map = unit.Map ?? model.Map;
            var colors = LightRenderer.Render(map, rgb, keys);
            if (unit.Shown != null && ReferenceEquals(unit.ShownMap, map) && LightRenderer.Same(colors, unit.Shown))
            {
                // The device shows this picture already and keeps it.
                unit.Pending = null;
                unit.PendingMap = null;
                return KeepAlive(unit, ref due);
            }
            if (unit.Pending == null || !ReferenceEquals(unit.PendingMap, map) || !LightRenderer.Same(colors, unit.Pending))
            {
                unit.Pending = colors;
                unit.PendingMap = map;
                unit.PendingAt = now;
            }
            long at = unit.PendingAt + model.HoldMs;
            if (unit.Shown != null) at = Math.Max(at, unit.SentAt + model.RepaintMs);
            at = Math.Max(at, unit.LaterAt);
            if (now < at)
            {
                // The device still shows the picture before, and is kept
                // showing it meanwhile.
                Soonest(ref due, at);
                return KeepAlive(unit, ref due);
            }

            var session = unit.Session;
            HidLightingPaint result;
            try { result = session.Paint(unit.Io, _wait, map.Lights, colors); }
            catch { result = HidLightingPaint.Failed; }
            Track(unit);
            if (result == HidLightingPaint.Failed) return false;
            if (result == HidLightingPaint.Later)
            {
                unit.LaterAt = _ticks() + Math.Max(1, session.LaterMs);
                Soonest(ref due, unit.LaterAt);
                return true;
            }
            bool first = unit.Shown == null;
            unit.Shown = colors;
            unit.ShownMap = map;
            unit.SentAt = _ticks();
            unit.Pending = null;
            unit.PendingMap = null;
            unit.LaterAt = 0;
            if (first)
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} lit, {colors.Length} lights");
            return true;
        }

        /// <summary>Sends the session's keep-alive once the model's interval
        /// has passed since the one before and its idle time since the last
        /// picture (<see cref="HidLightingModel.KeepAliveMs"/>), while the
        /// session shows a picture, and names when the next one is due.
        /// The picture the device shows does not change. False when a write
        /// or a read failed.</summary>
        private bool KeepAlive(Unit unit, ref long due)
        {
            var model = unit.Model;
            if (model.KeepAliveMs <= 0 || unit.Shown == null) return true;
            long at = Math.Max(unit.KeptAt + model.KeepAliveMs, unit.SentAt + model.KeepAliveIdleMs);
            if (_ticks() < at)
            {
                Soonest(ref due, at);
                return true;
            }
            bool sent;
            try { sent = unit.Session.KeepAlive(unit.Io, _wait, unit.ShownMap.Lights, unit.Shown); }
            catch { sent = false; }
            Track(unit);
            if (!sent) return false;
            unit.KeptAt = _ticks();
            Soonest(ref due, Math.Max(unit.KeptAt + model.KeepAliveMs, unit.SentAt + model.KeepAliveIdleMs));
            return true;
        }

        /// <summary>The unit's session returns the device to its own
        /// lighting. True when it did, or had nothing to undo.</summary>
        private bool Release(Unit unit)
        {
            if (unit.Session == null) return true;
            // OpenRGB holds the device and the linker closed the path
            // (#557): the worker lets go without a frame, by the rule below.
            var program = PeripheralOutputs.TakenByOpenRgb(PathOf(unit))
                ? VendorPrograms.OpenRgb
                : ProgramRunning(unit.Model.Programs);
            if (program != null)
            {
                // A program of the model started, and the linker takes the
                // unit's path away from the pass that sees it. The program
                // lights the device from here on, and a hand-back that
                // landed after its own writes would undo them, so the
                // worker lets go without a frame, as the Razer worker does
                // while Synapse runs (RazerDirectBackend.Pump and LetGo)
                // and the SteelSeries worker once GG has the path
                // (SteelSeriesDirectBackend.Release). What the session
                // wrote stays owed in the model's keeping.
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL HidLighting {unit.Label} left to {program.Name}, which started");
                return true;
            }
            bool released = false;
            try { released = unit.Session.HandBack(unit.Io, _wait); } catch { }
            Track(unit);
            if (released) PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} lighting handed back");
            return released;
        }

        /// <summary>The first of <paramref name="programs"/> that runs now,
        /// by the presence the link pass last published, or null.</summary>
        private static VendorProgram ProgramRunning(IReadOnlyList<VendorProgram> programs)
            => programs == null || programs.Count == 0 ? null : PeripheralOutputs.Presence.Programs.FirstOf(programs);

        private static bool ProgramRuns(IReadOnlyList<VendorProgram> programs) => ProgramRunning(programs) != null;

        /// <summary>A write, an open or a read failed. The channel closes and
        /// the unit is listed asleep, never taken off the list.</summary>
        private void Fail(Unit unit, long now, string why, ref bool changed, ref bool failed, ref long due)
        {
            Close(unit);
            unit.Asleep = true;
            unit.RetryAt = now + RetryMs;
            changed = true;
            failed = true;
            Soonest(ref due, unit.RetryAt);
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} {why}, listed asleep");
        }

        /// <summary>Closes the unit's channel and its siblings' and drops its
        /// session. The map a session read from the device stays
        /// published.</summary>
        private void Close(Unit unit)
        {
            Unrecord(unit);
            var handle = unit.Handle;
            var siblings = unit.SiblingIo;
            unit.Handle = null;
            unit.Io = null;
            unit.SiblingIo = null;
            unit.Session = null;
            unit.Shown = null;
            unit.ShownMap = null;
            unit.Pending = null;
            unit.PendingMap = null;
            unit.LaterAt = 0;
            try { handle?.Dispose(); } catch { }
            Dispose(siblings);
        }

        // ─────────────────────────────────────────────
        //  The crash path's record
        // ─────────────────────────────────────────────

        /// <summary>Brings the unit's entry on the record in line with the
        /// frames its session names now.</summary>
        private void Track(Unit unit)
        {
            var frames = unit.Session?.CrashFrames ?? Array.Empty<byte[]>();
            if (unit.Recorded ? ReferenceEquals(frames, unit.RecordedFrames) : frames.Length == 0) return;
            Unrecord(unit);
            if (frames.Length == 0) return;
            var current = Volatile.Read(ref _recorded);
            var next = new CrashRecord[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = new CrashRecord(unit.Path, frames, unit.Session.Feature, unit.Model.Programs,
                unit.Own != null ? unit.Model.WriteOnce : null);
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
            var next = new List<CrashRecord>(current.Length);
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
