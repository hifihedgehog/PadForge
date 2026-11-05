using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>The ASUS ATK ACPI device a TUF laptop's keyboard lighting is
    /// set through (#557).</summary>
    internal interface IAtkAcpiChannel : IDisposable
    {
        /// <summary>One method call: <paramref name="method"/> and the
        /// argument bytes, as OpenRGB's deviceIoControlWrapper sends them
        /// (AsusTUFLaptopController_Windows.cpp:129-161). Answers the first
        /// four bytes of the answer as a little-endian number, as
        /// deviceControl reads it (lines 199-208), or null when the call
        /// failed.</summary>
        int? Call(uint method, byte[] arguments);
    }

    /// <summary>The HP BIOS WMI interface an Omen laptop's keyboard lighting
    /// is set through (#557).</summary>
    internal interface IHpBiosChannel : IDisposable
    {
        /// <summary>One call of hpqBIntM.hpqBIOSInt128, as OpenRGB's execute
        /// makes it (HPOmenLaptopController_Windows.cpp:57-256). Answers the
        /// 128 data bytes of the answer, or null when the call failed.</summary>
        byte[] Call(int command, int commandType, byte[] input);
    }

    /// <summary>A USB serial port a keyboard's firmware speaks a line
    /// protocol on (#557).</summary>
    internal interface ISerialLineChannel : IDisposable
    {
        /// <summary>Writes every byte. False when the write failed or wrote
        /// fewer.</summary>
        bool Write(byte[] bytes);

        /// <summary>Reads what has come in, waiting at most the port's read
        /// timeouts. The count read, 0 when nothing came, or -1 when the
        /// read failed.</summary>
        int Read(byte[] buffer);
    }

    /// <summary>
    /// The machine side of the shared worker's families whose devices are
    /// no HID collections (#557): the ASUS ATK ACPI device, the HP BIOS WMI
    /// method and USB serial ports, and the Lenovo GameZone WMI method a
    /// 4-zone keyboard's hand-back calls. Every member that opens a device,
    /// calls a WMI method or writes asks <see cref="OffLimitsForTest"/>
    /// first, so the test host reaches none of them (OwnLightingOffLimits
    /// in PadForge.Tests). The reads the looks make, a WMI query of the
    /// PC's product name, the class of the HP BIOS interface and the
    /// container of a device instance, change nothing and are not fenced.
    /// </summary>
    internal static class OwnLightingChannels
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app.</summary>
        internal static bool OffLimitsForTest;

        // ─────────────────────────────────────────────
        //  ASUS ATK ACPI
        // ─────────────────────────────────────────────

        /// <summary>The device OpenRGB opens
        /// (AsusTUFLaptopController_Windows.cpp:81, and G-Helper's
        /// AsusACPI.cs:32).</summary>
        internal const string AtkAcpiPath = @"\\.\ATKACPI";

        /// <summary>The IOCTL every call goes through
        /// (AsusTUFLaptopController_Windows.cpp:143, AsusACPI.cs:33).</summary>
        internal const uint AtkIoctl = 0x0022240C;

        /// <summary>The device instance of the ASUS WMI interface OpenRGB
        /// requires before it opens the device (checkWMIType's type 2,
        /// AsusTUFLaptopController_Windows.cpp:63 and 97).</summary>
        internal const string AtkWmiInstance = @"ACPI\PNP0C14\ATK";

        /// <summary>The PC's product name, from Win32_ComputerSystemProduct
        /// as OpenRGB's detector reads it (AsusTUFLaptopDetect_Windows.cpp:26-33),
        /// or null when the query gave no single answer. A read.</summary>
        internal static string ProductName()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_ComputerSystemProduct");
                using var results = searcher.Get();
                string name = null;
                int count = 0;
                foreach (ManagementBaseObject row in results)
                {
                    using (row)
                    {
                        count++;
                        name = row["Name"]?.ToString();
                    }
                }
                return count == 1 ? name : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether a present device has this instance ID. A
        /// read.</summary>
        internal static bool InstancePresent(string instanceId)
            => !string.IsNullOrEmpty(instanceId) && CM_Locate_DevNodeW(out _, instanceId, 0) == 0;

        /// <summary>Opens the ATK ACPI device for read and write, as OpenRGB
        /// does (CreateFileW with 0xC0000000, share mode 3 and
        /// OPEN_EXISTING, AsusTUFLaptopController_Windows.cpp:81). Null when
        /// it did not open.</summary>
        internal static IAtkAcpiChannel OpenAtkAcpi()
        {
            if (OffLimitsForTest) return null;
            var handle = CreateFileW(AtkAcpiPath, GenericReadWrite, 3, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
            return new AtkAcpiChannel(handle);
        }

        private sealed class AtkAcpiChannel : IAtkAcpiChannel
        {
            private readonly SafeFileHandle _handle;

            public AtkAcpiChannel(SafeFileHandle handle) => _handle = handle;

            public int? Call(uint method, byte[] arguments)
            {
                arguments ??= Array.Empty<byte>();
                // The method, the argument length, then the arguments, into
                // an answer buffer of 1024 bytes
                // (AsusTUFLaptopController_Windows.cpp:131-149).
                var input = new byte[8 + arguments.Length];
                BitConverter.TryWriteBytes(input.AsSpan(0, 4), method);
                BitConverter.TryWriteBytes(input.AsSpan(4, 4), (uint)arguments.Length);
                arguments.CopyTo(input, 8);
                var output = new byte[1024];
                if (!DeviceIoControl(_handle, AtkIoctl, input, input.Length, output, output.Length, out _, IntPtr.Zero))
                    return null;
                return BitConverter.ToInt32(output, 0);
            }

            public void Dispose() => _handle.Dispose();
        }

        // ─────────────────────────────────────────────
        //  HP BIOS WMI
        // ─────────────────────────────────────────────

        internal const string HpNamespace = @"ROOT\WMI";
        internal const string HpMethodClass = "hpqBIntM";
        internal const string HpDataClass = "hpqBDataIn";
        internal const string HpMethod = "hpqBIOSInt128";

        /// <summary>The instance OpenRGB calls the method on
        /// (HPOmenLaptopController_Windows.cpp:211).</summary>
        internal const string HpInstance = @"hpqBIntM.InstanceName='ACPI\PNP0C14\0_0'";

        /// <summary>The shared secret every call carries, "SECU"
        /// (HPOmenLaptopController_Windows.cpp:63, OmenMon
        /// Hardware/BiosData.cs:17-19).</summary>
        internal static readonly byte[] HpSign = { 83, 69, 67, 85 };

        /// <summary>Whether the PC's WMI has HP's BIOS method class. A read
        /// of the class's definition, which calls nothing.</summary>
        internal static bool HpBiosClassPresent()
        {
            try
            {
                using var cls = new ManagementClass(HpNamespace, HpMethodClass, null);
                cls.Get();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A channel to the HP BIOS method. Null in the test
        /// host.</summary>
        internal static IHpBiosChannel OpenHpBios()
        {
            if (OffLimitsForTest) return null;
            return new HpBiosChannel();
        }

        private sealed class HpBiosChannel : IHpBiosChannel
        {
            public byte[] Call(int command, int commandType, byte[] input)
            {
                if (OffLimitsForTest) return null;
                input ??= Array.Empty<byte>();
                try
                {
                    using var dataClass = new ManagementClass(HpNamespace, HpDataClass, null);
                    using var data = dataClass.CreateInstance();
                    data["Sign"] = HpSign;
                    data["Command"] = command;
                    data["CommandType"] = commandType;
                    data["Size"] = input.Length;
                    data["hpqBData"] = input;
                    using var method = new ManagementObject(HpNamespace, HpInstance, null);
                    using var parameters = method.GetMethodParameters(HpMethod);
                    parameters["InData"] = data;
                    using var result = method.InvokeMethod(HpMethod, parameters, null);
                    // OutData of hpqBDataOut128, whose Data holds the answer
                    // (HPOmenLaptopController_Windows.cpp:221-251).
                    if (result?["OutData"] is not ManagementBaseObject outData) return null;
                    using (outData)
                        return outData["Data"] as byte[];
                }
                catch
                {
                    return null;
                }
            }

            public void Dispose() { }
        }

        /// <summary>The crash path's one call to the HP BIOS method, bounded.
        /// True when it went out.</summary>
        internal static bool HpBiosCallOnce(int command, int commandType, byte[] input, int timeoutMs)
        {
            if (OffLimitsForTest) return false;
            return HidReportChannel.RunBounded(() =>
            {
                using var bios = new HpBiosChannel();
                return bios.Call(command, commandType, input) != null;
            }, timeoutMs);
        }

        // ─────────────────────────────────────────────
        //  Lenovo GameZone WMI
        // ─────────────────────────────────────────────

        internal const string LenovoNamespace = @"ROOT\WMI";
        internal const string LenovoGameZoneQuery = "SELECT * FROM LENOVO_GAMEZONE_DATA";
        internal const string LenovoLightOwnerMethod = "SetLightControlOwner";

        /// <summary>LENOVO_GAMEZONE_DATA.SetLightControlOwner with
        /// <paramref name="owner"/> as its Data, on the first instance the
        /// query finds, as Lenovo Legion Toolkit calls it
        /// (WMI.LenovoGameZoneData.cs:171-174, CallAsync in WMI.cs:58-78).
        /// 0 gives a 4-zone keyboard back to the firmware. Bounded by
        /// <paramref name="timeoutMs"/>, since a WMI call can stall the
        /// worker. True when the call went through in time.</summary>
        internal static bool SetLenovoLightControlOwner(int owner, int timeoutMs)
        {
            if (OffLimitsForTest) return false;
            return HidReportChannel.RunBounded(() =>
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(LenovoNamespace, LenovoGameZoneQuery);
                    using var results = searcher.Get();
                    foreach (ManagementBaseObject row in results)
                    {
                        using (row)
                        {
                            if (row is not ManagementObject zone) continue;
                            using var parameters = zone.GetMethodParameters(LenovoLightOwnerMethod);
                            parameters["Data"] = owner;
                            using var result = zone.InvokeMethod(LenovoLightOwnerMethod, parameters, null);
                            return true;
                        }
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }, timeoutMs);
        }

        // ─────────────────────────────────────────────
        //  USB serial ports
        // ─────────────────────────────────────────────

        /// <summary>The container a device instance sits in, or Guid.Empty.
        /// A read.</summary>
        internal static Guid ContainerOfInstance(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId) || CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0)
                return Guid.Empty;
            var key = new DEVPROPKEY { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 };
            var buffer = new byte[16];
            uint size = 16;
            return CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0) == 0 && size == 16
                ? new Guid(buffer)
                : Guid.Empty;
        }

        private const int ErrorAccessDenied = 5;

        /// <summary>The last <see cref="OpenSerial"/> on this thread found the
        /// port taken: it opens for one program at a time, and Windows answers
        /// any other with access denied.</summary>
        [ThreadStatic] internal static bool SerialDenied;

        /// <summary>Opens a serial port as OpenRGB's serial_port opens it
        /// (serial_port.cpp:197-318): read and write, not shared, the baud
        /// rate given, 8 data bits, no parity, one stop bit, no flow
        /// control, DTR off, and its write timeouts, with the read timeouts of
        /// <see cref="SerialReadTotalMs"/>. <paramref name="path"/> is the port
        /// as \\.\COMn. Null when it did not open.</summary>
        internal static ISerialLineChannel OpenSerial(string path, int baud)
        {
            if (OffLimitsForTest) return null;
            SerialDenied = false;
            var handle = CreateFileW(path, GenericReadWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                SerialDenied = Marshal.GetLastWin32Error() == ErrorAccessDenied;
                handle.Dispose();
                return null;
            }
            SetupComm(handle, 1, 128);
            var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
            GetCommState(handle, ref dcb);
            dcb.BaudRate = (uint)baud;
            dcb.Parity = 0;
            dcb.ByteSize = 8;
            dcb.StopBits = 0;
            // fOutxCtsFlow, fOutxDsrFlow, fDtrControl, fOutX, fInX,
            // fRtsControl and fAbortOnError all off (serial_port.cpp:284-301).
            dcb.Flags &= ~(DcbOutxCtsFlow | DcbOutxDsrFlow | DcbDtrControl | DcbOutX | DcbInX | DcbRtsControl | DcbAbortOnError);
            if (!SetCommState(handle, ref dcb))
            {
                handle.Dispose();
                return null;
            }
            var timeouts = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = 50, ReadTotalTimeoutConstant = SerialReadTotalMs, ReadTotalTimeoutMultiplier = SerialReadPerByteMs,
                WriteTotalTimeoutConstant = 50, WriteTotalTimeoutMultiplier = 10,
            };
            SetCommTimeouts(handle, ref timeouts);
            return new SerialLineChannel(handle);
        }

        /// <summary>How long a read waits on a port that sends nothing: this
        /// much in all, and this much for each byte asked for. OpenRGB's Windows
        /// build asks 50 ms and 10 ms a byte (serial_port.cpp:312-314), so a read
        /// of 256 bytes from a silent device lasts 2.6 s on the one thread that
        /// serves every lit unit. Its read on Linux waits 0.1 s whatever it asks
        /// for (VTIME 1 and VMIN 0, lines 549-550), and that is the bound here.
        /// A read hands back what has come by then, and the callers read
        /// again.</summary>
        internal const uint SerialReadTotalMs = 100;
        internal const uint SerialReadPerByteMs = 0;

        /// <summary>The longest a read of <paramref name="bytes"/> bytes takes
        /// on a port that sends nothing.</summary>
        internal static int SerialReadBoundMs(int bytes) => (int)(SerialReadTotalMs + SerialReadPerByteMs * (uint)bytes);

        /// <summary>The crash path's one write to a serial port, through a
        /// handle of its own, bounded. True when every byte went out.</summary>
        internal static bool SerialWriteOnce(string path, int baud, byte[] bytes, int timeoutMs)
        {
            if (OffLimitsForTest) return false;
            return HidReportChannel.RunBounded(() =>
            {
                using var port = OpenSerial(path, baud);
                return port != null && port.Write(bytes);
            }, timeoutMs);
        }

        private sealed class SerialLineChannel : ISerialLineChannel, IConnectedChannel
        {
            private readonly SafeFileHandle _handle;
            private int _disposed;

            public SerialLineChannel(SafeFileHandle handle) => _handle = handle;

            /// <summary>Whether the port is still there: a serial device that
            /// was removed fails this on its old handle.</summary>
            public bool StillConnected => System.Threading.Volatile.Read(ref _disposed) == 0
                && ClearCommError(_handle, out _, IntPtr.Zero);

            public bool Write(byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0) return true;
                return WriteFile(_handle, bytes, bytes.Length, out int written, IntPtr.Zero) && written == bytes.Length;
            }

            public int Read(byte[] buffer)
            {
                if (buffer == null || buffer.Length == 0) return 0;
                return ReadFile(_handle, buffer, buffer.Length, out int read, IntPtr.Zero) ? read : -1;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) _handle.Dispose();
            }
        }

        // ─────────────────────────────────────────────
        //  Imports
        // ─────────────────────────────────────────────

        private const uint GenericReadWrite = 0xC0000000;
        private const uint OpenExisting = 3;

        private const uint DcbOutxCtsFlow = 1u << 2;
        private const uint DcbOutxDsrFlow = 1u << 3;
        private const uint DcbDtrControl = 3u << 4;
        private const uint DcbOutX = 1u << 8;
        private const uint DcbInX = 1u << 9;
        private const uint DcbRtsControl = 3u << 12;
        private const uint DcbAbortOnError = 1u << 14;

        [StructLayout(LayoutKind.Sequential)]
        private struct DCB
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint Flags;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public byte XonChar;
            public byte XoffChar;
            public byte ErrorChar;
            public byte EofChar;
            public byte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
            byte[] output, int outputSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetupComm(SafeFileHandle file, uint inQueue, uint outQueue);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetCommState(SafeFileHandle file, ref DCB dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ClearCommError(SafeFileHandle file, out uint errors, IntPtr status);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommState(SafeFileHandle file, ref DCB dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommTimeouts(SafeFileHandle file, ref COMMTIMEOUTS timeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int count, out int read, IntPtr overlapped);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type,
            byte[] buffer, ref uint size, uint flags);
    }
}
