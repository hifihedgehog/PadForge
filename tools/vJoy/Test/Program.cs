using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

/// <summary>
/// Comprehensive standalone vJoy test tool.
/// Proves vJoy works externally from PadForge by exercising every layer:
/// registry configuration, SetupAPI device creation, DLL P/Invoke, and output feeding.
///
/// Commands:
///   VJoyTest create N [axes=6] [buttons=11] [povs=1]
///   VJoyTest remove
///   VJoyTest feed [--count N] [--updatevjd] [--v3]
///   VJoyTest info
///   VJoyTest full [N] [axes=6] [buttons=11] [povs=1]
/// </summary>
class Program
{
    // ─────────────────────────────────────────────────────────────────────
    //  vJoy P/Invoke
    // ─────────────────────────────────────────────────────────────────────

    const string DLL = "vJoyInterface.dll";

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool vJoyEnabled();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern int GetVJDStatus(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AcquireVJD(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern void RelinquishVJD(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ResetVJD(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetAxis(int value, uint rID, uint axis);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetBtn([MarshalAs(UnmanagedType.Bool)] bool value, uint rID, byte nBtn);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetDiscPov(int value, uint rID, byte nPov);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetContPov(int value, uint rID, byte nPov);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern int GetVJDButtonNumber(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern int GetVJDDiscPovNumber(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern int GetVJDContPovNumber(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVJDAxisExist(uint rID, uint axis);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVJDAxisMax(uint rID, uint axis, ref long maxVal);

    // UpdateVJD takes a pointer to JOYSTICK_POSITION (which is V2 or V3
    // depending on compile-time API version). The DLL compiled with V3
    // expects a 124-byte struct. With V2 it expects 108 bytes. Both are
    // tried by this test tool.
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UpdateVJD")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateVJD_V2(uint rID, ref JOYSTICK_POSITION_V2 pData);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UpdateVJD")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateVJD_V3(uint rID, ref JOYSTICK_POSITION_V3 pData);

    // SetupAPI P/Invoke for device node creation
    const int DIF_REGISTERDEVICE = 0x19;
    const int SPDRP_HARDWAREID = 0x01;
    const int DICD_GENERATE_ID = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName,
        ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData, int Property, byte[] PropertyBuffer, int PropertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string HardwareId,
        string FullInfPath, int InstallFlags, out bool bRebootRequired);

    // ─────────────────────────────────────────────────────────────────────
    //  WinMM joystick API (same backend as joy.cpl / Game Controllers)
    // ─────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct JOYINFOEX
    {
        public int dwSize;
        public int dwFlags;
        public int dwXpos;
        public int dwYpos;
        public int dwZpos;
        public int dwRpos;
        public int dwUpos;
        public int dwVpos;
        public int dwButtons;
        public int dwButtonNumber;
        public int dwPOV;
        public int dwReserved1;
        public int dwReserved2;
    }

    const int JOY_RETURNALL = 0xFF;
    const int JOYERR_NOERROR = 0;

    [DllImport("winmm.dll")]
    static extern int joyGetNumDevs();

    [DllImport("winmm.dll")]
    static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

    // ─────────────────────────────────────────────────────────────────────
    //  VjdStat values
    // ─────────────────────────────────────────────────────────────────────

    const int VJD_STAT_OWN = 0;
    const int VJD_STAT_FREE = 1;
    const int VJD_STAT_BUSY = 2;
    const int VJD_STAT_MISS = 3;

    static readonly string[] StatusNames = { "OWN", "FREE", "BUSY", "MISS" };

    static string StatusName(int s) => s >= 0 && s < StatusNames.Length ? StatusNames[s] : $"UNKNOWN({s})";

    // ─────────────────────────────────────────────────────────────────────
    //  HID Usage IDs (Generic Desktop page)
    // ─────────────────────────────────────────────────────────────────────

    const uint HID_USAGE_X = 0x30;
    const uint HID_USAGE_Y = 0x31;
    const uint HID_USAGE_Z = 0x32;
    const uint HID_USAGE_RX = 0x33;
    const uint HID_USAGE_RY = 0x34;
    const uint HID_USAGE_RZ = 0x35;

    static readonly uint[] AllAxes = { HID_USAGE_X, HID_USAGE_Y, HID_USAGE_Z, HID_USAGE_RX, HID_USAGE_RY, HID_USAGE_RZ };
    static readonly string[] AxisNames = { "X", "Y", "Z", "RX", "RY", "RZ" };

    // HID class GUID
    static Guid HID_GUID = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    const string VJOY_HWID = "root\\VID_1234&PID_0FFB&REV_0222";

    // ─────────────────────────────────────────────────────────────────────
    //  JOYSTICK_POSITION_V2 — 108 bytes (API version 2)
    //  Layout from public.h _JOYSTICK_POSITION_V2 with natural 4-byte alignment.
    //  Fields: bDevice(1B) + 3B pad + 18 LONGs(72B) + lButtons(4B)
    //          + 4 DWORDs(16B) + 3 LONGs(12B) = 108 bytes
    // ─────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Explicit, Size = 108)]
    struct JOYSTICK_POSITION_V2
    {
        [FieldOffset(0)]  public byte bDevice;       // 1-based device index
        [FieldOffset(4)]  public int wThrottle;
        [FieldOffset(8)]  public int wRudder;
        [FieldOffset(12)] public int wAileron;
        [FieldOffset(16)] public int wAxisX;
        [FieldOffset(20)] public int wAxisY;
        [FieldOffset(24)] public int wAxisZ;
        [FieldOffset(28)] public int wAxisXRot;
        [FieldOffset(32)] public int wAxisYRot;
        [FieldOffset(36)] public int wAxisZRot;
        [FieldOffset(40)] public int wSlider;
        [FieldOffset(44)] public int wDial;
        [FieldOffset(48)] public int wWheel;
        [FieldOffset(52)] public int wAxisVX;
        [FieldOffset(56)] public int wAxisVY;
        [FieldOffset(60)] public int wAxisVZ;
        [FieldOffset(64)] public int wAxisVBRX;
        [FieldOffset(68)] public int wAxisVBRY;
        [FieldOffset(72)] public int wAxisVBRZ;
        [FieldOffset(76)] public int lButtons;       // Buttons 1-32 bitmask
        [FieldOffset(80)] public uint bHats;          // Discrete POV 1 (low nibble)
        [FieldOffset(84)] public uint bHatsEx1;       // Discrete POV 2
        [FieldOffset(88)] public uint bHatsEx2;       // Discrete POV 3
        [FieldOffset(92)] public uint bHatsEx3;       // Discrete POV 4
        [FieldOffset(96)]  public int lButtonsEx1;   // Buttons 33-64
        [FieldOffset(100)] public int lButtonsEx2;   // Buttons 65-96
        [FieldOffset(104)] public int lButtonsEx3;   // Buttons 97-128
    }

    // ─────────────────────────────────────────────────────────────────────
    //  JOYSTICK_POSITION_V3 — 124 bytes (API version 3)
    //  V3 reorganized axes: removed wAxisVZ/VBR* from the middle,
    //  added wAccelerator/wBrake/wClutch/wSteering, moved VZ/VBR* to tail.
    // ─────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Explicit, Size = 124)]
    struct JOYSTICK_POSITION_V3
    {
        [FieldOffset(0)]   public byte bDevice;
        [FieldOffset(4)]   public int wThrottle;
        [FieldOffset(8)]   public int wRudder;
        [FieldOffset(12)]  public int wAileron;
        [FieldOffset(16)]  public int wAxisX;
        [FieldOffset(20)]  public int wAxisY;
        [FieldOffset(24)]  public int wAxisZ;
        [FieldOffset(28)]  public int wAxisXRot;
        [FieldOffset(32)]  public int wAxisYRot;
        [FieldOffset(36)]  public int wAxisZRot;
        [FieldOffset(40)]  public int wSlider;
        [FieldOffset(44)]  public int wDial;
        [FieldOffset(48)]  public int wWheel;
        [FieldOffset(52)]  public int wAccelerator;    // V3 new
        [FieldOffset(56)]  public int wBrake;          // V3 new
        [FieldOffset(60)]  public int wClutch;         // V3 new
        [FieldOffset(64)]  public int wSteering;       // V3 new
        [FieldOffset(68)]  public int wAxisVX;         // moved from offset 52 in V2
        [FieldOffset(72)]  public int wAxisVY;         // moved from offset 56 in V2
        [FieldOffset(76)]  public int lButtons;
        [FieldOffset(80)]  public uint bHats;
        [FieldOffset(84)]  public uint bHatsEx1;
        [FieldOffset(88)]  public uint bHatsEx2;
        [FieldOffset(92)]  public uint bHatsEx3;
        [FieldOffset(96)]  public int lButtonsEx1;
        [FieldOffset(100)] public int lButtonsEx2;
        [FieldOffset(104)] public int lButtonsEx3;
        [FieldOffset(108)] public int wAxisVZ;         // V3 moved to tail
        [FieldOffset(112)] public int wAxisVBRX;       // V3 moved to tail
        [FieldOffset(116)] public int wAxisVBRY;       // V3 moved to tail
        [FieldOffset(120)] public int wAxisVBRZ;       // V3 moved to tail
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Device layout configuration
    // ─────────────────────────────────────────────────────────────────────

    struct DeviceLayout
    {
        public int Axes;     // 1-6 (X, Y, Z, RX, RY, RZ)
        public int Buttons;  // 1-128
        public int Povs;     // 0-4 (discrete hat switches)

        public override string ToString() => $"{Axes} axes, {Buttons} buttons, {Povs} POVs";
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Entry point
    // ─────────────────────────────────────────────────────────────────────

    static bool _stopping;

    static int Main(string[] args)
    {
        Console.WriteLine("=== vJoy Comprehensive Test Tool ===");
        Console.WriteLine($"    JOYSTICK_POSITION_V2 size: {Marshal.SizeOf<JOYSTICK_POSITION_V2>()} bytes");
        Console.WriteLine($"    JOYSTICK_POSITION_V3 size: {Marshal.SizeOf<JOYSTICK_POSITION_V3>()} bytes");
        Console.WriteLine();

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string cmd = args[0].ToLowerInvariant();
        return cmd switch
        {
            "create"       => CmdCreate(args),
            "remove"       => CmdRemove(),
            "feed"         => CmdFeed(args),
            "info"         => CmdInfo(),
            "full"         => CmdFull(args),
            "battery"      => CmdBattery(),
            "incremental"  => CmdIncremental(args),
            "diag"         => CmdDiag(),
            "freshinstall" => CmdFreshInstall(),
            _              => Error($"Unknown command: {args[0]}")
        };
    }

    static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  VJoyTest create N [axes=6] [buttons=11] [povs=1]");
        Console.WriteLine("    Create N device nodes with specified layout.");
        Console.WriteLine("    Layout is written to the registry BEFORE creating nodes.");
        Console.WriteLine();
        Console.WriteLine("  VJoyTest remove");
        Console.WriteLine("    Remove all vJoy device nodes (ROOT\\HIDCLASS\\*).");
        Console.WriteLine();
        Console.WriteLine("  VJoyTest feed [--count N] [--updatevjd] [--v3]");
        Console.WriteLine("    Acquire first N free devices and feed test data.");
        Console.WriteLine("    Default: individual SetAxis/SetBtn/SetDiscPov calls.");
        Console.WriteLine("    --updatevjd: use UpdateVJD (single IOCTL per frame).");
        Console.WriteLine("    --v3: use V3 struct with UpdateVJD (default is V2).");
        Console.WriteLine();
        Console.WriteLine("  VJoyTest info");
        Console.WriteLine("    Show status and capabilities of all 16 device IDs.");
        Console.WriteLine();
        Console.WriteLine("  VJoyTest full [N] [axes=6] [buttons=11] [povs=1]");
        Console.WriteLine("    Full end-to-end test: create N devices, configure,");
        Console.WriteLine("    feed data, then cleanup on Ctrl+C.");
        Console.WriteLine();
        Console.WriteLine("  VJoyTest incremental [N]");
        Console.WriteLine("    Create N devices ONE AT A TIME (default 3), verifying each");
        Console.WriteLine("    previously created device still works after adding the next.");
        Console.WriteLine("    Tests PadForge's incremental creation pattern.");
        Console.WriteLine();
        Console.WriteLine("  VJoyTest freshinstall");
        Console.WriteLine("    Full DriverInstaller-style cleanup + reinstall + data flow test.");
        Console.WriteLine("    Replicates the exact cleanup that PadForge's DriverInstaller.cs does:");
        Console.WriteLine("    remove nodes, stop/delete service, clean registry, re-add driver.");
        Console.WriteLine();
        Console.WriteLine("Must run elevated (Administrator) for create/remove/full/freshinstall.");
    }

    static int Error(string msg)
    {
        Console.Error.WriteLine($"ERROR: {msg}");
        return 1;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Command: create
    // ─────────────────────────────────────────────────────────────────────

    static int CmdCreate(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int count) || count < 1 || count > 16)
            return Error("Usage: VJoyTest create N [axes=6] [buttons=11] [povs=1]  (N = 1-16)");

        var layout = ParseLayout(args, 2);
        Console.WriteLine($"Creating {count} device(s) with layout: {layout}");

        WriteRegistryConfig(count, layout);
        if (!CreateDeviceNodes(count))
            return Error("Failed to create device nodes.");

        Console.WriteLine("Waiting for PnP driver binding...");
        if (!WaitForDevices(count, timeout: TimeSpan.FromSeconds(10)))
        {
            Console.WriteLine("WARNING: Devices created but driver not fully bound yet.");
            Console.WriteLine("         Try 'VJoyTest info' in a few seconds.");
        }
        else
        {
            Console.WriteLine("All devices ready.");
        }
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Command: remove
    // ─────────────────────────────────────────────────────────────────────

    static int CmdRemove()
    {
        Console.WriteLine("Enumerating vJoy device nodes...");
        var ids = EnumerateVJoyInstanceIds();
        if (ids.Count == 0)
        {
            Console.WriteLine("No vJoy device nodes found.");
            return 0;
        }

        Console.WriteLine($"Found {ids.Count} vJoy device node(s):");
        foreach (var id in ids)
            Console.WriteLine($"  {id}");

        int removed = 0;
        foreach (var id in ids)
        {
            Console.Write($"  Removing {id}... ");
            if (RemoveDeviceNode(id))
            {
                Console.WriteLine("OK");
                removed++;
            }
            else
            {
                Console.WriteLine("FAILED");
            }
        }

        Console.WriteLine($"Removed {removed}/{ids.Count} device node(s).");
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Command: info
    // ─────────────────────────────────────────────────────────────────────

    static int CmdInfo()
    {
        if (!TryLoadDll())
            return Error("Cannot load vJoyInterface.dll");

        bool enabled;
        try { enabled = vJoyEnabled(); }
        catch (DllNotFoundException) { return Error("vJoyInterface.dll not found"); }

        Console.WriteLine($"vJoyEnabled: {enabled}");
        Console.WriteLine();

        // Also show PnP device nodes
        var instanceIds = EnumerateVJoyInstanceIds();
        Console.WriteLine($"PnP device nodes (pnputil): {instanceIds.Count}");
        foreach (var iid in instanceIds)
            Console.WriteLine($"  {iid}");
        Console.WriteLine();

        Console.WriteLine($"{"ID",-4} {"Status",-8} {"Axes",-14} {"Btns",-6} {"POVs",-12} {"AxisMax",-10}");
        Console.WriteLine(new string('-', 58));

        for (uint id = 1; id <= 16; id++)
        {
            int status = GetVJDStatus(id);
            string sName = StatusName(status);

            if (status == VJD_STAT_MISS)
            {
                Console.WriteLine($"{id,-4} {sName,-8} {"---",-14} {"---",-6} {"---",-12}");
                continue;
            }

            // Query capabilities
            var sb = new StringBuilder();
            for (int i = 0; i < AllAxes.Length; i++)
            {
                if (GetVJDAxisExist(id, AllAxes[i]))
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(AxisNames[i]);
                }
            }
            int buttons = GetVJDButtonNumber(id);
            int discPovs = GetVJDDiscPovNumber(id);
            int contPovs = GetVJDContPovNumber(id);
            string povStr = contPovs > 0 ? $"{contPovs}c" : discPovs > 0 ? $"{discPovs}d" : "0";
            long maxVal = 0;
            GetVJDAxisMax(id, HID_USAGE_X, ref maxVal);

            Console.WriteLine($"{id,-4} {sName,-8} {sb,-14} {buttons,-6} {povStr,-12} {maxVal,-10}");
        }

        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Command: feed
    // ─────────────────────────────────────────────────────────────────────

    static int CmdFeed(string[] args)
    {
        int count = 1;
        bool useUpdateVjd = false;
        bool useV3 = false;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (a == "--count" && i + 1 < args.Length && int.TryParse(args[i + 1], out int c))
            {
                count = c;
                i++;
            }
            else if (a == "--updatevjd") useUpdateVjd = true;
            else if (a == "--v3") useV3 = true;
        }

        if (!TryLoadDll())
            return Error("Cannot load vJoyInterface.dll");

        if (!vJoyEnabled())
            return Error("vJoy is not enabled. Create device nodes first.");

        // Find free devices
        var deviceIds = new List<uint>();
        for (uint id = 1; id <= 16 && deviceIds.Count < count; id++)
        {
            int status = GetVJDStatus(id);
            if (status == VJD_STAT_FREE || status == VJD_STAT_OWN)
                deviceIds.Add(id);
        }

        if (deviceIds.Count == 0)
            return Error("No free or owned vJoy devices found.");

        if (deviceIds.Count < count)
            Console.WriteLine($"WARNING: Only found {deviceIds.Count} available device(s) (requested {count}).");

        // Acquire all devices
        foreach (uint id in deviceIds)
        {
            int status = GetVJDStatus(id);
            if (status == VJD_STAT_FREE)
            {
                Console.Write($"  Acquiring device {id}... ");
                if (!AcquireVJD(id))
                {
                    Console.WriteLine("FAILED");
                    continue;
                }
                Console.WriteLine("OK");
            }
            else
            {
                Console.WriteLine($"  Device {id}: already owned");
            }
            ResetVJD(id);
        }

        // Query capabilities for each device
        Console.WriteLine("\nDevice capabilities:");
        foreach (uint id in deviceIds)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < AllAxes.Length; i++)
            {
                if (GetVJDAxisExist(id, AllAxes[i]))
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(AxisNames[i]);
                }
            }
            int btns = GetVJDButtonNumber(id);
            int povs = Math.Max(GetVJDContPovNumber(id), GetVJDDiscPovNumber(id));
            long maxV = 0;
            GetVJDAxisMax(id, HID_USAGE_X, ref maxV);
            Console.WriteLine($"  Device {id}: axes=[{sb}] buttons={btns} POVs={povs} axisMax={maxV}");
        }

        string mode = useUpdateVjd ? (useV3 ? "UpdateVJD (V3 struct)" : "UpdateVJD (V2 struct)") : "Individual SetAxis/SetBtn/SetDiscPov";
        Console.WriteLine($"\nFeeding {deviceIds.Count} device(s) using: {mode}");
        Console.WriteLine("Open 'Set up USB game controllers' (joy.cpl) to see the devices.");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        // Feed loop
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            _stopping = true;
        };

        FeedLoop(deviceIds, useUpdateVjd, useV3);

        // Cleanup
        Console.WriteLine("\nCleaning up...");
        foreach (uint id in deviceIds)
        {
            ResetVJD(id);
            RelinquishVJD(id);
            Console.WriteLine($"  Relinquished device {id}");
        }

        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Command: full (end-to-end)
    // ─────────────────────────────────────────────────────────────────────

    static int CmdFull(string[] args)
    {
        int count = 1;
        int startIdx = 1;

        // Parse optional count
        if (args.Length > 1 && int.TryParse(args[1], out int c) && c >= 1 && c <= 16)
        {
            count = c;
            startIdx = 2;
        }

        var layout = ParseLayout(args, startIdx);

        Console.WriteLine($"Full end-to-end test: {count} device(s) with layout: {layout}");
        Console.WriteLine("Press Ctrl+C to stop and cleanup.\n");

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            _stopping = true;
        };

        // Phase 1: Write registry configuration
        Console.WriteLine("[Phase 1] Writing registry configuration...");
        WriteRegistryConfig(count, layout);

        // Phase 2: Create device nodes
        Console.WriteLine("[Phase 2] Creating device nodes via SetupAPI...");
        if (!CreateDeviceNodes(count))
        {
            Console.Error.WriteLine("ERROR: Failed to create device nodes. Are you running elevated?");
            return 1;
        }

        // Phase 3: Wait for PnP
        Console.WriteLine("[Phase 3] Waiting for PnP driver binding...");
        if (!WaitForDevices(count, timeout: TimeSpan.FromSeconds(15)))
        {
            Console.Error.WriteLine("ERROR: Devices not ready after 15 seconds.");
            Cleanup(new List<uint>());
            return 1;
        }

        // Phase 4: Acquire devices
        Console.WriteLine("[Phase 4] Acquiring devices...");
        var deviceIds = new List<uint>();
        for (uint id = 1; id <= 16 && deviceIds.Count < count; id++)
        {
            int status = GetVJDStatus(id);
            if (status == VJD_STAT_FREE)
            {
                if (AcquireVJD(id))
                {
                    ResetVJD(id);
                    deviceIds.Add(id);
                    Console.WriteLine($"  Acquired device {id}");
                }
                else
                {
                    Console.WriteLine($"  Failed to acquire device {id}");
                }
            }
        }

        if (deviceIds.Count == 0)
        {
            Console.Error.WriteLine("ERROR: No devices could be acquired.");
            Cleanup(deviceIds);
            return 1;
        }

        // Print capabilities
        Console.WriteLine("\n[Phase 4b] Device capabilities:");
        foreach (uint id in deviceIds)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < AllAxes.Length; i++)
            {
                if (GetVJDAxisExist(id, AllAxes[i]))
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(AxisNames[i]);
                }
            }
            int btns = GetVJDButtonNumber(id);
            int povs = Math.Max(GetVJDContPovNumber(id), GetVJDDiscPovNumber(id));
            long maxV = 0;
            GetVJDAxisMax(id, HID_USAGE_X, ref maxV);
            Console.WriteLine($"  Device {id}: axes=[{sb}] buttons={btns} POVs={povs} axisMax={maxV}");
        }

        // Phase 5: Test both output modes
        Console.WriteLine("\n[Phase 5] Testing individual SetAxis/SetBtn/SetDiscPov...");
        if (!TestIndividualCalls(deviceIds))
            Console.WriteLine("  WARNING: Individual calls had failures.");
        else
            Console.WriteLine("  Individual calls: OK");

        Console.WriteLine("\n[Phase 6] Testing UpdateVJD with V2 struct...");
        if (!TestUpdateVjdV2(deviceIds))
            Console.WriteLine("  WARNING: UpdateVJD V2 had failures (this is normal if DLL uses V3).");
        else
            Console.WriteLine("  UpdateVJD V2: OK");

        Console.WriteLine("\n[Phase 7] Testing UpdateVJD with V3 struct...");
        if (!TestUpdateVjdV3(deviceIds))
            Console.WriteLine("  WARNING: UpdateVJD V3 had failures.");
        else
            Console.WriteLine("  UpdateVJD V3: OK");

        // Phase 8: Continuous feed
        Console.WriteLine("\n[Phase 8] Continuous feed (individual calls). Press Ctrl+C to stop.\n");
        FeedLoop(deviceIds, useUpdateVjd: false, useV3: false);

        // Cleanup
        Cleanup(deviceIds);
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Command: freshinstall — Full DriverInstaller-style cleanup + test
    //  Replicates PadForge's DriverInstaller.cs flow exactly.
    // ─────────────────────────────────────────────────────────────────────

    static int CmdFreshInstall()
    {
        Console.WriteLine("=== FRESH INSTALL: Replicating PadForge DriverInstaller.cs flow ===\n");

        string vjoyDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
        string vjoyInf = System.IO.Path.Combine(vjoyDir, "vjoy.inf");
        string vjoyDll = System.IO.Path.Combine(vjoyDir, "vJoyInterface.dll");

        // Verify vJoy files exist before we nuke everything.
        if (!System.IO.File.Exists(vjoyInf))
            return Error($"vjoy.inf not found at {vjoyInf}. Install vJoy first via PadForge Settings.");
        if (!System.IO.File.Exists(System.IO.Path.Combine(vjoyDir, "vjoy.sys")))
            return Error($"vjoy.sys not found in {vjoyDir}. Install vJoy first via PadForge Settings.");

        // Snapshot WinMM devices before.
        var beforeDevices = EnumerateWinMMDevices();
        Console.WriteLine($"WinMM devices before: {string.Join(",", beforeDevices.OrderBy(x => x))}");

        // ─── Step 1: Remove ALL device nodes ───
        Console.WriteLine("\n[Step 1] Removing all vJoy device nodes...");
        RemoveAllVJoyNodes();
        // Also try ROOT\HIDCLASS\0000-0015 explicitly (DriverInstaller does this).
        for (int i = 0; i <= 15; i++)
        {
            string instanceId = $"ROOT\\HIDCLASS\\{i:D4}";
            RemoveDeviceNode(instanceId); // Ignore failures — may not exist.
        }
        Thread.Sleep(2000);
        Console.WriteLine("  Done.");

        // ─── Step 2: Stop vjoy service ───
        Console.WriteLine("\n[Step 2] Stopping vjoy service...");
        RunPnpCmd("sc.exe", "stop vjoy");
        Thread.Sleep(2000);

        // ─── Step 3: Delete OEM inf from driver store ───
        Console.WriteLine("\n[Step 3] Removing vJoy OEM inf from driver store...");
        var oemInfs = FindVJoyOemInfs();
        if (oemInfs.Length > 0)
        {
            foreach (var inf in oemInfs)
            {
                Console.Write($"  Removing {inf}... ");
                RunPnpCmd("pnputil.exe", $"/delete-driver {inf} /uninstall /force");
                Console.WriteLine("done");
            }
        }
        else
        {
            Console.WriteLine("  No OEM infs found.");
        }

        // ─── Step 4: Delete the service ───
        Console.WriteLine("\n[Step 4] Deleting vjoy service (sc delete)...");
        RunPnpCmd("sc.exe", "delete vjoy");

        // ─── Step 5: Remove ALL service registry keys from ALL ControlSets ───
        Console.WriteLine("\n[Step 5] Cleaning service registry keys...");
        string[] controlSets = { "CurrentControlSet", "ControlSet001", "ControlSet002", "ControlSet003" };
        foreach (var cs in controlSets)
        {
            string path = $@"SYSTEM\{cs}\Services\vjoy";
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                Console.WriteLine($"  Deleted HKLM\\{path}");
            }
            catch
            {
                Console.WriteLine($"  HKLM\\{path} — not found or locked");
            }
        }

        // ─── Step 6: Delete vjoy.sys from System32\drivers ───
        Console.WriteLine("\n[Step 6] Removing vjoy.sys from System32\\drivers...");
        string driversSys = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "vjoy.sys");
        try
        {
            if (System.IO.File.Exists(driversSys))
            {
                System.IO.File.Delete(driversSys);
                Console.WriteLine($"  Deleted {driversSys}");
            }
            else
            {
                Console.WriteLine($"  {driversSys} not found.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not delete: {ex.Message}");
        }

        Console.WriteLine("\n  >>> Full cleanup complete. Waiting 3 seconds for system to settle...");
        Thread.Sleep(3000);

        // ─── Step 7: Re-add driver to store ───
        Console.WriteLine("\n[Step 7] Adding vJoy driver back to driver store...");
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/add-driver \"{vjoyInf}\" /install",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(30_000);
                Console.WriteLine(stdout);
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.WriteLine($"  stderr: {stderr}");
                Console.WriteLine($"  pnputil exit code: {proc.ExitCode}");
            }
        }

        Thread.Sleep(2000);

        // ─── Step 8: Create device node via SetupAPI ───
        Console.WriteLine("\n[Step 8] Creating device node via SetupAPI...");
        if (!CreateDeviceNodes(1))
            return Error("Failed to create device node.");

        // ─── Step 9: Wait for PnP ───
        Console.WriteLine("\n[Step 9] Waiting for PnP driver binding...");
        if (!WaitForDevices(1, timeout: TimeSpan.FromSeconds(15)))
        {
            Console.Error.WriteLine("WARNING: Device not ready after 15 seconds.");
        }

        Thread.Sleep(3000);

        // ─── Step 10: Check driver state ───
        Console.WriteLine("\n[Step 10] Checking driver state...");
        string uf = GetDeviceNodeUpperFilters();
        Console.WriteLine($"  UpperFilters: {(string.IsNullOrEmpty(uf) ? "MISSING" : uf)}");

        // Check service status.
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query vjoy",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);
                Console.WriteLine(output);
            }
        }

        // ─── Step 11: Load DLL and acquire ───
        Console.WriteLine("\n[Step 11] Loading vJoyInterface.dll and acquiring device...");
        if (!TryLoadDll())
            return Error("Cannot load vJoyInterface.dll");

        bool enabled = vJoyEnabled();
        Console.WriteLine($"  vJoyEnabled: {enabled}");
        if (!enabled)
            return Error("vJoyEnabled() returned false after fresh install.");

        uint devId = 0;
        for (uint id = 1; id <= 16; id++)
        {
            int status = GetVJDStatus(id);
            Console.WriteLine($"  Device {id}: {StatusName(status)}");
            if (status == VJD_STAT_FREE && devId == 0)
            {
                if (AcquireVJD(id))
                {
                    ResetVJD(id);
                    devId = id;
                    Console.WriteLine($"  >>> Acquired device {id}");
                }
            }
        }

        if (devId == 0)
            return Error("No devices could be acquired.");

        // Print capabilities.
        int btns = GetVJDButtonNumber(devId);
        int povs = Math.Max(GetVJDContPovNumber(devId), GetVJDDiscPovNumber(devId));
        long maxV = 0;
        GetVJDAxisMax(devId, HID_USAGE_X, ref maxV);
        Console.WriteLine($"  Capabilities: buttons={btns} povs={povs} axisMax={maxV}");

        // ─── Step 12: Feed data and verify via WinMM ───
        Console.WriteLine("\n[Step 12] Feeding data and verifying via WinMM...");

        int axisMax = (int)maxV;
        if (axisMax <= 0) axisMax = 32767;

        // Set distinctive values: X=75%, Y=25%.
        int testX = axisMax * 3 / 4;
        int testY = axisMax / 4;
        bool setOk = SetAxis(testX, devId, HID_USAGE_X);
        setOk &= SetAxis(testY, devId, HID_USAGE_Y);
        if (GetVJDAxisExist(devId, HID_USAGE_Z))
            setOk &= SetAxis(axisMax / 2, devId, HID_USAGE_Z);
        setOk &= SetBtn(true, devId, 1);
        Console.WriteLine($"  SetAxis/SetBtn calls: {(setOk ? "OK" : "FAIL")}");

        // Pump many frames to ensure the timer fires.
        Console.Write("  Pumping 500 frames (5 seconds)... ");
        for (int i = 0; i < 500; i++)
        {
            SetAxis(testX, devId, HID_USAGE_X);
            SetAxis(testY, devId, HID_USAGE_Y);
            Thread.Sleep(10);
        }
        Console.WriteLine("done.");

        // Check WinMM.
        var afterDevices = EnumerateWinMMDevices();
        var newDevices = new HashSet<int>(afterDevices);
        newDevices.ExceptWith(beforeDevices);

        Console.WriteLine($"  WinMM devices after: {string.Join(",", afterDevices.OrderBy(x => x))} (new: {string.Join(",", newDevices.OrderBy(x => x))})");

        // Try all devices.
        Console.WriteLine("\n  WinMM device state dump:");
        int vjoyJoyId = -1;
        foreach (int jid in afterDevices.OrderBy(x => x))
        {
            var r = ReadWinMMDevice(jid);
            bool isNew = newDevices.Contains(jid);
            Console.WriteLine($"    joyID={jid}{(isNew ? " *NEW*" : "")} : X={r.x} Y={r.y} Z={r.z} R={r.r} U={r.u} V={r.v} Btns=0x{r.buttons:X} POV={r.pov}");
            if (isNew && vjoyJoyId < 0)
                vjoyJoyId = jid;
        }

        if (vjoyJoyId < 0 && afterDevices.Count > 0)
        {
            // No new devices — try to find one with non-default data.
            foreach (int jid in afterDevices.OrderBy(x => x))
            {
                var r = ReadWinMMDevice(jid);
                if (r.ok && r.x != 0 && r.x != 32767 && r.x != 65535)
                {
                    vjoyJoyId = jid;
                    break;
                }
            }
        }

        // ─── Step 13: Data flow verification ───
        Console.WriteLine("\n[Step 13] Data flow verification...");
        if (vjoyJoyId >= 0)
        {
            var r1 = ReadWinMMDevice(vjoyJoyId);
            Console.WriteLine($"  Target joyID={vjoyJoyId}: X={r1.x} Y={r1.y}");

            // Change value and re-read.
            int newX = axisMax / 2;
            SetAxis(newX, devId, HID_USAGE_X);
            Thread.Sleep(100);
            // Pump a few more frames.
            for (int i = 0; i < 20; i++)
            {
                SetAxis(newX, devId, HID_USAGE_X);
                Thread.Sleep(10);
            }

            var r2 = ReadWinMMDevice(vjoyJoyId);
            Console.WriteLine($"  After SetAxis(X={newX}): X={r2.x} Y={r2.y}");

            if (r2.x != r1.x && r2.x != 32767)
            {
                Console.WriteLine("\n  >>> DATA FLOWS CORRECTLY! Fresh install fixed the issue. <<<");
            }
            else
            {
                Console.WriteLine("\n  >>> DATA STILL NOT FLOWING — same as before. <<<");
            }
        }
        else
        {
            Console.WriteLine("  No vJoy device visible to WinMM at all.");
        }

        // Cleanup.
        Console.WriteLine("\n[Cleanup] Relinquishing device...");
        RelinquishVJD(devId);

        Console.WriteLine("\nDone. Device node left in place for manual inspection.");
        Console.WriteLine("Run 'VJoyTest remove' to clean up, or 'VJoyTest feed' to test more.");
        return 0;
    }

    /// <summary>
    /// Finds vJoy OEM inf names in the driver store (same as DriverInstaller.cs).
    /// </summary>
    static string[] FindVJoyOemInfs()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/enum-drivers",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return Array.Empty<string>();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);

            var results = new List<string>();
            string[] lines = output.Split('\n');
            string currentOem = null;
            bool isVJoyBlock = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("Published", StringComparison.OrdinalIgnoreCase) && line.Contains(":"))
                {
                    if (isVJoyBlock && currentOem != null)
                        results.Add(currentOem);
                    currentOem = line.Substring(line.IndexOf(':') + 1).Trim();
                    isVJoyBlock = false;
                }
                else if (currentOem != null &&
                         (line.IndexOf("shaul", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          line.IndexOf("vjoy", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    isVJoyBlock = true;
                }
            }
            if (isVJoyBlock && currentOem != null)
                results.Add(currentOem);
            return results.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Runs a command and ignores output/errors (best-effort, like DriverInstaller scripts).
    /// </summary>
    static void RunPnpCmd(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit(10_000);
            }
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Battery test — automated permutations with DirectInput readback
    // ─────────────────────────────────────────────────────────────────────

    static int CmdBattery()
    {
        Console.WriteLine("=== BATTERY TEST: Automated permutation tests with WinMM readback ===\n");

        // Test configurations: (axes, buttons, povs)
        var configs = new (int axes, int buttons, int povs)[]
        {
            (6, 11, 1),   // PadForge default (Xbox-like)
            (2, 4, 0),    // Minimal: 2 axes, 4 buttons, no POV
            (6, 32, 1),   // Max buttons for V2 bitmask
            (4, 8, 1),    // Mid-range
            (6, 11, 0),   // No POV
            (1, 1, 0),    // Absolute minimal
        };

        int passed = 0;
        int failed = 0;
        var failures = new List<string>();

        foreach (var cfg in configs)
        {
            string cfgName = $"axes={cfg.axes} buttons={cfg.buttons} povs={cfg.povs}";
            Console.WriteLine($"─── Config: {cfgName} ───");

            // Cleanup any previous state
            RemoveAllVJoyNodes();
            Thread.Sleep(2000); // WinMM needs time to deregister the old device

            var layout = new DeviceLayout { Axes = cfg.axes, Buttons = cfg.buttons, Povs = cfg.povs };

            // Snapshot WinMM devices BEFORE creating vJoy
            var beforeDevices = EnumerateWinMMDevices();
            Console.WriteLine($"  WinMM devices before: {string.Join(",", beforeDevices.OrderBy(x => x))}");

            // Phase 1: Registry
            try { WriteRegistryConfig(1, layout); }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL: Registry write: {ex.Message}");
                failures.Add($"{cfgName}: Registry write failed");
                failed++;
                continue;
            }

            // Phase 2: Create device node
            if (!CreateDeviceNodes(1))
            {
                Console.WriteLine($"  FAIL: Device node creation failed");
                failures.Add($"{cfgName}: Device creation failed");
                failed++;
                continue;
            }

            // Phase 3: Wait for PnP
            if (!WaitForDevices(1, timeout: TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine($"  FAIL: PnP binding timeout");
                failures.Add($"{cfgName}: PnP timeout");
                failed++;
                RemoveAllVJoyNodes();
                continue;
            }

            // Phase 4: Acquire
            uint devId = 0;
            for (uint id = 1; id <= 16; id++)
            {
                if (GetVJDStatus(id) == VJD_STAT_FREE && AcquireVJD(id))
                {
                    ResetVJD(id);
                    devId = id;
                    break;
                }
            }
            if (devId == 0)
            {
                Console.WriteLine($"  FAIL: Could not acquire any device");
                failures.Add($"{cfgName}: Acquire failed");
                failed++;
                RemoveAllVJoyNodes();
                continue;
            }

            // Phase 5: Verify vJoy API capabilities match
            int reportedBtns = GetVJDButtonNumber(devId);
            int reportedPovs = Math.Max(GetVJDContPovNumber(devId), GetVJDDiscPovNumber(devId));
            long maxV = 0;
            GetVJDAxisMax(devId, HID_USAGE_X, ref maxV);
            Console.WriteLine($"  vJoy API: buttons={reportedBtns} povs={reportedPovs} axisMax={maxV}");

            bool apiOk = true;
            if (reportedBtns != cfg.buttons)
            {
                Console.WriteLine($"  WARN: Expected {cfg.buttons} buttons, got {reportedBtns}");
                // Not a hard failure — driver may report differently
            }
            if (reportedPovs != cfg.povs)
            {
                Console.WriteLine($"  WARN: Expected {cfg.povs} POVs, got {reportedPovs}");
            }

            // Phase 6: Test individual calls
            bool indivOk = true;
            int axisMax = (int)maxV;
            if (axisMax <= 0) axisMax = 32767;

            // Set known values
            int testX = axisMax * 3 / 4;  // 75%
            int testY = axisMax / 4;       // 25%
            if (cfg.axes >= 1) indivOk &= SetAxis(testX, devId, HID_USAGE_X);
            if (cfg.axes >= 2) indivOk &= SetAxis(testY, devId, HID_USAGE_Y);
            if (cfg.axes >= 3) indivOk &= SetAxis(axisMax / 2, devId, HID_USAGE_Z);
            if (cfg.axes >= 4) indivOk &= SetAxis(axisMax / 3, devId, HID_USAGE_RX);
            if (cfg.axes >= 5) indivOk &= SetAxis(axisMax * 2 / 3, devId, HID_USAGE_RY);
            if (cfg.axes >= 6) indivOk &= SetAxis(axisMax / 5, devId, HID_USAGE_RZ);

            // Set button 1 pressed
            if (cfg.buttons >= 1) indivOk &= SetBtn(true, devId, 1);

            // Set POV (continuous: 18000 = 180° = South/Down)
            if (cfg.povs >= 1) indivOk &= SetContPov(18000, devId, 1);

            Console.WriteLine($"  Individual calls: {(indivOk ? "OK" : "FAIL")}");

            // Phase 7: Test UpdateVJD V2
            var posV2 = new JOYSTICK_POSITION_V2 { bDevice = (byte)devId };
            posV2.wAxisX = testX;
            posV2.wAxisY = testY;
            posV2.lButtons = 1; // Button 1
            posV2.bHats = 18000;    // POV Down (continuous: 180°)
            bool v2Ok = UpdateVJD_V2(devId, ref posV2);
            Console.WriteLine($"  UpdateVJD V2: {(v2Ok ? "OK" : "FAIL")}");

            // Phase 8: WinMM readback verification
            // Poll WinMM for up to 5 seconds until the device appears.
            // WinMM caches device enumeration and can be slow to pick up
            // newly created devices, especially after rapid remove/create cycles.
            HashSet<int> afterDevices = null;
            HashSet<int> newDevices = null;
            for (int poll = 0; poll < 10; poll++)
            {
                Thread.Sleep(500);
                afterDevices = EnumerateWinMMDevices();
                newDevices = new HashSet<int>(afterDevices);
                newDevices.ExceptWith(beforeDevices);
                if (newDevices.Count > 0 || afterDevices.Count > beforeDevices.Count)
                    break;
            }

            Console.Write("  WinMM readback: ");
            Console.Write($"before={beforeDevices.Count} after={afterDevices.Count} new={newDevices.Count} | ");

            int vjoyJoyId = -1;
            if (newDevices.Count > 0)
            {
                vjoyJoyId = newDevices.First();
            }
            else if (afterDevices.Count > 0)
            {
                // No new devices — vJoy might have replaced an existing slot.
                // Try all responding devices and look for one whose data matches what we set.
                foreach (int id in afterDevices)
                {
                    var r = ReadWinMMDevice(id);
                    // Our test values: X=75%, Y=25% of axisMax
                    if (r.ok && r.x != 0 && r.x != 32767)
                    {
                        vjoyJoyId = id;
                        break;
                    }
                }
            }

            if (vjoyJoyId >= 0)
            {
                var r = ReadWinMMDevice(vjoyJoyId);
                Console.WriteLine($"FOUND at joyID={vjoyJoyId}");
                Console.WriteLine($"    X={r.x} Y={r.y} Z={r.z} R={r.r} U={r.u} V={r.v} Btns=0x{r.buttons:X} POV={r.pov}");

                // Now verify output data changes: set a different value and read back
                int newTestX = axisMax / 2;
                SetAxis(newTestX, devId, HID_USAGE_X);
                Thread.Sleep(50);
                var r2 = ReadWinMMDevice(vjoyJoyId);
                bool dataFlows = r2.ok && r2.x != r.x;
                Console.Write($"    Data flow test: set X={newTestX}, read X={r2.x} → ");
                if (dataFlows)
                    Console.WriteLine("DATA FLOWS CORRECTLY");
                else
                    Console.WriteLine("DATA NOT CHANGING (output not reaching WinMM!)");

                if (!dataFlows)
                {
                    failures.Add($"{cfgName}: WinMM data flow failed — output not reaching Windows");
                    failed++;
                    RelinquishVJD(devId);
                    RemoveAllVJoyNodes();
                    continue;
                }
            }
            else
            {
                Console.WriteLine("NOT FOUND — vJoy device not visible to WinMM/joy.cpl!");
                // Dump all WinMM device states for diagnosis
                foreach (int id in afterDevices)
                {
                    var r = ReadWinMMDevice(id);
                    Console.WriteLine($"    joyID={id}: X={r.x} Y={r.y} Btns=0x{r.buttons:X} POV={r.pov}");
                }
                failures.Add($"{cfgName}: WinMM readback failed — device not visible");
                failed++;
                RelinquishVJD(devId);
                RemoveAllVJoyNodes();
                continue;
            }

            // Phase 9: Check device node UpperFilters
            Console.Write("  UpperFilters: ");
            string upperFilters = GetDeviceNodeUpperFilters();
            Console.WriteLine(string.IsNullOrEmpty(upperFilters) ? "MISSING (no mshidkmdf!)" : upperFilters);
            if (string.IsNullOrEmpty(upperFilters) || !upperFilters.Contains("mshidkmdf", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  CRITICAL: mshidkmdf not in UpperFilters — HID bridge missing!");
                failures.Add($"{cfgName}: mshidkmdf UpperFilter missing");
            }

            Console.WriteLine($"  RESULT: PASS\n");
            passed++;

            // Cleanup
            RelinquishVJD(devId);
            RemoveAllVJoyNodes();
            Thread.Sleep(2000); // Give WinMM time to deregister
        }

        // Summary
        Console.WriteLine("\n═══════════════════════════════════════");
        Console.WriteLine($"  PASSED: {passed}/{configs.Length}");
        Console.WriteLine($"  FAILED: {failed}/{configs.Length}");
        if (failures.Count > 0)
        {
            Console.WriteLine("  Failures:");
            foreach (var f in failures)
                Console.WriteLine($"    - {f}");
        }
        Console.WriteLine("═══════════════════════════════════════");

        return failed > 0 ? 1 : 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Command: incremental — tests PadForge's incremental creation pattern
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tests incremental device creation: creates devices one at a time
    /// (exactly like PadForge does), verifying each device works before
    /// creating the next. This catches the INSTALLFLAG_FORCE bug where
    /// UpdateDriverForPlugAndPlayDevicesW re-binds ALL existing nodes.
    /// </summary>
    static int CmdIncremental(string[] args)
    {
        int totalDevices = 3;
        if (args.Length > 1 && int.TryParse(args[1], out int n) && n >= 1 && n <= 16)
            totalDevices = n;

        var layout = new DeviceLayout { Axes = 6, Buttons = 11, Povs = 1 };
        Console.WriteLine($"=== INCREMENTAL TEST: Add {totalDevices} devices one at a time with full rebuild (6/11/1) ===\n");
        Console.WriteLine("This mirrors PadForge's pattern: relinquish all → remove all → recreate batch → re-acquire.\n");

        // Clean slate
        RemoveAllVJoyNodes();
        Thread.Sleep(2000);

        if (!TryLoadDll())
            return Error("Cannot load vJoyInterface.dll");

        var acquiredDevices = new List<uint>();
        bool allOk = true;

        for (int step = 1; step <= totalDevices; step++)
        {
            Console.WriteLine($"─── Step {step}: Add device #{step} (rebuild with {step} total nodes) ───");

            // Step A: Relinquish all existing devices (like PadForge's CreateVJoyController)
            if (acquiredDevices.Count > 0)
            {
                Console.Write($"  Relinquishing {acquiredDevices.Count} existing device(s)... ");
                foreach (uint id in acquiredDevices)
                {
                    ResetVJD(id);
                    RelinquishVJD(id);
                }
                Console.WriteLine("OK");
            }

            // Step B: Remove ALL existing nodes
            if (step > 1)
            {
                Console.Write($"  Removing all existing nodes... ");
                RemoveAllVJoyNodes();
                Console.WriteLine("OK");
                Thread.Sleep(500);
            }

            // Step C: Write registry for the full set
            WriteRegistryConfig(step, layout);

            // Step D: Create ALL nodes in one batch
            Console.Write($"  Creating {step} node(s) in one batch... ");
            if (!CreateDeviceNodes(step))
            {
                Console.Error.WriteLine("FAIL");
                allOk = false;
                break;
            }
            Console.WriteLine("OK");

            // Step E: Wait for PnP
            if (!WaitForDevices(step, timeout: TimeSpan.FromSeconds(10)))
            {
                Console.Error.WriteLine($"  FAIL: PnP binding timeout for {step} devices");
                allOk = false;
                break;
            }

            // Step F: Re-acquire ALL devices (existing + new)
            acquiredDevices.Clear();
            for (uint id = 1; id <= 16 && acquiredDevices.Count < step; id++)
            {
                int status = GetVJDStatus(id);
                if (status == VJD_STAT_FREE)
                {
                    if (AcquireVJD(id))
                    {
                        ResetVJD(id);
                        acquiredDevices.Add(id);
                        Console.WriteLine($"  Acquired device {id}");
                    }
                }
            }
            if (acquiredDevices.Count < step)
            {
                Console.Error.WriteLine($"  FAIL: Only acquired {acquiredDevices.Count}/{step} devices");
                allOk = false;
                break;
            }

            // Step G: Verify ALL devices work with UpdateVJD
            Console.Write($"  Verifying all {acquiredDevices.Count} device(s)... ");
            bool allWork = true;
            foreach (uint id in acquiredDevices)
            {
                int testVal = (int)(id * 5000);
                var pos = new JOYSTICK_POSITION_V2
                {
                    bDevice = (byte)id,
                    wAxisX = testVal,
                    wAxisY = 16383,
                    bHats = 0xFFFF_FFFFu,
                    bHatsEx1 = 0xFFFF_FFFFu,
                    bHatsEx2 = 0xFFFF_FFFFu,
                    bHatsEx3 = 0xFFFF_FFFFu,
                };
                if (!UpdateVJD_V2(id, ref pos))
                {
                    Console.Write($"[Dev{id}:UpdateVJD FAILED] ");
                    allWork = false;
                }
            }
            Console.WriteLine(allWork ? "OK" : "FAIL");

            if (!allWork)
            {
                allOk = false;
                break;
            }

            // Check device count in PnP
            var nodeIds = EnumerateVJoyInstanceIds();
            Console.WriteLine($"  PnP nodes: {nodeIds.Count} (expected {step})");
            if (nodeIds.Count != step)
            {
                Console.Error.WriteLine($"  FAIL: Expected {step} nodes, found {nodeIds.Count}");
                allOk = false;
                break;
            }

            Console.WriteLine($"  Step {step}: PASS\n");
        }

        // Feed all devices for 2 seconds to verify sustained output
        if (allOk && acquiredDevices.Count >= 2)
        {
            Console.WriteLine($"─── Sustained feed test: {acquiredDevices.Count} devices for 2 seconds ───");
            var sw = Stopwatch.StartNew();
            int frames = 0;
            while (sw.ElapsedMilliseconds < 2000)
            {
                foreach (uint id in acquiredDevices)
                {
                    int t = (int)(sw.ElapsedMilliseconds % 32767);
                    var pos = new JOYSTICK_POSITION_V2
                    {
                        bDevice = (byte)id,
                        wAxisX = t,
                        wAxisY = 32767 - t,
                        bHats = 0xFFFF_FFFFu,
                        bHatsEx1 = 0xFFFF_FFFFu,
                        bHatsEx2 = 0xFFFF_FFFFu,
                        bHatsEx3 = 0xFFFF_FFFFu,
                    };
                    UpdateVJD_V2(id, ref pos);
                }
                frames++;
                Thread.Sleep(1);
            }
            double fps = frames / (sw.ElapsedMilliseconds / 1000.0);
            Console.WriteLine($"  {frames} frames in {sw.ElapsedMilliseconds}ms = {fps:F1} fps");

            // Check WinMM sees the right number of devices
            var winmmDevs = EnumerateWinMMDevices();
            Console.WriteLine($"  WinMM responding devices: {winmmDevs.Count}");
        }

        // Cleanup
        Console.WriteLine("\n─── Cleanup ───");
        foreach (uint id in acquiredDevices)
        {
            ResetVJD(id);
            RelinquishVJD(id);
        }
        RemoveAllVJoyNodes();

        Console.WriteLine($"\n=== INCREMENTAL TEST: {(allOk ? "PASS" : "FAIL")} ===");
        return allOk ? 0 : 1;
    }

    /// <summary>
    /// Enumerates which WinMM joystick IDs are currently responding.
    /// </summary>
    static HashSet<int> EnumerateWinMMDevices()
    {
        var result = new HashSet<int>();
        int numDevs = joyGetNumDevs();
        for (int i = 0; i < Math.Min(numDevs, 16); i++)
        {
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
            if (joyGetPosEx(i, ref info) == JOYERR_NOERROR)
                result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// Reads joystick state for a specific WinMM joystick ID.
    /// </summary>
    static (bool ok, int x, int y, int z, int r, int u, int v, int buttons, int pov) ReadWinMMDevice(int joyId)
    {
        var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
        if (joyGetPosEx(joyId, ref info) == JOYERR_NOERROR)
            return (true, info.dwXpos, info.dwYpos, info.dwZpos, info.dwRpos, info.dwUpos, info.dwVpos, info.dwButtons, info.dwPOV);
        return (false, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Checks UpperFilters on ROOT\HIDCLASS device nodes via registry.
    /// </summary>
    static string GetDeviceNodeUpperFilters()
    {
        try
        {
            // Check all ROOT\HIDCLASS\NNNN entries
            using var enumKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\ROOT\HIDCLASS");
            if (enumKey == null) return "";

            foreach (string subName in enumKey.GetSubKeyNames())
            {
                using var devKey = enumKey.OpenSubKey(subName);
                if (devKey == null) continue;

                string desc = devKey.GetValue("DeviceDesc") as string ?? "";
                if (desc.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // Also check hardware ID
                    string[] hwids = devKey.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
                    bool isVjoy = false;
                    foreach (var hwid in hwids)
                        if (hwid.Contains("VID_1234", StringComparison.OrdinalIgnoreCase))
                            isVjoy = true;
                    if (!isVjoy) continue;
                }

                // Found a vJoy node — check UpperFilters
                string[] filters = devKey.GetValue("UpperFilters") as string[] ?? Array.Empty<string>();
                return string.Join(",", filters);
            }
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }
        return "";
    }

    static void RemoveAllVJoyNodes()
    {
        // Use pnputil to remove all vJoy nodes
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = "/enum-devices /class HIDClass",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5_000);

        string currentId = null;
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.IndexOf("Instance ID", StringComparison.OrdinalIgnoreCase) >= 0 && line.Contains(":"))
                currentId = line.Substring(line.IndexOf(':') + 1).Trim();
            else if (currentId != null && line.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) >= 0
                     && currentId.StartsWith("ROOT\\HIDCLASS\\", StringComparison.OrdinalIgnoreCase))
            {
                var rmPsi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/remove-device \"{currentId}\" /subtree",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var rmProc = Process.Start(rmPsi);
                rmProc?.WaitForExit(5_000);
                currentId = null;
            }
            else if (string.IsNullOrEmpty(line))
                currentId = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Diagnostic: single device, deep investigation of data flow
    // ─────────────────────────────────────────────────────────────────────

    static int CmdDiag()
    {
        Console.WriteLine("=== DIAGNOSTIC: Deep investigation of vJoy data flow ===\n");

        // Step 1: Clean slate
        Console.WriteLine("[1] Cleaning up existing nodes...");
        RemoveAllVJoyNodes();
        Thread.Sleep(1000);

        // Step 2: Snapshot WinMM before
        var beforeWinMM = EnumerateWinMMDevices();
        Console.WriteLine($"[2] WinMM devices before: [{string.Join(",", beforeWinMM.OrderBy(x => x))}]");

        // Step 3: Write registry (standard 6/11/1 config)
        Console.WriteLine("[3] Writing registry config (6 axes, 11 buttons, 1 POV)...");
        var layout = new DeviceLayout { Axes = 6, Buttons = 11, Povs = 1 };
        WriteRegistryConfig(1, layout);

        // Step 4: Create device node
        Console.WriteLine("[4] Creating device node...");
        if (!CreateDeviceNodes(1))
        {
            Console.Error.WriteLine("FATAL: Failed to create device node.");
            return 1;
        }

        // Step 5: Wait for PnP
        Console.WriteLine("[5] Waiting for PnP...");
        if (!WaitForDevices(1, timeout: TimeSpan.FromSeconds(15)))
        {
            Console.Error.WriteLine("FATAL: PnP binding timeout.");
            return 1;
        }

        // Step 6: Check UpperFilters
        Console.Write("[6] UpperFilters: ");
        string uf = GetDeviceNodeUpperFilters();
        Console.WriteLine(string.IsNullOrEmpty(uf) ? "MISSING!" : uf);

        // Step 7: Check device node registry in detail
        Console.WriteLine("[7] Device node registry:");
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\HIDCLASS");
            if (enumKey != null)
            {
                foreach (string sub in enumKey.GetSubKeyNames())
                {
                    using var dk = enumKey.OpenSubKey(sub);
                    if (dk == null) continue;
                    string[] hwids = dk.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
                    bool isVjoy = hwids.Any(h => h.Contains("VID_1234", StringComparison.OrdinalIgnoreCase));
                    if (!isVjoy) continue;
                    Console.WriteLine($"    {sub}:");
                    Console.WriteLine($"      HardwareID: {string.Join("; ", hwids)}");
                    Console.WriteLine($"      Service: {dk.GetValue("Service")}");
                    Console.WriteLine($"      UpperFilters: {string.Join(",", dk.GetValue("UpperFilters") as string[] ?? Array.Empty<string>())}");
                    Console.WriteLine($"      DeviceDesc: {dk.GetValue("DeviceDesc")}");
                    Console.WriteLine($"      ClassGUID: {dk.GetValue("ClassGUID")}");
                    Console.WriteLine($"      Driver: {dk.GetValue("Driver")}");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"    Error: {ex.Message}"); }

        // Step 8: Acquire
        Console.WriteLine("[8] Acquiring device...");
        uint devId = 0;
        for (uint id = 1; id <= 16; id++)
        {
            int status = GetVJDStatus(id);
            Console.WriteLine($"    Device {id}: status={StatusName(status)}");
            if (status == VJD_STAT_FREE && devId == 0)
            {
                if (AcquireVJD(id))
                {
                    devId = id;
                    ResetVJD(id);
                    Console.WriteLine($"    Acquired device {id}");
                }
                else
                    Console.WriteLine($"    Failed to acquire device {id}");
            }
            if (status == VJD_STAT_MISS) break;
        }

        if (devId == 0)
        {
            Console.Error.WriteLine("FATAL: No device acquired.");
            return 1;
        }

        // Step 9: Print capabilities
        long maxVal = 0;
        GetVJDAxisMax(devId, HID_USAGE_X, ref maxVal);
        int axisMax = (int)maxVal;
        Console.WriteLine($"[9] Caps: buttons={GetVJDButtonNumber(devId)} contPovs={GetVJDContPovNumber(devId)} discPovs={GetVJDDiscPovNumber(devId)} axisMax={axisMax}");

        // Step 10: WinMM check (before feeding)
        Thread.Sleep(500);
        var afterWinMM = EnumerateWinMMDevices();
        var newDevs = new HashSet<int>(afterWinMM);
        newDevs.ExceptWith(beforeWinMM);
        Console.WriteLine($"[10] WinMM devices after: [{string.Join(",", afterWinMM.OrderBy(x => x))}]  new: [{string.Join(",", newDevs.OrderBy(x => x))}]");

        // Step 11: Set known axis values and check WinMM readback
        Console.WriteLine("[11] Testing data flow (SetAxis)...");
        int testX = axisMax * 3 / 4;
        int testY = axisMax / 4;

        // Set values
        bool setOk = true;
        setOk &= SetAxis(testX, devId, HID_USAGE_X);
        setOk &= SetAxis(testY, devId, HID_USAGE_Y);
        setOk &= SetBtn(true, devId, 1);
        Console.WriteLine($"    SetAxis/SetBtn result: {(setOk ? "OK" : "FAIL")}");
        Console.WriteLine($"    Sent: X={testX} Y={testY} Btn1=pressed");

        Thread.Sleep(100);

        // Read back from ALL WinMM devices
        Console.WriteLine("    WinMM readback (all devices):");
        foreach (int joyId in afterWinMM.OrderBy(x => x))
        {
            var r = ReadWinMMDevice(joyId);
            Console.WriteLine($"      joyID={joyId}: X={r.x} Y={r.y} Z={r.z} R={r.r} U={r.u} V={r.v} Btns=0x{r.buttons:X} POV={r.pov}");
        }

        // Step 12: Now test with UpdateVJD V2
        Console.WriteLine("[12] Testing data flow (UpdateVJD V2)...");
        var pos = new JOYSTICK_POSITION_V2 { bDevice = (byte)devId };
        pos.wAxisX = axisMax / 5;  // Very different from above
        pos.wAxisY = axisMax * 4 / 5;
        pos.lButtons = 0b111; // Buttons 1-3
        pos.bHats = 9000; // POV Right (continuous: 90°)
        bool v2Ok = UpdateVJD_V2(devId, ref pos);
        Console.WriteLine($"    UpdateVJD V2 result: {(v2Ok ? "OK" : "FAIL")}");
        Console.WriteLine($"    Sent: X={pos.wAxisX} Y={pos.wAxisY} Btns=0x{pos.lButtons:X} POV=1");

        Thread.Sleep(100);

        Console.WriteLine("    WinMM readback (all devices):");
        foreach (int joyId in afterWinMM.OrderBy(x => x))
        {
            var r = ReadWinMMDevice(joyId);
            Console.WriteLine($"      joyID={joyId}: X={r.x} Y={r.y} Z={r.z} R={r.r} U={r.u} V={r.v} Btns=0x{r.buttons:X} POV={r.pov}");
        }

        // Step 13: Pump data rapidly for 2 seconds, then check
        Console.WriteLine("[13] Pumping data for 2 seconds...");
        var sw = Stopwatch.StartNew();
        int frameCount = 0;
        while (sw.ElapsedMilliseconds < 2000)
        {
            int x = (frameCount * 100) % (axisMax + 1);
            SetAxis(x, devId, HID_USAGE_X);
            SetAxis(axisMax - x, devId, HID_USAGE_Y);
            frameCount++;
            Thread.Sleep(1);
        }
        Console.WriteLine($"    Pumped {frameCount} frames in {sw.ElapsedMilliseconds}ms");

        Console.WriteLine("    WinMM readback after pump:");
        foreach (int joyId in afterWinMM.OrderBy(x => x))
        {
            var r = ReadWinMMDevice(joyId);
            Console.WriteLine($"      joyID={joyId}: X={r.x} Y={r.y} Z={r.z} R={r.r} U={r.u} V={r.v} Btns=0x{r.buttons:X} POV={r.pov}");
        }

        // Cleanup
        Console.WriteLine("\n[14] Cleanup...");
        RelinquishVJD(devId);
        RemoveAllVJoyNodes();

        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Feed loop — drives all devices with sweeping test data
    // ─────────────────────────────────────────────────────────────────────

    static void FeedLoop(List<uint> deviceIds, bool useUpdateVjd, bool useV3)
    {
        // Per-device state: slightly offset phases so each device moves differently.
        int[] xVals = new int[deviceIds.Count];
        int[] yVals = new int[deviceIds.Count];

        // Get axis max (assume all devices share the same max).
        long maxVal = 0;
        GetVJDAxisMax(deviceIds[0], HID_USAGE_X, ref maxVal);
        int axisMax = (int)maxVal;
        if (axisMax <= 0) axisMax = 32767;

        uint frameCount = 0;
        var sw = Stopwatch.StartNew();

        while (!_stopping)
        {
            for (int d = 0; d < deviceIds.Count; d++)
            {
                uint id = deviceIds[d];
                int offset = d * 3000; // Phase offset per device

                int x = (xVals[d] + 200 + offset) % (axisMax + 1);
                int y = (yVals[d] + 300 + offset) % (axisMax + 1);
                xVals[d] = x;
                yVals[d] = y;

                int buttons = GetVJDButtonNumber(id);
                int povs = Math.Max(GetVJDContPovNumber(id), GetVJDDiscPovNumber(id));

                byte activeBtn = (byte)(1 + (frameCount / 25) % (uint)Math.Max(1, buttons));
                // Continuous POV: cycle through 8 directions + centered.
                // 0=N, 4500=NE, 9000=E, 13500=SE, 18000=S, 22500=SW, 27000=W, 31500=NW, -1=centered
                int[] povDirs = { -1, 0, 4500, 9000, 13500, 18000, 22500, 27000, 31500 };
                int povVal = povs > 0 ? povDirs[frameCount / 50 % 9] : -1;

                if (useUpdateVjd)
                {
                    if (useV3)
                    {
                        var pos = new JOYSTICK_POSITION_V3 { bDevice = (byte)id };
                        pos.wAxisX = x;
                        pos.wAxisY = y;
                        pos.wAxisZ = axisMax / 2;
                        pos.wAxisXRot = axisMax - x;
                        pos.wAxisYRot = axisMax - y;
                        pos.wAxisZRot = axisMax / 2;
                        // Set button bitmask
                        if (activeBtn >= 1 && activeBtn <= 32)
                            pos.lButtons = 1 << (activeBtn - 1);
                        // Continuous POV: degree value × 100, or 0xFFFFFFFF for centered
                        pos.bHats = povVal < 0 ? 0xFFFFFFFFu : (uint)povVal;
                        UpdateVJD_V3(id, ref pos);
                    }
                    else
                    {
                        var pos = new JOYSTICK_POSITION_V2 { bDevice = (byte)id };
                        pos.wAxisX = x;
                        pos.wAxisY = y;
                        pos.wAxisZ = axisMax / 2;
                        pos.wAxisXRot = axisMax - x;
                        pos.wAxisYRot = axisMax - y;
                        pos.wAxisZRot = axisMax / 2;
                        if (activeBtn >= 1 && activeBtn <= 32)
                            pos.lButtons = 1 << (activeBtn - 1);
                        pos.bHats = povVal < 0 ? 0xFFFFFFFFu : (uint)povVal;
                        UpdateVJD_V2(id, ref pos);
                    }
                }
                else
                {
                    // Individual calls
                    if (GetVJDAxisExist(id, HID_USAGE_X))  SetAxis(x, id, HID_USAGE_X);
                    if (GetVJDAxisExist(id, HID_USAGE_Y))  SetAxis(y, id, HID_USAGE_Y);
                    if (GetVJDAxisExist(id, HID_USAGE_Z))  SetAxis(axisMax / 2, id, HID_USAGE_Z);
                    if (GetVJDAxisExist(id, HID_USAGE_RX)) SetAxis(axisMax - x, id, HID_USAGE_RX);
                    if (GetVJDAxisExist(id, HID_USAGE_RY)) SetAxis(axisMax - y, id, HID_USAGE_RY);
                    if (GetVJDAxisExist(id, HID_USAGE_RZ)) SetAxis(axisMax / 2, id, HID_USAGE_RZ);

                    for (byte b = 1; b <= buttons && b <= 128; b++)
                        SetBtn(b == activeBtn, id, b);

                    for (byte p = 1; p <= povs && p <= 4; p++)
                        SetContPov(povVal, id, p);
                }
            }

            frameCount++;

            if (frameCount % 50 == 0)
            {
                double fps = frameCount / sw.Elapsed.TotalSeconds;
                Console.Write($"\r  Frame {frameCount}: ");
                for (int d = 0; d < deviceIds.Count; d++)
                    Console.Write($"[Dev{deviceIds[d]} X={xVals[d],5} Y={yVals[d],5}] ");
                Console.Write($"  {fps:F1} fps  ");
            }

            Thread.Sleep(16); // ~60 fps
        }

        Console.WriteLine();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Targeted tests for the full command
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Quick verification of individual SetAxis/SetBtn/SetDiscPov calls.
    /// Sets each axis to mid, presses button 1, sets POV to up, then resets.
    /// </summary>
    static bool TestIndividualCalls(List<uint> deviceIds)
    {
        bool allOk = true;
        foreach (uint id in deviceIds)
        {
            long maxVal = 0;
            GetVJDAxisMax(id, HID_USAGE_X, ref maxVal);
            int mid = (int)(maxVal / 2);

            for (int i = 0; i < AllAxes.Length; i++)
            {
                if (GetVJDAxisExist(id, AllAxes[i]))
                {
                    bool ok = SetAxis(mid, id, AllAxes[i]);
                    if (!ok) { Console.WriteLine($"    SetAxis({AxisNames[i]}, dev{id}) FAILED"); allOk = false; }
                }
            }

            int btns = GetVJDButtonNumber(id);
            for (byte b = 1; b <= btns && b <= 128; b++)
            {
                bool ok = SetBtn(b == 1, id, b); // Press button 1 only
                if (!ok) { Console.WriteLine($"    SetBtn({b}, dev{id}) FAILED"); allOk = false; }
            }

            int povs = Math.Max(GetVJDContPovNumber(id), GetVJDDiscPovNumber(id));
            for (byte p = 1; p <= povs && p <= 4; p++)
            {
                bool ok = SetContPov(0, id, p); // POV Up (0 degrees)
                if (!ok) { Console.WriteLine($"    SetContPov({p}, dev{id}) FAILED"); allOk = false; }
            }

            ResetVJD(id);
        }
        return allOk;
    }

    /// <summary>
    /// Quick verification of UpdateVJD with the V2 struct.
    /// </summary>
    static bool TestUpdateVjdV2(List<uint> deviceIds)
    {
        bool allOk = true;
        foreach (uint id in deviceIds)
        {
            long maxVal = 0;
            GetVJDAxisMax(id, HID_USAGE_X, ref maxVal);
            int mid = (int)(maxVal / 2);

            var pos = new JOYSTICK_POSITION_V2
            {
                bDevice = (byte)id,
                wAxisX = mid,
                wAxisY = mid,
                wAxisZ = mid,
                wAxisXRot = mid,
                wAxisYRot = mid,
                wAxisZRot = mid,
                lButtons = 0x01, // Button 1 pressed
                bHats = 0,       // POV Up (continuous: 0°)
            };

            bool ok = UpdateVJD_V2(id, ref pos);
            if (!ok) { Console.WriteLine($"    UpdateVJD V2 (dev{id}) FAILED"); allOk = false; }
            else Console.WriteLine($"    UpdateVJD V2 (dev{id}) OK");

            ResetVJD(id);
        }
        return allOk;
    }

    /// <summary>
    /// Quick verification of UpdateVJD with the V3 struct.
    /// </summary>
    static bool TestUpdateVjdV3(List<uint> deviceIds)
    {
        bool allOk = true;
        foreach (uint id in deviceIds)
        {
            long maxVal = 0;
            GetVJDAxisMax(id, HID_USAGE_X, ref maxVal);
            int mid = (int)(maxVal / 2);

            var pos = new JOYSTICK_POSITION_V3
            {
                bDevice = (byte)id,
                wAxisX = mid,
                wAxisY = mid,
                wAxisZ = mid,
                wAxisXRot = mid,
                wAxisYRot = mid,
                wAxisZRot = mid,
                lButtons = 0x01,
                bHats = 0,
            };

            bool ok = UpdateVJD_V3(id, ref pos);
            if (!ok) { Console.WriteLine($"    UpdateVJD V3 (dev{id}) FAILED"); allOk = false; }
            else Console.WriteLine($"    UpdateVJD V3 (dev{id}) OK");

            ResetVJD(id);
        }
        return allOk;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Cleanup — relinquish devices then remove device nodes
    // ─────────────────────────────────────────────────────────────────────

    static void Cleanup(List<uint> deviceIds)
    {
        Console.WriteLine("\n[Cleanup] Relinquishing devices...");
        foreach (uint id in deviceIds)
        {
            try
            {
                ResetVJD(id);
                RelinquishVJD(id);
                Console.WriteLine($"  Relinquished device {id}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Failed to relinquish device {id}: {ex.Message}");
            }
        }

        Console.WriteLine("[Cleanup] Removing device nodes...");
        var instanceIds = EnumerateVJoyInstanceIds();
        int removed = 0;
        foreach (var iid in instanceIds)
        {
            if (RemoveDeviceNode(iid))
            {
                Console.WriteLine($"  Removed {iid}");
                removed++;
            }
            else
            {
                Console.WriteLine($"  Failed to remove {iid}");
            }
        }
        Console.WriteLine($"[Cleanup] Removed {removed}/{instanceIds.Count} device node(s).");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Registry: write HID report descriptor for each device
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes vJoy device configuration to the registry.
    /// The driver reads HidReportDescriptor (correctly spelled in the compiled binary)
    /// from HKLM\SYSTEM\CurrentControlSet\services\vjoy\Parameters\DeviceNN.
    /// NOTE: The vjoy.h source has misspelled names (HidReportDesctiptor), but
    /// the compiled vjoy.sys binary uses the correctly-spelled names.
    /// Must be called BEFORE device nodes are created / driver is loaded.
    /// </summary>
    static void WriteRegistryConfig(int count, DeviceLayout layout = default)
    {
        try
        {
            // Ensure the service key exists for the driver to load.
            using (var svcKey = Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\services\vjoy"))
            {
                // Only write service config if it doesn't exist — don't clobber
                // an existing driver installation.
                if (svcKey.GetValue("Type") == null)
                {
                    svcKey.SetValue("Type", 1, RegistryValueKind.DWord);           // SERVICE_KERNEL_DRIVER
                    svcKey.SetValue("Start", 3, RegistryValueKind.DWord);          // SERVICE_DEMAND_START
                    svcKey.SetValue("ErrorControl", 0, RegistryValueKind.DWord);
                    svcKey.SetValue("ImagePath", @"System32\DRIVERS\vjoy.sys", RegistryValueKind.ExpandString);
                    Console.WriteLine("  Created vjoy service registry key.");
                }
            }

            // Ensure vjoy.sys is in the drivers directory.
            string vjoyDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            string srcSys = System.IO.Path.Combine(vjoyDir, "vjoy.sys");
            string dstSys = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "vjoy.sys");
            if (!System.IO.File.Exists(dstSys) && System.IO.File.Exists(srcSys))
            {
                System.IO.File.Copy(srcSys, dstSys, overwrite: false);
                Console.WriteLine($"  Copied vjoy.sys to {dstSys}");
            }

            // Write per-device HID report descriptors to registry.
            // The descriptor must match the driver's fixed 97-byte report layout:
            // 16 axes × 32-bit + 4 POV DWORDs + 128 button bits = 96 bytes + 1 report ID.
            // Disabled axes/POVs/buttons are emitted as constant padding in the descriptor.
            using var baseKey = Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\services\vjoy\Parameters");

            // Clean any existing DeviceNN keys first
            foreach (string subKeyName in baseKey.GetSubKeyNames())
            {
                if (subKeyName.StartsWith("Device", StringComparison.OrdinalIgnoreCase))
                {
                    try { baseKey.DeleteSubKeyTree(subKeyName, false); } catch { }
                }
            }

            // Write descriptor for each device (Device01..DeviceNN)
            for (int i = 1; i <= count; i++)
            {
                byte[] descriptor = BuildHidDescriptor((byte)i, layout);
                string keyName = $"Device{i:D2}";
                using var devKey = baseKey.CreateSubKey(keyName);
                devKey.SetValue("HidReportDescriptor", descriptor, RegistryValueKind.Binary);
                devKey.SetValue("HidReportDescriptorSize", descriptor.Length, RegistryValueKind.DWord);
                Console.WriteLine($"  Wrote {keyName}: {descriptor.Length} bytes ({layout})");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to write registry config: {ex.Message}");
            Console.Error.WriteLine("Are you running as Administrator?");
        }
    }

    /// <summary>
    /// Builds a HID Report Descriptor matching the vJoyConf format exactly.
    /// The report always has a fixed 97-byte layout:
    ///   1 byte report ID + 16 axes × 4 bytes + 4 POV DWORDs + 128 button bits (16 bytes)
    /// Disabled axes/POVs/buttons are emitted as constant padding so the byte offsets
    /// always match the driver's HID_INPUT_REPORT struct.
    /// </summary>
    static byte[] BuildHidDescriptor(byte reportId, DeviceLayout layout)
    {
        int nAxes = Math.Clamp(layout.Axes, 0, 6);
        int nButtons = Math.Clamp(layout.Buttons, 0, 128);
        int nPovs = Math.Clamp(layout.Povs, 0, 4);

        // All 16 axis HID usages in vJoyConf order
        byte[] axisUsages = {
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35,   // X, Y, Z, RX, RY, RZ
            0x36, 0x37, 0x38,                       // Slider, Dial, Wheel
            0xC4, 0xC5, 0xC6, 0xC8,                // Accelerator, Brake, Clutch, Steering
            0xB0, 0xBA, 0xBB                        // Aileron, Rudder, Throttle
        };

        var d = new List<byte>();

        // ── Outer header ──
        d.AddRange(new byte[] { 0x05, 0x01 });         // USAGE_PAGE (Generic Desktop)
        d.AddRange(new byte[] { 0x15, 0x00 });         // LOGICAL_MINIMUM (0)
        d.AddRange(new byte[] { 0x09, 0x04 });         // USAGE (Joystick)
        d.AddRange(new byte[] { 0xA1, 0x01 });         // COLLECTION (Application)

        // ── Axes collection ──
        d.AddRange(new byte[] { 0x05, 0x01 });         //   USAGE_PAGE (Generic Desktop)
        d.AddRange(new byte[] { 0x85, reportId });      //   REPORT_ID
        d.AddRange(new byte[] { 0x09, 0x01 });         //   USAGE (Pointer)
        d.AddRange(new byte[] { 0x15, 0x00 });         //   LOGICAL_MINIMUM (0)
        d.AddRange(new byte[] { 0x26, 0xFF, 0x7F });   //   LOGICAL_MAXIMUM (32767)
        d.AddRange(new byte[] { 0x75, 0x20 });         //   REPORT_SIZE (32)
        d.AddRange(new byte[] { 0x95, 0x01 });         //   REPORT_COUNT (1)
        d.AddRange(new byte[] { 0xA1, 0x00 });         //   COLLECTION (Physical)

        // All 16 axes — enabled ones get Usage + Input(Data), disabled ones get Input(Cnst)
        for (int i = 0; i < 16; i++)
        {
            if (i < nAxes)
            {
                d.AddRange(new byte[] { 0x09, axisUsages[i] });  // USAGE (axis)
                d.AddRange(new byte[] { 0x81, 0x02 });           // INPUT (Data, Var, Abs)
            }
            else
            {
                d.AddRange(new byte[] { 0x81, 0x01 });           // INPUT (Cnst, Ary, Abs)
            }
        }

        d.Add(0xC0);                                    //   END_COLLECTION (Physical)

        // ── Continuous POV hats — always 128 bits (4 × 32-bit DWORDs) ──
        // Continuous POV uses degree values × 100 (0–35900), enabling 8-way diagonals.
        if (nPovs > 0)
        {
            d.AddRange(new byte[] { 0x15, 0x00 });                     // LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x27, 0x3C, 0x8C, 0x00, 0x00 });  // LOGICAL_MAXIMUM (35900)
            d.AddRange(new byte[] { 0x35, 0x00 });                     // PHYSICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x47, 0x3C, 0x8C, 0x00, 0x00 });  // PHYSICAL_MAXIMUM (35900)
            d.AddRange(new byte[] { 0x65, 0x14 });                     // UNIT (Eng Rot:Angular Pos)
            d.AddRange(new byte[] { 0x75, 0x20 });                     // REPORT_SIZE (32)
            d.AddRange(new byte[] { 0x95, 0x01 });                     // REPORT_COUNT (1)

            // Active POVs
            for (int p = 0; p < nPovs; p++)
            {
                d.AddRange(new byte[] { 0x09, 0x39 });     // USAGE (Hat Switch)
                d.AddRange(new byte[] { 0x81, 0x02 });     // INPUT (Data, Var, Abs)
            }

            // Padding DWORDs to fill 4 total (128 bits)
            if (nPovs < 4)
            {
                d.AddRange(new byte[] { 0x95, (byte)(4 - nPovs) });  // REPORT_COUNT (4-nPovs)
                d.AddRange(new byte[] { 0x81, 0x01 });                // INPUT (Cnst, Ary, Abs)
            }
        }
        else
        {
            // No POVs: 4 × 32-bit constant padding (128 bits)
            d.AddRange(new byte[] { 0x75, 0x20 });         // REPORT_SIZE (32)
            d.AddRange(new byte[] { 0x95, 0x04 });         // REPORT_COUNT (4)
            d.AddRange(new byte[] { 0x81, 0x01 });         // INPUT (Cnst, Ary, Abs)
        }

        // ── Buttons — always 128 bits ──
        byte usageMin = (byte)(nButtons > 0 ? 0x01 : 0x00);
        d.AddRange(new byte[] { 0x05, 0x09 });             // USAGE_PAGE (Button)
        d.AddRange(new byte[] { 0x15, 0x00 });             // LOGICAL_MINIMUM (0)
        d.AddRange(new byte[] { 0x25, 0x01 });             // LOGICAL_MAXIMUM (1)
        d.AddRange(new byte[] { 0x55, 0x00 });             // UNIT_EXPONENT (0)
        d.AddRange(new byte[] { 0x65, 0x00 });             // UNIT (None)
        d.AddRange(new byte[] { 0x19, usageMin });          // USAGE_MINIMUM (1 or 0)
        d.Add(0x29); d.Add((byte)nButtons);                 // USAGE_MAXIMUM (nButtons)
        d.AddRange(new byte[] { 0x75, 0x01 });             // REPORT_SIZE (1)
        d.Add(0x95); d.Add((byte)nButtons);                 // REPORT_COUNT (nButtons)
        d.AddRange(new byte[] { 0x81, 0x02 });             // INPUT (Data, Var, Abs)

        // Padding to fill 128 bits total
        if (nButtons < 128)
        {
            int padBits = 128 - nButtons;
            d.Add(0x75); d.Add((byte)padBits);              // REPORT_SIZE (128-nButtons)
            d.AddRange(new byte[] { 0x95, 0x01 });         // REPORT_COUNT (1)
            d.AddRange(new byte[] { 0x81, 0x01 });         // INPUT (Cnst, Ary, Abs)
        }

        d.Add(0xC0);                                        // END_COLLECTION (Application)
        return d.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  SetupAPI: create device nodes (same approach as PadForge)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates vJoy device nodes via SetupAPI.
    /// Each node gets DICD_GENERATE_ID so Windows picks a unique instance ID.
    /// After registering nodes, calls UpdateDriverForPlugAndPlayDevicesW with
    /// flag=0 (no INSTALLFLAG_FORCE) to bind the driver only to new/unmatched
    /// nodes without re-binding already-bound ones.
    /// </summary>
    static bool CreateDeviceNodes(int count)
    {
        if (count < 1) return true;

        string vjoyDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
        string infPath = System.IO.Path.Combine(vjoyDir, "vjoy.inf");

        if (!System.IO.File.Exists(infPath))
        {
            Console.Error.WriteLine($"ERROR: vjoy.inf not found at {infPath}");
            Console.Error.WriteLine("       Install the vJoy driver first (PadForge Settings or manual install).");
            return false;
        }

        byte[] hwidBytes = Encoding.Unicode.GetBytes(VJOY_HWID + "\0\0");
        int created = 0;

        for (int i = 0; i < count; i++)
        {
            IntPtr dis = SetupDiCreateDeviceInfoList(ref HID_GUID, IntPtr.Zero);
            if (dis == new IntPtr(-1))
            {
                Console.Error.WriteLine($"  SetupDiCreateDeviceInfoList failed: {Marshal.GetLastWin32Error()}");
                continue;
            }

            try
            {
                var did = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };

                // DeviceName must be "HIDClass" (the class name), NOT the hardware ID.
                // Using a backslash in DeviceName causes ERROR_INVALID_DEVINST_NAME (0xE0000205).
                if (!SetupDiCreateDeviceInfoW(dis, "HIDClass", ref HID_GUID,
                    "vJoy Device", IntPtr.Zero, DICD_GENERATE_ID, ref did))
                {
                    Console.Error.WriteLine($"  SetupDiCreateDeviceInfoW failed: 0x{Marshal.GetLastWin32Error():X}");
                    continue;
                }

                if (!SetupDiSetDeviceRegistryPropertyW(dis, ref did, SPDRP_HARDWAREID,
                    hwidBytes, hwidBytes.Length))
                {
                    Console.Error.WriteLine($"  SetupDiSetDeviceRegistryPropertyW failed: 0x{Marshal.GetLastWin32Error():X}");
                    continue;
                }

                if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, dis, ref did))
                {
                    Console.Error.WriteLine($"  SetupDiCallClassInstaller failed: 0x{Marshal.GetLastWin32Error():X}");
                    continue;
                }

                created++;
                Console.WriteLine($"  Created device node {created}/{count}");
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(dis);
            }
        }

        if (created == 0)
        {
            Console.Error.WriteLine("ERROR: No device nodes were created.");
            return false;
        }

        // Bind driver to new device nodes. Flag=0 (no INSTALLFLAG_FORCE) so
        // already-bound devices are left alone — only unmatched nodes get installed.
        // INSTALLFLAG_FORCE (1) would re-bind ALL matching devices, creating duplicate
        // HID children and invalidating existing controller handles.
        Console.Write($"  Binding driver ({infPath})... ");
        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, VJOY_HWID, infPath, 0, out reboot))
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"FAILED (error 0x{err:X})");
            return false;
        }
        Console.WriteLine("OK" + (reboot ? " (reboot required)" : ""));

        Console.WriteLine($"  Created {created} device node(s).");
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PnP enumeration and removal
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates vJoy device instance IDs via pnputil.
    /// Looks for ROOT\HIDCLASS\* devices whose description contains "vJoy".
    /// </summary>
    static List<string> EnumerateVJoyInstanceIds()
    {
        var results = new List<string>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/enum-devices /class HIDClass",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return results;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);

            string currentInstanceId = null;
            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.IndexOf("Instance ID", StringComparison.OrdinalIgnoreCase) >= 0 && line.Contains(":"))
                {
                    currentInstanceId = line.Substring(line.IndexOf(':') + 1).Trim();
                }
                else if (currentInstanceId != null &&
                         line.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         currentInstanceId.StartsWith("ROOT\\HIDCLASS\\", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(currentInstanceId);
                    currentInstanceId = null;
                }
                else if (string.IsNullOrEmpty(line))
                {
                    currentInstanceId = null;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"EnumerateVJoyInstanceIds error: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// Removes a single device node by instance ID via pnputil.
    /// Requires elevation.
    /// </summary>
    static bool RemoveDeviceNode(string instanceId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/remove-device \"{instanceId}\" /subtree",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            // Read all output BEFORE WaitForExit to avoid deadlock.
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            bool exited = proc.WaitForExit(10_000);
            if (!exited)
            {
                Console.Error.WriteLine($"  pnputil timed out for {instanceId}");
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RemoveDeviceNode error: {ex.Message}");
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  DLL loading
    // ─────────────────────────────────────────────────────────────────────

    static bool TryLoadDll()
    {
        // Try default search paths first.
        if (NativeLibrary.TryLoad("vJoyInterface.dll", out _))
            return true;

        // Try vJoy installation directory (root first, then arch subdirectory).
        string vjoyDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
        string dllPath = System.IO.Path.Combine(vjoyDir, "vJoyInterface.dll");
        if (System.IO.File.Exists(dllPath) && NativeLibrary.TryLoad(dllPath, out _))
        {
            Console.WriteLine($"Loaded DLL from: {dllPath}");
            return true;
        }

        string archDll = System.IO.Path.Combine(vjoyDir, "x64", "vJoyInterface.dll");
        if (System.IO.File.Exists(archDll) && NativeLibrary.TryLoad(archDll, out _))
        {
            Console.WriteLine($"Loaded DLL from: {archDll}");
            return true;
        }

        Console.Error.WriteLine("Cannot find vJoyInterface.dll.");
        Console.Error.WriteLine($"Searched: default paths, {dllPath}, {archDll}");
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Wait for devices to become ready after creation
    // ─────────────────────────────────────────────────────────────────────

    static bool WaitForDevices(int expectedCount, TimeSpan timeout)
    {
        if (!TryLoadDll())
            return false;

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (!vJoyEnabled())
                {
                    Thread.Sleep(500);
                    continue;
                }

                int freeCount = 0;
                for (uint id = 1; id <= 16; id++)
                {
                    int status = GetVJDStatus(id);
                    if (status == VJD_STAT_FREE || status == VJD_STAT_OWN)
                        freeCount++;
                }

                if (freeCount >= expectedCount)
                    return true;
            }
            catch (DllNotFoundException)
            {
                // DLL not loadable yet — driver still binding.
            }

            Thread.Sleep(500);
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Argument parsing helpers
    // ─────────────────────────────────────────────────────────────────────

    static DeviceLayout ParseLayout(string[] args, int startIndex)
    {
        var layout = new DeviceLayout { Axes = 6, Buttons = 11, Povs = 1 };

        for (int i = startIndex; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (a.StartsWith("axes=") && int.TryParse(a.Substring(5), out int ax))
                layout.Axes = Math.Clamp(ax, 0, 6);
            else if (a.StartsWith("buttons=") && int.TryParse(a.Substring(8), out int bt))
                layout.Buttons = Math.Clamp(bt, 0, 128);
            else if (a.StartsWith("povs=") && int.TryParse(a.Substring(5), out int pv))
                layout.Povs = Math.Clamp(pv, 0, 4);
        }

        return layout;
    }
}
