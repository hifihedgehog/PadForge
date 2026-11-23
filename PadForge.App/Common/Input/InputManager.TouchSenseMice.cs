using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // TouchSense mice bound to WinUSB (#494). A bound mouse is read
        // over WinUSB and has no Raw Input row, so it gets a row of its
        // own, a mouse-type device (TouchSenseMouseDevice). Looking for
        // the bound mice and opening one are blocking calls, so a worker
        // makes them on the sweep's cadence and the poll thread only
        // registers the rows it finished and retires the ones whose
        // channel closed, the headset sweep's shape (Phase 1g).
        //
        // WinUSB lets one handle open a device. This phase is the one
        // place a bound mouse is opened: the row's reader closes the
        // channel when it leaves, and a mouse is opened again only after
        // the channel it had before has closed.

        /// <summary>The least time between two looks for the bound mice.
        /// Half the device pass's interval, so every pass starts one,
        /// whatever the tick counter's step does to the 2000 ms between
        /// two passes, and a pass run out of turn starts no second look
        /// right behind the last.</summary>
        internal const int TouchSenseSweepMs = EnumerationIntervalMs / 2;

        /// <summary>How long a mouse waits for its next open after one
        /// that failed or a channel that closed, the wait the backend
        /// gives a mouse whose write failed (TouchSenseBackend.ReopenMs).</summary>
        internal const int TouchSenseReopenMs = TouchSenseBackend.ReopenMs;

        /// <summary>How long the shutdown waits for a sweep that is in the
        /// middle of an open.</summary>
        internal const int TouchSenseShutdownWaitMs = 3000;

        /// <summary>The open rows by interface path, and the rows a sweep
        /// finished that the poll thread has not registered yet. Guarded
        /// by _touchSenseLock.</summary>
        private readonly Dictionary<string, TouchSenseMouseDevice> _touchSenseMice =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<TouchSenseMouseDevice> _touchSensePending = new();

        /// <summary>Per path, when the mouse may be opened again, the
        /// channel it had before, which has to be closed first, and
        /// whether its last open failed, which is logged once and not at
        /// every try. Guarded by _touchSenseLock.</summary>
        private readonly Dictionary<string, (long At, ITouchSenseChannel Before, bool Failed)> _touchSenseRetry =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _touchSenseLock = new();
        private volatile bool _touchSenseSuppressed;
        private volatile bool _touchSenseSweepRunning;

        // Rows open or waiting to register, so a PC with no bound mouse
        // costs the poll thread a few reads a pass, and a worker one look
        // a second for the interfaces PadForge's packages registered.
        private volatile bool _touchSenseAnyRows;
        private volatile Task _touchSenseWorker;
        private long _touchSenseNextSweepTicks;

        // Test seams: the look for the bound mice (null when it failed),
        // the open of one, the table the open channels are listed in, the
        // clock, and how the sweep is run off the poll thread.
        internal Func<IReadOnlyList<TouchSenseMouse>> TouchSenseFind = TouchSenseBinding.FindBound;
        internal Func<TouchSenseMouse, ITouchSenseChannel> TouchSenseOpen = mouse => TouchSenseChannel.Open(mouse.Path);
        internal TouchSenseChannels TouchSenseTable = TouchSenseChannels.Shared;
        internal Func<long> TouchSenseTicks = () => Environment.TickCount64;
        internal Func<Action, Task> TouchSenseRun = Task.Run;

        /// <summary>Test seam: how long the shutdown waits for a sweep, so
        /// the test of a wait that runs out does not sit through it.</summary>
        internal int TouchSenseShutdownWait = TouchSenseShutdownWaitMs;

        partial void UpdateTouchSenseMice(ref bool changed) => changed |= SyncTouchSenseMice();

        partial void ShutdownTouchSenseMice() => RetireTouchSenseMice();

        /// <summary>Phase 1n. Starts a sweep when one is due, registers
        /// the rows the last one finished, takes a row whose channel
        /// closed offline, and puts back a row the user removed from the
        /// Devices page while its mouse is still there. True when the
        /// device list changed.</summary>
        internal bool SyncTouchSenseMice()
        {
            if (_touchSenseSuppressed) return false;
            long now = TouchSenseTicks();
            if (!_touchSenseSweepRunning && now >= _touchSenseNextSweepTicks)
            {
                _touchSenseSweepRunning = true;
                _touchSenseNextSweepTicks = now + TouchSenseSweepMs;
                _touchSenseWorker = TouchSenseRun(() =>
                {
                    try { SweepTouchSenseMice(); }
                    catch { }
                    finally { _touchSenseSweepRunning = false; }
                });
            }
            if (!_touchSenseAnyRows) return false;

            bool changed = false;
            lock (_touchSenseLock)
            {
                // Again under the lock: the shutdown may have latched
                // while a sweep was finishing.
                if (_touchSenseSuppressed) return false;

                foreach (var row in _touchSensePending)
                {
                    try
                    {
                        RegisterTouchSenseMouse(row);
                        _touchSenseMice[row.DevicePath] = row;
                        MarkChanged(ref changed, "touchsense", "+ " + row.Name);
                    }
                    catch (Exception ex)
                    {
                        row.Dispose();
                        TouchSenseTable.Forget(row.Channel);
                        _touchSenseRetry[row.DevicePath] = (now + TouchSenseReopenMs, row.Channel, false);
                        RaiseDeviceError($"Error registering the row of '{row.Name}'", ex);
                    }
                }
                _touchSensePending.Clear();

                List<string> gone = null;
                foreach (var pair in _touchSenseMice)
                {
                    var row = pair.Value;
                    if (!row.IsAttached)
                    {
                        (gone ??= new List<string>()).Add(pair.Key);
                    }
                    else if (FindOnlineDeviceByInstanceGuid(row.InstanceGuid) == null)
                    {
                        // Removed from the Devices page. The mouse is
                        // still there and still read, so its row comes
                        // back with the channel it has: closing and
                        // opening the mouse again would gain nothing.
                        try
                        {
                            RegisterTouchSenseMouse(row);
                            MarkChanged(ref changed, "touchsense", "+ " + row.Name + " (removed by user)");
                        }
                        catch (Exception ex)
                        {
                            RaiseDeviceError($"Error registering the row of '{row.Name}'", ex);
                        }
                    }
                }
                if (gone != null)
                {
                    foreach (var path in gone)
                    {
                        var row = _touchSenseMice[path];
                        _touchSenseMice.Remove(path);
                        RetireTouchSenseMouse(row, wait: false);
                        // The mouse left, its driver went back to Windows,
                        // or a write to it failed. Whichever it was, the
                        // next sweep that still finds it opens it again,
                        // no sooner than the wait.
                        _touchSenseRetry[path] = (now + TouchSenseReopenMs, row.Channel, false);
                        MarkChanged(ref changed, "touchsense", "- " + row.Name);
                    }
                }
                _touchSenseAnyRows = _touchSenseMice.Count > 0 || _touchSensePending.Count > 0;
            }
            return changed;
        }

        /// <summary>The worker's half: looks for the bound mice and opens
        /// each one that has no row and whose wait is over. All blocking
        /// I/O of the phase is here.</summary>
        private void SweepTouchSenseMice()
        {
            var found = TouchSenseFind();
            // The look failed: what is open stays, and the next sweep
            // looks again.
            if (found == null) return;
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mouse in found)
            {
                if (string.IsNullOrEmpty(mouse.Path) || !present.Add(mouse.Path)) continue;
                ITouchSenseChannel before = null;
                bool failedBefore = false;
                lock (_touchSenseLock)
                {
                    if (_touchSenseSuppressed) return;
                    if (_touchSenseMice.ContainsKey(mouse.Path)) continue;
                    bool pending = false;
                    foreach (var row in _touchSensePending)
                        if (string.Equals(row.DevicePath, mouse.Path, StringComparison.OrdinalIgnoreCase)) { pending = true; break; }
                    if (pending) continue;
                    if (_touchSenseRetry.TryGetValue(mouse.Path, out var retry))
                    {
                        if (TouchSenseTicks() < retry.At) continue;
                        before = retry.Before;
                        failedBefore = retry.Failed;
                    }
                }

                // A driver switch has this mouse's node
                // (VendorUsbDriverInstaller.Bind and Restore). An open now
                // would hold the node the switch waits on Windows to
                // restart, so the mouse is left for a later sweep. An open
                // that does begin is one a switch that starts meanwhile
                // waits for, so its end is said on every way out.
                if (!TouchSenseTable.TryBeginOpen(mouse.Path)) continue;
                try
                {
                    // The channel the mouse had before is closed by now,
                    // by its reader or by whoever saw its write fail. This
                    // waits for a close still under way, so the mouse
                    // never has two handles asked of it.
                    try { before?.Dispose(); } catch { }
                    ITouchSenseChannel channel = null;
                    try { channel = TouchSenseOpen(mouse); }
                    catch { }
                    // A mouse that cannot be opened is tried again every
                    // few seconds for as long as it is there, and said once.
                    if (channel != null)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"TouchSense: opened {mouse.VendorId:X4}:{mouse.ProductId:X4} at {mouse.Path} (pointer={channel.ReadsPointer})");
                    else if (!failedBefore)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"TouchSense: open failed for {mouse.VendorId:X4}:{mouse.ProductId:X4} at {mouse.Path}, trying again every {TouchSenseReopenMs / 1000} s");

                    lock (_touchSenseLock)
                    {
                        if (_touchSenseSuppressed)
                        {
                            try { channel?.Dispose(); } catch { }
                            return;
                        }
                        if (channel == null)
                        {
                            _touchSenseRetry[mouse.Path] = (TouchSenseTicks() + TouchSenseReopenMs, null, true);
                            continue;
                        }
                        _touchSenseRetry.Remove(mouse.Path);
                        _touchSensePending.Add(new TouchSenseMouseDevice(mouse, channel));
                        _touchSenseAnyRows = true;
                    }
                    // Listed once its row exists, which wakes a backend
                    // that waits for this mouse. A channel whose node a
                    // driver switch took meanwhile is closed there, and
                    // its row retires on the next pass like any row whose
                    // channel closed.
                    TouchSenseTable.Publish(channel);
                }
                finally
                {
                    TouchSenseTable.EndOpen(mouse.Path);
                }
            }

            // A mouse that left has nothing to wait for: when it comes
            // back it is opened at once.
            lock (_touchSenseLock)
            {
                List<string> stale = null;
                foreach (var path in _touchSenseRetry.Keys)
                    if (!present.Contains(path)) (stale ??= new List<string>()).Add(path);
                if (stale != null)
                    foreach (var path in stale) _touchSenseRetry.Remove(path);
            }
        }

        /// <summary>Puts a row on the device list. Caller holds
        /// _touchSenseLock.</summary>
        private void RegisterTouchSenseMouse(TouchSenseMouseDevice row)
        {
            UserDevice ud = FindOrCreateUserDevice(row.InstanceGuid);
            ud.LoadFromExternalDevice(row);
            ud.IsOnline = true;
        }

        /// <summary>Takes a row offline and stops its reader, which closes
        /// its channel. The poll thread never waits for that. The shutdown
        /// does (<paramref name="wait"/>), so the mouse's handle is gone
        /// when it returns. Caller holds _touchSenseLock.</summary>
        private void RetireTouchSenseMouse(TouchSenseMouseDevice row, bool wait)
        {
            // A row going away mid-rumble owes its level a zero, the
            // offline path's rule for every other device.
            PeripheralOutputs.StopHaptics(row.InstanceGuid);
            var ud = FindOnlineDeviceByInstanceGuid(row.InstanceGuid);
            if (ud != null && (ud.Device == null || ReferenceEquals(ud.Device, row)))
            {
                ud.IsOnline = false;
                ud.Device = null;
                NeutralizeMappedOutputsFor(ud);
            }
            if (wait) row.Close();
            else row.Dispose();
            TouchSenseTable.Forget(row.Channel);
        }

        /// <summary>Retires every row and closes every bound mouse, and
        /// suppresses the phase, so a poll pass that outlived the stop
        /// opens nothing more. Called when the manager is disposed, after
        /// the peripheral host has stopped, whose TouchSense backend sent
        /// each mouse its last frames on the channel closed here.</summary>
        internal void RetireTouchSenseMice()
        {
            _touchSenseSuppressed = true;
            // A sweep in the middle of an open finishes it and then sees
            // the latch, so no handle is left open behind this.
            try { _touchSenseWorker?.Wait(TouchSenseShutdownWait); } catch { }
            lock (_touchSenseLock)
            {
                foreach (var row in _touchSensePending)
                {
                    row.Close();
                    TouchSenseTable.Forget(row.Channel);
                }
                _touchSensePending.Clear();
                foreach (var row in _touchSenseMice.Values)
                    RetireTouchSenseMouse(row, wait: true);
                _touchSenseMice.Clear();
                // A row retired a moment ago leaves its close to its
                // reader. This waits for that close too.
                foreach (var retry in _touchSenseRetry.Values)
                {
                    try { retry.Before?.Dispose(); } catch { }
                }
                _touchSenseRetry.Clear();
                _touchSenseAnyRows = false;
            }
        }

        /// <summary>The open rows, a snapshot for tests.</summary>
        internal TouchSenseMouseDevice[] TouchSenseMice
        {
            get
            {
                lock (_touchSenseLock)
                {
                    var rows = new TouchSenseMouseDevice[_touchSenseMice.Count];
                    _touchSenseMice.Values.CopyTo(rows, 0);
                    return rows;
                }
            }
        }
    }
}
