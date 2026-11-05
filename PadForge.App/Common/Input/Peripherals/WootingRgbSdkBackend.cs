using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The worker of the Wooting route (#557 item 11,
    /// <see cref="WootingRgbSdkRoute"/>): while Wootility runs, it lights the
    /// Wooting keyboards the link pass gives it through the RGB SDK's
    /// reports, beside Wootility, as an RGB SDK application does. One thread
    /// owns its channels, the shape of the direct backends
    /// (<see cref="IDirectUnitBackend"/>), and it lists no units of its own:
    /// its paths name the shared worker's Wooting units, and the linker adds
    /// them while Wootility runs (<see cref="WootingRgbSdkRoute.AddShared"/>).
    ///
    /// <para>Open. A linked path whose collection is present has its lights
    /// published for the Key Colors card. A path a claim resolves on is
    /// opened (<see cref="HidReportChannel.Open"/>), sent the color init
    /// command, and painted. A picture goes out when it changes, at most
    /// once every <see cref="RepaintMs"/>, the bound the Wooting family
    /// keeps (<see cref="WootingKeyboardLighting.RepaintMs"/>): neither the
    /// SDK nor OpenRGB bounds it.</para>
    ///
    /// <para>Hand back. When no claim resolves on the path any more, the
    /// reset command, then the handle is closed. Not when the shared
    /// worker's own path to the same collection is linked, the case once
    /// Wootility is closed: that worker opens the keyboard with its own init
    /// and picture, and a reset that lands after them would undo them. Nor
    /// while OpenRGB holds the keyboard (<see cref="PeripheralOutputs.TakenByOpenRgb"/>).
    /// A write that fails closes the handle, and the reset is owed: it goes
    /// out once the collection opens again with no claim on it, or a new
    /// claim takes it over. The worker's finally hands back what it holds,
    /// and the crash path sends the reset of each keyboard still on record
    /// through a handle of its own, bounded.</para>
    /// </summary>
    internal sealed class WootingRgbSdkBackend : IDirectUnitBackend
    {
        internal const int IdleTickMs = HidppBackend.IdleTickMs;
        internal const int StopJoinMs = 3000;
        internal const int PanicJoinMs = HidppBackend.PanicJoinMs;
        internal const int RetryMs = HidLightingBackend.RetryMs;
        internal const int RepaintMs = WootingKeyboardLighting.RepaintMs;
        internal const int OneShotMs = HidLightingBackend.OneShotMs;

        /// <summary>One keyboard the worker holds. Worker thread only.</summary>
        private sealed class Unit
        {
            public string Key;
            public WootingKeyboardLighting.Protocol Protocol;
            public WootingKeyboardLighting.KeyboardType Type;
            public LightMap Map;
            public IHidReportChannel Channel;
            public bool Lit;
            public bool Owed;
            public long RetryAt;
            public int[] Shown;
            public long SentAt;

            public OutputPath Path => new(OutputFamily.WootingRgbSdk, Key);
        }

        private readonly Func<string, IHidReportChannel> _open;
        private readonly Func<string, byte[], int, bool> _setFeatureOnce;
        private readonly Func<long> _ticks;
        private readonly int _idleTickMs;
        private readonly int _stopJoinMs;
        private readonly int _responseTimeoutMs;
        private readonly AutoResetEvent _wake = new(false);

        private IReadOnlyList<HidCollectionInfo> _present;
        private string[] _recorded = Array.Empty<string>();
        private Thread _thread;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;

        /// <summary>The route's workers over the engine's restarts.</summary>
        private static readonly WorkerSuccession s_workers = new();

        public WootingRgbSdkBackend() : this(null, null, null, IdleTickMs) { }

        /// <summary>Test seam: the channel opener, the crash path's one-shot
        /// feature write, the clock, the idle tick, Stop's wait and the wait
        /// for a command's answer. The production opener opens nothing in
        /// the test host (<see cref="RealHidAccess"/>).</summary>
        internal WootingRgbSdkBackend(Func<string, IHidReportChannel> open, Func<string, byte[], int, bool> setFeatureOnce,
            Func<long> ticks, int idleTickMs, int stopJoinMs = StopJoinMs,
            int responseTimeoutMs = WootingRgbSdkRoute.ResponseTimeoutMs)
        {
            _open = open ?? HidReportChannel.Open;
            _setFeatureOnce = setFeatureOnce ?? HidReportChannel.SetFeatureOnce;
            _ticks = ticks ?? (() => Environment.TickCount64);
            _idleTickMs = idleTickMs;
            _stopJoinMs = stopJoinMs;
            _responseTimeoutMs = responseTimeoutMs;
        }

        public OutputFamily Family => OutputFamily.WootingRgbSdk;

        /// <summary>Always a finished scan with no units: the route's paths
        /// name the shared worker's Wooting units.</summary>
        public DirectBackendSnapshot Snapshot { get; } = new(Array.Empty<DirectUnit>());

        public event Action SnapshotChanged { add { } remove { } }
        public event Action RescanRequested { add { } remove { } }

        internal int PassCount => Volatile.Read(ref _passes);
        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>The keys of the keyboards the crash path would reset.</summary>
        internal string[] Recorded => Volatile.Read(ref _recorded);

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _thread != null) return;
            var run = new WorkerRun();
            _run = run;
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralWootingSdk" };
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

        /// <summary>The present HID collections, which give each path its
        /// vendor, product and usage page.</summary>
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

        /// <summary>For the crash path: the worker stops and its finally
        /// hands back. A keyboard still on record after that gets its reset
        /// through a handle of its own, each write bounded, until the
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
            var present = Volatile.Read(ref _present);
            foreach (string key in Interlocked.Exchange(ref _recorded, Array.Empty<string>()))
            {
                left = deadline - _ticks();
                if (left <= 0) return;
                if (TakenOver(key) || Describe(key, present) is not { } found) continue;
                try { _setFeatureOnce(key, WootingRgbSdkRoute.Command(found.Protocol, WootingRgbSdkRoute.ResetAllCommand), (int)Math.Min(left, OneShotMs)); }
                catch { }
            }
        }

        internal void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        /// <summary>The protocol and type of a path's collection, from the
        /// present list, or null when it is not there or speaks no Wooting
        /// protocol.</summary>
        internal static (WootingKeyboardLighting.Protocol Protocol, WootingKeyboardLighting.KeyboardType Type)? Describe(
            string key, IReadOnlyList<HidCollectionInfo> present)
        {
            if (present == null) return null;
            foreach (var collection in present)
            {
                if (!string.Equals(collection.Path, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (WootingKeyboardLighting.ProtocolOf(collection.VendorId, collection.UsagePage) is not { } protocol) return null;
                return (protocol, WootingKeyboardLighting.TypeOf(collection.ProductId));
            }
            return null;
        }

        /// <summary>Whether a keyboard's reset would land on someone else's
        /// picture: the shared worker's own path to it is linked, or OpenRGB
        /// holds it.</summary>
        internal static bool TakenOver(string key)
        {
            var own = new OutputPath(OutputFamily.HidLightingUnit, key);
            return PeripheralOutputs.Links.ByPath.ContainsKey(own) || PeripheralOutputs.TakenByOpenRgb(own);
        }

        private void Worker(WorkerRun run)
        {
            s_workers.Enter(run, _stopJoinMs, "Wooting SDK");
            var units = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
            try
            {
                while (!run.Stop)
                {
                    long now = _ticks();
                    long due = long.MaxValue;
                    var links = PeripheralOutputs.Links;
                    var present = Volatile.Read(ref _present);
                    Track(units, links, present);
                    bool lit = false;
                    foreach (var unit in new List<Unit>(units.Values))
                    {
                        if (run.Stop) break;
                        Serve(run, unit, links, present, now, ref due);
                        lit |= unit.Lit;
                        if (!links.ByPath.ContainsKey(unit.Path) && !unit.Lit && !unit.Owed)
                        {
                            Close(unit);
                            PeripheralLightMaps.Remove(unit.Path);
                            units.Remove(unit.Key);
                        }
                    }
                    PeripheralOutputs.SetBackendState(OutputFamily.WootingRgbSdk, lit ? BackendState.Connected : BackendState.Idle);
                    int wait = due == long.MaxValue ? _idleTickMs : (int)Math.Clamp(due - _ticks(), 1, _idleTickMs);
                    Interlocked.Increment(ref _passes);
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Wooting SDK worker fault: " + ex.GetType().Name);
            }
            finally
            {
                // A keyboard handed back, or taken over by another writer,
                // leaves the crash path's record. One whose reset did not go
                // out, or is owed from a failed write, stays on it. A keyboard
                // a later run has claimed is that run's to say.
                foreach (var unit in units.Values)
                {
                    if (s_workers.MayReach(unit.Key, run))
                    {
                        bool taken = TakenOver(unit.Key);
                        bool back = false;
                        if (!taken && unit.Lit && unit.Channel != null)
                            try { back = HandBack(unit); } catch { }
                        if (taken || back || (!unit.Lit && !unit.Owed)) Unrecord(unit.Key);
                    }
                    if (s_workers.Holds(run)) PeripheralLightMaps.Remove(unit.Path);
                    Close(unit);
                }
                if (s_workers.Holds(run)) PeripheralOutputs.SetBackendState(OutputFamily.WootingRgbSdk, BackendState.Idle);
            }
        }

        /// <summary>Takes in every linked path whose collection is present,
        /// with its lights published. A path that is no longer linked
        /// leaves, map and all, once it owes nothing, in the worker's pass.</summary>
        private static void Track(Dictionary<string, Unit> units, LinkTable links, IReadOnlyList<HidCollectionInfo> present)
        {
            foreach (var path in links.ByPath.Keys)
            {
                if (path.Family != OutputFamily.WootingRgbSdk || units.ContainsKey(path.Key)) continue;
                if (Describe(path.Key, present) is not { } found) continue;
                var unit = new Unit { Key = path.Key, Protocol = found.Protocol, Type = found.Type };
                unit.Map = new LightMap(WootingKeyboardLighting.Lights(found.Type), "wooting-rgb-sdk matrix, OpenRGB RGBController_WootingKeyboard.cpp");
                units.Add(path.Key, unit);
                PeripheralLightMaps.Publish(unit.Path, unit.Map);
            }
        }

        private void Serve(WorkerRun run, Unit unit, LinkTable links, IReadOnlyList<HidCollectionInfo> present, long now, ref long due)
        {
            if (Describe(unit.Key, present) == null)
            {
                // The collection left the walk, and with it whatever the
                // keyboard showed: nothing is owed to a keyboard that is
                // gone, as a direct backend's debt ends when its unit
                // leaves the list (IDirectUnitBackend, "Owe").
                if (unit.Lit || unit.Owed) Unrecord(unit.Key);
                unit.Lit = unit.Owed = false;
                Close(unit);
                return;
            }
            int rgb = 0;
            PeripheralOutputs.Claimant ruler = default;
            bool wanted = links.ByPath.ContainsKey(unit.Path)
                          && PeripheralOutputs.TryResolveColor(unit.Path, out rgb, out ruler);
            if (!wanted || run.Stop)
            {
                if (unit.Lit || unit.Owed) Release(unit, now, ref due);
                return;
            }
            if (unit.Channel == null)
            {
                if (now < unit.RetryAt)
                {
                    Soonest(ref due, unit.RetryAt);
                    return;
                }
                if (!Enter(run, unit, now))
                {
                    Soonest(ref due, unit.RetryAt);
                    return;
                }
            }
            var colors = LightRenderer.Render(unit.Map, rgb, ruler.Keys);
            if (unit.Shown != null && LightRenderer.Same(colors, unit.Shown)) return;
            if (unit.Shown != null && now < unit.SentAt + RepaintMs)
            {
                Soonest(ref due, unit.SentAt + RepaintMs);
                return;
            }
            foreach (var report in WootingRgbSdkRoute.Reports(unit.Protocol, unit.Type, unit.Channel.OutputReportLength,
                         unit.Map.Lights, colors))
            {
                if (unit.Channel.WriteOutput(report)) continue;
                Fail(unit, now);
                Soonest(ref due, unit.RetryAt);
                return;
            }
            if (unit.Shown == null) PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL Wooting SDK {unit.Key} lit");
            unit.Shown = colors;
            unit.SentAt = now;
        }

        /// <summary>Opens a keyboard and sends the color init command. The
        /// reset goes on record before the command, since the keyboard shows
        /// the route's colors from its first picture. False when the
        /// collection did not open or the command did not go out.</summary>
        private bool Enter(WorkerRun run, Unit unit, long now)
        {
            IHidReportChannel channel = null;
            try { channel = _open(unit.Key); } catch { }
            if (channel == null)
            {
                unit.RetryAt = now + RetryMs;
                return false;
            }
            unit.Channel = channel;
            s_workers.Claim(unit.Key, run);
            Record(unit.Key);
            if (!Command(unit, WootingRgbSdkRoute.ColorInitCommand))
            {
                Fail(unit, now);
                return false;
            }
            unit.Lit = true;
            unit.Owed = false;
            unit.Shown = null;
            return true;
        }

        /// <summary>Lets a keyboard go: its reset, unless another writer has
        /// taken it over, then the handle closed. An owed reset is sent once
        /// the collection opens again.</summary>
        private void Release(Unit unit, long now, ref long due)
        {
            if (TakenOver(unit.Key))
            {
                unit.Lit = unit.Owed = false;
                Unrecord(unit.Key);
                Close(unit);
                return;
            }
            if (unit.Channel == null)
            {
                if (now < unit.RetryAt)
                {
                    Soonest(ref due, unit.RetryAt);
                    return;
                }
                try { unit.Channel = _open(unit.Key); } catch { }
                if (unit.Channel == null)
                {
                    unit.RetryAt = now + RetryMs;
                    Soonest(ref due, unit.RetryAt);
                    return;
                }
            }
            if (HandBack(unit))
            {
                unit.Lit = unit.Owed = false;
                Unrecord(unit.Key);
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL Wooting SDK {unit.Key} handed back");
            }
            else
            {
                unit.Lit = false;
                unit.Owed = true;
                unit.RetryAt = now + RetryMs;
                Soonest(ref due, unit.RetryAt);
            }
            Close(unit);
        }

        private bool HandBack(Unit unit) => Command(unit, WootingRgbSdkRoute.ResetAllCommand);

        /// <summary>One command and the keyboard's answer to it, read as the
        /// SDK reads it (wooting-usb.c:720-754 and 805-828): input reports
        /// until the answer's size is in, each read waiting at most a second,
        /// one report on the third protocol. A report numbered 0 counts
        /// without its ID byte, as hidapi hands it over. An answer that does
        /// not come in time is let go: the command went out. False when the
        /// command itself did not.</summary>
        private bool Command(Unit unit, byte command)
        {
            if (!unit.Channel.SetFeature(WootingRgbSdkRoute.Command(unit.Protocol, command))) return false;
            int want = WootingRgbSdkRoute.ResponseSize(unit.Protocol);
            int length = unit.Channel.InputReportLength;
            if (length <= 0) return true;
            var buffer = new byte[length];
            int got = 0;
            while (got < want)
            {
                int read = unit.Channel.ReadInput(buffer, _responseTimeoutMs);
                if (read <= 0) break;
                got += buffer[0] == 0 ? read - 1 : read;
            }
            return true;
        }

        private void Fail(Unit unit, long now)
        {
            Close(unit);
            if (unit.Lit) unit.Owed = true;
            if (!unit.Owed) Unrecord(unit.Key);
            unit.Lit = false;
            unit.Shown = null;
            unit.RetryAt = now + RetryMs;
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL Wooting SDK {unit.Key} write failed, closed");
        }

        private static void Close(Unit unit)
        {
            try { unit.Channel?.Dispose(); } catch { }
            unit.Channel = null;
            unit.Shown = null;
        }

        private static void Soonest(ref long due, long at)
        {
            if (at < due) due = at;
        }

        private void Record(string key)
        {
            var current = Volatile.Read(ref _recorded);
            if (Array.IndexOf(current, key) >= 0) return;
            var next = new string[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = key;
            Volatile.Write(ref _recorded, next);
        }

        private void Unrecord(string key)
        {
            var current = Volatile.Read(ref _recorded);
            if (Array.IndexOf(current, key) < 0) return;
            var next = new List<string>(current.Length);
            foreach (string entry in current)
                if (entry != key) next.Add(entry);
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
