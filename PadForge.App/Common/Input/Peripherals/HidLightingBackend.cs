using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The shared HID lighting worker (#557). It lights every device of the
    /// OpenRGB controller families in <see cref="HidLightingFamilies"/>
    /// through the device's own lighting collection, light by light. One
    /// thread owns every collection of those families, the shape of
    /// <see cref="SteelSeriesDirectBackend"/>, and each family supplies only
    /// what differs: which collections are its own, its lights, and its
    /// frames (<see cref="IHidLightingFamily"/>).
    ///
    /// <para>Scan. From the list the host hands over
    /// (<see cref="Rescan"/>), by the families' registrations, with nothing
    /// asked of any device. A unit stays listed for as long as its
    /// collection is in the last list, and its model's light map is
    /// published for the Lighting tab and Map Key Lights from the scan on,
    /// with the names Map Key Lights learned for the model
    /// (<see cref="LearnedKeyMaps"/>).</para>
    ///
    /// <para>Open. Only a unit a row links and a claim resolves on, for
    /// read and write, and then the model reads what its frames need
    /// (<see cref="HidLightingModel.Begin"/>). A collection that opens with
    /// no access right only is one another program holds, and is not
    /// written to.</para>
    ///
    /// <para>Paint. The claim's color and key frame are rendered into one
    /// color per light (<see cref="LightRenderer.Render"/>), and every paint
    /// sends every light, so a paint is its own re-assert. A change goes
    /// out once the model's repaint interval has passed since the paint
    /// before, and an unchanged picture goes out again on its re-assert
    /// interval and once more when Windows resumes from sleep. The unit's
    /// hand-back goes on the crash path's record before its first
    /// frame.</para>
    ///
    /// <para>Hand back. When no claim resolves on the unit or its path
    /// leaves the link table, the model's frames that return the device to
    /// its own lighting go out and the handle closes.</para>
    ///
    /// <para>Fail. A write that fails, a hand-back included, closes the
    /// unit's channel and lists the unit asleep, asks the host for a walk,
    /// and tries the unit again every <see cref="RetryMs"/> by opening it.
    /// A unit that may show a color of ours is owed its hand-back, which
    /// keeps its place on the crash path's record and goes out on the try
    /// that finds the unit open again, before anything else.</para>
    /// </summary>
    internal sealed class HidLightingBackend : IDirectUnitBackend
    {
        internal const int ActiveTickMs = HidppBackend.ActiveTickMs;
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

        /// <summary>One present lighting collection and what the worker
        /// holds of it. Worker thread only.</summary>
        private sealed class Unit
        {
            /// <summary>The interface path, kept exactly as the first walk
            /// gave it: it is the unit's key, compared ordinally.</summary>
            public string Path;
            public ushort VendorId;
            public ushort ProductId;
            public HidLightingModel Model;
            public Guid Container;
            public long ContainerRetryAt;
            public bool Seen;

            /// <summary>The published map: the model's with the learned
            /// names applied.</summary>
            public LightMap Map;

            public bool Asleep;
            public long RetryAt;

            /// <summary>The hand-back the unit is owed and how it goes out,
            /// or null. Its entry stays on the crash path's record
            /// meanwhile.</summary>
            public byte[][] Owed;
            public bool OwedFeature;

            /// <summary>The open channel and its session, or null.
            /// Everything below is of one open and is reset by the next.</summary>
            public IHidReportChannel Io;
            public HidLightingSession Session;

            public bool Recorded;
            public byte[][] RecordedFrames;

            /// <summary>A paint went out, or was tried, so the device may
            /// show a color of ours.</summary>
            public bool Lit;

            /// <summary>The colors last sent, the map they were rendered
            /// for, and when.</summary>
            public int[] Shown;
            public LightMap ShownMap;
            public long PaintAt;

            /// <summary>Windows resumed from sleep: the picture goes out
            /// once more.</summary>
            public bool Reassert;

            public string Label => $"{Model?.Name ?? "unit"} {VendorId:X4}:{ProductId:X4}";
        }

        /// <summary>The units that may show a color of ours, with the
        /// frames that undo it, for the crash path, which cannot wait on
        /// this worker. Swapped whole.</summary>
        private (string Path, byte[][] Frames, bool Feature)[] _recorded = Array.Empty<(string, byte[][], bool)>();

        private readonly IReadOnlyList<IHidLightingFamily> _families;
        private readonly Func<string, IHidReportChannel> _open;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<long> _ticks;
        private readonly Func<string, byte[], int, bool> _setFeatureOnce;
        private readonly Func<string, byte[], int, bool> _writeOutputOnce;
        private readonly int _activeTickMs;
        private readonly int _idleTickMs;
        private readonly int _predecessorJoinMs;
        private readonly int _stopJoinMs;
        private readonly AutoResetEvent _wake = new(false);

        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;
        private IReadOnlyList<HidCollectionInfo> _present;
        private Thread _thread;
        private Thread _worker;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;
        private int _waitMs;

        private readonly bool _powerEvents;
        private int _resumed;

        /// <summary>The newest worker thread of any instance, which the
        /// next one waits for. Static because the collections it holds are
        /// the machine's.</summary>
        private static Thread s_lastWorker;

        public HidLightingBackend()
            : this(null, null, null, null, null, null, ActiveTickMs, IdleTickMs, powerEvents: true) { }

        /// <summary>Test seam: the families, the channel opener, the
        /// container lookup, the clock, the crash path's two one-shot
        /// writers, the two ticks, the wait for a predecessor worker and
        /// Stop's wait. The opener and the one-shot writers are the worker's
        /// only ways to a device, and their defaults open nothing in the
        /// test host (<see cref="RealHidAccess"/>).</summary>
        internal HidLightingBackend(IReadOnlyList<IHidLightingFamily> families, Func<string, IHidReportChannel> open,
            Func<string, Guid> containerOf, Func<long> ticks, Func<string, byte[], int, bool> setFeatureOnce,
            Func<string, byte[], int, bool> writeOutputOnce, int activeTickMs, int idleTickMs,
            int predecessorJoinMs = PredecessorJoinMs, int stopJoinMs = StopJoinMs, bool powerEvents = false)
        {
            _powerEvents = powerEvents;
            _families = families ?? HidLightingFamilies.All;
            _open = open ?? HidReportChannel.Open;
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _ticks = ticks ?? (() => Environment.TickCount64);
            _setFeatureOnce = setFeatureOnce ?? HidReportChannel.SetFeatureOnce;
            _writeOutputOnce = writeOutputOnce ?? HidReportChannel.WriteOutputOnce;
            _activeTickMs = activeTickMs;
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
        internal (string Path, byte[][] Frames, bool Feature)[] Recorded => Volatile.Read(ref _recorded);

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _thread != null) return;
            var run = new WorkerRun();
            _run = run;
            Volatile.Write(ref _present, null);
            Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralHidLighting" };
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
        /// on a handle of its own.</summary>
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
            foreach (var (path, frames, feature) in Interlocked.Exchange(ref _recorded, Array.Empty<(string, byte[][], bool)>()))
            {
                foreach (byte[] frame in frames)
                {
                    left = deadline - _ticks();
                    if (left <= 0) return;
                    try { WriteOnce(path, frame, feature, (int)Math.Min(left, OneShotMs)); } catch { }
                }
            }
        }

        private bool WriteOnce(string path, byte[] frame, bool feature, int timeoutMs)
            => feature ? _setFeatureOnce(path, frame, timeoutMs) : _writeOutputOnce(path, frame, timeoutMs);

        private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) NoteResume();
        }

        private void OnLearned(object sender, EventArgs e) => Wake();

        /// <summary>Windows resumed from sleep: every open unit gets its
        /// picture once more, since a device that lost power in between
        /// shows its stored lighting again while its collection never left
        /// the list.</summary>
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
                    bool resumed = Interlocked.Exchange(ref _resumed, 0) != 0;
                    foreach (var unit in units)
                    {
                        if (run.Stop) break;
                        if (resumed && unit.Io != null) unit.Reassert = true;
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
                foreach (var unit in units)
                {
                    if (unit.Io == null) continue;
                    bool sent = false;
                    try { sent = Release(unit); } catch { }
                    // A hand-back that did not go out on the worker's
                    // channel is owed, and goes out below on a handle of
                    // its own.
                    if (!sent && unit.Lit && unit.Session != null)
                    {
                        unit.Owed = unit.Session.HandBack();
                        unit.OwedFeature = unit.Session.Feature;
                    }
                    Close(unit);
                }
                // A unit owed its hand-back has no handle of the worker's,
                // and gets it now through a one-shot write, as on the crash
                // path. Not from a worker that outlived its Stop into the
                // next run, whose worker has the collections by then.
                var successor = _run;
                bool ownRun = successor == null || ReferenceEquals(successor, run);
                foreach (var unit in units)
                {
                    if (unit.Owed == null) continue;
                    if (ownRun)
                    {
                        foreach (var frame in unit.Owed)
                        {
                            try { WriteOnce(unit.Path, frame, unit.OwedFeature, OneShotMs); } catch { }
                        }
                    }
                    ClearOwed(unit);
                }
                var current = _run;
                if (current == null || ReferenceEquals(current, run))
                {
                    foreach (var unit in units) PeripheralLightMaps.Remove(PathOf(unit));
                    Volatile.Write(ref _recorded, Array.Empty<(string, byte[][], bool)>());
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
            foreach (var collection in present)
            {
                var model = HidLightingFamilies.Match(collection, _families);
                if (model == null) continue;
                Unit unit = null;
                foreach (var known in units)
                {
                    if (!string.Equals(known.Path, collection.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    unit = known;
                    break;
                }
                if (unit == null)
                {
                    unit = new Unit
                    {
                        Path = collection.Path, VendorId = collection.VendorId, ProductId = collection.ProductId,
                        Model = model,
                    };
                    LookUpContainer(unit, now);
                    PublishMap(unit);
                    units.Add(unit);
                    changed = true;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL HidLighting found {unit.Label} ({model.Family?.Name}), {model.Map.Lights.Count} lights");
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
                // The device is gone, so there is nothing to hand back to.
                ClearOwed(unit);
                Close(unit);
                PeripheralLightMaps.Remove(PathOf(unit));
                units.RemoveAt(i);
                changed = true;
            }
            return changed;
        }

        /// <summary>Publishes the unit's map with the names Map Key Lights
        /// learned for the model.</summary>
        private static void PublishMap(Unit unit)
        {
            var map = LearnedKeyMaps.Apply(unit.Model.Map) ?? unit.Model.Map;
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
                    Asleep = unit.Asleep,
                    LitBesideWindows = unit.Model.LitBesideWindows,
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
                    if (unit.Owed != null ? PayBack(unit) : Answers(unit))
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
                if (unit.Io == null) return;
                if (Release(unit)) Close(unit);
                else Fail(unit, now, "hand-back failed", ref changed, ref failed, ref due);
                return;
            }

            // A stopping worker opens nothing and paints nothing more. Its
            // finally hands back what it holds.
            if (run.Stop) return;
            if (unit.Io == null && !Open(unit))
            {
                Fail(unit, now, "did not open for read and write or did not answer", ref changed, ref failed, ref due);
                return;
            }
            if (!Paint(unit, rgb, ruler.Keys, now, ref due))
                Fail(unit, now, "write failed", ref changed, ref failed, ref due);
        }

        /// <summary>Whether an asleep unit can be opened for read and write
        /// again. Nothing is sent.</summary>
        private bool Answers(Unit unit)
        {
            IHidReportChannel io = null;
            try { io = _open(unit.Path); } catch { }
            bool answers = io != null && !io.FeatureOnly;
            try { io?.Dispose(); } catch { }
            return answers;
        }

        /// <summary>The try of an asleep unit that is owed its hand-back:
        /// the collection is opened as a claim opens it and sent the frames.
        /// True when all of them went out, which settles the debt and says
        /// the unit answers again.</summary>
        private bool PayBack(Unit unit)
        {
            IHidReportChannel io = null;
            try { io = _open(unit.Path); } catch { }
            bool sent = io != null && !io.FeatureOnly;
            if (sent)
            {
                // No frame goes out behind one that failed.
                foreach (var frame in unit.Owed) sent = sent && Send(io, frame, unit.OwedFeature);
            }
            try { io?.Dispose(); } catch { }
            if (!sent) return false;
            ClearOwed(unit);
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} lighting handed back, owed since it failed");
            return true;
        }

        /// <summary>Opens the unit's collection for read and write and
        /// starts its session. False when it did not open that way or the
        /// model's first read went unanswered.</summary>
        private bool Open(Unit unit)
        {
            IHidReportChannel io = null;
            try { io = _open(unit.Path); } catch { }
            if (io == null || io.FeatureOnly)
            {
                try { io?.Dispose(); } catch { }
                return false;
            }
            HidLightingSession session = null;
            try { session = unit.Model.Begin?.Invoke(io); } catch { }
            if (session == null)
            {
                try { io.Dispose(); } catch { }
                return false;
            }
            unit.Io = io;
            unit.Session = session;
            unit.Lit = false;
            unit.Shown = null;
            unit.ShownMap = null;
            unit.Reassert = false;
            return true;
        }

        /// <summary>One pass over an open, wanted unit. False when a write
        /// failed.</summary>
        private bool Paint(Unit unit, int rgb, KeyFrame keys, long now, ref long due)
        {
            var model = unit.Model;
            var map = unit.Map ?? model.Map;
            var colors = LightRenderer.Render(map, rgb, keys);
            bool first = unit.Shown == null || !ReferenceEquals(unit.ShownMap, map);
            bool fresh = !first && LightRenderer.Same(colors, unit.Shown);
            long repaintAt = unit.PaintAt + model.RepaintMs;
            long reassertAt = unit.PaintAt + model.ReassertMs;
            if (!first && !unit.Reassert && now < reassertAt && (fresh || now < repaintAt))
            {
                Soonest(ref due, fresh ? reassertAt : repaintAt);
                return true;
            }

            // The crash path knows the hand-back before the first frame
            // goes out, so no moment exists in which the device shows a
            // color of ours and nothing can undo it (HidppBackend.PaintLighting).
            if (!unit.Recorded) Record(unit);
            unit.Lit = true;
            foreach (var frame in unit.Session.Paint(map.Lights, colors))
                if (!Send(unit.Io, frame, unit.Session.Feature)) return false;
            unit.Shown = colors;
            unit.ShownMap = map;
            unit.PaintAt = _ticks();
            unit.Reassert = false;
            if (first)
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} lit, {colors.Length} lights");
            Soonest(ref due, unit.PaintAt + model.ReassertMs);
            return true;
        }

        private static bool Send(IHidReportChannel io, byte[] frame, bool feature)
        {
            try { return io != null && (feature ? io.SetFeature(frame) : io.WriteOutput(frame)); }
            catch { return false; }
        }

        /// <summary>Sends the hand-back of a unit a paint went to. True
        /// when every frame went out, or none was owed.</summary>
        private bool Release(Unit unit)
        {
            if (!unit.Lit || unit.Session == null) return true;
            foreach (var frame in unit.Session.HandBack())
                if (!Send(unit.Io, frame, unit.Session.Feature)) return false;
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} lighting handed back");
            return true;
        }

        /// <summary>A write, an open or the session's first read failed. The
        /// channel closes and the unit is listed asleep, never taken off the
        /// list. A unit a paint went to is owed its hand-back from here
        /// on.</summary>
        private void Fail(Unit unit, long now, string why, ref bool changed, ref bool failed, ref long due)
        {
            if (unit.Lit && unit.Session != null)
            {
                var handBack = unit.Session.HandBack();
                if (handBack.Length > 0)
                {
                    unit.Owed = handBack;
                    unit.OwedFeature = unit.Session.Feature;
                }
            }
            Close(unit);
            unit.Asleep = true;
            unit.RetryAt = now + RetryMs;
            changed = true;
            failed = true;
            Soonest(ref due, unit.RetryAt);
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HidLighting {unit.Label} {why}, listed asleep");
        }

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
            unit.Session = null;
            unit.Lit = false;
            unit.Shown = null;
            unit.ShownMap = null;
            try { io?.Dispose(); } catch { }
        }

        // ─────────────────────────────────────────────
        //  The crash path's record
        // ─────────────────────────────────────────────

        private void Record(Unit unit)
        {
            var frames = unit.Session.HandBack();
            if (frames.Length == 0) return;
            var current = Volatile.Read(ref _recorded);
            var next = new (string, byte[][], bool)[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = (unit.Path, frames, unit.Session.Feature);
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
            var next = new List<(string, byte[][], bool)>(current.Length);
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
