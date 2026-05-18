using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Writes rumble + impulse-trigger output to physical Xbox One / Elite /
    /// Elite Series 2 / Xbox Series X|S controllers via a raw HID output
    /// report. Bypasses SDL3 / XInput / WGI / GameInput entirely.
    ///
    /// <para>Two report shapes per SDL3 HIDAPI's verified
    /// <c>SDL_hidapi_xboxone.c</c> (<c>HIDAPI_DriverXboxOne_UpdateRumble</c>):
    /// </para>
    /// <code>
    /// // Bluetooth (PID 0x02E0, 0x02FD, 0x0B05, 0x0B13)
    /// // 9 bytes: { 0x03, 0x0F, LT, RT, LM, RM, 0xFF, 0x00, 0xEB }
    ///
    /// // GIP (USB / Xbox Wireless Adapter — PID 0x02D1, 0x02DD, 0x02E3,
    /// //       0x02EA, 0x02FF, 0x0B00, 0x0B12)
    /// // 13 bytes: { 0x09, 0x00, 0x00, 0x09, 0x00, 0x0F, LT, RT, LM, RM,
    /// //             0xFF, 0x00, 0xEB }
    /// </code>
    ///
    /// <para>Device discovery uses the Ds4InputDump pattern at
    /// <c>tools/Ds4InputDump/Program.cs:208-248</c>: enumerate HID
    /// interfaces via <c>HidD_GetHidGuid + DIGCF_DEVICEINTERFACE</c>,
    /// CreateFile each with access=0 (no permissions required), read
    /// <c>HidD_GetAttributes</c> for VID/PID, filter. This is more robust
    /// than instance-ID string matching (filter drivers like
    /// <c>xinputhid.sys</c> can hide the device class enumerator from
    /// SetupDiEnumDeviceInfo but the HID interface remains openable).
    /// </para>
    ///
    /// <para>HIDMaestro virtual-controller loopback guard: each enumerated
    /// HID interface is also checked against the PadForge fork's
    /// <c>StableXInputInstance.FindAll</c> result set. That list is the
    /// already-HM-filtered set of physical instance IDs for this VID/PID
    /// (substring + 16-level PnP parent walk for "HIDMaestro" hardware
    /// IDs). Any HID interface whose instance-ID-portion isn't in that
    /// set is rejected as a possible HM virtual.</para>
    /// </summary>
    internal static class XboxImpulseHidWriter
    {
        // ─────────────────────────────────────────────
        //  Bluetooth-PID detection
        // ─────────────────────────────────────────────

        /// <summary>Bluetooth-attached Xbox controllers use the simpler
        /// 9-byte HID output report. USB / Xbox Wireless Adapter
        /// controllers use the 13-byte GIP report.</summary>
        private static bool IsBluetoothPid(ushort pid)
            => pid == 0x02E0  // Xbox One S Bluetooth
            || pid == 0x02FD  // Xbox One S Bluetooth (alt firmware)
            || pid == 0x0B05  // Xbox Elite Series 2 Bluetooth
            || pid == 0x0B13; // Xbox Series X|S Bluetooth

        // ─────────────────────────────────────────────
        //  Public write entry
        // ─────────────────────────────────────────────

        /// <summary>Writes the four motor magnitudes to the physical Xbox
        /// One+ controller that <paramref name="ud"/> represents. Input
        /// values are PadForge's 0..65535 motor range — scaled to the
        /// 0..100 range the controller expects (per SDL3 HIDAPI's
        /// XboxOne driver: magnitude in 1..100).</summary>
        public static bool Write(
            UserDevice ud,
            ushort leftMotor16,
            ushort rightMotor16,
            ushort leftTrigger16,
            ushort rightTrigger16)
        {
            if (ud == null) return false;
            if (!XboxControllerIdentity.IsImpulseTriggerDevice(ud.VendorId, ud.ProdId))
                return false;

            string interfacePath = ResolveInterfacePath(ud);
            if (string.IsNullOrEmpty(interfacePath))
                return false;

            // SDL3 HIDAPI scales 16-bit → 0..100 via `/ 655`. Match that.
            byte lt = (byte)Math.Min(100, leftTrigger16 / 655);
            byte rt = (byte)Math.Min(100, rightTrigger16 / 655);
            byte lm = (byte)Math.Min(100, leftMotor16 / 655);
            byte rm = (byte)Math.Min(100, rightMotor16 / 655);

            bool bt = IsBluetoothPid(ud.ProdId);
            byte[] buf;
            if (bt)
            {
                buf = new byte[] { 0x03, 0x0F, lt, rt, lm, rm, 0xFF, 0x00, 0xEB };
            }
            else
            {
                // GIP protocol (Gaming Input Protocol) — used by Xbox One
                // wired controllers and Xbox Wireless Adapter dongle.
                buf = new byte[] { 0x09, 0x00, 0x00, 0x09, 0x00, 0x0F, lt, rt, lm, rm, 0xFF, 0x00, 0xEB };
            }

            return WriteRaw(interfacePath, buf);
        }

        // ─────────────────────────────────────────────
        //  HID interface enumeration
        // ─────────────────────────────────────────────

        /// <summary>Enumerates connected HID interfaces, filters to Xbox
        /// One+ controllers (by HidD_GetAttributes VID/PID, not by
        /// instance-ID string match), applies the HIDMaestro loopback
        /// filter via StableXInputInstance, and disambiguates by
        /// <c>ud.DevicePath</c>'s XInput slot index when multiple
        /// candidates exist.</summary>
        private static string ResolveInterfacePath(UserDevice ud)
        {
            // Cross-reference: get the HM-filtered list of physical
            // instance IDs for this VID/PID. Any HID interface we find
            // whose instance portion isn't in this list is rejected as
            // a possible virtual.
            IReadOnlyList<string> hmFiltered;
            try { hmFiltered = StableXInputInstance.FindAll(ud.VendorId, ud.ProdId); }
            catch { return null; }

            // Enumerate all HID interfaces and collect those matching
            // ud.VendorId / ud.ProdId via HidD_GetAttributes. This open-
            // and-query pattern is what tools/Ds4InputDump uses and is
            // robust against filter drivers that hide the device class
            // from SetupDiEnumDeviceInfo.
            var matches = new List<string>(); // interface paths
            Guid hidGuid = Guid.Empty;
            HidD_GetHidGuid(ref hidGuid);

            IntPtr devInfoSet = SetupDiGetClassDevsW(
                ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (devInfoSet == new IntPtr(-1))
                return null;

            try
            {
                var ifaceData = new SP_DEVICE_INTERFACE_DATA();
                ifaceData.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

                for (uint i = 0;
                     SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref hidGuid, i, ref ifaceData);
                     i++)
                {
                    int required = 0;
                    SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                    if (required <= 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, detail, required, ref required, IntPtr.Zero))
                            continue;

                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        // Open with access=0 — sufficient for HidD_GetAttributes
                        // and HidD_GetSerialNumberString, doesn't require
                        // elevation, and won't conflict with other
                        // consumers' exclusive locks.
                        using var probeHandle = CreateFileSafe(path, 0,
                            FILE_SHARE_READ | FILE_SHARE_WRITE, 0);
                        if (probeHandle.IsInvalid) continue;

                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(probeHandle, ref attr)) continue;

                        if (attr.VendorID != ud.VendorId || attr.ProductID != ud.ProdId)
                            continue;

                        // Extract instance ID from interface path so we
                        // can cross-check against the HM-filtered list.
                        // Interface path: \\?\HID#VID_045E&PID_0B13&...#7&abc&0&0000#{4d1e55b2-...}
                        // Instance ID:   HID\VID_045E&PID_0B13&...\7&abc&0&0000
                        string instanceId = InterfacePathToInstanceId(path);
                        bool inHmFiltered = false;
                        if (hmFiltered != null && instanceId != null)
                        {
                            foreach (var hm in hmFiltered)
                            {
                                if (string.Equals(hm, instanceId, StringComparison.OrdinalIgnoreCase))
                                {
                                    inHmFiltered = true;
                                    break;
                                }
                            }
                        }

                        if (!inHmFiltered)
                        {
                            // Either it's an HM virtual or our path→instance
                            // conversion missed something. Either way, skip
                            // it — we only write to confirmed-physical
                            // controllers that StableXInputInstance vouches
                            // for.
                            continue;
                        }

                        matches.Add(path);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            if (matches.Count == 0) return null;
            if (matches.Count == 1) return matches[0];

            // Disambiguation: SDL XInput backend's "XInput#N" path. Slot
            // index picks the candidate. Mirrors BuildInstanceGuid's
            // XInput-path branch.
            int slot = ParseXInputSlot(ud.DevicePath);
            if (slot >= 0 && slot < matches.Count)
            {
                // Sort matches deterministically so the slot-N mapping
                // matches StableXInputInstance.FindAll's sort order
                // (lexicographic on instance ID).
                matches.Sort(StringComparer.OrdinalIgnoreCase);
                return matches[slot];
            }

            // Otherwise take the first match (acknowledged: if multiple
            // controllers, this picks one — caller should pass a more
            // discriminating ud.DevicePath next time).
            matches.Sort(StringComparer.OrdinalIgnoreCase);
            return matches[0];
        }

        /// <summary>Converts a HID interface path to the device instance
        /// ID format. Interface paths look like
        /// <c>\\?\HID#VID_045E&amp;PID_0B13&amp;...#7&amp;abc&amp;0&amp;0000#{4d1e55b2-...}</c>;
        /// instance IDs look like
        /// <c>HID\VID_045E&amp;PID_0B13&amp;...\7&amp;abc&amp;0&amp;0000</c>.
        /// Conversion: strip the leading <c>\\?\</c>, drop the trailing
        /// device-class-GUID segment, replace remaining <c>#</c> with
        /// <c>\</c>. Use <c>LastIndexOf("#{")</c> — Bluetooth-paired
        /// Xbox controllers embed the BT GATT service GUID
        /// <c>{00001812-...}</c> mid-path, so the FIRST <c>#{</c> isn't
        /// the trailing class GUID.</summary>
        private static string InterfacePathToInstanceId(string interfacePath)
        {
            if (string.IsNullOrEmpty(interfacePath)) return null;
            string s = interfacePath;
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
                s = s.Substring(4);

            // Drop trailing #{class-guid} segment — must be the LAST
            // occurrence to avoid clipping mid-path BT service GUIDs.
            int hashBrace = s.LastIndexOf("#{", StringComparison.Ordinal);
            if (hashBrace >= 0) s = s.Substring(0, hashBrace);

            return s.Replace('#', '\\');
        }

        private static int ParseXInputSlot(string devicePath)
        {
            const string prefix = "XInput#";
            if (string.IsNullOrEmpty(devicePath)) return -1;
            if (!devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return -1;

            int start = prefix.Length;
            int end = start;
            while (end < devicePath.Length && devicePath[end] >= '0' && devicePath[end] <= '9')
                end++;
            if (end == start) return -1;

            return int.TryParse(devicePath.AsSpan(start, end - start), out int slot) ? slot : -1;
        }

        // ─────────────────────────────────────────────
        //  HID write (synchronous, no overlapped — matches X1nput)
        // ─────────────────────────────────────────────

        private static bool WriteRaw(string devicePath, byte[] buf)
        {
            using var handle = CreateFileSafe(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                0); // synchronous open — matches X1nput

            if (handle.IsInvalid) return false;

            return WriteFile(handle, buf, (uint)buf.Length, out _, IntPtr.Zero);
        }

        private static SafeFileHandle CreateFileSafe(
            string path, uint access, uint share, uint flags)
        {
            return CreateFileW(path, access, share, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        }

        // ─────────────────────────────────────────────
        //  P/Invoke
        // ─────────────────────────────────────────────

        private const uint GENERIC_WRITE         = 0x40000000u;
        private const uint GENERIC_READ          = 0x80000000u;
        private const uint FILE_SHARE_READ       = 0x00000001u;
        private const uint FILE_SHARE_WRITE      = 0x00000002u;
        private const uint OPEN_EXISTING         = 3u;
        private const int  DIGCF_PRESENT         = 0x00000002;
        private const int  DIGCF_DEVICEINTERFACE = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(ref Guid HidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevsW(
            ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
            ref Guid InterfaceClassGuid, uint MemberIndex,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            int DeviceInterfaceDetailDataSize,
            ref int RequiredSize,
            IntPtr DeviceInfoData);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(
            SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);
    }
}
