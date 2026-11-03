using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Services;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The SteelSeries GameSense worker (#494). GameSense addresses device
    /// types, never one mouse: its tactile handler drives every tactile Rival
    /// at once (gamesense-sdk standard-zones.md:3, "a device category"), and a
    /// color handler lights every device of its type. So the tactile path
    /// takes the level of the assigned Rival on the virtual controller with
    /// the smallest displayed player number
    /// (<see cref="PeripheralOutputs.TryResolveHapticRuler"/>), and each color
    /// type shows the color its path resolves to
    /// (<see cref="PeripheralOutputs.TryResolveColor(OutputPath, out int)"/>).
    ///
    /// <para>GG is reached only while something is assigned, a Rival or a
    /// SteelSeries device or the GG row that a controller lights, or while
    /// Identify buzzes a Rival (<see cref="PeripheralOutputs.BeginIdentify(Guid, int)"/>).
    /// Rumble and colors share one PADFORGE game (<see cref="GameSenseClient"/>):
    /// a post only when a value changes, a heartbeat while one holds, a color
    /// event removed as soon as its type stops being claimed, and stop_game
    /// once nothing has been claimed for <see cref="ReleaseMs"/>, which hands
    /// the devices back to GG. A type claimed with light colors (#557) is
    /// colored light by light on its own event
    /// (<see cref="GameSenseLights"/>), and its lights are published for
    /// the Key Colors card while the worker runs. The HTTP calls run here,
    /// never on the poll thread. With GG let go and nothing claimed, the worker waits
    /// <see cref="IdleTickMs"/> between looks, and a level that starts, a
    /// color claimed or Identify wakes it at once.</para>
    /// </summary>
    internal sealed class GameSenseBackend : IDisposable
    {
        internal const int TickMs = 20;

        /// <summary>The wait between looks while GG is let go and nothing is
        /// claimed, the other peripheral workers' idle cadence.</summary>
        internal const int IdleTickMs = 100;

        /// <summary>How often the ruling Rival is worked out again. Each look
        /// walks the assignments, which change at the speed of a drag. A
        /// level that starts and Identify bring the next look forward.</summary>
        internal const int RulerMs = 250;

        /// <summary>How long GG may go unanswered before the next try.</summary>
        internal const int RetryMs = 15000;

        /// <summary>How long the game may sit with nothing claimed before the
        /// devices go back to GG, so a reassignment does not churn the
        /// registration.</summary>
        internal const int ReleaseMs = 2000;

        /// <summary>The fastest a held color is posted again, so an animated
        /// mode costs GG at most twenty requests a second per type.</summary>
        internal const int ColorMs = 50;

        private readonly Func<GameSenseClient> _client;
        private readonly Func<Guid, int> _playerOf;
        private readonly Func<Guid, float> _levelOf;
        private readonly Func<long> _clock;
        private readonly int _tickMs;
        private readonly int _rulerMs;
        private readonly AutoResetEvent _wake = new(false);
        private Thread _thread;
        private WorkerRun _run;
        private int _disposed;

        /// <summary>Set when a level starts or Identify begins or ends, so the
        /// worker works the ruling Rival out again on its next look instead
        /// of at <see cref="RulerMs"/>.</summary>
        private int _rulerDirty;

        private int _passes;
        private int _waitMs;

        /// <summary>The level GG was last told, 0 when it plays nothing.
        /// Written by the worker.</summary>
        private int _posted;

        /// <summary>1 from before a look reads the ruling Rival's level until
        /// what GG plays is recorded, so the crash wait can tell a level may
        /// be on its way to GG. Set before the read, as the Razer Sensa
        /// worker marks its tick: set after it, a level read before the
        /// engine's quiesce could be posted after the wait had read that GG
        /// plays nothing.</summary>
        private int _posting;

        public GameSenseBackend()
            : this(() => new GameSenseClient(), PeripheralOutputs.HapticRulingPlayer, TickMs) { }

        /// <summary>Test seam: the client factory (a local server's address
        /// file), the player lookup, the tick, the ruler's cadence, the
        /// source of a row's level, and the clock the worker reads, which a
        /// test moves itself so the release delay is its own to run out.</summary>
        internal GameSenseBackend(Func<GameSenseClient> client, Func<Guid, int> playerOf, int tickMs, int rulerMs = RulerMs,
            Func<Guid, float> levelOf = null, Func<long> clock = null)
        {
            _client = client;
            _playerOf = playerOf;
            _tickMs = tickMs;
            _rulerMs = rulerMs;
            _levelOf = levelOf ?? PeripheralOutputs.AmplitudeOf;
            _clock = clock ?? (() => Environment.TickCount64);
        }

        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>Passes the worker has made, for the test of its idle
        /// cadence.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        /// <summary>The wait the worker asked for at the end of its last
        /// look, so the test of its cadence reads the tick it chose and
        /// times nothing.</summary>
        internal int WaitMs => Volatile.Read(ref _waitMs);

        public void Start()
        {
            if (_thread != null) return;
            var run = new WorkerRun();
            _run = run;
            PeripheralOutputs.HapticsChanged += OnRulerMoved;
            PeripheralOutputs.IdentifyChanged += OnRulerMoved;
            PeripheralOutputs.LightingChanged += Wake;
            foreach (var pair in GameSenseLights.Maps)
                PeripheralLightMaps.Publish(new OutputPath(OutputFamily.GameSenseColor, pair.Key), pair.Value);
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralGameSense" };
            _thread.Start();
        }

        public void Stop()
        {
            PeripheralOutputs.HapticsChanged -= OnRulerMoved;
            PeripheralOutputs.IdentifyChanged -= OnRulerMoved;
            PeripheralOutputs.LightingChanged -= Wake;
            if (_thread == null) return;
            foreach (var pair in GameSenseLights.Maps)
                PeripheralLightMaps.Remove(new OutputPath(OutputFamily.GameSenseColor, pair.Key));
            _run.Stop = true;
            Wake();
            try { _thread.Join(3000); } catch { }
            _thread = null;
            _run = null;
        }

        /// <summary>A level started, or Identify began or ended on a row: the
        /// ruling Rival is worked out again at once, so the first pulse of an
        /// Identify train plays on GG instead of waiting out
        /// <see cref="RulerMs"/>.</summary>
        private void OnRulerMoved()
        {
            Interlocked.Exchange(ref _rulerDirty, 1);
            Wake();
        }

        private void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        /// <summary>What one color type should show: a color, and the light
        /// colors on top of it (#557), null for none.</summary>
        internal readonly record struct Want(string Type, int Rgb, KeyFrame Keys);

        /// <summary>Each claimed color type's color and light colors, in path
        /// order.</summary>
        internal static List<Want> Colors()
        {
            var colors = new List<Want>();
            foreach (var type in PeripheralLinker.GameSenseColorTypes)
                if (PeripheralOutputs.TryResolveColor(new OutputPath(OutputFamily.GameSenseColor, type), out int rgb,
                        out var ruler))
                    colors.Add(new Want(type, rgb, ruler.Keys));
            return colors;
        }

        /// <summary>Whether the claimed types differ from the ones GG holds.</summary>
        private static bool TypesChanged(List<Want> colors, IReadOnlyCollection<string> held)
        {
            if (colors.Count != held.Count) return true;
            foreach (var want in colors)
            {
                bool found = false;
                foreach (var type in held)
                    if (type == want.Type) { found = true; break; }
                if (!found) return true;
            }
            return false;
        }

        private void Worker(WorkerRun run)
        {
            GameSenseClient client = null;
            try
            {
                client = _client();
                Guid ruler = Guid.Empty;
                long nextRuler = 0, nextConnect = 0, idleSince = 0, nextColors = 0;
                int level = 0;
                while (!run.Stop)
                {
                    Interlocked.Increment(ref _passes);
                    long now = _clock();
                    var path = PeripheralLinker.GameSenseTactilePath;
                    bool rulerMoved = Interlocked.Exchange(ref _rulerDirty, 0) != 0;
                    if (rulerMoved || now >= nextRuler)
                    {
                        nextRuler = now + _rulerMs;
                        if (!PeripheralOutputs.TryResolveHapticRuler(path, _playerOf, out ruler))
                            ruler = Guid.Empty;
                    }
                    var colors = Colors();

                    if (ruler == Guid.Empty && colors.Count == 0)
                    {
                        if (idleSince == 0) idleSince = now;
                        level = 0;
                        // The motor stops at once, and a type nobody claims
                        // goes back to GG at once. GG repeats a level's pulse
                        // until the value changes, so waiting out the release
                        // below would keep a Rival that just left its
                        // controller buzzing for two seconds.
                        if (client.Connected && !(client.Render(0, now) && client.SetColors(colors, now)))
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL GameSense stopped answering");
                        if (client.Connected && now - idleSince >= ReleaseMs)
                        {
                            client.Close();
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL GameSense handed back");
                        }
                        if (!client.Connected)
                        {
                            PeripheralOutputs.SetBackendState(OutputFamily.GameSenseTactile, BackendState.Idle);
                            PeripheralOutputs.SetBackendState(OutputFamily.GameSenseColor, BackendState.Idle);
                        }
                    }
                    else
                    {
                        idleSince = 0;
                        // A look that reads a level is in flight before it
                        // reads it, and both this exchange and the engine's
                        // zeroing of a level are interlocked, so a crash wait
                        // that began after the zeroing either sees this look
                        // or this look reads the zero.
                        if (ruler != Guid.Empty) Interlocked.Exchange(ref _posting, 1);
                        level = ruler == Guid.Empty ? 0 : HapticRumbleShaper.Level(_levelOf(ruler), level);
                        if (!client.Connected && now >= nextConnect)
                        {
                            if (client.TryConnect(now))
                            {
                                nextColors = 0;
                                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL GameSense bound");
                            }
                            else
                            {
                                nextConnect = now + RetryMs;
                            }
                        }
                        if (client.Connected)
                        {
                            bool ok = client.Render(level, now);
                            if (ok && (now >= nextColors || TypesChanged(colors, client.ColorTypes)))
                            {
                                ok = client.SetColors(colors, now);
                                nextColors = now + ColorMs;
                            }
                            if (!ok)
                            {
                                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL GameSense stopped answering");
                                nextConnect = now + RetryMs;
                            }
                        }
                        var state = client.Connected ? BackendState.Connected : BackendState.Waiting;
                        PeripheralOutputs.SetBackendState(OutputFamily.GameSenseTactile,
                            ruler == Guid.Empty ? BackendState.Idle : state);
                        PeripheralOutputs.SetBackendState(OutputFamily.GameSenseColor,
                            colors.Count == 0 ? BackendState.Idle : state);
                    }
                    // What GG plays now: the level just rendered, or nothing
                    // once the connection is gone.
                    Volatile.Write(ref _posted, client.Connected ? level : 0);
                    Volatile.Write(ref _posting, 0);
                    // With GG let go and nothing claimed there is nothing to
                    // tell it, and a level that starts, a color claimed or
                    // Identify wakes the worker. A connection keeps the fast
                    // tick for the release countdown, the heartbeat and the
                    // crash wait.
                    bool idle = ruler == Guid.Empty && colors.Count == 0 && !client.Connected;
                    int wait = idle ? IdleTickMs : _tickMs;
                    Volatile.Write(ref _waitMs, wait);
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL GameSense worker fault: " + ex.GetType().Name);
            }
            finally
            {
                try { client?.Dispose(); } catch { }
                Volatile.Write(ref _posted, 0);
                Volatile.Write(ref _posting, 0);
                PeripheralOutputs.SetBackendState(OutputFamily.GameSenseTactile, BackendState.Idle);
                PeripheralOutputs.SetBackendState(OutputFamily.GameSenseColor, BackendState.Idle);
            }
        }

        /// <summary>Waits up to <paramref name="waitMs"/> for the worker to
        /// tell GG to play nothing, for an abnormal exit after the engine's
        /// quiesce zeroed every level. GG repeats a level's pulse at its rate
        /// until the value changes (gamesense-sdk json-handlers-tactile.md:187-195)
        /// or the game goes 15 seconds without an event
        /// (sending-game-events.md:83), so a process that died first would
        /// leave a Rival buzzing that long. The worker's next tick posts the
        /// zero. A look that read its level before the quiesce still posts
        /// it, so the wait holds while a look that reads a level is in
        /// flight, and only a GG told to play nothing with no such look in
        /// flight ends it: every look after that reads the level the quiesce
        /// left. A look that reads no level, one that only paints colors,
        /// never holds it. True when the worker went quiet inside the wait,
        /// and at once when no worker runs. The Bliss-Box stop waits the same
        /// way (InputManager.QuiesceOutputs).</summary>
        public bool WaitForSilence(int waitMs)
            => SpinWait.SpinUntil(() => !WorkerAlive
                || (Volatile.Read(ref _posting) == 0 && Volatile.Read(ref _posted) == 0), waitMs);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }
}
