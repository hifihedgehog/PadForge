using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Watches for the controllers <see cref="VendorUsbDriverInstaller"/>
    /// binds and binds each one when it appears (hifihedgehog/SDL#33), with no
    /// prompt, as the DualShock 3 is bound on plug-in (Ds3DirectService's
    /// OpenUsb). SDL opens a device at its next device change once WinUSB or
    /// xusb22 serves it, so nothing here talks to SDL. The monitor loop is
    /// SpaceMouseService's.
    /// </summary>
    public sealed class VendorUsbBindingService
    {
        private const int SweepMs = 3000;

        /// <summary>At most one bind per ID in this window. A device Windows
        /// keeps moving back to HidUsb is bound again at this pace, never on
        /// every sweep.</summary>
        private const long RetryMs = 30000;

        /// <summary>Failed binds of one ID before this session stops
        /// trying.</summary>
        private const int MaxFailures = 3;

        private readonly Action<string> _log;
        private volatile bool _running;
        private Thread _monitor;
        private CancellationTokenSource _cts;

        // Monitor thread only.
        private readonly Dictionary<string, (long At, int Failures)> _attempts = new(StringComparer.OrdinalIgnoreCase);
        private string _lastVerdict;

        public VendorUsbBindingService(Action<string> log = null) => _log = log ?? (_ => { });

        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "VendorUsbBinder" };
            _monitor.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _monitor?.Join(3000); } catch { }
            _monitor = null;
        }

        private void MonitorLoop()
        {
            while (_running)
            {
                try { Sweep(); }
                catch (Exception ex) { _log("sweep failed: " + ex.Message); }

                // Sleep in slices so Stop() is never stranded behind a full
                // sweep interval.
                for (int waited = 0; _running && waited < SweepMs; waited += 100)
                    Thread.Sleep(100);
            }
        }

        private void Sweep()
        {
            var nodes = VendorUsbDriverInstaller.ListPresentUsbNodes();
            var wanted = new List<(VendorUsbDriverInstaller.UsbNode Node, VendorUsbDriverInstaller.BindPlan Plan)>();
            foreach (var node in nodes)
            {
                if (VendorUsbDriverInstaller.Plan(node) is { } plan)
                    wanted.Add((node, plan));
            }

            // Said when it changes, so a 3 s sweep cannot flood the ring.
            string verdict = wanted.Count == 0 ? null : string.Join(", ", wanted.Select(w =>
                $"{w.Plan.Name} {w.Node.InstanceId} on {(string.IsNullOrEmpty(w.Node.Service) ? "(no driver)" : w.Node.Service)}"
                + $" for {(w.Plan.Driver == VendorUsbDriverInstaller.BindDriver.Xusb22 ? "xusb22" : "WinUSB")}"));
            if (verdict != _lastVerdict)
            {
                _lastVerdict = verdict;
                if (verdict != null) _log("needs a driver: " + verdict);
            }

            // One bind covers every node its ID names.
            var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (node, plan) in wanted)
            {
                if (!_running) return;
                if (!bound.Add(plan.BindId)) continue;

                long now = Environment.TickCount64;
                _attempts.TryGetValue(plan.BindId, out var last);
                if (last.At != 0 && now - last.At < RetryMs) continue;
                if (last.Failures >= MaxFailures) continue;

                bool ok = VendorUsbDriverInstaller.Bind(plan, node.InstanceId, nodes, _log, _cts.Token);
                int failures = ok ? 0 : last.Failures + 1;
                _attempts[plan.BindId] = (now, failures);
                if (failures == MaxFailures)
                    _log($"{plan.Name}: {MaxFailures} binds of {plan.BindId} failed, so this session stops trying.");
            }
        }
    }
}
