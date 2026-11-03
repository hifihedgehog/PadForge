using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Razer direct worker (#494): it lights one Razer mouse or keyboard
    /// at a time through the device's own feature reports, with no Razer
    /// software running, so a device assigned to a virtual controller shows
    /// that controller's color. <see cref="RazerDirectProtocol"/> builds the
    /// reports, <see cref="RazerDirectTable"/> says which a product takes,
    /// and <see cref="HidppBackend"/> is the model for the worker.
    ///
    /// <para>Scan. A unit is a listed product with a color path, found among
    /// the collections the host hands over by the one fact every Windows
    /// reference goes by: its feature report is 91 bytes (OpenSynapse
    /// src/OpenSynapse.Windows/Protocols/RazerFeatureReport.cs:5,
    /// openrazer-win openrazer_win/core/transport.py:46-48). A device can
    /// show more than one such collection. They are one unit, keyed by the
    /// collection the table ranks first
    /// (<see cref="RazerDirectProtocol.CollectionRank"/>), and written
    /// through the first of them that answers. Nothing is sent to a device
    /// during a scan.</para>
    ///
    /// <para>Claim. A unit is opened only once a row links its path and a
    /// color resolves on it. The first request is a read that changes
    /// nothing. Its reply settles the transaction ID: the two projects the
    /// table was read from disagree on it for 64 of the products, a device
    /// answers some IDs and stays silent on others, and the IDs the table
    /// lists are tried one claim attempt at a time, so a mouse asleep
    /// behind its receiver never gets a burst of them. A receiver's 04
    /// settles none: it says so for a mouse that is asleep and for an ID
    /// nothing behind it reads alike. Then the device's
    /// own regions are read where the family has the list, and what the
    /// device shows on its own is read where the family can say, which is
    /// the hand-back. Only then, with the hand-back on the crash path's
    /// record, do the claim reports and the first color go out.</para>
    ///
    /// <para>Every request is followed by a read of its reply, under the
    /// lock third-party Razer tools share (<see cref="RazerGuard"/>). The
    /// wait between the two is the product's
    /// (<see cref="RazerDirectRow.ReplyWaitUs"/>). A wired device's 600
    /// microseconds are waited for in place. A wireless mouse takes 31 ms
    /// or 59.9 ms, and the worker does not sleep on those: it notes when
    /// the read is due and serves the other devices meanwhile
    /// (<see cref="RazerDirectRow.SlowReply"/>). A request is tried five
    /// times, 10 ms apart (openrazer driver/razermouse_driver.c:127-157,
    /// OpenSynapse src/OpenSynapse.Windows/Protocols/RazerFeatureTransport.cs:302).</para>
    ///
    /// <para>Paint. A color is one report per LED, or a frame, by the
    /// product's family. No color is queued: a pass paints the color that
    /// resolves then, at most every <see cref="PaintMs"/>, and never starts
    /// a paint while one is in flight. The claim and the color go out again
    /// every <see cref="ReassertMs"/>, since a device that lost power shows
    /// its stored effect again and says nothing (openrazer-win README.md:336),
    /// and once when Windows resumes from sleep (uncoil
    /// crates/uncoil-hid/src/transport.rs:116-117). A laptop keyboard goes
    /// back to its own lighting when the host goes quiet, so its claim read
    /// is sent whenever <see cref="KeepAliveMs"/> pass with nothing else
    /// (OpenRGB
    /// Controllers/RazerController/RazerController/RazerController.cpp:94-110 and 155-170,
    /// OpenSynapse src/OpenSynapse.Windows/Lighting/BladeLightingController.cs:73-74).
    /// A product painted by the standard matrix effect report, which has no
    /// storage byte, takes a color only once it has held for
    /// <see cref="RazerDirectProtocol.EffectHoldMs"/>, the first color of a
    /// claim included, so a claim that comes and goes inside the hold
    /// opens nothing and writes nothing. It is not painted again on the
    /// clock.</para>
    ///
    /// <para>Set Chroma Color (#468) paints the Razer devices on its
    /// virtual controller whether or not their Lighting tabs take control.
    /// For a unit that is the live macro of the controller with the
    /// smallest displayed number among the ones that hold a row of it,
    /// ahead of the unit's ruling claim, the precedence the Chroma worker
    /// keeps for a category (<see cref="ChromaBackend.Wanted"/>). A macro
    /// with no claim behind it claims the unit for its window and hands it
    /// back after.</para>
    ///
    /// <para>Hand back. When no color resolves on a unit any more, each LED
    /// is given what the device showed for it where that could be read, or
    /// its spectrum effect, and a product painted by frame is sent nothing
    /// (<see cref="RazerDirectProtocol.SpectrumBack"/>). No report in any
    /// reference returns a device to its stored effect, so this is as far
    /// as a hand-back goes.</para>
    ///
    /// <para>A Razer program. Synapse and Razer Central drive every Razer
    /// device, and nothing keeps their reports and PadForge's apart, so
    /// from the pass that sees one running
    /// (<see cref="PeripheralPresence.RazerSoftwareRunning"/>) nothing more
    /// is sent to any unit: no color, no hand-back, no read of a reply in
    /// flight. The claim is forgotten and the handle closed, as the HID++
    /// worker lets go for G HUB (HidppBackend.LeftToLogitechProgram). The
    /// link table is read before the presence on every pass, because the
    /// link pass publishes the presence first: a table that shows a path
    /// withdrawn is never read beside a presence older than it.</para>
    ///
    /// <para>A unit that stops answering, or whose write fails, a
    /// hand-back's included, is not taken off the list. Its handle is
    /// closed, it is listed asleep, a
    /// walk is asked for where a write failed, and it is asked again every
    /// <see cref="RetryMs"/> while a row that held it is still lit, and
    /// once after each walk the host hands over, so a device whose row was
    /// replaced while it slept is found awake again. It leaves the list
    /// when its collection leaves the walk.</para>
    ///
    /// <para>A unit that falls asleep painted is owed its hand-back
    /// (<see cref="Unit.Owed"/>): a device that missed one report and
    /// stayed still shows the color it was sent. The hand-back keeps its
    /// place on the crash path's record, the unit is asked again every
    /// <see cref="RetryMs"/> whether or not anybody lights it, and once
    /// it answers the hand-back is the first thing it is sent, before it
    /// is listed awake. A stop sends it through a handle opened for each
    /// report. It ends with no report when a Razer program runs and when
    /// the device leaves.</para>
    /// </summary>
    internal sealed class RazerDirectBackend : IDirectUnitBackend
    {
        /// <summary>How often a lit unit's claim and color are sent again,
        /// and the fastest a color is repainted, both the HID++ worker's
        /// (<see cref="HidppBackend.ReassertMs"/>,
        /// <see cref="HidppBackend.PaintMs"/>).</summary>
        internal const int ReassertMs = HidppBackend.ReassertMs;
        internal const int PaintMs = HidppBackend.PaintMs;

        /// <summary>How long a laptop goes with nothing sent before its
        /// claim read is, OpenRGB's period on the two models it keeps
        /// awake.</summary>
        internal const int KeepAliveMs = 2500;

        /// <summary>How long until a unit that did not answer, or a claim
        /// the lock kept out, is tried again.</summary>
        internal const int RetryMs = 5000;

        internal const int Attempts = 5;
        internal const int AttemptGapMs = 10;
        internal const int IdleTickMs = 100;

        /// <summary>How long a new worker waits for the one a timed-out
        /// Stop left behind, before it opens the collections anyway
        /// (<see cref="HidppBackend.PredecessorJoinMs"/>).</summary>
        internal const int PredecessorJoinMs = HidppBackend.PredecessorJoinMs;

        /// <summary>How long Stop waits for the worker to hand its units
        /// back and end.</summary>
        internal const int StopJoinMs = 3000;

        /// <summary>How long the crash path waits for the worker to hand
        /// its units back on its own handles, and how long one of its own
        /// writes may take.</summary>
        internal const int PanicJoinMs = 250;
        internal const int OneShotMs = 200;

        /// <summary>The color of a unit nothing was painted on yet.</summary>
        private const int NoColor = -1;

        private enum Outcome
        {
            Pending,
            /// <summary>The reply was this request's, with status 02 or 01.</summary>
            Accepted,
            /// <summary>Status 05.</summary>
            NotSupported,
            /// <summary>Status 03 on every attempt.</summary>
            Refused,
            /// <summary>Status 04: nothing answers behind the receiver.</summary>
            Timeout,
            /// <summary>No attempt was answered.</summary>
            Silent,
            /// <summary>Another program kept the lock: nothing was sent.</summary>
            Busy,
            /// <summary>Every attempt failed at the handle.</summary>
            Dead,
        }

        /// <summary>One request and what became of it. A unit's
        /// conversation hands these to the worker one at a time and reads
        /// the outcome when it goes on.</summary>
        private sealed class Ask
        {
            public readonly byte[] Request;
            public readonly int LockWaitMs;

            /// <summary>The request goes out even where the lock stayed
            /// busy: a hand-back is never skipped.</summary>
            public readonly bool EvenUnlocked;

            public Outcome Outcome;
            public byte[] Reply;
            public bool WentUnlocked;

            // Where the request stands while it is in flight.
            public int Sends, HandleFaults, Refusals;
            public bool Sent, Reread, Locked;
            public long DueAt;

            public Ask(byte[] request, int lockWaitMs, bool evenUnlocked = false)
            {
                Request = request;
                LockWaitMs = lockWaitMs;
                EvenUnlocked = evenUnlocked;
            }
        }

        private sealed class Unit
        {
            public DirectUnit Listed;
            public RazerDirectRow Row;
            public OutputPath Path;

            /// <summary>The run of the worker that listed the unit, which
            /// marks its entry on the crash path's record.</summary>
            public WorkerRun Run;

            /// <summary>The device's 91-byte collections, the one to try
            /// first in front.</summary>
            public string[] Collections;

            /// <summary>The rows that last held the path, which decide
            /// whether an asleep unit is still worth asking.</summary>
            public Guid[] Rows = Array.Empty<Guid>();

            public bool Asleep;
            public bool Gone;
            public long RetryAt;

            /// <summary>A walk came in while the unit slept, so it is
            /// asked once more whether or not a row still wants it: the
            /// row that held it may have been replaced meanwhile.</summary>
            public bool AskOnce;

            // What a claim settled, kept until the device leaves.
            public byte TransactionId;

            /// <summary>The ID the unit's reads go out under where that is
            /// another than <see cref="TransactionId"/>, which its writes
            /// carry. Else 0 (<see cref="ReadId"/>).</summary>
            public byte ProbeId;
            public string Collection;
            public int NextId;

            /// <summary>0 for the row's own paint, else the fallback in
            /// use, counted from 1.</summary>
            public int Plan;

            // The claim.
            public IHidReportChannel Io;
            public string IoPath;
            public bool Claimed;
            public bool Written;
            public bool Reassert;
            public bool DimNoted;
            public byte[] Leds;
            public byte[][] HandBack = Array.Empty<byte[]>();

            /// <summary>The hand-back the unit is owed, and the collection
            /// it stays on the crash path's record under. Set when a
            /// painted unit falls asleep (<see cref="Sleep"/>), which is
            /// the only way a unit comes to owe one, so a unit that owes
            /// is asleep and holds no claim. Paid by the conversation that
            /// finds the unit answering again (<see cref="Probe"/>) and by
            /// the worker's last act (<see cref="LetGo"/>).</summary>
            public byte[][] Owed = Array.Empty<byte[]>();
            public string OwedPath;
            public long ClaimedAt, SentAt, LastSentAt, HeldSince;
            public int Rgb = NoColor, Held = NoColor;

            // What this pass found.
            public bool Wants;
            public int Wanted;

            // The conversation in flight.
            public IEnumerator<Ask> Script;
            public Ask Flight;
        }

        /// <summary>Everything one worker run owns, so a worker a timed-out
        /// Stop left behind shares nothing with its successor. What the
        /// backend itself holds for both, the snapshot and the crash path's
        /// record, a worker touches only for its own run
        /// (<see cref="Current"/>, <see cref="RemoveClaimed"/>).</summary>
        private sealed class Work
        {
            public readonly WorkerRun Run;
            public readonly Dictionary<string, Unit> Units = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Guid> Containers = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>The units that refused every paint the table has
            /// for them, by key, until their collection leaves the walk.</summary>
            public readonly HashSet<string> Refusing = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<int, ISet<Guid>> OnSlot = new();
            public IReadOnlyList<HidCollectionInfo> Present;
            public bool Scanned, Dirty, AskWalk;

            /// <summary>Set for the worker's last act, which waits for a
            /// slow device's reply in place where a pass notes it as due.</summary>
            public bool Blocking;
            public long ContainerRetryAt = long.MaxValue;
            public long Now, Next;

            public Work(WorkerRun run) => Run = run;
        }

        private readonly Func<string, IHidReportChannel> _open;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<long> _ticks;
        private readonly Action<int> _wait;
        private readonly IRazerGuard _guard;
        private readonly bool _ownsGuard;
        private readonly Func<string, byte[], int, bool> _setFeatureOnce;
        private readonly Func<int, ISet<Guid>> _devicesOnSlot;
        private readonly Func<int, int> _slotNumber;
        private readonly int _idleTickMs;
        private readonly int _reassertMs;
        private readonly int _predecessorJoinMs;
        private readonly int _stopJoinMs;
        private readonly bool _powerEvents;
        private readonly AutoResetEvent _wake = new(false);

        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;

        /// <summary>The list the host last handed over, until the worker
        /// takes it.</summary>
        private IReadOnlyList<HidCollectionInfo> _handed;

        /// <summary>The units a claim is under way on or held for, and the
        /// ones owed a hand-back from a claim that ended asleep, each
        /// with its hand-back and the run that made the entry, for the
        /// crash path, which cannot wait on this worker. Swapped whole,
        /// under <see cref="_claimedGate"/>, which is never held across a
        /// device call.</summary>
        private (string Path, byte[][] HandBack, WorkerRun Run)[] _claimed = Array.Empty<(string, byte[][], WorkerRun)>();
        private readonly object _claimedGate = new();

        private Thread _thread;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;
        private int _waitMs;
        private int _resumed;

        /// <summary>The newest worker thread of any instance, which the
        /// next one waits for. Static because the collections it holds are
        /// the machine's.</summary>
        private static Thread s_lastWorker;

        public RazerDirectBackend()
            : this(powerEvents: true) { }

        /// <summary>Test seam: the channel opener, the container lookup,
        /// the clock every interval here is measured on, the wait before a
        /// reply and between attempts (in microseconds), the lock, the
        /// crash path's one-shot writer, the two lookups a Set Chroma Color
        /// reads, the idle tick, the re-assert interval, the wait for a
        /// predecessor worker and Stop's join, so a bench drives the worker
        /// against scripted channels and a time it sets itself. Only the
        /// worker the app makes listens for Windows resuming. A test says
        /// so through <see cref="NoteResume"/>.</summary>
        internal RazerDirectBackend(Func<string, IHidReportChannel> open = null, Func<string, Guid> containerOf = null,
            Func<long> ticks = null, Action<int> wait = null, IRazerGuard guard = null,
            Func<string, byte[], int, bool> setFeatureOnce = null,
            Func<int, ISet<Guid>> devicesOnSlot = null, Func<int, int> slotNumber = null,
            int idleTickMs = IdleTickMs, int reassertMs = ReassertMs, int predecessorJoinMs = PredecessorJoinMs,
            int stopJoinMs = StopJoinMs, bool powerEvents = false)
        {
            _stopJoinMs = stopJoinMs;
            _open = open ?? (path => HidReportChannel.Open(path));
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _ticks = ticks ?? (() => Environment.TickCount64);
            // Never less than a millisecond: Windows adds its own latency
            // between the two reports, whatever the device needs
            // (openrazer-win openrazer_win/core/transport.py:37-39).
            _wait = wait ?? (us => Thread.Sleep(Math.Max(1, (us + 999) / 1000)));
            _ownsGuard = guard == null;
            _guard = guard ?? new RazerGuard();
            _setFeatureOnce = setFeatureOnce ?? HidReportChannel.SetFeatureOnce;
            _devicesOnSlot = devicesOnSlot ?? PeripheralOutputs.DevicesOnSlot;
            _slotNumber = slotNumber ?? PeripheralOutputs.DisplayedSlotNumber;
            _idleTickMs = idleTickMs;
            _reassertMs = reassertMs;
            _predecessorJoinMs = predecessorJoinMs;
            _powerEvents = powerEvents;
        }

        public OutputFamily Family => OutputFamily.RazerUnit;

        public DirectBackendSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public event Action SnapshotChanged;
        public event Action RescanRequested;

        /// <summary>Passes the worker has started, for a test that waits
        /// on passes and times nothing.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        /// <summary>The wait the worker asked for at the end of its last
        /// pass, so a test of its cadence reads the wait it chose.</summary>
        internal int WaitMs => Volatile.Read(ref _waitMs);

        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>The crash path's record, for the tests of when a unit
        /// is on it.</summary>
        internal IReadOnlyList<(string Path, byte[][] HandBack)> Claimed
            => Array.ConvertAll(Volatile.Read(ref _claimed), entry => (entry.Path, entry.HandBack));

        public void Start()
        {
            if (_thread != null || Volatile.Read(ref _disposed) != 0) return;
            var run = new WorkerRun();
            _run = run;
            // A list handed to an earlier run is not this run's, and
            // neither is what that run found: the host walks again on its
            // first pass, and this run has scanned nothing until then.
            Volatile.Write(ref _handed, null);
            Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
            Interlocked.Exchange(ref _resumed, 0);
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralRazer" };
            _thread.Start();
            PeripheralOutputs.LightingChanged += Wake;
            PeripheralOutputs.LinksChanged += Wake;
            PeripheralOutputs.ChromaMacroChanged += Wake;
            if (_powerEvents) SystemEvents.PowerModeChanged += OnPowerMode;
        }

        public void Stop()
        {
            PeripheralOutputs.LightingChanged -= Wake;
            PeripheralOutputs.LinksChanged -= Wake;
            PeripheralOutputs.ChromaMacroChanged -= Wake;
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
            // Kept for the worker to take. A list that arrives while no
            // worker runs is dropped at the next Start.
            if (present == null) return;
            Volatile.Write(ref _handed, present);
            Wake();
        }

        public void SignalStop()
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
        }

        private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) NoteResume();
        }

        /// <summary>Windows resumed from sleep: every lit unit is claimed
        /// and painted once more, since a device that lost power in
        /// between shows its stored effect again.</summary>
        internal void NoteResume()
        {
            Interlocked.Exchange(ref _resumed, 1);
            Wake();
        }

        private void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        // ─────────────────────────────────────────────
        //  The worker
        // ─────────────────────────────────────────────

        private void Worker(WorkerRun run)
        {
            // A worker a timed-out Stop left behind still holds its handles
            // and hands its units back in its finally, so a new worker
            // waits for it before it opens anything, for a bounded time
            // (HidppBackend.Worker).
            var previous = Interlocked.Exchange(ref s_lastWorker, Thread.CurrentThread);
            if (previous != null && previous != Thread.CurrentThread && previous.IsAlive
                && !previous.Join(_predecessorJoinMs))
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL Razer previous worker still running after {_predecessorJoinMs} ms, starting anyway");
            var work = new Work(run);
            try
            {
                while (!run.Stop)
                {
                    Interlocked.Increment(ref _passes);
                    Pass(work);
                    int wait = (int)Math.Clamp(work.Next - work.Now, 1, _idleTickMs);
                    Volatile.Write(ref _waitMs, wait);
                    // A Dispose whose Stop gave up its join releases the
                    // wake under a worker still inside a device call.
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Razer worker fault: " + ex.GetType().Name);
            }
            finally
            {
                LetGo(work);
            }
        }

        private static void Due(Work work, long tick)
        {
            if (tick < work.Next) work.Next = tick;
        }

        private void Pass(Work work)
        {
            long now = work.Now = _ticks();
            work.Next = long.MaxValue;
            work.OnSlot.Clear();

            var handed = Interlocked.Exchange(ref _handed, null);
            if (handed != null)
            {
                work.Present = handed;
                Scan(work, walked: true);
            }
            else if (now >= work.ContainerRetryAt)
            {
                // A container that did not answer is asked again, from the
                // list already in hand.
                Scan(work, walked: false);
            }
            Due(work, work.ContainerRetryAt);

            // The table first, then the presence: the link pass publishes
            // the presence before the table it links from it, so a table
            // that shows a path withdrawn is never read beside a presence
            // older than it (HidppBackend.LeftToLogitechProgram).
            var links = PeripheralOutputs.Links;
            bool programRuns = PeripheralOutputs.Presence.RazerSoftwareRunning;
            bool resumed = Interlocked.Exchange(ref _resumed, 0) != 0;

            List<Unit> gone = null;
            foreach (var unit in work.Units.Values)
            {
                if (resumed && unit.Claimed) unit.Reassert = true;
                Serve(work, unit, links, programRuns);
                // A unit that is owed its hand-back stays until it has it.
                if (unit.Gone && unit.Script == null && unit.Owed.Length == 0) (gone ??= new List<Unit>()).Add(unit);
            }
            if (gone != null)
                foreach (var unit in gone) work.Units.Remove(unit.Listed.Key);

            // A live Set Chroma Color ends without a word, so the pass
            // that lets it go runs at its lapse (ChromaBackend.PollWaitMs).
            Due(work, PeripheralOutputs.NextChromaMacroLapse(now));

            if (work.Dirty) Publish(work);
            if (work.AskWalk)
            {
                work.AskWalk = false;
                try { RescanRequested?.Invoke(); } catch { }
            }
        }

        /// <summary>True while this worker's run is the backend's own. A
        /// worker a timed-out Stop left inside a device call is no longer
        /// that when it comes back, and the snapshot is its successor's to
        /// publish by then.</summary>
        private bool Current(Work work) => ReferenceEquals(_run, work.Run);

        /// <summary>The units among the collections the host handed over.
        /// Nothing here opens a collection or sends it a report.
        /// <paramref name="walked"/> says the list is a new walk, after
        /// which an asleep unit is asked once more.</summary>
        private void Scan(Work work, bool walked)
        {
            var present = work.Present;
            if (present == null) return;
            long now = work.Now;
            work.ContainerRetryAt = long.MaxValue;

            // The control collections of each listed device. Every
            // collection of one USB device reports the same container and
            // product ID, and two devices of one model do not share a
            // container.
            var devices = new Dictionary<(Guid Container, ushort ProductId), List<HidCollectionInfo>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var collection in present)
            {
                if (collection == null || string.IsNullOrEmpty(collection.Path)
                    || collection.VendorId != RazerDirectProtocol.VendorId
                    || collection.FeatureReportLength != RazerDirectProtocol.BufferLength)
                    continue;
                var row = RazerDirectTable.Find(collection.ProductId);
                if (row == null || !row.HasColorPath || !seen.Add(collection.Path)) continue;
                if (!work.Containers.TryGetValue(collection.Path, out var container))
                {
                    try { container = _containerOf(collection.Path); }
                    catch { container = Guid.Empty; }
                    // A lookup that failed is not kept, and is made again
                    // in a while: without its container a unit matches no
                    // row, so it is not listed until the lookup answers.
                    if (container != Guid.Empty) work.Containers[collection.Path] = container;
                }
                if (container == Guid.Empty)
                {
                    work.ContainerRetryAt = now + RetryMs;
                    continue;
                }
                var device = (container, collection.ProductId);
                if (!devices.TryGetValue(device, out var list)) devices[device] = list = new List<HidCollectionInfo>();
                list.Add(collection);
            }
            Forget(work.Containers, seen);
            work.Refusing.RemoveWhere(key => !seen.Contains(key));

            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in devices)
            {
                var row = RazerDirectTable.Find(pair.Key.ProductId);
                var collections = pair.Value;
                collections.Sort((a, b) =>
                {
                    int byRank = RazerDirectProtocol.CollectionRank(row, a).CompareTo(RazerDirectProtocol.CollectionRank(row, b));
                    return byRank != 0 ? byRank : string.CompareOrdinal(a.Path, b.Path);
                });
                var paths = new string[collections.Count];
                for (int i = 0; i < paths.Length; i++) paths[i] = collections[i].Path;

                // A unit keeps the key it was first listed under for as
                // long as that collection is there, whatever else of the
                // device came into the walk since.
                Unit unit = null;
                bool refusing = false;
                foreach (string path in paths)
                {
                    refusing |= work.Refusing.Contains(path);
                    if (unit == null && work.Units.TryGetValue(path, out var known) && !known.Gone) unit = known;
                }
                if (refusing) continue;
                if (unit == null)
                {
                    string key = paths[0];
                    unit = new Unit
                    {
                        Row = row,
                        Run = work.Run,
                        Path = new OutputPath(OutputFamily.RazerUnit, key),
                        Listed = new DirectUnit(OutputFamily.RazerUnit, key, pair.Key.Container,
                            RazerDirectProtocol.VendorId, row.ProductId),
                        Leds = row.Leds,
                    };
                    work.Units[key] = unit;
                    work.Dirty = true;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL Razer found '{row.Name}' pid={row.ProductId:X4} family={row.Family} collections={paths.Length}");
                }
                unit.Collections = paths;
                if (unit.Collection != null && Array.FindIndex(paths, p => string.Equals(p, unit.Collection, StringComparison.OrdinalIgnoreCase)) < 0)
                    unit.Collection = null;
                if (walked && unit.Asleep) unit.AskOnce = true;
                kept.Add(unit.Listed.Key);
            }

            // A unit that refused every paint is on its way out already,
            // and leaves once its hand-back is through. One that is owed
            // that hand-back leaves with its collection, as any unit does.
            List<Unit> left = null;
            foreach (var unit in work.Units.Values)
                if (unit.Gone ? unit.Owed.Length > 0 && !seen.Contains(unit.Listed.Key) : !kept.Contains(unit.Listed.Key))
                    (left ??= new List<Unit>()).Add(unit);
            if (left != null)
            {
                foreach (var unit in left)
                {
                    // Its collection left the walk: the device is gone, and
                    // with it anything a hand-back could reach, an owed one
                    // too.
                    ClearOwed(unit);
                    Abandon(unit);
                    Shut(unit);
                    work.Units.Remove(unit.Listed.Key);
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL Razer '{unit.Row.Name}' left");
                }
                work.Dirty = true;
            }
            if (!work.Scanned)
            {
                work.Scanned = true;
                work.Dirty = true;
            }
        }

        private static void Forget(Dictionary<string, Guid> containers, HashSet<string> seen)
        {
            List<string> gone = null;
            foreach (string path in containers.Keys)
                if (!seen.Contains(path)) (gone ??= new List<string>()).Add(path);
            if (gone != null)
                foreach (string path in gone) containers.Remove(path);
        }

        private void Publish(Work work)
        {
            work.Dirty = false;
            if (!Current(work)) return;
            var units = new List<DirectUnit>(work.Units.Count);
            foreach (var unit in work.Units.Values)
            {
                if (unit.Gone) continue;
                if (unit.Listed.Asleep != unit.Asleep) unit.Listed = unit.Listed with { Asleep = unit.Asleep };
                units.Add(unit.Listed);
            }
            // One order whatever the dictionary's, so a list that holds the
            // same units reads as the same list.
            units.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Volatile.Write(ref _snapshot, new DirectBackendSnapshot(units, work.Scanned));
            try { SnapshotChanged?.Invoke(); } catch { }
        }

        // ─────────────────────────────────────────────
        //  One unit, one pass
        // ─────────────────────────────────────────────

        private void Serve(Work work, Unit unit, LinkTable links, bool programRuns)
        {
            if (programRuns)
            {
                // A Razer program has the devices. Whatever this unit
                // holds is forgotten with no report, a hand-back least of
                // all: it would land on top of the program's own effect.
                if (unit.Io != null || unit.Script != null || unit.Claimed)
                {
                    bool lit = unit.Claimed;
                    Abandon(unit);
                    Shut(unit);
                    if (lit)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL Razer '{unit.Row.Name}' lighting left to the Razer program that started");
                }
                // What a unit was owed ends the same way, asleep as it is:
                // the program has the device.
                ClearOwed(unit);
                unit.Wants = false;
                return;
            }

            unit.Wants = !unit.Asleep && !unit.Gone && Wants(work, unit, links, out unit.Wanted);

            // A conversation that ends leaves room for the next one in the
            // same pass: a claim that was just refused its lock, a paint
            // that follows a hand-back. Three turns bound it.
            for (int turn = 0; turn < 3; turn++)
            {
                if (unit.Script == null && !Begin(work, unit)) return;
                if (!Pump(work, unit)) return;
            }
        }

        /// <summary>The color a unit shows now: the live Set Chroma Color
        /// of a virtual controller that holds a row of it, else its ruling
        /// claim. False when no row links its path or neither gives a
        /// color.</summary>
        private bool Wants(Work work, Unit unit, LinkTable links, out int rgb)
        {
            rgb = 0;
            if (!links.ByPath.TryGetValue(unit.Path, out var rows)) return false;
            unit.Rows = rows;
            if (PeripheralOutputs.AnyChromaMacro(work.Now)
                && MacroOn(LitThrough(links, unit.Path, rows), work.Now, slot => OnSlot(work, slot), _slotNumber, out rgb))
                return true;
            return PeripheralOutputs.TryResolveColor(unit.Path, out rgb);
        }

        private ISet<Guid> OnSlot(Work work, int slot)
        {
            if (!work.OnSlot.TryGetValue(slot, out var devices)) work.OnSlot[slot] = devices = _devicesOnSlot(slot);
            return devices;
        }

        /// <summary>The rows that hold a path for lighting, among the rows
        /// the link table lists for it.</summary>
        internal static List<Guid> LitThrough(LinkTable links, OutputPath path, Guid[] rows)
        {
            var lit = new List<Guid>(rows.Length);
            foreach (var device in rows)
            {
                var row = links.For(device);
                if (row != null && Array.IndexOf(row.Lighting, path) >= 0) lit.Add(device);
            }
            return lit;
        }

        /// <summary>
        /// The live Set Chroma Color that paints a unit, given the rows lit
        /// through it: the macro of a virtual controller one of those rows
        /// is assigned to, and the one with the smallest displayed number
        /// where two are live, the Chroma worker's rule for a category
        /// (<see cref="ChromaBackend.Wanted"/>). The Chroma worker asks the
        /// same question to leave such a unit's category out of its
        /// session.
        /// </summary>
        internal static bool MacroOn(IReadOnlyList<Guid> rows, long now, Func<int, ISet<Guid>> devicesOnSlot,
            Func<int, int> slotNumber, out int rgb)
        {
            rgb = 0;
            if (rows.Count == 0 || !PeripheralOutputs.AnyChromaMacro(now)) return false;
            bool found = false;
            int best = 0;
            for (int slot = 0; slot < InputManager.MaxPads; slot++)
            {
                if (!PeripheralOutputs.TryGetChromaMacro(slot, now, out int macro)) continue;
                var assigned = devicesOnSlot(slot);
                if (assigned == null || assigned.Count == 0) continue;
                bool holds = false;
                foreach (var device in rows)
                    if (assigned.Contains(device)) { holds = true; break; }
                if (!holds) continue;
                int number = slotNumber(slot);
                if (found && best <= number) continue;
                best = number;
                rgb = macro;
                found = true;
            }
            return found;
        }

        /// <summary>Whether an asleep unit is still worth asking: a row
        /// that held its path is lit, or a Set Chroma Color is live on a
        /// virtual controller that holds one. No path is linked to an
        /// asleep unit, so no claim resolves on it meanwhile, and a unit
        /// nobody lights any more is left alone until somebody does or a
        /// walk comes in (<see cref="Unit.AskOnce"/>), unless it is owed
        /// its hand-back (<see cref="Unit.Owed"/>).</summary>
        private bool StillWanted(Work work, Unit unit)
        {
            foreach (var device in unit.Rows)
                if (PeripheralOutputs.IsLit(device)) return true;
            return MacroOn(unit.Rows, work.Now, slot => OnSlot(work, slot), _slotNumber, out _);
        }

        private bool Held(Unit unit)
            => unit.Plan == 0
                ? unit.Row.Has(RazerRowFlags.HeldEffect)
                : unit.Row.Fallbacks[unit.Plan - 1].Kind == RazerFallbackKind.Effect;

        /// <summary>Starts what a unit needs next, if anything is due.
        /// False when nothing was started.</summary>
        private bool Begin(Work work, Unit unit)
        {
            long now = work.Now;
            // A unit that is owed its hand-back is asked for as long as
            // the debt stands, lit or not, on its way out or not.
            if (unit.Gone && unit.Owed.Length == 0) return false;
            if (unit.Asleep)
            {
                if (!unit.AskOnce && unit.Owed.Length == 0 && !StillWanted(work, unit)) return false;
                if (now < unit.RetryAt)
                {
                    Due(work, unit.RetryAt);
                    return false;
                }
                unit.AskOnce = false;
                return Start(unit, Probe(work, unit));
            }

            if (!unit.Wants)
            {
                // A color that comes back starts its hold anew.
                unit.Held = NoColor;
                if (unit.Io == null) return false;
                // A path that is gone or unclaimed with no Razer program
                // running: a switch turned off, a device unassigned, a
                // macro that ran out. The device gets its own lighting
                // back, where anything was written to it.
                if (unit.Written && unit.HandBack.Length > 0) return Start(unit, HandBack(work, unit));
                Release(unit, handedBack: false);
                return false;
            }

            if (!unit.Claimed)
            {
                if (now < unit.RetryAt)
                {
                    Due(work, unit.RetryAt);
                    return false;
                }
                // A stopping worker claims nothing more.
                if (work.Run.Stop) return false;
                if (Held(unit))
                {
                    // The report that may be stored carries the first color
                    // of a claim as it carries any other: once the color
                    // has held. A claim that comes and goes inside the
                    // hold, a short Set Chroma Color, opens nothing and
                    // writes nothing.
                    if (unit.Wanted != unit.Held)
                    {
                        unit.Held = unit.Wanted;
                        unit.HeldSince = _ticks();
                    }
                    long holdEnds = unit.HeldSince + RazerDirectProtocol.EffectHoldMs;
                    if (now < holdEnds)
                    {
                        Due(work, holdEnds);
                        return false;
                    }
                }
                return Start(unit, Claim(work, unit));
            }

            bool held = Held(unit);
            // No paint starts sooner than PaintMs after the one before it.
            long ready = unit.SentAt + PaintMs;

            // The claim goes out again on a clock of its own, whatever the
            // colors are doing, and once when Windows resumed. The effect
            // report that may be stored is never repeated on the clock.
            if (unit.Reassert || (!held && now - unit.ClaimedAt >= _reassertMs))
            {
                if (now >= ready) return Start(unit, Paint(work, unit, claim: true));
                Due(work, ready);
            }
            else if (unit.Wanted != unit.Rgb)
            {
                if (held)
                {
                    if (unit.Wanted != unit.Held)
                    {
                        // Stamped when the change is seen, which is after
                        // the pass read its clock: counted from that
                        // earlier tick, a color that changed during the
                        // pass would be written before it had held.
                        unit.Held = unit.Wanted;
                        unit.HeldSince = _ticks();
                    }
                    ready = Math.Max(ready, unit.HeldSince + RazerDirectProtocol.EffectHoldMs);
                }
                if (now >= ready) return Start(unit, Paint(work, unit, claim: false));
                Due(work, ready);
            }
            else
            {
                unit.Held = unit.Rgb;
            }
            if (!held) Due(work, unit.ClaimedAt + _reassertMs);

            if (unit.Row.Has(RazerRowFlags.Laptop))
            {
                long quiet = unit.LastSentAt + KeepAliveMs;
                if (now >= quiet) return Start(unit, KeepAlive(work, unit));
                Due(work, quiet);
            }
            return false;
        }

        private static bool Start(Unit unit, IEnumerable<Ask> script)
        {
            unit.Script = script.GetEnumerator();
            return true;
        }

        // ─────────────────────────────────────────────
        //  Conversations
        // ─────────────────────────────────────────────

        private static Ask Read(byte[] request) => new(request, RazerGuard.CommandWaitMs);

        /// <summary>
        /// Opens the device's control collection and settles its
        /// transaction ID with the claim read. On return the unit is open
        /// on a collection that answered, or closed: it does not answer and
        /// was put to sleep, or another program kept the lock.
        ///
        /// <para>A reply of 02, 01, 03 or 05 that echoes the request
        /// settles the ID. 02 and 01 are the answer, and 03 and 05 mean the
        /// firmware read the request and has no such value, which ten
        /// products may say since no project reads a brightness from them.
        /// 04 settles the collection alone. It is a receiver answering, for
        /// a mouse that is asleep or for an ID nothing behind it reads: of
        /// four IDs sent to a Mouse Dock Pro three answered and one timed
        /// out (sanshoku razer/driver.go:73-74), and that tool goes on to
        /// its next ID on 04 (razer/decode.go:78, razer/driver.go:81). A
        /// wrong ID gets no reply at all, so silence moves on too: to the
        /// device's next collection now, and to the row's next ID at the
        /// next attempt, <see cref="RetryMs"/> later. A settled ID and
        /// collection are kept until the device leaves.</para>
        ///
        /// <para>Nine mice and one keyboard are read under an ID that is
        /// none of the ones they are painted under
        /// (<see cref="RazerDirectRow.ProbeTransactionId"/>). Their read
        /// goes out under that ID first, as the one project that sends it
        /// sends it, and an answer to it settles the row's first ID for
        /// every write, the pair that project drives them with. Every read
        /// of such a unit keeps the ID that answered
        /// (<see cref="ReadId"/>).</para>
        /// </summary>
        private IEnumerable<Ask> Settle(Work work, Unit unit)
        {
            var row = unit.Row;
            byte[] ids = RazerDirectProtocol.TransactionIds(row);
            byte[] asked = RazerDirectProtocol.ProbeIds(row);
            // The ID the read goes out under, and the ID an answer to it
            // settles for every other report.
            byte probe = unit.TransactionId == 0 ? asked[unit.NextId % asked.Length] : ReadId(unit);
            byte id = unit.TransactionId != 0 ? unit.TransactionId
                : Array.IndexOf(ids, probe) >= 0 ? probe : ids[0];
            string[] collections = unit.Collection != null ? new[] { unit.Collection } : unit.Collections;
            bool handle = false;
            foreach (string path in collections)
            {
                IHidReportChannel io = null;
                try { io = _open(path); } catch { }
                if (io == null)
                {
                    handle = true;
                    continue;
                }
                unit.Io = io;
                unit.IoPath = path;
                var ask = Read(RazerDirectProtocol.ProbeRead(row, probe));
                yield return ask;
                switch (ask.Outcome)
                {
                    case Outcome.Accepted:
                    case Outcome.NotSupported:
                    case Outcome.Refused:
                        unit.TransactionId = id;
                        unit.ProbeId = probe == id ? (byte)0 : probe;
                        unit.Collection = path;
                        if (ask.Outcome == Outcome.Accepted) NoteBrightness(unit, ask.Reply);
                        yield break;
                    case Outcome.Timeout:
                        // The receiver answered, so the collection is its
                        // own. The ID stays open, and the next attempt
                        // asks under the row's next one.
                        unit.Collection = path;
                        if (unit.TransactionId == 0) unit.NextId = (unit.NextId + 1) % asked.Length;
                        Sleep(work, unit, walk: false);
                        yield break;
                    case Outcome.Busy:
                        Shut(unit);
                        unit.RetryAt = _ticks() + RetryMs;
                        yield break;
                    case Outcome.Dead:
                        handle = true;
                        break;
                }
                Shut(unit);
            }
            if (unit.TransactionId == 0) unit.NextId = (unit.NextId + 1) % asked.Length;
            Sleep(work, unit, walk: handle);
        }

        /// <summary>The ID a unit's reads go out under: the one its claim
        /// read was answered under. Its writes carry
        /// <see cref="Unit.TransactionId"/>, which is the same ID unless the
        /// read has one of its own or the writes were moved on
        /// (<see cref="TryNextWriteId"/>).</summary>
        private static byte ReadId(Unit unit) => unit.ProbeId != 0 ? unit.ProbeId : unit.TransactionId;

        /// <summary>Moves a unit's writes to the row's next transaction ID
        /// and keeps its reads under the ID that answered. For a device
        /// that answered the claim read and then took no report of the
        /// claim: a firmware may read each kind under an ID of its own,
        /// the way openrazer reads thirteen of these products under one ID
        /// and paints them under another. A row with one ID has none to
        /// move to.</summary>
        private static void TryNextWriteId(Unit unit)
        {
            byte[] ids = RazerDirectProtocol.TransactionIds(unit.Row);
            if (ids.Length < 2) return;
            byte read = ReadId(unit);
            byte next = ids[(Array.IndexOf(ids, unit.TransactionId) + 1) % ids.Length];
            unit.TransactionId = next;
            unit.ProbeId = read == next ? (byte)0 : read;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL Razer '{unit.Row.Name}' answers reads under {read:X2} and took no report of its claim, writing under {next:X2} next");
        }

        /// <summary>A device whose own brightness is 0 shows no color,
        /// whatever is painted on it. It is left that way and named in the
        /// log once.</summary>
        private static void NoteBrightness(Unit unit, byte[] reply)
        {
            if (unit.DimNoted || unit.Row.Probe == RazerProbe.ClassicState) return;
            int at = RazerDirectProtocol.ArgumentsAt + (unit.Row.Probe == RazerProbe.LaptopBrightness ? 1 : 2);
            if (reply[RazerDirectProtocol.StatusAt] != RazerDirectProtocol.StatusSuccess || reply[at] != 0) return;
            unit.DimNoted = true;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL Razer '{unit.Row.Name}' has its brightness at 0, so no color shows on it");
        }

        /// <summary>An asleep unit is asked again. One that answers is
        /// listed awake, and the claim follows once a row links it again.
        ///
        /// <para>A unit that is owed its hand-back gets it here, on the
        /// handle the question went out on, before it is listed awake. So
        /// the claim that may follow reads what the device shows on its
        /// own (<see cref="OwnLighting"/>) only after the color PadForge
        /// left on it is gone. No reference says whether a classic LED
        /// keeps a color sent unstored apart from its stored one, and
        /// this order asks nothing of that. The lock is waited for once
        /// and the reports go out without it, as a hand-back's do
        /// (<see cref="HandBack"/>). A report the device does not take
        /// puts the unit back to sleep with the whole hand-back still
        /// owed.</para></summary>
        private IEnumerable<Ask> Probe(Work work, Unit unit)
        {
            foreach (var ask in Settle(work, unit)) yield return ask;
            if (unit.Io == null) yield break;
            if (unit.Owed.Length > 0)
            {
                bool wait = true;
                foreach (var report in unit.Owed)
                {
                    var ask = new Ask(report, wait ? RazerGuard.CommandWaitMs : 0, evenUnlocked: true);
                    yield return ask;
                    if (ask.WentUnlocked) wait = false;
                    if (ask.Outcome is Outcome.Dead or Outcome.Silent or Outcome.Timeout)
                    {
                        Sleep(work, unit, walk: ask.Outcome == Outcome.Dead);
                        yield break;
                    }
                }
                ClearOwed(unit);
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL Razer '{unit.Row.Name}' lighting handed back, owed since it stopped answering");
            }
            Shut(unit);
            unit.Asleep = false;
            work.Dirty = true;
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL Razer '{unit.Row.Name}' answers again");
        }

        /// <summary>What a read that got no answer means for the claim it
        /// is part of. True when the conversation is over.</summary>
        private bool Lost(Work work, Unit unit, Ask ask)
        {
            switch (ask.Outcome)
            {
                case Outcome.Dead:
                    Sleep(work, unit, walk: true);
                    return true;
                case Outcome.Timeout:
                    Sleep(work, unit, walk: false);
                    return true;
                case Outcome.Busy:
                    Shut(unit);
                    unit.RetryAt = _ticks() + RetryMs;
                    return true;
                default:
                    // Silence to a read no project sends to this product
                    // says nothing about the device: it answered the claim
                    // read a moment ago. The read counts as not answered.
                    return false;
            }
        }

        private IEnumerable<Ask> Claim(Work work, Unit unit)
        {
            foreach (var ask in Settle(work, unit)) yield return ask;
            if (unit.Io == null) yield break;
            var row = unit.Row;

            // The lit regions the device names, where its family has the
            // list (OpenRGB RazerController.cpp:1197-1271).
            unit.Leds = row.Leds;
            if (row.Family == RazerFamily.Extended)
            {
                var ask = Read(RazerDirectProtocol.RegionListRead(ReadId(unit)));
                yield return ask;
                if (Lost(work, unit, ask)) yield break;
                if (ask.Outcome == Outcome.Accepted)
                    unit.Leds = RazerDirectProtocol.PaintLeds(row, RazerDirectProtocol.Regions(ask.Reply));
            }

            // What the device shows on its own, read before anything is
            // written, for the hand-back. A fallback has none: no
            // reference says what undoes another family's paint.
            var back = new List<byte[]>();
            if (unit.Plan == 0)
            {
                foreach (var ask in OwnLighting(work, unit, back)) yield return ask;
                if (unit.Io == null) yield break;
            }
            unit.HandBack = back.ToArray();

            // The crash path learns of the claim before its first write
            // goes out, so no moment exists in which a device is painted
            // and the hand-back does not know it. A stopping worker claims
            // nothing more, and neither does a unit nobody wants by now.
            if (work.Run.Stop || !unit.Wants)
            {
                Shut(unit);
                yield break;
            }
            NoteClaimed(unit);
            unit.Rgb = NoColor;
            foreach (var ask in Paint(work, unit, claim: true)) yield return ask;
        }

        /// <summary>
        /// Fills <paramref name="back"/> with the reports that give each
        /// painted LED its own lighting again, from what the device says it
        /// shows where its family can be asked.
        ///
        /// <para>The extended families are asked for the stored effect of
        /// each LED, a read one Synapse capture shows and no project sends
        /// (<see cref="RazerDirectProtocol.StoredEffectRead"/>). An LED
        /// whose reply cannot be trusted gets the spectrum effect, on a
        /// product that has it. A classic LED says its state, color and
        /// effect exactly, and is written back as read. The mouse extended
        /// matrix and the standard matrix have nothing to ask.</para>
        ///
        /// <para>The unit comes back closed when the device stopped
        /// answering on the way or the lock stayed busy.</para>
        /// </summary>
        private IEnumerable<Ask> OwnLighting(Work work, Unit unit, List<byte[]> back)
        {
            var row = unit.Row;
            byte id = unit.TransactionId;
            if (row.Family is RazerFamily.Extended or RazerFamily.ExtendedFrame)
            {
                bool missing = false;
                var restored = new List<byte>();
                foreach (byte led in unit.Leds)
                {
                    var ask = Read(RazerDirectProtocol.StoredEffectRead(ReadId(unit), led));
                    yield return ask;
                    if (Lost(work, unit, ask)) yield break;
                    if (ask.Outcome == Outcome.Accepted
                        && RazerDirectProtocol.TryStoredEffect(id, led, ask.Reply, out var stored))
                    {
                        back.Add(stored);
                        restored.Add(led);
                    }
                    else if (Array.IndexOf(row.Leds, led) >= 0) missing = true;
                    // A region the device named itself, on a product that
                    // has the spectrum effect.
                    else if (row.SpectrumLeds.Length > 0) back.Add(RazerDirectProtocol.Spectrum(row, id, led));
                }
                // The table's own list, which names the zones of a mouse
                // whose whole-device spectrum openrazer marks doubtful.
                if (missing)
                    foreach (byte led in row.SpectrumLeds)
                        if (!restored.Contains(led)) back.Add(RazerDirectProtocol.Spectrum(row, id, led));
            }
            else if (row.Family == RazerFamily.Classic)
            {
                foreach (byte led in unit.Leds)
                {
                    var state = Read(RazerDirectProtocol.ClassicStateRead(ReadId(unit), led));
                    yield return state;
                    if (Lost(work, unit, state)) yield break;
                    var color = Read(RazerDirectProtocol.ClassicColorRead(ReadId(unit), led));
                    yield return color;
                    if (Lost(work, unit, color)) yield break;
                    var effect = Read(RazerDirectProtocol.ClassicEffectRead(ReadId(unit), led));
                    yield return effect;
                    if (Lost(work, unit, effect)) yield break;
                    // A row whose claim sets no state gets none written
                    // back, and its state read decides nothing.
                    bool withState = !row.Has(RazerRowFlags.NoState);
                    if ((!withState || state.Outcome == Outcome.Accepted) && color.Outcome == Outcome.Accepted
                        && effect.Outcome == Outcome.Accepted
                        && RazerDirectProtocol.TryClassicStored(id, led, state.Reply, color.Reply, effect.Reply, withState, out var stored))
                        back.AddRange(stored);
                    else if (Array.IndexOf(row.SpectrumLeds, led) >= 0)
                        back.Add(RazerDirectProtocol.Spectrum(row, id, led));
                }
            }
            else
            {
                back.AddRange(RazerDirectProtocol.SpectrumBack(row, id));
            }
        }

        /// <summary>
        /// One paint of the color wanted when it starts, behind the claim
        /// reports at a claim and at a re-assert.
        ///
        /// <para>The first paint of a claim decides whether the product
        /// takes its family at all, on its first color report and on any
        /// claim report in front of it. 05 there, or 03 on all five
        /// attempts, moves on to the next fallback the table gives the
        /// product, and with none left the unit is released and leaves the
        /// list until its collection has left the walk and come back: it is
        /// a product this table cannot paint. Once a color report was
        /// taken, in this paint or one before it, a report refused is one
        /// report lost, and a region the device named itself is not asked
        /// again (<see cref="Forgo"/>). A fallback that is the effect
        /// report waits for a color that has held, so the claim ends here
        /// and is made again under it.</para>
        ///
        /// <para>A lock that stays busy skips the paint. Nothing is
        /// recorded as sent, so the next pass paints the color that
        /// resolves then.</para>
        /// </summary>
        private IEnumerable<Ask> Paint(Work work, Unit unit, bool claim)
        {
            var row = unit.Row;
            while (true)
            {
                int rgb = unit.Wanted;
                // Every stamp is a fresh read of the clock. A pass reads
                // its own once, at its start, and may get here much later,
                // behind another device's replies.
                long started = _ticks();
                byte id = unit.TransactionId;
                var reports = new List<byte[]>();
                if (claim && unit.Plan == 0) reports.AddRange(RazerDirectProtocol.Claim(row, id));
                int claimReports = reports.Count;
                reports.AddRange(unit.Plan == 0
                    ? RazerDirectProtocol.Paint(row, id, unit.Leds, rgb)
                    : RazerDirectProtocol.Paint(row.Fallbacks[unit.Plan - 1], id, rgb));

                bool refused = false;
                int painted = 0;
                for (int at = 0; at < reports.Count; at++)
                {
                    byte[] report = reports[at];
                    // Not wanted any more, or stopping: the hand-back takes
                    // it from here.
                    if (work.Run.Stop || !unit.Wants) yield break;
                    var ask = new Ask(report, unit.Claimed ? RazerGuard.PaintWaitMs : RazerGuard.CommandWaitMs);
                    yield return ask;
                    // Pump has recorded the write (Unit.Written).
                    if (ask.Outcome == Outcome.Accepted)
                    {
                        if (at >= claimReports) painted++;
                        continue;
                    }
                    if (ask.Outcome == Outcome.Busy)
                    {
                        unit.SentAt = _ticks();
                        if (unit.Claimed) yield break;
                        if (unit.Written)
                        {
                            // Part of the claim went out. The rest follows
                            // as a re-assert, which waits for the lock no
                            // longer than a paint does.
                            unit.Claimed = true;
                            unit.Reassert = true;
                            unit.Rgb = NoColor;
                        }
                        else
                        {
                            Release(unit, handedBack: false);
                            unit.RetryAt = _ticks() + RetryMs;
                        }
                        yield break;
                    }
                    if (ask.Outcome is Outcome.NotSupported or Outcome.Refused)
                    {
                        // Nothing of the product's color was ever taken:
                        // it does not take this family.
                        if (!unit.Claimed && painted == 0)
                        {
                            refused = true;
                            break;
                        }
                        Forgo(unit, report);
                        continue;
                    }
                    // The device answered its claim read a moment ago and
                    // has taken no report under this ID, so the next
                    // attempt writes under the row's next one. A unit
                    // that took one keeps the ID that worked.
                    if (ask.Outcome == Outcome.Silent && !unit.Written) TryNextWriteId(unit);
                    Sleep(work, unit, walk: ask.Outcome == Outcome.Dead);
                    yield break;
                }

                if (!refused)
                {
                    if (!unit.Claimed)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL Razer '{row.Name}' lighting claimed (id {id:X2}"
                            + (unit.Plan == 0 ? ")" : $", fallback {unit.Plan})"));
                    unit.Claimed = true;
                    unit.Reassert = false;
                    if (claim) unit.ClaimedAt = started;
                    unit.Rgb = rgb;
                    unit.Held = rgb;
                    unit.SentAt = started;
                    yield break;
                }

                if (unit.Plan >= row.Fallbacks.Length)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL Razer '{row.Name}' refuses every paint known for it, leaving it to its own lighting");
                    work.Refusing.Add(unit.Listed.Key);
                    unit.Gone = true;
                    work.Dirty = true;
                    if (unit.Written && unit.HandBack.Length > 0)
                        foreach (var ask in HandBack(work, unit)) yield return ask;
                    else
                        Release(unit, handedBack: false);
                    yield break;
                }
                // The product's own family is not the device's, so what was
                // read for its hand-back does not apply either.
                unit.Plan++;
                unit.HandBack = Array.Empty<byte[]>();
                if (Held(unit))
                {
                    // This fallback is the report that may be stored, so
                    // it waits for a color that has held like every paint
                    // of its kind. The claim is made again under it.
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL Razer '{row.Name}' refused its paint, trying fallback {unit.Plan} once the color has held");
                    Release(unit, handedBack: false);
                    yield break;
                }
                NoteClaimed(unit);
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL Razer '{row.Name}' refused its paint, trying fallback {unit.Plan}");
            }
        }

        /// <summary>A region the device named itself and then refused a
        /// color for is left to the device from here on: out of the paint
        /// and out of the hand-back, on the crash path's record too. An LED
        /// of the table's own row stays, and is asked with every paint.</summary>
        private void Forgo(Unit unit, byte[] report)
        {
            var row = unit.Row;
            if (unit.Plan != 0 || row.Family != RazerFamily.Extended
                || report[RazerDirectProtocol.ClassAt] != 0x0F || report[RazerDirectProtocol.CommandAt] != 0x02)
                return;
            byte led = report[RazerDirectProtocol.ArgumentsAt + 1];
            if (Array.IndexOf(row.Leds, led) >= 0 || Array.IndexOf(unit.Leds, led) < 0) return;
            unit.Leds = Array.FindAll(unit.Leds, other => other != led);
            unit.HandBack = Array.FindAll(unit.HandBack, back =>
                back[RazerDirectProtocol.ClassAt] != 0x0F || back[RazerDirectProtocol.CommandAt] != 0x02
                || back[RazerDirectProtocol.ArgumentsAt + 1] != led);
            if (unit.IoPath != null) NoteClaimed(unit);
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL Razer '{row.Name}' takes no color on its region {led:X2}, leaving that region to the device");
        }

        /// <summary>A laptop's claim read, sent for nothing but to be
        /// sent.</summary>
        private IEnumerable<Ask> KeepAlive(Work work, Unit unit)
        {
            var ask = new Ask(RazerDirectProtocol.ProbeRead(unit.Row, ReadId(unit)), RazerGuard.PaintWaitMs);
            yield return ask;
            if (ask.Outcome is Outcome.Dead or Outcome.Silent or Outcome.Timeout)
                Sleep(work, unit, walk: ask.Outcome == Outcome.Dead);
            // Another program is at the lock: the next read is a whole
            // period away, and the re-assert repaints if the keyboard
            // went back to its own lighting meanwhile.
            else if (ask.Outcome == Outcome.Busy) unit.LastSentAt = _ticks();
        }

        /// <summary>The hand-back, then the handle is closed. The lock is
        /// waited for once: a hand-back that finds it busy goes out anyway,
        /// and waits again neither for its own later attempts nor for the
        /// reports after it. A hand-back the device did not take is a
        /// failed call like any other: the unit is listed asleep, still
        /// owed it, and a walk is asked for where the handle failed.</summary>
        private IEnumerable<Ask> HandBack(Work work, Unit unit)
        {
            bool wait = true;
            var lost = Outcome.Accepted;
            foreach (var report in unit.HandBack)
            {
                var ask = new Ask(report, wait ? RazerGuard.CommandWaitMs : 0, evenUnlocked: true);
                yield return ask;
                if (ask.WentUnlocked) wait = false;
                // The device is gone or asleep: the rest would fare no
                // better.
                if (ask.Outcome is Outcome.Dead or Outcome.Silent or Outcome.Timeout)
                {
                    lost = ask.Outcome;
                    break;
                }
            }
            if (lost == Outcome.Accepted) Release(unit, handedBack: true);
            // The worker's last act has no later pass to ask again in.
            else if (work.Blocking) Release(unit, handedBack: false, lost: true);
            else Sleep(work, unit, walk: lost == Outcome.Dead);
        }

        // ─────────────────────────────────────────────
        //  Requests and replies
        // ─────────────────────────────────────────────

        /// <summary>Runs a unit's conversation until it ends or has to
        /// wait for a slow device. True when it ended.</summary>
        private bool Pump(Work work, Unit unit)
        {
            while (unit.Script != null)
            {
                // Checked ahead of every report and every read, so a Razer
                // program that starts in the middle of a paint gets the
                // device at once.
                if (PeripheralOutputs.Presence.RazerSoftwareRunning)
                {
                    Abandon(unit);
                    Shut(unit);
                    unit.Wants = false;
                    return true;
                }
                if (unit.Flight == null)
                {
                    // A stopping worker starts no further request, a read
                    // of a claim under way included. Its last act hands
                    // back whatever was written (LetGo).
                    if (work.Run.Stop) return false;
                    if (!unit.Script.MoveNext())
                    {
                        unit.Script.Dispose();
                        unit.Script = null;
                        return true;
                    }
                    unit.Flight = unit.Script.Current;
                }
                if (!Exchange(work, unit, unit.Flight))
                {
                    Due(work, unit.Flight.DueAt);
                    return false;
                }
                // A write the device took is recorded here and not in the
                // conversation, which a stopping worker never resumes: the
                // worker's last act hands back whatever went out.
                if (Took(unit.Flight)) unit.Written = true;
                unit.Flight = null;
            }
            return true;
        }

        /// <summary>Whether a request was a write the device answered
        /// with 02 or 01.</summary>
        private static bool Took(Ask ask)
            => ask.Outcome == Outcome.Accepted
               && (ask.Request[RazerDirectProtocol.CommandAt] & 0x80) == 0;

        private static int ReplyMs(RazerDirectRow row) => (row.ReplyWaitUs + 999) / 1000;

        /// <summary>
        /// Takes one request toward its outcome: the lock, the report, the
        /// wait, the reply. False while it waits on a slow device, in
        /// which case <see cref="Ask.DueAt"/> says until when.
        ///
        /// <para>A reply that is not the request's yet is read once more
        /// before the request is sent again (uncoil docs/PROTOCOL.md:59-60).
        /// An attempt that ends with no answer, with status 03 or with a
        /// failed call releases the lock and is made again after
        /// <see cref="AttemptGapMs"/>, up to <see cref="Attempts"/> times
        /// (OpenSynapse RazerFeatureTransport.cs:302-378, which tries a
        /// failed HidD_SetFeature again too). 05 ends it at once, as it
        /// does there (351-354). So does 04, which OpenSynapse sends again
        /// (361-372) and uncoil takes as the answer of a receiver with no
        /// mouse behind it (crates/uncoil-hid/src/transport.rs:104-114 and
        /// 247-253): the unit is asked again on the worker's own interval.</para>
        /// </summary>
        private bool Exchange(Work work, Unit unit, Ask ask)
        {
            long now = work.Now;
            var row = unit.Row;
            // A slow device's waits are noted as due and left to a later
            // pass. The worker's last act has no later pass, and waits for
            // each in place.
            bool defer = row.SlowReply && !work.Blocking;
            while (true)
            {
                if (!ask.Sent)
                {
                    if (defer && now < ask.DueAt) return false;
                    if (!ask.Locked)
                    {
                        // A request that already went out without the lock
                        // does not wait for it again on a later attempt.
                        if (_guard.Take(ask.WentUnlocked ? 0 : ask.LockWaitMs)) ask.Locked = true;
                        else if (ask.EvenUnlocked) ask.WentUnlocked = true;
                        else
                        {
                            ask.Outcome = Outcome.Busy;
                            return true;
                        }
                    }
                    ask.Sends++;
                    if (!unit.Io.SetFeature(ask.Request))
                    {
                        ask.HandleFaults++;
                        if (!Again(work, unit, ask)) return true;
                        if (defer) return false;
                        continue;
                    }
                    ask.Sent = true;
                    ask.Reread = false;
                    // The reply is due from when the request went out, and
                    // that can be well after the pass read its clock.
                    long sent = _ticks();
                    unit.LastSentAt = sent;
                    if (defer)
                    {
                        ask.DueAt = sent + ReplyMs(row);
                        return false;
                    }
                    _wait(row.ReplyWaitUs);
                }
                else if (defer && now < ask.DueAt)
                {
                    return false;
                }

                var reply = new byte[RazerDirectProtocol.BufferLength];
                if (!unit.Io.GetFeature(0, reply))
                {
                    ask.HandleFaults++;
                }
                else
                {
                    switch (RazerDirectProtocol.Check(ask.Request, reply))
                    {
                        case RazerReply.Accepted:
                            return Done(ask, Outcome.Accepted, reply);
                        case RazerReply.NotSupported:
                            return Done(ask, Outcome.NotSupported, reply);
                        case RazerReply.Timeout:
                            return Done(ask, Outcome.Timeout, reply);
                        case RazerReply.Failure:
                            ask.Refusals++;
                            break;
                        default:
                            if (ask.Reread) break;
                            ask.Reread = true;
                            if (defer)
                            {
                                ask.DueAt = _ticks() + ReplyMs(row);
                                return false;
                            }
                            _wait(row.ReplyWaitUs);
                            continue;
                    }
                }
                if (!Again(work, unit, ask)) return true;
                if (defer) return false;
            }
        }

        private bool Done(Ask ask, Outcome outcome, byte[] reply)
        {
            ask.Outcome = outcome;
            ask.Reply = reply;
            Unlock(ask);
            return true;
        }

        private void Unlock(Ask ask)
        {
            if (!ask.Locked) return;
            ask.Locked = false;
            _guard.Release();
        }

        /// <summary>Ends one attempt. True when another follows, after the
        /// gap. False when none is left, with the outcome set.</summary>
        private bool Again(Work work, Unit unit, Ask ask)
        {
            ask.Sent = false;
            Unlock(ask);
            if (ask.Sends >= Attempts)
            {
                ask.Outcome = ask.HandleFaults >= Attempts ? Outcome.Dead
                    : ask.Refusals >= Attempts ? Outcome.Refused
                    : Outcome.Silent;
                return false;
            }
            if (unit.Row.SlowReply && !work.Blocking) ask.DueAt = _ticks() + AttemptGapMs;
            else _wait(AttemptGapMs * 1000);
            return true;
        }

        // ─────────────────────────────────────────────
        //  Letting go
        // ─────────────────────────────────────────────

        /// <summary>Drops the conversation in flight with no further
        /// report and no read of a reply still owed.</summary>
        private void Abandon(Unit unit)
        {
            if (unit.Flight != null)
            {
                Unlock(unit.Flight);
                unit.Flight = null;
            }
            if (unit.Script != null)
            {
                try { unit.Script.Dispose(); } catch { }
                unit.Script = null;
            }
        }

        /// <summary>Closes a unit's handle and forgets its claim, on the
        /// crash path's record too. What a claim settled about the device
        /// stays, and so does a hand-back the unit is owed, which the
        /// record keeps. A unit that owes holds no claim, so what the
        /// record has for it then is that hand-back.</summary>
        private void Shut(Unit unit)
        {
            if (unit.Io != null)
            {
                try { unit.Io.Dispose(); } catch { }
                unit.Io = null;
            }
            if (unit.IoPath != null && unit.Owed.Length == 0) RemoveClaimed(unit);
            unit.IoPath = null;
            unit.Claimed = false;
            unit.Written = false;
            unit.Reassert = false;
            unit.HandBack = Array.Empty<byte[]>();
            unit.Rgb = NoColor;
            unit.Held = NoColor;
        }

        private void Release(Unit unit, bool handedBack, bool lost = false)
        {
            bool lit = unit.Claimed;
            Shut(unit);
            if (!lit) return;
            PadForge.Engine.SdlDiagLog.WriteLine(
                lost ? $"PERIPHERAL Razer '{unit.Row.Name}' did not take its hand-back"
                : handedBack ? $"PERIPHERAL Razer '{unit.Row.Name}' lighting handed back"
                : $"PERIPHERAL Razer '{unit.Row.Name}' lighting let go, with nothing to hand back");
        }

        /// <summary>The unit does not answer, or a call on its handle
        /// failed. It stays listed, asleep, with its handle closed and its
        /// claim ended: there is no channel to hand anything back through.
        /// A unit that took a write of its claim and has a hand-back on
        /// its books is owed that hand-back from here on
        /// (<see cref="Unit.Owed"/>). The record already holds it under
        /// the claim's collection, and keeps it. A unit that already owes
        /// has no hand-back on its books, so a report of its debt that
        /// went out before the device fell silent again changes nothing
        /// of what it is owed. A failed call asks for a walk, once, since
        /// the device may have left with no row to say so.</summary>
        private void Sleep(Work work, Unit unit, bool walk)
        {
            if (unit.Written && unit.HandBack.Length > 0)
            {
                unit.Owed = unit.HandBack;
                unit.OwedPath = unit.IoPath;
            }
            Shut(unit);
            unit.RetryAt = _ticks() + RetryMs;
            if (unit.Asleep) return;
            unit.Asleep = true;
            work.Dirty = true;
            if (walk) work.AskWalk = true;
            PadForge.Engine.SdlDiagLog.WriteLine(walk
                ? $"PERIPHERAL Razer '{unit.Row.Name}' handle failed, closing it and asking again later"
                : $"PERIPHERAL Razer '{unit.Row.Name}' does not answer, asking again later");
        }

        /// <summary>Adds a unit to the crash path's record, or replaces
        /// what the record holds for its collection, with the hand-back the
        /// unit holds now.</summary>
        private void NoteClaimed(Unit unit)
        {
            lock (_claimedGate)
            {
                var next = new List<(string, byte[][], WorkerRun)>(_claimed.Length + 1);
                foreach (var entry in _claimed)
                    if (!string.Equals(entry.Path, unit.IoPath, StringComparison.OrdinalIgnoreCase)) next.Add(entry);
                next.Add((unit.IoPath, unit.HandBack, unit.Run));
                Volatile.Write(ref _claimed, next.ToArray());
            }
        }

        /// <summary>Takes a unit off the crash path's record, where the
        /// entry is its own run's. A worker that outlived its run closes
        /// its handles when it comes back, and the collection may be on
        /// the record for its successor by then, which holds it now.</summary>
        private void RemoveClaimed(Unit unit) => Unrecord(unit.IoPath, unit.Run);

        /// <summary>Takes a run's entry for a collection off the crash
        /// path's record, whether a claim made it or it is a hand-back
        /// that was owed.</summary>
        private void Unrecord(string path, WorkerRun run)
        {
            lock (_claimedGate)
            {
                int at = Array.FindIndex(_claimed, e => ReferenceEquals(e.Run, run)
                    && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
                if (at < 0) return;
                var next = new List<(string, byte[][], WorkerRun)>(_claimed);
                next.RemoveAt(at);
                Volatile.Write(ref _claimed, next.ToArray());
            }
        }

        /// <summary>Ends what a unit is owed, with no report, and takes it
        /// off the crash path's record.</summary>
        private void ClearOwed(Unit unit)
        {
            if (unit.Owed.Length == 0) return;
            Unrecord(unit.OwedPath, unit.Run);
            unit.Owed = Array.Empty<byte[]>();
            unit.OwedPath = null;
        }

        /// <summary>Whether the crash path's record holds a collection for
        /// another run, which is the run of a worker that started after
        /// this one's Stop gave up waiting for it.</summary>
        private bool HeldByAnotherRun(string path, WorkerRun run)
        {
            if (path == null) return false;
            foreach (var entry in Volatile.Read(ref _claimed))
                if (!ReferenceEquals(entry.Run, run)
                    && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Sends what a unit is owed with no handle of the
        /// worker's: each report through a handle opened for it, as on the
        /// crash path, behind the lock where it is free within a paint's
        /// wait. No reply is read, so each report gets the product's reply
        /// time before the next goes out. A report that does not go out
        /// ends it: the rest would fare no better.</summary>
        private void PayOnce(Unit unit)
        {
            for (int at = 0; at < unit.Owed.Length; at++)
            {
                if (at > 0) _wait(unit.Row.ReplyWaitUs);
                bool locked = _guard.Take(RazerGuard.PaintWaitMs);
                bool sent;
                try { sent = _setFeatureOnce(unit.OwedPath, unit.Owed[at], OneShotMs); }
                catch { sent = false; }
                finally
                {
                    if (locked) _guard.Release();
                }
                if (!sent) return;
            }
        }

        /// <summary>The worker's last act: every unit it painted gets its
        /// hand-back on the worker's own handle, then every handle is
        /// closed, so stopping the engine leaves no mouse on the last
        /// color it was sent. The worker is ending, so each reply is
        /// waited for in place, a slow device's too, and a request still
        /// in flight gets its reply time and its read before the hand-back
        /// goes out behind it: every request is followed by its read
        /// (openrazer driver/razermouse_driver.c:127-157). A unit that is
        /// owed its hand-back has no handle of the worker's, and gets it
        /// last, through a handle opened for each report
        /// (<see cref="PayOnce"/>). Nothing is sent while a Razer program
        /// runs. The snapshot goes back to "not scanned" unless a
        /// successor's run has begun, whose snapshot it is by then.</summary>
        private void LetGo(Work work)
        {
            bool programRuns = PeripheralOutputs.Presence.RazerSoftwareRunning;
            work.Blocking = true;
            foreach (var unit in work.Units.Values)
            {
                try
                {
                    // A worker that outlived its run sends nothing to a
                    // collection its successor has claimed since: what
                    // that device shows is the successor's to say.
                    bool reach = unit.Io != null && !programRuns && !HeldByAnotherRun(unit.IoPath, unit.Run);
                    if (reach && unit.Flight != null && unit.Flight.Sent)
                    {
                        // A write this reply accepts went out, and is handed
                        // back with the rest.
                        _wait(unit.Row.ReplyWaitUs);
                        var reply = new byte[RazerDirectProtocol.BufferLength];
                        if (unit.Io.GetFeature(0, reply)
                            && RazerDirectProtocol.Check(unit.Flight.Request, reply) == RazerReply.Accepted
                            && (unit.Flight.Request[RazerDirectProtocol.CommandAt] & 0x80) == 0)
                            unit.Written = true;
                    }
                    Abandon(unit);
                    if (reach && unit.Written && unit.HandBack.Length > 0)
                        foreach (var ask in HandBack(work, unit)) Exchange(work, unit, ask);
                }
                catch { }
                finally
                {
                    Shut(unit);
                }
            }
            // Every handle is closed. What is owed goes out now, except to
            // a collection a successor has on record, which is the
            // successor's to speak to.
            if (!programRuns)
            {
                foreach (var unit in work.Units.Values)
                {
                    if (unit.Owed.Length == 0 || HeldByAnotherRun(unit.OwedPath, unit.Run)) continue;
                    try { PayOnce(unit); } catch { }
                }
            }
            lock (_claimedGate)
                Volatile.Write(ref _claimed, Array.FindAll(_claimed, e => !ReferenceEquals(e.Run, work.Run)));
            var current = _run;
            if (current == null || ReferenceEquals(current, work.Run))
                Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
        }

        /// <summary>
        /// The crash path's second step. A crash dialog keeps the process,
        /// and this worker, alive, and a worker left running would paint
        /// its devices again, so the worker stops first and hands back on
        /// its own handles (<see cref="LetGo"/>). One that does not stop
        /// within <see cref="PanicJoinMs"/> claims nothing more, and each
        /// unit still on record gets its hand-back through a handle opened
        /// for that one report, each bounded, until the deadline.
        ///
        /// <para>The lock is waited for no longer than a paint waits and
        /// the report goes out all the same, as uncoil's shutdown write
        /// does. No reply is read. Nothing is sent while a Razer program
        /// runs.</para>
        /// </summary>
        public void ReleaseClaimedNow(long deadline)
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
            int join = (int)Math.Clamp(deadline - _ticks(), 0, PanicJoinMs);
            try { _thread?.Join(join); } catch { }
            (string Path, byte[][] HandBack, WorkerRun Run)[] claimed;
            lock (_claimedGate)
            {
                claimed = _claimed;
                Volatile.Write(ref _claimed, Array.Empty<(string, byte[][], WorkerRun)>());
            }
            if (PeripheralOutputs.Presence.RazerSoftwareRunning) return;
            foreach (var (path, handBack, _) in claimed)
            {
                foreach (var report in handBack)
                {
                    long left = deadline - _ticks();
                    if (left <= 0) return;
                    bool locked = _guard.Take((int)Math.Min(RazerGuard.PaintWaitMs, left));
                    try { _setFeatureOnce(path, report, (int)Math.Clamp(deadline - _ticks(), 1, OneShotMs)); }
                    catch { }
                    finally
                    {
                        if (locked) _guard.Release();
                    }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
            if (_ownsGuard) (_guard as IDisposable)?.Dispose();
        }
    }
}
