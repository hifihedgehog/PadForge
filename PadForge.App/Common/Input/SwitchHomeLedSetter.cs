using System;
using System.Collections.Concurrent;
using System.Threading;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// HOME button LED brightness for Nintendo Switch controllers
    /// (discussion #226, the third #209 Guide LED lane), via SDL's
    /// per-device SDL_SetJoystickLED. The Switch HIDAPI driver converts
    /// max(r, g, b) to a 0-100 brightness and builds a subcommand 0x38
    /// Set HOME Light packet that holds the LED steady at a 4-bit
    /// intensity (SDL_hidapi_switch.c HIDAPI_DriverSwitch_SetJoystickLED
    /// + SetHomeLED), so brightness is genuinely variable, 15 nonzero
    /// hardware steps, not the on/off the reporter assumed (dekuNukem
    /// bluetooth_hid_subcommands_notes.md subcommand 0x38: "LED Start
    /// Intensity. Value x0=0% - xF=100%").
    ///
    /// Unlike the 2015 Steam Controller's process-global hint, this lane
    /// is PER DEVICE: two Switch pads on different slots hold different
    /// brightness values. SDL refuses the write inside its own driver
    /// for devices without the LED (type check in
    /// HIDAPI_DriverSwitch_SetJoystickLED; the Switch 2 driver's
    /// SetJoystickLED is SDL_Unsupported outright), so a masquerading
    /// clone that probes as a licensed controller fails safely.
    ///
    /// The Switch driver's subcommand path waits for the controller's
    /// ACK (~30 ms typical, 100 ms per attempt worst case,
    /// SDL_hidapi_switch.c ReadSubcommandReply) while SDL's global
    /// joystick lock is held, so <see cref="TrySet"/> only enqueues:
    /// a lazy background worker owns every SDL_SetJoystickLED call,
    /// latest-wins per device, change-detected per SDL instance id.
    /// Instance ids are never reused, so a reconnect (fresh id) is
    /// structurally invalidated and the configured brightness reapplies
    /// on the connect-window ApplyGuideLeds pass. Nothing here ever
    /// throws into a caller.
    /// </summary>
    internal static class SwitchHomeLedSetter
    {
        /// <summary>The Switch-family devices SDL's fork actually drives
        /// for the home LED, under Nintendo VID 0x057E (usb_ids.h /
        /// controller_list.h):
        /// 0x2009 Pro Controller and 0x2007 Joy-Con (R), the two types
        /// HIDAPI_DriverSwitch_SetJoystickLED accepts
        /// (SDL_hidapi_switch.c: ProController when not input-only, or
        /// JoyConRight); 0x2008 the combined Joy-Con pair, whose driver
        /// forwards the write to both children and the right one acts
        /// (SDL_hidapi_combined.c HIDAPI_DriverCombined_SetJoystickLED);
        /// 0x200E the charging grip, whose right-slot interface reads
        /// type JoyConRight (SDL_hidapi_switch.c
        /// CalculateControllerType). A LEFT Joy-Con docked alone in the
        /// grip shares this PID and gets the row, but its write refuses
        /// inside SDL's type check, the honest limit of a PID gate.
        ///
        /// Excluded on the SDL source: 0x2006 standalone Joy-Con (L)
        /// (no home LED, SetJoystickLED returns SDL_Unsupported), the
        /// Switch 2 family 0x2066/0x2067/0x2068/0x2069
        /// (SDL_hidapi_switch2.c HIDAPI_DriverSwitch2_SetJoystickLED is
        /// SDL_Unsupported), NSO classic controllers (HasHomeLED false
        /// for Nintendo types past ProController), and third-party pads
        /// (their own VIDs; licensed/unknown probe types are refused by
        /// SDL's driver anyway).</summary>
        internal static bool IsSwitchHomeLedDevice(ushort vendorId, ushort productId)
            => vendorId == 0x057E
            && (productId == 0x2007   // Joy-Con (R)
             || productId == 0x2008   // Joy-Con pair (combined)
             || productId == 0x2009   // Pro Controller
             || productId == 0x200E); // Charging grip (right slot acts)

        // ─────────────────────────────────────────────
        //  Latest-wins request queue + lazy worker
        // ─────────────────────────────────────────────

        private static readonly ConcurrentDictionary<Guid, (UserDevice Ud, int Percent)> _pending = new();
        private const int MaxPendingDevices = 64;

        private static readonly AutoResetEvent _work = new(false);
        private static Thread _worker;
        private static readonly object WorkerGate = new();

        /// <summary>Per-SDL-instance-id change detection: the last percent
        /// SUCCESSFULLY written, so the 30 s apply cadence and
        /// device-update reseeds skip redundant subcommand round-trips.
        /// A failed write is never recorded, so the next apply pass
        /// retries it. Instance ids are monotonic and never reused, so a
        /// reconnected pad always misses the ledger and gets rewritten.</summary>
        private static readonly ConcurrentDictionary<uint, int> _lastWritten = new();
        private const int MaxLedgerEntries = 256;

        /// <summary>Diag dedup only (house GUIDELED change-gated style):
        /// last percent logged per device at enqueue, and last
        /// (percent, result) logged per instance id at write.</summary>
        private static readonly ConcurrentDictionary<Guid, int> _lastLoggedEnqueue = new();
        private static readonly ConcurrentDictionary<uint, (int Pct, bool Ok)> _lastLoggedWrite = new();

        /// <summary>Queues a home LED brightness write for one Switch
        /// device. Latest-wins per device, so a slider drag or a
        /// flash-on-engage macro collapses to the newest value. Never
        /// throws and never blocks on device I/O.</summary>
        public static bool TrySet(UserDevice ud, int percent0to100)
        {
            try
            {
                if (ud == null || ud.InstanceGuid == Guid.Empty) return false;
                if (!IsSwitchHomeLedDevice(ud.VendorId, ud.ProdId)) return false;
                if (_pending.Count >= MaxPendingDevices
                    && !_pending.ContainsKey(ud.InstanceGuid)) return false;

                int pct = Math.Clamp(percent0to100, 0, 100);
                _pending[ud.InstanceGuid] = (ud, pct);
                EnsureWorker();
                _work.Set();

                if (_lastLoggedEnqueue.Count > MaxPendingDevices) _lastLoggedEnqueue.Clear();
                bool changed = !_lastLoggedEnqueue.TryGetValue(ud.InstanceGuid, out int prior)
                               || prior != pct;
                if (changed)
                {
                    _lastLoggedEnqueue[ud.InstanceGuid] = pct;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"GUIDELED switch enqueue vid=0x{ud.VendorId:X4} pid=0x{ud.ProdId:X4} pct={pct}");
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when the ledger holds no successful write of this
        /// exact percent for this SDL instance id. Split out (with
        /// <see cref="RecordWritten"/>) so the apply-on-change contract is
        /// unit-testable without SDL.</summary>
        internal static bool ShouldWrite(uint sdlInstanceId, int percent)
            => !_lastWritten.TryGetValue(sdlInstanceId, out int prior) || prior != percent;

        /// <summary>Records a successful write. The ledger is bounded;
        /// dropping it wholesale at the cap only costs one redundant
        /// rewrite per device (SDL core dedups same-value LED writes
        /// anyway, SDL_joystick.c SDL_SetJoystickLED).</summary>
        internal static void RecordWritten(uint sdlInstanceId, int percent)
        {
            if (_lastWritten.Count >= MaxLedgerEntries
                && !_lastWritten.ContainsKey(sdlInstanceId))
                _lastWritten.Clear();
            _lastWritten[sdlInstanceId] = percent;
        }

        internal static void ResetLedgerForTests()
        {
            _lastWritten.Clear();
            _lastLoggedEnqueue.Clear();
            _lastLoggedWrite.Clear();
        }

        private static void EnsureWorker()
        {
            if (_worker != null) return;
            lock (WorkerGate)
            {
                if (_worker != null) return;
                var t = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "SwitchHomeLed",
                };
                _worker = t;
                t.Start();
            }
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                try
                {
                    _work.WaitOne();
                    foreach (var key in _pending.Keys)
                    {
                        if (!_pending.TryRemove(key, out var req)) continue;
                        WriteOne(req.Ud, req.Percent);
                    }
                }
                catch { /* LED writes are cosmetic; the worker never dies */ }
            }
        }

        private static void WriteOne(UserDevice ud, int percent)
        {
            if (ud == null || !ud.IsOnline) return;
            // The physical wrapper only: a RemotePeerDevice never reaches
            // this lane (the callers relay peers via ShipGuideLed first,
            // the same order the Xbox/Steam lanes use).
            if (ud.Device is not PadForge.Engine.SdlDeviceWrapper w) return;
            uint id = w.SdlInstanceId;
            if (id == 0) return; // closed between enqueue and drain
            if (!ShouldWrite(id, percent)) return;

            bool ok = w.SetHomeLedBrightness(percent);
            if (ok) RecordWritten(id, percent);

            if (_lastLoggedWrite.Count > MaxLedgerEntries) _lastLoggedWrite.Clear();
            bool logChanged = !_lastLoggedWrite.TryGetValue(id, out var priorLog)
                              || priorLog.Pct != percent || priorLog.Ok != ok;
            if (logChanged)
            {
                _lastLoggedWrite[id] = (percent, ok);
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"GUIDELED switch write id={id} vid=0x{ud.VendorId:X4} pid=0x{ud.ProdId:X4} pct={percent} ret={ok}");
            }
        }
    }
}
