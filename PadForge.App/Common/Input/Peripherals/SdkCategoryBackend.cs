using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The worker of a vendor SDK that lights a vendor's devices by kind
    /// while the vendor's program runs (#557 item 11): the ASUS Aura SDK
    /// and MSI's Mystic Light SDK. Each kind of device is one shared path,
    /// as a Chroma category is (<see cref="ChromaBackend"/>), since neither
    /// SDK names a device by anything a row can be matched by. The worker
    /// keeps the shape of the Logitech LED SDK worker
    /// (<see cref="LedSdkBackend"/>): it reaches the SDK only while one of
    /// its paths is claimed and the vendor's program runs, every SDK call is
    /// made on its one thread, a path whose picture did not change sends
    /// nothing, and the session ends with the hand-back the SDK documents.
    ///
    /// <para>The peripheral host runs it beside the direct backends, through
    /// <see cref="IDirectUnitBackend"/>, so the host starts it, stops it and
    /// asks it to hand back on the crash path. It finds no units: its paths
    /// are named by kind, and its snapshot is always an empty scan.</para>
    ///
    /// <para>A path's lights come from a map the subclass makes, first from
    /// what it knows before the SDK is opened and then from the devices the
    /// SDK reports, and the worker publishes each for the Key Colors card
    /// (<see cref="PeripheralLightMaps"/>). A claim without light colors, or
    /// a path with no map, paints one color.</para>
    /// </summary>
    internal abstract class SdkCategoryBackend : IDirectUnitBackend
    {
        private readonly int _pollMs;
        private readonly int _retryMs;
        private readonly int _stopWaitMs;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly object _gate = new();
        private Thread _thread;
        private Run _run;
        private Thread _previous;
        private int _disposed;

        /// <summary>One Start's flags, its own, so a worker that outlived a
        /// timed-out Stop reads its own stop set whatever a later Start
        /// does (<see cref="WorkerRun"/>'s rule).</summary>
        private sealed class Run
        {
            public volatile bool Stop;
            public volatile bool Signaled;
            public readonly ManualResetEventSlim Released = new(false);
            public bool Ended => Stop || Signaled;
        }

        protected SdkCategoryBackend(int pollMs, int retryMs, int stopWaitMs)
        {
            _pollMs = pollMs;
            _retryMs = retryMs;
            _stopWaitMs = stopWaitMs;
        }

        public abstract OutputFamily Family { get; }

        /// <summary>The path keys, in paint order.</summary>
        protected abstract IReadOnlyList<string> Categories { get; }

        /// <summary>The name the log gives the SDK.</summary>
        protected abstract string LogName { get; }

        /// <summary>Whether the SDK is installed, from the presence read.</summary>
        protected abstract bool Installed(PeripheralPresence presence);

        /// <summary>Whether the vendor's program runs, from the presence
        /// read. The SDK answers only while it does.</summary>
        protected abstract bool ProgramRuns(PeripheralPresence presence);

        /// <summary>A path's lights before the SDK is opened, or null.</summary>
        protected abstract LightMap StaticMap(string category);

        /// <summary>Opens the SDK and reads its devices. False with a reason
        /// when it cannot. Called on the worker alone.</summary>
        protected abstract bool Open(out string detail);

        /// <summary>A path's lights from the devices the open session read,
        /// or null for one color.</summary>
        protected abstract LightMap SessionMap(string category);

        /// <summary>Paints one path. <paramref name="last"/> is the picture
        /// the path shows in this session, whose color at each light
        /// (<see cref="SdkPicture.ColorAt"/>) says which lights changed, and
        /// null for the whole picture. Both SDKs set each light by its own
        /// call, so a light whose color did not change needs nothing, one
        /// color or light by light alike. False when a call failed, which
        /// ends the session.</summary>
        protected abstract bool Paint(string category, SdkPicture picture, SdkPicture last);

        /// <summary>A path nobody claims any more while the session goes on
        /// for others: its devices go back to the program where the SDK can
        /// hand one kind back alone. Must not throw.</summary>
        protected abstract void Release(string category);

        /// <summary>Ends the session with the SDK's own hand-back and lets
        /// the SDK go. Must not throw.</summary>
        protected abstract void Close();

        public DirectBackendSnapshot Snapshot { get; } = new(Array.Empty<DirectUnit>(), scanned: true);

        public event Action SnapshotChanged { add { } remove { } }

        public event Action RescanRequested { add { } remove { } }

        /// <summary>Its paths are named by kind, so a walk of the HID
        /// collections tells it nothing.</summary>
        public void Rescan(IReadOnlyList<HidCollectionInfo> present) { }

        public void Start()
        {
            lock (_gate)
            {
                if (_thread != null || Volatile.Read(ref _disposed) != 0) return;
                var run = new Run();
                _run = run;
                PeripheralOutputs.LightingChanged += Wake;
                foreach (string category in Categories)
                    PeripheralLightMaps.Publish(PathOf(category), StaticMap(category));
                var previous = _previous;
                _thread = new Thread(() => Loop(run, previous))
                {
                    IsBackground = true,
                    Name = "PadForge " + LogName,
                };
                _thread.Start();
            }
        }

        public void Stop()
        {
            Thread thread;
            lock (_gate)
            {
                thread = _thread;
                if (thread == null) return;
                _thread = null;
                PeripheralOutputs.LightingChanged -= Wake;
                _run.Stop = true;
            }
            Wake();
            bool finished = true;
            try { finished = thread.Join(_stopWaitMs); } catch { }
            // A worker still inside the SDK is waited for by the next
            // Start's worker, so two never call the SDK at once.
            _previous = finished ? null : thread;
            if (!finished)
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL {LogName} stop timed out after {_stopWaitMs} ms, worker left inside the SDK");
            foreach (string category in Categories)
                PeripheralLightMaps.Remove(PathOf(category));
            PeripheralOutputs.SetBackendState(Family, BackendState.Idle);
        }

        /// <summary>The crash path's first step: paint nothing more, and
        /// hand back at once.</summary>
        public void SignalStop()
        {
            var run = _run;
            if (run != null) run.Signaled = true;
            Wake();
        }

        /// <summary>The crash path's second step: waits for the worker to
        /// hand back on its own thread, the one every SDK call is made on,
        /// at most 250 ms and never past <paramref name="deadline"/>.</summary>
        public void ReleaseClaimedNow(long deadline)
        {
            var run = _run;
            if (run == null || _thread == null) return;
            run.Signaled = true;
            Wake();
            long wait = Math.Min(250, deadline - Environment.TickCount64);
            if (wait <= 0) return;
            try { run.Released.Wait((int)wait); } catch { }
        }

        internal OutputPath PathOf(string category) => new(Family, category);

        private void Wake()
        {
            try { if (_wake.CurrentCount == 0) _wake.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private void Wait(int ms)
        {
            try { _wake.Wait(ms); }
            catch (ObjectDisposedException) { Thread.Sleep(ms); }
        }

        /// <summary>What one path should show: a color, and the light
        /// colors on top of it, null for none.</summary>
        internal readonly record struct Want(string Category, int Rgb, KeyFrame Keys);

        /// <summary>Each claimed path's color and light colors right now, in
        /// paint order.</summary>
        internal List<Want> Wanted()
        {
            var wanted = new List<Want>();
            foreach (string category in Categories)
                if (PeripheralOutputs.TryResolveColor(PathOf(category), out int rgb, out var ruler))
                    wanted.Add(new Want(category, rgb, ruler.Keys));
            return wanted;
        }

        /// <summary>The picture a want asks for on a map: light by light
        /// when it carries light colors and the map has more than one
        /// light, else one color.</summary>
        internal static SdkPicture PictureOf(Want want, LightMap map)
        {
            if (map == null || !map.PerLight || want.Keys == null || want.Keys.IsEmpty)
                return new SdkPicture(want.Rgb, null, null, want.Rgb & 0xFFFFFF);
            return new SdkPicture(want.Rgb, map, LightRenderer.Render(map, want.Rgb, want.Keys),
                LightRenderer.Fill(want.Rgb, want.Keys));
        }

        private void Report(BackendState state) => PeripheralOutputs.SetBackendState(Family, state);

        private void Loop(Run run, Thread previous)
        {
            bool open = false;
            string failureLogged = null;
            long retryAt = long.MinValue;
            var sent = new Dictionary<string, SdkPicture>(StringComparer.Ordinal);
            var maps = new Dictionary<string, LightMap>(StringComparer.Ordinal);
            try
            {
                if (previous != null && previous.IsAlive)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL {LogName} waiting for the worker of the previous session to leave the SDK");
                    previous.Join(15000);
                }

                while (!run.Ended)
                {
                    var wanted = Wanted();
                    var presence = PeripheralOutputs.Presence;
                    if (wanted.Count == 0)
                    {
                        if (open) CloseSession(ref open, sent);
                        failureLogged = null;
                        Report(BackendState.Idle);
                        Wait(_pollMs);
                        continue;
                    }
                    if (!Installed(presence) || !ProgramRuns(presence))
                    {
                        // The program went away: its SDK goes with it, and
                        // the session ends with what can still be sent.
                        if (open) CloseSession(ref open, sent);
                        failureLogged = null;
                        Report(BackendState.Waiting);
                        Wait(_pollMs);
                        continue;
                    }
                    if (!open)
                    {
                        if (Environment.TickCount64 < retryAt)
                        {
                            Report(BackendState.Waiting);
                            Wait(_pollMs);
                            continue;
                        }
                        if (!Open(out string detail))
                        {
                            if (detail != failureLogged)
                            {
                                failureLogged = detail;
                                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL {LogName} open failed: {detail}");
                            }
                            retryAt = Environment.TickCount64 + _retryMs;
                            Report(BackendState.Waiting);
                            Wait(_pollMs);
                            continue;
                        }
                        open = true;
                        failureLogged = null;
                        sent.Clear();
                        maps.Clear();
                        foreach (string category in Categories)
                        {
                            var map = SessionMap(category) ?? StaticMap(category);
                            maps[category] = map;
                            PeripheralLightMaps.Publish(PathOf(category), map);
                        }
                        Report(BackendState.Connected);
                        PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL {LogName} session open");
                    }

                    // A path nobody claims any more goes back to the program
                    // where the SDK can hand one kind back alone.
                    foreach (string category in new List<string>(sent.Keys))
                    {
                        if (wanted.Exists(w => w.Category == category)) continue;
                        Release(category);
                        sent.Remove(category);
                    }

                    bool failed = false;
                    foreach (var want in wanted)
                    {
                        if (run.Ended) break;
                        maps.TryGetValue(want.Category, out var map);
                        var picture = PictureOf(want, map);
                        sent.TryGetValue(want.Category, out var last);
                        if (picture.Same(last)) continue;
                        bool took;
                        try { took = Paint(want.Category, picture, last); }
                        catch (Exception ex)
                        {
                            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL {LogName} paint fault: {ex.GetType().Name}");
                            took = false;
                        }
                        if (!took)
                        {
                            failed = true;
                            break;
                        }
                        sent[want.Category] = picture;
                    }
                    if (failed)
                    {
                        // The program stopped answering: the session ends,
                        // and a new one opens after the retry interval.
                        CloseSession(ref open, sent);
                        retryAt = Environment.TickCount64 + _retryMs;
                        Report(BackendState.Waiting);
                    }
                    Wait(_pollMs);
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL {LogName} worker fault: {ex.GetType().Name}");
            }
            finally
            {
                if (open) CloseSession(ref open, sent);
                run.Released.Set();
            }
        }

        private void CloseSession(ref bool open, Dictionary<string, SdkPicture> sent)
        {
            try { Close(); } catch { }
            open = false;
            sent.Clear();
            foreach (string category in Categories)
                PeripheralLightMaps.Publish(PathOf(category), StaticMap(category));
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL {LogName} handed back");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }

    /// <summary>What one path shows: one color when <see cref="Colors"/>
    /// is null, else a color per light of <see cref="Map"/> over
    /// <see cref="Fill"/>, the color every light the frame names nothing
    /// for takes.</summary>
    internal sealed record SdkPicture(int Rgb, LightMap Map, int[] Colors, int Fill)
    {
        public bool Same(SdkPicture other)
            => other != null && (Colors == null
                ? other.Colors == null && Fill == other.Fill
                : ReferenceEquals(Map, other.Map) && Fill == other.Fill && LightRenderer.Same(Colors, other.Colors));

        /// <summary>The color of the light at <paramref name="address"/>,
        /// <see cref="Fill"/> for an address the map does not hold, and the
        /// one color when there is no map.</summary>
        public int ColorAt(int address)
        {
            if (Colors == null || Map == null) return Fill;
            var lights = Map.Lights;
            for (int i = 0; i < lights.Count; i++)
                if (lights[i].Address == address) return Colors[i];
            return Fill;
        }
    }
}
