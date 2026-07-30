using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HIDMaestro;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Writes Sony effect packets (DualSense / DualShock 4) directly to a
    /// physical HID device. Pairs the v1.3.5 <see cref="HMOutputEncoder"/>
    /// data-driven encoder with a raw Win32 WriteFile path that bypasses
    /// SDL3 entirely. Mirrors what OpenRGB's SonyDualSenseController.cpp
    /// does via hidapi.
    ///
    /// <para>Why bypass SDL? SDL3's PS5 driver runs an internal state
    /// machine that fires its own UpdateEffects packets (player-index
    /// default color on SetDevicePlayerIndex, BT LED reset at ~10.2s
    /// post-connect, etc.). These race against SDL_SendGamepadEffect
    /// calls and can override user-supplied colors after a hot-plug or
    /// reconnect. Raw HID writes through a separate file handle cut SDL
    /// out of the loop — the firmware applies whichever WriteFile lands
    /// most recently, regardless of which process opened the handle.</para>
    ///
    /// <para>Open + write + close per call. ~1 ms overhead per write,
    /// acceptable for slider-drag cadence. Avoids handle staleness on
    /// device disconnect — if the device path is gone, CreateFile fails
    /// cleanly and we report failure rather than writing into a dead
    /// handle.</para>
    /// </summary>
    internal static class PlayStationEffectWriter
    {
        private const uint GENERIC_WRITE         = 0x40000000u;
        private const uint GENERIC_READ          = 0x80000000u;
        private const uint FILE_SHARE_READ       = 0x00000001u;
        private const uint FILE_SHARE_WRITE      = 0x00000002u;
        private const uint OPEN_EXISTING         = 3u;
        private const uint FILE_FLAG_OVERLAPPED  = 0x40000000u;
        private const uint WAIT_OBJECT_0         = 0u;
        private const int  ERROR_IO_PENDING      = 997;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [StructLayout(LayoutKind.Sequential)]
        private struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint OffsetLow;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

        // IntPtr buffer, not byte[]: the marshaler's automatic pin on a byte[]
        // parameter ends when WriteFile returns ERROR_IO_PENDING, leaving the
        // kernel reading a movable managed array for the whole pending window.
        // The caller pins for the full I/O lifetime (the house pattern in
        // HapticToneService.OverlappedWrite and both BtWritePools).
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToWrite,
            IntPtr lpNumberOfBytesWritten,
            ref OVERLAPPED lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(
            IntPtr hFile,
            ref OVERLAPPED lpOverlapped,
            out uint lpNumberOfBytesTransferred,
            bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>Heuristic for USB vs Bluetooth from a HID device path.
        /// USB: <c>\\?\HID#VID_054C&amp;PID_0CE6&amp;...</c>.
        /// Bluetooth: <c>\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&amp;0002054c_PID&amp;0ce6...</c>.
        /// The BT GATT HID-over-BT service UUID <c>0x1124</c> appears in
        /// every BT-paired HID's path; USB paths use the unbracketed
        /// <c>VID_</c>/<c>PID_</c> form.</summary>
        public static bool IsBluetoothPath(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            return devicePath.IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("BTHENUM",   StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("_VID&",     StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Encodes <paramref name="fields"/> through
        /// <paramref name="profile"/>'s <c>extendedOutputReport</c> spec
        /// and writes the resulting bytes to the device at
        /// <paramref name="devicePath"/>. Returns true on success.
        /// CRC32 footers (BT) are computed by the encoder; the caller
        /// supplies semantic fields, never byte offsets.</summary>
        public static bool Write(
            string devicePath,
            HMProfile profile,
            IReadOnlyDictionary<string, object> fields)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (profile == null || fields == null) return false;
            if (!profile.HasExtendedOutput) return false;

            byte[] packet;
            try
            {
                packet = HMOutputEncoder.Encode(profile, fields);
            }
            catch
            {
                return false;
            }

            ApplyAudioControl2(packet, fields);

            // Reverse output relay (#138): a "peer://" device lives on another PC. The
            // dispatcher has already baked the full config (rumble + adaptive triggers +
            // lightbar + mic/player LED + audio-control) into this USB-shape packet, so
            // ship the report body (report-id stripped) to the owner, which replays it
            // via SDL_SendGamepadEffect and re-frames for its own transport (USB/BT).
            if (RemoteLinkOutputRouter.IsPeerPath(devicePath))
            {
                // 0x02 = DualSense USB output report, 0x05 = DualShock 4 USB output report.
                // A peer:// path is never classified Bluetooth, so these are the only two
                // ids the consumer encoder produces here — accept both or DS4 output is
                // silently dropped on the owner (#138 F29).
                if (packet.Length >= 2 && (packet[0] == 0x02 || packet[0] == 0x05))
                    RemoteLinkOutputRouter.ShipSonyEffect(devicePath, packet.AsSpan(1));
                return true; // handled remotely; no local write
            }

            // Sole-writer guard (#138): a remote game holds the output lease on this LOCAL
            // shared DualSense/DS4 — skip the local write so the inbound relay is the sole
            // writer. Report success so the pipeline doesn't treat the skip as a failure.
            if (RemoteLinkOutputRouter.IsClaimedByPeer(devicePath)) return true;

            return WriteRaw(devicePath, packet);
        }

        /// <summary>The DS5 audio_flags2 byte (speaker pre-gain in bits 0-2,
        /// gated by valid_flag1 bit 7) isn't declared by the HM profiles, so
        /// the encoder can't place it; poke it post-encode. Offsets per
        /// dualsensectl's packed output struct (common+37): USB report 0x02
        /// byte 38, BT report 0x31 byte 40 — after which the BT CRC32 footer
        /// (over {0xA2} + bytes [0..73], LE at [74..77]) must be redone.</summary>
        private static void ApplyAudioControl2(byte[] packet, IReadOnlyDictionary<string, object> fields)
        {
            if (!fields.TryGetValue("audioControl2", out var raw) || raw is not byte value) return;

            if (packet[0] == 0x02 && packet.Length >= 48)
            {
                packet[38] = value;
            }
            else if (packet[0] == 0x31 && packet.Length >= 78)
            {
                packet[40] = value;
                uint crc = 0xFFFFFFFFu;
                crc ^= 0xA2;
                for (int b = 0; b < 8; b++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(crc & 1)));
                for (int i = 0; i <= 73; i++)
                {
                    crc ^= packet[i];
                    for (int b = 0; b < 8; b++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(crc & 1)));
                }
                crc = ~crc;
                packet[74] = (byte)(crc & 0xFF);
                packet[75] = (byte)((crc >> 8) & 0xFF);
                packet[76] = (byte)((crc >> 16) & 0xFF);
                packet[77] = (byte)((crc >> 24) & 0xFF);
            }
        }

        private static bool WriteRaw(string devicePath, byte[] buf)
        {

            // Open-per-write, the pre-2026-07-20 shape, RESTORED after a
            // field regression: a held-open Bluetooth HID handle made
            // DualSense rumble discontinuous (regression confirmed on
            // hardware the same day the per-path cache shipped; the
            // radio's link management does not keep a parked handle
            // write-ready the way a freshly opened one is). hidapi (which
            // OpenRGB uses) opens with FILE_FLAG_OVERLAPPED,
            // GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ |
            // FILE_SHARE_WRITE per write. Match that exactly. Do NOT
            // re-cache this handle without a Bluetooth hardware pass.
            IntPtr handle = CreateFileW(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE) return false;

            try
            {
                IntPtr ev = CreateEventW(IntPtr.Zero, true, false, null);
                if (ev == IntPtr.Zero) return false;
                try
                {
                    return WriteOnHandle(handle, ev, buf);
                }
                finally { CloseHandle(ev); }
            }
            finally { CloseHandle(handle); }
        }

        private static bool WriteOnHandle(IntPtr handle, IntPtr ev, byte[] buf)
        {
            // Manual-reset event: clear before reuse.
            ResetEvent(ev);

            // Pin held until every path below has passed its unbounded
            // GetOverlappedResult (or established no I/O is pending), so
            // the kernel never reads a moved buffer mid-write.
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                var ol = new OVERLAPPED { hEvent = ev };
                bool ok = WriteFile(handle, pin.AddrOfPinnedObject(), (uint)buf.Length, IntPtr.Zero, ref ol);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) return false;
                    if (WaitForSingleObject(ev, 1000) != WAIT_OBJECT_0)
                    {
                        // Timed out. CancelIo only REQUESTS abort; the write can
                        // still be in flight, and `ol` is a stack local while `buf`
                        // is pinned only until the finally below. Block until
                        // the cancelled I/O actually completes before unwinding, or
                        // the kernel writes completion status into freed stack
                        // memory / reads an unpinned buffer (the sibling drain in
                        // HapticToneService.OverlappedWrite exists for this reason).
                        CancelIo(handle);
                        GetOverlappedResult(handle, ref ol, out _, true);
                        return false;
                    }
                }
                return GetOverlappedResult(handle, ref ol, out _, true);
            }
            finally
            {
                pin.Free();
            }
        }
    }
}
