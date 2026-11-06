using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// handle with no access right (<see cref="HidLightingModel.Identify"/>).
    /// A unit stays listed for as long as its collection is in the last
    /// list, and its model's light map is
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
    /// (<see cref="HidLightingModel.Siblings"/>). Windows opens a collection
    /// <see cref="HidCollectionInfo.WindowsHolds"/> names with no access right
    /// only, for everyone, and takes feature reports on that handle, so a model
    /// whose lighting lives on one keeps it
    /// (<see cref="HidLightingModel.HeldByWindows"/>). Any other model writes
    /// nothing there and its unit says Windows holds it
    /// (<see cref="DirectUnit.WindowsHolds"/>). On any other collection a handle
    /// with no access right means another program has the collection: nothing
    /// is written, the unit says so (<see cref="DirectUnit.HeldByProgram"/>),
    /// and it is tried every <see cref="RetryMs"/>, so the device lights soon
    /// after that program closes.
    /// A unit no claim resolves on is opened only while its model owes it
    /// colors an earlier run left in the device's memory
    /// (<see cref="HidLightingModel.Owed"/>), to hand those back, from
    /// <see cref="RepairGraceMs"/> after the worker starts.</para>
    ///
    /// <para>Paint. The claim's color and key frame are rendered into one
    /// color per light (<see cref="LightRenderer.Render"/>). A picture the
    /// device already shows is not sent again. A changed one goes out once
    /// it has stayed the same for the hold time and the repaint interval
    /// has passed since the picture before, the model's or, where the
    /// session tells its device apart, the session's, and a picture
    /// the session cannot send yet is tried again after the time it names.
    /// A model with a hold stores each picture, so a color that never holds,
    /// a rainbow or a light that follows the sound, goes out not at all and
    /// the row's line says so. A color whose mode keeps changing it goes out
    /// not at all either, where it pauses long enough to hold, since a picture
    /// takes a few hundred milliseconds to reach the lights (#559). Its pictures
    /// of any other color after the first are also held
    /// to a burst and a rate (<see cref="StoredWriteBurst"/>,
    /// <see cref="StoredWriteRefillMs"/>), whatever the hold let through.
    /// The session reads the device's state
    /// again on the model's recheck interval, and the picture goes out once
    /// more when the session says the device showed something else since
    /// (<see cref="HidLightingSession.RepaintDue"/>), or when Windows resumes
    /// from sleep (<see cref="NoteResume"/>). A device that drops the host's colors when the host goes
    /// quiet gets the session's keep-alive on the model's interval while a
    /// picture shows, with no change in the picture
    /// (<see cref="HidLightingModel.KeepAliveMs"/>), also from inside the
    /// waits of another unit's session. One thread serves every unit, so a
    /// session that waits does hold up other units' pictures, for the length
    /// of its wait. The crash path's record
    /// holds the session's own frames for undoing what it wrote, kept current after every call into the
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
    /// opening it. The frames of the session it lost are owed
    /// (<see cref="IDirectUnitBackend"/>, Owe): they stay on the crash record
    /// and go out on a handle of their own once the unit answers, claim or no
    /// claim, ahead of any new session, unless a program or OpenRGB has the
    /// device by then. A model with a backup of its own keeps what is owed
    /// itself (<see cref="HidLightingModel.Owed"/>). A handle that opens and
    /// whose session is refused says the device is there and takes no
    /// lighting from PadForge, so it asks for no walk, the unit says so
    /// (<see cref="DirectUnit.Refused"/>), and each try waits twice as long as
    /// the one before, up to <see cref="RefusedRetryMaxMs"/>. A collection Windows
    /// holds says so the same way, and is asked each minute. A handle found
    /// dead at a walk, a device unplugged and plugged back in between two
    /// walks, fails the same way as a write, with the walk already made. A
    /// fault in one unit or one family costs that unit or that collection,
    /// and never the worker.</para>
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

        /// <summary>How long a unit that just answered again waits before the
        /// colors it holds go back with no claim on it. The link pass took its
        /// path away while it slept, and this is time for that pass
        /// (<see cref="PeripheralOutputHost.LinkMs"/>) to see it awake and link
        /// it again, so a device a claim still lights is not handed back and
        /// lit again a moment later.</summary>
        internal const int WakeRepairGraceMs = 2 * PeripheralOutputHost.LinkMs;

        /// <summary>The longest a unit whose session was refused waits before
        /// the next try. A device that really is asleep lights within this
        /// long of waking.</summary>
        internal const int RefusedRetryMaxMs = 60_000;

        /// <summary>How many pictures a stored-picture model takes in a row
        /// after its first, as a user drags a color into place, before they
        /// go out only as <see cref="StoredWriteRefillMs"/> allows. No
        /// reference states what memory these controllers store to, or how
        /// many writes it lasts, so the bound is PadForge's. The newest
        /// picture is the one that goes out when one comes due.</summary>
        internal const int StoredWriteBurst = 30;

        /// <summary>How often a stored-picture model earns one more picture:
        /// 360 an hour at the most, where a color a game drives could
        /// otherwise reach 7,200.</summary>
        internal const int StoredWriteRefillMs = 10_000;

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

            /// <summary>What the unit's worker keeps of its walks.</summary>
            public WalkState Walk;

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

            /// <summary>The name a look gave the unit, and the name a session
            /// gave it from what the device answered, kept while the device
            /// stays. Null: the look's, then the model's.</summary>
            public string LookName;
            public string AnsweredName;

            public string Name => AnsweredName ?? LookName ?? Model?.Name;

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

            /// <summary>When the unit last answered again, plus
            /// <see cref="WakeRepairGraceMs"/>.</summary>
            public long RepairAt;

            /// <summary>The handle opened and the session was refused, and how
            /// many times in a row.</summary>
            public bool Refused;

            /// <summary>The collection opened with no access right for a model
            /// that needs more, and it is one
            /// <see cref="HidCollectionInfo.WindowsHolds"/> names, so Windows takes
            /// none of the model's reports. A kind of refusal.</summary>
            public bool WindowsHolds;

            /// <summary>The same handle on any other collection: another program
            /// has it open.</summary>
            public bool HeldByProgram;
            public int Refusals;

            /// <summary>The frames of a session that ended with no hand-back,
            /// which go out once the unit answers, and whether they are
            /// feature reports. Null when nothing is owed.</summary>
            public byte[][] OwedFrames;
            public bool OwedFeature;
            public string OwedPath;

            /// <summary>A keep-alive sent from inside another unit's wait
            /// failed. The unit's own turn fails it, unless a close dropped the
            /// session it failed on first.</summary>
            public bool KeepAliveFailed;

            /// <summary>The device stores each picture, by its session where
            /// the session tells its device apart, else by its model.</summary>
            public bool StoresPictures;

            /// <summary>The pictures a stored-picture model may still send at
            /// once, and when they were last counted. Kept across closes and
            /// reopens, since the device's memory is.</summary>
            public int StoredWrites = StoredWriteBurst;
            public long StoredWritesAt;

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

            /// <summary>The strip counts the session was last given
            /// (<see cref="HidLightingSession.SizeStrips"/>), and whether it
            /// has yet to be given any on a device that lists strips. A new
            /// session starts with none, while the map an earlier one sized
            /// stays, so a claim with no counts has to reach it too.</summary>
            public IReadOnlyList<int> Sized;
            public bool SizeDue;

            /// <summary>When a picture the session could not send yet is
            /// tried again.</summary>
            public long LaterAt;

            /// <summary>When the session reads the device's state
            /// again.</summary>
            public long CheckAt;

            /// <summary>When the session's last keep-alive went out, or when
            /// the session began.</summary>
            public long KeptAt;

            /// <summary>How long the unit's last picture took, waits included, or
            /// -1 before its first.</summary>
            public long PaintTookMs = -1;

            public string Label => $"{Name ?? "unit"} {VendorId:X4}:{ProductId:X4}";
        }

        /// <summary>One unit's entry on the crash path's record: its key, the
        /// frames that undo what its session wrote, whether they are feature
        /// reports, its model's programs, for a unit an own look found, the
        /// model's one-shot writer (<see cref="HidLightingModel.WriteOnce"/>),
        /// and the collection the frames go to where it is not the key's.</summary>
        internal sealed record CrashRecord(string Path, byte[][] Frames, bool Feature,
            IReadOnlyList<VendorProgram> Programs, Func<string, byte[], int, bool> Write, string WritePath = null);

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
        private readonly Func<long> _elapsedMs;
        private readonly int _idleTickMs;
        private readonly int _predecessorJoinMs;
        private readonly int _stopJoinMs;
        private readonly bool _powerEvents;
        private readonly Action<int> _sessionWait;
        private readonly AutoResetEvent _wake = new(false);

        /// <summary>The worker's units while it serves them, and the unit whose
        /// session has the worker now, for the wait a session is handed. Each
        /// worker thread keeps its own: a worker a timed-out Stop left behind
        /// waits on its own units, and its end clears its own, while the next
        /// one runs.</summary>
        [ThreadStatic] private static List<Unit> t_served;
        [ThreadStatic] private static WorkerRun t_run;
        [ThreadStatic] private static Unit t_serving;

        /// <summary>0 on a unit's turn, 1 in the pass beside its wait, 2 in the
        /// keep-alives beside a wait of that pass, which waits plainly.</summary>
        [ThreadStatic] private static int t_besideDepth;

        /// <summary>The unit the pass beside a wait is serving.</summary>
        [ThreadStatic] private static Unit t_servedBeside;

        [ThreadStatic] private static long t_picturesBesideAt;

        /// <summary>How often a long wait lets the other units' pictures out. Once
        /// per display frame, since a session can take many short waits, and each
        /// look renders every unit's picture.</summary>
        internal const int PicturesBesideMs = 16;

        /// <summary>Whether the worker this runs on is stopping. Its session's reads
        /// are on the stop's clock then.</summary>
        internal static bool WorkerStopping => t_run is { Stop: true };

        /// <summary>The high-resolution counter a wait goes by, as the HID++ worker's
        /// clock does: the worker's own clock moves in steps of 15.6 ms, longer than the
        /// 5 ms a session can wait between two reports.</summary>
        private static readonly Stopwatch s_elapsed = Stopwatch.StartNew();

        private int MsSince(long at) => (int)Math.Min(int.MaxValue, _elapsedMs() - at);

        /// <summary>What a session names for its crash path, carried to the next
        /// run when a worker running late has not handed it back.</summary>
        private sealed record LateFrames(byte[][] Frames, bool Feature, string Path);

        private int _resumed;

        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;
        private IReadOnlyList<HidCollectionInfo> _present;

        /// <summary>What one worker keeps of the walks it scanned. Its own
        /// thread's alone: a worker a timed-out Stop left behind keeps its
        /// own while the next one runs.</summary>
        private sealed class WalkState
        {
            /// <summary>The walk last scanned, where a unit's siblings are
            /// found.</summary>
            public IReadOnlyList<HidCollectionInfo> Present = Array.Empty<HidCollectionInfo>();

            /// <summary>The containers of the walk's paths that siblings were
            /// looked for among, kept while the path stays in the walk. Only
            /// a container Windows named is kept.</summary>
            public readonly Dictionary<string, Guid> Containers = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>What a model's identity check said of each path of
            /// the walk (<see cref="HidLightingModel.Identify"/>), kept while
            /// the path stays. A path whose strings could not be read has no
            /// entry and is asked again at the next walk.</summary>
            public readonly Dictionary<string, bool> Identified = new(StringComparer.OrdinalIgnoreCase);
        }
        private Thread _thread;
        private Thread _worker;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;
        private int _waitMs;
        private long _repairAt;

        /// <summary>The family's workers over the engine's restarts.</summary>
        private static readonly WorkerSuccession s_workers = new();

        public HidLightingBackend()
            : this(null, null, null, null, null, null, null, IdleTickMs) => _powerEvents = true;

        /// <summary>Test seam: the families, the channel opener, the
        /// container lookup, the clock, the crash path's two one-shot
        /// writers, the wait a session takes between an exchange and the
        /// next, the idle tick, the wait for a predecessor worker, Stop's
        /// wait, the opener with no access right the scan reads strings
        /// through, and the clock a wait goes by, the given clock when it is
        /// left out. The openers and the one-shot writers are the worker's
        /// only ways to a device, and their defaults open nothing in the
        /// test host (<see cref="RealHidAccess"/>).</summary>
        internal HidLightingBackend(IReadOnlyList<IHidLightingFamily> families, Func<string, IHidReportChannel> open,
            Func<string, Guid> containerOf, Func<long> ticks, Func<string, byte[], int, bool> setFeatureOnce,
            Func<string, byte[], int, bool> writeOutputOnce, Action<int> wait, int idleTickMs,
            int predecessorJoinMs = PredecessorJoinMs, int stopJoinMs = StopJoinMs,
            Func<string, IHidReportChannel> query = null, Func<long> elapsedMs = null)
        {
            _elapsedMs = elapsedMs ?? ticks ?? (static () => s_elapsed.ElapsedMilliseconds);
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
            _sessionWait = SessionWait;
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
            if (_powerEvents) Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode;
        }

        public void Stop()
        {
            PeripheralOutputs.LightingChanged -= Wake;
            PeripheralOutputs.LinksChanged -= Wake;
            LearnedKeyMaps.Changed -= OnLearned;
            if (_powerEvents) Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerMode;
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
                    try { WriteOnce(record.WritePath ?? record.Path, frame, record.Feature, record.Write, (int)Math.Min(left, OneShotMs)); } catch { }
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

        private void OnPowerMode(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) NoteResume();
        }

        /// <summary>Windows resumed from sleep: every open unit gets its
        /// picture once more, since a device whose firmware does not keep a
        /// written state through sleep shows its own lighting again while its
        /// collection never left the list (Lenovo Legion Toolkit
        /// PowerStateListener.cs:145-146, SteelSeriesDirectBackend.NoteResume).</summary>
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
            // (SteelSeriesDirectBackend.Worker).
            s_workers.Enter(run, _predecessorJoinMs, "HidLighting");
            var units = new List<Unit>();
            var walk = new WalkState();
            int learned = LearnedKeyMaps.Version;
            try
            {
                IReadOnlyList<HidCollectionInfo> scanned = null;
                t_served = units;
                t_run = run;
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
                        changed = Scan(units, present, now, walk, ref due) || scanned == null;
                        scanned = present;
                    }

                    if (Interlocked.Exchange(ref _resumed, 0) != 0)
                    {
                        foreach (var unit in units)
                        {
                            unit.Shown = null;
                            unit.ShownMap = null;
                        }
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
                        t_serving = unit;
                        try { Serve(run, unit, links, _ticks(), ref changed, ref failed, ref due); }
                        catch (Exception ex)
                        {
                            // One unit's fault costs that unit, listed asleep
                            // and tried again, and every other family stays lit.
                            Fail(unit, "fault: " + ex.GetType().Name, ref changed, ref failed, ref due);
                        }
                    }
                    t_serving = null;

                    // A stopped worker publishes nothing more: its finally takes
                    // the list down, or the next run has it by then.
                    if (changed && !run.Stop) Publish(units);
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
                t_served = null;
                t_serving = null;
                // The hand-backs below read the run from here, with nothing else
                // of the turn's state.
                t_run = run;
                foreach (var unit in units)
                {
                    // A later run that claimed this collection may have it open
                    // by now, and a hand-back would land on its picture.
                    bool reach = s_workers.MayReach(unit.Path, run);
                    if (unit.Handle == null)
                    {
                        // What a lost session still owes goes out once more,
                        // on a handle of its own.
                        if (reach)
                        {
                            try { PayOwed(unit); } catch { }
                        }
                        else Unrecord(unit);
                        continue;
                    }
                    bool released = false;
                    if (reach)
                    {
                        try { released = Release(unit); } catch { }
                    }
                    var frames = released ? null : unit.Session?.CrashFrames;
                    bool feature = unit.Session?.Feature ?? true;
                    string target = unit.Session?.CrashPath ?? unit.Path;
                    var own = unit.Own != null ? unit.Model.WriteOnce : null;
                    // The maps are taken down below, and a successor's are
                    // its own.
                    Close(unit);
                    // A hand-back that did not go out on the worker's
                    // channel goes out through a report of its own, as on
                    // the crash path, where the session names frames for it.
                    // A unit whose program runs is released with nothing to send
                    // (Release). What a model's own backup holds stays in the
                    // model's keeping for the next run.
                    if (frames == null || !reach) continue;
                    bool sent = true;
                    foreach (var frame in frames)
                    {
                        try { sent &= WriteOnce(target, frame, feature, own, OneShotMs); }
                        catch { sent = false; }
                    }
                    if (sent) s_workers.HandedBack(unit.Path, run);
                }
                if (s_workers.Holds(run))
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
        private bool Scan(List<Unit> units, IReadOnlyList<HidCollectionInfo> present, long now, WalkState walk, ref long due)
        {
            bool changed = false;
            walk.Present = present;
            ForgetGone(walk, present);
            foreach (var collection in present)
            {
                // A fault in a family or a model costs that collection, and
                // every other family stays lit.
                try
                {
                    var model = HidLightingFamilies.Match(collection, _families);
                    if (model == null) continue;
                    // A device that talks through more collections is one OpenRGB
                    // makes only once it found each of them
                    // (RoccatControllerDetect.cpp:155-201).
                    if (!SiblingsPresent(walk, model, collection)) continue;
                    // A plugin that tries the collections of its list takes the first the
                    // device has, and the others stay out of its way.
                    if (Outranked(walk, model, collection)) continue;
                    if (model.Identify != null && !Identified(walk, model, collection)) continue;
                    var unit = Find(units, collection.Path);
                    if (unit == null)
                    {
                        unit = new Unit
                        {
                            Path = collection.Path, VendorId = collection.VendorId, ProductId = collection.ProductId,
                            Model = model, Walk = walk,
                        };
                        LookUpContainer(unit, now);
                        Found(units, unit);
                        changed = true;
                    }
                    unit.Info = collection;
                    unit.Seen = true;
                }
                catch (Exception ex)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL HidLighting fault at {collection?.VendorId:X4}:{collection?.ProductId:X4}: {ex.GetType().Name}");
                }
            }

            // The families whose devices are no HID collections look on
            // their own, at the same cue (IDirectUnitBackend.Rescan).
            foreach (var family in _families)
            {
                if (family is not IOwnLookFamily own) continue;
                IReadOnlyList<OwnDevice> found;
                try { found = own.Look(present); }
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
                            Container = device.Container, Walk = walk, LookName = device.Name,
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
                    // A device unplugged and plugged back in between two walks
                    // is on the same path with its handle dead for good, and a
                    // picture that does not change would never find out. The
                    // walk is what brought this here, so it asks for none.
                    if (unit.Handle is IConnectedChannel { StillConnected: false })
                    {
                        bool ignored = false;
                        Fail(unit, "handle went away", ref changed, ref ignored, ref due);
                        unit.RetryAt = _ticks();
                    }
                    continue;
                }
                // The device is gone, so there is nothing to hand back to,
                // and the unit, with the map its sessions read, goes off the
                // list. A device that comes back is a new unit.
                unit.OwedFrames = null;
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
        private bool SiblingsPresent(WalkState walk, HidLightingModel model, HidCollectionInfo collection)
        {
            var siblings = model.Siblings;
            if (siblings == null || siblings.Count == 0) return true;
            Guid own = SiblingContainer(walk, collection.Path);
            foreach (var sibling in siblings)
            {
                bool found = false;
                foreach (var candidate in walk.Present)
                {
                    if (!sibling.Is(collection, candidate)) continue;
                    Guid theirs = own == Guid.Empty ? Guid.Empty : SiblingContainer(walk, candidate.Path);
                    if (own != Guid.Empty && theirs != Guid.Empty && theirs != own) continue;
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>Whether the model names another collection of the walk as the unit in
        /// place of this one, in the collection's container. One whose container Windows
        /// cannot name yet does not count against a collection whose container it can: nothing
        /// says the two are one device, and this one may be lit. With this one's unnamed, the
        /// model's word stands, and the next walk asks again.</summary>
        private bool Outranked(WalkState walk, HidLightingModel model, HidCollectionInfo collection)
        {
            if (model.Outranks == null) return false;
            Guid own = SiblingContainer(walk, collection.Path);
            foreach (var candidate in walk.Present)
            {
                if (candidate?.Path == null || string.Equals(candidate.Path, collection.Path, StringComparison.OrdinalIgnoreCase)
                    || !model.Outranks(candidate, collection))
                    continue;
                if (own != Guid.Empty && SiblingContainer(walk, candidate.Path) != own) continue;
                return true;
            }
            return false;
        }

        /// <summary>The container of a path of the walk, looked up once while
        /// it stays, or Guid.Empty while Windows cannot name it.</summary>
        private Guid SiblingContainer(WalkState walk, string path)
        {
            if (walk.Containers.TryGetValue(path, out var known)) return known;
            Guid container;
            try { container = _containerOf(path); }
            catch { container = Guid.Empty; }
            if (container != Guid.Empty) walk.Containers[path] = container;
            return container;
        }

        /// <summary>Drops what the worker kept of paths that left the
        /// walk, so a device that comes back on one is asked again.</summary>
        private static void ForgetGone(WalkState walk, IReadOnlyList<HidCollectionInfo> present)
        {
            if (walk.Containers.Count == 0 && walk.Identified.Count == 0) return;
            var here = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var collection in present)
                if (collection?.Path != null) here.Add(collection.Path);
            Forget(walk.Containers, here);
            Forget(walk.Identified, here);
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
        /// at the next walk, when that handle did not open or a check that
        /// said no had no string to go by.</summary>
        private bool Identified(WalkState walk, HidLightingModel model, HidCollectionInfo collection)
        {
            if (walk.Identified.TryGetValue(collection.Path, out bool known)) return known;
            IHidReportChannel io = null;
            try { io = _query(collection.Path); } catch { }
            if (io == null) return false;
            bool ours = false, read = false;
            try
            {
                ours = model.Identify(io);
                read = ours || io.ManufacturerString != null || io.ProductString != null || io.SerialNumber != null;
            }
            catch { }
            try { io.Dispose(); } catch { }
            if (!read) return false;
            walk.Identified[collection.Path] = ours;
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
            unit.StoresPictures = unit.Model.HoldMs > 0;
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
                    Name = unit.Name,
                    Asleep = unit.Asleep,
                    Refused = unit.Refused,
                    WindowsHolds = unit.WindowsHolds,
                    HeldByProgram = unit.HeldByProgram,
                    StoresPictures = unit.StoresPictures,
                    ReplacesStoredEffects = unit.Model.ReplacesStoredEffects,
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
            // What a program stores on the device is its own lighting from here on,
            // whether or not the device answers while the program has it, so nothing
            // owed goes out once the program has stopped.
            var path = PathOf(unit);
            if (ProgramRuns(unit.Model.Programs) || PeripheralOutputs.TakenByOpenRgb(path))
            {
                s_workers.LeftToAProgram(unit.Path, run);
                if (unit.OwedFrames != null) EndOwed(unit);
            }

            if (unit.Container == Guid.Empty)
            {
                if (now >= unit.ContainerRetryAt && LookUpContainer(unit, now)) changed = true;
                if (unit.Container == Guid.Empty) Soonest(ref due, unit.ContainerRetryAt);
            }

            if (unit.Asleep)
            {
                if (now >= unit.RetryAt)
                {
                    bool windows = unit.WindowsHolds, program = unit.HeldByProgram;
                    if (Answers(unit) && PayOwed(unit))
                    {
                        unit.Asleep = false;
                        unit.RepairAt = _ticks() + WakeRepairGraceMs;
                        changed = true;
                        PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} answers again");
                    }
                    else
                    {
                        // A collection Windows holds does not stop being held
                        // on its own, so it is asked as rarely as a refusal.
                        unit.RetryAt = _ticks() + (unit.WindowsHolds ? RefusedRetryMaxMs : RetryMs);
                        // The row says who holds the collection, which a try
                        // can find out for a unit asleep for another reason.
                        if (unit.WindowsHolds != windows || unit.HeldByProgram != program) changed = true;
                    }
                }
                if (unit.Asleep)
                {
                    Soonest(ref due, unit.RetryAt);
                    return;
                }
            }

            if (unit.KeepAliveFailed)
            {
                unit.KeepAliveFailed = false;
                Fail(unit, "write failed", ref changed, ref failed, ref due);
                return;
            }

            int rgb = 0;
            PeripheralOutputs.Claimant ruler = default;
            bool wanted = links.ByPath.ContainsKey(path) && PeripheralOutputs.TryResolveColor(path, out rgb, out ruler);
            if (!wanted)
            {
                if (unit.Handle != null)
                {
                    if (Release(unit)) Close(unit);
                    else Fail(unit, "hand-back failed", ref changed, ref failed, ref due);
                    return;
                }
                // Colors an earlier run left in the device's memory go back
                // with no claim on it, once this run's first claims have had
                // the time to resolve and a unit that just answered again has
                // had the time to be linked again, and once per answer of the
                // unit's. Not while a program of the model has the device: they
                // stay owed, in the model's keeping, until it has stopped.
                // A family whose devices are no HID collections does this
                // through a channel of its own, where it has the call for
                // it (HidLightingModel.RepairOwn).
                if (run.Stop || (unit.Own != null && unit.Model.RepairOwn == null) || ProgramRuns(unit.Model.Programs)
                    || PeripheralOutputs.TakenByOpenRgb(path) || unit.Model.Owed?.Invoke(unit.Path) != true) return;
                long repairAt = Math.Max(Volatile.Read(ref _repairAt), unit.RepairAt);
                if (now < repairAt)
                {
                    Soonest(ref due, repairAt);
                    return;
                }
                if (!Repair(unit))
                    Fail(unit, "did not take back the colors an earlier run left", ref changed, ref failed, ref due);
                return;
            }

            // A stopping worker opens nothing and paints nothing more. Its
            // finally hands back what it holds.
            if (run.Stop) return;
            // A program of the model started. The link pass publishes the
            // presence before the table it links from it, so the path is
            // still linked for the moment between, and nothing goes out
            // meanwhile. The next table lets the unit go (Release). The same
            // for OpenRGB taking the path during this pass, which read its
            // table before another unit's wait: the next pass lets the unit go.
            if (ProgramRuns(unit.Model.Programs) || PeripheralOutputs.TakenByOpenRgb(path)) return;
            if (unit.Handle == null)
            {
                // A worker running late left its picture on the device, so what it
                // would hand back goes first, and the session reads the device's
                // own lighting.
                if (unit.OwedFrames == null && s_workers.TakeHandBack(unit.Path, run, out LateFrames late))
                {
                    unit.OwedFrames = late.Frames;
                    unit.OwedFeature = late.Feature;
                    unit.OwedPath = late.Path;
                    RecordOwed(unit);
                }
                if (unit.OwedFrames != null && !PayOwed(unit))
                {
                    Fail(unit, "did not take back the colors a worker running late left", ref changed, ref failed, ref due);
                    return;
                }
                if (!Open(unit, out bool refused))
                {
                    if (refused) Refuse(unit, ref changed, ref due);
                    else if (unit.HeldByProgram)
                    {
                        // Nothing says the device left, so this asks for no walk.
                        bool noWalk = false;
                        Fail(unit, "is open in another program", ref changed, ref noWalk, ref due);
                    }
                    else Fail(unit, "did not open for read and write", ref changed, ref failed, ref due);
                    return;
                }
                // A session's Begin can read the device. A stop that came during
                // it paints nothing, and the finally decides on the hand-back.
                if (run.Stop) return;
                s_workers.Claim(unit.Path, run);
                if (unit.Refused)
                {
                    unit.Refused = false;
                    unit.Refusals = 0;
                    changed = true;
                }
                string answered = null;
                try { answered = unit.Session.DeviceName; } catch { }
                if (answered != null && answered != unit.AnsweredName)
                {
                    unit.AnsweredName = answered;
                    changed = true;
                }
            }
            // Map Key Lights asking about one light has no claim behind it,
            // so the strips stay as the claims sized them.
            if ((ruler.Device != Guid.Empty && !SizeStrips(unit, ruler.Strips)) || !Paint(unit, rgb, ruler.Keys, ruler.KeepsChanging, ref due))
            {
                Fail(unit, "write failed", ref changed, ref failed, ref due);
                return;
            }
            // The row's line follows what the session says of its device.
            bool stores;
            try { stores = (unit.Session.HoldMs ?? unit.Model.HoldMs) > 0; }
            catch { stores = unit.Model.HoldMs > 0; }
            if (stores != unit.StoresPictures)
            {
                unit.StoresPictures = stores;
                changed = true;
            }
        }

        /// <summary>The wait a session is handed. It serves the other units while
        /// this session waits on its device, so a session that waits between its
        /// reports, 2 s a picture on a Dell G series, holds nothing up: another
        /// unit's new picture goes out as it would on that unit's turn, and a
        /// keyboard that drops the host's colors when the host goes quiet does not
        /// fall back to its own lighting (HyperXKeyboardLighting.KeepAliveMs is 50).
        /// A picture goes out here only when its last one fit in what is left of
        /// the wait, so the waiting session keeps its pace, and what the pass takes
        /// comes off the wait. A session that waits inside that picture has the
        /// keep-alives served beside it, and nothing further. A write that fails here
        /// fails its own unit on that unit's turn.</summary>
        private void SessionWait(int ms)
        {
            var units = t_served;
            int depth = t_besideDepth;
            if (units == null || depth >= 2)
            {
                _wait(ms);
                return;
            }
            int left = ms;
            while (left > 0)
            {
                long next = long.MaxValue;
                long passAt = _elapsedMs();
                long lookAt = _ticks();
                bool pictures = depth == 0 && t_run is { Stop: false } && lookAt - t_picturesBesideAt >= PicturesBesideMs;
                if (pictures) t_picturesBesideAt = lookAt;
                t_besideDepth = depth + 1;
                try
                {
                    foreach (var unit in units)
                    {
                        if (ReferenceEquals(unit, t_serving) || ReferenceEquals(unit, t_servedBeside) || unit.Asleep
                            || unit.KeepAliveFailed || unit.Session == null) continue;
                        // A program of the model or OpenRGB took the device since
                        // this unit's turn. Nothing goes out, as on that turn
                        // (Serve), and the turn lets the unit go.
                        if (ProgramRuns(unit.Model.Programs) || PeripheralOutputs.TakenByOpenRgb(PathOf(unit))) continue;
                        // A later run claimed the collection, and the keep-alive
                        // of a stopped worker is its old picture.
                        if (!s_workers.MayReach(unit.Path, t_run)) continue;
                        if (depth == 0) t_servedBeside = unit;
                        try
                        {
                            bool fits = unit.PaintTookMs >= 0 && unit.PaintTookMs < left - MsSince(passAt);
                            bool sent = pictures && fits && !OnOneDevice(unit, t_serving)
                                ? PictureBeside(unit, ref next)
                                : KeepAlive(unit, ref next);
                            if (!sent) unit.KeepAliveFailed = true;
                        }
                        catch
                        {
                            unit.KeepAliveFailed = true;
                        }
                        finally
                        {
                            if (depth == 0) t_servedBeside = null;
                        }
                    }
                }
                finally
                {
                    t_besideDepth = depth;
                }
                left -= Math.Min(MsSince(passAt), left);
                if (left <= 0) break;
                int step = next == long.MaxValue ? left : (int)Math.Clamp(next - _ticks(), 1, left);
                // A color that comes during a long wait goes out during it. A unit with
                // nothing else to serve just waits.
                if (depth == 0 && units.Count > 1) step = Math.Min(step, PicturesBesideMs);
                // A short sleep can take twice what it asks, on a timer the system
                // keeps coarse, and many of them must not stretch the wait.
                long sleptAt = _elapsedMs();
                _wait(step);
                left -= Math.Max(step, MsSince(sleptAt));
            }
        }

        /// <summary>Two collections of one device: a session's reports may not
        /// have another's between them.</summary>
        private static bool OnOneDevice(Unit a, Unit b)
            => a != null && b != null && a.Container != Guid.Empty && a.Container == b.Container;

        /// <summary>What a unit's turn sends once it has a session: its picture or
        /// its keep-alive. Everything else of a turn waits for that turn: a unit
        /// nobody wants any more is let go there. False when a write failed.</summary>
        private bool PictureBeside(Unit unit, ref long due)
        {
            var path = PathOf(unit);
            if (!PeripheralOutputs.Links.ByPath.ContainsKey(path)
                || !PeripheralOutputs.TryResolveColor(path, out int rgb, out var ruler))
                return KeepAlive(unit, ref due);
            return (ruler.Device == Guid.Empty || SizeStrips(unit, ruler.Strips)) && Paint(unit, rgb, ruler.Keys, ruler.KeepsChanging, ref due);
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
                bool held = false;
                try { held = own == null && unit.Model.OpenFoundHeld?.Invoke() == true; } catch { }
                unit.HeldByProgram = held;
                return own;
            }
            try { io = _open(unit.Path); } catch { }
            NoteHolder(unit, io is { FeatureOnly: true } && !unit.Model.HeldByWindows ? unit.Info : null);
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

        /// <summary>Says who has a collection that opened with no access right
        /// for a model that needs more: Windows where
        /// <see cref="HidCollectionInfo.WindowsHolds"/> names it, and another
        /// program anywhere else. Null: nobody does.</summary>
        private static void NoteHolder(Unit unit, HidCollectionInfo collection)
        {
            unit.WindowsHolds = collection?.WindowsHolds == true;
            unit.HeldByProgram = collection != null && !collection.WindowsHolds;
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
        /// that way, and one that opened with no access right says who has
        /// it.</summary>
        private IHidReportChannel[] OpenSiblings(Unit unit, IReadOnlyList<HidSibling> wanted)
        {
            if (unit.Info == null || unit.Walk == null || unit.Container == Guid.Empty) return null;
            var opened = new IHidReportChannel[wanted.Count];
            for (int i = 0; i < wanted.Count; i++)
            {
                HidCollectionInfo sibling = null;
                foreach (var candidate in unit.Walk.Present)
                {
                    if (!wanted[i].Is(unit.Info, candidate) || SiblingContainer(unit.Walk, candidate.Path) != unit.Container) continue;
                    sibling = candidate;
                    break;
                }
                IHidReportChannel channel = null;
                try { channel = sibling == null ? null : _open(sibling.Path); } catch { }
                if (channel == null || channel.FeatureOnly)
                {
                    if (channel != null) NoteHolder(unit, sibling);
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
        /// earlier run left in it (<see cref="HidLightingModel.Repair"/>, or
        /// <see cref="HidLightingModel.RepairOwn"/> through the family's own
        /// channel). True when nothing is owed any more.</summary>
        private bool Repair(Unit unit)
        {
            if (unit.Own != null)
            {
                IDisposable own = null;
                try { own = unit.Model.Open?.Invoke(unit.Path); } catch { }
                if (own == null) return false;
                bool done = false;
                try { done = unit.Model.RepairOwn?.Invoke(own, _sessionWait) ?? true; } catch { }
                try { own.Dispose(); } catch { }
                if (done)
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} colors an earlier run left are settled");
                return done;
            }
            IHidReportChannel io = null;
            try { io = _open(unit.Path); } catch { }
            if (!Usable(unit.Model, io))
            {
                try { io?.Dispose(); } catch { }
                return false;
            }
            bool settled = false;
            try { settled = unit.Model.Repair?.Invoke(io, _sessionWait) ?? true; } catch { }
            try { io.Dispose(); } catch { }
            if (settled)
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} colors an earlier run left handed back");
            return settled;
        }

        /// <summary>Opens the unit's collection for read and write and
        /// starts its session. False when it did not open that way, or, with
        /// <paramref name="refused"/>, when Windows holds the collection, or
        /// when it opened and the model's first reads went unanswered or
        /// showed a device the model does not light.</summary>
        private bool Open(Unit unit, out bool refused)
        {
            refused = false;
            var handle = OpenHandle(unit, out var io, out var siblings);
            if (handle == null)
            {
                refused = unit.WindowsHolds;
                return false;
            }
            HidLightingSession session = null;
            try
            {
                session = unit.Own != null ? unit.Model.BeginOwn?.Invoke(handle, _sessionWait)
                    : siblings != null ? unit.Model.BeginWith?.Invoke(io, siblings, _sessionWait)
                    : unit.Model.Begin?.Invoke(io, _sessionWait);
            }
            catch { }
            if (session == null)
            {
                try { handle.Dispose(); } catch { }
                Dispose(siblings);
                refused = true;
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
            unit.PaintTookMs = -1;
            unit.Sized = null;
            ReadSessionMap(unit);
            unit.SizeDue = (unit.SessionMap ?? unit.Model.Map).Strips.Count > 0;
            // A session that finds colors of an earlier run's names their
            // undoing from its first moment.
            Track(unit);
            return true;
        }

        /// <summary>A session that read the device's own map lights and
        /// publishes by it from now on, for as long as the device stays
        /// (<see cref="HidLightingSession.Map"/>). A later session that reads
        /// none leaves the map an earlier one read.</summary>
        private static void ReadSessionMap(Unit unit)
        {
            LightMap read = null;
            try { read = unit.Session.Map; } catch { }
            if (read == null) return;
            unit.SessionMap = read;
            PublishMap(unit);
        }

        /// <summary>Gives the session the ruling claim's strip counts where
        /// they differ from the last it was given. False when a write or a
        /// read failed.</summary>
        private bool SizeStrips(Unit unit, IReadOnlyList<int> counts)
        {
            if (!unit.SizeDue && StripCounts.Same(unit.Sized, counts)) return true;
            unit.SizeDue = false;
            unit.Sized = counts;
            bool sent;
            try { sent = unit.Session.SizeStrips(unit.Io, _sessionWait, counts ?? StripCounts.None); }
            catch { sent = false; }
            Track(unit);
            if (!sent) return false;
            ReadSessionMap(unit);
            return true;
        }

        /// <summary>One pass over an open, wanted unit, with the claim's
        /// color and key frame as the pass resolved them. False when a
        /// write or a read failed.</summary>
        private bool Paint(Unit unit, int rgb, KeyFrame keys, bool keepsChanging, ref long due)
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
                    bool answered, repaint = false;
                    try
                    {
                        answered = unit.Session.Recheck(unit.Io, _sessionWait);
                        repaint = answered && unit.Session.RepaintDue;
                    }
                    catch { answered = false; }
                    Track(unit);
                    if (!answered) return false;
                    if (repaint)
                    {
                        unit.Shown = null;
                        unit.ShownMap = null;
                    }
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
            int holdMs, repaintMs;
            try
            {
                holdMs = unit.Session.HoldMs ?? model.HoldMs;
                repaintMs = unit.Session.RepaintMs ?? model.RepaintMs;
            }
            catch
            {
                holdMs = model.HoldMs;
                repaintMs = model.RepaintMs;
            }
            if (holdMs > 0 && keepsChanging)
            {
                // A stored picture cannot follow it, and each one written costs
                // the device's memory a write. The device keeps what it shows.
                unit.Pending = null;
                unit.PendingMap = null;
                return KeepAlive(unit, ref due);
            }
            long at = unit.PendingAt + holdMs;
            if (unit.Shown != null)
            {
                at = Math.Max(at, unit.SentAt + repaintMs);
                if (holdMs > 0) at = Math.Max(at, StoredWriteDue(unit, now));
            }
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
            long paintAt = _elapsedMs();
            try { result = session.Paint(unit.Io, _sessionWait, map.Lights, colors); }
            catch { result = HidLightingPaint.Failed; }
            // A picture that went out. A session's Later has sent nothing, however
            // long the picture it holds back will take.
            if (result == HidLightingPaint.Sent) unit.PaintTookMs = MsSince(paintAt);
            Track(unit);
            if (result == HidLightingPaint.Failed) return false;
            if (result == HidLightingPaint.Later)
            {
                unit.LaterAt = _ticks() + Math.Max(1, session.LaterMs);
                Soonest(ref due, unit.LaterAt);
                return true;
            }
            bool first = unit.Shown == null;
            if (!first && holdMs > 0) unit.StoredWrites--;
            unit.Shown = colors;
            unit.ShownMap = map;
            unit.SentAt = _ticks();
            unit.Pending = null;
            unit.PendingMap = null;
            unit.LaterAt = 0;
            if (first)
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} lit, {colors.Length} lights");
            // The worker wakes for the first keep-alive after this picture,
            // not at its idle tick, which is longer than HyperX's 50 ms.
            if (model.KeepAliveMs > 0)
                Soonest(ref due, Math.Max(unit.KeptAt + model.KeepAliveMs, unit.SentAt + model.KeepAliveIdleMs));
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
            try { sent = unit.Session.KeepAlive(unit.Io, _sessionWait, unit.ShownMap.Lights, unit.Shown); }
            catch { sent = false; }
            Track(unit);
            if (!sent) return false;
            unit.KeptAt = _ticks();
            Soonest(ref due, Math.Max(unit.KeptAt + model.KeepAliveMs, unit.SentAt + model.KeepAliveIdleMs));
            return true;
        }

        /// <summary>When a stored-picture model may send its next picture: at
        /// once while it has one left of its burst, else when it earns the next
        /// (<see cref="StoredWriteBurst"/>).</summary>
        private static long StoredWriteDue(Unit unit, long now)
        {
            if (unit.StoredWritesAt == 0) unit.StoredWritesAt = now;
            long earned = (now - unit.StoredWritesAt) / StoredWriteRefillMs;
            if (earned > 0)
            {
                unit.StoredWrites = (int)Math.Min(StoredWriteBurst, unit.StoredWrites + earned);
                unit.StoredWritesAt += earned * StoredWriteRefillMs;
            }
            return unit.StoredWrites > 0 ? now : unit.StoredWritesAt + StoredWriteRefillMs;
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
                s_workers.HandedBack(unit.Path, t_run);
                return true;
            }
            bool released = false;
            try { released = unit.Session.HandBack(unit.Io, _sessionWait); } catch { }
            Track(unit);
            if (released) s_workers.HandedBack(unit.Path, t_run);
            if (released) PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} lighting handed back");
            return released;
        }

        /// <summary>The first of <paramref name="programs"/> that runs now,
        /// by the presence the link pass last published, or null.</summary>
        private static VendorProgram ProgramRunning(IReadOnlyList<VendorProgram> programs)
            => programs == null || programs.Count == 0 ? null : PeripheralOutputs.Presence.Programs.FirstOf(programs);

        private static bool ProgramRuns(IReadOnlyList<VendorProgram> programs) => ProgramRunning(programs) != null;

        /// <summary>A write, an open or a read failed. What the session would
        /// have handed back stays owed, the channel closes and the unit is
        /// listed asleep, never taken off the list. The retry counts from now,
        /// after whatever the failure held the worker for.</summary>
        private void Fail(Unit unit, string why, ref bool changed, ref bool failed, ref long due)
        {
            Owe(unit);
            Close(unit);
            RecordOwed(unit);
            unit.Asleep = true;
            unit.RetryAt = _ticks() + RetryMs;
            changed = true;
            failed = true;
            Soonest(ref due, unit.RetryAt);
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} {why}, listed asleep");
        }

        /// <summary>The handle opened and the session was refused, or Windows
        /// holds the collection: the device is there and takes no lighting from
        /// PadForge, for now or for good. No walk is asked for, since nothing
        /// says it left. After a refused session each try waits twice as long
        /// as the one before, up to <see cref="RefusedRetryMaxMs"/>. A collection
        /// Windows holds comes here once: its next try waits
        /// <see cref="RetryMs"/>, and every try after that waits the bound
        /// (Serve).</summary>
        private void Refuse(Unit unit, ref bool changed, ref long due)
        {
            long wait = Math.Min(RefusedRetryMaxMs, (long)RetryMs << Math.Min(unit.Refusals, 16));
            unit.Refusals++;
            unit.Refused = true;
            unit.Asleep = true;
            unit.RetryAt = _ticks() + wait;
            changed = true;
            Soonest(ref due, unit.RetryAt);
            if (unit.Refusals == 1)
                PadForge.Engine.SdlDiagLog.WriteLine(unit.WindowsHolds
                    ? $"PERIPHERAL HidLighting {unit.Label} is a collection Windows holds, and its reports do not pass, listed asleep"
                    : $"PERIPHERAL HidLighting {unit.Label} opened and took no session, listed asleep");
        }

        /// <summary>Keeps the frames of a session that ends with no hand-back.
        /// A model with a backup of its own settles that itself, by the next
        /// session or its repair.</summary>
        private static void Owe(Unit unit)
        {
            var session = unit.Session;
            if (session == null || unit.Model.Owed != null) return;
            if (unit.Own != null && unit.Model.WriteOnce == null) return;
            byte[][] frames = null;
            try { frames = session.CrashFrames; } catch { }
            if (frames == null || frames.Length == 0) return;
            unit.OwedFrames = frames;
            unit.OwedFeature = session.Feature;
            unit.OwedPath = session.CrashPath;
        }

        /// <summary>Sends what the unit is owed, ahead of whatever else it is
        /// sent. It ends with nothing sent where a hand-back sends nothing: a
        /// program of the model or OpenRGB has the device (<see cref="Release"/>).
        /// True when nothing is owed any more, false when a frame did not go
        /// out, and then the unit stays owed.</summary>
        private bool PayOwed(Unit unit)
        {
            var frames = unit.OwedFrames;
            if (frames == null) return true;
            if (!ProgramRuns(unit.Model.Programs) && !PeripheralOutputs.TakenByOpenRgb(PathOf(unit)))
            {
                var own = unit.Own != null ? unit.Model.WriteOnce : null;
                foreach (var frame in frames)
                {
                    bool sent = false;
                    try { sent = WriteOnce(unit.OwedPath ?? unit.Path, frame, unit.OwedFeature, own, OneShotMs); } catch { }
                    if (!sent) return false;
                }
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} owed hand-back sent");
            }
            EndOwed(unit);
            return true;
        }

        private void EndOwed(Unit unit)
        {
            unit.OwedFrames = null;
            Unrecord(unit);
            s_workers.HandedBack(unit.Path, t_run);
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
            unit.KeepAliveFailed = false;
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
            if (frames.Length == 0)
            {
                s_workers.HandedBack(unit.Path, t_run);
                return;
            }
            // What Owe keeps: a model with a backup of its own settles that itself,
            // and a channel of a family's own needs that family's write.
            if (unit.Model.Owed == null && (unit.Own == null || unit.Model.WriteOnce != null))
                s_workers.NoteHandBack(unit.Path, t_run, new LateFrames(frames, unit.Session.Feature, unit.Session.CrashPath));
            var current = Volatile.Read(ref _recorded);
            var next = new CrashRecord[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = new CrashRecord(unit.Path, frames, unit.Session.Feature, unit.Model.Programs,
                unit.Own != null ? unit.Model.WriteOnce : null, unit.Session.CrashPath);
            Volatile.Write(ref _recorded, next);
            unit.Recorded = true;
            unit.RecordedFrames = frames;
        }

        /// <summary>Keeps what the unit is owed on the record until it is
        /// settled, with its session gone.</summary>
        private void RecordOwed(Unit unit)
        {
            var frames = unit.OwedFrames;
            if (frames == null) return;
            var current = Volatile.Read(ref _recorded);
            var next = new CrashRecord[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = new CrashRecord(unit.Path, frames, unit.OwedFeature, unit.Model.Programs,
                unit.Own != null ? unit.Model.WriteOnce : null, unit.OwedPath);
            Volatile.Write(ref _recorded, next);
            unit.Recorded = true;
            unit.RecordedFrames = frames;
        }

        /// <summary>Takes the unit's own entry off the record: its path and
        /// its frames. Not every entry of its path: a worker a timed-out Stop
        /// left behind closes its units while the next worker holds the same
        /// collections. Not every entry of its frames: two units of one model
        /// can share one array of them.</summary>
        private void Unrecord(Unit unit)
        {
            if (!unit.Recorded) return;
            unit.Recorded = false;
            var frames = unit.RecordedFrames;
            unit.RecordedFrames = null;
            var current = Volatile.Read(ref _recorded);
            var next = new List<CrashRecord>(current.Length);
            foreach (var entry in current)
                if (!ReferenceEquals(entry.Frames, frames) || !string.Equals(entry.Path, unit.Path, StringComparison.Ordinal))
                    next.Add(entry);
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
