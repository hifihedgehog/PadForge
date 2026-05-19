// PadForge Impulse Trigger Tester — sends a 7-byte IOCTL_XUSB_SET_STATE
// payload (main motors + impulse trigger motors) directly to PadForge's
// HIDMaestro virtual XInput slot. Bypasses xinput1_4.dll (which has no
// exported function that carries the impulse trigger bytes) and
// Windows.Gaming.Input (which doesn't reach our HM virtual at all in a
// non-packaged desktop console app).
//
// PadForge's HM parser at HMaestroVirtualController.cs:663-678 reads
// data[2]/data[3] as main motors (high byte 0..255) and data[4]/data[5]
// as impulse trigger motors when the payload length is >= 7 bytes.
// That's the wire format games using GameInput SDK end up writing once
// Microsoft's GameInputSvc translates their call into an XUSB IOCTL.
//
// Slot enumeration mirrors XboxImpulseHidWriter.cs:122-218 in reverse:
// walk XUSB_INTERFACE_CLASS_GUID with SetupDi, KEEP only paths whose
// device-instance ID contains "hidmaestro" (the HM virtual marker),
// and index by surviving order — Nth surviving HM XUSB interface = the
// device handle for PadForge slot N.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ImpulseTriggerTester
{
    public static class Program
    {
        // Per-motor magnitude (0..255 — what the XUSB wire format carries).
        private static byte _lMain, _rMain, _lTrig, _rTrig;
        private static int _slotIndex;
        private static readonly object _renderLock = new();

        public static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help" || args[0] == "/?"))
            {
                PrintHelp();
                return 0;
            }

            Console.Title = "PadForge Impulse Trigger Tester";
            Console.CursorVisible = false;
            try
            {
                Console.Clear();
                Console.WriteLine("PadForge Impulse Trigger Tester");
                Console.WriteLine("================================");
                Console.WriteLine();
                Console.WriteLine("Drives all four motors (main L/R + impulse-trigger L/R) of");
                Console.WriteLine("PadForge's HIDMaestro virtual XInput slot N via the same");
                Console.WriteLine("IOCTL_XUSB_SET_STATE path Microsoft's GameInputSvc uses when");
                Console.WriteLine("a game (Forza / Gears / Halo) writes impulse triggers.");
                Console.WriteLine();
                Console.WriteLine("Slot index 0..15 = Nth surviving HM virtual XUSB interface");
                Console.WriteLine("in enumeration order — matches the slot index PadForge shows.");
                Console.WriteLine();
                PrintControls();
                Console.WriteLine();

                Render();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape)
                        break;
                    HandleKey(key);
                    Push();
                    Render();
                }
            }
            finally
            {
                _lMain = _rMain = _lTrig = _rTrig = 0;
                Push();
                Console.CursorVisible = true;
                Console.WriteLine();
                Console.WriteLine("Motors zeroed. Bye.");
            }
            return 0;
        }

        private static void PrintControls()
        {
            Console.WriteLine("Controls:");
            Console.WriteLine("  Q / A  - Left  impulse trigger motor   +/- 10%");
            Console.WriteLine("  W / S  - Right impulse trigger motor   +/- 10%");
            Console.WriteLine("  E / D  - Left  main (low-freq) motor   +/- 10%");
            Console.WriteLine("  R / F  - Right main (high-freq) motor  +/- 10%");
            Console.WriteLine("  L      - Pulse left  trigger full for 1 second");
            Console.WriteLine("  T      - Pulse right trigger full for 1 second");
            Console.WriteLine("  B      - Pulse both  triggers full for 1 second");
            Console.WriteLine("  M      - Pulse both  main motors full for 1 second");
            Console.WriteLine("  X      - Zero all motors");
            Console.WriteLine("  0..9   - Switch slot index (Shift+0..5 = slot 10..15)");
            Console.WriteLine("  Esc    - Quit (motors zero'd on exit)");
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage: ImpulseTriggerTester [-h|--help]");
            Console.WriteLine();
            PrintControls();
        }

        private static void HandleKey(ConsoleKeyInfo k)
        {
            const int step = 26; // 26/255 ≈ 10%
            switch (k.Key)
            {
                case ConsoleKey.Q: _lTrig = ClampStep(_lTrig + step); break;
                case ConsoleKey.A: _lTrig = ClampStep(_lTrig - step); break;
                case ConsoleKey.W: _rTrig = ClampStep(_rTrig + step); break;
                case ConsoleKey.S: _rTrig = ClampStep(_rTrig - step); break;
                case ConsoleKey.E: _lMain = ClampStep(_lMain + step); break;
                case ConsoleKey.D: _lMain = ClampStep(_lMain - step); break;
                case ConsoleKey.R: _rMain = ClampStep(_rMain + step); break;
                case ConsoleKey.F: _rMain = ClampStep(_rMain - step); break;
                case ConsoleKey.X: _lMain = _rMain = _lTrig = _rTrig = 0; break;
                case ConsoleKey.L: Pulse(lt: 255, rt: 0,   lm: 0,   rm: 0);   break;
                case ConsoleKey.T: Pulse(lt: 0,   rt: 255, lm: 0,   rm: 0);   break;
                case ConsoleKey.B: Pulse(lt: 255, rt: 255, lm: 0,   rm: 0);   break;
                case ConsoleKey.M: Pulse(lt: 0,   rt: 0,   lm: 255, rm: 255); break;
                case ConsoleKey.D0: _slotIndex = (k.Modifiers & ConsoleModifiers.Shift) != 0 ? 10 : 0; break;
                case ConsoleKey.D1: _slotIndex = (k.Modifiers & ConsoleModifiers.Shift) != 0 ? 11 : 1; break;
                case ConsoleKey.D2: _slotIndex = (k.Modifiers & ConsoleModifiers.Shift) != 0 ? 12 : 2; break;
                case ConsoleKey.D3: _slotIndex = (k.Modifiers & ConsoleModifiers.Shift) != 0 ? 13 : 3; break;
                case ConsoleKey.D4: _slotIndex = (k.Modifiers & ConsoleModifiers.Shift) != 0 ? 14 : 4; break;
                case ConsoleKey.D5: _slotIndex = (k.Modifiers & ConsoleModifiers.Shift) != 0 ? 15 : 5; break;
                case ConsoleKey.D6: _slotIndex = 6; break;
                case ConsoleKey.D7: _slotIndex = 7; break;
                case ConsoleKey.D8: _slotIndex = 8; break;
                case ConsoleKey.D9: _slotIndex = 9; break;
            }
        }

        private static byte ClampStep(int v) => (byte)Math.Clamp(v, 0, 255);

        private static void Pulse(byte lt, byte rt, byte lm, byte rm)
        {
            _lTrig = lt; _rTrig = rt; _lMain = lm; _rMain = rm;
            Push();
            Render();
            Thread.Sleep(1000);
            _lTrig = _rTrig = _lMain = _rMain = 0;
            Push();
        }

        private static void Push()
        {
            string path = ResolveHmVirtualPath(_slotIndex);
            if (path == null) return;
            using var h = CreateFileSafe(path,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, 0);
            if (h.IsInvalid) return;
            SendVibration(h, _lMain, _rMain, _lTrig, _rTrig);
        }

        // ─────────────────────────────────────────────
        //  IOCTL_XUSB_SET_STATE wire format
        // ─────────────────────────────────────────────

        // Per HM's HMaestroVirtualController.cs:663-678:
        //   data[2] = leftMotorSpeed  (0..255)
        //   data[3] = rightMotorSpeed (0..255)
        //   data[4] = leftTriggerMotor  — only read when payload.Length >= 7
        //   data[5] = rightTriggerMotor
        // OpenXInput's InSetState_t (the standard short version):
        //   byte 0 = deviceIndex
        //   byte 1 = ledState
        //   byte 2 = leftMotor
        //   byte 3 = rightMotor
        //   byte 4 = flags
        // The extended version inserts trigger bytes at 4/5 and shifts
        // flags to byte 6.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct XusbSetStateEx
        {
            public byte DeviceIndex;
            public byte LedState;
            public byte LeftMotor;
            public byte RightMotor;
            public byte LeftTriggerMotor;
            public byte RightTriggerMotor;
            public byte Flags;
        }

        private const byte XUSB_SET_STATE_FLAG_VIBRATION = 0x02;
        private const uint IOCTL_XUSB_SET_STATE = 0x8000A010;

        private static void SendVibration(SafeFileHandle h, byte lMain, byte rMain, byte lTrig, byte rTrig)
        {
            var payload = new XusbSetStateEx
            {
                DeviceIndex = 0,
                LedState = 0,
                LeftMotor = lMain,
                RightMotor = rMain,
                LeftTriggerMotor = lTrig,
                RightTriggerMotor = rTrig,
                Flags = XUSB_SET_STATE_FLAG_VIBRATION,
            };
            int size = Marshal.SizeOf<XusbSetStateEx>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(payload, buf, fDeleteOld: false);
                _ = DeviceIoControl(h, IOCTL_XUSB_SET_STATE,
                    buf, (uint)size,
                    IntPtr.Zero, 0,
                    out _, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // ─────────────────────────────────────────────
        //  HM-virtual XUSB enumeration
        // ─────────────────────────────────────────────

        // Mirrors XboxImpulseHidWriter.cs:122-218 — walk
        // XUSB_INTERFACE_CLASS_GUID with SetupDi, but KEEP only paths
        // whose device-instance ID contains "hidmaestro". Surviving Nth
        // = PadForge slot N.
        private static string ResolveHmVirtualPath(int slot)
        {
            Guid classGuid = XUSB_INTERFACE_CLASS_GUID;
            IntPtr devInfoSet = SetupDiGetClassDevsW(
                ref classGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfoSet == new IntPtr(-1)) return null;

            int survivingIdx = 0;
            string matched = null;
            try
            {
                var ifaceData = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
                };
                for (uint i = 0;
                     SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref classGuid, i, ref ifaceData);
                     i++)
                {
                    int required = 0;
                    SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData,
                        IntPtr.Zero, 0, ref required, IntPtr.Zero);
                    if (required <= 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        var devInfo = new SP_DEVINFO_DATA
                        {
                            cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>(),
                        };
                        if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData,
                                detail, required, ref required, ref devInfo))
                            continue;

                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        // KEEP only HM virtuals.
                        if (path.IndexOf("hidmaestro", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        if (survivingIdx == slot)
                        {
                            matched = path;
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
            return matched;
        }

        private static int CountHmVirtuals()
        {
            Guid classGuid = XUSB_INTERFACE_CLASS_GUID;
            IntPtr devInfoSet = SetupDiGetClassDevsW(
                ref classGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfoSet == new IntPtr(-1)) return 0;

            int count = 0;
            try
            {
                var ifaceData = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
                };
                for (uint i = 0;
                     SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref classGuid, i, ref ifaceData);
                     i++)
                {
                    int required = 0;
                    SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData,
                        IntPtr.Zero, 0, ref required, IntPtr.Zero);
                    if (required <= 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        var devInfo = new SP_DEVINFO_DATA
                        {
                            cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>(),
                        };
                        if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData,
                                detail, required, ref required, ref devInfo))
                            continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (string.IsNullOrEmpty(path)) continue;
                        if (path.IndexOf("hidmaestro", StringComparison.OrdinalIgnoreCase) >= 0)
                            count++;
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
            return count;
        }

        private static SafeFileHandle CreateFileSafe(string path, uint access, uint share, uint flags)
        {
            var h = CreateFileW(path, access, share, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
            return new SafeFileHandle(h, ownsHandle: true);
        }

        // ─────────────────────────────────────────────
        //  Render
        // ─────────────────────────────────────────────

        private static void Render()
        {
            lock (_renderLock)
            {
                int hmCount = CountHmVirtuals();
                bool slotPresent = _slotIndex < hmCount;
                const int row = 22;
                WriteAt(row + 0, $"Slot:              {_slotIndex}   ({hmCount} HM virtual(s) visible, slot {(slotPresent ? "present" : "MISSING")})");
                WriteAt(row + 1, $"L Main Motor:      {Bar(_lMain)}");
                WriteAt(row + 2, $"R Main Motor:      {Bar(_rMain)}");
                WriteAt(row + 3, $"L Impulse Trigger: {Bar(_lTrig)}");
                WriteAt(row + 4, $"R Impulse Trigger: {Bar(_rTrig)}");
            }
        }

        private static void WriteAt(int row, string text)
        {
            try
            {
                Console.SetCursorPosition(0, row);
                int width = Math.Max(Console.WindowWidth - 1, text.Length);
                Console.Write(text.PadRight(width));
            }
            catch
            {
                Console.WriteLine(text);
            }
        }

        private static string Bar(byte v)
        {
            const int width = 20;
            int filled = (int)Math.Round(v / 255.0 * width);
            return $"[{new string('#', filled)}{new string('-', width - filled)}] {(int)Math.Round(v / 255.0 * 100),3}%";
        }

        // ─────────────────────────────────────────────
        //  Win32 P/Invoke
        // ─────────────────────────────────────────────

        private static readonly Guid XUSB_INTERFACE_CLASS_GUID =
            new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x01;
        private const uint FILE_SHARE_WRITE = 0x02;
        private const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevsW(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr devInfoSet, IntPtr devInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr devInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr detailBuffer, int detailBufferSize, ref int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr devInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr detailBuffer, int detailBufferSize, ref int requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);
    }
}
