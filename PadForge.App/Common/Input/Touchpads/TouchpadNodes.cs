using System;
using System.Collections.Generic;
using System.Linq;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace PadForge.Common.Input.Touchpads
{
    /// <summary>
    /// Where a precision touchpad's collections sit in the device tree. The
    /// touchpad's HID device has its touchpad collection (Digitizer 0x0D,
    /// Touch Pad 0x05) and its mouse collection (Generic Desktop 0x01, Mouse
    /// 0x02) as children, and Windows names each by its usage among its
    /// hardware IDs: HID_DEVICE_UP:000D_U:0005 and HID_DEVICE_UP:0001_U:0002,
    /// both under ACPI\GXTP5100 on the Goodix I2C touchpad this was measured
    /// on. Read through the property calls the driver switch reads a parent
    /// with (TouchSenseBinding.ParentOf), so no collection is opened.
    /// </summary>
    internal static class TouchpadNodes
    {
        /// <summary>The hardware ID of a touchpad collection.</summary>
        internal const string TouchpadHardwareId = "HID_DEVICE_UP:000D_U:0005";

        /// <summary>The hardware ID of a mouse collection.</summary>
        internal const string MouseHardwareId = "HID_DEVICE_UP:0001_U:0002";

        /// <summary>A collection's parent and the parent's children with
        /// their hardware IDs, or null when the collection is not present.</summary>
        internal delegate (string Parent, IReadOnlyList<(string Id, IReadOnlyList<string> HardwareIds)> Children)? TreeReader(string instanceId);

        /// <summary>The touchpad collection of the device a collection
        /// belongs to: the collection itself when it is a touchpad's, the
        /// touchpad collection beside it when it is a touchpad's mouse
        /// collection, or null for any other collection, or when the tree
        /// cannot be read.</summary>
        internal static string TouchpadOf(string instanceId) => TouchpadOf(instanceId, ReadTree);

        /// <summary>The same answer from a tree a caller hands in.</summary>
        internal static string TouchpadOf(string instanceId, TreeReader read)
        {
            var tree = string.IsNullOrEmpty(instanceId) ? null : read(instanceId);
            if (tree == null) return null;
            var (_, children) = tree.Value;
            var self = children.FirstOrDefault(c => string.Equals(c.Id, instanceId, StringComparison.OrdinalIgnoreCase));
            if (self.Id != null && Has(self.HardwareIds, TouchpadHardwareId)) return self.Id;
            if (self.Id == null || !Has(self.HardwareIds, MouseHardwareId)) return null;
            var touchpads = children.Where(c => Has(c.HardwareIds, TouchpadHardwareId)).ToList();
            // A device with two touchpad collections says nothing about which
            // one this mouse collection reports for.
            return touchpads.Count == 1 ? touchpads[0].Id : null;
        }

        /// <summary>The mouse collections beside a touchpad collection, the
        /// ones its touchpad reports through in mouse mode.</summary>
        internal static List<string> MiceBeside(string touchpadId) => MiceBeside(touchpadId, ReadTree);

        /// <summary>The same answer from a tree a caller hands in.</summary>
        internal static List<string> MiceBeside(string touchpadId, TreeReader read)
        {
            var tree = string.IsNullOrEmpty(touchpadId) ? null : read(touchpadId);
            if (tree == null) return new List<string>();
            return tree.Value.Children
                .Where(c => Has(c.HardwareIds, MouseHardwareId))
                .Select(c => c.Id)
                .ToList();
        }

        private static bool Has(IReadOnlyList<string> ids, string id)
            => ids != null && ids.Any(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The device tree. The test host reads none.</summary>
        private static (string Parent, IReadOnlyList<(string Id, IReadOnlyList<string> HardwareIds)> Children)? ReadTree(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId) || RealHidAccess.Refused) return null;
            try
            {
                string parent = PnPDevice.GetDeviceByInstanceId(instanceId, DeviceLocationFlags.Normal)
                    .GetProperty<string>(DevicePropertyKey.Device_Parent);
                if (string.IsNullOrEmpty(parent)) return null;
                string[] childIds = PnPDevice.GetDeviceByInstanceId(parent, DeviceLocationFlags.Normal)
                    .GetProperty<string[]>(DevicePropertyKey.Device_Children);
                if (childIds == null) return null;
                var children = new List<(string Id, IReadOnlyList<string> HardwareIds)>(childIds.Length);
                foreach (string id in childIds)
                {
                    string[] hardwareIds = null;
                    try
                    {
                        hardwareIds = PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Normal)
                            .GetProperty<string[]>(DevicePropertyKey.Device_HardwareIds);
                    }
                    catch { }
                    children.Add((id, hardwareIds ?? Array.Empty<string>()));
                }
                return (parent, children);
            }
            catch
            {
                return null;
            }
        }
    }
}
