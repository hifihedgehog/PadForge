using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Engine.Tablets;

internal static class TabletNative
{
    internal const uint Success = 0x00110000;
    internal const uint IncompatibleReport = 0xC011000A;
    internal static readonly Guid HidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    internal struct Attributes
    {
        internal int Size;
        internal ushort Vendor, Product, Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Caps
    {
        internal ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
        internal ushort LinkNodes, InputButtons, InputValues, InputData;
        internal ushort OutputButtons, OutputValues, OutputData;
        internal ushort FeatureButtons, FeatureValues, FeatureData;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct ValueCap
    {
        [FieldOffset(0)] internal ushort Page;
        [FieldOffset(2)] internal byte ReportId;
        [FieldOffset(3)] internal byte IsAlias;
        [FieldOffset(6)] internal ushort Link;
        [FieldOffset(8)] internal ushort LinkUsage;
        [FieldOffset(10)] internal ushort LinkPage;
        [FieldOffset(12)] internal byte IsRange;
        [FieldOffset(15)] internal byte IsAbsolute;
        [FieldOffset(16)] internal byte HasNull;
        [FieldOffset(18)] internal ushort BitSize;
        [FieldOffset(20)] internal ushort ReportCount;
        [FieldOffset(32)] internal uint UnitsExponent;
        [FieldOffset(36)] internal uint Units;
        [FieldOffset(40)] internal int LogicalMin;
        [FieldOffset(44)] internal int LogicalMax;
        [FieldOffset(56)] internal ushort UsageMin;
        [FieldOffset(58)] internal ushort UsageMax;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct ButtonCap
    {
        [FieldOffset(0)] internal ushort Page;
        [FieldOffset(2)] internal byte ReportId;
        [FieldOffset(3)] internal byte IsAlias;
        [FieldOffset(6)] internal ushort Link;
        [FieldOffset(12)] internal byte IsRange;
        [FieldOffset(56)] internal ushort UsageMin;
        [FieldOffset(58)] internal ushort UsageMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint Flags;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal UIntPtr Reserved;
    }

    internal static IEnumerable<(string Path, string Instance)> EnumerateInterfaces()
    {
        Guid hid = HidInterface;
        IntPtr set = SetupDiGetClassDevs(ref hid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var item = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, index, ref item))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                if ((item.Flags & 1) == 0) continue;
                var device = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
                SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out uint required, ref device);
                if (required < 6 || required > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref item, detail, required, out _, ref device)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4);
                    var instance = new StringBuilder(1024);
                    if (!SetupDiGetDeviceInstanceId(set, ref device, instance, instance.Capacity, out _)) continue;
                    if (!string.IsNullOrEmpty(path)) yield return (path, instance.ToString());
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    internal static SafeFileHandle OpenMetadata(string path)
        => CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

    internal static string ReadString(SafeFileHandle handle, bool serial)
    {
        byte[] bytes = new byte[512];
        bool read = serial ? HidD_GetSerialNumberString(handle, bytes, bytes.Length)
                           : HidD_GetProductString(handle, bytes, bytes.Length);
        if (!read) return "";
        string text = Encoding.Unicode.GetString(bytes);
        int end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    internal static void RestartCollection(string instance, CancellationToken stop)
    {
        if (string.IsNullOrWhiteSpace(instance) || !instance.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a HID collection can be restarted.");
        stop.ThrowIfCancellationRequested();
        uint result = CM_Locate_DevNode(out uint node, instance, 0);
        if (result != 0) throw new InvalidOperationException($"Locate HID collection failed (CR 0x{result:X}).");
        result = CM_Disable_DevNode(node, 0);
        if (result != 0) throw new InvalidOperationException($"Restart HID collection failed (CR 0x{result:X}).");
        // Re-enable even if the request was canceled after the disable completed.
        result = CM_Enable_DevNode(node, 0);
        if (result != 0) throw new InvalidOperationException($"Enable HID collection failed (CR 0x{result:X}).");
        stop.ThrowIfCancellationRequested();
    }

    internal static bool IsStarted(string instance)
        => CM_Locate_DevNode(out uint node, instance, 0) == 0
        && CM_Get_DevNode_Status(out uint status, out _, node, 0) == 0
        && (status & 8) != 0;

    internal static bool IsPresent(string instance)
        => CM_Locate_DevNode(out _, instance, 0) == 0;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] value, int length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] value, int length);
    [DllImport("hid.dll")] internal static extern uint HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] internal static extern uint HidP_GetValueCaps(int type, [Out] ValueCap[] values, ref ushort count, IntPtr data);
    [DllImport("hid.dll")] internal static extern uint HidP_GetButtonCaps(int type, [Out] ButtonCap[] values, ref ushort count, IntPtr data);
    [DllImport("hid.dll")] internal static extern uint HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr data, IntPtr report, uint length);
    [DllImport("hid.dll")] internal static extern uint HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] usages, ref uint count, IntPtr data, IntPtr report, uint length);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint length, out uint required, ref DeviceInfoData device);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfoData device, StringBuilder value, int length, out int required);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNode(out uint node, string instance, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Disable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Enable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
}
