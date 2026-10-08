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
    /// <para>GG is reached only while something is assigned: a Rival, or a
    /// SteelSeries device or the GG row that a controller lights. Rumble and
    /// colors share one PADFORGE game (<see cref="GameSenseClient"/>): a post
    /// only when a value changes, a heartbeat while one holds, a color event
    /// removed as soon as its type stops being claimed, and stop_game once
    /// nothing has been claimed for <see cref="ReleaseMs"/>, which hands the
    /// devices back to GG. The HTTP calls run here, never on the poll
    /// thread.</para>
    /// </summary>
    internal sealed class GameSenseBackend : IDisposable
    {
        internal const int TickMs = 20;

        /// <summary>How often the ruling Rival is worked out again. Each look
        /// walks the assignments, which change at the speed of a drag.</summary>
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
        private readonly int _tickMs;
        private Thread _thread;
        private volatile bool _stop;
        private int _disposed;

        /// <summary>The level GG was last told, 0 when it plays nothing.
        /// Written by the worker.</summary>
        private int _posted;

        public GameSenseBackend()
            : this(() => new GameSenseClient(), PeripheralOutputs.HapticRulingPlayer, TickMs) { }

        /// <summary>Test seam: the client factory (a local server's address
        /// file), the player lookup and the tick.</summary>
        internal GameSenseBackend(Func<GameSenseClient> client, Func<Guid, int> playerOf, int tickMs)
        {
            _client = client;
            _playerOf = playerOf;
            _tickMs = tickMs;
        }

        internal bool WorkerAlive => _thread?.IsAlive == true;

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Worker) { IsBackground = true, Name = "PeripheralGameSense" };
            _thread.Start();
        }

        public void Stop()
        {
            if (_thread == null) return;
            _stop = true;
            try { _thread.Join(3000); } catch { }
            _thread = null;
        }

        /// <summary>Each claimed color type's color, in path order.</summary>
        internal static List<KeyValuePair<string, int>> Colors()
        {
            var colors = new List<KeyValuePair<string, int>>();
            foreach (var type in PeripheralLinker.GameSenseColorTypes)
                if (PeripheralOutputs.TryResolveColor(new OutputPath(OutputFamily.GameSenseColor, type), out int rgb))
                    colors.Add(new KeyValuePair<string, int>(type, rgb));
            return colors;
        }

        /// <summary>Whether the claimed types differ from the ones GG holds.</summary>
        private static bool TypesChanged(List<KeyValuePair<string, int>> colors, IReadOnlyCollection<string> held)
        {
            if (colors.Count != held.Count) return true;
            foreach (var pair in colors)
            {
                bool found = false;
                foreach (var type in held)
                    if (type == pair.Key) { found = true; break; }
                if (!found) return true;
            }
            return false;
        }

        private void Worker()
        {
            GameSenseClient client = null;
            try
            {
                client = _client();
                Guid ruler = Guid.Empty;
                long nextRuler = 0, nextConnect = 0, idleSince = 0, nextColors = 0;
                int level = 0;
                while (!_stop)
                {
                    long now = Environment.TickCount64;
                    var path = PeripheralLinker.GameSenseTactilePath;
                    if (now >= nextRuler)
                    {
                        nextRuler = now + RulerMs;
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
                        level = ruler == Guid.Empty ? 0 : HapticRumbleShaper.Level(PeripheralOutputs.AmplitudeOf(ruler), level);
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
                            // A level is GG's from the moment its post may
                            // land, so a crash wait never passes a post in
                            // flight.
                            if (level != 0) Volatile.Write(ref _posted, level);
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
                    Thread.Sleep(_tickMs);
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
        /// zero. The Bliss-Box stop waits the same way
        /// (InputManager.QuiesceOutputs).</summary>
        public void WaitForSilence(int waitMs)
            => SpinWait.SpinUntil(() => Volatile.Read(ref _posted) == 0 || !WorkerAlive, waitMs);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
        }
    }
}
