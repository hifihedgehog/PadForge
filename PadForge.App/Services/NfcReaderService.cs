using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Common.Input;

namespace PadForge.Services
{
    /// <summary>
    /// Owns the PC/SC context and a single background monitor thread that
    /// blocks in <c>SCardGetStatusChange</c> and raises <see cref="TagDetected"/>
    /// when a tag is presented to any reader (issue #150, Path A). Event-driven,
    /// not polled: NFC arrival is an event, so this mirrors CursorControlService's
    /// ownership shape (a static <see cref="Active"/> handle, IDisposable
    /// teardown) but never runs a fixed-rate timer.
    ///
    /// Reader enumeration and the blocking wait both tolerate a stopped Smart
    /// Card service or zero readers: those are treated as "no NFC devices",
    /// inert exactly like absent MIDI services. The flow (establish context,
    /// watch the PnP pseudo-reader plus each real reader in one
    /// SCardGetStatusChange call, read the UID on a present-and-changed
    /// transition) is taken from pcsc-sharp's MonitorReaderEvents example
    /// (BSD-2-Clause), read for the call sequence only.
    /// </summary>
    internal sealed class NfcReaderService : IDisposable
    {
        public static NfcReaderService Active { get; private set; }

        /// <summary>Raised on the monitor thread when a tag is read. Carries
        /// the reader name and the tag UID as uppercase hex.</summary>
        public event Action<string, string> TagDetected;

        private IntPtr _ctx = IntPtr.Zero;
        private Thread _thread;
        private volatile bool _running;
        private readonly object _readersLock = new();
        private List<string> _readers = new();

        private NfcReaderService() { }

        /// <summary>Establishes the PC/SC context and starts the monitor.
        /// Returns null (nothing started) when the Smart Card service is
        /// unavailable, so the caller treats NFC as simply absent.</summary>
        public static NfcReaderService Start()
        {
            if (Active != null) return Active;
            var svc = new NfcReaderService();
            int rc = WinScard.SCardEstablishContext(
                WinScard.SCARD_SCOPE_SYSTEM, IntPtr.Zero, IntPtr.Zero, out svc._ctx);
            if (rc != WinScard.SCARD_S_SUCCESS || svc._ctx == IntPtr.Zero)
                return null; // no Smart Card service / no resource manager
            try
            {
                svc._running = true;
                svc._thread = new Thread(svc.MonitorLoop)
                {
                    IsBackground = true,
                    Name = "PadForge NFC Monitor",
                };
                svc._thread.Start();
            }
            catch
            {
                // Thread creation/start failed (OOM-class). Release the context
                // we just established so it does not leak (there is no finalizer
                // and Active was never published).
                try { WinScard.SCardReleaseContext(svc._ctx); } catch { }
                svc._ctx = IntPtr.Zero;
                return null;
            }
            Active = svc;
            return svc;
        }

        /// <summary>Reader names currently visible. Snapshotted by the monitor
        /// thread each wake; the device-registration sweep reads this instead
        /// of calling winscard itself.</summary>
        public List<string> GetReaders()
        {
            lock (_readersLock) return new List<string>(_readers);
        }

        private void MonitorLoop()
        {
            // Per-reader last-known event state, so a tag that stays on the
            // reader fires exactly once (on the empty -> present edge) rather
            // than every wake.
            var lastState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // The PnP pseudo-reader's last-known state. This MUST carry forward
            // like a real reader's: SCardGetStatusChange returns immediately
            // whenever any watched entry's actual state differs from the
            // dwCurrentState passed in, and the PnP reader's real state is never
            // UNAWARE. Re-pinning it to UNAWARE every loop guaranteed a
            // perpetual mismatch and an immediate return, busy-spinning a CPU
            // core. Seed UNAWARE once, then feed back the returned event state.
            int pnpState = WinScard.SCARD_STATE_UNAWARE;

            while (_running)
            {
                List<string> readers;
                try { readers = WinScard.ListReaders(_ctx); }
                catch { readers = new List<string>(); }

                lock (_readersLock) _readers = new List<string>(readers);

                // One PnP sentinel entry + one entry per real reader. Watching
                // the PnP entry makes the blocking call wake on reader add /
                // remove so a reader plugged in after launch is picked up.
                WinScard.SCARD_READERSTATE[] states;
                int rc;
                try
                {
                    states = new WinScard.SCARD_READERSTATE[readers.Count + 1];
                    states[0] = new WinScard.SCARD_READERSTATE
                    {
                        szReader = WinScard.PNP_NOTIFICATION,
                        dwCurrentState = pnpState,
                        rgbAtr = new byte[36],
                    };
                    for (int i = 0; i < readers.Count; i++)
                    {
                        lastState.TryGetValue(readers[i], out int prev);
                        states[i + 1] = new WinScard.SCARD_READERSTATE
                        {
                            szReader = readers[i],
                            dwCurrentState = prev,
                            rgbAtr = new byte[36],
                        };
                    }

                    rc = WinScard.SCardGetStatusChange(_ctx, WinScard.INFINITE, states, states.Length);
                }
                catch
                {
                    // An allocation or marshaling fault must not kill the monitor
                    // thread: nothing nulls Active on a thread death, so the
                    // throttle would never restart it and NFC would go silent for
                    // the session. Back off and retry instead.
                    if (!_running) break;
                    Thread.Sleep(500);
                    continue;
                }
                if (!_running) break;

                if (rc != WinScard.SCARD_S_SUCCESS)
                {
                    uint urc = unchecked((uint)rc);
                    // Cancelled = Dispose() asked us to stop.
                    if (urc == WinScard.SCARD_E_CANCELLED) break;

                    // The Smart Card service stopped (e.g. the last reader was
                    // unplugged on Win10/11, which stops SCardSvr, or the
                    // service was restarted). The existing context is now dead
                    // and winscard never re-binds it to a restarted resource
                    // manager, so we must release it and establish a fresh one
                    // or NFC never recovers for the session. On Windows the
                    // last-reader-unplug case returns SHUTDOWN or the bare
                    // ERROR_INVALID_HANDLE, not NO_SERVICE (pcsc-sharp Changelog
                    // 6.1.0), so all of those route here.
                    if (urc == WinScard.SCARD_E_NO_SERVICE
                        || urc == WinScard.SCARD_E_SERVICE_STOPPED
                        || urc == WinScard.SCARD_E_INVALID_HANDLE
                        || urc == WinScard.SCARD_E_SHUTDOWN
                        || urc == WinScard.ERROR_INVALID_HANDLE)
                    {
                        Thread.Sleep(1000);
                        if (!_running) break;
                        if (TryReestablishContext())
                        {
                            lastState.Clear();
                            pnpState = WinScard.SCARD_STATE_UNAWARE;
                        }
                        continue;
                    }

                    // Other transient error (no readers yet, timeout). Brief
                    // sleep avoids a busy spin, then re-enumerate.
                    Thread.Sleep(500);
                    continue;
                }

                // Carry the PnP entry's returned state forward (masked of the
                // CHANGED bit) so the next call blocks instead of returning
                // immediately. This is the busy-spin fix.
                pnpState = states[0].dwEventState & ~WinScard.SCARD_STATE_CHANGED;

                // states[0] is PnP; entries 1.. are the real readers.
                for (int i = 1; i < states.Length; i++)
                {
                    int ev = states[i].dwEventState;
                    string reader = states[i].szReader;
                    lastState[reader] = ev & ~WinScard.SCARD_STATE_CHANGED;

                    bool changed = (ev & WinScard.SCARD_STATE_CHANGED) != 0;
                    bool present = (ev & WinScard.SCARD_STATE_PRESENT) != 0;
                    if (!changed || !present) continue;

                    // A card just arrived on this reader. ReadUid establishes
                    // its own short-lived context (it never touches the
                    // monitored context), so a connect/transmit racing teardown
                    // cannot crash. A null UID (mid-seat tag, foreign card) just
                    // drops the event.
                    string uid = null;
                    try { uid = WinScard.ReadUid(reader); }
                    catch { }
                    if (!string.IsNullOrEmpty(uid))
                    {
                        try { TagDetected?.Invoke(reader, uid); }
                        catch { }
                    }
                }

                // Prune state for readers that vanished, so a pathological
                // stack that renames a reader on every reconnect cannot grow
                // lastState without bound across a long session.
                if (lastState.Count > readers.Count)
                {
                    var live = new HashSet<string>(readers, StringComparer.OrdinalIgnoreCase);
                    var stale = new List<string>();
                    foreach (var k in lastState.Keys)
                        if (!live.Contains(k)) stale.Add(k);
                    foreach (var k in stale) lastState.Remove(k);
                }
            }
        }

        /// <summary>Releases the dead context and establishes a fresh one in
        /// place, so a stopped-then-restarted Smart Card service is recovered
        /// without restarting PadForge. Returns false (caller retries) on
        /// failure.</summary>
        private bool TryReestablishContext()
        {
            var old = _ctx;
            if (old != IntPtr.Zero)
            {
                try { WinScard.SCardReleaseContext(old); } catch { }
            }
            int rc = WinScard.SCardEstablishContext(
                WinScard.SCARD_SCOPE_SYSTEM, IntPtr.Zero, IntPtr.Zero, out var fresh);
            if (rc != WinScard.SCARD_S_SUCCESS || fresh == IntPtr.Zero)
            {
                _ctx = IntPtr.Zero;
                return false;
            }
            _ctx = fresh;
            return true;
        }

        public void Dispose()
        {
            _running = false;
            // Best-effort cancel of the blocking SCardGetStatusChange on whatever
            // context the monitor currently holds. A snapshot is fine: after
            // _running = false the monitor will not re-establish again (it
            // re-checks _running before TryReestablishContext), so it exits at
            // its next wake regardless of which context the cancel hit.
            var cancelCtx = _ctx;
            if (cancelCtx != IntPtr.Zero)
            {
                try { WinScard.SCardCancel(cancelCtx); } catch { }
            }

            bool exited = true;
            try { exited = _thread?.Join(2000) ?? true; } catch { exited = true; }

            // Release the context ONLY after the monitor thread has exited. The
            // re-establish path mutates _ctx on the monitor thread, so reading it
            // for release while the thread is still alive would risk a torn read
            // and a double-release/leak (the round-3 finding). Post-exit, _ctx is
            // stable and holds the final context the monitor left; any context it
            // swapped out during re-establish was already released by the monitor
            // itself. If the join times out, leave the context for the OS to
            // reclaim at process exit rather than release it under an in-flight
            // native call. App-shutdown only, so the leak is bounded.
            if (exited)
            {
                var ctx = _ctx;
                if (ctx != IntPtr.Zero)
                {
                    try { WinScard.SCardReleaseContext(ctx); } catch { }
                }
                _ctx = IntPtr.Zero;
            }
            if (Active == this) Active = null;
        }
    }
}
