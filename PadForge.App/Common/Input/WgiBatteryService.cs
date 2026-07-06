using System;
using System.Collections.Generic;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Battery levels for Xbox pads via Windows.Gaming.Input (issue #187).
    ///
    /// The XInput battery IOCTL is a dead end for Bluetooth Xbox pads: probed
    /// on real hardware (Series X, VID 045E PID 0B13, Bluetooth), even
    /// System32's own xinput1_4 returns BATTERY_TYPE_DISCONNECTED for a pad
    /// whose battery Windows Settings displays fine. Windows reads it through
    /// the WinRT gaming stack instead, which is what this service taps:
    /// RawGameController.TryGetBatteryReport, the same API family Game Bar
    /// uses. SDL has no WGI battery path (its WGI backend is off here and
    /// does not read battery), so this overlays the SDL-side unknown rather
    /// than replacing the lane: HIDAPI-owned pads (DualSense, Switch) keep
    /// their SDL-reported battery, and the overlay applies only where SDL
    /// says unknown and the vendor is Microsoft.
    ///
    /// HIDMaestro virtual pads appear in WGI too, but they expose no battery
    /// (TryGetBatteryReport returns null capacities), so they contribute no
    /// entries and can never shadow a physical pad here.
    ///
    /// Refresh() runs on the UI thread's 5 s battery tick; WGI statics are
    /// agile and the enumeration is a snapshot read, no events held. Matching
    /// is by (VID, PID): with two identical pads the values assign in
    /// enumeration order, which can swap the two displays but never invents a
    /// value. Same approximation the identical-name BT merge already lives
    /// with.
    /// </summary>
    internal static class WgiBatteryService
    {
        private static readonly Dictionary<(ushort Vid, ushort Pid), Queue<(int Percent, bool Charging)>> _byId = new();

        /// <summary>Re-enumerates WGI controllers and rebuilds the per-(VID,
        /// PID) battery queues. Call once per battery tick, before TryTake.</summary>
        public static void Refresh()
        {
            _byId.Clear();
            try
            {
                var controllers = Windows.Gaming.Input.RawGameController.RawGameControllers;
                foreach (var rgc in controllers)
                {
                    Windows.Devices.Power.BatteryReport report;
                    try { report = rgc.TryGetBatteryReport(); }
                    catch { continue; }
                    if (report == null) continue;

                    int? remaining = report.RemainingCapacityInMilliwattHours;
                    int? full = report.FullChargeCapacityInMilliwattHours;
                    if (remaining == null || full == null || full.Value <= 0) continue;

                    int pct = Math.Clamp((int)Math.Round(100.0 * remaining.Value / full.Value), 0, 100);
                    bool charging = report.Status == Windows.System.Power.BatteryStatus.Charging;

                    var key = (rgc.HardwareVendorId, rgc.HardwareProductId);
                    if (!_byId.TryGetValue(key, out var q))
                        _byId[key] = q = new Queue<(int, bool)>();
                    q.Enqueue((pct, charging));
                }
            }
            catch
            {
                // WGI unavailable (headless session, stack failure): the
                // overlay simply contributes nothing this tick.
            }
        }

        /// <summary>Takes one battery reading for a (VID, PID) pair, or false
        /// when WGI has none left for that hardware id.</summary>
        public static bool TryTake(ushort vid, ushort pid, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (!_byId.TryGetValue((vid, pid), out var q) || q.Count == 0) return false;
            (percent, charging) = q.Dequeue();
            return true;
        }
    }
}
