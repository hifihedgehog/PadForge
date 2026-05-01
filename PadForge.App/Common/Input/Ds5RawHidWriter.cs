using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Raw HID writer for DualSense effect packets. Bypasses SDL3 entirely
    /// — opens the device by path via Win32 CreateFile and writes directly
    /// with WriteFile. Mirrors what OpenRGB's SonyDualSenseController.cpp
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
    /// <para>Open + write + close per call. ~1ms overhead per write,
    /// acceptable for slider-drag cadence. Avoids handle staleness on
    /// device disconnect — if the device path is gone, CreateFile fails
    /// cleanly and we report failure rather than writing into a dead
    /// handle.</para>
    /// </summary>
    internal static class Ds5RawHidWriter
    {
        private const uint GENERIC_WRITE     = 0x40000000u;
        private const uint FILE_SHARE_READ   = 0x00000001u;
        private const uint FILE_SHARE_WRITE  = 0x00000002u;
        private const uint OPEN_EXISTING     = 3u;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            IntPtr hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>USB DualSense effect report — 1 byte report ID (0x02)
        /// + 47 byte payload = 48 bytes total. Constant matches
        /// OpenRGB's SONY_DUALSENSE_USB_PACKET_SIZE.</summary>
        public const int UsbPacketSize = 48;

        /// <summary>Bluetooth DualSense effect report — 1 byte report ID
        /// (0x31) + 1 byte tag/sequence + 47 byte payload + 24 bytes
        /// reserved + 4 bytes CRC32 = 78 bytes total. Constant matches
        /// OpenRGB's SONY_DUALSENSE_BT_PACKET_SIZE.</summary>
        public const int BluetoothPacketSize = 78;

        /// <summary>Heuristic for USB vs Bluetooth from a HID device path.
        /// USB: <c>\\?\HID#VID_054C&amp;PID_0CE6&amp;...</c>.
        /// Bluetooth: <c>\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&amp;0002054c_PID&amp;0ce6...</c>.
        /// The BT GATT HID-over-BT service UUID <c>0x1124</c> appears in
        /// every BT-paired HID's path; USB paths use the unbracketed
        /// <c>VID_</c>/<c>PID_</c> form.</summary>
        public static bool IsBluetoothPath(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            // Lowercase for case-insensitive contains.
            return devicePath.IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("BTHENUM",   StringComparison.OrdinalIgnoreCase) >= 0
                || devicePath.IndexOf("_VID&",     StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Writes the 47-byte effect payload to a USB DualSense
        /// at <paramref name="devicePath"/>. Returns true on success.
        /// Caller is responsible for filling <paramref name="payload47"/>
        /// per the standard 47-byte effect layout (validFlag0/validFlag1/
        /// rumble/audio/triggers/lightbarSetup/ledBrightness/
        /// playerIndicator/RGB).</summary>
        public static bool WriteUsb(string devicePath, ReadOnlySpan<byte> payload47)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (payload47.Length != Ds5EffectSynthesizer.PayloadSize) return false;

            var buf = new byte[UsbPacketSize];
            buf[0] = 0x02;                    // report ID
            payload47.CopyTo(buf.AsSpan(1));  // 47-byte payload follows

            return WriteRaw(devicePath, buf);
        }

        // BT sequence counter — incremented per BT write. The DS5 firmware
        // uses the upper nibble as a sequence counter; OpenRGB hardcodes
        // 0x02 for the whole byte but that breaks if firmware enforces
        // monotonic sequencing. Increment 0x10 per call (low nibble stays
        // 0x0); rolls over after 16 writes. Static across all BT DS5s
        // since Windows serializes writes anyway and no DS5 distinguishes
        // sequences across paired devices in a way we can observe.
        private static int _btSeq = 0;
        private static readonly object _btSeqLock = new();

        /// <summary>Writes the 47-byte effect payload to a Bluetooth
        /// DualSense at <paramref name="devicePath"/>. Wraps the payload
        /// in the 78-byte BT envelope (report ID 0x31 + tag + payload +
        /// reserved + CRC32). Returns true on success.</summary>
        public static bool WriteBluetooth(string devicePath, ReadOnlySpan<byte> payload47)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (payload47.Length != Ds5EffectSynthesizer.PayloadSize) return false;

            // CRC seed prefix (0xA2) + 78-byte sent buffer.
            // Wire format (matches OpenRGB SonyDualSenseController.cpp):
            //   [0]    0x31 — report ID
            //   [1]    tag/sequence (high nibble)
            //   [2..48] 47-byte effect payload
            //   [49..73] reserved (zeros)
            //   [74..77] CRC32(0xA2 + bytes [0..73])
            byte seq;
            lock (_btSeqLock)
            {
                seq = (byte)((_btSeq & 0x0F) << 4);
                _btSeq = (_btSeq + 1) & 0xFF;
            }

            // 79-byte work buffer: [0]=0xA2 (for CRC, stripped), [1..78]=sent bytes.
            var work = new byte[BluetoothPacketSize + 1];
            work[0] = 0xA2;
            work[1] = 0x31;
            work[2] = seq;
            payload47.CopyTo(work.AsSpan(3, 47));

            // CRC32 over [0..73] inclusive of the 0xA2 prefix → 75 bytes.
            uint crc = Crc32(work, 0, BluetoothPacketSize - 3);

            // Build the 78-byte sent buffer: drop the 0xA2 prefix.
            var sent = new byte[BluetoothPacketSize];
            Array.Copy(work, 1, sent, 0, BluetoothPacketSize - 4);
            sent[BluetoothPacketSize - 4] = (byte)(crc & 0xFF);
            sent[BluetoothPacketSize - 3] = (byte)((crc >> 8) & 0xFF);
            sent[BluetoothPacketSize - 2] = (byte)((crc >> 16) & 0xFF);
            sent[BluetoothPacketSize - 1] = (byte)((crc >> 24) & 0xFF);

            return WriteRaw(devicePath, sent);
        }

        /// <summary>Convenience: dispatches USB or BT write based on the
        /// device path. Returns true on success.</summary>
        public static bool Write(string devicePath, ReadOnlySpan<byte> payload47)
        {
            return IsBluetoothPath(devicePath)
                ? WriteBluetooth(devicePath, payload47)
                : WriteUsb(devicePath, payload47);
        }

        // Standard CRC32 (poly 0xEDB88320, init 0xFFFFFFFF, final XOR
        // 0xFFFFFFFF, reflected) — same algorithm hidapi/CRCpp use, which
        // is what OpenRGB calls for the DS5 BT effect packet checksum.
        private static readonly uint[] _crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }

        private static uint Crc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
                crc = _crc32Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static bool WriteRaw(string devicePath, byte[] buf)
        {
            IntPtr handle = CreateFileW(
                devicePath,
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
                return false;

            try
            {
                return WriteFile(handle, buf, (uint)buf.Length, out uint _, IntPtr.Zero);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
