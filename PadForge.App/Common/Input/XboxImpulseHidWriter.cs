using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Writes rumble and impulse-trigger output to physical Xbox One, Elite,
    /// Elite Series 2 and Xbox Series controllers, and to a pad that presents
    /// as one, such as a DualShock 3 on DsHidMini's XInput mode (045E:02FF).
    /// PadForge writes the report itself. SDL's rumble call, the XInput API,
    /// WGI and GameInput are not used for these pads.
    ///
    /// <para>The report is X1nput's: 9 bytes,
    /// <c>{ 0x03, 0x0F, LT, RT, LM, RM, 0xFF, 0x00, 0xEB }</c>, written with
    /// WriteFile to the pad's XUSB interface on every transport (X1nput.cpp,
    /// its IOCTL 0x8000a010 branch). SDL's HIDAPI driver has a 13-byte GIP
    /// shape for its own path past the Xbox driver
    /// (<c>SDL_hidapi_xboxone.c</c>), which needs Steam's driver and is not
    /// this one.</para>
    ///
    /// <para>The interface is the one OpenXInput serves the pad's XInput
    /// slot from (<see cref="XInputSlotIdentity"/>, #583), so a card's
    /// rumble goes to the pad its input comes from. OpenXInput gives no slot
    /// to a HIDMaestro virtual controller (OpenXinput.cpp,
    /// IsHidMaestroInterface), so the interface is a physical pad's. Before
    /// #583 the writer took the Nth XUSB interface in Windows' list for slot
    /// N. OpenXInput hands slots out in the order pads connect, and Windows'
    /// list has another order, so a DualShock 3 that connected after a
    /// Navigation controller sent its rumble to the Navigator. The position
    /// walk stays only for a process running Microsoft's xinput1_4.dll,
    /// which has no identity call.</para>
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
            ushort rightTrigger16,
            bool force = false)
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
            // XUSB interface (verified by X1nput). Scratch is PER-THREAD:
            // the poll thread is NOT the sole caller, see the note on
            // s_targets below.
            var buf = BuildReport(lt, rt, lm, rm);

            // Which interface serves the pad's slot now. The identity call
            // does no device I/O, so it is asked on every write (#583).
            var slot = SlotAnswer(ud);

            // Cached resolved path + persistent handle: the per-change
            // resolve ran a full SetupDi interface sweep plus probe opens,
            // and WriteRawDiag re-opened the device, at game rumble-update
            // rate on the poll thread. A handle whose slot has changed hands
            // since it was cached is dropped. So is one whose write fails,
            // and the resolve runs again for this same call.
            if (s_targets.TryGetValue(ud.DevicePath, out var cached))
            {
                if (cached.Serves(slot)
                    && cached.Handle != null && !cached.Handle.IsInvalid && !cached.Handle.IsClosed
                    && WriteFile(cached.Handle, buf, (uint)buf.Length, out _, IntPtr.Zero))
                    return true;
                cached.Handle?.Dispose();
                s_targets.TryRemove(ud.DevicePath, out _);
            }

            // A pad whose last resolve, open or write failed waits out the
            // cooldown. The poll thread re-arms a refused write every frame,
            // and each try ran the XUSB interface sweep with a probe open per
            // interface, so a pad whose interface would not open swept at
            // the poll rate. A stop or a user's test passes force and tries
            // at once.
            long now = Environment.TickCount64;
            if (!force && s_retryAt.TryGetValue(ud.DevicePath, out long retryAt) && now < retryAt)
                return false;

            string interfacePath = TargetOf(ud, slot);
            if (string.IsNullOrEmpty(interfacePath))
                return Failed(ud.DevicePath, now);

            var handle = CreateFileSafe(
                interfacePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                0); // synchronous open, matching X1nput
            if (handle.IsInvalid)
                return Failed(ud.DevicePath, now);

            bool ok = WriteFile(handle, buf, (uint)buf.Length, out _, IntPtr.Zero);
            if (ok)
            {
                s_retryAt.TryRemove(ud.DevicePath, out _);
                // TryAdd, not the indexer: if the other caller cached this
                // same device between our miss and here, its handle is as
                // good as ours, so drop ours rather than orphan it under an
                // overwritten entry. The write above already succeeded
                // either way, so the result is true regardless of who won.
                if (!s_targets.TryAdd(ud.DevicePath,
                        new CachedTarget
                        {
                            InterfacePath = interfacePath,
                            Generation = slot.Kind == XInputSlotIdentity.Kind.Found ? slot.Generation : 0,
                            Handle = handle,
                        }))
                    handle.Dispose();
                return true;
            }
            handle.Dispose();
            return Failed(ud.DevicePath, now);
        }

        /// <summary>OpenXInput's answer for the pad's slot, or
        /// <see cref="XInputSlotIdentity.Kind.Unsupported"/> for a path that
        /// is no XInput slot. A busy lock is asked again twice, a millisecond
        /// apart, since a resolve that finds nothing waits out the
        /// cooldown.</summary>
        private static XInputSlotIdentity.Answer SlotAnswer(UserDevice ud)
            => XInputSlotIdentity.TryParseSlot(ud.DevicePath, out int slot)
                ? XInputSlotIdentity.Get(slot, busyRetries: 2)
                : new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Unsupported);

        /// <summary>Where the report goes. The slot's own interface when it
        /// carries the pad's vendor and product id, and nothing when it
        /// carries another's: the slot has just changed hands, and the write
        /// waits for SDL to catch up. Nothing for a busy or an empty slot.
        /// The position walk (<see cref="Resolver"/>) only where there is no
        /// identity call.</summary>
        internal static string TargetOf(UserDevice ud, XInputSlotIdentity.Answer slot)
        {
            switch (slot.Kind)
            {
                case XInputSlotIdentity.Kind.Found:
                    return HidHideController.NamesVidPid(slot.InterfacePath, ud.VendorId, ud.ProdId)
                        ? slot.InterfacePath : null;
                case XInputSlotIdentity.Kind.Unsupported:
                    return Resolver(ud);
                default:
                    return null;
            }
        }

        /// <summary>How long a pad whose resolve, open or write failed waits
        /// before Write tries it again.</summary>
        internal const long FailedRetryCooldownMs = 1000;

        /// <summary>The XUSB interface lookup Write runs on a miss where
        /// OpenXInput has no identity call, <see cref="ResolveInterfacePath"/>.
        /// Tests replace it.</summary>
        internal static Func<UserDevice, string> Resolver = ResolveInterfacePath;

        /// <summary>Per device path, the time before which Write does not try
        /// again after a failure. Cleared by <see cref="InvalidateCachedTargets"/>,
        /// since a device change can make the interface resolvable.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> s_retryAt =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool Failed(string devicePath, long now)
        {
            s_retryAt[devicePath] = now + FailedRetryCooldownMs;
            return false;
        }

        /// <summary>Whether a failure holds <paramref name="devicePath"/> in
        /// its cooldown at <paramref name="now"/>. For tests.</summary>
        internal static bool CoolingDown(string devicePath, long now)
            => s_retryAt.TryGetValue(devicePath, out long retryAt) && now < retryAt;

        internal sealed class CachedTarget
        {
            public string InterfacePath;
            /// <summary>The slot's generation when the handle was cached, or 0
            /// where there was no identity call.</summary>
            public ulong Generation;
            public Microsoft.Win32.SafeHandles.SafeFileHandle Handle;

            /// <summary>Whether the handle still serves the slot. The same
            /// interface and generation do. A busy answer, or no identity call,
            /// keeps the handle, since nothing says otherwise. An empty slot or
            /// another interface does not.</summary>
            public bool Serves(XInputSlotIdentity.Answer slot) => slot.Kind switch
            {
                XInputSlotIdentity.Kind.Found =>
                    Generation == slot.Generation
                    && string.Equals(InterfacePath, slot.InterfacePath, StringComparison.OrdinalIgnoreCase),
                XInputSlotIdentity.Kind.Busy => true,
                XInputSlotIdentity.Kind.Unsupported => true,
                _ => false,
            };
        }
        // The poll thread is NOT the sole caller, though a comment here
        // claimed it was. The Remote Link output path (#138) reaches
        // Write from the network receive thread whenever a remote peer
        // drives an Xbox One+ pad (InputService's effect apply, the
        // impulse-trigger branch). A plain Dictionary mutated from two
        // threads with no lock can corrupt its buckets, and a corrupt
        // Dictionary sends TryGetValue into an INFINITE LOOP, which on the
        // 1 kHz poll thread is a hung input pipeline rather than a wrong
        // value. Concurrent now.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedTarget> s_targets =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Drops every cached handle and every cooldown. Called on any
        /// confirmed device disconnect, because the cache key is
        /// <c>ud.DevicePath</c>, which for these pads is SDL's slot-derived
        /// "XInput#N" string rather than a stable hardware path.
        ///
        /// <para>The lookup heals itself when a cached handle has gone bad:
        /// the write fails, the entry is dropped and the resolve runs again. A
        /// handle that is still valid on a slot another pad now holds is
        /// caught by the slot's generation (<see cref="CachedTarget.Serves"/>).
        /// Where there is no identity call, this sweep is what catches
        /// it.</para></summary>
        internal static void InvalidateCachedTargets()
        {
            foreach (var kv in s_targets)
            {
                try { kv.Value?.Handle?.Dispose(); } catch { /* best effort */ }
            }
            s_targets.Clear();
            s_retryAt.Clear();
        }

        // Same two-caller reason: per-thread, so a local write and a remote
        // write cannot interleave their motor bytes into a blend neither
        // side asked for. Keeps the allocation-free property the shared
        // buffer existed for, with no lock on the hot path.
        [ThreadStatic] private static byte[] t_reportScratch;

        /// <summary>Fills this thread's 9-byte GIP report. Bytes 0/1 and
        /// 6/7/8 are the fixed X1nput command frame, 2..5 are LT / RT /
        /// large / small. Internal so the per-thread isolation is
        /// testable.</summary>
        internal static byte[] BuildReport(byte lt, byte rt, byte lm, byte rm)
        {
            var buf = t_reportScratch ??=
                new byte[] { 0x03, 0x0F, 0, 0, 0, 0, 0xFF, 0x00, 0xEB };
            buf[2] = lt; buf[3] = rt; buf[4] = lm; buf[5] = rm;
            return buf;
        }

        // ─────────────────────────────────────────────
        //  HID interface enumeration
        // ─────────────────────────────────────────────

        /// <summary>The XUSB interface in slot N's position: the Nth interface
        /// in SetupAPI's order that opens, answers
        /// IOCTL_XINPUT_GET_INFORMATION and has no "hidmaestro" in its path,
        /// where N is the slot parsed from SDL's <c>"XInput#N"</c> path.
        ///
        /// <para>The fallback for a process with no identity call
        /// (<see cref="TargetOf"/>). The position is the slot only when every
        /// pad arrived in SetupAPI's order, since OpenXInput hands slots out
        /// in the order pads connect (OpenXinput.cpp, ProcessEnumeratedDevice),
        /// and when both lists leave out the same interfaces: OpenXInput also
        /// leaves out an interface whose node or one of the three nodes above
        /// it carries HIDMaestro's hardware id (IsHidMaestroInterface). With
        /// one pad it is right.</para></summary>
        private static string ResolveInterfacePath(UserDevice ud)
        {
            if (!XInputSlotIdentity.TryParseSlot(ud.DevicePath, out int slot))
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

        // ─────────────────────────────────────────────
        //  HID write (synchronous, no overlapped — matches X1nput)
        // ─────────────────────────────────────────────

        private static SafeFileHandle CreateFileSafe(
            string path, uint access, uint share, uint flags)
        {
            // The test host opens no device (DirectDeviceAccess): the answer
            // of a failed open.
            if (DirectDeviceAccess.Refuses(path)) return new SafeFileHandle(new IntPtr(-1), true);
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
        /// controllers (OpenXinput.cpp:506). The position fallback walks
        /// the same class to reach the device handle that
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
