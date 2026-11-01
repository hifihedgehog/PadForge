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
/// pointer, so it is never left hidden with nothing reading it, kept cloaks
/// included.
/// </summary>
public partial class InputService
{
    /// <summary>The touchpad paths whose capture the last apply asked for.</summary>
    private readonly HashSet<string> _requestedTouchpadCaptures = new(StringComparer.OrdinalIgnoreCase);

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
        if (_inputManager?.CanCaptureTouchpads != true) return;
        foreach (var ud in snapshot)
        {
            if (!ud.TouchpadOnlyInPadForge || !IsTouchpadCollectionRow(ud)) continue;
            string id = HidHideController.DevicePathToInstanceId(ud.DevicePath);
            if (keepOut(id) || HidHideController.IsHidMaestroDeviceInstance(id))
            {
                hidLog.Add($"HIDHIDE touchpad {id} kept out");
                continue;
            }
            desiredIds.Add(id);
            touchpadIds.Add(id);
            hidLog.Add($"HIDHIDE touchpad {ud.VendorId:X4}:{ud.ProdId:X4} id={id}");
        }
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
            foreach (var capture in captures)
            {
                var row = TouchpadRowOf(snapshot, capture);
                if (row?.TouchpadOnlyInPadForge != true && capture.CaptureWasRequested)
                    capture.SetCapture(false);
                else if (row?.TouchpadOnlyInPadForge == true || _requestedTouchpadCaptures.Contains(capture.DevicePath))
                    capture.ReportPolicyFailure("The device hiding change could not be verified.");
            }
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
                    requested && newCloaks.Contains(capture.DeviceInstanceId));
            }
        }

        foreach (var id in removed ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) continue;
            // A capture that read the touchpad gives it back with its own
            // restart (TouchpadCapture.SetCapture).
            if (captures.Any(c => c.CaptureWasRequested
                    && string.Equals(c.DeviceInstanceId, id, StringComparison.OrdinalIgnoreCase))) continue;
            if (!string.Equals(TouchpadNodes.TouchpadOf(id), id, StringComparison.OrdinalIgnoreCase)) continue;
            _ = TouchpadCapture.RestartForWindows(id);
        }
    }

    /// <summary>The release at engine stop and at exit: every touchpad's hide
    /// comes off, kept cloaks or not, and its capture gives it back with a
    /// restart. A touchpad with no capture is restarted for Windows.</summary>
    private void ReleaseTouchpads(TouchpadCapture[] captures, bool keepCloaks)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ud in UserDeviceSnapshot())
            if (ud.TouchpadOnlyInPadForge && IsTouchpadCollectionRow(ud))
                ids.Add(HidHideController.DevicePathToInstanceId(ud.DevicePath));
        foreach (var capture in captures) ids.Add(capture.DeviceInstanceId);
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
            if (stillHidden == null || !stillHidden.Contains(id))
                _ = TouchpadCapture.RestartForWindows(id);
        _requestedTouchpadCaptures.Clear();
    }

    private void OnTouchpadCaptureChanged(TouchpadCapture capture, int revision, bool rollback, string error)
    {
        var owner = _inputManager;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_stopped != 0 || !ReferenceEquals(owner, _inputManager)) return;
            if (!capture.IsCurrentCaptureRevision(revision) && !(rollback && capture.CanRollbackCaptureRevision(revision))) return;
            var ud = UserDeviceSnapshot().FirstOrDefault(d => d.IsTouchpad
                && string.Equals(d.DevicePath, capture.DevicePath, StringComparison.OrdinalIgnoreCase));
            if (ud == null) return;
            var row = _mainVm.Devices.FindByGuid(ud.InstanceGuid);
            if (row != null) row.TouchpadCaptureState = capture.CaptureState;
            // A touchpad PadForge cannot read goes back to Windows, so no
            // pointer is left hidden with nothing reading it.
            if (capture.CaptureState == TabletCaptureState.Failed && ud.TouchpadOnlyInPadForge)
            {
                ud.TouchpadOnlyInPadForge = false;
                if (row != null) row.TouchpadOnlyInPadForge = false;
                _settingsService?.MarkDirty();
                _mainVm.StatusText = string.Format(Strings.Instance.Status_TouchpadReadFailed_Format,
                    string.IsNullOrWhiteSpace(capture.Name) ? ud.InstanceName : capture.Name);
                PadForge.Engine.SdlDiagLog.WriteLine($"TOUCHPAD given back after a failed read: {capture.DeviceInstanceId}: {error}");
                ApplyAutomaticDeviceHiding();
                _mainVm.Devices.RefreshDriverOffer();
            }
        }));
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
