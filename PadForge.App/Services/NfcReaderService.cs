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
            svc._running = true;
            svc._thread = new Thread(svc.MonitorLoop)
            {
                IsBackground = true,
                Name = "PadForge NFC Monitor",
            };
            svc._thread.Start();
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

            while (_running)
            {
                List<string> readers;
                try { readers = WinScard.ListReaders(_ctx); }
                catch { readers = new List<string>(); }

                lock (_readersLock) _readers = new List<string>(readers);

                // One PnP sentinel entry + one entry per real reader. Watching
                // the PnP entry makes the blocking call wake on reader add /
                // remove so a reader plugged in after launch is picked up.
                var states = new WinScard.SCARD_READERSTATE[readers.Count + 1];
                states[0] = new WinScard.SCARD_READERSTATE
                {
                    szReader = WinScard.PNP_NOTIFICATION,
                    dwCurrentState = WinScard.SCARD_STATE_UNAWARE,
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

                int rc = WinScard.SCardGetStatusChange(_ctx, WinScard.INFINITE, states, states.Length);
                if (!_running) break;

                if (rc != WinScard.SCARD_S_SUCCESS)
                {
                    uint urc = unchecked((uint)rc);
                    // Cancelled = Dispose() asked us to stop.
                    if (urc == WinScard.SCARD_E_CANCELLED) break;
                    // No readers yet: wait for the PnP entry alone would have
                    // returned. Brief sleep avoids a busy spin when the service
                    // bounces, then re-enumerate.
                    Thread.Sleep(500);
                    continue;
                }

                // states[0] is PnP; entries 1.. are the real readers.
                for (int i = 1; i < states.Length; i++)
                {
                    int ev = states[i].dwEventState;
                    string reader = states[i].szReader;
                    lastState[reader] = ev & ~WinScard.SCARD_STATE_CHANGED;

                    bool changed = (ev & WinScard.SCARD_STATE_CHANGED) != 0;
                    bool present = (ev & WinScard.SCARD_STATE_PRESENT) != 0;
                    if (!changed || !present) continue;

                    // A card just arrived on this reader. Read its UID and
                    // fan out. Connect/transmit can fail transiently (the tag
                    // is mid-seat); a null UID just drops the event.
                    string uid = null;
                    try { uid = WinScard.ReadUid(_ctx, reader); }
                    catch { }
                    if (!string.IsNullOrEmpty(uid))
                    {
                        try { TagDetected?.Invoke(reader, uid); }
                        catch { }
                    }
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            var ctx = _ctx;
            if (ctx != IntPtr.Zero)
            {
                try { WinScard.SCardCancel(ctx); } catch { }
            }
            try { _thread?.Join(1000); } catch { }
            if (ctx != IntPtr.Zero)
            {
                try { WinScard.SCardReleaseContext(ctx); } catch { }
                _ctx = IntPtr.Zero;
            }
            if (Active == this) Active = null;
        }
    }
}
