using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Common.Input.Falcon;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // Novint Falcons opened through Force Dimension's library (issue #521),
        // the analog keyboards' shape: a sweep on a worker opens new devices
        // and queues them, and the poll thread registers and retires rows.
        private const int FalconSweepMs = 3000;
        /// <summary><c>DHD_ERROR_DEVICE_IN_USE</c> (<c>dhdc.h</c>).</summary>
        private const int DhdErrorDeviceInUse = 20;

        private readonly object _falconLock = new object();
        private readonly Dictionary<int, FalconDevice> _falconOpen = new();
        private readonly List<FalconDevice> _falconPending = new();
        private readonly HashSet<int> _falconOtherTypes = new();
        private int _falconLastCount = -1;
        private volatile bool _falconSweepRunning;
        private volatile bool _falconSuppressed;
        private volatile bool _falconAnyRows;
        private long _falconNextSweepTicks;

        /// <summary>The open Falcons, for the Settings card and Calibrate.</summary>
        internal IReadOnlyList<FalconDevice> FalconRows
        {
            get { lock (_falconLock) return _falconOpen.Values.ToList(); }
        }

        /// <summary>Phase 1j3 (issue #521). Off with nothing to retire: three
        /// volatile reads and out.</summary>
        private bool UpdateFalconDevices()
        {
            if (_falconSuppressed)
                return false;

            bool enabled = FalconRuntime.Enabled;
            if (!enabled && !_falconAnyRows)
                return false;

            long now = Environment.TickCount64;
            if (enabled && !_falconSweepRunning && now >= _falconNextSweepTicks)
            {
                _falconSweepRunning = true;
                _falconNextSweepTicks = now + FalconSweepMs;
                Task.Run(() =>
                {
                    try { FalconSweep(); }
                    catch (Exception ex) { PadForge.Engine.SdlDiagLog.WriteLine("Falcon: sweep failed: " + ex.Message); }
                    finally { _falconSweepRunning = false; }
                });
            }

            bool changed = false;
            lock (_falconLock)
            {
                if (_falconSuppressed)
                    return false;

                foreach (var dev in _falconPending)
                {
                    if (!enabled || !dev.IsAttached)
                    {
                        DisposeFalconAsync(dev);
                        continue;
                    }
                    try
                    {
                        UserDevice ud = FindOrCreateUserDevice(dev.InstanceGuid, dev.ProductGuid);
                        ud.LoadFromExternalDevice(dev);
                        ud.IsOnline = true;
                        dev.Reach = FalconDevice.ParseReach(ud.FalconCalibration);
                        _falconOpen[dev.Id] = dev;
                        MarkChanged(ref changed, "falcon", "+ " + dev.DevicePath);
                    }
                    catch (Exception ex)
                    {
                        DisposeFalconAsync(dev);
                        RaiseDeviceError("Error registering a Novint Falcon", ex);
                    }
                }
                _falconPending.Clear();

                List<int> gone = null;
                foreach (var kvp in _falconOpen)
                {
                    bool removedByUser = FindOnlineDeviceByInstanceGuid(kvp.Value.InstanceGuid) == null;
                    if (!enabled || !kvp.Value.IsAttached || removedByUser)
                        (gone ??= new List<int>()).Add(kvp.Key);
                }
                if (gone != null)
                {
                    foreach (int id in gone)
                    {
                        var dev = _falconOpen[id];
                        // The feature switched off is PadForge's own work: no
                        // connection note (#600 item 42).
                        if (!enabled) PadForge.Engine.OwnDrops.Row(dev.InstanceGuid);
                        RetireFalconRow(dev);
                        DisposeFalconAsync(dev);
                        _falconOpen.Remove(id);
                        MarkChanged(ref changed, "falcon", "- " + dev.DevicePath);
                    }
                }
                _falconAnyRows = _falconOpen.Count > 0 || _falconPending.Count > 0;
            }
            return changed;
        }

        // Caller holds _falconLock.
        private void RetireFalconRow(FalconDevice dev)
        {
            var ud = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid);
            if (ud == null) return;
            ud.IsOnline = false;
            ud.Device = null;
            // A button held at retire time would stay stamped on the slot's
            // output, the analog keyboards' reason.
            NeutralizeMappedOutputsFor(ud);
        }

        /// <summary>The worker half: load the library, open each device at
        /// an index no Falcon of ours holds, keep the Falcons, close the
        /// rest, and publish what was found. All library I/O lives here and
        /// on the haptic threads.</summary>
        private void FalconSweep()
        {
            if (_falconSuppressed || !FalconRuntime.Enabled) return;
            // A dhd64.dll picked while another was loaded: every Falcon on the
            // old copy closes here, the poll thread lets each one go, and the
            // first sweep that finds none left hands over to the new copy and
            // opens the Falcons on it. Until then an ID the old copy gave
            // would still be listed as ours.
            if (FalconRuntime.StagePick())
            {
                List<FalconDevice> held;
                lock (_falconLock) held = _falconOpen.Values.Concat(_falconPending).ToList();
                if (held.Count > 0)
                {
                    foreach (var dev in held)
                    {
                        try { dev.Dispose(); }
                        catch { }
                    }
                    return;
                }
                FalconRuntime.SwapToStaged();
                lock (_falconLock)
                {
                    _falconOtherTypes.Clear();
                    _falconLastCount = -1;
                }
            }
            var link = FalconRuntime.Acquire(Environment.TickCount64, out string reason, out string path, out string version);
            int inUse = 0, falcons;
            // The library's reason for the last failed open, read on this
            // thread right after the call, since it keeps one per thread.
            string openError = null;
            if (link != null)
            {
                int count = link.DeviceCount();
                if (count < 0) openError = link.LastErrorText();
                HashSet<int> ours;
                lock (_falconLock)
                {
                    if (count != _falconLastCount)
                    {
                        // A device came or went, so every index may now name
                        // another device.
                        _falconOtherTypes.Clear();
                        _falconLastCount = count;
                    }
                    ours = new HashSet<int>(_falconOpen.Keys.Concat(_falconPending.Select(d => d.Id)));
                }
                for (int index = 0; index < count && index <= sbyte.MaxValue; index++)
                {
                    if (_falconOtherTypes.Contains(index)) continue;
                    // The test host opens no device (DirectDeviceAccess),
                    // only a test's scripted Falcon.
                    if (DirectDeviceAccess.RefusesAll && !FalconRuntime.ScriptedForTest) break;
                    int id = link.OpenIndex(index);
                    if (id < 0)
                    {
                        // Another program holds it, FalconJoyForce or Force
                        // Dimension's own tools, or the open failed.
                        if (link.LastError() == DhdErrorDeviceInUse) inUse++;
                        else openError = link.LastErrorText();
                        continue;
                    }
                    // An index already open returns the ID it opened with.
                    if (ours.Contains(id)) continue;
                    if (link.SystemType(id) != FalconDevice.SystemTypeFalcon)
                    {
                        link.Close(id);
                        lock (_falconLock) _falconOtherTypes.Add(index);
                        continue;
                    }
                    ushort? serial = link.SerialNumber(id, out ushort sn) == 0 ? sn : null;
                    // Decided inside the lock, closed after it: the poll
                    // thread takes this lock every pass, and a library call
                    // can block.
                    bool keep;
                    lock (_falconLock)
                    {
                        keep = !_falconSuppressed && FalconRuntime.Enabled;
                        if (keep)
                        {
                            _falconPending.Add(new FalconDevice(link, id, index, serial));
                            _falconAnyRows = true;
                        }
                    }
                    if (!keep)
                    {
                        link.Close(id);
                        return;
                    }
                    ours.Add(id);
                    PadForge.Engine.SdlDiagLog.WriteLine($"Falcon: opened index {index} as device {id}, serial {(serial?.ToString() ?? "none")}");
                }
            }
            lock (_falconLock) falcons = _falconOpen.Count + _falconPending.Count;

            // With no Falcon open, the Falcon's own USB nodes say why: no
            // driver, a driver other than Force Dimension's, or Force
            // Dimension's own, which the library still failed to open.
            string[] drivers = Array.Empty<string>();
            int ownDriver = 0;
            if (falcons == 0)
            {
                var others = new List<string>();
                foreach (var node in DriverInstaller.FalconNodes())
                {
                    if (DriverInstaller.IsOnFalconPackage(node)) ownDriver++;
                    else others.Add(node.Service ?? string.Empty);
                }
                drivers = others.ToArray();
            }
            // The package the Install button would use, read here on the
            // worker: the loaded library's SDK folder first, then any other
            // SDK folder this PC names, for a library loaded from System32.
            string sdkRoot = PadForge.Engine.Common.ForceDimension.ForceDimensionCatalog.SdkRootOf(path);
            string package = null;
            if (Array.IndexOf(drivers, string.Empty) >= 0)
            {
                package = DriverInstaller.FalconDriverPackage(sdkRoot);
                if (package == null)
                    foreach (string root in PadForge.Engine.Common.ForceDimension.ForceDimensionCatalog.SdkRoots())
                        if ((package = DriverInstaller.FalconDriverPackage(root)) != null) break;
            }
            FalconRuntime.Status = new FalconSweepStatus(reason, path, version, sdkRoot, inUse, drivers, package,
                FalconRuntime.PickNotLoaded, ownDriver, openError);
        }

        private static void DisposeFalconAsync(FalconDevice dev)
        {
            // The haptic thread's join takes up to two seconds, so it never
            // runs on the poll thread.
            Task.Run(() =>
            {
                try { dev.Dispose(); }
                catch { }
            });
        }

        /// <summary>The Step 2 hand-off for a Falcon its slots drive: the
        /// first slot's settings and the combined force, Trigger Rumble Fold
        /// applied as on every direct writer. A Falcon a peer's game drives
        /// over Remote Link keeps the peer's hand-off while the peer holds
        /// the output lease (#138's sole-writer rule), and this PC's slots
        /// take it back once the lease lapses.</summary>
        private static void HandFalconForce(UserDevice ud, FalconDevice falcon, PadSetting firstPadSetting, Vibration combined,
            ushort combinedL, ushort combinedR, ushort combinedLT, ushort combinedRT)
        {
            if (RemoteLinkOutputRouter.IsClaimedByPeer(ud.DevicePath)) return;
            RemoteLinkOutputRouter.NoteLocalWrite(ud.DevicePath);
            ushort left = combinedL, right = combinedR;
            ForceFeedbackState.FoldTriggersForDirectWriter(firstPadSetting, combinedLT, combinedRT, ref left, ref right);
            falcon.Publish(firstPadSetting, combined, left, right);
        }

        /// <summary>A Falcon no slot on this PC is assigned holds itself up
        /// and does nothing else, unless a peer's game drives it: its
        /// hand-off then stands while the peer's lease is fresh, and past
        /// that while the peer wrote last, as a shared pad keeps a peer's
        /// rumble (#402).</summary>
        private static void UnassignFalcon(UserDevice ud, FalconDevice falcon)
        {
            if (RemoteLinkOutputRouter.IsClaimedByPeer(ud.DevicePath) || RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath))
                return;
            falcon.Handoff = FalconHandoff.Unassigned;
        }

        /// <summary>No slot gave a force this pass: the Falcon keeps its feel
        /// and loses the game's force, unless a peer's game drives it.</summary>
        private static void SilenceFalconGame(UserDevice ud, FalconDevice falcon)
        {
            if (RemoteLinkOutputRouter.IsClaimedByPeer(ud.DevicePath)) return;
            falcon.SilenceGame();
        }

        /// <summary>An engine silence edge (stop, focus suspend, the crash
        /// quiesce): each Falcon keeps its feel and loses the game's force.</summary>
        private void SilenceFalcons()
        {
            List<FalconDevice> rows;
            lock (_falconLock) rows = _falconOpen.Values.ToList();
            foreach (var dev in rows) dev.SilenceGame();
        }

        /// <summary>The crash and exit quiesce: every Falcon's haptic thread
        /// is told to stop, which sets zero force and closes its device, as
        /// FalconJoyForce does on exit (FalconJoyForce.cpp:2062-2063), with
        /// <paramref name="waitMs"/> for all of them together.</summary>
        private void QuiesceFalcons(int waitMs)
        {
            _falconSuppressed = true;
            List<FalconDevice> rows;
            lock (_falconLock) rows = _falconOpen.Values.Concat(_falconPending).ToList();
            long until = Environment.TickCount64 + waitMs;
            foreach (var dev in rows) dev.Quiesce(until);
        }

        /// <summary>Engine stop: every Falcon's haptic thread sets zero force
        /// and closes its device before the rest of the engine goes.</summary>
        public void ShutdownFalconInputs()
        {
            _falconSuppressed = true;
            List<FalconDevice> rows;
            lock (_falconLock)
            {
                rows = _falconOpen.Values.Concat(_falconPending).ToList();
                foreach (var dev in _falconOpen.Values) RetireFalconRow(dev);
                _falconOpen.Clear();
                _falconPending.Clear();
                _falconAnyRows = false;
            }
            foreach (var dev in rows)
            {
                try { dev.Dispose(); }
                catch { }
            }
        }
    }
}
