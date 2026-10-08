using System;
using System.Threading;
using PadForge.Services;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The SteelSeries GameSense worker (#494). GameSense addresses device
    /// types, never one mouse: its tactile handler drives every tactile Rival
    /// at once (gamesense-sdk standard-zones.md:3, "a device category"). So
    /// the tactile path takes the level of the assigned Rival on the virtual
    /// controller with the smallest displayed player number
    /// (<see cref="PeripheralOutputs.TryResolveHapticRuler"/>).
    ///
    /// <para>GG is reached only while a Rival is assigned. The connection is
    /// the first build's client (<see cref="GameSenseTactile"/>): one bind,
    /// a post only when the level changes, a heartbeat while it holds, and
    /// stop_game when nothing is assigned any more, which hands the mice back
    /// to GG. The HTTP calls run here, never on the poll thread.</para>
    /// </summary>
    internal sealed class GameSenseBackend : IDisposable
    {
        internal const int TickMs = 20;

        /// <summary>How often the ruling Rival is worked out again. Each look
        /// walks the assignments, which change at the speed of a drag.</summary>
        internal const int RulerMs = 250;

        /// <summary>How long GG may go unanswered before the next try.</summary>
        internal const int RetryMs = 15000;

        /// <summary>How long the tactile path may sit without a ruler before
        /// the mice go back to GG, so a reassignment does not churn the
        /// registration.</summary>
        internal const int ReleaseMs = 2000;

        private readonly Func<GameSenseTactile> _client;
        private readonly Func<Guid, int> _playerOf;
        private readonly int _tickMs;
        private Thread _thread;
        private volatile bool _stop;
        private int _disposed;

        /// <summary>The level GG was last told, 0 when it plays nothing.
        /// Written by the worker.</summary>
        private int _posted;

        public GameSenseBackend()
            : this(() => new GameSenseTactile(), PeripheralOutputs.HapticRulingPlayer, TickMs) { }

        /// <summary>Test seam: the client factory (a local server's address
        /// file), the player lookup and the tick.</summary>
        internal GameSenseBackend(Func<GameSenseTactile> client, Func<Guid, int> playerOf, int tickMs)
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

        private void Worker()
        {
            GameSenseTactile client = null;
            try
            {
                client = _client();
                Guid ruler = Guid.Empty;
                long nextRuler = 0, nextConnect = 0, rulerLost = 0;
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

                    if (ruler == Guid.Empty)
                    {
                        if (rulerLost == 0) rulerLost = now;
                        level = 0;
                        // The motor stops at once. GG repeats a level's pulse
                        // until the value changes, so waiting out the release
                        // below would keep a Rival that just left its
                        // controller buzzing for two seconds.
                        if (client.Connected && !client.Render(0, now))
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL GameSense stopped answering");
                        if (client.Connected && now - rulerLost >= ReleaseMs)
                        {
                            client.Close();
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL GameSense handed back");
                        }
                        if (!client.Connected)
                            PeripheralOutputs.SetBackendState(OutputFamily.GameSenseTactile, BackendState.Idle);
                    }
                    else
                    {
                        rulerLost = 0;
                        level = HapticRumbleShaper.Level(PeripheralOutputs.AmplitudeOf(ruler), level);
                        if (!client.Connected && now >= nextConnect)
                        {
                            if (client.TryConnect(now))
                            {
                                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL GameSense bound");
                                PeripheralOutputs.SetBackendState(OutputFamily.GameSenseTactile, BackendState.Connected);
                            }
                            else
                            {
                                nextConnect = now + RetryMs;
                                PeripheralOutputs.SetBackendState(OutputFamily.GameSenseTactile, BackendState.Waiting);
                            }
                        }
                        // A level is GG's from the moment its post may land,
                        // so a crash wait never passes a post in flight.
                        if (client.Connected && level != 0) Volatile.Write(ref _posted, level);
                        if (client.Connected && !client.Render(level, now))
                        {
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL GameSense stopped answering");
                            nextConnect = now + RetryMs;
                            PeripheralOutputs.SetBackendState(OutputFamily.GameSenseTactile, BackendState.Waiting);
                        }
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
