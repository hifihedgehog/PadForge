using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    static extern int GetVJDButtonNumber(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern int GetVJDDiscPovNumber(uint rID);

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
    const string VJOY_HWID = "root\\VID_1234&PID_BEAD&REV_0222";

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
            "create" => CmdCreate(args),
            "remove" => CmdRemove(),
            "feed"   => CmdFeed(args),
            "info"   => CmdInfo(),
            "full"   => CmdFull(args),
            _        => Error($"Unknown command: {args[0]}")
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
        Console.WriteLine("Must run elevated (Administrator) for create/remove/full.");
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

        Console.WriteLine($"{"ID",-4} {"Status",-8} {"Axes",-14} {"Btns",-6} {"POVs",-6} {"AxisMax",-10}");
        Console.WriteLine(new string('-', 52));

        for (uint id = 1; id <= 16; id++)
        {
            int status = GetVJDStatus(id);
            string sName = StatusName(status);

            if (status == VJD_STAT_MISS)
            {
                Console.WriteLine($"{id,-4} {sName,-8} {"---",-14} {"---",-6} {"---",-6}");
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
            int povs = GetVJDDiscPovNumber(id);
            long maxVal = 0;
            GetVJDAxisMax(id, HID_USAGE_X, ref maxVal);

            Console.WriteLine($"{id,-4} {sName,-8} {sb,-14} {buttons,-6} {povs,-6} {maxVal,-10}");
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
            int povs = GetVJDDiscPovNumber(id);
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
            int povs = GetVJDDiscPovNumber(id);
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
                int povs = GetVJDDiscPovNumber(id);

                byte activeBtn = (byte)(1 + (frameCount / 25) % (uint)Math.Max(1, buttons));
                int povVal = povs > 0 ? (int)(frameCount / 50 % 5) - 1 : -1; // -1,0,1,2,3

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
                        // Discrete POV packed in low nibble: 0xF=centered, 0-3=direction
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
                        SetDiscPov(povVal, id, p);
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

            int povs = GetVJDDiscPovNumber(id);
            for (byte p = 1; p <= povs && p <= 4; p++)
            {
                bool ok = SetDiscPov(0, id, p); // POV Up
                if (!ok) { Console.WriteLine($"    SetDiscPov({p}, dev{id}) FAILED"); allOk = false; }
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
                bHats = 0,       // POV Up
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
    static void WriteRegistryConfig(int count, DeviceLayout layout)
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

            using var baseKey = Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\services\vjoy\Parameters");

            for (int id = 1; id <= count; id++)
            {
                byte[] descriptor = BuildHidDescriptor((byte)id, layout);
                string subKeyName = $"Device{id:D2}";
                using var devKey = baseKey.CreateSubKey(subKeyName);

                // The compiled vjoy.sys binary uses correctly-spelled names,
                // even though the vjoy.h source header has misspelled versions.
                // Verified by extracting Unicode strings from vjoy.sys.
                devKey.SetValue("HidReportDescriptor", descriptor, RegistryValueKind.Binary);
                devKey.SetValue("HidReportDescriptorSize", descriptor.Length, RegistryValueKind.DWord);

                // Clean up old misspelled keys from previous versions.
                try { devKey.DeleteValue("HidReportDesctiptor", throwOnMissingValue: false); } catch { }
                try { devKey.DeleteValue("HidReportDesctiptorSize", throwOnMissingValue: false); } catch { }

                Console.WriteLine($"  {subKeyName}: {descriptor.Length} byte descriptor ({layout})");
            }

            // Delete stale DeviceNN subkeys beyond 'count'.
            // The vJoy driver reads ALL DeviceNN keys and concatenates their
            // HID descriptors — stale keys cause phantom devices to appear.
            for (int id = count + 1; id <= 16; id++)
            {
                string subKeyName = $"Device{id:D2}";
                try { baseKey.DeleteSubKeyTree(subKeyName, false); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to write registry config: {ex.Message}");
            Console.Error.WriteLine("Are you running as Administrator?");
        }
    }

    /// <summary>
    /// Builds a HID Report Descriptor for a vJoy device with the specified layout.
    /// All axes are 32-bit, range 0-32767 (matching JOYSTICK_POSITION_V3 axis range).
    /// </summary>
    static byte[] BuildHidDescriptor(byte reportId, DeviceLayout layout)
    {
        var d = new List<byte>();

        // Collection: Generic Desktop / Joystick
        d.AddRange(new byte[] { 0x05, 0x01 });             // USAGE_PAGE (Generic Desktop)
        d.AddRange(new byte[] { 0x09, 0x04 });             // USAGE (Joystick)
        d.AddRange(new byte[] { 0xA1, 0x01 });             // COLLECTION (Application)
        d.AddRange(new byte[] { 0x85, reportId });          //   REPORT_ID

        // Axes: each 32-bit, range 0-32767
        if (layout.Axes > 0)
        {
            d.AddRange(new byte[] { 0x15, 0x00 });       //   LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x26, 0xFF, 0x7F }); //   LOGICAL_MAXIMUM (32767)
            d.AddRange(new byte[] { 0x75, 0x20 });       //   REPORT_SIZE (32)
            d.AddRange(new byte[] { 0x95, 0x01 });       //   REPORT_COUNT (1)

            // Usage IDs: X=0x30, Y=0x31, Z=0x32, RX=0x33, RY=0x34, RZ=0x35
            byte[] usages = { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35 };
            for (int i = 0; i < layout.Axes && i < usages.Length; i++)
            {
                d.AddRange(new byte[] { 0x09, usages[i] }); // USAGE (axis)
                d.AddRange(new byte[] { 0x81, 0x02 });      // INPUT (Data, Var, Abs)
            }
        }

        // Buttons
        if (layout.Buttons > 0)
        {
            int btnCount = Math.Min(layout.Buttons, 128);
            d.AddRange(new byte[] { 0x05, 0x09 });       //   USAGE_PAGE (Button)
            d.AddRange(new byte[] { 0x19, 0x01 });       //   USAGE_MINIMUM (1)
            d.Add(0x29); d.Add((byte)btnCount);           //   USAGE_MAXIMUM (N)
            d.AddRange(new byte[] { 0x15, 0x00 });       //   LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x25, 0x01 });       //   LOGICAL_MAXIMUM (1)
            d.AddRange(new byte[] { 0x75, 0x01 });       //   REPORT_SIZE (1)
            d.Add(0x95); d.Add((byte)btnCount);           //   REPORT_COUNT (N)
            d.AddRange(new byte[] { 0x81, 0x02 });       //   INPUT (Data, Var, Abs)

            // Padding to byte boundary
            int padBits = (8 - (btnCount % 8)) % 8;
            if (padBits > 0)
            {
                d.AddRange(new byte[] { 0x75, 0x01 });       //   REPORT_SIZE (1)
                d.Add(0x95); d.Add((byte)padBits);            //   REPORT_COUNT (pad)
                d.AddRange(new byte[] { 0x81, 0x01 });       //   INPUT (Cnst)
            }
        }

        // Discrete POV hat switches
        if (layout.Povs > 0)
        {
            d.AddRange(new byte[] { 0x05, 0x01 });       //   USAGE_PAGE (Generic Desktop)

            for (int p = 0; p < layout.Povs && p < 4; p++)
            {
                d.AddRange(new byte[] { 0x09, 0x39 });       //   USAGE (Hat Switch)
                d.AddRange(new byte[] { 0x15, 0x00 });       //   LOGICAL_MINIMUM (0)
                d.AddRange(new byte[] { 0x25, 0x03 });       //   LOGICAL_MAXIMUM (3)
                d.AddRange(new byte[] { 0x35, 0x00 });       //   PHYSICAL_MINIMUM (0)
                d.AddRange(new byte[] { 0x46, 0x0E, 0x01 }); //   PHYSICAL_MAXIMUM (270)
                d.AddRange(new byte[] { 0x65, 0x14 });       //   UNIT (Eng Rotation: degrees)
                d.AddRange(new byte[] { 0x75, 0x04 });       //   REPORT_SIZE (4)
                d.AddRange(new byte[] { 0x95, 0x01 });       //   REPORT_COUNT (1)
                d.AddRange(new byte[] { 0x81, 0x42 });       //   INPUT (Data, Var, Abs, Null)
                // 4-bit padding per POV
                d.AddRange(new byte[] { 0x75, 0x04 });       //   REPORT_SIZE (4)
                d.AddRange(new byte[] { 0x95, 0x01 });       //   REPORT_COUNT (1)
                d.AddRange(new byte[] { 0x81, 0x01 });       //   INPUT (Cnst)
                d.AddRange(new byte[] { 0x65, 0x00 });       //   UNIT (None)
                d.AddRange(new byte[] { 0x35, 0x00 });       //   PHYSICAL_MINIMUM (0)
                d.AddRange(new byte[] { 0x45, 0x00 });       //   PHYSICAL_MAXIMUM (0)
            }
        }

        d.Add(0xC0); // END_COLLECTION
        return d.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  SetupAPI: create device nodes (same approach as PadForge)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates vJoy device nodes via SetupAPI.
    /// Each node gets DICD_GENERATE_ID so Windows picks a unique instance ID.
    /// After registering nodes, calls UpdateDriverForPlugAndPlayDevicesW to
    /// bind the driver to the hardware ID.
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

        // Bind the vJoy driver to all device nodes with this hardware ID.
        Console.Write($"  Binding driver ({infPath})... ");
        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, VJOY_HWID, infPath, 1, out reboot))
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
