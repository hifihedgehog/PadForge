using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The OpenRGB route (#557): while OpenRGB runs with its SDK server on,
    /// PadForge lights the devices OpenRGB holds through that server, LED
    /// by LED, instead of writing beside it. One thread owns the
    /// connection, the shape of the direct backends
    /// (<see cref="IDirectUnitBackend"/>), and each controller the server
    /// lists is a unit of its own.
    ///
    /// <para>Presence. The worker connects only while OpenRGB runs, by the
    /// process snapshot the link pass already takes
    /// (<see cref="VendorPrograms.OpenRgb"/>), and then to the address its
    /// command line and settings name (<see cref="OpenRgbServerPort"/>),
    /// once every <see cref="RetryMs"/> while nothing answers. A server
    /// that answers is asked for its controllers, which is all it takes to
    /// learn which devices OpenRGB holds. Nothing is written to any of them
    /// until a claim resolves on one.</para>
    ///
    /// <para>Units. Each controller is listed with the device its location
    /// names (<see cref="OpenRgbDevices"/>), and the linker gives it every
    /// row of that device in place of every path of PadForge's own
    /// (<see cref="PeripheralLinker.ChooseLighting"/>). A controller with no
    /// Direct mode is listed too, since OpenRGB holds its device all the
    /// same, and is not colored (<see cref="OpenRgbDevices.DirectModeName"/>).
    /// When the server stops answering while OpenRGB still runs, the units
    /// stay listed asleep, so the paths their devices would be lit through
    /// on their own stay closed, and they wake when the server lists them
    /// again. When OpenRGB stops, they leave the list.</para>
    ///
    /// <para>Claim and paint. A claim's first pass reads the controller
    /// again, keeps its active mode and colors for the hand-back, and sets
    /// its Direct mode as the server described it, the way RGB.NET's
    /// provider takes a controller (OpenRGBDeviceProvider.cs:111-116, where
    /// OpenRGB.NET reads the controller again and sends its mode back,
    /// OpenRGBClient.cs:190-227). Then each changed picture goes out as
    /// UpdateLEDs, at most one every <see cref="RepaintMs"/>. The server
    /// sets a flag for its own device thread on each UpdateLEDs and that
    /// thread writes the newest colors when it comes round
    /// (RGBController/RGBController.cpp:2024-2028 and 2082-2160), so the
    /// server paces the device itself and a picture is never sent twice.</para>
    ///
    /// <para>Hand back. OpenRGB's protocol has no call that releases a
    /// controller. When no claim resolves on a unit any more, the worker
    /// sets back the mode the controller was in and, for a mode with a
    /// color per LED, the colors it showed, with the same two packets, so
    /// OpenRGB's own lighting returns. The server applies a mode before the
    /// colors that follow it (RGBController.cpp:2091-2140). A hand-back
    /// that cannot go out is owed and goes out when the server answers
    /// again, ahead of any new claim, which takes it over as its own. The
    /// worker's finally and the crash path send what is on record, the
    /// finally what is owed too while it has a session. What cannot go out at
    /// a stop stays owed to the next run, or the next run would read
    /// PadForge's Direct mode as the controller's own, and goes with OpenRGB
    /// if that stops first.</para>
    ///
    /// <para>A controller OpenRGB registers again under the same location
    /// (DetectionManager.cpp:2167-2200) is the same unit to the worker, and
    /// the list shows it in another mode than the Direct one the worker set
    /// (the server keeps the active mode, NetworkServer.cpp:3240). The worker
    /// sets Direct again, paints again and keeps the hand-back it holds.</para>
    /// </summary>
    internal sealed class OpenRgbBackend : IDirectUnitBackend
    {
        internal const int IdleTickMs = HidppBackend.IdleTickMs;
        internal const int StopJoinMs = 3000;
        internal const int PanicJoinMs = HidppBackend.PanicJoinMs;

        /// <summary>How long a connection may take. The server is on this
        /// PC, and OpenRGB's own client allows 4 s for one anywhere on a
        /// network (NetworkClient.cpp:89).</summary>
        internal const int ConnectTimeoutMs = 1000;

        /// <summary>How long a server that did not answer waits before the
        /// next try, the interval the shared worker tries an asleep unit
        /// on (<see cref="HidLightingBackend.RetryMs"/>).</summary>
        internal const int RetryMs = HidLightingBackend.RetryMs;

        /// <summary>The least time between two pictures, RGB.NET's default
        /// rate of 30 updates a second (RGB.NET.Core
        /// Update/TimerUpdateTrigger.cs:36).</summary>
        internal const int RepaintMs = 33;

        /// <summary>How long a unit whose container Windows could not name
        /// yet waits before the next lookup.</summary>
        internal const int ContainerRetryMs = HidLightingBackend.ContainerRetryMs;

        /// <summary>What sets the controller back: the mode it was in, by
        /// index, as the server described it, and its colors.</summary>
        internal sealed record HandBack(int ModeIndex, OpenRgbMode Mode, uint[] Colors);

        /// <summary>One controller the server lists and what the worker
        /// holds of it. Worker thread only.</summary>
        private sealed class Unit
        {
            public string Key;
            public uint Index;
            public OpenRgbController Controller;
            public int DirectMode = -1;
            public LightMap Map;
            public string HidPath;
            public Guid Container;
            public long ContainerRetryAt;
            public ushort VendorId;
            public ushort ProductId;
            public bool BuiltInKeyboard;
            public string HeldKey;
            public int Kind;
            public bool Asleep;

            /// <summary>Set once the claim's first pass set the Direct mode,
            /// with what sets the controller back.</summary>
            public bool Entered;
            public HandBack Back;

            /// <summary>A hand-back that did not go out.</summary>
            public HandBack Owed;

            /// <summary>The colors the controller shows from PadForge, the
            /// map they were rendered for, and when they went out.</summary>
            public int[] Shown;
            public LightMap ShownMap;
            public long SentAt;

            public bool Recorded;

            public string Label => $"'{Controller?.Name}'";
        }

        /// <summary>One entered unit's hand-back on the crash path's record,
        /// as the packets that send it and the connection they go out
        /// on.</summary>
        internal sealed record CrashRecord(string Key, IOpenRgbLink Link, byte[][] Packets);

        private CrashRecord[] _recorded = Array.Empty<CrashRecord>();

        /// <summary>The hand-backs a run could not send at its stop, by unit
        /// key, for the next run's list. Handed from one worker to the next,
        /// which waits for it, over the engine's restarts.</summary>
        private static Dictionary<string, HandBack> s_carried;

        /// <summary>For a test: nothing carried from the last one.</summary>
        internal static void ForgetCarriedForTest() => Volatile.Write(ref s_carried, null);

        private readonly Func<OpenRgbServerAddress, int, IOpenRgbLink> _connect;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<long> _ticks;
        private readonly Func<bool> _runs;
        private readonly Func<OpenRgbServerAddress> _address;
        private readonly int _idleTickMs;
        private readonly int _stopJoinMs;
        private readonly int _versionWaitMs;
        private readonly int _replyWaitMs;
        private readonly AutoResetEvent _wake = new(false);

        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;
        private IReadOnlyList<HidCollectionInfo> _present;
        private Thread _thread;
        private Thread _worker;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;
        private int _waitMs;

        /// <summary>The family's workers over the engine's restarts, so two
        /// never hold the server's controllers at once.</summary>
        private static readonly WorkerSuccession s_workers = new();

        public OpenRgbBackend()
            : this(null, null, null, null, null, IdleTickMs) { }

        /// <summary>Test seam: the connection maker (address, timeout), the
        /// container lookup, the clock, whether OpenRGB runs, the address the
        /// server listens on (<see cref="OpenRgbServerPort.Find"/>), the
        /// idle tick, Stop's wait and the session's two waits. The connection
        /// maker is the worker's only way to the server, and its default
        /// connects to nothing in the test host
        /// (<see cref="OpenRgbTcpLink.OffLimitsForTest"/>).</summary>
        internal OpenRgbBackend(Func<OpenRgbServerAddress, int, IOpenRgbLink> connect, Func<string, Guid> containerOf, Func<long> ticks,
            Func<bool> runs, Func<OpenRgbServerAddress> address, int idleTickMs, int stopJoinMs = StopJoinMs,
            int versionWaitMs = OpenRgbSession.VersionWaitMs, int replyWaitMs = OpenRgbSession.ReplyWaitMs)
        {
            _connect = connect ?? OpenRgbTcpLink.Connect;
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _ticks = ticks ?? (() => Environment.TickCount64);
            _runs = runs ?? OpenRgbRuns;
            _address = address ?? OpenRgbServerPort.Find;
            _idleTickMs = idleTickMs;
            _stopJoinMs = stopJoinMs;
            _versionWaitMs = versionWaitMs;
            _replyWaitMs = replyWaitMs;
        }

        /// <summary>Whether OpenRGB runs, by the presence the link pass last
        /// published.</summary>
        internal static bool OpenRgbRuns()
            => PeripheralOutputs.Presence.Programs.FirstOf(OpenRgbOnly) != null;

        private static readonly VendorProgram[] OpenRgbOnly = { VendorPrograms.OpenRgb };

        public OutputFamily Family => OutputFamily.OpenRgbDevice;

        public DirectBackendSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public event Action SnapshotChanged;

        /// <summary>Never raised: the server says which devices OpenRGB
        /// holds, and no walk of the HID collections finds one sooner.</summary>
        public event Action RescanRequested { add { } remove { } }

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
            Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralOpenRgb" };
            Volatile.Write(ref _worker, _thread);
            _thread.Start();
            PeripheralOutputs.LightingChanged += Wake;
            PeripheralOutputs.LinksChanged += Wake;
        }

        public void Stop()
        {
            PeripheralOutputs.LightingChanged -= Wake;
            PeripheralOutputs.LinksChanged -= Wake;
            if (_thread == null) return;
            _run.Stop = true;
            Wake();
            try { _thread.Join(_stopJoinMs); } catch { }
            _thread = null;
            _run = null;
        }

        /// <summary>The present HID collections, which give a controller
        /// found by its HID path its vendor and product ID.</summary>
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
        /// stops first and its finally hands back what it holds. A worker
        /// that does not stop in time claims nothing more, and each unit
        /// still on record gets its hand-back on the connection it was
        /// entered on, each send bounded
        /// (<see cref="OpenRgbTcpLink.SendTimeoutMs"/>), until the
        /// deadline.</summary>
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
                foreach (var packet in record.Packets)
                {
                    if (deadline - _ticks() <= 0) return;
                    bool sent;
                    try { sent = record.Link.Send(packet); }
                    catch { sent = false; }
                    if (!sent) break;
                }
            }
        }

        internal void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        private static OutputPath PathOf(Unit unit) => new(OutputFamily.OpenRgbDevice, unit.Key);

        private void Worker(WorkerRun run)
        {
            s_workers.Enter(run, _stopJoinMs, "OpenRGB");
            var units = new List<Unit>();
            OpenRgbSession session = null;
            bool published = false, unanswered = false;
            long nextConnect = 0;
            try
            {
                while (!run.Stop)
                {
                    long now = _ticks();
                    bool changed = !published;
                    long due = long.MaxValue;

                    bool runs = _runs();
                    if (!runs)
                    {
                        // OpenRGB stopped: its server and its hold went with
                        // it, so there is nothing to hand back to.
                        Volatile.Write(ref s_carried, null);
                        if (session != null || units.Count > 0)
                        {
                            Close(ref session);
                            foreach (var unit in units)
                            {
                                Unrecord(unit);
                                s_workers.HandedBack(unit.Key, run);
                                PeripheralLightMaps.Remove(PathOf(unit));
                            }
                            units.Clear();
                            changed = true;
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL OpenRGB stopped, its devices let go");
                        }
                        nextConnect = 0;
                    }
                    else
                    {
                        if (session != null && !session.Drain()) Lose(ref session, units, now, ref nextConnect, ref changed);
                        if (session == null && now >= nextConnect)
                        {
                            session = Connect();
                            if (session == null) nextConnect = now + RetryMs;
                            else
                            {
                                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL OpenRGB server answers, protocol {session.Version}");
                                session.ListChanged = true;
                            }
                        }
                        if (session != null && session.ListChanged)
                        {
                            if (!List(session, units, now)) Lose(ref session, units, now, ref nextConnect, ref changed);
                            else changed = true;
                        }
                        if (session == null) Soonest(ref due, nextConnect);
                    }

                    foreach (var unit in units)
                    {
                        if (unit.Container == Guid.Empty && unit.HidPath != null)
                        {
                            if (now >= unit.ContainerRetryAt && LookUp(unit, now)) changed = true;
                            if (unit.Container == Guid.Empty) Soonest(ref due, unit.ContainerRetryAt);
                        }
                    }

                    var links = PeripheralOutputs.Links;
                    bool lit = false;
                    foreach (var unit in units)
                    {
                        if (run.Stop || session == null) break;
                        if (!Serve(run, session, unit, links, ref changed, ref due))
                        {
                            Lose(ref session, units, _ticks(), ref nextConnect, ref changed);
                            break;
                        }
                        lit |= unit.Entered;
                    }

                    // OpenRGB runs and its server has not said which devices
                    // it holds: the link pass leaves every device it may
                    // drive to it (LinkInputs.OpenRgbUnanswered).
                    bool nowUnanswered = session == null && runs;
                    if (nowUnanswered != unanswered)
                    {
                        unanswered = nowUnanswered;
                        changed = true;
                    }
                    if (changed)
                    {
                        Publish(units, unanswered);
                        published = true;
                    }
                    PeripheralOutputs.SetBackendState(OutputFamily.OpenRgbDevice, lit ? BackendState.Connected : BackendState.Idle);

                    int wait = due == long.MaxValue ? _idleTickMs : (int)Math.Clamp(due - _ticks(), 1, _idleTickMs);
                    Volatile.Write(ref _waitMs, wait);
                    Interlocked.Increment(ref _passes);
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL OpenRGB worker fault: " + ex.GetType().Name);
            }
            finally
            {
                // Each controller goes back to OpenRGB's own lighting before
                // the connection closes, what was owed since a lost connection
                // included. What does not go out is left to the next run. A
                // controller a later run has entered is that run's to say.
                HandBackAtStop(run, session, units);
                Close(ref session);
                if (s_workers.Holds(run))
                {
                    PeripheralOutputs.SetBackendState(OutputFamily.OpenRgbDevice, BackendState.Idle);
                    foreach (var unit in units) PeripheralLightMaps.Remove(PathOf(unit));
                    Volatile.Write(ref _recorded, Array.Empty<CrashRecord>());
                    Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
                }
            }
        }

        /// <summary>The worker's last act for each unit. A unit that is asleep
        /// has an index the server has not confirmed, so what it is owed waits
        /// for the next run's list, as with no session at all. A run that never
        /// read a list has no units and writes nothing, so what the run before
        /// it carried waits for the first list that is read.</summary>
        private void HandBackAtStop(WorkerRun run, OpenRgbSession session, List<Unit> units)
        {
            Dictionary<string, HandBack> carried = null;
            foreach (var unit in units)
            {
                if (!s_workers.MayReach(unit.Key, run)) continue;
                bool sent = true;
                try
                {
                    if (session != null && unit.Entered) sent = SendBack(session, unit);
                    else if (session != null && unit.Owed != null && !unit.Asleep) sent = PayOwed(session, unit);
                }
                catch { sent = false; }
                if (sent && unit.Owed == null) s_workers.HandedBack(unit.Key, run);
                if (!sent)
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL OpenRGB {unit.Label} hand-back did not go out at the stop, owed to the next run");
                if (unit.Owed != null) (carried ??= new Dictionary<string, HandBack>(StringComparer.Ordinal))[unit.Key] = unit.Owed;
            }
            if (carried != null) Volatile.Write(ref s_carried, carried);
        }

        private static void Soonest(ref long due, long at)
        {
            if (at < due) due = at;
        }

        private OpenRgbSession Connect()
        {
            IOpenRgbLink link = null;
            OpenRgbServerAddress address;
            try { address = _address(); }
            catch { address = OpenRgbServerAddress.Default; }
            PeripheralOutputs.OpenRgbPortTried = address.Port;
            try { link = _connect(address, ConnectTimeoutMs); } catch { }
            if (link == null) return null;
            OpenRgbSession session = null;
            try { session = OpenRgbSession.Begin(link, _versionWaitMs, _replyWaitMs); } catch { }
            if (session == null)
            {
                try { link.Dispose(); } catch { }
            }
            return session;
        }

        private static void Close(ref OpenRgbSession session)
        {
            try { session?.Dispose(); } catch { }
            session = null;
        }

        /// <summary>The server stopped answering while OpenRGB runs. Every
        /// unit stays listed, asleep, so its device's own paths stay
        /// closed. An entered unit's hand-back is owed, since the controller
        /// may still show PadForge's colors.</summary>
        private void Lose(ref OpenRgbSession session, List<Unit> units, long now, ref long nextConnect, ref bool changed)
        {
            Close(ref session);
            nextConnect = now + RetryMs;
            foreach (var unit in units)
            {
                if (unit.Entered)
                {
                    unit.Owed = unit.Back;
                    unit.Entered = false;
                    unit.Back = null;
                }
                unit.Shown = null;
                unit.ShownMap = null;
                Unrecord(unit);
                if (!unit.Asleep) changed = true;
                unit.Asleep = true;
            }
            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL OpenRGB server stopped answering, its devices listed asleep");
        }

        // ─────────────────────────────────────────────
        //  The list
        // ─────────────────────────────────────────────

        /// <summary>The unit keys of a list: each controller's location,
        /// which names its device, or its name where the location is empty,
        /// and a number after the first where two are alike.</summary>
        internal static string[] KeysOf(IReadOnlyList<OpenRgbController> controllers)
        {
            var keys = new string[controllers.Count];
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < keys.Length; i++)
            {
                var controller = controllers[i];
                string key = string.IsNullOrEmpty(controller?.Location) ? "name: " + controller?.Name : controller.Location;
                seen.TryGetValue(key, out int count);
                seen[key] = ++count;
                keys[i] = count == 1 ? key : key + " #" + count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return keys;
        }

        /// <summary>Reads the server's list and brings the units in line
        /// with it, by key: a unit keeps what it holds when it is listed
        /// again at another index. False when the server stopped
        /// answering.</summary>
        private bool List(OpenRgbSession session, List<Unit> units, long now)
        {
            var controllers = session.ReadControllers();
            if (controllers == null) return false;
            var keys = KeysOf(controllers);
            var present = Volatile.Read(ref _present);
            var listed = new HashSet<Unit>();
            var carried = Interlocked.Exchange(ref s_carried, null);
            for (int i = 0; i < controllers.Count; i++)
            {
                var controller = controllers[i];
                if (controller == null) continue;
                Unit unit = null;
                foreach (var known in units)
                    if (known.Key == keys[i]) { unit = known; break; }
                if (unit == null)
                {
                    unit = new Unit { Key = keys[i] };
                    if (carried != null && carried.TryGetValue(keys[i], out var owed)) unit.Owed = owed;
                    units.Add(unit);
                    Describe(unit, controller, present, now);
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL OpenRGB lists {unit.Label} at {unit.VendorId:X4}:{unit.ProductId:X4}, {unit.Map?.Lights.Count ?? 0} LEDs"
                        + (unit.DirectMode < 0 ? ", no Direct mode" : string.Empty));
                }
                else
                {
                    Describe(unit, controller, present, now);
                }
                unit.Index = (uint)i;
                unit.Asleep = false;
                if (unit.Recorded) Track(session, unit, force: true);
                listed.Add(unit);
                // Registered again under this location, in a mode of its own:
                // the claim stands, so Direct goes back on and the picture
                // with it. The hand-back held is still the controller's.
                if (unit.Entered && unit.DirectMode >= 0 && controller.ActiveMode != unit.DirectMode)
                {
                    unit.Shown = null;
                    unit.ShownMap = null;
                    if (!session.UpdateMode(unit.Index, unit.DirectMode, controller.Modes[unit.DirectMode])) return false;
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL OpenRGB {unit.Label} listed out of its Direct mode, set to it again");
                }
            }
            for (int i = units.Count - 1; i >= 0; i--)
            {
                var unit = units[i];
                if (listed.Contains(unit)) continue;
                // The controller left OpenRGB's list, and with it the device
                // OpenRGB held. Nothing is left to hand back to.
                Unrecord(unit);
                PeripheralLightMaps.Remove(PathOf(unit));
                units.RemoveAt(i);
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL OpenRGB no longer lists {unit.Label}");
            }
            return true;
        }

        /// <summary>Takes a controller's description into its unit: the
        /// device it names, its Direct mode and its light map, which is
        /// published for the Key Colors card while the unit can be
        /// colored.</summary>
        private void Describe(Unit unit, OpenRgbController controller, IReadOnlyList<HidCollectionInfo> present, long now)
        {
            bool first = unit.Controller == null;
            string location = controller.Location;
            unit.Controller = controller;
            unit.DirectMode = OpenRgbDevices.DirectMode(controller);
            if (first)
            {
                unit.HidPath = OpenRgbDevices.HidPath(location);
                unit.BuiltInKeyboard = OpenRgbDevices.BuiltInKeyboard(location);
                unit.HeldKey = OpenRgbDevices.HeldKey(location);
                unit.Kind = OpenRgbDevices.OnReceiver(location) ? OpenRgbDevices.KindOf(controller.Type) : 0;
                if (unit.BuiltInKeyboard) unit.Container = PeripheralLinker.LocalMachineContainer;
                if (unit.HidPath != null)
                {
                    (unit.VendorId, unit.ProductId) = OpenRgbDevices.IdsOf(unit.HidPath);
                    if (present != null)
                        foreach (var collection in present)
                            if (string.Equals(collection.Path, unit.HidPath, StringComparison.OrdinalIgnoreCase))
                            {
                                unit.VendorId = collection.VendorId;
                                unit.ProductId = collection.ProductId;
                                break;
                            }
                    LookUp(unit, now);
                }
            }
            var map = OpenRgbDevices.Map(controller);
            if (unit.Map == null || !SameLights(unit.Map, map)) unit.Map = map;
            if (unit.DirectMode >= 0) PeripheralLightMaps.Publish(PathOf(unit), unit.Map);
            else PeripheralLightMaps.Remove(PathOf(unit));
        }

        private static bool SameLights(LightMap a, LightMap b)
        {
            if (a.Lights.Count != b.Lights.Count) return false;
            for (int i = 0; i < a.Lights.Count; i++)
                if (a.Lights[i] != b.Lights[i]) return false;
            return true;
        }

        private bool LookUp(Unit unit, long now)
        {
            Guid container;
            try { container = _containerOf(unit.HidPath); }
            catch { container = Guid.Empty; }
            unit.Container = container;
            unit.ContainerRetryAt = now + ContainerRetryMs;
            return container != Guid.Empty;
        }

        private void Publish(List<Unit> units, bool unanswered)
        {
            var list = new DirectUnit[units.Count];
            for (int i = 0; i < list.Length; i++)
            {
                var unit = units[i];
                list[i] = new DirectUnit(OutputFamily.OpenRgbDevice, unit.Key, unit.Container, unit.VendorId, unit.ProductId)
                {
                    Name = unit.Controller?.Name,
                    Kind = unit.Kind,
                    BuiltInKeyboard = unit.BuiltInKeyboard,
                    HeldKey = unit.HeldKey,
                    NoDirectMode = unit.DirectMode < 0,
                    Asleep = unit.Asleep,
                };
            }
            Volatile.Write(ref _snapshot, new DirectBackendSnapshot(list) { ProgramUnanswered = unanswered });
            try { SnapshotChanged?.Invoke(); } catch { }
        }

        // ─────────────────────────────────────────────
        //  One unit, one pass
        // ─────────────────────────────────────────────

        /// <summary>One pass over a unit. False when a send or a read
        /// failed, which ends the session.</summary>
        private bool Serve(WorkerRun run, OpenRgbSession session, Unit unit, LinkTable links, ref bool changed, ref long due)
        {
            if (unit.Asleep) return true;
            var path = PathOf(unit);
            int rgb = 0;
            PeripheralOutputs.Claimant ruler = default;
            bool wanted = unit.DirectMode >= 0 && links.ByPath.ContainsKey(path)
                          && PeripheralOutputs.TryResolveColor(path, out rgb, out ruler);
            if (!wanted)
            {
                if (unit.Entered)
                {
                    bool sent = SendBack(session, unit);
                    if (sent) s_workers.HandedBack(unit.Key, run);
                    return sent;
                }
                // A hand-back owed from a session that ended. A stopping
                // worker sends it from its finally, and leaves it to the next
                // run when it cannot (HandBackAtStop).
                if (unit.Owed != null && !run.Stop)
                {
                    bool paid = PayOwed(session, unit);
                    if (paid) s_workers.HandedBack(unit.Key, run);
                    return paid;
                }
                return true;
            }
            if (run.Stop) return true;
            if (!unit.Entered)
            {
                if (!Enter(run, session, unit, ref changed)) return false;
                if (!unit.Entered) return true;
                // A stop that came while the mode was set paints nothing
                // more. The finally sets the controller back.
                if (run.Stop) return true;
            }
            return Paint(session, unit, rgb, ruler.Keys, ref due);
        }

        /// <summary>The claim's first pass: reads the controller again, keeps
        /// what sets it back, and sets its Direct mode. A unit whose
        /// controller turns out to be another, or to have no Direct mode
        /// now, is not entered, and the list is read again.</summary>
        private bool Enter(WorkerRun run, OpenRgbSession session, Unit unit, ref bool changed)
        {
            var fresh = session.ReadController(unit.Index);
            if (fresh == null) return false;
            // A stop that came during the read enters nothing.
            if (run.Stop) return true;
            if (fresh.Location != unit.Controller.Location || fresh.Name != unit.Controller.Name)
            {
                session.ListChanged = true;
                return true;
            }
            Describe(unit, fresh, null, _ticks());
            if (unit.DirectMode < 0)
            {
                changed = true;
                session.ListChanged = true;
                return true;
            }
            // A worker running late left its Direct mode on the controller, and
            // what it read is the controller's own.
            var back = unit.Owed ?? (s_workers.TakeHandBack(unit.Key, run, out HandBack late) ? late : new HandBack(fresh.ActiveMode,
                fresh.ActiveMode >= 0 && fresh.ActiveMode < fresh.Modes.Count ? fresh.Modes[fresh.ActiveMode] : null,
                (uint[])fresh.Colors.Clone()));
            s_workers.Claim(unit.Key, run);
            s_workers.NoteHandBack(unit.Key, run, back);
            unit.Back = back;
            unit.Owed = null;
            unit.Entered = true;
            unit.Shown = null;
            unit.ShownMap = null;
            // The hand-back goes on record before the first packet that
            // changes the controller.
            Track(session, unit);
            if (!session.UpdateMode(unit.Index, unit.DirectMode, fresh.Modes[unit.DirectMode])) return false;
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL OpenRGB {unit.Label} set to its Direct mode");
            return true;
        }

        private bool Paint(OpenRgbSession session, Unit unit, int rgb, KeyFrame keys, ref long due)
        {
            long now = _ticks();
            var map = unit.Map;
            var colors = LightRenderer.Render(map, rgb, keys);
            if (unit.Shown != null && ReferenceEquals(unit.ShownMap, map) && LightRenderer.Same(colors, unit.Shown))
                return true;
            if (unit.Shown != null)
            {
                long at = unit.SentAt + RepaintMs;
                if (now < at)
                {
                    Soonest(ref due, at);
                    return true;
                }
            }
            var packed = new uint[colors.Length];
            for (int i = 0; i < colors.Length; i++) packed[i] = OpenRgbProtocol.ToRgbColor(colors[i]);
            if (!session.UpdateLeds(unit.Index, packed)) return false;
            bool first = unit.Shown == null;
            unit.Shown = colors;
            unit.ShownMap = map;
            unit.SentAt = now;
            if (first) PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL OpenRGB {unit.Label} lit, {colors.Length} LEDs");
            return true;
        }

        /// <summary>The packets that set a controller back: its mode, then,
        /// for a mode with a color per LED, its colors
        /// (<see cref="HandBack"/>).</summary>
        internal static byte[][] HandBackPackets(uint index, HandBack back, uint version)
        {
            var packets = new List<byte[]>(2);
            if (back.Mode != null && back.ModeIndex >= 0)
                packets.Add(OpenRgbProtocol.UpdateModeRequest(index, back.ModeIndex, back.Mode, version));
            if ((back.Mode == null || back.Mode.ColorMode == OpenRgbProtocol.ModeColorsPerLed) && back.Colors.Length > 0)
                packets.Add(OpenRgbProtocol.UpdateLedsRequest(index, back.Colors));
            return packets.ToArray();
        }

        /// <summary>Hands an entered unit back. False when a packet did not
        /// go out: the hand-back is then owed.</summary>
        private bool SendBack(OpenRgbSession session, Unit unit)
        {
            var back = unit.Back;
            unit.Entered = false;
            unit.Back = null;
            unit.Shown = null;
            unit.ShownMap = null;
            Unrecord(unit);
            if (back == null) return true;
            foreach (var packet in HandBackPackets(unit.Index, back, session.Version))
            {
                if (session.Link.Send(packet)) continue;
                unit.Owed = back;
                return false;
            }
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL OpenRGB {unit.Label} lighting handed back");
            return true;
        }

        private bool PayOwed(OpenRgbSession session, Unit unit)
        {
            var owed = unit.Owed;
            foreach (var packet in HandBackPackets(unit.Index, owed, session.Version))
                if (!session.Link.Send(packet)) return false;
            unit.Owed = null;
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL OpenRGB {unit.Label} lighting handed back, owed since the server stopped answering");
            return true;
        }

        // ─────────────────────────────────────────────
        //  The crash path's record
        // ─────────────────────────────────────────────

        /// <summary>Puts an entered unit's hand-back on the record, at its
        /// index now, in place of any it had there.</summary>
        private void Track(OpenRgbSession session, Unit unit, bool force = false)
        {
            if (unit.Recorded && !force) return;
            Unrecord(unit);
            if (unit.Back == null) return;
            var record = new CrashRecord(unit.Key, session.Link, HandBackPackets(unit.Index, unit.Back, session.Version));
            var current = Volatile.Read(ref _recorded);
            var next = new CrashRecord[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = record;
            Volatile.Write(ref _recorded, next);
            unit.Recorded = true;
        }

        private void Unrecord(Unit unit)
        {
            if (!unit.Recorded) return;
            unit.Recorded = false;
            var current = Volatile.Read(ref _recorded);
            var next = new List<CrashRecord>(current.Length);
            foreach (var entry in current)
                if (entry.Key != unit.Key) next.Add(entry);
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
