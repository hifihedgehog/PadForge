using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Touchpads;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Touchpads;

namespace PadForge.Common.Input;

/// <summary>
/// Precision touchpads PadForge reads alone, the pen tablet's capture
/// (InputManager.Tablets.cs) for a touchpad. The hiding pass decides which
/// touchpad is hidden and starts its capture (InputService.Touchpads.cs).
/// The touchpad's row keeps reading through <see cref="PrecisionTouchpadReader"/>,
/// which files a capture's state under the touchpad's own path. This phase
/// gives the touchpad's mouse row, which Windows' pointer fed before, the
/// motion and buttons of the captured touchpad (<see cref="TouchpadMouseDevice"/>).
/// </summary>
public partial class InputManager
{
    /// <summary>Raised when a touchpad capture's state changes: the capture,
    /// the revision of the change, whether it asks for a hide just written to
    /// be taken back, and the error.</summary>
    public event Action<TouchpadCapture, int, bool, string> TouchpadCaptureChanged;

    /// <summary>The mouse rows fed by a capture, by the touchpad's path.
    /// Poll thread only.</summary>
    private readonly Dictionary<string, TouchpadMouseDevice> _touchpadMice = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mouse collections beside each captured touchpad, read
    /// once per capture. Poll thread only.</summary>
    private readonly Dictionary<string, List<string>> _touchpadMouseIds = new(StringComparer.OrdinalIgnoreCase);

    private void AttachTouchpadCaptures(PrecisionTouchpadReader reader) => reader.CaptureChanged += OnTouchpadCaptureChanged;

    private void DetachTouchpadCaptures(PrecisionTouchpadReader reader)
    {
        if (reader != null) reader.CaptureChanged -= OnTouchpadCaptureChanged;
    }

    private void OnTouchpadCaptureChanged(TouchpadCapture capture, int revision, bool rollback, string error)
    {
        if (_disposed) return;
        TouchpadCaptureChanged?.Invoke(capture, revision, rollback, error);
        DevicesUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether the running engine can read a touchpad alone: its
    /// touchpad reader exists. The hiding pass hides no touchpad before.</summary>
    public bool CanCaptureTouchpads => _ptpReader != null;

    /// <summary>Every touchpad capture the running engine made.</summary>
    public TouchpadCapture[] GetTouchpadCaptures() => _ptpReader?.GetCaptures() ?? Array.Empty<TouchpadCapture>();

    /// <summary>The capture of a touchpad row's collection, made on the
    /// first ask, with the row's own path, name and IDs. Null while the
    /// engine is stopped, and for a row whose path names no HID
    /// collection.</summary>
    public TouchpadCapture TouchpadCaptureFor(UserDevice row)
    {
        if (row == null || string.IsNullOrEmpty(row.DevicePath)) return null;
        string instanceId = HidHideController.DevicePathToInstanceId(row.DevicePath);
        if (instanceId == null || !instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) return null;
        return _ptpReader?.CaptureFor(row.DevicePath, instanceId, row.InstanceName, row.VendorId, row.ProdId);
    }

    /// <summary>Phase 1d2. Gives each captured touchpad's mouse row the
    /// touchpad's pointer, and gives the row back to the Raw Input pass once
    /// the capture stops reading.</summary>
    private void UpdateTouchpadMouseRows(ref bool changed)
    {
        var reading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capture in GetTouchpadCaptures())
        {
            if (!capture.IsReading) continue;
            reading.Add(capture.DevicePath);
            if (_touchpadMice.TryGetValue(capture.DevicePath, out var fed))
            {
                var current = FindOnlineDeviceByInstanceGuid(fed.InstanceGuid);
                if (current != null && ReferenceEquals(current.Device, fed) && fed.IsAttached) continue;
                _touchpadMice.Remove(capture.DevicePath);
                fed.Dispose();
            }

            var row = FindTouchpadMouseRow(capture);
            if (row == null) continue;
            var device = new TouchpadMouseDevice(capture.DevicePath, row.DevicePath, row.InstanceGuid,
                row.InstanceName, row.VendorId, row.ProdId, _ptpReader);
            // The Raw Input wrapper the row held reads nothing of the
            // touchpad while HidHide hides it. Its handle leaves the mouse
            // lane's tracking on the next prune, and the lane skips the path
            // while this row is online (EnumerateMice).
            var previous = row.Device;
            row.LoadFromExternalDevice(device);
            row.IsOnline = true;
            if (previous != null && !ReferenceEquals(previous, device))
            {
                try { previous.Dispose(); } catch { }
            }
            _touchpadMice[capture.DevicePath] = device;
            MarkChanged(ref changed, "touchpad", $"+ pointer of {capture.Name} on {row.InstanceName}");
        }

        List<string> stopped = null;
        foreach (var pair in _touchpadMice)
            if (!reading.Contains(pair.Key)) (stopped ??= new List<string>()).Add(pair.Key);
        if (stopped == null) return;
        foreach (string path in stopped)
        {
            var device = _touchpadMice[path];
            _touchpadMice.Remove(path);
            _touchpadMouseIds.Remove(path);
            RetireTouchpadMouse(device);
            MarkChanged(ref changed, "touchpad", $"- pointer of {path}");
        }
        // The mouse row comes back on a Raw Input wrapper, which the
        // trackpad redirect then finds again (Phase 1d).
        _ptpMouseRedirected = false;
    }

    /// <summary>The mouse row of a captured touchpad: a mouse record whose
    /// collection is beside the touchpad's collection under one device. An
    /// online record comes first.</summary>
    private UserDevice FindTouchpadMouseRow(TouchpadCapture capture)
    {
        if (!_touchpadMouseIds.TryGetValue(capture.DevicePath, out var mice))
        {
            mice = TouchpadNodes.MiceBeside(capture.DeviceInstanceId);
            _touchpadMouseIds[capture.DevicePath] = mice;
        }
        if (mice.Count == 0) return null;
        var devices = SettingsManager.UserDevices?.Items;
        if (devices == null) return null;
        UserDevice found = null;
        lock (SettingsManager.UserDevices.SyncRoot)
        {
            foreach (var ud in devices)
            {
                if (ud == null || ud.CapType != InputDeviceType.Mouse || string.IsNullOrEmpty(ud.DevicePath)) continue;
                string id = HidHideController.DevicePathToInstanceId(ud.DevicePath);
                if (id == null || !mice.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                if (ud.IsOnline) return ud;
                found ??= ud;
            }
        }
        return found;
    }

    /// <summary>Takes a touchpad's mouse row offline, so the Raw Input pass
    /// opens it again as the mouse Windows reports. A row the poll already
    /// took offline, when the capture stopped first, is cleared the same
    /// way, so nothing it held stays on a slot.</summary>
    private void RetireTouchpadMouse(TouchpadMouseDevice device)
    {
        var ud = SettingsManager.FindDeviceByInstanceGuid(device.InstanceGuid);
        if (ud != null && ReferenceEquals(ud.Device, device))
        {
            ud.IsOnline = false;
            ud.Device = null;
            NeutralizeMappedOutputsFor(ud);
        }
        device.Dispose();
    }

    /// <summary>Retires every touchpad mouse row, for the manager's
    /// shutdown.</summary>
    private void ShutdownTouchpadMice()
    {
        foreach (var device in _touchpadMice.Values) RetireTouchpadMouse(device);
        _touchpadMice.Clear();
        _touchpadMouseIds.Clear();
    }
}
