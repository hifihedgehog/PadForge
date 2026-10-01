using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Nefarius.Utilities.DeviceManagement.PnP;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The DualShock 3 behind a device row (#474), as "address/source": the
    /// pad's Bluetooth address, and what serves its yaw word. Every DualShock
    /// 3 on PadForge's own path reaches SDL as one virtual joystick and shares
    /// one row, so a gyro calibration names the pad by its address.
    /// <see cref="Ds3DirectService"/> knows it for its live connection
    /// ("direct"). A pad under DsHidMini carries it on its device node, where
    /// BthPS3 (Bluetooth) or DsHidMini (USB, driver/Ds3.c) publishes
    /// DEVPKEY_Bluetooth_DeviceAddress, and the source is that node's driver
    /// release ("node:3.15.0.0"): PadForge's own path reads the word against
    /// 512, DsHidMini 3.15.0 and later zero it, and earlier releases don't,
    /// so a calibration taken under one is wrong under another.
    /// </summary>
    internal static class Ds3UnitIdentity
    {
        // {2BD67D8B-8BEB-48D5-87E0-6CDA3428040A},1, the key
        // Ds3PairingService.ReadDeviceNodeAddress reads.
        private static readonly DevicePropertyKey AddressKey = CustomDeviceProperty.CreateCustomDeviceProperty(
            new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), 1, typeof(string));

        // DEVPKEY_Device_DriverVersion (devpkey.h).
        private static readonly DevicePropertyKey DriverVersionKey = CustomDeviceProperty.CreateCustomDeviceProperty(
            new Guid("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), 3, typeof(string));

        /// <summary>Reads a node path's identity. Tests replace it.</summary>
        internal static Func<string, string> NodeReader = ReadNodeIdentity;

        // Keyed by SDL instance id, which changes on every attach, so another
        // pad on the same port or Bluetooth slot is read again.
        private static readonly ConcurrentDictionary<uint, string> NodeIdentities = new();
        private static readonly ConcurrentDictionary<uint, byte> Reading = new();

        // The last identity each row resolved, served while a new connection's
        // read is still running. Without it the first bias read after a
        // reconnect found no owner and left a large offset uncorrected for the
        // bias snapshot's quarter second.
        private static readonly ConcurrentDictionary<Guid, string> LastByRow = new();

        /// <summary>The pad's identity, or null when it can't be known. Never
        /// blocks: a node read that <see cref="Prime"/> didn't do at arrival
        /// runs off the calling thread, which may be the poll thread.</summary>
        internal static string Identity(UserDevice ud)
        {
            var device = ud?.Device;
            if (device == null) return null;
            uint id = device.SdlInstanceId;
            if (id == 0) return null;
            string direct = Ds3DirectService.GetPadAddress(id);
            if (direct != null) return direct + "/direct";
            // PadForge's own path with no readable address on this connection.
            if (Ds3DirectService.GetDevicePath(id) != null) return null;
            if (NodeIdentities.TryGetValue(id, out string known)) return known;
            string path = ud.DevicePath;
            Guid row = ud.InstanceGuid;
            if (!string.IsNullOrEmpty(path) && Reading.TryAdd(id, 0))
                Task.Run(() => Resolve(id, row, path));
            return LastByRow.TryGetValue(row, out string last) ? last : null;
        }

        /// <summary>Reads a DualShock 3's node when Step 1 brings its row
        /// online, so the first bias read after a connect knows the pad. A
        /// device-node read at arrival, on the poll thread, like the rest of
        /// the arrival work.</summary>
        internal static void Prime(UserDevice ud)
        {
            var device = ud?.Device;
            if (device == null || !DualShock3Motion.Is(ud.VendorId, ud.ProdId)) return;
            uint id = device.SdlInstanceId;
            if (id == 0 || Ds3DirectService.GetDevicePath(id) != null) return;
            if (NodeIdentities.ContainsKey(id) || !Reading.TryAdd(id, 0)) return;
            Resolve(id, ud.InstanceGuid, ud.DevicePath);
        }

        private static void Resolve(uint id, Guid row, string path)
        {
            string identity = null;
            try { identity = NodeReader(path); }
            catch { }
            if (NodeIdentities.Count > 64) NodeIdentities.Clear();
            NodeIdentities[id] = identity;
            if (identity != null) LastByRow[row] = identity;
            Reading.TryRemove(id, out _);
        }

        private static string ReadNodeIdentity(string interfacePath)
        {
            if (string.IsNullOrEmpty(interfacePath)) return null;
            IPnPDevice node = PnPDevice.GetDeviceByInterfaceId(interfacePath, DeviceLocationFlags.Normal);
            // The HID collection first, then the nodes above it, where the
            // function driver's device carries the property.
            for (int depth = 0; node != null && depth < 3; depth++, node = node.Parent)
            {
                string address = Normalize(node.GetProperty<string>(AddressKey));
                if (address == null) continue;
                string release = node.GetProperty<string>(DriverVersionKey);
                return address + "/node:" + (string.IsNullOrWhiteSpace(release) ? "?" : release.Trim());
            }
            return null;
        }

        /// <summary>Twelve lowercase hex digits, or null for anything that
        /// can't name one pad: a malformed string, all zeros, or a locally
        /// administered address, which is what DsHidMini derives from the USB
        /// port for a pad that won't report its own
        /// (DsUsb_Ds3SynthesizeDeviceAddress).</summary>
        internal static string Normalize(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            var sb = new System.Text.StringBuilder(12);
            foreach (char c in address)
            {
                if (c == ':' || c == '-' || char.IsWhiteSpace(c)) continue;
                if (!Uri.IsHexDigit(c)) return null;
                sb.Append(char.ToLowerInvariant(c));
            }
            if (sb.Length != 12) return null;
            string hex = sb.ToString();
            if (hex == "000000000000") return null;
            if ((Convert.ToInt32(hex.Substring(0, 2), 16) & 0x02) != 0) return null;
            return hex;
        }

        /// <summary>Forgets every connection and row. Tests only.</summary>
        internal static void ResetForTests()
        {
            NodeIdentities.Clear();
            Reading.Clear();
            LastByRow.Clear();
            NodeReader = ReadNodeIdentity;
        }
    }
}
