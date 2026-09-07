using PadForge.Engine;
using PadForge.Engine.Tablets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common.Input;

public partial class InputManager
{
    private WindowsTabletReader _tabletReader;
    private readonly Dictionary<Guid, WindowsTabletDevice> _tabletDevices = new();
    public event Action<WindowsTabletDevice, int, bool, string> TabletCaptureChanged;

    private void StartTabletReader()
    {
        _tabletReader = new WindowsTabletReader(HidHideController.IsHidMaestroDeviceInstance);
        _tabletReader.CaptureChanged += OnTabletCaptureChanged;
        _tabletReader.Start();
    }

    private void OnTabletCaptureChanged(WindowsTabletDevice device, int revision, bool rollback, string error)
    {
        if (_disposed) return;
        TabletCaptureChanged?.Invoke(device, revision, rollback, error);
        DevicesUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void StopTabletReader()
    {
        var reader = _tabletReader;
        _tabletReader = null;
        if (reader != null)
        {
            reader.CaptureChanged -= OnTabletCaptureChanged;
            reader.Dispose();
        }
        _tabletDevices.Clear();
    }

    public WindowsTabletDevice[] GetTabletDevices() => _tabletReader?.GetDevices() ?? Array.Empty<WindowsTabletDevice>();

    private void UpdateTabletDevices(ref bool changed)
    {
        var live = GetTabletDevices();
        var present = new HashSet<Guid>();
        foreach (var device in live)
        {
            if (!device.IsAttached) continue;
            present.Add(device.InstanceGuid);
            var ud = FindOrCreateUserDevice(device.InstanceGuid);
            if (!ReferenceEquals(ud.Device, device))
            {
                ud.LoadFromExternalDevice(device);
                ud.IsOnline = true;
                _tabletDevices[device.InstanceGuid] = device;
                MarkChanged(ref changed, "tablet", $"+ {device.Name} {device.VendorId:X4}:{device.ProductId:X4}");
            }
        }
        foreach (var pair in _tabletDevices.ToArray())
        {
            if (present.Contains(pair.Key)) continue;
            var ud = FindOnlineDeviceByInstanceGuid(pair.Key);
            if (ud != null && ReferenceEquals(ud.Device, pair.Value))
            {
                ud.IsOnline = false;
                NeutralizeMappedOutputsFor(ud);
                ud.Device = null;
            }
            _tabletDevices.Remove(pair.Key);
            MarkChanged(ref changed, "tablet", $"- {pair.Value.Name}");
        }
    }
}
