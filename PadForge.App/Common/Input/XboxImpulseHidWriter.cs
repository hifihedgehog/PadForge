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

            // SDL3 HIDAPI scales 16-bit → 0..100 via `/ 655`. Match that.
            byte lt = (byte)Math.Min(100, leftTrigger16 / 655);
            byte rt = (byte)Math.Min(100, rightTrigger16 / 655);
            byte lm = (byte)Math.Min(100, leftMotor16 / 655);
            byte rm = (byte)Math.Min(100, rightMotor16 / 655);

            // X1nput's MS-driver branch writes a 9-byte report to the
            // XUSB device handle, and 9 bytes to the HID handle on
            // Bluetooth pads. SDL3 HIDAPI uses a 13-byte GIP shape for
            // its OWN bus-bypassing HIDAPI path, but that requires Steam
            // Xbox Extended Feature Driver and is not what we are doing
            // here. Stock XUSB driver accepts the 9-byte shape on the
            // XUSB interface (verified by X1nput). Scratch reuse: the poll
            // thread is the sole caller (Step 2 ApplyForceFeedback).
            var buf = s_reportScratch;
            buf[2] = lt; buf[3] = rt; buf[4] = lm; buf[5] = rm;

            // Cached resolved path + persistent handle: the per-change
            // ResolveInterfacePath ran a full SetupDi interface sweep plus
            // probe opens, and WriteRawDiag re-opened the device, at game
            // rumble-update rate on the poll thread. FALLBACK-SAFE: any
            // cached-handle write failure drops the cache and re-runs the
            // exact legacy resolve+open+write for this same call.
            if (s_targets.TryGetValue(ud.DevicePath, out var cached))
            {
                if (cached.Handle != null && !cached.Handle.IsInvalid && !cached.Handle.IsClosed
                    && WriteFile(cached.Handle, buf, (uint)buf.Length, out _, IntPtr.Zero))
                    return true;
                cached.Handle?.Dispose();
                s_targets.Remove(ud.DevicePath);
            }

            string interfacePath = ResolveInterfacePath(ud);
            if (string.IsNullOrEmpty(interfacePath))
                return false;

            var handle = CreateFileSafe(
                interfacePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                0); // synchronous open, matching X1nput
            if (handle.IsInvalid)
                return false;

            bool ok = WriteFile(handle, buf, (uint)buf.Length, out _, IntPtr.Zero);
            if (ok)
            {
                s_targets[ud.DevicePath] = new CachedTarget { InterfacePath = interfacePath, Handle = handle };
                return true;
            }
            handle.Dispose();
            return false;
        }

        private sealed class CachedTarget
        {
            public string InterfacePath;
            public Microsoft.Win32.SafeHandles.SafeFileHandle Handle;
        }
        // Poll thread is the sole caller, so a plain dictionary suffices.
        private static readonly System.Collections.Generic.Dictionary<string, CachedTarget> s_targets =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly byte[] s_reportScratch =
            { 0x03, 0x0F, 0, 0, 0, 0, 0xFF, 0x00, 0xEB };

        // ─────────────────────────────────────────────
        //  HID interface enumeration
        // ─────────────────────────────────────────────

        /// <summary>Resolves the XUSB device interface path PadForge
        /// CreateFile+WriteFiles to send the 9-byte rumble report.
        ///
        /// <para>Pairing logic, matching OpenXInput's
        /// <c>EnumerateXInputDevices</c> at <c>OpenXinput.cpp:1057</c>:
        /// enumerate <see cref="XUSB_INTERFACE_CLASS_GUID"/> in
        /// SetupAPI's natural order, skip HIDMaestro virtual XInput
        /// devices by substring on the interface path, take the Nth
        /// surviving interface where N is the slot parsed from SDL's
        /// <c>"XInput#N"</c> device path. SDL's XInput backend
        /// inherits this same OpenXInput enumeration order through our
        /// embedded <c>xinput1_4.dll</c>, so position N in the
        /// HM-filtered list is the same physical controller SDL sees
        /// at <c>XInput#N</c>.</para>
        ///
        /// <para>Verifies via P/Invoke into <c>OpenXInputGetDeviceUSBIds</c>
        /// (an added export in PadForge's OpenXInput fork — header at
        /// <c>OpenXinput.h:439</c>) that the slot OpenXInput reports
        /// has the same VID/PID as PadForge expects. Mismatch logs a
        /// warning but doesn't abort — VID/PID is identical for
        /// same-model duplicates anyway, so this is a sanity check, not
        /// a discriminator.</para></summary>
        private static string ResolveInterfacePath(UserDevice ud)
        {
            int slot = ParseXInputSlot(ud.DevicePath);
            if (slot < 0)
                return null;

            Guid classGuid = XUSB_INTERFACE_CLASS_GUID;
            string matchedPath = null;

            IntPtr devInfoSet = SetupDiGetClassDevsW(
                ref classGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (devInfoSet == new IntPtr(-1))
                return null;

            int survivingIdx = 0;

            try
            {
                var ifaceData = new SP_DEVICE_INTERFACE_DATA();
                ifaceData.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

                for (uint i = 0;
                     SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref classGuid, i, ref ifaceData);
                     i++)
                {
                    int required = 0;
                    SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                    if (required <= 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        var devInfo = new SP_DEVINFO_DATA();
                        devInfo.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
                        if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, detail, required, ref required, ref devInfo))
                            continue;

                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        // Open the XUSB interface with the same flags
                        // OpenXInput uses (OpenXinput.cpp:2887).
                        // GENERIC_READ|GENERIC_WRITE so we can issue
                        // IOCTL_XINPUT_GET_INFORMATION (and later the
                        // WriteFile for rumble).
                        // FILE_SHARE_READ|FILE_SHARE_WRITE lets us share
                        // with xinput1_4.dll if a game has the same
                        // device open.
                        using var probeHandle = CreateFileSafe(path,
                            GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_READ | FILE_SHARE_WRITE, 0);
                        if (probeHandle.IsInvalid)
                            continue;

                        // HM virtual filter — substring on the path,
                        // case-insensitive. Matches the cheap fast-path
                        // OpenXInput uses to skip HIDMaestro virtual
                        // XInput devices during its enumeration.
                        if (path.IndexOf("hidmaestro", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        // XUSB device's VID/PID via IOCTL. Used as a
                        // sanity check that this slot is actually the
                        // VID/PID PadForge expects.
                        if (!QueryXusbDeviceInfo(probeHandle, out _, out _))
                            continue;

                        if (survivingIdx == slot)
                        {
                            matchedPath = path;
                            break;
                        }

                        survivingIdx++;
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

            return matchedPath;
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

        private static (bool ok, int err) WriteRawDiag(string devicePath, byte[] buf)
        {
            using var handle = CreateFileSafe(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                0); // synchronous open — matches X1nput

            if (handle.IsInvalid)
                return (false, Marshal.GetLastWin32Error());

            bool ok = WriteFile(handle, buf, (uint)buf.Length, out _, IntPtr.Zero);
            return (ok, ok ? 0 : Marshal.GetLastWin32Error());
        }

        private static SafeFileHandle CreateFileSafe(
            string path, uint access, uint share, uint flags)
        {
            return CreateFileW(path, access, share, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        }

        /// <summary>Queries an XUSB-interface device handle for its
        /// controller VID/PID via IOCTL_XINPUT_GET_INFORMATION
        /// (0x80006000). Matches OpenXinput.cpp's
        /// <c>GetDeviceInfoFromInterface</c>. The output buffer layout
        /// is <c>OutDeviceInfos_t</c>: WORD XUSBVersion, BYTE
        /// deviceIndex, 3 BYTE unk, WORD unk, WORD vendorId, WORD
        /// productId.</summary>
        private static bool QueryXusbDeviceInfo(SafeFileHandle handle, out ushort vid, out ushort pid)
        {
            vid = 0;
            pid = 0;
            byte[] outBuf = new byte[13]; // sizeof(OutDeviceInfos_t)

            bool ok = DeviceIoControl(
                handle,
                IOCTL_XINPUT_GET_INFORMATION,
                IntPtr.Zero, 0,
                outBuf, (uint)outBuf.Length,
                out _, IntPtr.Zero);
            if (!ok) return false;

            // OutDeviceInfos_t layout (#pragma pack(1) — OpenXinput.cpp:176-186):
            //   WORD XUSBVersion;   // 0-1
            //   BYTE deviceIndex;   // 2
            //   BYTE unk1,unk2,unk3;// 3,4,5
            //   WORD unk4;          // 6-7
            //   WORD vendorId;      // 8-9
            //   WORD productId;     // 10-11
            vid = (ushort)(outBuf[8] | (outBuf[9] << 8));
            pid = (ushort)(outBuf[10] | (outBuf[11] << 8));
            return true;
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

        /// <summary>XUSB driver's device interface class GUID, used by
        /// <c>xinput1_4.dll</c> + OpenXInput to enumerate Xbox
        /// controllers (OpenXinput.cpp:506). PadForge uses the same
        /// enumeration to reach the device handle that
        /// <c>IOCTL_XINPUT_SET_GAMEPAD_STATE</c> hits and that X1nput's
        /// 9-byte WriteFile lands on for impulse-trigger rumble.</summary>
        private static readonly Guid XUSB_INTERFACE_CLASS_GUID =
            new Guid(0xEC87F1E3, 0xC13B, 0x4100, 0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB);

        /// <summary>CTL_CODE(0x8000, 0x800, METHOD_BUFFERED,
        /// FILE_READ_ACCESS) per OpenXinput.cpp:510. Returns
        /// OutDeviceInfos_t.</summary>
        private const uint IOCTL_XINPUT_GET_INFORMATION = 0x80006000;

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

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            int DeviceInterfaceDetailDataSize,
            ref int RequiredSize,
            IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            int DeviceInterfaceDetailDataSize,
            ref int RequiredSize,
            ref SP_DEVINFO_DATA DeviceInfoData);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);
    }
}
