using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.Touchpads;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Tablets;
using PadForge.Engine.Touchpads;
using PadForge.Resources.Strings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Services;

/// <summary>
/// Precision touchpads PadForge reads alone (Use Only in PadForge on the
/// touchpad's rows), the pen tablet's hiding (InputService.Tablets.cs) for a
/// touchpad. The hiding pass hides the touchpad's collection, and only that
/// collection, while the engine runs, and the capture opens it
/// (<see cref="TouchpadCapture"/>). When the engine stops, PadForge closes,
/// or the switch is given back, the hide comes off and the collection is
/// restarted, so Windows opens it again. A touchpad is often a laptop's only
/// pointer, so it is not left hidden with nothing reading it, kept cloaks
/// included. A crash or a Windows restart releases it at once
/// (<see cref="ReleaseTouchpadsNow"/>). A kill cannot, and the task registered
/// before the hide is written is waiting for PadForge to end, and releases it at
/// once. A power cut stops that wait too, and the same task releases it at the
/// next boot (<see cref="GiveBackTask"/>).
/// </summary>
public partial class InputService
{
    /// <summary>How long a crash or a session's end waits for the touchpads to
    /// go back. Whatever is left of the work runs on until the process
    /// ends.</summary>
    internal const int TouchpadReleaseBudgetMs = 1500;

    /// <summary>How long a touchpad's cover waits after schtasks refused it,
    /// since an apply runs on the UI thread and comes often.</summary>
    internal const int TouchpadCoverRetryMs = 60000;

    /// <summary>The touchpad IDs whose give-back task this session registered
    /// and has not deleted. Each one's next hide needs no new task.</summary>
    internal readonly HashSet<string> _touchpadGiveBackTasks = new(StringComparer.OrdinalIgnoreCase);
    private long _touchpadCoverRetryAt;

    /// <summary>Set by a crash or a session's end: no apply hides a touchpad
    /// again after its hide came off.</summary>
    private volatile bool _touchpadsReleasing;

    /// <summary>The touchpad paths whose capture the last apply asked for.</summary>
    private readonly HashSet<string> _requestedTouchpadCaptures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths of the touchpads a refused sync kept from going back
    /// to Windows. The retry applies again until a sync lands
    /// (<see cref="RetryOwedGiveBacks"/>).</summary>
    private readonly HashSet<string> _touchpadGiveBacksOwed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The instance IDs of the touchpads whose hide a release or a
    /// give-back could not take off.</summary>
    internal readonly HashSet<string> TouchpadHidesOwed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths of the touchpads whose capture failed because the
    /// node left, each waiting to be read again when it returns.</summary>
    private readonly HashSet<string> _touchpadsGone = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: whether a collection is present. Windows by
    /// default.</summary>
    internal Func<string, bool> TouchpadNodePresent = TouchpadCapture.IsNodePresent;

    /// <summary>How long a failed read waits before it asks whether the node
    /// left: a read fails on a removal before Windows has finished taking the
    /// node off its list.</summary>
    internal int TouchpadGoneSettleMs = 500;

    /// <summary>A precision touchpad's row whose path names its HID
    /// collection, the row the switch is set on. The merged touchpad row has
    /// none.</summary>
    internal static bool IsTouchpadCollectionRow(UserDevice ud)
    {
        if (ud == null || !ud.IsTouchpad || string.IsNullOrEmpty(ud.DevicePath)
            || ud.DevicePath.StartsWith("aggregate://", StringComparison.Ordinal))
            return false;
        string id = HidHideController.DevicePathToInstanceId(ud.DevicePath);
        return id != null && id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A precision touchpad's mouse collection as a mouse row. Its
    /// own hiding flag is never applied: HidHide is not on a mouse
    /// collection's stack, and the expansion from it hid the touchpad
    /// collection beside it with nothing reading it.</summary>
    internal static bool IsTouchpadMouseRow(UserDevice ud)
    {
        if (ud == null || ud.CapType != InputDeviceType.Mouse || string.IsNullOrEmpty(ud.DevicePath)) return false;
        string id = HidHideController.DevicePathToInstanceId(ud.DevicePath);
        if (id == null || !id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) return false;
        string touchpad = TouchpadNodes.TouchpadOf(id);
        return touchpad != null && !string.Equals(touchpad, id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds the collection of each touchpad the switch is on to the
    /// hide set, while the engine runs. A row left visible elsewhere keeps a
    /// node out as it does for every row (#400).</summary>
    private void AddTouchpadHides(UserDevice[] snapshot, HashSet<string> desiredIds, HashSet<string> touchpadIds,
        Func<string, bool> keepOut, List<string> hidLog)
    {
        if (_inputManager?.CanCaptureTouchpads != true || _touchpadsReleasing) return;
        foreach (var ud in snapshot)
        {
            if (!ud.TouchpadOnlyInPadForge || !IsTouchpadCollectionRow(ud)) continue;
            string id = HidHideController.DevicePathToInstanceId(ud.DevicePath);
            if (keepOut(id) || HidHideController.IsHidMaestroDeviceInstance(id))
            {
                hidLog.Add($"HIDHIDE touchpad {id} kept out");
                continue;
            }
            // The cover goes on first, so the hide is never on without it.
            if (!CoverTouchpadHide(id, _touchpadGiveBackTasks, ref _touchpadCoverRetryAt, Environment.TickCount64,
                    i => GiveBackTask.RegisterTouchpad(i, line => PadForge.Engine.SdlDiagLog.WriteLine("TOUCHPAD " + line)), hidLog))
                continue;
            desiredIds.Add(id);
            touchpadIds.Add(id);
            // This engine reads it now, so no retry of an earlier release
            // takes its hide away.
            TouchpadHidesOwed.Remove(id);
            hidLog.Add($"HIDHIDE touchpad {ud.VendorId:X4}:{ud.ProdId:X4} id={id}");
        }
    }

    /// <summary>Registers a touchpad's give-back task unless one from this
    /// session still stands. False when schtasks refused it, and the touchpad is then not hidden. A
    /// PC where no task can take a hide off (<see cref="GiveBackTask.Outcome.NoTool"/>)
    /// has its touchpad hidden with the release at crash and session end alone.
    /// True when the hide may be written.</summary>
    internal static bool CoverTouchpadHide(string id, ISet<string> covered, ref long retryAt, long now,
        Func<string, GiveBackTask.Outcome> register, List<string> hidLog)
    {
        if (covered.Contains(id)) return true;
        if (now < retryAt) return false;
        switch (register(id))
        {
            case GiveBackTask.Outcome.Registered:
                covered.Add(id);
                return true;
            case GiveBackTask.Outcome.NoTool:
                covered.Add(id);
                hidLog.Add($"HIDHIDE touchpad {id} has no give-back task: its path cannot be quoted and this PC has no HidHide CLI");
                return true;
            default:
                retryAt = now + TouchpadCoverRetryMs;
                hidLog.Add($"HIDHIDE touchpad {id} kept out: its boot give-back could not be registered");
                return false;
        }
    }

    /// <summary>Deletes the give-back task of each touchpad HidHide shows and
    /// no row wants hidden, the end of a give-back.</summary>
    internal static void DropTasksOfTouchpadsShown(ISet<string> covered, ISet<string> hidden, UserDevice[] snapshot, Action<string> delete)
    {
        foreach (var id in covered.ToList())
        {
            if (hidden.Contains(id)) continue;
            if (snapshot.Any(d => d.TouchpadOnlyInPadForge && IsTouchpadCollectionRow(d)
                    && string.Equals(HidHideController.DevicePathToInstanceId(d.DevicePath), id, StringComparison.OrdinalIgnoreCase)))
                continue;
            delete(id);
            covered.Remove(id);
        }
    }

    private void OnWinUsbTakeoversFound(IReadOnlyList<PadForge.Common.Input.WinUsbKeyboards.KeyboardTakeoverEntry> found)
        => _dispatcher.BeginInvoke(new Action(() => AdoptTakeovers(found)));

    /// <summary>Takes on the takeovers a look built for devices left on
    /// PadForge's package with no record. Each goes on the list, in the file at
    /// once, and to the device pass, which opens its row. Its offline row is
    /// made here, so the Devices page has its Restore with the engine off or a
    /// channel that never opens. UI thread.</summary>
    internal void AdoptTakeovers(IEnumerable<PadForge.Common.Input.WinUsbKeyboards.KeyboardTakeoverEntry> found)
    {
        var list = _mainVm.Settings.KeyboardTakeovers;
        bool added = false;
        foreach (var entry in found ?? Enumerable.Empty<PadForge.Common.Input.WinUsbKeyboards.KeyboardTakeoverEntry>())
        {
            if (!PadForge.Common.Input.WinUsbKeyboards.KeyboardTakeovers.IsValid(entry)
                || PadForge.Common.Input.WinUsbKeyboards.KeyboardTakeovers.ForNode(list, entry.NodeId) != null)
                continue;
            list.Add(entry);
            added = true;
            AddOfflineRowOf(entry);
            PadForge.Engine.SdlDiagLog.WriteLine($"VendorUSB: {entry.NodeId} is on PadForge's driver with no takeover, so one was made.");
            _mainVm.SetStatus(string.Format(Strings.Instance.Status_DriverAdopted_Format, entry.Name), persist: true);
        }
        if (!added) return;
        PadForge.Common.Input.WinUsbKeyboards.KeyboardTakeovers.Apply(list);
        _settingsService?.Save();
        RefreshDeviceList();
        _mainVm.Devices.RefreshDriverOffer();
    }

    private static void AddOfflineRowOf(PadForge.Common.Input.WinUsbKeyboards.KeyboardTakeoverEntry entry)
    {
        var devices = SettingsManager.UserDevices;
        if (devices?.Items == null) return;
        lock (devices.SyncRoot)
        {
            if (devices.Items.Any(d => d.InstanceGuid == entry.Row)) return;
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = entry.Row,
                InstanceName = entry.Name,
                ProductName = entry.Name,
                VendorId = entry.VendorId,
                ProdId = entry.ProductId,
                CapType = entry.IsMouse ? InputDeviceType.Mouse : InputDeviceType.Keyboard,
                IsOnline = false,
            });
        }
    }

    /// <summary>Takes back the switch of every touchpad still waiting on its
    /// Keep prompt when the last session ended. Returns those rows.</summary>
    internal static List<UserDevice> EndKeepsLeftPending(IEnumerable<UserDevice> rows)
    {
        var ended = new List<UserDevice>();
        foreach (var ud in rows)
        {
            if (!ud.TouchpadKeepPending) continue;
            ud.TouchpadKeepPending = false;
            ud.TouchpadOnlyInPadForge = false;
            ended.Add(ud);
        }
        return ended;
    }

    /// <summary>At a start, before any engine: a touchpad nobody kept goes back
    /// to Windows, whatever its boot task has done by now. With no engine about
    /// to start, a hide a killed session left comes off too, since a touchpad
    /// is hidden only while the engine reads it.</summary>
    internal void GiveBackTouchpadsNobodyKept(bool engineStarting)
    {
        var ended = EndKeepsLeftPending(UserDeviceSnapshot());
        foreach (var ud in ended)
        {
            if (!IsTouchpadCollectionRow(ud)) continue;
            string id = HidHideController.DevicePathToInstanceId(ud.DevicePath);
            if (TakeTouchpadHideOff(ud)) EndTouchpadTask(id);
            _mainVm.StatusText = string.Format(Strings.Instance.Status_TouchpadKeepExpired_Format, ud.InstanceName);
        }
        if (ended.Count > 0) _settingsService?.Save();
        if (engineStarting) return;
        foreach (var ud in UserDeviceSnapshot())
            if (ud.TouchpadOnlyInPadForge) TakeTouchpadHideOff(ud);
    }

    /// <summary>The release for a crash and a session's end, on the thread that
    /// is about to lose the process: the touchpads' own hides come off and
    /// their captures give them back, within a budget, since a lock this work
    /// takes may be held by whatever just failed. Cloaks of other devices stay
    /// for the normal release, if one comes.</summary>
    internal void ReleaseTouchpadsNow(int budgetMs = TouchpadReleaseBudgetMs)
    {
        var captures = _inputManager?.GetTouchpadCaptures() ?? Array.Empty<TouchpadCapture>();
        if (captures.Length == 0) return;
        _touchpadsReleasing = true;
        var work = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var capture in captures) capture.PrepareForUnhide(retireCapture: true);
            ReleaseTouchpads(captures, keepCloaks: true);
        });
        try { work.Wait(budgetMs); }
        catch (AggregateException) { }
    }

    /// <summary>Closes the capture of every touchpad the next blacklist
    /// write shows again, before that write. With <paramref name="hideNone"/>
    /// the write takes back every hide (#484), whatever the rows ask.</summary>
    private void PrepareTouchpadVisibilityChanges(UserDevice[] snapshot, bool hideNone = false)
    {
        foreach (var capture in _inputManager?.GetTouchpadCaptures() ?? Array.Empty<TouchpadCapture>())
        {
            var row = TouchpadRowOf(snapshot, capture);
            if (_requestedTouchpadCaptures.Contains(capture.DevicePath) && (hideNone || row?.TouchpadOnlyInPadForge != true))
                capture.PrepareForUnhide();
        }
    }

    /// <summary>After the blacklist write: each touchpad the switch is on is
    /// read while HidHide hides it, every other capture gives its touchpad
    /// back, and a touchpad whose hide came off with no capture to give it
    /// back is restarted for Windows.</summary>
    private void RefreshTouchpadCapture(UserDevice[] snapshot, bool syncSucceeded, IEnumerable<string> added,
        IEnumerable<string> removed)
    {
        var manager = _inputManager;
        var captures = manager?.GetTouchpadCaptures() ?? Array.Empty<TouchpadCapture>();
        var blacklist = HidHideController.GetBlacklist();
        bool activeRead = HidHideController.TryGetActive(out bool active);
        if (!syncSucceeded || blacklist == null || !activeRead)
        {
            if (StopCapturesAfterFailedSync(captures, snapshot, _requestedTouchpadCaptures))
                foreach (var capture in captures)
                    if (TouchpadRowOf(snapshot, capture)?.TouchpadOnlyInPadForge != true && capture.CaptureWasRequested
                        && capture.CaptureState != TabletCaptureState.Shared)
                        _touchpadGiveBacksOwed.Add(capture.DevicePath);
            return;
        }

        var hidden = new HashSet<string>(blacklist, StringComparer.OrdinalIgnoreCase);
        var newCloaks = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
        _requestedTouchpadCaptures.Clear();
        if (manager != null)
        {
            foreach (var ud in snapshot)
            {
                if (!IsTouchpadCollectionRow(ud)) continue;
                bool requested = _mainVm.Settings.EnableInputHiding && ud.TouchpadOnlyInPadForge;
                var capture = requested
                    ? manager.TouchpadCaptureFor(ud)
                    : captures.FirstOrDefault(c => string.Equals(c.DevicePath, ud.DevicePath, StringComparison.OrdinalIgnoreCase));
                if (capture == null) continue;
                if (requested) _requestedTouchpadCaptures.Add(capture.DevicePath);
                capture.SetCapture(active && hidden.Contains(capture.DeviceInstanceId),
                    requested && newCloaks.Contains(capture.DeviceInstanceId),
                    requested && TakeTouchpadRetry(capture));
            }
        }
        GiveBackCapturesWithNoRow(captures, snapshot, hidden, active);
        DropTasksOfTouchpadsShown(_touchpadGiveBackTasks, hidden, snapshot, id => GiveBackTask.Delete(id));

        // A give-back a refused sync held up has landed.
        foreach (var capture in captures)
            if (_touchpadGiveBacksOwed.Contains(capture.DevicePath))
                _mainVm.StatusText = string.Format(Strings.Instance.Status_TouchpadGivenBack_Format, capture.Name);
        _touchpadGiveBacksOwed.Clear();

        foreach (var id in removed ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) continue;
            // A capture that read the touchpad gives it back with its own
            // restart (TouchpadCapture.SetCapture), row or no row.
            if (captures.Any(c => c.CaptureWasRequested
                    && string.Equals(c.DeviceInstanceId, id, StringComparison.OrdinalIgnoreCase))) continue;
            if (!string.Equals(TouchpadNodes.TouchpadOf(id), id, StringComparison.OrdinalIgnoreCase)) continue;
            _ = TouchpadCapture.RestartForWindows(id);
        }
    }

    /// <summary>The hiding pass could not confirm what HidHide holds. Every
    /// capture stops and says why. A restart now, with the hide possibly still
    /// on, would have Windows' reopen refused and leave nothing to restart the
    /// collection once the hide comes off, so a touchpad on its way back to
    /// Windows waits: the failure leaves its request open, and the next sync
    /// that lands restarts it (TouchpadCapture.SetCapture). True when a
    /// touchpad is left waiting that way. One already given back stays as it
    /// is.</summary>
    internal static bool StopCapturesAfterFailedSync(TouchpadCapture[] captures, UserDevice[] snapshot, ISet<string> requested)
    {
        bool owed = false;
        foreach (var capture in captures)
        {
            var row = TouchpadRowOf(snapshot, capture);
            if (row?.TouchpadOnlyInPadForge != true && capture.CaptureWasRequested)
            {
                if (capture.CaptureState == TabletCaptureState.Shared) continue;
                capture.ReportPolicyFailure("The device hiding change could not be verified.");
                owed = true;
            }
            else if (row?.TouchpadOnlyInPadForge == true || requested.Contains(capture.DevicePath))
                capture.ReportPolicyFailure("The device hiding change could not be verified.");
        }
        return owed;
    }

    /// <summary>The capture of a touchpad with no row gives it back once
    /// HidHide shows it. Reset to Defaults empties the rows and the blacklist
    /// with one write, so no row and no removal in the sync's diff would
    /// ever restart it.</summary>
    internal static void GiveBackCapturesWithNoRow(TouchpadCapture[] captures, UserDevice[] snapshot, ISet<string> hidden, bool active)
    {
        foreach (var capture in captures)
        {
            if (active && hidden.Contains(capture.DeviceInstanceId)) continue;
            bool hasRow = snapshot.Any(d => IsTouchpadCollectionRow(d)
                && string.Equals(d.DevicePath, capture.DevicePath, StringComparison.OrdinalIgnoreCase));
            if (!hasRow) capture.SetCapture(false);
        }
    }

    /// <summary>Takes a touchpad's own hide off the driver's list, whichever
    /// session wrote it, and has the collection restarted if one came off.
    /// True when HidHide no longer lists it, or is not on this PC. False when
    /// its list could not be read or the write was refused, a control device
    /// another client holds among those.</summary>
    internal static bool TakeHideOff(string instanceId, Action<string> restart)
    {
        const int ERROR_FILE_NOT_FOUND = 2;
        if (string.IsNullOrEmpty(instanceId)) return true;
        if (!HidHideController.TryProbe(out int error)) return error == ERROR_FILE_NOT_FOUND;
        if (!HidHideController.RemoveFromBlacklist(new[] { instanceId }, out var removed)) return false;
        if (removed.Count > 0) restart(instanceId);
        return true;
    }

    /// <summary>The check a give-back ends on: the row's hide is off, so
    /// Windows can open the touchpad. A capture restarts the collection itself
    /// on the apply that sees the hide gone, and with none it is restarted
    /// here. A hide a session before left is in no managed set, so the apply's
    /// sync took nothing off. A hide that stays goes on the retry's list.</summary>
    internal bool TakeTouchpadHideOff(UserDevice ud)
    {
        if (!IsTouchpadCollectionRow(ud)) return true;
        var capture = TouchpadCaptureOf(ud);
        string instanceId = HidHideController.DevicePathToInstanceId(ud.DevicePath);
        bool took = false;
        bool off = TakeHideOff(instanceId, id =>
        {
            took = true;
            if (capture == null) _ = TouchpadCapture.RestartForWindows(id);
        });
        if (took && capture != null) ApplyAutomaticDeviceHiding();
        if (took) EndTouchpadTask(instanceId);
        if (!off) TouchpadHidesOwed.Add(instanceId);
        return off;
    }

    /// <summary>Tries again what a refused HidHide write held up, on the
    /// status timer's lane. Costs nothing while nothing is owed.</summary>
    internal void RetryOwedGiveBacks()
    {
        if (_touchpadGiveBacksOwed.Count > 0 || _tabletGiveBackOwed) ApplyAutomaticDeviceHiding();
        foreach (var id in TouchpadHidesOwed.ToList())
        {
            if (!TakeHideOff(id, gone => _ = TouchpadCapture.RestartForWindows(gone))) continue;
            TouchpadHidesOwed.Remove(id);
            EndTouchpadTask(id);
        }
    }

    /// <summary>Deletes a touchpad's give-back task, and the session's record
    /// of it, so its next hide registers another.</summary>
    private void EndTouchpadTask(string id)
    {
        GiveBackTask.Delete(id);
        _touchpadGiveBackTasks.Remove(id);
    }

    /// <summary>Whether a capture whose touchpad left is to be read again:
    /// its node is back. True once per return, since a read that fails with
    /// the node there gives the touchpad back to Windows.</summary>
    internal bool TakeTouchpadRetry(TouchpadCapture capture)
    {
        if (!_touchpadsGone.Contains(capture.DevicePath) || !TouchpadNodePresent(capture.DeviceInstanceId)) return false;
        _touchpadsGone.Remove(capture.DevicePath);
        return true;
    }

    /// <summary>The release at engine stop and at exit: every touchpad's hide
    /// comes off, kept cloaks or not, and its capture gives it back with a
    /// restart. A touchpad with no capture is restarted for Windows. A hide
    /// still on goes on the retry's list.</summary>
    private void ReleaseTouchpads(TouchpadCapture[] captures, bool keepCloaks)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ud in UserDeviceSnapshot())
            if (ud.TouchpadOnlyInPadForge && IsTouchpadCollectionRow(ud))
                ids.Add(HidHideController.DevicePathToInstanceId(ud.DevicePath));
        foreach (var capture in captures) ids.Add(capture.DeviceInstanceId);
        var released = new List<string>(ids);
        // No touchpad to give back: HidHide is asked nothing, and kept cloaks
        // stay a release with no driver access, as they always were.
        if (ids.Count == 0) return;
        if (keepCloaks)
        {
            try
            {
                if (HidHideController.IsAvailable()) HidHideController.RemoveManaged(ids);
            }
            catch { /* best effort, as the full removal is */ }
        }
        var blacklist = HidHideController.GetBlacklist();
        var stillHidden = blacklist == null ? null : new HashSet<string>(blacklist, StringComparer.OrdinalIgnoreCase);
        foreach (var capture in captures)
        {
            ids.Remove(capture.DeviceInstanceId);
            if (stillHidden == null || stillHidden.Contains(capture.DeviceInstanceId))
                TouchpadHidesOwed.Add(capture.DeviceInstanceId);
            if (stillHidden != null && stillHidden.Contains(capture.DeviceInstanceId))
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"TOUCHPAD still hidden at release: {capture.DeviceInstanceId}");
                continue;
            }
            capture.SetCapture(false);
        }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        foreach (var capture in captures)
        {
            TimeSpan left = deadline - DateTime.UtcNow;
            if (!capture.WaitForTransition(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                PadForge.Engine.SdlDiagLog.WriteLine($"TOUCHPAD release is still completing: {capture.DeviceInstanceId}");
        }
        // A touchpad hidden with no capture, by a session before this one.
        foreach (var id in ids)
        {
            if (stillHidden == null || stillHidden.Contains(id)) TouchpadHidesOwed.Add(id);
            if (stillHidden == null || !stillHidden.Contains(id))
                _ = TouchpadCapture.RestartForWindows(id);
        }
        // A hide seen gone ends its boot task. One still on keeps its task, the
        // give-back that outlives this process.
        if (stillHidden != null)
            foreach (var id in released)
                if (!stillHidden.Contains(id)) EndTouchpadTask(id);
        _requestedTouchpadCaptures.Clear();
        _touchpadGiveBacksOwed.Clear();
        _touchpadsGone.Clear();
    }

    private void OnTouchpadCaptureChanged(TouchpadCapture capture, int revision, bool rollback, string error)
    {
        var owner = _inputManager;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_stopped != 0 || !ReferenceEquals(owner, _inputManager)) return;
            _ = HandleTouchpadCaptureChanged(capture, revision, rollback, error);
        }));
    }

    /// <summary>A capture's status on the UI thread. A touchpad PadForge
    /// cannot read goes back to Windows, so no pointer is left hidden with
    /// nothing reading it. A touchpad that left is no such touchpad: a USB or
    /// detachable one fails its read as it goes, or at an engine start without
    /// it. Its switch stays on, and its capture reads it again when it returns
    /// (<see cref="TakeTouchpadRetry"/>).</summary>
    internal async System.Threading.Tasks.Task HandleTouchpadCaptureChanged(TouchpadCapture capture, int revision, bool rollback, string error)
    {
        if (!capture.IsCurrentCaptureRevision(revision) && !(rollback && capture.CanRollbackCaptureRevision(revision))) return;
        var ud = UserDeviceSnapshot().FirstOrDefault(d => d.IsTouchpad
            && string.Equals(d.DevicePath, capture.DevicePath, StringComparison.OrdinalIgnoreCase));
        if (ud == null) return;
        var row = _mainVm.Devices.FindByGuid(ud.InstanceGuid);
        if (row != null) row.TouchpadCaptureState = capture.CaptureState;
        if (capture.CaptureState != TabletCaptureState.Failed || !ud.TouchpadOnlyInPadForge) return;

        if (TouchpadGoneSettleMs > 0)
        {
            await System.Threading.Tasks.Task.Delay(TouchpadGoneSettleMs);
            if (_stopped != 0 || !capture.IsCurrentCaptureRevision(revision) || !ud.TouchpadOnlyInPadForge) return;
        }
        if (!TouchpadNodePresent(capture.DeviceInstanceId))
        {
            _touchpadsGone.Add(capture.DevicePath);
            PadForge.Engine.SdlDiagLog.WriteLine($"TOUCHPAD left, switch kept: {capture.DeviceInstanceId}: {error}");
            return;
        }

        ud.TouchpadOnlyInPadForge = false;
        if (row != null) row.TouchpadOnlyInPadForge = false;
        _settingsService?.MarkDirty();
        _mainVm.StatusText = string.Format(Strings.Instance.Status_TouchpadReadFailed_Format,
            string.IsNullOrWhiteSpace(capture.Name) ? ud.InstanceName : capture.Name);
        PadForge.Engine.SdlDiagLog.WriteLine($"TOUCHPAD given back after a failed read: {capture.DeviceInstanceId}: {error}");
        ApplyAutomaticDeviceHiding();
        _mainVm.Devices.RefreshDriverOffer();
    }

    /// <summary>The state of a touchpad row's capture for its line under the
    /// switch: Shared while the switch is off or no capture was made.</summary>
    private TabletCaptureState TouchpadCaptureStateOf(UserDevice ud)
    {
        if (ud == null || !ud.TouchpadOnlyInPadForge) return TabletCaptureState.Shared;
        var capture = (_inputManager?.GetTouchpadCaptures() ?? Array.Empty<TouchpadCapture>())
            .FirstOrDefault(c => string.Equals(c.DevicePath, ud.DevicePath, StringComparison.OrdinalIgnoreCase));
        return capture?.CaptureState ?? TabletCaptureState.Shared;
    }

    /// <summary>The capture of a touchpad row, or null while the engine is
    /// stopped or before the hiding pass asked for one.</summary>
    internal TouchpadCapture TouchpadCaptureOf(UserDevice ud)
    {
        if (ud == null || string.IsNullOrEmpty(ud.DevicePath)) return null;
        return (_inputManager?.GetTouchpadCaptures() ?? Array.Empty<TouchpadCapture>())
            .FirstOrDefault(c => string.Equals(c.DevicePath, ud.DevicePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The device records, copied under their lock.</summary>
    private static UserDevice[] UserDeviceSnapshot()
    {
        var devices = SettingsManager.UserDevices;
        if (devices?.Items == null) return Array.Empty<UserDevice>();
        lock (devices.SyncRoot) return devices.Items.ToArray();
    }

    private static UserDevice TouchpadRowOf(UserDevice[] snapshot, TouchpadCapture capture)
        => snapshot.FirstOrDefault(d => d.IsTouchpad
            && string.Equals(d.DevicePath, capture.DevicePath, StringComparison.OrdinalIgnoreCase));
}
