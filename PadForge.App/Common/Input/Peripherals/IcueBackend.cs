using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The iCUE route (#557 item 11): while Corsair iCUE runs, PadForge
    /// lights the Corsair devices it lists through its SDK, LED by LED,
    /// instead of leaving them to it. One thread owns the SDK, the shape of
    /// the direct backends (<see cref="IDirectUnitBackend"/>), and it lists
    /// no units of its own: a row reaches iCUE's devices by type
    /// (<see cref="IcueRoute"/>), and the linker adds that path while iCUE
    /// runs (<see cref="IcueRoute.AddShared"/>).
    ///
    /// <para>Session. The worker opens one only while a claim resolves on a
    /// linked type path, so a PC with nothing assigned never loads the SDK.
    /// It loads the client library, calls CorsairConnect once and reads the
    /// devices when the SDK reports CSS_Connected. The SDK reconnects on its
    /// own after a timeout or a lost connection, Win+L included, and lists
    /// the devices again when it is back ("Transition between session
    /// states" and "Win+L", cue-sdk docs/index.html), so the worker waits on
    /// the state and reads the devices again whenever it returns to
    /// connected. Control is the shared level RGB.NET requests by default
    /// (CorsairDeviceProvider.cs:56 and 163), whose layer iCUE shows over its
    /// own (docs, CorsairSetLayerPriority). The list is read again every
    /// <see cref="DeviceReadMs"/> and after a device is not found, since a
    /// device can come or go while the session runs.</para>
    ///
    /// <para>Names. A type's map carries the names Map Key Lights learned for
    /// it (<see cref="LearnedKeyMaps"/>), again whenever they change, as every
    /// other backend whose map has a product does.</para>
    ///
    /// <para>Refusal. The path exists only while iCUE runs
    /// (<see cref="IcueRoute.AddShared"/>), so a session that does not come up
    /// is iCUE answering no, which the tab says in place of its waiting line
    /// (<see cref="PeripheralOutputs.RefusalOn"/>). CSS_ConnectionRefused
    /// names a setting (iCUESDK.h:40, docs/index.html:166).</para>
    ///
    /// <para>Paint. Each claimed type's picture is rendered on its map and
    /// sent to each of its devices through CorsairSetLedColors, the call
    /// RGB.NET's update queue makes (CorsairDeviceUpdateQueue.cs:400-410).
    /// The SDK keeps a color "until changed by successive calls" (docs,
    /// CorsairSetLedColors), so a picture goes out only when it changes, at
    /// most once every <see cref="RepaintMs"/>, RGB.NET's default rate.</para>
    ///
    /// <para>Hand back. A type no claim resolves on any more gets every LED
    /// of its devices translucent (<see cref="IcueRoute.Clear"/>), which
    /// shows iCUE's own lighting beneath the shared layer. When no type is
    /// claimed the session ends the way RGB.NET's provider ends it: every
    /// painted device cleared, CorsairDisconnect, and the library freed
    /// (CorsairDeviceProvider.cs:334-348). The crash path only stops the
    /// worker, whose finally does the same: the SDK is not safe to call from
    /// a second thread, and a process that ends closes its connection to
    /// iCUE with it.</para>
    /// </summary>
    internal sealed class IcueBackend : IDirectUnitBackend
    {
        internal const int IdleTickMs = HidppBackend.IdleTickMs;
        internal const int StopJoinMs = 3000;
        internal const int PanicJoinMs = HidppBackend.PanicJoinMs;

        /// <summary>How long a library that would not load or a connect the
        /// SDK refused waits before the next try.</summary>
        internal const int RetryMs = HidLightingBackend.RetryMs;

        /// <summary>How often the session's state is looked at while it is
        /// not connected.</summary>
        internal const int StatePollMs = 100;

        /// <summary>How often the device list is read again while the
        /// session is connected.</summary>
        internal const int DeviceReadMs = 5000;

        /// <summary>The least time between two pictures of one type, RGB.NET's
        /// default rate of 30 updates a second (RGB.NET.Core
        /// Update/TimerUpdateTrigger.cs:36).</summary>
        internal const int RepaintMs = 33;

        /// <summary>iCUE refuses SDK clients: the setting is off.</summary>
        private static readonly ProgramRefusal SdkOff = new(null, SettingOff: true);

        /// <summary>One device the session holds.</summary>
        private sealed class Device
        {
            public IcueDeviceInfo Info;
            public string Key;
            public IReadOnlyList<IcueLedPosition> Leds;

            /// <summary>What the device shows from PadForge, null for
            /// nothing.</summary>
            public IcueLedColor[] Shown;
        }

        private readonly Func<IIcueNative> _native;
        private readonly Func<long> _ticks;
        private readonly int _idleTickMs;
        private readonly int _stopJoinMs;
        private readonly AutoResetEvent _wake = new(false);

        private Thread _thread;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;
        private int _waitMs;

        /// <summary>The route's workers over the engine's restarts, so two
        /// never hold the SDK at once.</summary>
        private static readonly WorkerSuccession s_workers = new();

        /// <summary>What a worker claims when it wants the SDK.</summary>
        private const string SdkKey = "sdk";

        public IcueBackend() : this(null, null, IdleTickMs) { }

        /// <summary>Test seam: the native layer (made once per worker), the
        /// clock, the idle tick and Stop's wait. The production layer loads
        /// nothing in the test host
        /// (<see cref="PadForge.Services.IcueSdkNative.OffLimitsForTest"/>).</summary>
        internal IcueBackend(Func<IIcueNative> native, Func<long> ticks, int idleTickMs, int stopJoinMs = StopJoinMs)
        {
            _native = native ?? (() => new PadForge.Services.IcueSdkNative());
            _ticks = ticks ?? (() => Environment.TickCount64);
            _idleTickMs = idleTickMs;
            _stopJoinMs = stopJoinMs;
        }

        public OutputFamily Family => OutputFamily.IcueDevice;

        /// <summary>Always a finished scan with no units: iCUE's devices
        /// reach rows by type, through the link pass.</summary>
        public DirectBackendSnapshot Snapshot { get; } = new(Array.Empty<DirectUnit>());

        /// <summary>Never raised: the snapshot never changes.</summary>
        public event Action SnapshotChanged { add { } remove { } }

        /// <summary>Never raised: iCUE lists its own devices.</summary>
        public event Action RescanRequested { add { } remove { } }

        internal int PassCount => Volatile.Read(ref _passes);
        internal int WaitMs => Volatile.Read(ref _waitMs);
        internal bool WorkerAlive => _thread?.IsAlive == true;

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _thread != null) return;
            var run = new WorkerRun();
            _run = run;
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralIcue" };
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

        /// <summary>Nothing to keep: iCUE lists its own devices.</summary>
        public void Rescan(IReadOnlyList<HidCollectionInfo> present) { }

        public void SignalStop()
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
        }

        /// <summary>For the crash path: the worker stops and its finally
        /// hands back, waited for until the deadline. Nothing is called on
        /// the SDK from this thread.</summary>
        public void ReleaseClaimedNow(long deadline)
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
            long left = deadline - _ticks();
            if (left <= 0) return;
            try { _thread?.Join((int)Math.Min(left, PanicJoinMs)); } catch { }
        }

        internal void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        /// <summary>A name learned with Map Key Lights wakes the worker, which
        /// gives its maps the name on its next pass.</summary>
        private void OnLearned(object sender, EventArgs e) => Wake();

        /// <summary>What one type should show now: its path's ruling color
        /// and key frame, or false when no claim resolves on it or no row
        /// links it.</summary>
        internal static bool Wanted(string key, LinkTable links, out int rgb, out KeyFrame keys)
        {
            rgb = 0;
            keys = null;
            var path = new OutputPath(OutputFamily.IcueDevice, key);
            if (!links.ByPath.ContainsKey(path) || !PeripheralOutputs.TryResolveColor(path, out rgb, out var ruler)) return false;
            keys = ruler.Keys;
            return true;
        }

        private static bool AnyWanted(LinkTable links)
        {
            foreach (var (key, _) in IcueRoute.Types)
                if (Wanted(key, links, out _, out _)) return true;
            return false;
        }

        private void Worker(WorkerRun run)
        {
            s_workers.Enter(run, _stopJoinMs, "iCUE");
            var session = new Session(this, run);
            try
            {
                while (!run.Stop)
                {
                    long now = _ticks();
                    long due = long.MaxValue;
                    var links = PeripheralOutputs.Links;
                    if (!AnyWanted(links))
                    {
                        if (session.Open) session.End();
                        session.Retry = 0;
                        PeripheralOutputs.SetProgramRefusal(OutputFamily.IcueDevice, null);
                        PeripheralOutputs.SetBackendState(OutputFamily.IcueDevice, BackendState.Idle);
                    }
                    else
                    {
                        s_workers.Claim(SdkKey, run);
                        session.Pass(run, links, now, ref due);
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
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL iCUE worker fault: " + ex.GetType().Name);
            }
            finally
            {
                // A worker that outlived its Stop into a run that wants the SDK
                // frees its library and nothing more: the next run's worker holds
                // the SDK and the maps now.
                if (s_workers.MayReach(SdkKey, run))
                {
                    try { session.End(); } catch { }
                    PeripheralOutputs.SetProgramRefusal(OutputFamily.IcueDevice, null);
                    PeripheralOutputs.SetBackendState(OutputFamily.IcueDevice, BackendState.Idle);
                }
                else
                {
                    try { session.Abandon(); } catch { }
                }
            }
        }

        /// <summary>The session whose connect the SDK's one client holds,
        /// and the lock its connect and its disconnect go under, so a worker
        /// that outlived its Stop never disconnects a newer worker's
        /// session.</summary>
        private static readonly object s_sdkGate = new();
        private static Session s_sdkOwner;

        /// <summary>One worker's hold on the SDK. Worker thread only.</summary>
        private sealed class Session
        {
            private readonly IcueBackend _owner;
            private IIcueNative _native;
            private bool _loaded;
            private bool _connected;
            private bool _listed;
            private long _nextList;
            private string _loadFailure;
            private readonly List<Device> _devices = new();

            /// <summary>Each type's map from its devices' LEDs, and that map
            /// with the names Map Key Lights learned, the one published and
            /// painted by.</summary>
            private readonly Dictionary<string, (LightMap Base, LightMap Map)> _maps = new();

            /// <summary>The <see cref="LearnedKeyMaps.Version"/> the maps were
            /// last given their names at.</summary>
            private int _mapsVersion = -1;
            private readonly Dictionary<string, long> _sentAt = new();

            public long Retry;

            private readonly WorkerRun _run;

            public Session(IcueBackend owner, WorkerRun run)
            {
                _owner = owner;
                _run = run;
            }

            public bool Open => _loaded;

            public void Pass(WorkerRun run, LinkTable links, long now, ref long due)
            {
                if (!_loaded)
                {
                    if (now < Retry)
                    {
                        Soonest(ref due, Retry);
                        return;
                    }
                    if (!Load(now))
                    {
                        Soonest(ref due, Retry);
                        return;
                    }
                }
                if (run.Stop) return;
                int state = _native.State;
                if (state != IcueRoute.StateConnected)
                {
                    // Connecting, timed out, refused or lost: the SDK tries
                    // again on its own, and the devices it listed are gone
                    // until it is back. Refused and timed out are iCUE, which
                    // runs, not answering. The other two pass by themselves.
                    if (_listed) Forget();
                    PeripheralOutputs.SetProgramRefusal(OutputFamily.IcueDevice,
                        state == IcueRoute.StateConnectionRefused ? SdkOff
                        : state == IcueRoute.StateTimeout ? ProgramRefusal.Everything
                        : null);
                    PeripheralOutputs.SetBackendState(OutputFamily.IcueDevice, BackendState.Waiting);
                    Soonest(ref due, now + StatePollMs);
                    return;
                }
                PeripheralOutputs.SetProgramRefusal(OutputFamily.IcueDevice, null);
                if (!_listed || now >= _nextList)
                {
                    if (!List(now)) return;
                }
                else if (LearnedKeyMaps.Version != _mapsVersion) Publish();
                Soonest(ref due, _nextList);
                bool lost = false;
                foreach (var (key, _) in IcueRoute.Types)
                {
                    if (run.Stop || lost) break;
                    if (Wanted(key, links, out int rgb, out var keys)) lost = !Paint(key, rgb, keys, now, ref due);
                    else lost = !Clear(key);
                }
                PeripheralOutputs.SetBackendState(OutputFamily.IcueDevice,
                    lost ? BackendState.Waiting : BackendState.Connected);
            }

            private static void Soonest(ref long due, long at)
            {
                if (at < due) due = at;
            }

            private bool Load(long now)
            {
                _native = _owner._native();
                string detail;
                bool loaded;
                try { loaded = _native.TryLoad(out detail); }
                catch (Exception ex) { loaded = false; detail = ex.GetType().Name; }
                if (!loaded)
                {
                    if (detail != _loadFailure)
                    {
                        _loadFailure = detail;
                        PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL iCUE SDK not loaded: " + detail);
                    }
                    Unload();
                    Retry = now + RetryMs;
                    PeripheralOutputs.SetProgramRefusal(OutputFamily.IcueDevice, ProgramRefusal.Everything);
                    PeripheralOutputs.SetBackendState(OutputFamily.IcueDevice, BackendState.Waiting);
                    return false;
                }
                _loadFailure = null;
                _loaded = true;
                int error;
                lock (s_sdkGate)
                {
                    error = _native.Connect();
                    // The SDK's client is one per process, so a session a
                    // worker that outlived its Stop never ended leaves it
                    // connected, which CorsairConnect refuses with
                    // CE_InvalidOperation (iCUESDK.h:29, docs CorsairConnect).
                    // CorsairDisconnect takes that session's handler away,
                    // and the connect goes again once.
                    if (error == IcueRoute.InvalidOperation)
                    {
                        _native.Disconnect();
                        error = _native.Connect();
                    }
                    if (error == IcueRoute.Success) s_sdkOwner = this;
                }
                if (error != IcueRoute.Success)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL iCUE CorsairConnect refused: " + error);
                    Unload();
                    Retry = now + RetryMs;
                    PeripheralOutputs.SetProgramRefusal(OutputFamily.IcueDevice, ProgramRefusal.Everything);
                    PeripheralOutputs.SetBackendState(OutputFamily.IcueDevice, BackendState.Waiting);
                    return false;
                }
                _connected = true;
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL iCUE SDK connecting");
                return true;
            }

            /// <summary>Reads the device list and each device's LEDs, takes
            /// shared control of each, and publishes each type's map. A
            /// device that keeps its id keeps what it shows.</summary>
            private bool List(long now)
            {
                _nextList = now + DeviceReadMs;
                int error = _native.GetDevices(IcueRoute.TypeMask, out var infos);
                if (error != IcueRoute.Success || infos == null)
                {
                    if (error == IcueRoute.NotConnected) Forget();
                    return false;
                }
                var kept = new List<Device>();
                foreach (var info in infos)
                {
                    string key = IcueRoute.KeyOf(info?.Type ?? 0);
                    if (key == null || string.IsNullOrWhiteSpace(info.Id)) continue;
                    var device = _devices.Find(d => d.Info.Id == info.Id);
                    if (device == null)
                    {
                        if (_native.GetLedPositions(info.Id, out var leds) != IcueRoute.Success || leds == null || leds.Count == 0)
                            continue;
                        // RGB.NET requests control of each device before it
                        // writes (CorsairDeviceProvider.cs:163-165), at the
                        // shared level, which needs no request by the docs
                        // (CorsairRequestControl). A refusal leaves the
                        // device at that level anyway.
                        _native.RequestControl(info.Id, IcueRoute.AccessShared);
                        device = new Device { Info = info, Key = key, Leds = leds };
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL iCUE lists '{info.Model}', {key}, {leds.Count} LEDs");
                    }
                    kept.Add(device);
                }
                _devices.Clear();
                _devices.AddRange(kept);
                _listed = true;
                Publish();
                return true;
            }

            /// <summary>Publishes each type's map from its devices' LEDs, and
            /// takes away the map of a type with no device. A type whose LEDs
            /// did not change keeps its map, so the Lighting tab does not
            /// redraw on every read, until the names Map Key Lights learned
            /// change (HidppBackend.SyncMaps).</summary>
            private void Publish()
            {
                int version = LearnedKeyMaps.Version;
                bool relearn = version != _mapsVersion;
                _mapsVersion = version;
                foreach (var (key, _) in IcueRoute.Types)
                {
                    var leds = new List<IReadOnlyList<IcueLedPosition>>();
                    foreach (var device in _devices)
                        if (device.Key == key) leds.Add(device.Leds);
                    var fresh = IcueRoute.Map(key, leds);
                    var path = new OutputPath(OutputFamily.IcueDevice, key);
                    if (fresh == null)
                    {
                        if (_maps.Remove(key)) PeripheralLightMaps.Remove(path);
                        continue;
                    }
                    if (_maps.TryGetValue(key, out var known) && SameLights(known.Base, fresh))
                    {
                        if (!relearn) continue;
                        var again = LearnedKeyMaps.Apply(known.Base);
                        if (SameLights(again, known.Map)) continue;
                        _maps[key] = (known.Base, again);
                        PeripheralLightMaps.Publish(path, again);
                        continue;
                    }
                    var map = LearnedKeyMaps.Apply(fresh);
                    _maps[key] = (fresh, map);
                    PeripheralLightMaps.Publish(path, map);
                    foreach (var device in _devices)
                        if (device.Key == key) device.Shown = null;
                }
            }

            private static bool SameLights(LightMap a, LightMap b)
            {
                if (a.Lights.Count != b.Lights.Count) return false;
                for (int i = 0; i < a.Lights.Count; i++)
                    if (a.Lights[i] != b.Lights[i]) return false;
                return true;
            }

            /// <summary>One type's picture on each of its devices. False when
            /// the session is lost.</summary>
            private bool Paint(string key, int rgb, KeyFrame keys, long now, ref long due)
            {
                if (!_maps.TryGetValue(key, out var maps)) return true;
                var map = maps.Map;
                var colors = LightRenderer.Render(map, rgb, keys);
                int fill = LightRenderer.Fill(rgb, keys);
                bool pending = false;
                var pictures = new List<(Device Device, IcueLedColor[] Colors)>();
                foreach (var device in _devices)
                {
                    if (device.Key != key) continue;
                    var picture = IcueRoute.Colors(device.Leds, map, colors, fill);
                    if (device.Shown != null && device.Shown.AsSpan().SequenceEqual(picture)) continue;
                    pictures.Add((device, picture));
                    pending = true;
                }
                if (!pending) return true;
                if (_sentAt.TryGetValue(key, out long sent) && now < sent + RepaintMs)
                {
                    Soonest(ref due, sent + RepaintMs);
                    return true;
                }
                _sentAt[key] = now;
                foreach (var (device, picture) in pictures)
                {
                    int error = _native.SetLedColors(device.Info.Id, picture);
                    if (error == IcueRoute.Success)
                    {
                        if (device.Shown == null)
                            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL iCUE '{device.Info.Model}' lit, {picture.Length} LEDs");
                        device.Shown = picture;
                        continue;
                    }
                    if (error == IcueRoute.NotConnected)
                    {
                        Forget();
                        return false;
                    }
                    // The device left, or another client holds it: the list
                    // is read again on the next pass.
                    if (error == IcueRoute.DeviceNotFound) _nextList = 0;
                }
                return true;
            }

            /// <summary>Hands one type's painted devices back to iCUE. False
            /// when the session is lost.</summary>
            private bool Clear(string key)
            {
                foreach (var device in _devices)
                {
                    if (device.Key != key || device.Shown == null) continue;
                    device.Shown = null;
                    int error = _native.SetLedColors(device.Info.Id, IcueRoute.Cleared(device.Leds));
                    if (error == IcueRoute.NotConnected)
                    {
                        Forget();
                        return false;
                    }
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL iCUE '{device.Info.Model}' handed back");
                }
                return true;
            }

            /// <summary>The devices are gone with the connection: nothing
            /// they showed is PadForge's any more, and their maps go.</summary>
            private void Forget()
            {
                _devices.Clear();
                _listed = false;
                _nextList = 0;
                _sentAt.Clear();
                // The maps are the next run's once it wants the SDK.
                if (s_workers.MayReach(SdkKey, _run))
                    foreach (var key in _maps.Keys) PeripheralLightMaps.Remove(new OutputPath(OutputFamily.IcueDevice, key));
                _maps.Clear();
            }

            /// <summary>Ends the session: each painted device back to iCUE,
            /// then CorsairDisconnect and the library freed.</summary>
            public void End()
            {
                if (_native == null) return;
                if (_loaded && _native.State == IcueRoute.StateConnected)
                    foreach (var (key, _) in IcueRoute.Types)
                        if (!Clear(key)) break;
                Forget();
                if (_connected)
                {
                    // Not a session a newer worker's connect has replaced:
                    // the SDK's client is the process's, and the newer
                    // session is on it now.
                    lock (s_sdkGate)
                    {
                        if (ReferenceEquals(s_sdkOwner, this))
                        {
                            try { _native.Disconnect(); } catch { }
                            s_sdkOwner = null;
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL iCUE SDK disconnected");
                        }
                    }
                    _connected = false;
                }
                Unload();
            }

            /// <summary>Frees the library of a session a newer worker has
            /// replaced, with nothing sent.</summary>
            public void Abandon() => Unload();

            private void Unload()
            {
                try { _native?.Unload(); } catch { }
                _loaded = false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }
}
