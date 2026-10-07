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
    /// <see cref="HidHideController.PickUsbDeviceToCycle"/>.
    ///
    /// <para>Work runs on the thread pool, one batch after another, so the
    /// apply that queued it never waits on the device tree. A USB device is
    /// cycled at most once per <see cref="CooldownMs"/>: its return arrives as
    /// a device change that runs the apply again, and a hide another program
    /// keeps taking away would otherwise cycle it in a loop.</para>
    /// </summary>
    internal sealed class HiddenControllerRelease : IDisposable
    {
        internal const long CooldownMs = 30_000;

        /// <summary>Seams for the tests. Production walks the live device
        /// tree, cycles through Nefarius and logs to the diagnostics
        /// ring.</summary>
        internal Func<string, (string usb, string reason)> Resolve { get; set; } = id =>
        {
            string usb = HidHideController.FindUsbDeviceToCycle(id, out string reason);
            return (usb, reason);
        };

        internal Action<string> CyclePort { get; set; } = usb =>
            PnPDevice.GetDeviceByInstanceId(usb, DeviceLocationFlags.Normal).ToUsbPnPDevice().CyclePort();

        internal Func<long> Now { get; set; } = () => Environment.TickCount64;

        /// <summary>The wait between two port cycles of one batch.
        /// HandheldCompanion sleeps 500 ms between cycles because "rapid
        /// back-to-back cycles can cause re-enumeration collisions in the USB
        /// stack" (ControllerManager.cs).</summary>
        internal Action<int> Pause { get; set; } = ms => System.Threading.Thread.Sleep(ms);
        internal const int PauseBetweenCyclesMs = 500;

        internal Action<string> Log { get; set; } = line => PadForge.Engine.SdlDiagLog.WriteLine(line);

        private readonly object _lock = new();
        private readonly Dictionary<string, long> _lastCycle = new(StringComparer.OrdinalIgnoreCase);
        private Task _tail = Task.CompletedTask;
        private bool _disposed;

        /// <summary>Queues the release of the ids a hide just added and
        /// returns at once.</summary>
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
                _tail = _tail.ContinueWith(_ => RunBatch(ids), TaskScheduler.Default);
            }
        }

        /// <summary>The batch queued last, for the tests.</summary>
        internal Task Pending
        {
            get { lock (_lock) return _tail; }
        }

        private void RunBatch(string[] ids)
        {
            var devices = new List<string>();
            foreach (var id in ids)
            {
                (string usb, string reason) found;
                try { found = Resolve(id); }
                catch (Exception ex) { found = (null, ex.GetType().Name); }
                if (found.usb == null)
                {
                    // One line per collection. Its parents resolve the same
                    // way and would only repeat it.
                    if (id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase))
                        Log($"HIDHIDE release skipped {id}: {found.reason}");
                    continue;
                }
                if (!devices.Contains(found.usb, StringComparer.OrdinalIgnoreCase))
                    devices.Add(found.usb);
            }
            bool cycledOne = false;
            foreach (var usb in devices)
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    long now = Now();
                    if (_lastCycle.TryGetValue(usb, out long last) && now - last < CooldownMs)
                    {
                        Log($"HIDHIDE release held {usb}: its port was cycled {(now - last) / 1000} s ago");
                        continue;
                    }
                    _lastCycle[usb] = now;
                }
                if (cycledOne) Pause(PauseBetweenCyclesMs);
                cycledOne = true;
                try
                {
                    CyclePort(usb);
                    Log($"HIDHIDE released {usb}: cycled its USB port, so programs that opened it before the hide lose it");
                }
                catch (Exception ex)
                {
                    Log($"HIDHIDE release failed {usb}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            lock (_lock) _disposed = true;
        }
    }
}
