using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// A TouchSense mouse's WinUSB interface through winusb.dll (#494), the
    /// calls <see cref="Ds3DirectService"/> makes for a DualShock 3 on USB
    /// (Ds3DirectService.cs:586-606, 886-897 and 979), with the two it has
    /// no use for: a pipe write and a descriptor read. Nothing here decides
    /// anything. <see cref="TouchSenseChannel"/> holds the rules. The open
    /// and every call that reaches the device ask
    /// <see cref="RealTouchSenseAccess"/> first, so the test host opens no
    /// WinUSB device and sends none a transfer.
    /// </summary>
    internal sealed class WinUsbTouchSenseUsb : ITouchSenseUsb, WinUsbKeyboards.IKeyboardUsb
    {
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        private const int UsbdPipeTypeInterrupt = 3;
        private const byte USB_CONFIGURATION_DESCRIPTOR_TYPE = 0x02;
        private const int ERROR_INVALID_HANDLE = 6;
        private const int ERROR_SEM_TIMEOUT = 121;
        private static readonly IntPtr InvalidHandle = new(-1);

        private IntPtr _device;
        private IntPtr _interface;

        private WinUsbTouchSenseUsb(IntPtr device, IntPtr iface)
        {
            _device = device;
            _interface = iface;
        }

        /// <summary>Opens the device behind a WinUSB interface path, or
        /// null. Overlapped, so the read one thread waits in never holds up
        /// the control transfer of another.</summary>
        internal static WinUsbTouchSenseUsb Open(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            // The test host opens no device (RealTouchSenseAccess).
            if (RealTouchSenseAccess.Refused) return null;
            IntPtr device = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (device == InvalidHandle || device == IntPtr.Zero) return null;
            if (!WinUsb_Initialize(device, out IntPtr iface))
            {
                CloseHandle(device);
                return null;
            }
            return new WinUsbTouchSenseUsb(device, iface);
        }

        public bool Describe(out byte interfaceClass, out byte interruptIn, out byte interruptOut)
        {
            interfaceClass = interruptIn = interruptOut = 0;
            if (RealTouchSenseAccess.Refused) return false;
            if (!WinUsb_QueryInterfaceSettings(_interface, 0, out var settings)) return false;
            interfaceClass = settings.bInterfaceClass;
            for (byte i = 0; i < settings.bNumEndpoints; i++)
            {
                if (!WinUsb_QueryPipe(_interface, 0, i, out var pipe) || pipe.PipeType != UsbdPipeTypeInterrupt) continue;
                if ((pipe.PipeId & 0x80) != 0)
                {
                    if (interruptIn == 0) interruptIn = pipe.PipeId;
                }
                else if (interruptOut == 0)
                {
                    interruptOut = pipe.PipeId;
                }
            }
            return true;
        }

        /// <summary>The opened interface as a bound keyboard's open reads it
        /// (#549): <see cref="Describe"/>'s walk, with the interface's
        /// number, which a keyboard on a composite device needs for its
        /// requests, and the IN pipe's packet size, which one read asks for.</summary>
        public bool DescribeInterface(out byte number, out byte interfaceClass, out byte interruptIn, out ushort packetSize)
        {
            number = interfaceClass = interruptIn = 0;
            packetSize = 0;
            if (RealTouchSenseAccess.Refused) return false;
            if (!WinUsb_QueryInterfaceSettings(_interface, 0, out var settings)) return false;
            number = settings.bInterfaceNumber;
            interfaceClass = settings.bInterfaceClass;
            for (byte i = 0; i < settings.bNumEndpoints; i++)
            {
                if (!WinUsb_QueryPipe(_interface, 0, i, out var pipe) || pipe.PipeType != UsbdPipeTypeInterrupt) continue;
                if ((pipe.PipeId & 0x80) != 0 && interruptIn == 0)
                {
                    interruptIn = pipe.PipeId;
                    packetSize = pipe.MaximumPacketSize;
                }
            }
            return true;
        }

        public int ReadConfiguration(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return 0;
            if (RealTouchSenseAccess.Refused) return 0;
            return WinUsb_GetDescriptor(_interface, USB_CONFIGURATION_DESCRIPTOR_TYPE, 0, 0, buffer, (uint)buffer.Length, out uint read)
                ? (int)read : 0;
        }

        public bool ControlIn(byte[] setup, byte[] buffer, out int length)
        {
            length = 0;
            if (RealTouchSenseAccess.Refused) return false;
            if (!TryPacket(setup, out var packet) || buffer == null) return false;
            if (!WinUsb_ControlTransfer(_interface, packet, buffer, (uint)buffer.Length, out uint moved, IntPtr.Zero)) return false;
            length = (int)moved;
            return true;
        }

        public bool ControlOut(byte[] setup, byte[] data)
        {
            if (RealTouchSenseAccess.Refused) return false;
            if (!TryPacket(setup, out var packet)) return false;
            data ??= Array.Empty<byte>();
            return WinUsb_ControlTransfer(_interface, packet, data, (uint)data.Length, out uint moved, IntPtr.Zero)
                   && moved == (uint)data.Length;
        }

        public bool WritePipe(byte pipe, byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            if (RealTouchSenseAccess.Refused) return false;
            return WinUsb_WritePipe(_interface, pipe, data, (uint)data.Length, out uint moved, IntPtr.Zero)
                   && moved == (uint)data.Length;
        }

        public int ReadPipe(byte pipe, byte[] buffer, out int error)
        {
            error = 0;
            if (RealTouchSenseAccess.Refused)
            {
                // Read as a handle that is gone, which ends the channel.
                error = ERROR_INVALID_HANDLE;
                return -1;
            }
            if (WinUsb_ReadPipe(_interface, pipe, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero)) return (int)read;
            error = Marshal.GetLastWin32Error();
            // The pipe's own timeout: no report came, which is no fault.
            return error == ERROR_SEM_TIMEOUT ? 0 : -1;
        }

        public void SetTimeout(byte pipe, int milliseconds)
        {
            if (RealTouchSenseAccess.Refused) return;
            uint value = (uint)Math.Max(0, milliseconds);
            WinUsb_SetPipePolicy(_interface, pipe, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref value);
        }

        public void CancelIo()
        {
            IntPtr device = _device;
            if (device != IntPtr.Zero) CancelIoEx(device, IntPtr.Zero);
        }

        /// <summary>The interface, then the handle under it.</summary>
        public void Dispose()
        {
            IntPtr iface = _interface, device = _device;
            _interface = IntPtr.Zero;
            _device = IntPtr.Zero;
            if (iface != IntPtr.Zero) WinUsb_Free(iface);
            if (device != IntPtr.Zero) CloseHandle(device);
        }

        /// <summary>The eight bytes of a setup packet as WinUSB takes them.
        /// The three 16-bit fields are little-endian on the wire.</summary>
        internal static bool TryPacket(byte[] setup, out WINUSB_SETUP_PACKET packet)
        {
            packet = default;
            if (setup == null || setup.Length != 8) return false;
            packet = new WINUSB_SETUP_PACKET
            {
                RequestType = setup[0],
                Request = setup[1],
                Value = (ushort)(setup[2] | (setup[3] << 8)),
                Index = (ushort)(setup[4] | (setup[5] << 8)),
                Length = (ushort)(setup[6] | (setup[7] << 8)),
            };
            return true;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct WINUSB_SETUP_PACKET
        {
            public byte RequestType;
            public byte Request;
            public ushort Value;
            public ushort Index;
            public ushort Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints,
                bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINUSB_PIPE_INFORMATION
        {
            public int PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(IntPtr device, out IntPtr iface);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr iface);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryInterfaceSettings(IntPtr iface, byte alternate, out USB_INTERFACE_DESCRIPTOR settings);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryPipe(IntPtr iface, byte alternate, byte pipeIndex, out WINUSB_PIPE_INFORMATION pipe);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr iface, byte pipeId, uint policyType, uint valueLength, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetDescriptor(IntPtr iface, byte descriptorType, byte index, ushort languageId,
            [Out] byte[] buffer, uint bufferLength, out uint lengthTransferred);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ControlTransfer(IntPtr iface, WINUSB_SETUP_PACKET setup, [In, Out] byte[] buffer,
            uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_WritePipe(IntPtr iface, byte pipeId, [In] byte[] buffer, uint bufferLength,
            out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(IntPtr iface, byte pipeId, [Out] byte[] buffer, uint bufferLength,
            out uint lengthTransferred, IntPtr overlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
            uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);
    }
}
