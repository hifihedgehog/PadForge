using PadForge.Common;
using PadForge.Engine.Data;
using PadForge.Common.Input;
using PadForge.Engine.Tablets;
using PadForge.Resources.Strings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Services;

public partial class InputService
{
    private readonly HashSet<Guid> _requestedTabletHides = new();

    private void PrepareTabletVisibilityChanges(UserDevice[] snapshot)
    {
        foreach (var device in _inputManager?.GetTabletDevices() ?? Array.Empty<WindowsTabletDevice>())
        {
            var row = snapshot.FirstOrDefault(d => d.InstanceGuid == device.InstanceGuid);
            if (_requestedTabletHides.Contains(device.InstanceGuid) && row?.HidHideEnabled != true)
                device.PrepareForUnhide();
        }
    }

    private void RefreshTabletCapture(UserDevice[] snapshot, bool syncSucceeded, IEnumerable<string> added)
    {
        var live = _inputManager?.GetTabletDevices() ?? Array.Empty<WindowsTabletDevice>();
        if (live.Length == 0) return;
        var blacklist = HidHideController.GetBlacklist();
        bool activeRead = HidHideController.TryGetActive(out bool active);
        if (!syncSucceeded || blacklist == null || !activeRead)
        {
            foreach (var tablet in live)
            {
                var row = snapshot.FirstOrDefault(d => d.InstanceGuid == tablet.InstanceGuid);
                if (row?.HidHideEnabled != true && tablet.CaptureWasRequested)
                {
                    tablet.SetCapture(false);
                    _mainVm.StatusText = Strings.Instance.Tablet_InputFailed;
                }
                else if (row?.HidHideEnabled == true || _requestedTabletHides.Contains(tablet.InstanceGuid))
                    tablet.ReportPolicyFailure("The device hiding change could not be verified.");
            }
            return;
        }
        var hidden = new HashSet<string>(blacklist, StringComparer.OrdinalIgnoreCase);
        var newCloaks = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
        _requestedTabletHides.Clear();
        foreach (var tablet in live)
        {
            var row = snapshot.FirstOrDefault(d => d.InstanceGuid == tablet.InstanceGuid);
            bool requested = _mainVm.Settings.EnableInputHiding && row?.HidHideEnabled == true;
            if (requested) _requestedTabletHides.Add(tablet.InstanceGuid);
            tablet.SetCapture(active && hidden.Contains(tablet.DeviceInstanceId), requested && newCloaks.Contains(tablet.DeviceInstanceId));
        }
    }

    private void FinishTabletRelease(WindowsTabletDevice[] devices)
    {
        var blacklist = HidHideController.GetBlacklist();
        bool activeRead = HidHideController.TryGetActive(out bool active);
        if (blacklist == null || !activeRead)
        {
            foreach (var device in devices)
            {
                device.SetCapture(false);
                PadForge.Engine.SdlDiagLog.WriteLine($"TABLET restoring after hiding readback failed: {device.DeviceInstanceId}");
            }
        }
        else
        {
            var hidden = new HashSet<string>(blacklist, StringComparer.OrdinalIgnoreCase);
            foreach (var device in devices)
            {
                bool remainsHidden = active && hidden.Contains(device.DeviceInstanceId);
                if (remainsHidden && _stopped != 0) continue;
                device.SetCapture(remainsHidden);
            }
        }
        if (_stopped != 0)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            foreach (var device in devices)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (!device.WaitForTransition(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                    PadForge.Engine.SdlDiagLog.WriteLine($"TABLET restore is still completing: {device.DeviceInstanceId}");
            }
        }
        _requestedTabletHides.Clear();
    }

    private void OnTabletCaptureChanged(WindowsTabletDevice device, int revision, bool rollback, string error)
    {
        var owner = _inputManager;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_stopped != 0 || !ReferenceEquals(owner, _inputManager)) return;
            bool rollbackCurrent = rollback && device.CanRollbackCaptureRevision(revision);
            if (!rollbackCurrent && !device.IsCurrentCaptureRevision(revision)) return;
            var ud = SettingsManager.FindDeviceByInstanceGuid(device.InstanceGuid);
            if (ud == null || (!ReferenceEquals(ud.Device, device) && !(rollbackCurrent && ud.Device == null))) return;
            var row = _mainVm.Devices.FindByGuid(device.InstanceGuid);
            if (row != null) row.TabletCaptureState = device.CaptureState;
            if (!string.IsNullOrEmpty(error))
                _mainVm.StatusText = string.Format(Strings.Instance.Tablet_CaptureFailed_Format, device.Name);
            if (rollbackCurrent && ud.HidHideEnabled)
            {
                ud.HidHideEnabled = false;
                if (row != null) row.HidHideEnabled = false;
                _settingsService?.MarkDirty();
                ApplyDeviceHiding();
            }
        }));
    }
}
