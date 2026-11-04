using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Runs the peripheral outputs while the engine runs (#494): the HID++,
    /// GameSense, Razer Chroma and LED SDK workers, the direct backends
    /// (<see cref="IDirectUnitBackend"/>), the Razer Sensa worker while its
    /// row is assigned or Identify buzzes it, and the linker pass that ties
    /// device rows to paths.
    ///
    /// <para>Nothing here opens a vendor SDK for a device nobody assigned,
    /// except for the length of an Identify train on that device's row
    /// (<see cref="PeripheralOutputs.BeginIdentify(Guid, int)"/>): the linker
    /// reads presence from the registry, files and process names, and each
    /// backend reaches its vendor only while a path it serves is claimed.
    /// Assignment drives every output, the way it drives a gamepad's.</para>
    /// </summary>
    internal sealed class PeripheralOutputHost : IDisposable
    {
        internal const int LinkMs = 500;
        internal const int PresenceMs = 5000;

        /// <summary>How often the present HID collections are walked for
        /// the direct backends when nothing asked for a walk sooner, the
        /// HID++ worker's own slow rescan (HidppBackend.RescanMs).</summary>
        internal const int RescanMs = 30000;

        /// <summary>The least time between two walks when one is asked
        /// for: by the owner, by a HID interface that arrived, or by a
        /// backend (<see cref="RequestRescan"/>). A device with several
        /// collections raises one arrival for each, a few milliseconds
        /// apart, and several backends can ask about one device. One
        /// walk, a second after the last, answers all of them and sees
        /// every collection of the device.</summary>
        internal const int RescanFloorMs = 1000;

        /// <summary>How long the crash path may take to hand every directly
        /// lit device back, all backends together
        /// (<see cref="ReleaseLightingNow"/>).</summary>
        internal const int PanicBudgetMs = 750;

        /// <summary>How long a Sensa worker that ended on its own waits
        /// before the next one, the worker's own provider retry.</summary>
        internal const int SensaRestartMs = 30000;

        /// <summary>How long Stop waits for a link pass to end.</summary>
        internal const int StopJoinMs = 3000;

        private readonly HidppBackend _hidpp;
        private readonly GameSenseBackend _gameSense;
        private readonly ChromaBackend _chroma;
        private readonly LedSdkBackend _ledSdk;
        private readonly IReadOnlyList<IDirectUnitBackend> _direct;

        /// <summary>Each direct backend's family, by its place in
        /// <see cref="_direct"/>, read once when the host was made.</summary>
        private readonly OutputFamily[] _familyOf;

        /// <summary>The backends whose snapshot read has already been
        /// logged as failing, by their place in <see cref="_direct"/>, so a
        /// backend that fails on every pass is named once. Link thread
        /// only.</summary>
        private readonly bool[] _snapshotFaulted;
        private readonly OutputFamily[] _directFamilies;
        private readonly Func<SensaHapticsService> _sensaFactory;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<PeripheralPresence> _readPresence;
        private readonly Func<PeripheralPresence, PeripheralPresence> _readRunning;

        /// <summary>This host's own look at the running processes, behind
        /// both reads above unless a test scripts them.</summary>
        private readonly VendorProgramWatch _programs = new();
        private readonly Func<IReadOnlyList<HidCollectionInfo>> _enumerate;
        private readonly Func<long> _ticks;
        private readonly int _stopJoinMs;
        private readonly int _linkMs;
        private readonly Dictionary<string, Guid> _containers = new(StringComparer.OrdinalIgnoreCase);
        private readonly AutoResetEvent _wake = new(false);

        /// <summary>When each lit unit no row reaches was first seen
        /// that way (<see cref="PeripheralLinker.HeldOrphans"/>). Link
        /// thread only.</summary>
        private readonly Dictionary<OutputPath, long> _orphanSince = new();

        /// <summary>When each row was last matched by a listed unit of its
        /// own, for the hold a row gets after its last unit left
        /// (<see cref="PeripheralLinker.HeldRows"/>). Link thread only.</summary>
        private readonly Dictionary<Guid, long> _unitLastSeen = new();

        /// <summary>1 when a walk of the present collections was asked for
        /// since the last one: the owner said a device row came or went, a
        /// HID interface arrived, or a backend asked. It stays 1 until the
        /// walk is made, so an ask inside the floor is answered once the
        /// floor has passed.</summary>
        private int _rescanAsked;

        /// <summary>Held by a pass while it publishes or starts the Sensa
        /// worker, and by Stop while it marks the pass's run stopped. A pass
        /// that Stop's join gave up on then either finished its publish
        /// before Stop's teardown, which overwrites it, or sees the stop and
        /// publishes nothing.</summary>
        private readonly object _gate = new();
        private SensaHapticsService _sensa;
        private Action<SensaServiceState> _sensaReport;
        private long _sensaRestartAt;
        private Thread _thread;
        private WorkerRun _run;
        private int _disposed;
        private int _passes;

        /// <summary>The slots <see cref="PeripheralOutputs.SlotLive"/>
        /// answered true for at the last pass, one bit each, or -1 before the
        /// first. Link thread only.</summary>
        private long _liveSlots = -1;

        /// <summary>Raised from the host thread when a row's recorded
        /// outputs changed, so the owner saves the settings and refreshes the
        /// device list. The tabs refresh on <see cref="PeripheralOutputs.LinksChanged"/>
        /// and <see cref="PeripheralOutputs.StatusChanged"/>, raised first.</summary>
        public event Action CapabilitiesChanged;

        public PeripheralOutputHost()
            : this(new HidppBackend(), new GameSenseBackend(), () => new SensaHapticsService(),
                new ChromaBackend(), new LedSdkBackend(), direct: DirectBackends.Create()) { }

        /// <summary>Whether a slot still holds a virtual controller, set by
        /// the owner and published while the host runs
        /// (<see cref="PeripheralOutputs.SlotHasController"/>), where
        /// <see cref="PeripheralOutputs.SlotLive"/> reads it. A reorder moves
        /// the controllers first and rebuilds the slots' effects dispatchers
        /// after, one slot at a time, so a slot can be without a dispatcher
        /// for a second or two while its controller is in place. Null treats
        /// every slot as without one.</summary>
        public Func<int, bool> SlotHasController { get; set; }

        /// <summary>Test seam: the backends, the Sensa worker's factory, the
        /// container lookup, the presence read, Stop's join and the wait
        /// between timed link passes, then the direct backends, the walk of
        /// the present HID collections they scan from, the read of the
        /// running vendor programs alone, and the clock every other interval
        /// here is measured on, so a bench drives the host against scripted
        /// parts and a time it sets itself. A host given a presence read and
        /// no read of its own for the running programs takes them from that
        /// same read, so a scripted host never looks at the PC's real
        /// processes.</summary>
        internal PeripheralOutputHost(HidppBackend hidpp, GameSenseBackend gameSense, Func<SensaHapticsService> sensa,
            ChromaBackend chroma = null, LedSdkBackend ledSdk = null, Func<string, Guid> containerOf = null,
            Func<PeripheralPresence> readPresence = null, int stopJoinMs = StopJoinMs, int linkMs = LinkMs,
            IReadOnlyList<IDirectUnitBackend> direct = null,
            Func<IReadOnlyList<HidCollectionInfo>> enumerate = null,
            Func<PeripheralPresence, PeripheralPresence> readRunning = null,
            Func<long> ticks = null)
        {
            _hidpp = hidpp;
            _gameSense = gameSense;
            _sensaFactory = sensa;
            _chroma = chroma;
            _ledSdk = ledSdk;
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            // The full read is handed the presence published before it,
            // whose running flags stand when the processes cannot be listed.
            _readPresence = readPresence ?? (() => _programs.Read(PeripheralOutputs.Presence));
            _readRunning = readRunning ?? (readPresence == null
                ? _programs.ReadRunning
                : current => PeripheralPresence.CopyRunning(current, readPresence()));
            _stopJoinMs = stopJoinMs;
            _linkMs = linkMs;
            // Each backend's family is read here, once, and kept: a backend
            // that cannot name its family is left out, so a getter that
            // throws costs that backend alone and never the host.
            var backends = new List<IDirectUnitBackend>();
            var served = new List<OutputFamily>();
            var families = new List<OutputFamily>();
            foreach (var backend in direct ?? Array.Empty<IDirectUnitBackend>())
            {
                if (backend == null) continue;
                OutputFamily family;
                try { family = backend.Family; }
                catch (Exception ex)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL {backend.GetType().Name} left out, its family fault: {ex.GetType().Name}");
                    continue;
                }
                backends.Add(backend);
                served.Add(family);
                if (!families.Contains(family)) families.Add(family);
            }
            _direct = backends;
            _familyOf = served.ToArray();
            _snapshotFaulted = new bool[backends.Count];
            _directFamilies = families.ToArray();
            _enumerate = enumerate ?? HidCollectionRuntime.Enumerate;
            _ticks = ticks ?? (() => Environment.TickCount64);
        }

        /// <summary>Test seam: runs on the link thread right before a pass
        /// takes the gate, with "publish" before its publish and "sensa"
        /// before it starts the Sensa worker, so a bench can hold a pass there
        /// while Stop runs.</summary>
        internal static Action<string> BeforeGate;

        /// <summary>Link passes that have ended, so a bench can tell the loop
        /// is at rest between two.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        public void Start()
        {
            if (_thread != null) return;
            var run = new WorkerRun();
            _run = run;
            _liveSlots = -1;
            PeripheralOutputs.SlotHasController = SlotHasController;
            PeripheralOutputs.SetRunning(true);
            // Identify on the Razer Sensa row starts its worker at once, not
            // at the next pass.
            PeripheralOutputs.IdentifyChanged += Nudge;
            _hidpp.SnapshotChanged += Nudge;
            _hidpp.Start();
            _gameSense.Start();
            _chroma?.Start();
            _ledSdk?.Start();
            // Published before the first pass, so the tabs read from the
            // start which families run and that none has scanned yet. No
            // pass of an earlier run touches the orphans' clock or the
            // rows' any more: Stop marked that run stopped under the gate
            // both are kept under.
            _orphanSince.Clear();
            _unitLastSeen.Clear();
            Interlocked.Exchange(ref _rescanAsked, 0);
            PeripheralOutputs.Direct = new DirectSnapshot(Array.Empty<DirectUnit>(), _directFamilies,
                scanned: _direct.Count == 0);
            for (int i = 0; i < _direct.Count; i++)
            {
                // One family that cannot start keeps neither the others nor
                // the link pass from running. It never scans, so every
                // device row keeps the outputs it had on record.
                try
                {
                    _direct[i].SnapshotChanged += Nudge;
                    _direct[i].RescanRequested += RequestRescan;
                    _direct[i].Start();
                }
                catch (Exception ex)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL {_familyOf[i]} start fault: {ex.GetType().Name}");
                }
            }
            PeripheralOutputs.Presence = _readPresence();
            _thread = new Thread(() => Loop(run)) { IsBackground = true, Name = "PeripheralLink" };
            _thread.Start();
        }

        /// <summary>Brings the next link pass forward.</summary>
        public void Nudge()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        /// <summary>A device row came or went: the next link pass comes
        /// forward, and the present HID collections are walked again, on
        /// that pass or on the first one past the floor
        /// (<see cref="RescanFloorMs"/>), so a direct backend finds a
        /// device that was just plugged in without waiting for its slow
        /// rescan.</summary>
        public void DevicesChanged()
        {
            Interlocked.Exchange(ref _rescanAsked, 1);
            Nudge();
        }

        /// <summary>Asks for a walk of the present HID collections, handed
        /// to every direct backend, on the first link pass that is at least
        /// <see cref="RescanFloorMs"/> after the walk before. Each backend's
        /// <see cref="IDirectUnitBackend.RescanRequested"/> comes here, and
        /// so does Windows' notice of a HID interface arriving, which is
        /// how a lit device with no row of its own, a light strip or a
        /// headset, is found when it is plugged in. Any thread, and it
        /// never blocks. The first ask brings the next pass forward. One
        /// made while a walk is still owed brings nothing forward, so a
        /// backend that asks over and over inside the floor costs no pass.
        /// With no direct backend nothing would read the walk, so the ask
        /// is dropped and no pass is brought forward.</summary>
        public void RequestRescan()
        {
            if (_direct.Count == 0) return;
            if (Interlocked.Exchange(ref _rescanAsked, 1) == 0) Nudge();
        }

        public void Stop()
        {
            if (_thread != null)
            {
                // Under the gate: a pass already publishing finishes first,
                // and one that has not reached its publish sees the stop and
                // publishes nothing, so nothing a pass does lands after the
                // teardown below.
                lock (_gate) _run.Stop = true;
                Nudge();
                try { _thread.Join(_stopJoinMs); } catch { }
                _thread = null;
                _run = null;
            }
            PeripheralOutputs.IdentifyChanged -= Nudge;
            StopSensa();
            // Last started, first stopped. Each hands its devices back
            // before it closes them.
            for (int i = _direct.Count - 1; i >= 0; i--)
            {
                try { _direct[i].SnapshotChanged -= Nudge; } catch { }
                try { _direct[i].RescanRequested -= RequestRescan; } catch { }
                try { _direct[i].Stop(); } catch { }
            }
            _ledSdk?.Stop();
            _chroma?.Stop();
            _gameSense.Stop();
            _hidpp.SnapshotChanged -= Nudge;
            _hidpp.Stop();
            PeripheralOutputs.SetRunning(false);
            PeripheralOutputs.SlotHasController = null;
            PeripheralOutputs.ClearClaims();
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            PeripheralOutputs.Hidpp = HidppSnapshot.Empty;
            PeripheralOutputs.Direct = DirectSnapshot.Empty;
            PeripheralOutputs.UnitRows = Array.Empty<DirectUnit>();
        }

        private void Loop(WorkerRun run)
        {
            long nextPresence = _ticks() + PresenceMs;
            // The first pass walks the collections, so each direct backend
            // has something to scan from the start. No walk has been made
            // yet, so no floor holds an asked walk back either.
            long nextRescan = long.MinValue;
            long nextAsked = long.MinValue;
            while (!run.Stop)
            {
                try
                {
                    long now = _ticks();
                    if (now >= nextPresence)
                    {
                        PublishPresence(run, _readPresence());
                        nextPresence = now + PresenceMs;
                    }
                    else if (PeripheralOutputs.WatchesVendorPrograms(now))
                    {
                        // A device its vendor's program would take is lit
                        // on its own, by a claim or by a Set Chroma Color,
                        // so a program that starts must be seen now and not
                        // at the next presence read: until the pass
                        // withdraws the path, the device's worker keeps
                        // writing into the program's start. The read takes
                        // a process snapshot only when the list of process
                        // IDs moved (VendorProgramWatch), and a device no
                        // program can take costs nothing more than the read
                        // above.
                        PublishPresence(run, _readRunning(PeripheralOutputs.Presence));
                    }
                    RescanDirect(run, now, ref nextRescan, ref nextAsked);
                    Link(run);
                    SensaLifecycle(run);
                }
                catch (Exception ex)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL link pass fault: " + ex.GetType().Name);
                }
                Interlocked.Increment(ref _passes);
                // A pass held up past Stop's join, a Sensa worker slow to
                // stop, can come back after Dispose released the handle.
                try { _wake.WaitOne(_linkMs); }
                catch (ObjectDisposedException) { return; }
            }
        }

        /// <summary>Publishes what a pass read of the vendor software,
        /// unless its run was stopped while it read. A read of the running
        /// processes can be what held a pass up past Stop's join, and its
        /// answer is then as old as the wait: published after the next
        /// start, it would replace that start's own read and could open a
        /// direct path beside a program that runs. Stop marks the run under
        /// this gate, so the read lands before the stop or not at all.</summary>
        private void PublishPresence(WorkerRun run, PeripheralPresence read)
        {
            lock (_gate)
            {
                if (!run.Stop) PeripheralOutputs.Presence = read;
            }
        }

        /// <summary>Hands every direct backend the HID collections present
        /// now, from one walk: on the first pass, every
        /// <see cref="RescanMs"/>, and sooner when a walk was asked for
        /// (<see cref="RequestRescan"/>), though never within
        /// <see cref="RescanFloorMs"/> of the walk before. An ask inside
        /// the floor is kept and answered by the first pass after it. A
        /// walk that fails hands over nothing and is made again on the next
        /// pass, whatever the floor, so a failed SetupAPI call never reads
        /// as every device unplugged. With no direct backend there is no
        /// walk at all, and a pass whose run was stopped makes none and
        /// calls no backend: Stop does not wait for a slow pass past its
        /// join, and the backends it stopped are owed no more calls.</summary>
        private void RescanDirect(WorkerRun run, long now, ref long nextRescan, ref long nextAsked)
        {
            if (_direct.Count == 0 || run.Stop) return;
            if (now < nextRescan && (Volatile.Read(ref _rescanAsked) == 0 || now < nextAsked)) return;
            Interlocked.Exchange(ref _rescanAsked, 0);
            IReadOnlyList<HidCollectionInfo> present = null;
            try { present = _enumerate(); }
            catch { }
            if (present == null)
            {
                nextRescan = long.MinValue;
                return;
            }
            nextRescan = now + RescanMs;
            nextAsked = now + RescanFloorMs;
            for (int i = 0; i < _direct.Count; i++)
            {
                // The walk itself can be what held the pass up, so the
                // stop is read again before each hand-over.
                if (run.Stop) return;
                try { _direct[i].Rescan(present); }
                catch (Exception ex)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL {_familyOf[i]} rescan fault: {ex.GetType().Name}");
                }
            }
        }

        /// <summary>The interface path whose container a row's vendor channel
        /// shares. An analog keyboard row's own path is synthetic, so it gives
        /// the HID collection it reads.</summary>
        internal static string ContainerPath(Engine.Data.UserDevice ud)
            => ud.Device is AnalogKeyboardDevice analog ? analog.HidPath : ud.DevicePath;

        /// <summary>A row path's container ID, from the cache or from
        /// <paramref name="lookup"/>, a configuration manager round trip that
        /// answers Guid.Empty when it fails. A failed lookup is not kept, so
        /// the next pass asks again: kept, it would leave the row unmatched to
        /// its vendor channel for the rest of the engine run, a device that
        /// comes back on the same path included.</summary>
        internal static Guid ResolveContainer(Dictionary<string, Guid> cache, string path, Func<string, Guid> lookup)
        {
            if (cache.TryGetValue(path, out var container)) return container;
            try { container = lookup(path); }
            catch { container = Guid.Empty; }
            if (container != Guid.Empty) cache[path] = container;
            return container;
        }

        /// <summary>Forgets the container of every path no row used this
        /// pass, so a device that leaves and comes back is looked up again,
        /// the rule HidppBackend.Scan keeps for its own paths.</summary>
        internal static void ForgetContainers(Dictionary<string, Guid> cache, ISet<string> used)
        {
            List<string> gone = null;
            foreach (var path in cache.Keys)
                if (!used.Contains(path)) (gone ??= new List<string>()).Add(path);
            if (gone == null) return;
            foreach (var path in gone) cache.Remove(path);
        }

        /// <summary>One direct backend's snapshot for this pass, read once.
        /// A read that throws, or that gives no snapshot, counts as not
        /// scanned with no units: every device row then keeps the outputs it
        /// had on record, where an empty scanned list would clear them, and
        /// the pass goes on for every other family. The fault is logged the
        /// first time a backend shows it.</summary>
        private DirectBackendSnapshot ReadSnapshot(int index)
        {
            string fault;
            try
            {
                var snapshot = _direct[index].Snapshot;
                if (snapshot != null) return snapshot;
                fault = "gave no snapshot";
            }
            catch (Exception ex)
            {
                fault = "snapshot fault: " + ex.GetType().Name;
            }
            if (!_snapshotFaulted[index])
            {
                _snapshotFaulted[index] = true;
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL {_familyOf[index]} {fault}");
            }
            return DirectBackendSnapshot.NotScanned;
        }

        /// <summary>One pass: snapshot the rows, link them, publish a change,
        /// keep each row's recorded outputs current, and name the lit units
        /// no row reaches, which get rows of their own.</summary>
        private void Link(WorkerRun run)
        {
            // A pass Stop's join gave up on reads no backend: they are
            // stopped, and nothing it linked could be published anyway.
            if (run.Stop) return;
            var presence = PeripheralOutputs.Presence;
            var hidpp = _hidpp.Snapshot;
            bool charge = ChargesChanged(PeripheralOutputs.Hidpp, hidpp);

            // Each direct backend's snapshot, read once, so its units and
            // its scan state are of one scan, and the table, the records and
            // the snapshot the tabs read all come from one list.
            var found = new List<DirectUnit>();
            bool directScanned = true;
            for (int i = 0; i < _direct.Count; i++)
            {
                var theirs = ReadSnapshot(i);
                directScanned &= theirs.Scanned;
                foreach (var unit in theirs.Units)
                    if (unit != null) found.Add(unit);
            }
            DirectUnit[] units = found.ToArray();

            var rows = new List<LinkRow>();
            var devices = SettingsManager.UserDevices;
            if (devices == null) return;
            var users = new List<Engine.Data.UserDevice>();
            var paths = new List<string>();
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud == null || !ud.IsOnline) continue;
                    if (ud.Device is PeripheralOutputRow vendorRow)
                    {
                        rows.Add(new LinkRow(ud.InstanceGuid, ud.CapType, ud.VendorId, ud.ProdId, Guid.Empty, vendorRow.Kind));
                        users.Add(ud);
                        paths.Add(null);
                        continue;
                    }
                    if (ud.Device is PeripheralUnitRow unitRow)
                    {
                        rows.Add(new LinkRow(ud.InstanceGuid, ud.CapType, ud.VendorId, ud.ProdId, Guid.Empty, null)
                        {
                            Unit = unitRow.Unit,
                        });
                        users.Add(ud);
                        paths.Add(null);
                        continue;
                    }
                    if (!PeripheralLinker.IsLinkable(ud.CapType)) continue;
                    // A device forwarded from another PC plays its rumble
                    // there, so only that PC links it to its vendor channel.
                    if (RemoteLinkOutputRouter.IsPeerPath(ud.DevicePath)) continue;
                    // A keyboard or mouse of any vendor can carry a HID
                    // LampArray collection, so while a direct backend runs
                    // every such row is read. With none, only the vendors a
                    // shared family or the HID++ worker reaches can link.
                    if (_direct.Count == 0 && ud.VendorId != PeripheralLinker.LogitechVid
                        && ud.VendorId != PeripheralLinker.RazerVid && ud.VendorId != PeripheralLinker.SteelSeriesVid)
                        continue;
                    rows.Add(new LinkRow(ud.InstanceGuid, ud.CapType, ud.VendorId, ud.ProdId,
                        Guid.Empty, null));
                    users.Add(ud);
                    paths.Add(ContainerPath(ud));
                }
            }
            // Container IDs outside the device lock: a configuration manager
            // call is a kernel round trip, once per path that answers it.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].VendorRow != null) continue;
                string path = paths[i];
                if (string.IsNullOrEmpty(path)) continue;
                used.Add(path);
                rows[i] = rows[i] with { Container = ResolveContainer(_containers, path, _containerOf) };
            }
            ForgetContainers(_containers, used);

            var inputs = new LinkInputs(hidpp, presence) { Units = units, DirectScanned = directScanned };
            var table = PeripheralLinker.Build(rows, inputs);
            // The lit units no row reaches, which get rows of their own once
            // they have gone unreached for long enough.
            var orphans = PeripheralLinker.Orphans(rows, inputs);
            long now = _ticks();

            // A device unassigned from a slot keeps no claim from it, and a
            // slot that lights nothing keeps none. Read after the device lock
            // is released, never nested inside it.
            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                var assigned = new HashSet<(Guid Device, int Slot)>();
                lock (settings.SyncRoot)
                {
                    foreach (var us in settings.Items)
                        if (us != null && us.MapTo >= 0 && us.InstanceGuid != Guid.Empty)
                            assigned.Add((us.InstanceGuid, us.MapTo));
                    // Pruned under the lock the snapshot was read under, so an
                    // assignment lands either before the snapshot, which keeps
                    // its claim, or after the prune, before which no
                    // dispatcher can have claimed for it. A slot keeps its
                    // claims while a dispatcher lights it or a controller still
                    // sits there (PeripheralOutputs.SlotLive), so a reorder's
                    // rebuild never hands its devices back and takes them
                    // again. The prune takes no other lock, and its events only
                    // set a wake or queue a tab refresh.
                    PeripheralOutputs.PruneClaims(assigned, PeripheralOutputs.SlotLive);
                }
            }

            // A slot that starts or stops lighting anything changes what the
            // tabs say even where no claim was pruned: a rumble row, or a
            // device whose Lighting tab leaves it alone.
            long live = 0;
            for (int slot = 0; slot < InputManager.MaxPads && slot < 64; slot++)
                if (PeripheralOutputs.SlotLive(slot)) live |= 1L << slot;
            bool liveChanged = _liveSlots != -1 && live != _liveSlots;
            _liveSlots = live;

            BeforeGate?.Invoke("publish");
            lock (_gate)
            {
                // A pass Stop's join gave up on publishes nothing: the stop's
                // own teardown has the last word.
                if (run.Stop) return;
                PeripheralOutputs.Hidpp = hidpp;
                PeripheralOutputs.Direct = new DirectSnapshot(units, _directFamilies, directScanned);
                // Under the gate, past the stop check: only the pass of the
                // run in charge keeps the orphans' clock, so one Stop gave
                // up on never moves it under its successor.
                PeripheralOutputs.UnitRows = PeripheralLinker.HeldOrphans(_orphanSince, orphans, now);
                // The rows whose last unit left a moment ago, kept on the
                // same terms as the orphans' clock. A device that is
                // unplugged loses its unit at a walk and its row at the
                // input manager's next device scan, in either order, and
                // an offline row is not read here. So a row held until that
                // scan has come keeps its record whichever came first.
                var held = PeripheralLinker.HeldRows(_unitLastSeen, rows, units, now);

                // The records first, so a tab that refreshes on the new table
                // reads the record that goes with it.
                bool changed = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    var ud = users[i];
                    var links = table.For(rows[i].Device);
                    // A row whose device may still be found keeps what it
                    // had: a Logitech row whose container is still being
                    // probed or before the HID++ worker's first scan, any
                    // device row before the direct backends' first, a row
                    // whose unit its backend lists asleep, so a mouse
                    // asleep at launch, or one a write to just failed,
                    // keeps its tabs, and a row whose last unit left the
                    // list less than the hold ago.
                    bool stillLooking = PeripheralLinker.StillLooking(rows[i], inputs) || held.Contains(rows[i].Device);
                    int next = PeripheralLinker.Capabilities(ud.PeripheralOutputs, links, stillLooking);
                    if (next == ud.PeripheralOutputs) continue;
                    ud.PeripheralOutputs = next;
                    changed = true;
                }

                bool relinked = !table.SameAs(PeripheralOutputs.Links);
                if (relinked)
                {
                    PeripheralOutputs.PublishLinks(table);
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL links: {table.ByDevice.Count} row(s), {table.ByPath.Count} path(s)");
                }
                else if (changed || liveChanged)
                {
                    // A record that moved under an unchanged table, or a slot
                    // that came or went, still changes what the tabs show.
                    PeripheralOutputs.NotifyStatusChanged();
                }
                if (changed)
                {
                    try { CapabilitiesChanged?.Invoke(); } catch { }
                }
                // Battery mode is drawn on demand, and a device that gained or
                // lost a path is drawn on the next pass, so both ask for one
                // once the snapshot and the table that maps a unit to its row
                // are published. A new path reaches slots whose last pass lit
                // nothing too.
                if (relinked) UserEffectsDispatcher.RequestPeripheralRefreshAll(evenUnlit: true);
                else if (charge) UserEffectsDispatcher.RequestPeripheralRefreshAll();
            }
        }

        /// <summary>Whether any unit's charge differs between two snapshots, a
        /// unit found or gone included.</summary>
        internal static bool ChargesChanged(HidppSnapshot before, HidppSnapshot after)
        {
            if (ReferenceEquals(before, after)) return false;
            var old = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var unit in before?.Units ?? Array.Empty<HidppUnit>())
                old[unit.Key] = unit.BatteryPercent;
            int seen = 0;
            foreach (var unit in after?.Units ?? Array.Empty<HidppUnit>())
            {
                if (!old.TryGetValue(unit.Key, out int percent) || percent != unit.BatteryPercent) return true;
                seen++;
            }
            return seen != old.Count;
        }

        /// <summary>The Sensa worker runs while the Sensa row is assigned to
        /// a virtual controller or Identify buzzes it, and stops when it is
        /// neither, which stops the engine's events and releases the
        /// provider. Identify nudges the pass, so the worker starts inside
        /// the train. A worker that ends on its own while the row stays
        /// assigned, because the engine did not load or a straggling
        /// predecessor kept the slot (<see cref="SensaHapticsService"/>'s
        /// bounded join), is started again after <see cref="SensaRestartMs"/>,
        /// so a missing HAR.dll does not spin.</summary>
        private void SensaLifecycle(WorkerRun run)
        {
            var sensaGuid = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);
            bool linked = PeripheralOutputs.HasHaptics(sensaGuid);
            bool assigned = linked && SettingsManager.SlotOrders.GetIdentityPlayerNumber(sensaGuid) > 0;
            bool identifying = linked && PeripheralOutputs.IsIdentifying(sensaGuid);
            if (!assigned && !identifying)
            {
                _sensaRestartAt = 0;
                if (_sensa == null) return;
                StopSensa();
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Sensa worker stopped (row unassigned)");
                return;
            }

            long now = Environment.TickCount64;
            if (_sensa != null && !_sensa.WorkerAlive)
            {
                StopSensa();
                _sensaRestartAt = now + SensaRestartMs;
                PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics, BackendState.Waiting);
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Sensa worker ended on its own, starting again later");
            }
            if (_sensa != null || now < _sensaRestartAt) return;

            BeforeGate?.Invoke("sensa");
            lock (_gate)
            {
                // A pass Stop's join gave up on starts nothing: no Stop is
                // left to end the worker.
                if (run.Stop) return;
                var sensa = _sensaFactory();
                // Only the current worker reports: a stopped one's last word
                // would otherwise land after its successor's.
                Action<SensaServiceState> report = state =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _sensa), sensa)) return;
                    PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics,
                        state == SensaServiceState.Active ? BackendState.Connected : BackendState.Waiting);
                };
                sensa.StateChanged += report;
                _sensaReport = report;
                Volatile.Write(ref _sensa, sensa);
                sensa.Start();
            }
            PadForge.Engine.SdlDiagLog.WriteLine(assigned
                ? "PERIPHERAL Sensa worker started (row assigned)"
                : "PERIPHERAL Sensa worker started (Identify)");
        }

        /// <summary>For an abnormal exit, after the engine's quiesce zeroed
        /// every level: waits up to <paramref name="waitMs"/> in all for GG
        /// to be told (<see cref="GameSenseBackend.WaitForSilence"/>), for
        /// the Sensa worker to render the zero
        /// (<see cref="SensaHapticsService.WaitForSilence"/>), and for each
        /// direct backend that rumbles a device to send its stop
        /// (<see cref="IDirectUnitBackend.WaitForSilence"/>), each inside
        /// what the ones before it left of the wait. A HID++ pulse ends on
        /// its own.</summary>
        public void WaitForSilence(int waitMs)
        {
            long until = _ticks() + waitMs;
            _gameSense.WaitForSilence(waitMs);
            Volatile.Read(ref _sensa)?.WaitForSilence((int)Math.Max(0, until - _ticks()));
            foreach (var backend in _direct)
            {
                try { backend.WaitForSilence((int)Math.Max(0, until - _ticks())); }
                catch { }
            }
        }

        /// <summary>For an abnormal exit: hands every device lit on its own
        /// back to its own effect, inside <see cref="PanicBudgetMs"/> for
        /// the direct backends together. Every direct worker is told to
        /// claim nothing more before any is waited for, since a crash dialog
        /// keeps the workers alive and one left running would claim its
        /// device again. Then the HID++ units go back first
        /// (<see cref="HidppBackend.ReleaseClaimedNow"/>), because only they
        /// can leave a device in host mode, and the direct backends after,
        /// in the order <see cref="DirectBackends.Create"/> lists them, each
        /// stopping at the one deadline. Chroma and GameSense end their
        /// sessions on their own timeouts, and the LED SDK goes with the
        /// process.</summary>
        public void ReleaseLightingNow()
        {
            long deadline = _ticks() + PanicBudgetMs;
            foreach (var backend in _direct)
            {
                try { backend.SignalStop(); }
                catch { }
            }
            _hidpp.ReleaseClaimedNow();
            foreach (var backend in _direct)
            {
                try { backend.ReleaseClaimedNow(deadline); }
                catch { }
            }
        }

        private void StopSensa()
        {
            var sensa = Interlocked.Exchange(ref _sensa, null);
            if (sensa == null) return;
            if (_sensaReport != null) sensa.StateChanged -= _sensaReport;
            _sensaReport = null;
            try { sensa.Dispose(); } catch { }
            PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics, BackendState.Idle);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            for (int i = _direct.Count - 1; i >= 0; i--)
            {
                try { _direct[i].Dispose(); } catch { }
            }
            _ledSdk?.Dispose();
            _chroma?.Dispose();
            _gameSense.Dispose();
            _hidpp.Dispose();
            _wake.Dispose();
        }
    }
}
