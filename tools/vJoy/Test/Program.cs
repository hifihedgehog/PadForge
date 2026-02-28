using System;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Minimal vJoy test — uses the same raw P/Invoke as PadForge.
/// Tests: DLL load → vJoyEnabled → GetVJDStatus → AcquireVJD → feed axes/buttons → RelinquishVJD
/// </summary>
class Program
{
    const string DLL = "vJoyInterface.dll";

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern bool vJoyEnabled();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern int GetVJDStatus(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern bool AcquireVJD(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern void RelinquishVJD(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern bool ResetVJD(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern bool SetAxis(int value, uint rID, uint axis);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern bool SetBtn(bool value, uint rID, byte nBtn);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern bool SetDiscPov(int value, uint rID, byte nPov);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern int GetVJDButtonNumber(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern int GetVJDDiscPovNumber(uint rID);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern bool GetVJDAxisExist(uint rID, uint axis);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    static extern bool GetVJDAxisMax(uint rID, uint axis, ref long maxVal);

    // VjdStat values
    const int VJD_STAT_OWN = 0;
    const int VJD_STAT_FREE = 1;
    const int VJD_STAT_BUSY = 2;
    const int VJD_STAT_MISS = 3;

    // HID Usage IDs
    const uint HID_USAGE_X = 0x30;
    const uint HID_USAGE_Y = 0x31;
    const uint HID_USAGE_Z = 0x32;
    const uint HID_USAGE_RX = 0x33;
    const uint HID_USAGE_RY = 0x34;
    const uint HID_USAGE_RZ = 0x35;

    static readonly string[] StatusNames = { "OWN", "FREE", "BUSY", "MISS", "UNKNOWN" };

    static void Main(string[] args)
    {
        uint deviceId = 1;
        if (args.Length > 0 && uint.TryParse(args[0], out uint parsed) && parsed >= 1 && parsed <= 16)
            deviceId = parsed;

        Console.WriteLine("=== vJoy P/Invoke Test ===");
        Console.WriteLine($"Target device ID: {deviceId}");
        Console.WriteLine();

        // Step 1: Load DLL
        Console.Write("[1] Loading vJoyInterface.dll... ");
        try
        {
            // Try explicit load from Program Files first
            bool loaded = NativeLibrary.TryLoad(@"C:\Program Files\vJoy\vJoyInterface.dll", out _);
            if (!loaded)
                loaded = NativeLibrary.TryLoad(@"C:\Program Files\vJoy\x64\vJoyInterface.dll", out _);
            Console.WriteLine(loaded ? "OK (preloaded from Program Files)" : "will use default search");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"preload failed: {ex.Message}");
        }

        // Step 2: vJoyEnabled
        Console.Write("[2] Checking vJoyEnabled()... ");
        try
        {
            bool enabled = vJoyEnabled();
            Console.WriteLine(enabled ? "YES" : "NO (driver not loaded or no devices)");
            if (!enabled)
            {
                Console.WriteLine("\n*** vJoy driver is not enabled. This means either:");
                Console.WriteLine("    - The driver is not in the Windows driver store (run pnputil /add-driver vjoy.inf /install)");
                Console.WriteLine("    - No device nodes exist (need to create one via SetupAPI or devcon)");
                Console.WriteLine("    - The driver failed to load\n");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey(true);
                return;
            }
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine("FAILED - DLL not found!");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);
            return;
        }

        // Step 3: GetVJDStatus
        Console.Write($"[3] GetVJDStatus({deviceId})... ");
        int status = GetVJDStatus(deviceId);
        string statusName = status >= 0 && status < StatusNames.Length ? StatusNames[status] : $"UNKNOWN({status})";
        Console.WriteLine(statusName);

        if (status == VJD_STAT_MISS)
        {
            Console.WriteLine($"\n*** Device {deviceId} is MISSING. The driver is loaded but device ID {deviceId} doesn't exist.");
            Console.WriteLine("    This usually means no device node was created for this ID.\n");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);
            return;
        }

        if (status == VJD_STAT_BUSY)
        {
            Console.WriteLine($"\n*** Device {deviceId} is BUSY (owned by another application).\n");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);
            return;
        }

        // Step 4: Query capabilities
        Console.WriteLine($"[4] Device {deviceId} capabilities:");
        bool hasX = GetVJDAxisExist(deviceId, HID_USAGE_X);
        bool hasY = GetVJDAxisExist(deviceId, HID_USAGE_Y);
        bool hasZ = GetVJDAxisExist(deviceId, HID_USAGE_Z);
        bool hasRX = GetVJDAxisExist(deviceId, HID_USAGE_RX);
        bool hasRY = GetVJDAxisExist(deviceId, HID_USAGE_RY);
        bool hasRZ = GetVJDAxisExist(deviceId, HID_USAGE_RZ);
        int buttons = GetVJDButtonNumber(deviceId);
        int discPovs = GetVJDDiscPovNumber(deviceId);
        long maxVal = 0;
        GetVJDAxisMax(deviceId, HID_USAGE_X, ref maxVal);

        Console.WriteLine($"    Axes: X={hasX} Y={hasY} Z={hasZ} RX={hasRX} RY={hasRY} RZ={hasRZ}");
        Console.WriteLine($"    Buttons: {buttons}  Discrete POVs: {discPovs}  Axis max: {maxVal}");

        // Step 5: Acquire
        Console.Write($"[5] AcquireVJD({deviceId})... ");
        if (status == VJD_STAT_FREE)
        {
            bool acquired = AcquireVJD(deviceId);
            Console.WriteLine(acquired ? "OK" : "FAILED");
            if (!acquired)
            {
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey(true);
                return;
            }
        }
        else if (status == VJD_STAT_OWN)
        {
            Console.WriteLine("already owned");
        }

        ResetVJD(deviceId);
        Console.WriteLine($"[6] ResetVJD({deviceId})... OK");

        // Step 6: Feed test data
        Console.WriteLine("\n[7] Feeding test data (open Windows Game Controllers to verify).");
        Console.WriteLine("    Axes will sweep, buttons will cycle. Press Ctrl+C to stop.\n");

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nStopping...");
            ResetVJD(deviceId);
            RelinquishVJD(deviceId);
            Console.WriteLine($"RelinquishVJD({deviceId}) done.");
        };

        int x = 0, y = 0;
        uint count = 0;
        int maxI = (int)maxVal;

        while (true)
        {
            if (hasX) SetAxis(x, deviceId, HID_USAGE_X);
            if (hasY) SetAxis(y, deviceId, HID_USAGE_Y);
            if (hasZ) SetAxis(maxI / 2, deviceId, HID_USAGE_Z);  // center
            if (hasRX) SetAxis(x, deviceId, HID_USAGE_RX);
            if (hasRY) SetAxis(y, deviceId, HID_USAGE_RY);
            if (hasRZ) SetAxis(maxI / 2, deviceId, HID_USAGE_RZ); // center

            // Cycle through buttons
            byte activeBtn = (byte)(1 + (count / 25) % (uint)Math.Max(1, buttons));
            for (byte b = 1; b <= buttons && b <= 16; b++)
                SetBtn(b == activeBtn, deviceId, b);

            // Cycle POV
            if (discPovs > 0)
                SetDiscPov((int)(count / 50 % 5) - 1, deviceId, 1); // -1,0,1,2,3

            x += 200;
            y += 300;
            if (x > maxI) x = 0;
            if (y > maxI) y = 0;
            count++;

            Console.Write($"\r  Cycle {count}: X={x,6} Y={y,6} Btn={activeBtn,2}  ");
            Thread.Sleep(20);
        }
    }
}
