using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.WinUsbKeyboards;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // Keyboards bound to WinUSB (#549). A bound keyboard is read over
        // WinUSB and has no Raw Input row, so it gets a row of its own, a
        // keyboard-type device (WinUsbKeyboardDevice) with the ID its
        // takeover recorded, the Raw Input row's, so the keyboard keeps its
        // slots, mappings and macros. The phase is the TouchSense mice's
        // (Phase 1n, InputManager.TouchSenseMice.cs): looking for the bound
        // keyboards and opening one are blocking calls, so a worker makes
        // them on the sweep's cadence and the poll thread only registers
        // the rows it finished and retires the ones whose channel closed.
        //
        // WinUSB lets one handle open a device. This phase is the one place
        // a bound keyboard is opened: the row's reader closes the channel
        // when it leaves, and a keyboard is opened again only after the
        // channel it had before has closed. Each channel is listed in the
        // table every WinUSB handle of PadForge's is listed in
        // (TouchSenseChannels), so a driver switch closes it first.

        /// <summary>The least time between two looks for the bound
        /// keyboards, the TouchSense sweep's.</summary>
        internal const int WinUsbKeyboardSweepMs = TouchSenseSweepMs;

        /// <summary>How long a keyboard waits for its next open after one
        /// that failed or a channel that closed.</summary>
        internal const int WinUsbKeyboardReopenMs = TouchSenseReopenMs;

        /// <summary>How long the shutdown waits for a sweep that is in the
        /// middle of an open.</summary>
        internal const int WinUsbKeyboardShutdownWaitMs = TouchSenseShutdownWaitMs;

        /// <summary>The open rows by interface path, and the rows a sweep
        /// finished that the poll thread has not registered yet. Guarded
        /// by _winUsbKeyboardLock.</summary>
        private readonly Dictionary<string, WinUsbKeyboardDevice> _winUsbKeyboards =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<WinUsbKeyboardDevice> _winUsbKeyboardsPending = new();

        /// <summary>Per path, when the keyboard may be opened again, the
        /// channel it had before, which has to be closed first, and whether
        /// its last open failed, which is logged once. Guarded by
        /// _winUsbKeyboardLock.</summary>
        private readonly Dictionary<string, (long At, IKeyboardChannel Before, bool Failed)> _winUsbKeyboardRetry =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _winUsbKeyboardLock = new();
        private volatile bool _winUsbKeyboardsSuppressed;
        private volatile bool _winUsbKeyboardSweepRunning;
        private volatile bool _winUsbKeyboardAnyRows;
        private volatile Task _winUsbKeyboardWorker;
        private long _winUsbKeyboardNextSweepTicks;

        // Test seams: the look for the bound keyboards (null when it
        // failed), the open of one, the table the open channels are listed
        // in, the clock, and how the sweep is run off the poll thread.
        internal Func<IReadOnlyList<WinUsbKeyboard>> WinUsbKeyboardFind = WinUsbKeyboardBinding.FindBound;
        internal Func<WinUsbKeyboard, IKeyboardChannel> WinUsbKeyboardOpen = keyboard => WinUsbKeyboardChannel.Open(keyboard.Path);
        internal TouchSenseChannels WinUsbKeyboardTable = TouchSenseChannels.Shared;
        internal Func<long> WinUsbKeyboardTicks = () => Environment.TickCount64;
        internal Func<Action, Task> WinUsbKeyboardRun = Task.Run;

        /// <summary>Test seam: a row with no reader of its own, so a test
        /// feeds it reports.</summary>
        internal bool WinUsbKeyboardStartReaders = true;

        /// <summary>Test seam: how long the shutdown waits for a sweep.</summary>
        internal int WinUsbKeyboardShutdownWait = WinUsbKeyboardShutdownWaitMs;

        partial void UpdateWinUsbKeyboards(ref bool changed) => changed |= SyncWinUsbKeyboards();

        partial void ShutdownWinUsbKeyboards() => RetireWinUsbKeyboards();

        /// <summary>Phase 1o. Starts a sweep when one is due, registers the
        /// rows the last one finished, takes a row whose channel closed
        /// offline, and puts back a row the user removed from the Devices
        /// page while its keyboard is still there. True when the device
        /// list changed.</summary>
        internal bool SyncWinUsbKeyboards()
        {
            if (_winUsbKeyboardsSuppressed) return false;
            long now = WinUsbKeyboardTicks();
            // A PC with no takeover and no row costs no look at all: only a
            // takeover's node is ever a bound keyboard.
            if (!_winUsbKeyboardSweepRunning && now >= _winUsbKeyboardNextSweepTicks
                && (KeyboardTakeovers.Current.Count > 0 || _winUsbKeyboardAnyRows))
            {
                _winUsbKeyboardSweepRunning = true;
                _winUsbKeyboardNextSweepTicks = now + WinUsbKeyboardSweepMs;
                _winUsbKeyboardWorker = WinUsbKeyboardRun(() =>
                {
                    try { SweepWinUsbKeyboards(); }
                    catch { }
                    finally { _winUsbKeyboardSweepRunning = false; }
                });
            }
            if (!_winUsbKeyboardAnyRows) return false;

            bool changed = false;
            lock (_winUsbKeyboardLock)
            {
                // Again under the lock: the shutdown may have latched while
                // a sweep was finishing.
                if (_winUsbKeyboardsSuppressed) return false;

                foreach (var row in _winUsbKeyboardsPending)
                {
                    try
                    {
                        RegisterWinUsbKeyboard(row);
                        _winUsbKeyboards[row.DevicePath] = row;
                        MarkChanged(ref changed, "winusbkeyboard", "+ " + row.Name);
                    }
                    catch (Exception ex)
                    {
                        row.Dispose();
                        WinUsbKeyboardTable.Forget(row.Channel);
                        _winUsbKeyboardRetry[row.DevicePath] = (now + WinUsbKeyboardReopenMs, row.Channel, false);
                        RaiseError($"Error registering the row of '{row.Name}'", ex);
                    }
                }
                _winUsbKeyboardsPending.Clear();

                List<string> gone = null;
                foreach (var pair in _winUsbKeyboards)
                {
                    var row = pair.Value;
                    if (!row.IsAttached)
                    {
                        (gone ??= new List<string>()).Add(pair.Key);
                    }
                    else if (FindOnlineDeviceByInstanceGuid(row.InstanceGuid) == null)
                    {
                        // Removed from the Devices page. The keyboard is
                        // still there and still read, so its row comes back
                        // with the channel it has.
                        try
                        {
                            RegisterWinUsbKeyboard(row);
                            MarkChanged(ref changed, "winusbkeyboard", "+ " + row.Name + " (removed by user)");
                        }
                        catch (Exception ex)
                        {
                            RaiseError($"Error registering the row of '{row.Name}'", ex);
                        }
                    }
                }
                if (gone != null)
                {
                    foreach (var path in gone)
                    {
                        var row = _winUsbKeyboards[path];
                        _winUsbKeyboards.Remove(path);
                        RetireWinUsbKeyboard(row, wait: false);
                        // The keyboard left, its driver went back to
                        // Windows, or a read failed. Whichever it was, the
                        // next sweep that still finds it opens it again, no
                        // sooner than the wait.
                        _winUsbKeyboardRetry[path] = (now + WinUsbKeyboardReopenMs, row.Channel, false);
                        MarkChanged(ref changed, "winusbkeyboard", "- " + row.Name);
                    }
                }
                _winUsbKeyboardAnyRows = _winUsbKeyboards.Count > 0 || _winUsbKeyboardsPending.Count > 0;
            }
            return changed;
        }

        /// <summary>The worker's half: looks for the bound keyboards and
        /// opens each one that has no row and whose wait is over. All
        /// blocking I/O of the phase is here.</summary>
        private void SweepWinUsbKeyboards()
        {
            var found = WinUsbKeyboardFind();
            // The look failed: what is open stays, and the next sweep looks
            // again.
            if (found == null) return;
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyboard in found)
            {
                if (string.IsNullOrEmpty(keyboard.Path) || !present.Add(keyboard.Path)) continue;
                IKeyboardChannel before = null;
                bool failedBefore = false;
                lock (_winUsbKeyboardLock)
                {
                    if (_winUsbKeyboardsSuppressed) return;
                    if (_winUsbKeyboards.ContainsKey(keyboard.Path)) continue;
                    bool pending = false;
                    foreach (var row in _winUsbKeyboardsPending)
                        if (string.Equals(row.DevicePath, keyboard.Path, StringComparison.OrdinalIgnoreCase)) { pending = true; break; }
                    if (pending) continue;
                    if (_winUsbKeyboardRetry.TryGetValue(keyboard.Path, out var retry))
                    {
                        if (WinUsbKeyboardTicks() < retry.At) continue;
                        before = retry.Before;
                        failedBefore = retry.Failed;
                    }
                }

                // A driver switch has this keyboard's node: an open now
                // would hold the node the switch waits on Windows to
                // restart, so the keyboard is left for a later sweep. An
                // open that does begin is one a switch that starts
                // meanwhile waits for, so its end is said on every way out.
                if (!WinUsbKeyboardTable.TryBeginOpen(keyboard.Path)) continue;
                try
                {
                    // The channel the keyboard had before is closed by now,
                    // by its reader. This waits for a close still under
                    // way, so the keyboard never has two handles asked of it.
                    try { before?.Dispose(); } catch { }
                    IKeyboardChannel channel = null;
                    try { channel = WinUsbKeyboardOpen(keyboard); }
                    catch { }
                    if (channel != null)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"WinUsbKeyboard: opened {keyboard.VendorId:X4}:{keyboard.ProductId:X4} at {keyboard.Path}");
                    else if (!failedBefore)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"WinUsbKeyboard: open failed for {keyboard.VendorId:X4}:{keyboard.ProductId:X4} at {keyboard.Path}, trying again every {WinUsbKeyboardReopenMs / 1000} s");

                    lock (_winUsbKeyboardLock)
                    {
                        if (_winUsbKeyboardsSuppressed)
                        {
                            try { channel?.Dispose(); } catch { }
                            return;
                        }
                        if (channel == null)
                        {
                            _winUsbKeyboardRetry[keyboard.Path] = (WinUsbKeyboardTicks() + WinUsbKeyboardReopenMs, null, true);
                            continue;
                        }
                        _winUsbKeyboardRetry.Remove(keyboard.Path);
                        _winUsbKeyboardsPending.Add(new WinUsbKeyboardDevice(keyboard, channel, WinUsbKeyboardStartReaders));
                        _winUsbKeyboardAnyRows = true;
                    }
                    // Listed once its row exists. A channel whose node a
                    // driver switch took meanwhile is closed there, and its
                    // row retires on the next pass like any row whose
                    // channel closed.
                    WinUsbKeyboardTable.Publish(channel);
                }
                finally
                {
                    WinUsbKeyboardTable.EndOpen(keyboard.Path);
                }
            }

            // A keyboard that left has nothing to wait for: when it comes
            // back it is opened at once.
            lock (_winUsbKeyboardLock)
            {
                List<string> stale = null;
                foreach (var path in _winUsbKeyboardRetry.Keys)
                    if (!present.Contains(path)) (stale ??= new List<string>()).Add(path);
                if (stale != null)
                    foreach (var path in stale) _winUsbKeyboardRetry.Remove(path);
            }
        }

        /// <summary>Puts a row on the device list, in the place of the
        /// keyboard's Raw Input row when the takeover recorded it. Caller
        /// holds _winUsbKeyboardLock.</summary>
        private void RegisterWinUsbKeyboard(WinUsbKeyboardDevice row)
        {
            UserDevice ud = FindOrCreateUserDevice(row.InstanceGuid);
            ud.LoadFromExternalDevice(row);
            ud.IsOnline = true;
        }

        /// <summary>Takes a row offline and stops its reader, which closes
        /// its channel. The poll thread never waits for that. The shutdown
        /// does (<paramref name="wait"/>), so the keyboard's handle is gone
        /// when it returns. Caller holds _winUsbKeyboardLock.</summary>
        private void RetireWinUsbKeyboard(WinUsbKeyboardDevice row, bool wait)
        {
            var ud = FindOnlineDeviceByInstanceGuid(row.InstanceGuid);
            if (ud != null && (ud.Device == null || ReferenceEquals(ud.Device, row)))
            {
                ud.IsOnline = false;
                ud.Device = null;
                NeutralizeMappedOutputsFor(ud);
            }
            if (wait) row.Close();
            else row.Dispose();
            WinUsbKeyboardTable.Forget(row.Channel);
        }

        /// <summary>Retires every row and closes every bound keyboard, and
        /// suppresses the phase, so a poll pass that outlived the stop
        /// opens nothing more. Called when the manager is disposed.</summary>
        internal void RetireWinUsbKeyboards()
        {
            _winUsbKeyboardsSuppressed = true;
            // A sweep in the middle of an open finishes it and then sees the
            // latch, so no handle is left open behind this.
            try { _winUsbKeyboardWorker?.Wait(WinUsbKeyboardShutdownWait); } catch { }
            lock (_winUsbKeyboardLock)
            {
                foreach (var row in _winUsbKeyboardsPending)
                {
                    row.Close();
                    WinUsbKeyboardTable.Forget(row.Channel);
                }
                _winUsbKeyboardsPending.Clear();
                foreach (var row in _winUsbKeyboards.Values)
                    RetireWinUsbKeyboard(row, wait: true);
                _winUsbKeyboards.Clear();
                // A row retired a moment ago leaves its close to its reader.
                // This waits for that close too.
                foreach (var retry in _winUsbKeyboardRetry.Values)
                {
                    try { retry.Before?.Dispose(); } catch { }
                }
                _winUsbKeyboardRetry.Clear();
                _winUsbKeyboardAnyRows = false;
            }
        }

        /// <summary>The open rows, a snapshot for tests.</summary>
        internal WinUsbKeyboardDevice[] WinUsbKeyboardRows
        {
            get
            {
                lock (_winUsbKeyboardLock)
                {
                    var rows = new WinUsbKeyboardDevice[_winUsbKeyboards.Count];
                    _winUsbKeyboards.Values.CopyTo(rows, 0);
                    return rows;
                }
            }
        }
    }
}
