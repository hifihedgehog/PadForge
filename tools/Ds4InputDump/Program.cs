// Ds4InputDump — read raw HID input reports from a Sony controller
// (virtual DS4 / DualSense or a real one) and print decoded fields:
// sticks, triggers, touchpad fingers, gyro, accel, battery. Built to
// verify PadForge's Sony Report 0x01 packer without DS4Windows' AppData
// footprint or DS4MapperTest's profile system.
//
// Usage:
//   dotnet run                              — auto-pick first Sony pad
//   dotnet run -- --vid=054C --pid=09CC     — pick specific VID/PID
//   dotnet run -- --list                    — enumerate, exit
//
// Reads the HID input report directly via CreateFile + ReadFile so the
// decoded output reflects the exact bytes the device is shipping. The
// whole footprint is the build folder; no registry, no AppData, no
// installer.

using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ds4InputDump;

internal static class Program
{
    const ushort SONY_VID = 0x054C;

    static readonly Dictionary<ushort, string> SonyShapes = new()
    {
        { 0x05C4, "ds4" },        // DS4 v1
        { 0x09CC, "ds4" },        // DS4 v2
        { 0x0BA0, "ds4" },        // USB receiver
        { 0x0CE6, "dualsense" },  // DualSense
        { 0x0DF2, "dualsense" },  // DualSense Edge
    };

    static int Main(string[] args)
    {
        ushort wantedVid = 0, wantedPid = 0;
        bool listOnly = false;
        foreach (var arg in args)
        {
            if (arg == "--list") listOnly = true;
            else if (arg.StartsWith("--vid=")) wantedVid = ushort.Parse(arg[6..], NumberStyles.HexNumber);
            else if (arg.StartsWith("--pid=")) wantedPid = ushort.Parse(arg[6..], NumberStyles.HexNumber);
        }

        var devices = EnumerateSonyDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No Sony controller found. Make sure PadForge has a PlayStation slot");
            Console.WriteLine("active, or plug in a real DS4 / DualSense.");
            return 1;
        }

        if (listOnly)
        {
            Console.WriteLine($"Found {devices.Count} Sony controller(s):");
            foreach (var d in devices)
                Console.WriteLine($"  VID=0x{d.Vid:X4} PID=0x{d.Pid:X4} shape={d.Shape}  {d.Path}");
            return 0;
        }

        var target = devices[0];
        if (wantedVid != 0 || wantedPid != 0)
        {
            var match = devices.FirstOrDefault(d =>
                (wantedVid == 0 || d.Vid == wantedVid) &&
                (wantedPid == 0 || d.Pid == wantedPid));
            if (match == null)
            {
                Console.WriteLine($"No device matching VID=0x{wantedVid:X4} PID=0x{wantedPid:X4} found.");
                return 1;
            }
            target = match;
        }

        Console.WriteLine($"Reading from VID=0x{target.Vid:X4} PID=0x{target.Pid:X4} ({target.Shape})");
        Console.WriteLine($"Path: {target.Path}");
        Console.WriteLine("Press any key to exit.\n");

        using var handle = Native.CreateFileW(target.Path,
            0x80000000 /* GENERIC_READ */,
            0x03 /* FILE_SHARE_READ | WRITE */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x80 /* FILE_ATTRIBUTE_NORMAL */, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Console.WriteLine($"CreateFile failed (Win32 error {Marshal.GetLastWin32Error()}). Try elevated.");
            return 1;
        }

        var buf = new byte[128];
        var lastPrint = DateTime.MinValue;
        const int printIntervalMs = 50;
        bool checkKey = !Console.IsInputRedirected;

        while (!(checkKey && Console.KeyAvailable))
        {
            if (!Native.ReadFile(handle, buf, buf.Length, out int read, IntPtr.Zero) || read == 0)
                continue;
            if (buf[0] != 0x01) continue;

            var now = DateTime.Now;
            if ((now - lastPrint).TotalMilliseconds < printIntervalMs) continue;
            lastPrint = now;

            if (checkKey) Console.Clear();
            else Console.WriteLine("--------");
            Console.WriteLine($"VID=0x{target.Vid:X4} PID=0x{target.Pid:X4}  shape={target.Shape}  bytes={read}");
            Console.WriteLine($"{now:HH:mm:ss.fff}");
            Console.WriteLine();

            if (target.Shape == "ds4")
                PrintDs4(buf, read);
            else
                PrintDualSense(buf, read);

            Console.WriteLine();
            Console.WriteLine("Press any key to exit.");
        }

        return 0;
    }

    // DS4 Type 1: Report ID at byte 0, 64 bytes total.
    static void PrintDs4(byte[] r, int len)
    {
        if (len < 60) { Console.WriteLine($"short report ({len} bytes)"); return; }
        int LX = r[1], LY = r[2], RX = r[3], RY = r[4];
        int b1 = r[5], b2 = r[6], b3 = r[7];
        int LT = r[8], RT = r[9];
        int dpad = b1 & 0x0F;
        int gx = (short)(r[13] | (r[14] << 8));
        int gy = (short)(r[15] | (r[16] << 8));
        int gz = (short)(r[17] | (r[18] << 8));
        int ax = (short)(r[19] | (r[20] << 8));
        int ay = (short)(r[21] | (r[22] << 8));
        int az = (short)(r[23] | (r[24] << 8));
        int batRaw = r[30] & 0x0F;
        bool charging = (r[30] & 0x10) != 0;
        int touchPackets = r[33];
        Console.WriteLine($"sticks  L=({LX,3},{LY,3})  R=({RX,3},{RY,3})  triggers L2={LT,3} R2={RT,3}");
        Console.WriteLine($"dpad={dpad}  buttons[5]={b1:X2}  buttons[6]={b2:X2}  buttons[7]={b3:X2}");
        Console.WriteLine($"gyro  P={gx,7} Y={gy,7} R={gz,7}  (raw int16)");
        Console.WriteLine($"accel X={ax,7} Y={ay,7} Z={az,7}  (raw int16)");
        int max = charging ? 11 : 8;
        Console.WriteLine($"battery raw={batRaw,2}/{max}  charging={charging}  ≈ {(batRaw * 100 / max)}%");
        Console.WriteLine($"touch  packets={touchPackets}");
        if (touchPackets > 0)
        {
            // sCurrentTouch starts at byte 34 (= report-data offset 33 + 1 report ID byte).
            // 34: bPacketCounter, 35: tracking1, 36-38: data1, 39: tracking2, 40-42: data2.
            DecodeTouchPacket("F0", r, 35);
            DecodeTouchPacket("F1", r, 39);
        }
    }

    // DualSense USB Report 0x01 (PS5StatePacket_t shape).
    static void PrintDualSense(byte[] r, int len)
    {
        // 55, not 54: the connect byte below is r[54], so a 54-byte report
        // would read one past its own length. buf is 128 bytes, so that read
        // never threw. It printed a stale byte from the PREVIOUS report as
        // this one's connection state, which is worse than a crash.
        if (len < 55) { Console.WriteLine($"short report ({len} bytes)"); return; }
        int LX = r[1], LY = r[2], RX = r[3], RY = r[4];
        int LT = r[5], RT = r[6];
        int counter = r[7];
        int b8 = r[8], b9 = r[9], b10 = r[10];
        int dpad = b8 & 0x0F;
        // Struct offsets shift by +1 in raw report (report ID at byte 0).
        int gx = (short)(r[16] | (r[17] << 8));
        int gy = (short)(r[18] | (r[19] << 8));
        int gz = (short)(r[20] | (r[21] << 8));
        int ax = (short)(r[22] | (r[23] << 8));
        int ay = (short)(r[24] | (r[25] << 8));
        int az = (short)(r[26] | (r[27] << 8));
        int batByte = r[53];
        int batLevel = batByte & 0x0F;
        int batStatus = (batByte >> 4) & 0x0F;
        int connect = r[54];
        Console.WriteLine($"sticks  L=({LX,3},{LY,3})  R=({RX,3},{RY,3})  triggers L2={LT,3} R2={RT,3}");
        Console.WriteLine($"counter=0x{counter:X2}  dpad={dpad}  buttons[8]={b8:X2}  buttons[9]={b9:X2}  buttons[10]={b10:X2}");
        Console.WriteLine($"gyro  P={gx,7} Y={gy,7} R={gz,7}  (raw int16)");
        Console.WriteLine($"accel X={ax,7} Y={ay,7} Z={az,7}  (raw int16)");
        Console.WriteLine($"battery level={batLevel}/10  status={batStatus} ({BatStatus(batStatus)})  connect=0x{connect:X2}");
        // Touchpad: counter1 at struct[32]=byte 33, data1 at 34-36, counter2 at 37, data2 at 38-40.
        DecodeTouchPacket("F0", r, 33);
        DecodeTouchPacket("F1", r, 37);
    }

    static string BatStatus(int s) => s switch
    {
        0 => "discharging",
        1 => "charging",
        2 => "full",
        _ => "?"
    };

    // Touch packet: counter byte (bit 7 = NOT down, low 7 = tracking ID)
    // + 3 bytes (12-bit X + 12-bit Y).
    static void DecodeTouchPacket(string label, byte[] r, int off)
    {
        if (off + 3 >= r.Length) return;
        byte counter = r[off];
        bool down = (counter & 0x80) == 0;
        int trackingId = counter & 0x7F;
        int x = r[off + 1] | ((r[off + 2] & 0x0F) << 8);
        int y = (r[off + 2] >> 4) | (r[off + 3] << 4);
        Console.WriteLine($"  {label}  down={down,5}  id={trackingId,3}  x={x,4}  y={y,4}");
    }

    static List<HidDevice> EnumerateSonyDevices()
    {
        var results = new List<HidDevice>();
        Native.HidD_GetHidGuid(out Guid hidGuid);

        IntPtr devInfo = Native.SetupDiGetClassDevs(ref hidGuid, null,
            IntPtr.Zero, 0x12 /* DIGCF_PRESENT|DIGCF_DEVICEINTERFACE */);
        if (devInfo == new IntPtr(-1)) return results;

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA();
            ifData.cbSize = Marshal.SizeOf(ifData);
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                int needed = 0;
                Native.SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifData, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
                if (needed == 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifData, detail, needed, ref needed, IntPtr.Zero))
                        continue;
                    string path = Marshal.PtrToStringUni(detail + 4)!;
                    using var h = Native.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                    if (h.IsInvalid) continue;

                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!Native.HidD_GetAttributes(h, ref attr)) continue;
                    if (attr.VendorID != SONY_VID || !SonyShapes.TryGetValue(attr.ProductID, out string? shape))
                        continue;

                    results.Add(new HidDevice(path, attr.VendorID, attr.ProductID, shape));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(devInfo); }
        return results;
    }
}

internal sealed record HidDevice(string Path, ushort Vid, ushort Pid, string Shape);

[StructLayout(LayoutKind.Sequential)]
internal struct SP_DEVICE_INTERFACE_DATA
{
    public int cbSize;
    public Guid InterfaceClassGuid;
    public int Flags;
    public IntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HIDD_ATTRIBUTES
{
    public int Size;
    public ushort VendorID;
    public ushort ProductID;
    public ushort VersionNumber;
}

internal static partial class Native
{
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CreateFileW")]
    public static partial SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sec, uint create, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ReadFile(SafeFileHandle h, [Out] byte[] buf, int len, out int read, IntPtr ovl);

    [LibraryImport("hid.dll")]
    public static partial void HidD_GetHidGuid(out Guid guid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SetupDiGetClassDevsW")]
    public static partial IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid intfClass, uint memberIdx, ref SP_DEVICE_INTERFACE_DATA ifData);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetupDiGetDeviceInterfaceDetailW(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, int detailSize, ref int reqSize, IntPtr devInfoData);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);
}
