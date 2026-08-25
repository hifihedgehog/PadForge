using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Hosts the one <see cref="HandheldChordEngine"/> for the process
    /// (issue #343): hands it to the low-level hooks, and runs the worker
    /// that does what a hook callback must not do itself. The hook thread
    /// only feeds events and answers pass or swallow. This worker injects
    /// the replays and the Win mask through SendInput, and ticks the engine
    /// every 10 ms while a prefix is held or a capture is armed so held
    /// keys time out and replay on schedule.
    /// </summary>
    internal static class HandheldChordRuntime
    {
        public static HandheldChordEngine Engine { get; } = new HandheldChordEngine();

        private static readonly object _lock = new();
        private static Thread _worker;
        private static AutoResetEvent _wake;
        private static volatile bool _running;

        /// <summary>Tick cadence while the engine has timed work. Well under
        /// the 100 ms hold window, so a replay lands within one slice.</summary>
        private const int TickMs = 10;

        public static bool IsRunning => _running;

        /// <summary>True when the hooks must stay installed for this
        /// feature even with nothing else to suppress.</summary>
        public static bool NeedsHooks =>
            HandheldButtonRegistry.FeatureEnabled
            && (Engine.HasChords || Engine.IsCapturing || HandheldButtonRegistry.LearnCaptureActive);

        /// <summary>The hook host tore the hooks down (nothing left to
        /// suppress). Ups the engine will never see would otherwise leave
        /// keys down in it and held prefixes would replay stale on the next
        /// install, the InputHookManager.Stop clearing rule.</summary>
        public static void OnHooksDetached() => Engine.Reset();

        public static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
                _wake = new AutoResetEvent(false);
                InputHookManager.ChordEngine = Engine;
                InputHookManager.ChordWorkPending += Wake;
                _worker = new Thread(Loop) { IsBackground = true, Name = "PadForge.HandheldChords" };
                _worker.Start();
            }
        }

        public static void Stop()
        {
            Thread worker;
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                InputHookManager.ChordWorkPending -= Wake;
                InputHookManager.ChordEngine = null;
                Engine.SetChords(null);
                Engine.Reset();
                worker = _worker;
                _worker = null;
                try { _wake?.Set(); } catch { }
            }
            worker?.Join(1000);
            lock (_lock)
            {
                _wake?.Dispose();
                _wake = null;
            }
        }

        private static void Wake()
        {
            try { _wake?.Set(); } catch (ObjectDisposedException) { }
        }

        private static void Loop()
        {
            var replays = new List<(int Code, bool Down)>();
            var wake = _wake;
            while (_running)
            {
                int wait = Engine.HasPendingWork ? TickMs : Timeout.Infinite;
                try { wake.WaitOne(wait); }
                catch (ObjectDisposedException) { break; }
                if (!_running) break;

                try
                {
                    Engine.Tick(Environment.TickCount64);
                    if (Engine.TakeWinMask())
                        InputHookManager.InjectWinMask();
                    Engine.DrainReplays(replays);
                    foreach (var (code, down) in replays)
                        InputHookManager.InjectReplay(code, down);
                    replays.Clear();
                }
                catch (Exception ex)
                {
                    // A SendInput failure (a UIPI-blocked target, a dying
                    // session) must never take the worker down with it; the
                    // next event queues fresh work.
                    PadForge.Engine.SdlDiagLog.WriteLine("Handheld chords: worker error " + ex.Message);
                    replays.Clear();
                }
            }
        }
    }
}
