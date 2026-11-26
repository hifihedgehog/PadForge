using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nefarius.Utilities.DeviceManagement.Extensions;
using Nefarius.Utilities.DeviceManagement.PnP;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// Takes a controller PadForge just hid away from every program that
    /// opened it before the hide (#484 follow-up). HidHide decides access only
    /// when a handle is opened, so such a program kept the controller until it
    /// reconnected, and the docs used to send users off to reconnect it,
    /// restart the game or reboot. For a USB controller that was already
    /// connected, PadForge now cycles its hub port, the way HandheldCompanion
    /// releases a hidden pad, and the controller comes back where only
    /// whitelisted programs can open it. The device decision is
    /// <see cref="HidHideController.PickUsbDeviceToCycle(IReadOnlyList{HidHideController.ReleaseHop}, int, Func{string, int}, TimeSpan?, out string)"/>.
    ///
    /// <para>Work runs on the thread pool, one batch after another, so the
    /// apply that queued it never waits on the device tree. A batch can run
    /// long after that apply, behind other batches or thirty seconds later
    /// for a held cycle, so it decides from what is true when it runs. It
    /// reads HidHide's blacklist and cycles only for ids still on it
    /// (<see cref="ReadBlacklist"/>), and it reads how many of PadForge's
    /// controller rows sit on each USB device (<see cref="ControllerRows"/>).
    /// A controller shown again in the meantime stays connected, and so does
    /// a receiver another pad has joined. One thing is kept from the
    /// hand-over, its time: how long a controller had been connected is
    /// counted up to the moment its ids were handed over
    /// (<see cref="HidHideController.ReleaseArrivalGrace"/>), so a wait
    /// does not age a controller that connected moments before its hide
    /// into a cycle.</para>
    ///
    /// <para>A batch that cannot decide yet keeps its ids and asks again
    /// every <see cref="RetryMs"/>. That is so for up to
    /// <see cref="ListingWaitMs"/> while PadForge's engine has started and
    /// not yet listed its devices (<see cref="RowsPending"/>), a wait each
    /// engine run starts anew, and for up to <see cref="ListWaitMs"/> while
    /// HidHide's list does not read, as when another program holds HidHide's
    /// control device. An id that waited out the list is dropped without a
    /// cycle, because the controller may be in use by the time that program
    /// lets go, and its hide is left as it is. A batch whose list does not
    /// read also ends when HidHide's driver does not answer at all, since
    /// nothing is hidden then.</para>
    ///
    /// <para>A USB device is cycled at most once per
    /// <see cref="CooldownMs"/>: its return arrives as a device change that
    /// runs the apply again, and a hide another program keeps taking away
    /// would otherwise cycle it in a loop. A cycle the cooldown holds back
    /// runs once the cooldown ends, as a batch of its own, so hiding a
    /// controller again soon after showing it still takes it from a program
    /// that opened it in between.</para>
    ///
    /// <para>The HID ids of every cycled controller are kept for
    /// <see cref="TakeCycledIds"/>, each with the time its port was cycled.
    /// The controller comes back on a new device stack, and the apply opens
    /// it once more there, which the time lets it tell from the stack the
    /// controller had before.</para>
    /// </summary>
    internal sealed class HiddenControllerRelease : IDisposable
    {
        internal const long CooldownMs = 30_000;

        /// <summary>How long a batch that could not decide waits before it
        /// asks again.</summary>
        internal const long RetryMs = 2_000;

        /// <summary>How long batches wait for the engine to list its devices
        /// before they decide without the listing.</summary>
        internal const long ListingWaitMs = 10_000;

        /// <summary>How long a hidden id waits on a blacklist that does not
        /// read before it is dropped without a cycle, counted on the tick
        /// clock (<see cref="Now"/>) from the first time its batch found the
        /// list unread.</summary>
        internal const long ListWaitMs = 60_000;

        /// <summary>Seams for the tests. Production walks the live device
        /// tree, cycles through Nefarius and logs to the diagnostics ring.
        /// <see cref="Resolve"/> takes a hidden id, the count of PadForge's
        /// controller rows on a USB device, and when the id was handed to
        /// the release.</summary>
        internal Func<string, Func<string, int>, DateTime, (string usb, string reason)> Resolve { get; set; } = (id, controllersOn, handedOverUtc) =>
        {
            string usb = HidHideController.FindUsbDeviceToCycle(id, controllersOn, handedOverUtc, out string reason);
            return (usb, reason);
        };

        internal Action<string> CyclePort { get; set; } = usb =>
        {
            // A test host cycles no port of the PC it runs on
            // (HidHideController.SeamsOnlyForTest). A cycle that fails
            // throws, and so does one that is kept from the hub.
            if (HidHideController.SeamsOnlyForTest)
            {
                HidHideController.OffLimitsRefused?.Invoke("cycle " + usb);
                throw new InvalidOperationException("no port is cycled in a test host with no seam for the cycle");
            }
            CycleHubPort(usb);
        };

        /// <summary>The hub port cycle itself, IOCTL_USB_HUB_CYCLE_PORT
        /// through Nefarius.</summary>
        private static void CycleHubPort(string usb)
        {
            HidHideController.ThrowIfOffLimits("Cycling a USB hub port");
            PnPDevice.GetDeviceByInstanceId(usb, DeviceLocationFlags.Normal).ToUsbPnPDevice().CyclePort();
        }

        internal Func<long> Now { get; set; } = () => Environment.TickCount64;

        /// <summary>The wall clock a hand-over and a cycle are stamped
        /// with, the clock Windows stamps a device's arrival with.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>The wait between two port cycles of one batch.
        /// HandheldCompanion sleeps 500 ms between cycles because "rapid
        /// back-to-back cycles can cause re-enumeration collisions in the USB
        /// stack" (ControllerManager.cs).</summary>
        internal Action<int> Pause { get; set; } = ms => System.Threading.Thread.Sleep(ms);
        internal const int PauseBetweenCyclesMs = 500;

        internal Action<string> Log { get; set; } = line => PadForge.Engine.SdlDiagLog.WriteLine(line);

        /// <summary>Runs the action once the given milliseconds have passed,
        /// for a cycle the cooldown held back and for a batch that asks
        /// again.</summary>
        internal Action<long, Action> Schedule { get; set; } = (ms, action) =>
            Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, ms))).ContinueWith(_ => action(), TaskScheduler.Default);

        /// <summary>The ids HidHide's blacklist names, read once when a batch
        /// runs. Only an id still on the list is cycled for. The ids are null
        /// when the list does not read, and the second value then says
        /// whether HidHide's driver is there at all. Its control device takes
        /// one client at a time (ControlDevice.c, WdfDeviceInitSetExclusive),
        /// so while another program holds it an open is refused with access
        /// denied. That refusal shows the driver there
        /// (<see cref="HidHideArm64Installer.ControlDeviceAnswers"/>), and
        /// the list reads again once that program lets go.</summary>
        internal Func<(IReadOnlyCollection<string> ids, bool driverThere)> ReadBlacklist { get; set; } = () =>
        {
            var ids = HidHideController.GetBlacklist();
            if (ids != null) return (ids, true);
            bool opened = HidHideController.TryProbe(out int error);
            return (null, HidHideArm64Installer.ControlDeviceAnswers(opened, error));
        };

        /// <summary>How many of PadForge's controller rows sit on each device
        /// node, keyed by instance id, read once when a batch runs.
        /// InputService supplies it. Null counts no rows, which leaves the
        /// device tree's own count to decide.</summary>
        internal Func<IReadOnlyDictionary<string, int>> ControllerRows { get; set; } = () => null;

        /// <summary>Whether PadForge's engine has started and not yet listed
        /// its devices. Its rows say what is connected a moment later, so a
        /// batch waits for them, for up to <see cref="ListingWaitMs"/>,
        /// where it would count a receiver's pads from rows that all still
        /// read offline. InputService supplies it. While the engine is
        /// stopped no listing is coming, and the count takes the rows whose
        /// nodes Windows lists as present.</summary>
        internal Func<bool> RowsPending { get; set; } = () => false;

        /// <summary>A hidden id whose batch could not decide yet: when the
        /// id was handed to the release, on the wall clock Windows dates an
        /// arrival with, and when its batch first found HidHide's list
        /// unread, on the tick clock (<see cref="Now"/>), -1 while it has
        /// not.</summary>
        private readonly record struct Waiting(DateTime HandedOverUtc, long ListWaitSince);

        private readonly object _lock = new();
        private readonly Dictionary<string, long> _lastCycle = new(StringComparer.OrdinalIgnoreCase);
        // USB device to the hidden ids whose cycle waits for the cooldown,
        // each with the time it was handed over.
        private readonly Dictionary<string, Dictionary<string, DateTime>> _held = new(StringComparer.OrdinalIgnoreCase);
        // Hidden HID id to the time its USB device's port was cycled.
        private readonly Dictionary<string, DateTime> _cycledIds = new(StringComparer.OrdinalIgnoreCase);
        // Hidden ids whose batch could not decide yet, and whether the timer
        // that asks again for them is set.
        private readonly Dictionary<string, Waiting> _undecided = new(StringComparer.OrdinalIgnoreCase);
        private bool _retrySet;
        // What the waiting batches wait on, so each wait is logged once.
        private string _waitingOn;
        // When the wait for the engine's listing began, on the tick clock
        // (Now), -1 while none runs, and whether that wait has run out.
        private long _listingWaitSince = -1;
        private bool _listingWaitRanOut;
        private Task _tail = Task.CompletedTask;
        private bool _disposed;
        // When a port was last cycled, on the tick clock (Now), -1 for never.
        private long _lastCycleAt = -1;

        /// <summary>When PadForge last cycled a USB port, on the tick clock
        /// (<see cref="Now"/>), or -1 when it never has. The controller on
        /// that port drops and comes back by PadForge's own hand, and the
        /// device notes say nothing of it (#600).</summary>
        internal long LastCycleAt => System.Threading.Volatile.Read(ref _lastCycleAt);

        /// <summary>Queues the release of the ids a hide just added and
        /// returns at once. The batch reads what it decides on when it
        /// runs. Each id carries the time of this call with it.</summary>
        public void Release(IEnumerable<string> hiddenIds)
        {
            var ids = (hiddenIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ids.Length == 0) return;
            lock (_lock)
            {
                if (_disposed) return;
                DateTime handedOverUtc = UtcNow();
                var batch = ids.Select(id => (id, handedOverUtc)).ToArray();
                _tail = _tail.ContinueWith(_ => RunBatch(batch), TaskScheduler.Default);
            }
        }

        /// <summary>The batch queued last, for the tests.</summary>
        internal Task Pending
        {
            get { lock (_lock) return _tail; }
        }

        /// <summary>The HID ids of the controllers cycled since the last
        /// call, each with the time of its cycle, and forgets them.</summary>
        internal Dictionary<string, DateTime> TakeCycledIds()
        {
            lock (_lock)
            {
                var taken = new Dictionary<string, DateTime>(_cycledIds, StringComparer.OrdinalIgnoreCase);
                _cycledIds.Clear();
                return taken;
            }
        }

        private void RunBatch((string id, DateTime handedOverUtc)[] ids)
        {
            (string id, DateTime handedOverUtc)[] all;
            lock (_lock)
            {
                // A batch queued before Dispose reads nothing after it.
                if (_disposed) return;
                // The ids an earlier batch could not decide on ride with
                // this one, so a controller is decided on once. An id handed
                // over again since then was hidden again, and that hide
                // starts its own waits.
                foreach (var (id, handedOverUtc) in ids)
                {
                    if (!_undecided.TryGetValue(id, out var waiting) || handedOverUtc > waiting.HandedOverUtc)
                        _undecided[id] = new Waiting(handedOverUtc, -1);
                }
                all = _undecided.Select(w => (w.Key, w.Value.HandedOverUtc)).ToArray();
            }
            if (all.Length == 0) return;

            // What is true now decides, not what was true when the batch was
            // queued: which ids HidHide still hides, and how many controllers
            // sit on each USB device.
            bool rowsPending;
            try { rowsPending = RowsPending(); }
            catch (Exception) { rowsPending = false; }
            if (rowsPending)
            {
                // The wait is timed from the first batch that met it, so a
                // release that arrives meanwhile neither shortens it nor
                // starts it over.
                bool wait, ranOut;
                lock (_lock)
                {
                    long now = Now();
                    if (_listingWaitSince < 0) _listingWaitSince = now;
                    wait = now - _listingWaitSince < ListingWaitMs;
                    ranOut = !wait && !_listingWaitRanOut;
                    if (ranOut) _listingWaitRanOut = true;
                }
                if (wait)
                {
                    AskAgain("PadForge's engine has not listed its devices yet, so the controllers on a USB device are not known");
                    return;
                }
                // An engine suspended in the background lists nothing until
                // PadForge has the focus again. The count then takes the
                // rows whose nodes Windows lists as present, as it does
                // while the engine is stopped.
                if (ranOut)
                    Log($"HIDHIDE release goes ahead: the engine has not listed its devices after {ListingWaitMs / 1000} s");
            }
            else
            {
                // A wait for the listing belongs to one engine run. This run
                // has listed its devices, or the engine is stopped, so a
                // batch that still waits here on HidHide's list waits for
                // the next run's listing from that run's start.
                lock (_lock)
                {
                    _listingWaitSince = -1;
                    _listingWaitRanOut = false;
                }
            }

            HashSet<string> hidden = null;
            bool driverThere = true;
            try
            {
                var listed = ReadBlacklist();
                if (listed.ids != null) hidden = new HashSet<string>(listed.ids, StringComparer.OrdinalIgnoreCase);
                driverThere = listed.driverThere;
            }
            catch (Exception)
            {
                // A read that throws is a list that did not read.
            }
            if (hidden == null)
            {
                if (driverThere)
                {
                    WaitOnTheList();
                    return;
                }
                lock (_lock) Decided();
                Log("HIDHIDE release dropped: HidHide's driver does not answer, so nothing is hidden");
                return;
            }
            lock (_lock)
            {
                if (_disposed) return;
                Decided();
            }
            var stillHidden = all.Where(entry => hidden.Contains(entry.id)).ToArray();
            if (stillHidden.Length == 0) return;

            IReadOnlyDictionary<string, int> rows;
            try { rows = ControllerRows(); }
            catch (Exception ex)
            {
                // With no count, a receiver cannot be told from a single
                // controller, and cycling a receiver drops every pad on it.
                Log($"HIDHIDE release dropped: PadForge's controller rows did not read ({ex.GetType().Name}), so no USB device is known to carry one controller");
                return;
            }
            Func<string, int> controllersOn = usb =>
                usb != null && rows != null && rows.TryGetValue(usb, out int n) ? n : 0;

            var devices = new List<(string usb, List<(string id, DateTime handedOverUtc)> ids)>();
            foreach (var entry in stillHidden)
            {
                string id = entry.id;
                (string usb, string reason) found;
                // The arrival grace is counted up to the hand-over. Counted
                // up to this moment, a wait would age a controller that
                // connected moments before its hide into a cycle.
                try { found = Resolve(id, controllersOn, entry.handedOverUtc); }
                catch (Exception ex) { found = (null, ex.GetType().Name); }
                if (found.usb == null)
                {
                    // One line per collection. Its parents resolve the same
                    // way and would only repeat it.
                    if (id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase))
                        Log($"HIDHIDE release skipped {id}: {found.reason}");
                    continue;
                }
                int at = devices.FindIndex(d => string.Equals(d.usb, found.usb, StringComparison.OrdinalIgnoreCase));
                if (at < 0) devices.Add((found.usb, new List<(string id, DateTime handedOverUtc)> { entry }));
                else devices[at].ids.Add(entry);
            }
            bool cycledOne = false;
            foreach (var (usb, usbIds) in devices)
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    long now = Now();
                    if (_lastCycle.TryGetValue(usb, out long last) && now - last < CooldownMs)
                    {
                        Log($"HIDHIDE release held {usb}: its port was cycled {(now - last) / 1000} s ago, so it is cycled once {CooldownMs / 1000} s have passed");
                        HoldUntilCooldownEnds(usb, usbIds, CooldownMs - (now - last));
                        continue;
                    }
                    _lastCycle[usb] = now;
                    System.Threading.Volatile.Write(ref _lastCycleAt, now);
                }
                if (cycledOne) Pause(PauseBetweenCyclesMs);
                cycledOne = true;
                try
                {
                    // Stamped before the cycle, so the arrival that follows
                    // it is later than the stamp.
                    DateTime cycledUtc = UtcNow();
                    CyclePort(usb);
                    lock (_lock)
                    {
                        foreach (var (id, _) in usbIds)
                            if (id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase))
                                _cycledIds[id] = cycledUtc;
                    }
                    Log($"HIDHIDE released {usb}: cycled its USB port, so programs that opened it before the hide lose it");
                }
                catch (Exception ex)
                {
                    Log($"HIDHIDE release failed {usb}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>Queues one batch for the USB device once the cooldown
        /// ends. A device already waiting takes the new ids into the batch it
        /// has, so a device never waits on two. The batch carries the ids,
        /// each with the time it was handed over, and decides afresh when it
        /// runs. An id held twice keeps the later time, the time of the hide
        /// now in force. The caller holds the lock.</summary>
        private void HoldUntilCooldownEnds(string usb, List<(string id, DateTime handedOverUtc)> ids, long waitMs)
        {
            bool waitsAlready = _held.TryGetValue(usb, out var held);
            if (!waitsAlready) _held[usb] = held = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, handedOverUtc) in ids)
            {
                if (!held.TryGetValue(id, out DateTime earlier) || handedOverUtc > earlier)
                    held[id] = handedOverUtc;
            }
            if (waitsAlready) return;
            Schedule(waitMs, () =>
            {
                lock (_lock)
                {
                    // Dispose emptied the held work, so nothing is queued
                    // after it.
                    if (!_held.Remove(usb, out var due)) return;
                    var batch = due.Select(d => (d.Key, d.Value)).ToArray();
                    _tail = _tail.ContinueWith(_ => RunBatch(batch), TaskScheduler.Default);
                }
            });
        }

        /// <summary>HidHide's list did not read while its driver answers.
        /// Each waiting id is kept for <see cref="ListWaitMs"/> from the
        /// first time its batch found the list unread, and dropped without a
        /// cycle after that. HidHide's control device takes one client at a
        /// time, and a program can hold it for hours, so a cycle that waited
        /// without a limit ran whenever that program let go, in the middle
        /// of whatever the controller was then used for. One line names the
        /// ids dropped. Their hide is not the release's to change.</summary>
        private void WaitOnTheList()
        {
            List<string> dropped = null;
            bool waiting;
            lock (_lock)
            {
                // After Dispose nothing waits, and this does nothing.
                long now = Now();
                foreach (string id in _undecided.Keys.ToArray())
                {
                    Waiting entry = _undecided[id];
                    if (entry.ListWaitSince < 0)
                    {
                        _undecided[id] = entry with { ListWaitSince = now };
                    }
                    else if (now - entry.ListWaitSince >= ListWaitMs)
                    {
                        _undecided.Remove(id);
                        (dropped ??= new List<string>()).Add(id);
                    }
                }
                waiting = _undecided.Count > 0;
                if (!waiting) Decided();
            }
            if (dropped != null)
            {
                dropped.Sort(StringComparer.OrdinalIgnoreCase);
                Log($"HIDHIDE release dropped [{string.Join(" | ", dropped)}]: HidHide's blacklist did not read for {ListWaitMs / 1000} s, so no USB port is cycled for these ids. Their hide is left as it is");
            }
            if (waiting) AskAgain("HidHide's blacklist did not read, so no controller is known to be hidden");
        }

        /// <summary>Nothing waits any more: a batch has what it decides on,
        /// or the ids that waited were all dropped. The next batch that
        /// cannot decide starts its own wait. The caller holds the
        /// lock.</summary>
        private void Decided()
        {
            _undecided.Clear();
            _waitingOn = null;
            _listingWaitSince = -1;
            _listingWaitRanOut = false;
        }

        /// <summary>Keeps the ids of a batch that could not decide and queues
        /// one batch for them after <see cref="RetryMs"/>. One timer however
        /// many batches wait, and one log line for each thing they wait
        /// on.</summary>
        private void AskAgain(string waitingOn)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (!string.Equals(_waitingOn, waitingOn, StringComparison.Ordinal))
                {
                    _waitingOn = waitingOn;
                    Log($"HIDHIDE release waits: {waitingOn}. It asks again every {RetryMs / 1000} s");
                }
                if (_retrySet) return;
                _retrySet = true;
                Schedule(RetryMs, () =>
                {
                    lock (_lock)
                    {
                        _retrySet = false;
                        // Nothing waits any more once Dispose has emptied
                        // the ids or a batch that could decide has taken
                        // them along.
                        if (_undecided.Count == 0) return;
                        _tail = _tail.ContinueWith(_ => RunBatch(Array.Empty<(string, DateTime)>()), TaskScheduler.Default);
                    }
                });
            }
        }

        /// <summary>Stops the work. Held cycles and waiting ids are dropped,
        /// a batch still queued reads nothing, and a batch in flight cycles
        /// nothing more.</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _held.Clear();
                _undecided.Clear();
            }
        }
    }
}
